# Crafter Route Card

The route card that draws the van's journey on a map: stops folded into single
places, trips split at long stops, the line coloured by speed, and — when the
recorded fixes can be matched onto the road network — the route and the
kilometres following the roads rather than the straight lines between GPS fixes.

It is not part of the Washi BMS integration; it lives here so it is versioned
and reviewable. In Home Assistant it is installed as an inline Lovelace resource
(Settings → Dashboards → Resources), so a change here has to be published to
that resource to take effect.

## Road matching

Map matching goes to OSRM's `/match` service. The public demo servers answer
`400` to a request they dislike — too many coordinates, a search radius above
their limit, timestamps they will not read — and a `400` says something about
the request, never about the roads. So a refused stretch is retried with plainer
parameters, then in halves, then on the next endpoint in `osrm_url`, and only a
stretch that all of that cannot place falls back to its own raw GPS points. The
card names the server's own reason (`TooBig`, `NoSegment`, …) when it has to.

The distance is measured along the line as drawn, so a matched route reports
road kilometres.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `entity` | — | `device_tracker` to draw (or `latitude` + `longitude` entities) |
| `hours_to_show` | `24` | Range on first paint |
| `snap_to_roads` | `true` | Match the fixes onto the road network |
| `osrm_url` | two public servers | Endpoint, or a list of them, tried in order |
| `osrm_fallback` | `true` unless `osrm_url` is set | Append the built-in public endpoints. Off by default for a hand-configured endpoint, so a private OSRM's traffic never leaks to a public one |
| `color_by_speed` | `true` | Colour the line by speed |
| `stop_radius` / `stop_min_seconds` | `30` m / `180` s | What counts as standing still |
| `trip_gap_seconds` | `900` | A longer stop or data gap starts a new trip |

## Tests

```
node lovelace/tests/run.js
```

Node only, no dependencies: the harness stubs the browser globals the card
touches and drives the matching against a mocked `fetch`.
