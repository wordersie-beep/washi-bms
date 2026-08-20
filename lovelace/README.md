# Crafter Route Card

A Lovelace card that draws where the van actually went.

The built-in `map` card plots every recorded fix as a circle the size of its GPS
accuracy, so a van parked with 50 m accuracy becomes a field of overlapping
blobs and a drive becomes a dotted trail that cuts corners. This card treats the
history as a journey instead of a scatter plot:

* **Outliers are dropped.** A fix implying more than `max_jump_kmh` is a bad
  fix, not a teleport, and is skipped.
* **Standing still is one place.** Fixes that never leave a `stop_radius`
  circle for `stop_min_seconds` collapse into a single stop node that keeps its
  arrival and departure time — this is what removes the parked-van jitter cloud.
* **The history is split into trips** at long stops and at gaps in the
  recording, so the line never jumps across a stretch that was never driven.
* **The line follows roads.** Each stretch of *movement* is map-matched with
  OSRM in overlapping chunks — stops are never sent, because a van parked in a
  yard has no road within any sane search radius and is exactly what makes a
  match fail. A trace the server cannot match is retried once with OSRM's own
  default radius; anything that still fails falls back to the raw track, so the
  route stays continuous either way, and the reason is shown in the summary.
* **Speed is visible.** The line is coloured by speed band, and tapping
  anywhere on it reports the speed, the clock time, the date, how far along the
  trip that point was, and the altitude.
* **Tapping the map opens it full screen**, where it pans, pinches and zooms.
  Escape or the ✕ closes it.

The slippy map is implemented in the card, so there is no Leaflet or other
library to install. The route SVG is given a real viewport that tracks the view
(as Leaflet does) rather than relying on `overflow: visible`, which WebKit does
not honour — there, a zero-sized SVG clips the whole route away. Raster tiles come from CARTO — the same source the built-in
map card uses.

## Install

Register the file as a Lovelace resource (`Settings → Dashboards → ⋮ →
Resources`) as a **JavaScript module**, pointing at wherever you host it, e.g.
`/local/crafter-route-card.js`.

## Configuration

```yaml
type: custom:crafter-route-card
entity: device_tracker.crafter                    # marker + name; optional if lat/lon given
latitude: sensor.van_bt_proxy_crafter_latitude    # preferred source: numeric sensors
longitude: sensor.van_bt_proxy_crafter_longitude
speed: sensor.van_bt_proxy_crafter_speed          # km/h, m/s or mph — the unit is read
altitude: sensor.van_bt_proxy_crafter_altitude
hours_to_show: 24
height: 320
```

| Option | Default | What it does |
| --- | --- | --- |
| `entity` | — | `device_tracker` used for the current-position marker, and as the position source when no `latitude`/`longitude` are given |
| `latitude`, `longitude` | — | Numeric sensors holding the position; more precise than a tracker's attributes and cheaper to fetch |
| `speed`, `altitude` | — | Optional sensors; speed is otherwise derived from consecutive fixes |
| `hours_to_show` | `24` | Initial range; the chips under the map switch between 1 ч … 7 д |
| `height` | `320` | Height of the inline map in pixels |
| `snap_to_roads` | `true` | Map-match trips with OSRM. Turning it off keeps everything local |
| `color_by_speed` | `true` | Colour the line by speed band and show the legend |
| `show_range_picker` | `true` | Show the range chips |
| `stop_radius` | `30` | Metres — jitter inside this radius is one place |
| `stop_min_seconds` | `180` | Seconds standing still before it counts as a stop |
| `trip_gap_seconds` | `900` | A longer stop, or a gap in the recording, starts a new trip |
| `max_jump_kmh` | `200` | Above this, a fix is treated as an outlier |
| `simplify_meters` | `4` | Douglas–Peucker tolerance, applied only when the history exceeds 12 000 points |
| `osrm_url` | `https://router.project-osrm.org` | Map-matching server. Point it at your own OSRM to keep the track off a public service |

## A note on road snapping

With `snap_to_roads: true` the card sends the thinned coordinates of each trip
to `osrm_url` — by default the public OSRM demo server. If that is not
acceptable, set `snap_to_roads: false` (the road button on the map toggles it
live) or run your own OSRM instance and point `osrm_url` at it. Nothing else in
the card leaves the network: history comes from the recorder over the existing
websocket connection.
