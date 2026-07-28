"""JBD/Xiaoxiang-style protocol used by the Washi MINIBTS pack.

Everything in here is pure: bytes in, plain dicts out. That keeps the frame
handling unit-testable without a Bluetooth stack, and keeps the coordinator
free of bit twiddling.

Frame format
------------
Request   ``DD A5 <cmd> 00 <chk:2> 77``
Response  ``DD <cmd> <status> <len> <payload:len> <chk:2> 77``

``status`` is 0x00 on success, 0x80 on error. The checksum is
``0x10000 - sum(status, len, *payload)`` for responses and
``0x10000 - sum(cmd, len, *payload)`` for requests, big endian.
"""

from __future__ import annotations

import logging
from typing import Any, Final, NamedTuple

_LOGGER = logging.getLogger(__name__)

START_BYTE: Final = 0xDD
END_BYTE: Final = 0x77
HEADER_LEN: Final = 4
TRAILER_LEN: Final = 3

CMD_BASIC: Final = 0x03
CMD_CELLS: Final = 0x04
CMD_DEVICE_NAME: Final = 0x05

# Offsets inside the basic-info payload that this pack is known to honour.
# Everything up to and including the cycle counter has been verified against
# the vendor app and against the SmartShunt, so these are treated as fixed.
_OFF_TOTAL_VOLTAGE: Final = 0
_OFF_CURRENT: Final = 2
_OFF_REMAINING: Final = 4
_OFF_NOMINAL: Final = 6
_OFF_CYCLES: Final = 8
_OFF_PROTECTION: Final = 16

# The tail of the frame (RSOC / FET / cell count / NTC block) is *not* fixed:
# this pack does not follow the textbook JBD layout there. It is located at
# runtime by :func:`locate_tail`.
_TAIL_SEARCH_START: Final = 16
_TAIL_SEARCH_END: Final = 40

# Protection bits, as reported at offset 16..17 of the basic frame.
PROTECTION_FLAGS: Final[tuple[str, ...]] = (
    "cell_overvoltage",
    "cell_undervoltage",
    "pack_overvoltage",
    "pack_undervoltage",
    "charge_overtemperature",
    "charge_undertemperature",
    "discharge_overtemperature",
    "discharge_undertemperature",
    "charge_overcurrent",
    "discharge_overcurrent",
    "short_circuit",
    "frontend_ic_error",
    "software_lock",
)


class Frame(NamedTuple):
    """A decoded, checksum-verified response frame."""

    command: int
    payload: bytes


class Tail(NamedTuple):
    """Where the variable part of the basic-info payload actually starts.

    ``cell_count_index`` is the offset of the cell-count byte; RSOC and the FET
    status byte sit immediately before it, the NTC block immediately after.
    """

    cell_count_index: int
    ntc_count: int


def build_request(command: int) -> bytes:
    """Build a read request for ``command``."""
    checksum = (0x10000 - (command + 0x00)) & 0xFFFF
    return bytes(
        [START_BYTE, 0xA5, command, 0x00, checksum >> 8, checksum & 0xFF, END_BYTE]
    )


def _u16(data: bytes, offset: int) -> int:
    return int.from_bytes(data[offset : offset + 2], "big", signed=False)


def _i16(data: bytes, offset: int) -> int:
    return int.from_bytes(data[offset : offset + 2], "big", signed=True)


def extract_frame(buffer: bytearray) -> Frame | None:
    """Pull one complete frame off the front of ``buffer``.

    Consumes the bytes it uses, including any leading garbage. Returns ``None``
    when the buffer does not yet hold a full frame; the caller keeps appending
    notification chunks until it does.

    Raises:
        ValueError: the frame is complete but malformed (bad terminator,
            checksum mismatch, or an error status from the BMS).
    """
    # Resynchronise on the start byte — a reconnect can leave a partial frame.
    start = buffer.find(START_BYTE)
    if start == -1:
        buffer.clear()
        return None
    if start:
        del buffer[:start]

    if len(buffer) < HEADER_LEN:
        return None

    command = buffer[1]
    status = buffer[2]
    length = buffer[3]
    total = HEADER_LEN + length + TRAILER_LEN
    if len(buffer) < total:
        return None

    frame = bytes(buffer[:total])
    del buffer[:total]

    if frame[-1] != END_BYTE:
        raise ValueError(f"bad frame terminator 0x{frame[-1]:02x}")
    if status != 0x00:
        raise ValueError(f"BMS reported error status 0x{status:02x} for cmd 0x{command:02x}")

    payload = frame[HEADER_LEN : HEADER_LEN + length]
    expected = _u16(frame, HEADER_LEN + length)
    actual = (0x10000 - (status + length + sum(payload))) & 0xFFFF
    if expected != actual:
        raise ValueError(f"checksum mismatch: got 0x{expected:04x}, want 0x{actual:04x}")

    return Frame(command=command, payload=payload)


