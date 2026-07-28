"""Sensor entities for the Washi BMS.

The `key` of every description is what forms the entity's unique id, as
``<mac>_<key>``. Those keys match the ones the integration has always used, so
an existing install keeps its entities, its customised names and its recorded
history across the upgrade. Do not rename them.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from homeassistant.components.sensor import (
    SensorDeviceClass,
    SensorEntity,
    SensorEntityDescription,
    SensorStateClass,
)
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import (
    PERCENTAGE,
    EntityCategory,
    UnitOfElectricCurrent,
    UnitOfElectricPotential,
    UnitOfPower,
    UnitOfTemperature,
)
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import CONNECTION_BLUETOOTH, DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from .const import DOMAIN, MANUFACTURER
from .coordinator import WashiBmsCoordinator

UNIT_AMPERE_HOUR = "Ah"


@dataclass(frozen=True, kw_only=True)
class WashiSensorDescription(SensorEntityDescription):
    """Describes one sensor and how to pull its value out of the snapshot."""

    value_fn: Callable[[dict[str, Any]], Any]


def _cell_voltage(index: int) -> Callable[[dict[str, Any]], float | None]:
    def _value(data: dict[str, Any]) -> float | None:
        cells = data.get("cell_voltages") or []
        return cells[index] if index < len(cells) else None

    return _value


def _fet_state(key: str) -> Callable[[dict[str, Any]], str | None]:
    def _value(data: dict[str, Any]) -> str | None:
        # Absent means the frame layout could not be verified this poll. Report
        # nothing rather than claim the pack has cut off charging when it has
        # not — a false "off" here reads as a battery fault.
        state = data.get(key)
        return None if state is None else ("on" if state else "off")

    return _value


def _temperature(index: int) -> Callable[[dict[str, Any]], float | None]:
    def _value(data: dict[str, Any]) -> float | None:
        temperatures = data.get("temperatures") or []
        return temperatures[index] if index < len(temperatures) else None

    return _value


SENSORS: tuple[WashiSensorDescription, ...] = (
    WashiSensorDescription(
        key="pack_voltage",
        translation_key="pack_voltage",
        device_class=SensorDeviceClass.VOLTAGE,
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=UnitOfElectricPotential.VOLT,
        suggested_display_precision=2,
        value_fn=lambda data: data.get("pack_voltage"),
    ),
    WashiSensorDescription(
        key="current",
        translation_key="current",
        device_class=SensorDeviceClass.CURRENT,
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=UnitOfElectricCurrent.AMPERE,
        suggested_display_precision=2,
        value_fn=lambda data: data.get("current"),
    ),
    WashiSensorDescription(
        key="power",
        translation_key="power",
        device_class=SensorDeviceClass.POWER,
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=UnitOfPower.WATT,
        suggested_display_precision=1,
        value_fn=lambda data: data.get("power"),
    ),
    WashiSensorDescription(
        key="soc",
        translation_key="soc",
        device_class=SensorDeviceClass.BATTERY,
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=PERCENTAGE,
        value_fn=lambda data: data.get("soc"),
    ),
    WashiSensorDescription(
        key="remaining_capacity",
        translation_key="remaining_capacity",
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=UNIT_AMPERE_HOUR,
        suggested_display_precision=2,
        icon="mdi:battery-charging",
        value_fn=lambda data: data.get("remaining_capacity"),
    ),
    WashiSensorDescription(
        key="nominal_capacity",
        translation_key="nominal_capacity",
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=UNIT_AMPERE_HOUR,
        suggested_display_precision=2,
        icon="mdi:battery",
        entity_category=EntityCategory.DIAGNOSTIC,
        value_fn=lambda data: data.get("nominal_capacity"),
    ),
    WashiSensorDescription(
        key="cycle_count",
        translation_key="cycle_count",
        state_class=SensorStateClass.TOTAL_INCREASING,
        icon="mdi:battery-sync",
        entity_category=EntityCategory.DIAGNOSTIC,
        value_fn=lambda data: data.get("cycle_count"),
    ),
    # This pack carries four NTC probes. Only the first was ever exposed, so
    # probes 2-4 are new entities; the first keeps its key, and with it its
    # recorded history.
    *(
        WashiSensorDescription(
            key=f"temp_{number}",
            translation_key=f"temp_{number}",
            device_class=SensorDeviceClass.TEMPERATURE,
            state_class=SensorStateClass.MEASUREMENT,
            native_unit_of_measurement=UnitOfTemperature.CELSIUS,
            suggested_display_precision=1,
            icon="mdi:thermometer",
            entity_category=None if number == 1 else EntityCategory.DIAGNOSTIC,
            value_fn=_temperature(number - 1),
        )
        for number in range(1, 5)
    ),
    WashiSensorDescription(
        key="charge_fet",
        translation_key="charge_fet",
        icon="mdi:electric-switch",
        entity_category=EntityCategory.DIAGNOSTIC,
        value_fn=_fet_state("charge_fet"),
    ),
    WashiSensorDescription(
        key="discharge_fet",
        translation_key="discharge_fet",
        icon="mdi:electric-switch-closed",
        entity_category=EntityCategory.DIAGNOSTIC,
        value_fn=_fet_state("discharge_fet"),
    ),
    WashiSensorDescription(
        key="cell_count",
        translation_key="cell_count",
        entity_category=EntityCategory.DIAGNOSTIC,
        icon="mdi:counter",
        value_fn=lambda data: data.get("cell_count"),
    ),
    WashiSensorDescription(
        key="cell_min_voltage",
        translation_key="cell_min_voltage",
        device_class=SensorDeviceClass.VOLTAGE,
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=UnitOfElectricPotential.VOLT,
        suggested_display_precision=3,
        value_fn=lambda data: data.get("cell_min_voltage"),
    ),
    WashiSensorDescription(
        key="cell_max_voltage",
        translation_key="cell_max_voltage",
        device_class=SensorDeviceClass.VOLTAGE,
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=UnitOfElectricPotential.VOLT,
        suggested_display_precision=3,
        value_fn=lambda data: data.get("cell_max_voltage"),
    ),
    WashiSensorDescription(
        key="cell_delta_voltage",
        translation_key="cell_delta_voltage",
        device_class=SensorDeviceClass.VOLTAGE,
        state_class=SensorStateClass.MEASUREMENT,
        native_unit_of_measurement=UnitOfElectricPotential.VOLT,
        suggested_display_precision=4,
        icon="mdi:call-split",
        value_fn=lambda data: data.get("cell_delta_voltage"),
    ),
    *(
        WashiSensorDescription(
            key=f"cell_{number}",
            translation_key=f"cell_{number}",
            device_class=SensorDeviceClass.VOLTAGE,
            state_class=SensorStateClass.MEASUREMENT,
            native_unit_of_measurement=UnitOfElectricPotential.VOLT,
            suggested_display_precision=3,
            value_fn=_cell_voltage(number - 1),
        )
        for number in range(1, 5)
    ),
)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: ConfigEntry,
    async_add_entities: AddEntitiesCallback,
) -> None:
    """Set up the Washi BMS sensors."""
    coordinator: WashiBmsCoordinator = entry.runtime_data
    async_add_entities(
        WashiBmsSensor(coordinator, description) for description in SENSORS
    )


class WashiBmsSensor(CoordinatorEntity[WashiBmsCoordinator], SensorEntity):
    """A single reading from the pack."""

    _attr_has_entity_name = True
    entity_description: WashiSensorDescription

    def __init__(
        self, coordinator: WashiBmsCoordinator, description: WashiSensorDescription
    ) -> None:
        super().__init__(coordinator)
        self.entity_description = description
        self._attr_unique_id = f"{coordinator.address}_{description.key}"
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, coordinator.address)},
            connections={(CONNECTION_BLUETOOTH, coordinator.address)},
            manufacturer=MANUFACTURER,
        )

    @property
    def available(self) -> bool:
        """Available only when the last poll produced a value for this sensor."""
        return (
            super().available
            and self.coordinator.data is not None
            and self.entity_description.value_fn(self.coordinator.data) is not None
        )

    @property
    def native_value(self) -> Any:
        if self.coordinator.data is None:
            return None
        return self.entity_description.value_fn(self.coordinator.data)

    @property
    def extra_state_attributes(self) -> dict[str, Any] | None:
        """Surface active protection flags on the pack-voltage sensor."""
        if self.entity_description.key != "pack_voltage" or self.coordinator.data is None:
            return None
        flags = self.coordinator.data.get("problem_flags")
        return {"protection": flags or []} if flags is not None else None
