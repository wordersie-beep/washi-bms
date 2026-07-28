"""Constants for the Washi BMS integration."""

from __future__ import annotations

from typing import Final

DOMAIN: Final = "washi_bms"

CONF_ADDRESS: Final = "address"

# Polling. The BMS answers a full basic+cells round trip in ~0.35 s, so 5 s
# keeps the power-flow card live without hogging the BLE adapter.
SCAN_INTERVAL_SECONDS: Final = 5

# A single request/response round trip. Generous enough for a busy adapter,
# short enough that a wedged link is noticed within one poll.
COMMAND_TIMEOUT: Final = 4.0

# GATT layout of the Washi 12V314AH (MINIBTS) pack, confirmed by service
# discovery on AA:C2:37:0D:23:E6:
#   service 0000fff0  ->  fff2 [write-without-response], fff1 [read, notify]
# The candidates are tried in order; if none match, the coordinator falls back
# to generic discovery (any writable characteristic paired with a notifying one
# in the same service).
CHARACTERISTIC_CANDIDATES: Final[tuple[tuple[str, str], ...]] = (
    ("0000fff2-0000-1000-8000-00805f9b34fb", "0000fff1-0000-1000-8000-00805f9b34fb"),
    ("0000ff02-0000-1000-8000-00805f9b34fb", "0000ff01-0000-1000-8000-00805f9b34fb"),
    ("0000fa02-0000-1000-8000-00805f9b34fb", "0000fa03-0000-1000-8000-00805f9b34fb"),
)

MANUFACTURER: Final = "Shenzhen Washi Energy Co., Ltd"

# Data keys published by the coordinator.
KEY_PACK_VOLTAGE: Final = "pack_voltage"
KEY_CURRENT: Final = "current"
KEY_POWER: Final = "power"
KEY_SOC: Final = "soc"
KEY_REMAINING_CAPACITY: Final = "remaining_capacity"
KEY_NOMINAL_CAPACITY: Final = "nominal_capacity"
KEY_CYCLE_COUNT: Final = "cycle_count"
KEY_CHARGE_FET: Final = "charge_fet"
KEY_DISCHARGE_FET: Final = "discharge_fet"
KEY_CELL_COUNT: Final = "cell_count"
KEY_CELL_VOLTAGES: Final = "cell_voltages"
KEY_CELL_MIN: Final = "cell_min_voltage"
KEY_CELL_MAX: Final = "cell_max_voltage"
KEY_CELL_DELTA: Final = "cell_delta_voltage"
KEY_TEMPERATURES: Final = "temperatures"
KEY_PROBLEM_FLAGS: Final = "problem_flags"
KEY_SW_VERSION: Final = "sw_version"
