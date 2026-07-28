"""Protocol tests. Run with: python3 -m pytest tests/ -q"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "custom_components" / "washi_bms"))

import jbd  # noqa: E402


def build_response(command: int, payload: bytes, status: int = 0x00) -> bytes:
    """Wrap a payload the way the BMS does, so tests exercise the real decoder."""
    checksum = (0x10000 - (status + len(payload) + sum(payload))) & 0xFFFF
    return (
        bytes([jbd.START_BYTE, command, status, len(payload)])
        + payload
        + bytes([checksum >> 8, checksum & 0xFF, jbd.END_BYTE])
    )


def basic_payload(
    *,
    voltage: int = 1334,
    current: int = -177,
    remaining: int = 18860,
    nominal: int = 31400,
    cycles: int = 2,
    protection: int = 0,
    rsoc: int = 60,
    fet: int = 0x03,
    cells: int = 4,
    temps: tuple[int, ...] = (2981, 2985),
) -> bytes:
    """A textbook JBD basic frame, matching the real pack's leading fields."""
    out = bytearray()
    out += voltage.to_bytes(2, "big")
    out += current.to_bytes(2, "big", signed=True)
    out += remaining.to_bytes(2, "big")
    out += nominal.to_bytes(2, "big")
    out += cycles.to_bytes(2, "big")
    out += (0x2E1F).to_bytes(2, "big")  # production date
    out += (0).to_bytes(2, "big")  # balance low
    out += (0).to_bytes(2, "big")  # balance high
    out += protection.to_bytes(2, "big")
    out += bytes([0x20, rsoc, fet, cells, len(temps)])
    for temp in temps:
        out += temp.to_bytes(2, "big")
    return bytes(out)


# --- framing -----------------------------------------------------------------


def test_request_matches_documented_bytes():
    assert jbd.build_request(jbd.CMD_BASIC) == bytes.fromhex("dda50300fffd77")
    assert jbd.build_request(jbd.CMD_CELLS) == bytes.fromhex("dda50400fffc77")


def test_extract_frame_round_trip():
    payload = basic_payload()
    buffer = bytearray(build_response(jbd.CMD_BASIC, payload))
    frame = jbd.extract_frame(buffer)
    assert frame is not None
    assert frame.command == jbd.CMD_BASIC
    assert frame.payload == payload
    assert not buffer


def test_extract_frame_waits_for_the_rest_of_a_split_notification():
    raw = build_response(jbd.CMD_BASIC, basic_payload())
    buffer = bytearray(raw[:9])
    assert jbd.extract_frame(buffer) is None
    buffer += raw[9:]
    assert jbd.extract_frame(buffer) is not None


def test_extract_frame_resynchronises_after_leading_garbage():
    buffer = bytearray(b"\x01\x02" + build_response(jbd.CMD_CELLS, bytes.fromhex("0d0c0d0c")))
    frame = jbd.extract_frame(buffer)
    assert frame is not None and frame.command == jbd.CMD_CELLS


def test_extract_frame_rejects_a_corrupted_checksum():
    raw = bytearray(build_response(jbd.CMD_BASIC, basic_payload()))
    raw[-2] ^= 0xFF
    with pytest.raises(ValueError, match="checksum"):
        jbd.extract_frame(raw)


def test_extract_frame_reports_an_error_status():
    with pytest.raises(ValueError, match="error status"):
        jbd.extract_frame(bytearray(build_response(jbd.CMD_BASIC, b"", status=0x80)))


# --- decoding ----------------------------------------------------------------


def test_parse_cells_returns_volts():
    payload = b"".join(v.to_bytes(2, "big") for v in (3343, 3344, 3343, 3345))
    assert jbd.parse_cells(payload) == [3.343, 3.344, 3.343, 3.345]


def test_parse_basic_decodes_the_verified_leading_fields():
    data = jbd.parse_basic(basic_payload(), cell_count=4)
    assert data["pack_voltage"] == 13.34
    assert data["current"] == -1.77
    assert data["remaining_capacity"] == 188.6
    assert data["nominal_capacity"] == 314.0
    assert data["cycle_count"] == 2
    assert data["power"] == pytest.approx(-23.6, abs=0.05)


