"""Config flow for the Washi BMS."""

from __future__ import annotations

from typing import Any

import voluptuous as vol
from homeassistant.components.bluetooth import (
    BluetoothServiceInfoBleak,
    async_discovered_service_info,
)
from homeassistant.config_entries import ConfigFlow, ConfigFlowResult

from .const import CONF_ADDRESS, DOMAIN


class WashiBmsConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle discovery and manual setup of a Washi pack."""

    VERSION = 1

    def __init__(self) -> None:
        self._discovered: dict[str, str] = {}
        self._discovered_address: str | None = None
        self._discovered_name: str | None = None

    async def async_step_bluetooth(
        self, discovery_info: BluetoothServiceInfoBleak
    ) -> ConfigFlowResult:
        """Handle a pack found by the Bluetooth integration."""
        address = discovery_info.address.upper()
        await self.async_set_unique_id(address)
        self._abort_if_unique_id_configured()

        self._discovered_address = address
        self._discovered_name = discovery_info.name or address
        self.context["title_placeholders"] = {"name": self._discovered_name}
        return await self.async_step_confirm()

    async def async_step_confirm(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask before adding a discovered pack."""
        assert self._discovered_address is not None
        if user_input is not None:
            return self.async_create_entry(
                title=self._discovered_name or self._discovered_address,
                data={CONF_ADDRESS: self._discovered_address},
            )

        self._set_confirm_only()
        return self.async_show_form(
            step_id="confirm",
            description_placeholders={"name": self._discovered_name or ""},
        )

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Let the user pick from packs currently in range."""
        if user_input is not None:
            address = user_input[CONF_ADDRESS].upper()
            await self.async_set_unique_id(address, raise_on_progress=False)
            self._abort_if_unique_id_configured()
            return self.async_create_entry(
                title=self._discovered.get(address, address),
                data={CONF_ADDRESS: address},
            )

        configured = self._async_current_ids()
        self._discovered = {
            info.address.upper(): info.name or info.address
            for info in async_discovered_service_info(self.hass, connectable=True)
            if info.address.upper() not in configured
        }
        if not self._discovered:
            return self.async_abort(reason="no_devices_found")

        return self.async_show_form(
            step_id="user",
            data_schema=vol.Schema(
                {vol.Required(CONF_ADDRESS): vol.In(self._discovered)}
            ),
        )