def locate_tail(payload: bytes, cell_count: int) -> Tail | None:
    """Find the cell-count byte in the basic-info payload.

    The textbook JBD layout puts RSOC/FET/cell-count/NTC-count at offsets
    19..22, but this pack does not: reading those offsets yields a cell count of
    57 for a 4S pack and a charge-FET state that contradicts the measured
    current. Rather than hard-code a guess, anchor on the one byte whose true
    value is known independently — the cell count, taken from the cell-voltage
    frame — and require its neighbours to be self-consistent:

    * RSOC (two bytes before) must be a percentage,
    * the FET byte (one byte before) must use only its two low bits,
    * the NTC count must be plausible and its readings must be real
      temperatures.

    Returns ``None`` when no offset satisfies all of that, in which case the
    caller must not publish those fields — a wrong value is worse than none.
    """
    if cell_count <= 0:
        return None

    candidates: list[tuple[tuple[bool, bool], Tail]] = []
    end = min(len(payload), _TAIL_SEARCH_END)
    for index in range(max(_TAIL_SEARCH_START, 2), end):
        if payload[index] != cell_count:
            continue
        if payload[index - 2] > 100:  # RSOC
            continue
        if payload[index - 1] > 0x03:  # FET status uses bits 0 and 1 only
            continue
        if index + 1 >= len(payload):
            continue

        ntc_count = payload[index + 1]
        if ntc_count > 8:
            continue
        block_end = index + 2 + 2 * ntc_count
        if block_end > len(payload):
            continue
        if any(
            not _is_plausible_temperature(_u16(payload, index + 2 + 2 * probe))
            for probe in range(ntc_count)
        ):
            continue

        # A candidate whose NTC block runs exactly to the end of the payload,
        # and which reports at least one probe, is far more likely to be the
        # real thing than a run of zeros that happens to satisfy the bounds.
        rank = (block_end == len(payload), ntc_count > 0)
        candidates.append((rank, Tail(cell_count_index=index, ntc_count=ntc_count)))

    if not candidates:
        return None
    return max(candidates, key=lambda item: item[0])[1]


def _is_plausible_temperature(raw: int) -> bool:
    """True when ``raw`` decodes to a temperature a battery could actually be."""
    return -40.0 <= (raw - 2731) / 10.0 <= 100.0


def parse_cells(payload: bytes) -> list[float]:
    """Decode the cell-voltage frame (command 0x04) into volts."""
    return [_u16(payload, offset) / 1000.0 for offset in range(0, len(payload) & ~1, 2)]


def parse_basic(payload: bytes, cell_count: int) -> dict[str, Any]:
    """Decode the basic-info frame (command 0x03).

    ``cell_count`` comes from the cell-voltage frame and is used to locate the
    variable tail. Fields that cannot be located are simply absent from the
    result rather than guessed.
    """
    if len(payload) < 10:
        raise ValueError(f"basic frame too short: {len(payload)} bytes")

    voltage = _u16(payload, _OFF_TOTAL_VOLTAGE) / 100.0
    current = _i16(payload, _OFF_CURRENT) / 100.0

    data: dict[str, Any] = {
        "pack_voltage": voltage,
        "current": current,
        "power": round(voltage * current, 1),
        "remaining_capacity": _u16(payload, _OFF_REMAINING) / 100.0,
        "nominal_capacity": _u16(payload, _OFF_NOMINAL) / 100.0,
        "cycle_count": _u16(payload, _OFF_CYCLES),
        "tail_located": False,
    }

    if len(payload) >= _OFF_PROTECTION + 2:
        data["problem_flags"] = _decode_protection(_u16(payload, _OFF_PROTECTION))

    tail = locate_tail(payload, cell_count)
    if tail is None:
        return data

    index = tail.cell_count_index
    fet = payload[index - 1]
    data.update(
        {
            "tail_located": True,
            "tail_index": index,
            "soc": payload[index - 2],
            "charge_fet": bool(fet & 0x01),
            "discharge_fet": bool(fet & 0x02),
            "cell_count": payload[index],
            "temperatures": [
                round((_u16(payload, index + 2 + 2 * probe) - 2731) / 10.0, 1)
                for probe in range(tail.ntc_count)
            ],
        }
    )
    return data


def _decode_protection(word: int) -> list[str]:
    """Expand the protection bitfield into the names of the active faults."""
    return [name for bit, name in enumerate(PROTECTION_FLAGS) if word & (1 << bit)]
