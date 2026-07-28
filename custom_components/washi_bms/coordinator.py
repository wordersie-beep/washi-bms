"""BLE transport and polling for the Washi BMS."""

from __future__ import annotations

import asyncio
import logging
from datetime import timedelta
from typing import Any

from bleak.backends.characteristic import BleakGATTCharacteristic
from bleak.exc import BleakError
from bleak_retry_connector import (
    BleakClientWithServiceCache,
    BleakNotFoundError,
    establish_connection,
)
from homeassistant.components import bluetooth
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from . import jbd
from .const import (
    CHARACTERISTIC_CANDIDATES,
    COMMAND_TIMEOUT,
    DOMAIN,
    SCAN_INTERVAL_SECONDS,
)

_LOGGER = logging.getLogger(__name__)


class WashiBmsCoordinator(DataUpdateCoordinator[dict[str, Any]]):
    """Keeps one BLE link to the pack open and turns frames into state."""

    def __init__(self, hass: HomeAssistant, entry: ConfigEntry, address: str) -> None:
        super().__init__(
            hass,
            _LOGGER,
            name=f"{DOMAIN} {address}",
            update_interval=timedelta(seconds=SCAN_INTERVAL_SECONDS),
            config_entry=entry,
        )
        self.address = address
        # Model string as the pack reports it, e.g. "12V314AH LiFePO4 (MINIBTS)".
        # Read once on the first successful connection; None until then.
        self.model: str | None = None
        self._model_attempts = 0
        self._client: BleakClientWithServiceCache | None = None
        self._write_char: BleakGATTCharacteristic | None = None
        self._notify_char: BleakGATTCharacteristic | None = None
        self._buffer = bytearray()
        self._pending: asyncio.Future[jbd.Frame] | None = None
        self._expecting: int | None = None
        self._lock = asyncio.Lock()
        self._warned_about_tail = False
        # Kept purely so `diagnostics.py` can hand over real bytes when the
        # decoding of a new firmware revision needs to be worked out.
        self.last_frames: dict[str, str] = {}

    # ------------------------------------------------------------------
    # Connection handling
    # ------------------------------------------------------------------

    async def _async_connect(self) -> BleakClientWithServiceCache:
        """Return a live client, connecting and resolving characteristics first."""
        if self._client is not None and self._client.is_connected:
            return self._client

        ble_device = bluetooth.async_ble_device_from_address(
            self.hass, self.address, connectable=True
        )
        if ble_device is None:
            raise UpdateFailed(
                f"{self.address} is not in range of any Bluetooth adapter or proxy"
            )

        _LOGGER.debug("[%s] connecting", self.address)
        client = await establish_connection(
            BleakClientWithServiceCache,
            ble_device,
            self.address,
            self._on_disconnect,
            max_attempts=3,
        )

        try:
            self._write_char, self._notify_char = _resolve_characteristics(client)
            await client.start_notify(self._notify_char, self._on_notification)
        except Exception:
            # Never leave a half-open link behind: it would hold an adapter
            # slot and every later attempt would fail with "no slot available".
            await _safe_disconnect(client)
            raise

        self._client = client
        self._buffer.clear()
        _LOGGER.debug(
            "[%s] connected; write=%s notify=%s",
            self.address,
            self._write_char.uuid,
            self._notify_char.uuid,
        )
        return client

    def _on_disconnect(self, _client: BleakClientWithServiceCache) -> None:
        """Drop our references so the next poll reconnects from scratch."""
        _LOGGER.debug("[%s] disconnected", self.address)
        self._client = None
        self._write_char = None
        self._notify_char = None
        if self._pending is not None and not self._pending.done():
            self._pending.set_exception(BleakError("disconnected while awaiting reply"))

    async def _async_teardown(self) -> None:
        client, self._client = self._client, None
        self._write_char = None
        self._notify_char = None
        self._buffer.clear()
        if client is not None:
            await _safe_disconnect(client)

    async def async_shutdown(self) -> None:
        await super().async_shutdown()
        await self._async_teardown()

    # ------------------------------------------------------------------
    # Frame exchange
    # ------------------------------------------------------------------

    def _on_notification(self, _char: BleakGATTCharacteristic, data: bytearray) -> None:
        """Reassemble notification chunks into whole frames.

        The pack splits longer replies across several notifications, so frames
        are accumulated until one is complete rather than parsed per chunk.
        """
        self._buffer += data
        while True:
            try:
                frame = jbd.extract_frame(self._buffer)
            except ValueError as err:
                self._buffer.clear()
                if self._pending is not None and not self._pending.done():
                    self._pending.set_exception(err)
                return
            if frame is None:
                return
            # A late reply to the previous command must not satisfy this one:
            # the two are polled back to back, so a stale frame would otherwise
            # be decoded with the wrong parser.
            if frame.command != self._expecting:
                _LOGGER.debug(
                    "[%s] discarding stale frame for cmd 0x%02x",
                    self.address,
                    frame.command,
                )
                continue
            if self._pending is not None and not self._pending.done():
                self._pending.set_result(frame)

    async def _query(self, command: int) -> bytes:
        """Send one command and wait for its reply."""
        client = await self._async_connect()
        assert self._write_char is not None

        loop = asyncio.get_running_loop()
        self._pending = loop.create_future()
        self._expecting = command
        self._buffer.clear()
        try:
            await client.write_gatt_char(
                self._write_char, jbd.build_request(command), response=False
            )
            frame = await asyncio.wait_for(self._pending, COMMAND_TIMEOUT)
        except TimeoutError as err:
            raise UpdateFailed(
                f"no reply to command 0x{command:02x} within {COMMAND_TIMEOUT:g} s"
            ) from err
        finally:
            self._pending = None
            self._expecting = None

        _LOGGER.debug(
            "[%s] cmd 0x%02x -> %s", self.address, command, frame.payload.hex(" ")
        )
        self.last_frames[f"cmd_{command:02x}"] = frame.payload.hex(" ")
        return frame.payload

    # ------------------------------------------------------------------
    # Polling
    # ------------------------------------------------------------------

    async def _async_read_model(self) -> None:
        """Read the pack's model string once, best effort.

        Purely cosmetic — it fills in the device page — so a failure must never
        cost more than a few early attempts, and never fails a poll.
        """
        if self.model is not None or self._model_attempts >= 3:
            return
        self._model_attempts += 1
        try:
            raw = await self._query(jbd.CMD_DEVICE_NAME)
        except (UpdateFailed, BleakError, ValueError, OSError) as err:
            _LOGGER.debug("[%s] model string unavailable: %s", self.address, err)
            return
        self.model = raw.decode("ascii", "ignore").strip("\x00 \t\r\n") or None

    async def _async_update_data(self) -> dict[str, Any]:
        """Fetch one full snapshot: cell voltages first, then the basic frame.

        Cells come first because their count is what anchors the variable tail
        of the basic frame.
        """
        async with self._lock:
            try:
                cells = jbd.parse_cells(await self._query(jbd.CMD_CELLS))
                basic = jbd.parse_basic(await self._query(jbd.CMD_BASIC), len(cells))
            except UpdateFailed:
                await self._async_teardown()
                raise
            except (BleakNotFoundError, BleakError, ValueError, OSError) as err:
                await self._async_teardown()
                raise UpdateFailed(f"{type(err).__name__}: {err}") from err

            await self._async_read_model()

        data: dict[str, Any] = dict(basic)
        data["cell_voltages"] = cells
        data["cell_count"] = len(cells)
        if cells:
            data["cell_min_voltage"] = min(cells)
            data["cell_max_voltage"] = max(cells)
            data["cell_delta_voltage"] = round(max(cells) - min(cells), 3)

        if not data.get("tail_located") and not self._warned_about_tail:
            self._warned_about_tail = True
            _LOGGER.warning(
                "[%s] could not locate the state-of-charge/FET/temperature block "
                "in the basic frame, so those sensors stay unknown rather than "
                "report a wrong value. Raw frame for analysis: %s",
                self.address,
                self.last_frames.get(f"cmd_{jbd.CMD_BASIC:02x}", "n/a"),
            )
        elif data.get("tail_located") and self._warned_about_tail:
            self._warned_about_tail = False

        return data


