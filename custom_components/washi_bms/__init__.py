"""The Washi BMS integration."""

from __future__ import annotations

import logging

from homeassistant.components import bluetooth
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import Platform
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import ConfigEntryNotReady
from homeassistant.helpers import device_registry as dr

from .const import DOMAIN, MANUFACTURER
from .coordinator import WashiBmsCoordinator

_LOGGER = logging.getLogger(__name__)

PLATFORMS: list[Platform] = [Platform.SENSOR]

WashiBmsConfigEntry = ConfigEntry[WashiBmsCoordinator]

# Keys earlier versions of this integration used to store the MAC.
_ADDRESS_KEYS = ("address", "mac", "bluetooth_address", "device_address")


async def async_setup_entry(hass: HomeAssistant, entry: WashiBmsConfigEntry) -> bool:
    """Set up Washi BMS from a config entry."""
    address = _resolve_address(entry)
    if address is None:
        raise ConfigEntryNotReady(
            "no Bluetooth address stored in this config entry; remove and re-add the device"
        )

    coordinator = WashiBmsCoordinator(hass, entry, address)
    entry.runtime_data = coordinator

    # Registered before the first poll, so a pack that is out of range right
    # now is picked up the moment it advertises again rather than at the next
    # scheduled poll.
    entry.async_on_unload(
        bluetooth.async_register_callback(
            hass,
            coordinator.async_on_advertisement,
            {"address": address, "connectable": True},
            bluetooth.BluetoothScanningMode.PASSIVE,
        )
    )

    # A pack that is asleep, out of range, or behind a Bluetooth proxy that has
    # not reconnected yet must not take the whole entry down with it. Failing
    # setup here removes every sensor from Home Assistant, which is strictly
    # worse than an unavailable one: dashboards fall back to "entity not
    # found", and any automation watching for the pack going unavailable — the
    # one that restarts the BLE proxy to get it back — can no longer fire,
    # because its trigger entity no longer exists. So set up regardless and let
    # the entities read unavailable until the first successful poll.
    await coordinator.async_refresh()
    if not coordinator.last_update_success:
        _LOGGER.warning(
            "[%s] first poll failed (%s); the sensors stay unavailable until the "
            "pack is heard again",
            address,
            coordinator.last_exception,
        )

    _track_device_details(hass, entry, coordinator)

    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    entry.async_on_unload(entry.add_update_listener(_async_reload_entry))
    return True


async def async_unload_entry(hass: HomeAssistant, entry: WashiBmsConfigEntry) -> bool:
    """Unload a config entry and drop the BLE link."""
    unloaded = await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
    if unloaded:
        await entry.runtime_data.async_shutdown()
    return unloaded


async def _async_reload_entry(hass: HomeAssistant, entry: WashiBmsConfigEntry) -> None:
    await hass.config_entries.async_reload(entry.entry_id)


def _resolve_address(entry: ConfigEntry) -> str | None:
    """Find the pack's MAC, tolerating how older versions stored it.

    The entry on an existing install predates this rewrite, so the key it used
    is not guaranteed. Fall back to the entry's unique id, which for a
    Bluetooth-discovered entry is the address itself.
    """
    for key in _ADDRESS_KEYS:
        value = entry.data.get(key)
        if isinstance(value, str) and value:
            return value.upper()
    if entry.unique_id and ":" in entry.unique_id:
        return entry.unique_id.upper()
    return None


def _track_device_details(
    hass: HomeAssistant, entry: WashiBmsConfigEntry, coordinator: WashiBmsCoordinator
) -> None:
    """Register the device now and keep it in step with what the pack reports.

    The model string and the pack size are only known once the pack has
    actually answered, which — on a pack that starts out of range — can be a
    long time after setup. So the device is registered immediately with
    whatever is known, and refreshed when a poll fills the rest in.
    """
    written: dict[str, str] = {}

    @callback
    def _sync() -> None:
        details = _device_details(coordinator)
        if details == written:
            return
        written.clear()
        written.update(details)
        # Any field left out here keeps whatever the registry already holds,
        # so a name the user set by hand survives.
        dr.async_get(hass).async_get_or_create(
            config_entry_id=entry.entry_id,
            identifiers={(DOMAIN, coordinator.address)},
            connections={(dr.CONNECTION_BLUETOOTH, coordinator.address)},
            manufacturer=MANUFACTURER,
            name=entry.title,
            **details,
        )

    _sync()
    entry.async_on_unload(coordinator.async_add_listener(_sync))


def _device_details(coordinator: WashiBmsCoordinator) -> dict[str, str]:
    """The device-page fields that can only be filled in from a real reading."""
    details: dict[str, str] = {}
    if coordinator.model:
        details["model"] = coordinator.model
    nominal = (coordinator.data or {}).get("nominal_capacity")
    if nominal:
        details["hw_version"] = f"12.8V / {nominal:g}Ah / {nominal * 12.8:g}Wh"
    return details