def test_tail_is_located_in_a_textbook_frame():
    data = jbd.parse_basic(basic_payload(rsoc=60, fet=0x03), cell_count=4)
    assert data["tail_located"] is True
    assert data["tail_index"] == 21
    assert data["soc"] == 60
    assert data["charge_fet"] is True
    assert data["discharge_fet"] is True
    assert data["temperatures"] == [25.0, 25.4]


def test_tail_is_located_when_the_frame_carries_extra_bytes():
    """A firmware that pads the middle must not shift the readings."""
    payload = basic_payload()
    shifted = payload[:18] + b"\x00\x00" + payload[18:]
    data = jbd.parse_basic(shifted, cell_count=4)
    assert data["tail_located"] is True
    assert data["soc"] == 60
    assert data["temperatures"] == [25.0, 25.4]


def test_fet_and_soc_are_withheld_when_the_layout_cannot_be_verified():
    """The bug this integration was rewritten for: never publish a guess."""
    payload = bytearray(basic_payload())
    payload[21] = 57  # cell-count byte that disagrees with the cell frame
    payload[22] = 0
    data = jbd.parse_basic(bytes(payload), cell_count=4)
    assert data["tail_located"] is False
    assert "charge_fet" not in data
    assert "soc" not in data
    assert "temperatures" not in data
    # The trustworthy half of the frame still comes through.
    assert data["pack_voltage"] == 13.34
    assert data["remaining_capacity"] == 188.6


def test_implausible_temperatures_are_rejected():
    data = jbd.parse_basic(basic_payload(temps=(60000, 60000)), cell_count=4)
    assert data["tail_located"] is False


def test_a_pack_without_probes_still_locates_its_tail():
    data = jbd.parse_basic(basic_payload(temps=()), cell_count=4)
    assert data["tail_located"] is True
    assert data["temperatures"] == []
    assert data["soc"] == 60


def test_protection_flags_are_named():
    data = jbd.parse_basic(basic_payload(protection=0b1_0000_0001), cell_count=4)
    assert data["problem_flags"] == ["cell_overvoltage", "charge_overcurrent"]


def test_no_protection_means_an_empty_list_not_a_missing_key():
    assert jbd.parse_basic(basic_payload(), cell_count=4)["problem_flags"] == []


# --- regression: a real frame captured from the pack ---------------------------

# Basic-info frame read off AA:C2:37:0D:23:E6 (Washi 12V314AH, BMS DH04SC02-200A)
# on 2026-07-28. Kept verbatim: it is the ground truth that showed the previous
# implementation was reading the wrong bytes.
REAL_BASIC_FRAME = bytes.fromhex(
    "0522ffaf436d7aa80002347f000000000000353703040" "40b7e0b7c0b7c0b850080007aa8436d000000001900000000"
)
REAL_CELLS_FRAME = bytes.fromhex("0cd50cd60cd50cd6")


def test_real_frame_decodes_exactly():
    cells = jbd.parse_cells(REAL_CELLS_FRAME)
    assert cells == [3.285, 3.286, 3.285, 3.286]

    data = jbd.parse_basic(REAL_BASIC_FRAME, len(cells))
    assert data["tail_located"] is True
    assert data["tail_index"] == 21
    assert data["pack_voltage"] == 13.14
    assert data["current"] == -0.81
    assert data["remaining_capacity"] == 172.61
    assert data["nominal_capacity"] == 314.0
    assert data["cycle_count"] == 2
    assert data["cell_count"] == 4
    assert data["charge_fet"] is True
    assert data["discharge_fet"] is True
    assert data["temperatures"] == [21.1, 20.9, 20.9, 21.8]
    assert data["problem_flags"] == []


def test_real_frame_soc_agrees_with_the_coulomb_counter():
    """The contradiction that exposed the old bug: 47% reported against 60% counted."""
    data = jbd.parse_basic(REAL_BASIC_FRAME, 4)
    counted = data["remaining_capacity"] / data["nominal_capacity"] * 100
    assert abs(data["soc"] - counted) < 1.0