def _resolve_characteristics(
    client: BleakClientWithServiceCache,
) -> tuple[BleakGATTCharacteristic, BleakGATTCharacteristic]:
    """Pick the write/notify pair to talk to.

    Tries the UUID pairs seen on the known Washi/JBD packs, then falls back to
    discovery so a firmware revision that moves the vendor service still works.
    """
    for write_uuid, notify_uuid in CHARACTERISTIC_CANDIDATES:
        write = client.services.get_characteristic(write_uuid)
        notify = client.services.get_characteristic(notify_uuid)
        if write is not None and notify is not None and _can_notify(notify):
            return write, notify

    for service in client.services:
        writable = [char for char in service.characteristics if _can_write(char)]
        notifiable = [char for char in service.characteristics if _can_notify(char)]
        if writable and notifiable:
            _LOGGER.debug(
                "falling back to discovered pair %s / %s in service %s",
                writable[0].uuid,
                notifiable[0].uuid,
                service.uuid,
            )
            return writable[0], notifiable[0]

    raise UpdateFailed("no usable write/notify characteristic pair on this device")


def _can_write(char: BleakGATTCharacteristic) -> bool:
    return bool({"write", "write-without-response"} & set(char.properties))


def _can_notify(char: BleakGATTCharacteristic) -> bool:
    return bool({"notify", "indicate"} & set(char.properties))


async def _safe_disconnect(client: BleakClientWithServiceCache) -> None:
    """Disconnect without letting a teardown failure mask the original error."""
    try:
        await client.disconnect()
    except (BleakError, OSError, TimeoutError) as err:
        _LOGGER.debug("ignoring error while disconnecting: %s", err)
