# Washi BMS (LiFePO4 Battery) for Home Assistant

Local Bluetooth LE integration for Washi / MINIBTS LiFePO4 packs
(Shenzhen Washi Energy), such as the 12V 314Ah pack that speaks a
JBD/Xiaoxiang-style protocol over the `fff0` vendor service.

No cloud, no vendor app, no polling of anything outside the van.

## What it exposes

| Sensor | Notes |
| --- | --- |
| Pack voltage, current, power | Straight from the BMS shunt |
| Remaining / nominal capacity | Coulomb counter, in Ah |
| State of charge | The BMS's own RSOC byte, unmodified |
| Cell 1–4, lowest / highest cell, cell spread | Per-cell voltages |
| Cycle count, cell count | Diagnostic |
| Temperature | Only when the pack actually reports a probe |
| Charge / discharge FET | Only when the frame layout is verified — see below |

Active protection flags (over-voltage, over-current, temperature cut-offs and
so on) are attached to the pack-voltage sensor as a `protection` attribute.

## Why some sensors can read "unknown"

The first ten bytes of this pack's status frame follow the JBD spec exactly.
The tail of the frame — state of charge, FET status, cell count, temperature
probes — does not. Reading it at the documented offsets yields a cell count of
57 for a 4S pack and a charge-FET state that contradicts the measured current,
which is how a healthy battery ends up looking like it has cut off charging.

So the integration does not read those fields at fixed offsets. It anchors on
the cell count, whose true value is known independently from the cell-voltage
frame, and accepts a position only when its neighbours are self-consistent: a
plausible percentage where the state of charge belongs, a FET byte using only
its two low bits, and temperature readings inside a range a battery can
actually be. If no position satisfies all of that, those sensors report
`unknown` and the raw frame is logged once, at warning level.

An unknown value you can see is better than a wrong one you cannot.

## If a sensor stays unknown

Download diagnostics from the device page (⋮ → Download diagnostics). The dump
contains the raw hex of the last frames under `raw_frames`, which is what is
needed to decode a firmware revision this integration has not met yet. Attach
it to an issue.

## Installation

HACS → ⋮ → Custom repositories → add `wordersie-beep/washi-bms`, category
*Integration* → Download → restart Home Assistant. The pack is then discovered
over Bluetooth automatically.

## Requirements

Home Assistant 2024.12 or newer, and a Bluetooth adapter or ESPHome BLE proxy
within range of the pack.
