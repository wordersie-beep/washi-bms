"""Diagnostics for the Washi BMS.

The raw frames matter here: the tail of the basic-info frame is not laid out
the way the textbook JBD spec says, so a dump of the actual bytes is what makes
a new firmware revision decodable without physical access to the van.
"""

from __future__ import annotations

from typing import Any

from homeassistant.components.diagnostics import async_redact_data
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant

from .coordinator import WashiBmsCoordinator

TO_REDACT = {"address", "unique_id"}


async def async_get_config_entry_diagnostics(
    hass: HomeAssistant, entry: ConfigEntry
) -> dict[str, Any]:
    """Return diagnostics for a config entry."""
    # The dump is asked for precisely when things are broken, so it has to
    # survive an entry that never finished setting up rather than 500.
    coordinator: WashiBmsCoordinator | None = getattr(entry, "runtime_data", None)
    if coordinator is None:
        return {
            "entry": async_redact_data(dict(entry.data), TO_REDACT),
            "connection": {"loaded": False, "reason": entry.reason},
        }

    data = coordinator.data or {}

    return {
        "entry": async_redact_data(dict(entry.data), TO_REDACT),
        "connection": {
            "last_update_success": coordinator.last_update_success,
            "model": coordinator.model,
        },
        # Not redacted on purpose: these are battery telemetry, not identifiers,
        # and they are the whole point of the dump.
        "raw_frames": dict(coordinator.last_frames),
        "decoded": {
            "tail_located": data.get("tail_located"),
            "tail_index": data.get("tail_index"),
            "cell_count": data.get("cell_count"),
            "cell_voltages": data.get("cell_voltages"),
            "pack_voltage": data.get("pack_voltage"),
            "current": data.get("current"),
            "remaining_capacity": data.get("remaining_capacity"),
            "nominal_capacity": data.get("nominal_capacity"),
            "soc": data.get("soc"),
            "charge_fet": data.get("charge_fet"),
            "discharge_fet": data.get("discharge_fet"),
            "temperatures": data.get("temperatures"),
            "problem_flags": data.get("problem_flags"),
        },
    }
