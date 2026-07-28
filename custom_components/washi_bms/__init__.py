"""The Washi BMS integration."""

from __future__ import annotations

import logging

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import Platform
from homeassistant.core import HomeAssistant
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
    await coordinator.async_config_entry_first_refresh()

    entry.runtime_data = coordinator
    _register_device(hass, entry, coordinator)

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


def _register_device(
    hass: HomeAssistant, entry: WashiBmsConfigEntry, coordinator: WashiBmsCoordinator
) -> None:
    """Create or refresh the device entry.

    Any field left out here keeps whatever the registry already holds, so a
    name the user set by hand survives.
    """
    extra: dict[str, str] = {}
    if coordinator.model:
        extra["model"] = coordinator.model
    nominal = (coordinator.data or {}).get("nominal_capacity")
    if nominal:
        extra["hw_version"] = f"12.8V / {nominal:g}Ah / {nominal * 12.8:g}Wh"

    dr.async_get(hass).async_get_or_create(
        config_entry_id=entry.entry_id,
        identifiers={(DOMAIN, coordinator.address)},
        connections={(dr.CONNECTION_BLUETOOTH, coordinator.address)},
        manufacturer=MANUFACTURER,
        name=entry.title,
        **extra,
    )
