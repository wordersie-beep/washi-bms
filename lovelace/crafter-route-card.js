/**
 * Crafter Route Card — a real driven route on a real map, for Home Assistant.
 *
 * The built-in map card plots every recorded GPS fix as an accuracy circle, so a
 * parked van with 50 m accuracy turns into a field of blobs. This card instead
 * reconstructs the journey: it drops implausible fixes, folds stationary jitter
 * into single "stop" nodes, splits the history into trips, optionally snaps the
 * result onto the road network with OSRM, and draws one continuous line coloured
 * by speed. Tapping the map opens it full screen; tapping the line anywhere
 * reports the speed, the time and how far along the trip that point was.
 *
 * No external JavaScript: the slippy map (tiles, pan, pinch, wheel, hit-testing)
 * is implemented here, so the only network dependencies are the raster tiles the
 * built-in map card already uses, plus OSRM when road snapping is enabled.
 */

const CARD_VERSION = "1.3.1";

const DEFAULTS = {
  hours_to_show: 24,
  height: 320,
  snap_to_roads: true,
  color_by_speed: true,
  show_range_picker: true,
  stop_radius: 30,          // m — jitter inside this radius is one place
  stop_min_seconds: 180,    // s — standing still this long is a stop
  trip_gap_seconds: 900,    // s — a longer stop or data gap starts a new trip
  max_jump_kmh: 200,        // above this a fix is treated as an outlier
  simplify_meters: 4,       // Douglas–Peucker tolerance for the drawn line
  // Tried in order: the first one that answers wins. A single string works
  // too, and a config that names one endpoint still gets the built-in
  // fallbacks appended unless osrm_fallback is turned off.
  osrm_url: [
    "https://router.project-osrm.org",
    "https://routing.openstreetmap.de/routed-car",
  ],
  osrm_fallback: true,
  min_zoom: 3,
  max_zoom: 19,
};

const RANGES = [
  { h: 1, label: "1 ч" },
  { h: 6, label: "6 ч" },
  { h: 12, label: "12 ч" },
  { h: 24, label: "24 ч" },
  { h: 48, label: "2 д" },
  { h: 168, label: "7 д" },
];

// Speed bands, km/h. Everything the card draws or lists uses this one table.
const SPEED_BANDS = [
  { max: 20, color: "#38bdf8", label: "до 20" },
  { max: 50, color: "#34d399", label: "20–50" },
  { max: 80, color: "#fbbf24", label: "50–80" },
  { max: 100, color: "#fb923c", label: "80–100" },
  { max: Infinity, color: "#fb7185", label: "100+" },
];

const bandFor = (kmh) => SPEED_BANDS.find((b) => (kmh || 0) < b.max) || SPEED_BANDS[0];

/* ------------------------------------------------------------------ geometry */

const D2R = Math.PI / 180;
const EARTH = 6371008.8;

function haversine(a, b) {
  const dLat = (b.lat - a.lat) * D2R;
  const dLon = (b.lon - a.lon) * D2R;
  const s =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(a.lat * D2R) * Math.cos(b.lat * D2R) * Math.sin(dLon / 2) ** 2;
  return 2 * EARTH * Math.asin(Math.min(1, Math.sqrt(s)));
}

const worldSize = (z) => 256 * Math.pow(2, z);

function lonToX(lon, z) {
  return ((lon + 180) / 360) * worldSize(z);
}

function latToY(lat, z) {
  const s = Math.sin(Math.max(-85.05112878, Math.min(85.05112878, lat)) * D2R);
  return (0.5 - Math.log((1 + s) / (1 - s)) / (4 * Math.PI)) * worldSize(z);
}

function xToLon(x, z) {
  return (x / worldSize(z)) * 360 - 180;
}

function yToLat(y, z) {
  return Math.atan(Math.sinh(Math.PI * (1 - (2 * y) / worldSize(z)))) / D2R;
}

/** Perpendicular distance in metres, using a local equirectangular frame. */
function perpDistance(p, a, b) {
  const k = Math.cos(a.lat * D2R);
  const ax = 0;
  const ay = 0;
  const bx = (b.lon - a.lon) * k;
  const by = b.lat - a.lat;
  const px = (p.lon - a.lon) * k;
  const py = p.lat - a.lat;
  const len2 = bx * bx + by * by;
  let t = len2 ? ((px - ax) * bx + (py - ay) * by) / len2 : 0;
  t = Math.max(0, Math.min(1, t));
  const dx = px - bx * t;
  const dy = py - by * t;
  return Math.sqrt(dx * dx + dy * dy) * D2R * EARTH;
}

/** Douglas–Peucker, iterative so a long track cannot blow the stack. */
function simplify(points, tolerance) {
  if (points.length < 3) return points.slice();
  const keep = new Uint8Array(points.length);
  keep[0] = keep[points.length - 1] = 1;
  const stack = [[0, points.length - 1]];
  while (stack.length) {
    const [first, last] = stack.pop();
    let index = -1;
    let maxDist = tolerance;
    for (let i = first + 1; i < last; i++) {
      const d = perpDistance(points[i], points[first], points[last]);
      if (d > maxDist) {
        maxDist = d;
        index = i;
      }
    }
    if (index !== -1) {
      keep[index] = 1;
      stack.push([first, index], [index, last]);
    }
  }
  return points.filter((_, i) => keep[i]);
}

/* ----------------------------------------------------------------- formatting */

const num = (v, d = 0) =>
  Number(v).toLocaleString("ru-RU", { minimumFractionDigits: d, maximumFractionDigits: d });

function fmtDist(m) {
  if (!isFinite(m)) return "—";
  if (m < 950) return `${num(Math.round(m / 10) * 10)} м`;
  return `${num(m / 1000, m < 10000 ? 1 : 0)} км`;
}

function fmtDur(sec) {
  sec = Math.max(0, Math.round(sec));
  const h = Math.floor(sec / 3600);
  const m = Math.round((sec % 3600) / 60);
  if (h && m) return `${h} ч ${m} мин`;
  if (h) return `${h} ч`;
  if (m) return `${m} мин`;
  return `${sec} с`;
}

const _timeFmt = new Intl.DateTimeFormat("ru-RU", {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});
const _shortTimeFmt = new Intl.DateTimeFormat("ru-RU", { hour: "2-digit", minute: "2-digit" });
const _dateFmt = new Intl.DateTimeFormat("ru-RU", { day: "numeric", month: "short" });

const fmtTime = (ts) => _timeFmt.format(new Date(ts));
const fmtShortTime = (ts) => _shortTimeFmt.format(new Date(ts));
const fmtDate = (ts) => _dateFmt.format(new Date(ts)).replace(".", "");

function sameDay(a, b) {
  const x = new Date(a);
  const y = new Date(b);
  return x.getFullYear() === y.getFullYear() && x.getMonth() === y.getMonth() && x.getDate() === y.getDate();
}

/* -------------------------------------------------------------------- history */

/** Turn one compressed history series into sorted {t, v} numbers. */
function numberSeries(list) {
  const out = [];
  if (!Array.isArray(list)) return out;
  for (const it of list) {
    const s = it.s !== undefined ? it.s : it.state;
    if (s === undefined || s === null) continue;
    const v = Number(s);
    if (!isFinite(v)) continue;
    const ts = it.lu !== undefined ? it.lu : it.lc;
    if (!ts) continue;
    out.push({ t: ts * 1000, v });
  }
  out.sort((a, b) => a.t - b.t);
  return out;
}

/** Nearest value in a sorted series, or null when nothing is close enough. */
function lookup(series, t, tolerance) {
  if (!series.length) return null;
  let lo = 0;
  let hi = series.length - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (series[mid].t < t) lo = mid + 1;
    else hi = mid;
  }
  let best = series[lo];
  if (lo > 0 && Math.abs(series[lo - 1].t - t) < Math.abs(best.t - t)) best = series[lo - 1];
  return Math.abs(best.t - t) <= tolerance ? best.v : null;
}

/* --------------------------------------------------------------- track making */

/** Reject fixes that are impossible rather than merely noisy. */
function rejectOutliers(raw, opts) {
  const out = [];
  let last = null;
  let skipped = 0;
  for (const p of raw) {
    if (!isFinite(p.lat) || !isFinite(p.lon)) continue;
    if (Math.abs(p.lat) > 90 || Math.abs(p.lon) > 180) continue;
    if (p.lat === 0 && p.lon === 0) continue;
    if (last) {
      const dt = (p.t - last.t) / 1000;
      if (dt <= 0) continue;
      const d = haversine(last, p);
      if (d > 60 && (d / dt) * 3.6 > opts.max_jump_kmh && skipped < 3) {
        skipped++;
        continue;
      }
    }
    out.push(p);
    last = p;
    skipped = 0;
  }
  return out;
}

/**
 * Collapse runs of fixes that never leave a small circle into one stop node.
 * This is what turns a parked van's jitter cloud into a single point.
 */
function detectStops(points, opts) {
  const nodes = [];
  let i = 0;
  while (i < points.length) {
    let j = i + 1;
    while (j < points.length && haversine(points[i], points[j]) <= opts.stop_radius) j++;
    const span = (points[j - 1].t - points[i].t) / 1000;
    if (j - i >= 3 && span >= opts.stop_min_seconds) {
      let lat = 0;
      let lon = 0;
      let n = 0;
      for (let k = i; k < j; k++) {
        lat += points[k].lat;
        lon += points[k].lon;
        n++;
      }
      // A long stop drifts: keep absorbing fixes that stay around the centre,
      // otherwise one parked night turns into a string of separate stops.
      while (j < points.length) {
        const centre = { lat: lat / n, lon: lon / n };
        if (haversine(centre, points[j]) > opts.stop_radius * 1.6) break;
        lat += points[j].lat;
        lon += points[j].lon;
        n++;
        j++;
      }
      nodes.push({
        kind: "stop",
        lat: lat / n,
        lon: lon / n,
        t: points[i].t,
        tEnd: points[j - 1].t,
        dur: (points[j - 1].t - points[i].t) / 1000,
        v: 0,
        alt: points[i].alt,
      });
      i = j;
    } else {
      const p = points[i];
      nodes.push({ kind: "move", lat: p.lat, lon: p.lon, t: p.t, tEnd: p.t, v: p.v, alt: p.alt });
      i++;
    }
  }
  return nodes;
}

/** Split nodes into trips at long stops and at gaps in the recording. */
function splitTrips(nodes, opts) {
  const trips = [];
  let current = [];
  const flush = () => {
    if (current.length >= 2) trips.push(current);
  };
  for (let i = 0; i < nodes.length; i++) {
    const node = nodes[i];
    const prev = nodes[i - 1];
    if (prev) {
      const gap = (node.t - prev.tEnd) / 1000;
      const parked = prev.kind === "stop" && prev.dur >= opts.trip_gap_seconds;
      if (parked || gap > opts.trip_gap_seconds) {
        flush();
        // A long stop belongs to both trips: it ends one and starts the next.
        current = parked ? [prev] : [];
      }
    }
    current.push(node);
  }
  flush();
  return trips;
}

/** Everything the UI needs, derived once per fetch. */
function buildTrack(raw, opts) {
  const points = rejectOutliers(raw, opts);
  const nodes = detectStops(points, opts);

  // Cumulative distance and per-node speed, filled in along the way.
  let total = 0;
  let movingSec = 0;
  let maxV = 0;
  for (let i = 0; i < nodes.length; i++) {
    const n = nodes[i];
    const prev = nodes[i - 1];
    if (prev) {
      const d = haversine(prev, n);
      const dt = (n.t - prev.tEnd) / 1000;
      total += d;
      if (n.v === null || n.v === undefined) n.v = dt > 0 ? (d / dt) * 3.6 : 0;
      if (dt > 0 && dt < 600 && d > 2) movingSec += dt;
    } else if (n.v === null || n.v === undefined) {
      n.v = 0;
    }
    n.dist = total;
    if (n.kind === "move") maxV = Math.max(maxV, n.v || 0);
  }

  const trips = splitTrips(nodes, opts);
  const stops = nodes.filter((n) => n.kind === "stop");

  return {
    nodes,
    trips,
    stops,
    stats: {
      points: points.length,
      // Straight lines between fixes. Once the geometry is built this is
      // replaced by the length of the drawn route, which follows the roads
      // whenever the fixes could be matched onto them; gpsDist keeps the
      // original figure for when they could not.
      dist: total,
      gpsDist: total,
      snapped: false,
      movingSec,
      maxV,
      stops: stops.length,
      from: nodes.length ? nodes[0].t : null,
      to: nodes.length ? nodes[nodes.length - 1].tEnd : null,
    },
  };
}

/** Bounds of a list of {lat, lon}. */
function boundsOf(list) {
  if (!list.length) return null;
  let n = -90;
  let s = 90;
  let e = -180;
  let w = 180;
  for (const p of list) {
    if (p.lat > n) n = p.lat;
    if (p.lat < s) s = p.lat;
    if (p.lon > e) e = p.lon;
    if (p.lon < w) w = p.lon;
  }
  return { n, s, e, w };
}

/* ----------------------------------------------------------------- road snap */

/**
 * Map matching: laying the recorded fixes onto the road network with OSRM.
 *
 * Everything here is written around one fact about the public demo servers:
 * they answer 400 to a request they dislike (too many coordinates, a search
 * radius above their limit, timestamps they will not read), and a 400 is a
 * statement about the request, never about the roads. So a refused chunk is
 * retried with plainer parameters, then in halves, then on the next endpoint,
 * and only a chunk that all of that still cannot place falls back to its own
 * raw GPS points — the rest of the journey stays on the road network.
 */

/**
 * Parameter sets, best first, each one dropping whatever the previous one might
 * have been refused for: our own search radius (servers cap it, and answer 400
 * when it is over their limit), then the timestamps, and finally a wider radius
 * for fixes that sit too far from any road to be placed at all.
 */
const SNAP_ATTEMPTS = [
  { radius: 25, timestamps: true },
  { radius: 0, timestamps: true },
  { radius: 0, timestamps: false },
  { radius: 50, timestamps: false },
];

const SNAP_CHUNK = 60;        // coordinates per request; the demo servers cap at 100
const SNAP_TIMEOUT = 12000;   // ms — a dead server must not hold the card hostage
const SNAP_STRIKES = 2;       // network failures before an endpoint is abandoned
const SNAP_REFUSALS = 2;      // outright refusals before matching is given up on

// Matched geometry, kept for the life of the page: switching the range or
// toggling the button re-draws the same trips, and the demo servers are a
// shared resource that should not be asked the same question twice.
const SNAP_CACHE = new Map();
const SNAP_CACHE_MAX = 400;

function snapCacheGet(key) {
  return SNAP_CACHE.get(key) || null;
}

function snapCachePut(key, coords) {
  if (SNAP_CACHE.size >= SNAP_CACHE_MAX) SNAP_CACHE.delete(SNAP_CACHE.keys().next().value);
  SNAP_CACHE.set(key, coords);
}

function hostOf(url) {
  try {
    return new URL(url).host;
  } catch (err) {
    return String(url);
  }
}

/** The endpoints to try, in order, with the built-in fallbacks appended. */
function osrmEndpoints(opts) {
  const listed = Array.isArray(opts.osrm_url) ? opts.osrm_url : [opts.osrm_url];
  const out = [];
  const add = (url) => {
    if (typeof url !== "string") return;
    const clean = url.trim().replace(/\/+$/, "");
    if (clean && out.indexOf(clean) === -1) out.push(clean);
  };
  listed.forEach(add);
  if (opts.osrm_fallback !== false) DEFAULTS.osrm_url.forEach(add);
  return out;
}

/**
 * Errors carry what should happen next:
 *   "request" — the server refused these parameters; plainer ones may work.
 *   "nomatch" — the trace itself could not be placed on a road.
 *   "network" — the server is unreachable or broken; only another one helps.
 */
function snapError(message, kind) {
  const err = new Error(message);
  err.kind = kind;
  return err;
}

/** Whole seconds, strictly increasing — OSRM rejects a repeated timestamp. */
function snapTimestamps(chunk) {
  let last = 0;
  return chunk.map((p) => {
    let t = Math.round((p.t || 0) / 1000);
    if (!isFinite(t) || t <= last) t = last + 1;
    last = t;
    return t;
  });
}

function osrmUrl(base, chunk, radius, timestamps) {
  const coords = chunk.map((p) => `${p.lon.toFixed(6)},${p.lat.toFixed(6)}`).join(";");
  const params = ["geometries=geojson", "overview=full", "tidy=true"];
  if (timestamps) {
    // gaps=split only means anything with timestamps to split on.
    params.push(`timestamps=${snapTimestamps(chunk).join(";")}`, "gaps=split");
  } else {
    params.push("gaps=ignore");
  }
  if (radius) params.push(`radiuses=${chunk.map(() => radius).join(";")}`);
  return `${base}/match/v1/driving/${coords}?${params.join("&")}`;
}

/** One request. Returns the matched geometry, or throws a tagged error. */
async function osrmRequest(base, chunk, radius, timestamps, signal) {
  const local = new AbortController();
  const timer = setTimeout(() => local.abort(), SNAP_TIMEOUT);
  const relay = () => local.abort();
  signal.addEventListener("abort", relay, { once: true });
  let res;
  try {
    res = await fetch(osrmUrl(base, chunk, radius, timestamps), {
      signal: local.signal,
      referrerPolicy: "no-referrer",
    });
  } catch (err) {
    if (signal.aborted) throw Object.assign(new Error("aborted"), { name: "AbortError" });
    throw snapError(err && err.name === "AbortError" ? "нет ответа" : "сеть", "network");
  } finally {
    clearTimeout(timer);
    signal.removeEventListener("abort", relay);
  }

  // OSRM describes its own refusals in the body, 4xx included: read it, so the
  // card can say "TooBig" or "NoSegment" instead of a bare status code.
  let data = null;
  try {
    data = await res.json();
  } catch (err) {
    data = null;
  }
  if (!res.ok) {
    const detail = (data && (data.code || data.message)) || `HTTP ${res.status}`;
    const mine = res.status >= 400 && res.status < 500 && res.status !== 408 && res.status !== 429;
    throw snapError(detail, mine ? "request" : "network");
  }
  if (!data || data.code !== "Ok" || !Array.isArray(data.matchings) || !data.matchings.length) {
    throw snapError((data && (data.code || data.message)) || "нет совпадения", "nomatch");
  }
  const out = [];
  for (const m of data.matchings) {
    const line = m.geometry && m.geometry.coordinates;
    if (!Array.isArray(line)) continue;
    for (const c of line) out.push({ lat: c[1], lon: c[0] });
  }
  if (out.length < 2) throw snapError("пустая геометрия", "nomatch");
  return out;
}

const rawCoords = (chunk) => chunk.map((p) => ({ lat: p.lat, lon: p.lon }));

/**
 * Is this failure one that a shorter trace could survive? A refusal about the
 * size of the request is, and so is a trace the matcher could not place —
 * part of it usually still lies on a road. A flat "these parameters are wrong"
 * is not, and halving would only repeat the same refusal on the way down.
 */
function worthHalving(err) {
  if (!err) return false;
  if (err.kind === "nomatch") return true;
  return /toobig|too many|too long|too large|413|414/i.test(err.message || "");
}

/**
 * Snap one chunk, degrading the request until the server accepts it and, if it
 * never does, halving the trace — an over-long or partly unmatchable stretch
 * usually has a shorter piece inside it that matches perfectly well.
 *
 * Returns { coords, matched, missed, refused }: the counts are sub-chunks, and
 * they are what decides whether the card reports the route as snapped,
 * part-snapped or not snapped at all. `refused` says the server turned the
 * requests themselves away rather than failing to find a road, which is worth
 * taking to the next endpoint. Only a network failure is thrown, and it means
 * "this endpoint is no use", not "give up".
 */
async function snapChunk(chunk, base, signal, health, depth) {
  const first = chunk[0];
  const last = chunk[chunk.length - 1];
  const key =
    `${base}|${chunk.length}|${first.lat.toFixed(5)},${first.lon.toFixed(5)}` +
    `|${last.lat.toFixed(5)},${last.lon.toFixed(5)}|${first.t || 0}|${last.t || 0}`;
  const cached = snapCacheGet(key);
  if (cached) return { coords: cached, matched: 1, missed: 0, refused: false };

  let refused = false;
  let lastErr = null;
  for (const attempt of SNAP_ATTEMPTS) {
    try {
      const coords = await osrmRequest(base, chunk, attempt.radius, attempt.timestamps, signal);
      snapCachePut(key, coords);
      return { coords, matched: 1, missed: 0, refused: false };
    } catch (err) {
      if (err && err.name === "AbortError") throw err;
      if (!health.reason) health.reason = `${hostOf(base)}: ${err.message}`;
      if (err.kind === "network") throw err;
      if (err.kind === "request") refused = true;
      lastErr = err;
    }
  }

  if (depth < 2 && chunk.length >= 12 && worthHalving(lastErr)) {
    const mid = Math.floor(chunk.length / 2);
    const left = await snapChunk(chunk.slice(0, mid + 1), base, signal, health, depth + 1);
    const right = await snapChunk(chunk.slice(mid), base, signal, health, depth + 1);
    return {
      coords: left.coords.concat(right.coords.slice(1)),
      matched: left.matched + right.matched,
      missed: left.missed + right.missed,
      refused: left.refused || right.refused,
    };
  }
  return { coords: rawCoords(chunk), matched: 0, missed: 1, refused };
}

/**
 * Map-match one continuous run of movement. A parked van is the hardest thing
 * to snap — it sits in a yard or a layby with no road within the search radius
 * — so stops never come here; only the driving between them does.
 */
async function snapRun(points, opts, signal, health) {
  const coords = [];
  for (let i = 0; i < points.length - 1; i += SNAP_CHUNK - 1) {
    const chunk = points.slice(i, i + SNAP_CHUNK);
    if (chunk.length < 2) break;
    let piece = null;
    while (!piece && health.at < health.endpoints.length) {
      try {
        const res = await snapChunk(chunk, health.endpoints[health.at], signal, health, 0);
        if (!res.matched && res.refused) {
          // A server that turns every shape of request away is not going to
          // start now: hand the stretch to the next one. And once the last one
          // has refused twice, stop asking for this redraw — otherwise every
          // remaining chunk pays for the same refusal, and the wait is what the
          // person looking at the card actually feels.
          if (health.at + 1 < health.endpoints.length) {
            health.at++;
            health.strikes = 0;
            continue;
          }
          if (++health.refusals >= SNAP_REFUSALS) health.at = health.endpoints.length;
        }
        piece = res.coords;
        health.ok += res.matched;
        health.failed += res.missed;
      } catch (err) {
        if (err && err.name === "AbortError") throw err;
        // Unreachable server: give it one more chance, then move down the list.
        if (++health.strikes >= SNAP_STRIKES) {
          health.strikes = 0;
          health.at++;
        }
      }
    }
    if (!piece) {
      health.failed++;
      piece = rawCoords(chunk);
    }
    if (coords.length) coords.push(...piece.slice(1));
    else coords.push(...piece);
  }
  return coords;
}

/**
 * Split a trip into runs of movement separated by stops. The stops stay in the
 * drawn line as plain joints, so the route never breaks apart at a car park.
 */
function movingRuns(trip) {
  const runs = [];
  let run = [];
  for (const node of trip) {
    if (node.kind === "stop") {
      if (run.length) runs.push({ moving: true, nodes: run });
      runs.push({ moving: false, nodes: [node] });
      run = [];
    } else {
      run.push(node);
    }
  }
  if (run.length) runs.push({ moving: true, nodes: run });
  return runs;
}

/**
 * Carry time and speed from the recorded fixes onto the drawn vertices, and
 * measure the line as it is drawn.
 *
 * The measurement runs along the drawn geometry rather than from fix to fix, so
 * once a stretch has been matched onto the road network the kilometres are road
 * kilometres — the distance the van actually drove, not the sum of the straight
 * lines between GPS fixes, which cuts every corner and every bend. Each node
 * gets the reading of the vertex nearest to it, which is what a tap on the
 * route reports as "N км от начала".
 *
 * startDist carries the running total in from the previous trip; the total at
 * the end of this one comes back as `end`.
 */
function attachSamples(coords, nodes, startDist) {
  const base = startDist || 0;
  const speeds = new Float64Array(coords.length);
  const times = new Float64Array(coords.length);
  const dists = new Float64Array(coords.length);
  if (!nodes.length || !coords.length) return { speeds, times, dists, end: base };
  const reached = new Float64Array(nodes.length);
  const closest = new Float64Array(nodes.length).fill(Infinity);
  const seen = new Uint8Array(nodes.length);
  let j = 0;
  let run = base;
  for (let i = 0; i < coords.length; i++) {
    if (i) run += haversine(coords[i - 1], coords[i]);
    dists[i] = run;
    let best = j;
    let bestD = haversine(coords[i], nodes[j]);
    for (let k = j + 1; k < Math.min(nodes.length, j + 48); k++) {
      const d = haversine(coords[i], nodes[k]);
      if (d < bestD) {
        bestD = d;
        best = k;
      }
    }
    j = best;
    speeds[i] = nodes[j].v || 0;
    times[i] = nodes[j].t;
    // Each node is placed at the vertex that actually passes closest to it.
    if (bestD < closest[j]) {
      closest[j] = bestD;
      reached[j] = run;
      seen[j] = 1;
    }
  }
  // A node the line never passed closest to keeps the reading of the last one
  // it did, so the distance along the route never runs backwards.
  let carry = base;
  for (let k = 0; k < nodes.length; k++) {
    if (seen[k]) carry = reached[k];
    nodes[k].dist = carry;
  }
  return { speeds, times, dists, end: run };
}

/* ------------------------------------------------------------------- the map */

const SVG_NS = "http://www.w3.org/2000/svg";

/**
 * A small slippy map: raster tiles, a pane that pans and scales as one layer,
 * and an SVG route drawn in world pixels. Coordinates are kept relative to a
 * moving origin so that float precision never bites at high zoom.
 */
class VanMap {
  constructor(options) {
    this.o = Object.assign(
      { minZoom: 3, maxZoom: 19, dark: true, interactive: true, colorBySpeed: true },
      options
    );
    this._center = { lat: 53.35, lon: -6.26 };
    this._zoom = 12;
    this._z = 12;
    this._scale = 1;
    this._px0 = { x: 0, y: 0 };
    this._needOrigin = true;
    this._levels = new Map();
    this._geometry = [];
    this._markers = [];
    this._w = 0;
    this._h = 0;
    this.onTap = null;
    this.onViewChange = null;
    this._build();
  }

  /* ---------------------------------------------------------------- lifecycle */

  _build() {
    const el = (this.el = document.createElement("div"));
    el.className = "vrc-map";

    this._pane = document.createElement("div");
    this._pane.className = "vrc-pane";

    this._tileLayer = document.createElement("div");
    this._tileLayer.className = "vrc-tiles";

    this._svg = document.createElementNS(SVG_NS, "svg");
    this._svg.setAttribute("class", "vrc-svg");
    this._casingG = document.createElementNS(SVG_NS, "g");
    this._casingG.setAttribute("class", "vrc-casings");
    this._lineG = document.createElementNS(SVG_NS, "g");
    this._lineG.setAttribute("class", "vrc-lines");
    this._arrowG = document.createElementNS(SVG_NS, "g");
    this._arrowG.setAttribute("class", "vrc-arrows");
    this._svg.append(this._casingG, this._lineG, this._arrowG);

    this._pane.append(this._tileLayer, this._svg);

    this._overlay = document.createElement("div");
    this._overlay.className = "vrc-overlay";

    el.append(this._pane, this._overlay);

    this._bindGestures();
  }

  attach() {
    if (this._ro) return;
    this._ro = new ResizeObserver(() => this._measure());
    this._ro.observe(this.el);
    this._measure();
  }

  detach() {
    if (this._ro) this._ro.disconnect();
    this._ro = null;
  }

  _measure() {
    const w = this.el.clientWidth;
    const h = this.el.clientHeight;
    if (!w || !h) return;
    const first = !this._w;
    this._w = w;
    this._h = h;
    if (first && this._pendingFit) {
      const b = this._pendingFit;
      this._pendingFit = null;
      this.fitBounds(b.bounds, b.padding);
      return;
    }
    this.render();
  }

  /* ------------------------------------------------------------------- view */

  get zoom() {
    return this._zoom;
  }

  get center() {
    return { ...this._center };
  }

  setView(center, zoom) {
    this._center = { lat: center.lat, lon: center.lon };
    if (zoom !== undefined) this._zoom = this._clampZoom(zoom);
    this.render();
  }

  _clampZoom(z) {
    return Math.max(this.o.minZoom, Math.min(this.o.maxZoom, z));
  }

  fitBounds(bounds, padding = 36) {
    if (!bounds) return;
    if (!this._w || !this._h) {
      this._pendingFit = { bounds, padding };
      return;
    }
    const availW = Math.max(32, this._w - 2 * padding);
    const availH = Math.max(32, this._h - 2 * padding);
    let z = this.o.maxZoom;
    for (; z > this.o.minZoom; z--) {
      const w = Math.abs(lonToX(bounds.e, z) - lonToX(bounds.w, z));
      const h = Math.abs(latToY(bounds.s, z) - latToY(bounds.n, z));
      if (w <= availW && h <= availH) break;
    }
    const midY = (latToY(bounds.n, z) + latToY(bounds.s, z)) / 2;
    this._center = { lat: yToLat(midY, z), lon: (bounds.e + bounds.w) / 2 };
    this._zoom = this._clampZoom(z);
    this.render();
  }

  zoomBy(delta, anchor) {
    const next = this._clampZoom(this._zoom + delta);
    if (next === this._zoom) return;
    if (anchor) {
      const before = this.unproject(anchor.x, anchor.y);
      this._zoom = next;
      this._syncZoomLevel();
      const after = this.project(before.lat, before.lon);
      this._center = this.unproject(
        this._w / 2 + (after.x - anchor.x),
        this._h / 2 + (after.y - anchor.y)
      );
    } else {
      this._zoom = next;
    }
    this.render();
  }

  _syncZoomLevel() {
    const z = this._clampZoom(Math.round(this._zoom));
    if (z !== this._z) {
      this._z = z;
      this._needOrigin = true;
    }
    this._scale = Math.pow(2, this._zoom - this._z);
  }

  project(lat, lon) {
    const s = this._scale;
    const cx = lonToX(this._center.lon, this._z);
    const cy = latToY(this._center.lat, this._z);
    return {
      x: this._w / 2 + (lonToX(lon, this._z) - cx) * s,
      y: this._h / 2 + (latToY(lat, this._z) - cy) * s,
    };
  }

  unproject(x, y) {
    const s = this._scale;
    const cx = lonToX(this._center.lon, this._z);
    const cy = latToY(this._center.lat, this._z);
    return {
      lat: yToLat(cy + (y - this._h / 2) / s, this._z),
      lon: xToLon(cx + (x - this._w / 2) / s, this._z),
    };
  }

  /** Metres per screen pixel at the current view — used for hit-test radii. */
  metersPerPixel() {
    return (
      (156543.03392 * Math.cos(this._center.lat * D2R)) / Math.pow(2, this._z) / this._scale
    );
  }

  /* ----------------------------------------------------------------- render */

  render() {
    if (!this._w || !this._h) return;
    this._syncZoomLevel();

    const cx = lonToX(this._center.lon, this._z);
    const cy = latToY(this._center.lat, this._z);
    if (
      this._needOrigin ||
      Math.abs(cx - this._px0.x) > 150000 ||
      Math.abs(cy - this._px0.y) > 150000
    ) {
      this._px0 = { x: Math.round(cx), y: Math.round(cy) };
      this._needOrigin = false;
      this._buildRoute();
    }

    const tx = this._w / 2 - (cx - this._px0.x) * this._scale;
    const ty = this._h / 2 - (cy - this._px0.y) * this._scale;
    this._pane.style.transform = `translate3d(${tx.toFixed(2)}px, ${ty.toFixed(2)}px, 0) scale(${this._scale})`;

    this._sizeSvg(cx, cy);
    this._renderTiles();
    this._renderMarkers();
    if (this.onViewChange) this.onViewChange();
  }

  /**
   * Give the route SVG a real viewport covering the visible world plus half a
   * screen of slack, and re-state it in the viewBox. Relying on
   * `overflow: visible` on a zero-sized SVG is not portable — WebKit clips it,
   * which hides the whole route — and the stroke widths ride on an inherited
   * presentation attribute for the same reason.
   */
  _sizeSvg(cx, cy) {
    const halfW = this._w / (2 * this._scale);
    const halfH = this._h / (2 * this._scale);
    const x = Math.round(cx - this._px0.x - halfW * 1.5);
    const y = Math.round(cy - this._px0.y - halfH * 1.5);
    const w = Math.max(1, Math.round(halfW * 3));
    const h = Math.max(1, Math.round(halfH * 3));
    const svg = this._svg;
    svg.style.left = `${x}px`;
    svg.style.top = `${y}px`;
    svg.style.width = `${w}px`;
    svg.style.height = `${h}px`;
    svg.setAttribute("viewBox", `${x} ${y} ${w} ${h}`);

    const k = 1 / this._scale;
    this._casingG.setAttribute("stroke-width", (9 * k).toFixed(2));
    this._lineG.setAttribute("stroke-width", (5 * k).toFixed(2));
    this._arrowG.setAttribute("stroke-width", (1.5 * k).toFixed(2));
  }

  /** Swap the tile style; every loaded tile belongs to the old style. */
  setDark(dark) {
    if (this.o.dark === dark) return;
    this.o.dark = dark;
    for (const [, lv] of this._levels) lv.el.remove();
    this._levels.clear();
    this.render();
  }

  _tileUrl(z, x, y) {
    const style = this.o.dark ? "dark_all" : "light_all";
    const retina = (window.devicePixelRatio || 1) > 1.4 ? "@2x" : "";
    const sub = "abcd"[(x + y) % 4];
    return `https://${sub}.basemaps.cartocdn.com/${style}/${z}/${x}/${y}${retina}.png`;
  }

  _level(z) {
    let lv = this._levels.get(z);
    if (!lv) {
      const el = document.createElement("div");
      el.className = "vrc-level";
      lv = { z, el, px0: { ...this._px0 }, tiles: new Map() };
      this._levels.set(z, lv);
      this._tileLayer.appendChild(el);
    }
    return lv;
  }

  _renderTiles() {
    const z = this._z;
    const s = this._scale;
    let lv = this._level(z);

    const cx = lonToX(this._center.lon, z);
    const cy = latToY(this._center.lat, z);
    const halfW = this._w / (2 * s);
    const halfH = this._h / (2 * s);
    const n = Math.pow(2, z);
    const x0 = Math.floor((cx - halfW) / 256) - 1;
    const x1 = Math.floor((cx + halfW) / 256) + 1;
    const y0 = Math.max(0, Math.floor((cy - halfH) / 256) - 1);
    const y1 = Math.min(n - 1, Math.floor((cy + halfH) / 256) + 1);

    // Keep tile offsets small enough that the compositor stays precise.
    if (Math.abs(x0 * 256 - lv.px0.x) > 200000 || Math.abs(y0 * 256 - lv.px0.y) > 200000) {
      lv.el.remove();
      this._levels.delete(z);
      lv = this._level(z);
    }

    const wanted = new Set();
    for (let ty = y0; ty <= y1; ty++) {
      for (let tx = x0; tx <= x1; tx++) {
        const key = `${tx}/${ty}`;
        wanted.add(key);
        if (lv.tiles.has(key)) continue;
        const img = document.createElement("img");
        img.className = "vrc-tile";
        img.decoding = "async";
        img.alt = "";
        img.style.left = `${tx * 256 - lv.px0.x}px`;
        img.style.top = `${ty * 256 - lv.px0.y}px`;
        img.addEventListener("load", () => img.classList.add("is-on"), { once: true });
        img.addEventListener("error", () => img.classList.add("is-off"), { once: true });
        img.src = this._tileUrl(z, ((tx % n) + n) % n, ty);
        lv.el.appendChild(img);
        lv.tiles.set(key, img);
      }
    }
    for (const [key, img] of lv.tiles) {
      if (!wanted.has(key)) {
        img.remove();
        lv.tiles.delete(key);
      }
    }

    // Older zoom levels stay underneath for a moment so zooming does not flash.
    for (const [lz, other] of this._levels) {
      const k = Math.pow(2, z - lz);
      other.el.style.transform = `translate(${other.px0.x * k - this._px0.x}px, ${
        other.px0.y * k - this._px0.y
      }px) scale(${k})`;
      other.el.style.zIndex = lz === z ? "1" : "0";
      if (lz !== z) {
        if (!other.expires) other.expires = Date.now() + 500;
      } else {
        other.expires = 0;
      }
    }
    if (!this._sweep) {
      this._sweep = setTimeout(() => {
        this._sweep = null;
        const now = Date.now();
        for (const [lz, other] of this._levels) {
          if (other.expires && other.expires <= now) {
            other.el.remove();
            this._levels.delete(lz);
          }
        }
      }, 550);
    }
  }

  /* ------------------------------------------------------------------ route */

  setRoute(geometry) {
    this._geometry = geometry || [];
    this._buildRoute();
    this.render();
  }

  setMarkers(markers) {
    this._markers = markers || [];
    this._overlay.replaceChildren();
    for (const m of this._markers) {
      const el = document.createElement("div");
      el.className = `vrc-marker ${m.className || ""}`;
      el.innerHTML = m.html || "";
      if (m.title) el.title = m.title;
      m._el = el;
      this._overlay.appendChild(el);
    }
    if (this._tooltip) this._overlay.appendChild(this._tooltip);
    this._renderMarkers();
  }

  setTooltip(el) {
    if (this._tooltip) this._tooltip.remove();
    this._tooltip = el;
    if (el) this._overlay.appendChild(el);
  }

  _renderMarkers() {
    for (const m of this._markers) {
      if (!m._el) continue;
      const p = this.project(m.lat, m.lon);
      const visible = p.x > -80 && p.y > -80 && p.x < this._w + 80 && p.y < this._h + 80;
      m._el.style.display = visible ? "" : "none";
      if (visible) m._el.style.transform = `translate(${p.x.toFixed(1)}px, ${p.y.toFixed(1)}px)`;
    }
  }

  _path(d, cls, color) {
    const p = document.createElementNS(SVG_NS, "path");
    p.setAttribute("d", d);
    p.setAttribute("class", cls);
    if (color) p.setAttribute("stroke", color);
    return p;
  }

  _buildRoute() {
    const z = this._z;
    const ox = this._px0.x;
    const oy = this._px0.y;
    const casings = document.createDocumentFragment();
    const lines = document.createDocumentFragment();

    const xy = (c) => `${(lonToX(c.lon, z) - ox).toFixed(1)} ${(latToY(c.lat, z) - oy).toFixed(1)}`;

    for (const seg of this._geometry) {
      if (seg.coords.length < 2) continue;
      seg._xy = seg.coords.map(xy);
      casings.appendChild(this._path(`M${seg._xy.join("L")}`, "vrc-casing"));

      if (!this.o.colorBySpeed || !seg.speeds) {
        lines.appendChild(this._path(`M${seg._xy.join("L")}`, "vrc-line", SPEED_BANDS[0].color));
        continue;
      }
      let start = 0;
      let band = bandFor(seg.speeds[0]);
      for (let i = 1; i < seg.coords.length; i++) {
        const b = bandFor(seg.speeds[i]);
        if (b === band) continue;
        lines.appendChild(this._path(`M${seg._xy.slice(start, i + 1).join("L")}`, "vrc-line", band.color));
        start = i;
        band = b;
      }
      lines.appendChild(this._path(`M${seg._xy.slice(start).join("L")}`, "vrc-line", band.color));
    }

    this._casingG.replaceChildren(casings);
    this._lineG.replaceChildren(lines);
    this._buildArrows();
  }

  /** Direction chevrons, spaced along the line so a trip reads at a glance. */
  _buildArrows() {
    const z = this._z;
    const ox = this._px0.x;
    const oy = this._px0.y;
    const spacing = 130;
    const frag = document.createDocumentFragment();
    let budget = 260;

    for (const seg of this._geometry) {
      if (seg.coords.length < 2) continue;
      let carry = spacing * 0.6;
      let prev = null;
      for (const c of seg.coords) {
        const p = { x: lonToX(c.lon, z) - ox, y: latToY(c.lat, z) - oy };
        if (prev) {
          const dx = p.x - prev.x;
          const dy = p.y - prev.y;
          const len = Math.hypot(dx, dy);
          if (len > 0.01) {
            const angle = (Math.atan2(dy, dx) * 180) / Math.PI;
            let travelled = carry;
            while (travelled <= len && budget > 0) {
              const t = travelled / len;
              const g = document.createElementNS(SVG_NS, "g");
              g.setAttribute(
                "transform",
                `translate(${(prev.x + dx * t).toFixed(1)} ${(prev.y + dy * t).toFixed(1)}) rotate(${angle.toFixed(1)})`
              );
              const a = document.createElementNS(SVG_NS, "path");
              a.setAttribute("d", "M-3.5 -3.4 L1.6 0 L-3.5 3.4");
              g.appendChild(a);
              frag.appendChild(g);
              travelled += spacing;
              budget--;
            }
            carry = travelled - len;
          }
        }
        prev = p;
      }
    }
    this._arrowG.replaceChildren(frag);
  }

  /* --------------------------------------------------------------- gestures */

  setInteractive(flag) {
    this.o.interactive = flag;
    this.el.classList.toggle("is-interactive", !!flag);
  }

  _bindGestures() {
    const el = this.el;
    const pointers = new Map();
    let moved = 0;
    let startedAt = 0;
    let pinchDist = 0;
    let pinchZoom = 0;

    const mid = () => {
      const list = [...pointers.values()];
      return {
        x: (list[0].x + list[1].x) / 2,
        y: (list[0].y + list[1].y) / 2,
        d: Math.hypot(list[0].x - list[1].x, list[0].y - list[1].y),
      };
    };

    const local = (ev) => {
      const r = el.getBoundingClientRect();
      return { x: ev.clientX - r.left, y: ev.clientY - r.top };
    };

    el.addEventListener("pointerdown", (ev) => {
      if (!this.o.interactive) {
        startedAt = Date.now();
        moved = 0;
        pointers.set(ev.pointerId, local(ev));
        return;
      }
      el.setPointerCapture(ev.pointerId);
      pointers.set(ev.pointerId, local(ev));
      moved = 0;
      startedAt = Date.now();
      if (pointers.size === 2) {
        const m = mid();
        pinchDist = m.d;
        pinchZoom = this._zoom;
      }
      el.classList.add("is-dragging");
    });

    el.addEventListener("pointermove", (ev) => {
      if (!pointers.has(ev.pointerId)) return;
      const prev = pointers.get(ev.pointerId);
      const next = local(ev);
      pointers.set(ev.pointerId, next);
      moved += Math.hypot(next.x - prev.x, next.y - prev.y);
      if (!this.o.interactive) return;

      if (pointers.size === 1) {
        const s = this._scale;
        const cx = lonToX(this._center.lon, this._z) - (next.x - prev.x) / s;
        const cy = latToY(this._center.lat, this._z) - (next.y - prev.y) / s;
        this._center = { lat: yToLat(cy, this._z), lon: xToLon(cx, this._z) };
        this.render();
      } else if (pointers.size === 2 && pinchDist > 0) {
        const m = mid();
        const target = this._clampZoom(pinchZoom + Math.log2(Math.max(0.05, m.d / pinchDist)));
        if (Math.abs(target - this._zoom) > 0.001) this.zoomBy(target - this._zoom, m);
      }
    });

    const end = (ev) => {
      const p = pointers.get(ev.pointerId);
      pointers.delete(ev.pointerId);
      if (pointers.size < 2) pinchDist = 0;
      if (pointers.size === 0) el.classList.remove("is-dragging");
      if (!p) return;
      const tap = moved < 8 && Date.now() - startedAt < 500;
      if (tap && this.onTap) this.onTap({ ...p, ...this.unproject(p.x, p.y) });
    };

    el.addEventListener("pointerup", end);
    el.addEventListener("pointercancel", (ev) => {
      pointers.delete(ev.pointerId);
      if (pointers.size < 2) pinchDist = 0;
      if (pointers.size === 0) el.classList.remove("is-dragging");
    });

    el.addEventListener(
      "wheel",
      (ev) => {
        if (!this.o.interactive) return;
        ev.preventDefault();
        const step = ev.deltaMode === 1 ? 0.06 : 0.0022;
        this.zoomBy(-ev.deltaY * step, local(ev));
      },
      { passive: false }
    );

    el.addEventListener("dblclick", (ev) => {
      if (!this.o.interactive) return;
      ev.preventDefault();
      this.zoomBy(1, local(ev));
    });
  }
}

/* ------------------------------------------------------------------- styles */

const STYLES = `
.vrc-map {
  position: relative;
  overflow: hidden;
  width: 100%;
  height: 100%;
  background: #0f141b;
  touch-action: auto;
  cursor: pointer;
  user-select: none;
  -webkit-user-select: none;
  -webkit-tap-highlight-color: transparent;
}
.vrc-map.is-interactive { touch-action: none; cursor: grab; }
.vrc-map.is-interactive.is-dragging { cursor: grabbing; }
.vrc-pane, .vrc-tiles, .vrc-level {
  position: absolute; left: 0; top: 0;
  transform-origin: 0 0;
}
.vrc-pane { will-change: transform; }
.vrc-tile {
  position: absolute; width: 256px; height: 256px;
  opacity: 0; transition: opacity .2s ease-out;
  pointer-events: none; -webkit-user-drag: none;
}
.vrc-tile.is-on { opacity: 1; }
.vrc-svg {
  position: absolute; left: 0; top: 0;
  overflow: visible; pointer-events: none;
  z-index: 2;
}
.vrc-casing {
  fill: none; stroke: rgba(0,0,0,.5);
  stroke-linecap: round; stroke-linejoin: round;
}
.vrc-line {
  fill: none; stroke-linecap: round; stroke-linejoin: round;
}
.vrc-arrows path {
  fill: none; stroke: rgba(255,255,255,.9);
  stroke-linecap: round; stroke-linejoin: round;
}
.vrc-overlay { position: absolute; inset: 0; pointer-events: none; z-index: 3; }
.vrc-marker { position: absolute; left: 0; top: 0; will-change: transform; }
.vrc-marker > * {
  position: absolute;
  transform: translate(-50%, -50%);
  display: flex; align-items: center; justify-content: center;
  border-radius: 50%;
  color: #fff; font-weight: 600; font-size: 12px; line-height: 1;
  border: 2px solid rgba(255,255,255,.92);
  box-shadow: 0 1px 6px rgba(0,0,0,.55);
}
.vrc-marker.start > * { width: 22px; height: 22px; background: #22c55e; }
.vrc-marker.finish > * { width: 28px; height: 28px; background: #2563eb; font-size: 13px; }
.vrc-marker.stop > * {
  width: auto; height: 20px; min-width: 20px; padding: 0 6px;
  border-radius: 10px; background: rgba(100,116,139,.95); font-size: 10px;
  font-weight: 500; white-space: nowrap;
  /* sits just below the point so it never hides the start/finish pin */
  transform: translate(-50%, 8px);
}
.vrc-tip { position: absolute; left: 0; top: 0; z-index: 6; }
.vrc-tip-probe {
  position: absolute; width: 13px; height: 13px; border-radius: 50%;
  transform: translate(-50%, -50%);
  background: #fff; border: 3px solid #0ea5e9;
  box-shadow: 0 0 0 3px rgba(14,165,233,.25);
}
.vrc-tip-inner {
  position: absolute;
  transform: translate(-50%, calc(-100% - 16px));
  background: rgba(15,23,42,.95);
  border: 1px solid rgba(148,163,184,.28);
  color: #f1f5f9;
  border-radius: 12px; padding: 8px 11px;
  font-size: 12px; line-height: 1.4; white-space: nowrap;
  box-shadow: 0 8px 24px rgba(0,0,0,.5);
  pointer-events: none;
}
.vrc-tip.is-below .vrc-tip-inner { transform: translate(-50%, 16px); }
.vrc-tip-h { font-size: 14px; font-weight: 600; margin-bottom: 2px; }
.vrc-tip-r { color: #cbd5e1; }
.vrc-dot {
  display: inline-block; width: 9px; height: 9px; border-radius: 50%;
  margin-right: 6px; vertical-align: baseline;
}
.vrc-ctls { position: absolute; inset: 0; z-index: 4; pointer-events: none; }
.vrc-ctl {
  position: absolute; display: flex; flex-direction: column; gap: 6px;
  pointer-events: auto;
}
.vrc-ctl.at-left { left: 10px; top: 10px; }
.vrc-ctl.at-right { right: 10px; top: 10px; }
.vrc-btn {
  width: 34px; height: 34px; border-radius: 9px;
  display: flex; align-items: center; justify-content: center;
  background: rgba(15,23,42,.82); color: #e2e8f0;
  border: 1px solid rgba(148,163,184,.28);
  cursor: pointer; padding: 0;
  -webkit-backdrop-filter: blur(3px);
  backdrop-filter: blur(3px);
}
.vrc-btn:hover { background: rgba(30,41,59,.92); }
.vrc-btn.is-on { background: #2563eb; border-color: #2563eb; color: #fff; }
.vrc-btn ha-icon { --mdc-icon-size: 20px; }
.vrc-attr {
  position: absolute; right: 0; bottom: 0; z-index: 4;
  font-size: 10px; line-height: 1.6;
  background: rgba(15,23,42,.62); color: #cbd5e1;
  padding: 1px 6px; border-radius: 6px 0 0 0;
}
.vrc-attr a { color: #cbd5e1; text-decoration: none; }
.vrc-note {
  position: absolute; left: 10px; bottom: 8px;
  z-index: 4; font-size: 11px;
  transition: opacity .6s ease-out;
  background: rgba(15,23,42,.8); color: #e2e8f0;
  border: 1px solid rgba(148,163,184,.25);
  border-radius: 14px; padding: 4px 12px; white-space: nowrap;
  pointer-events: none;
}
.vrc-note.is-faded { opacity: 0; }
.vrc-head {
  display: flex; align-items: center; gap: 8px;
  padding: 12px 14px 2px; font-size: 15px; font-weight: 500;
  color: var(--primary-text-color);
}
.vrc-head .vrc-spacer { flex: 1; }
.vrc-stats {
  display: flex; flex-wrap: wrap; gap: 2px 14px;
  padding: 6px 14px 10px; font-size: 12px;
  color: var(--secondary-text-color);
}
.vrc-stats b { color: var(--primary-text-color); font-weight: 500; }
.vrc-ranges { display: flex; flex-wrap: wrap; gap: 6px; padding: 10px 14px 0; }
.vrc-chip {
  border: 1px solid var(--divider-color, rgba(148,163,184,.35));
  background: transparent; color: var(--secondary-text-color);
  border-radius: 14px; padding: 3px 11px; font-size: 12px; cursor: pointer;
  font-family: inherit;
}
.vrc-chip.is-on {
  background: var(--primary-color, #2563eb);
  border-color: var(--primary-color, #2563eb);
  color: var(--text-primary-color, #fff);
}
.vrc-legend {
  display: flex; flex-wrap: wrap; gap: 4px 12px;
  padding: 10px 14px 14px; font-size: 11px;
  color: var(--secondary-text-color);
}
.vrc-legend span { white-space: nowrap; }
.vrc-legend i {
  display: inline-block; width: 15px; height: 3px;
  border-radius: 2px; margin-right: 5px; vertical-align: middle;
}
.vrc-empty {
  display: flex; align-items: center; justify-content: center;
  height: 100%; padding: 20px; text-align: center;
  color: #94a3b8; font-size: 13px;
}
.vrc-fs {
  position: fixed; inset: 0; z-index: 12;
  display: flex; flex-direction: column;
  background: var(--card-background-color, #111);
  color: var(--primary-text-color, #fff);
}
.vrc-fs .vrc-fs-map { flex: 1; min-height: 0; position: relative; }
.vrc-fs .vrc-head { padding-top: max(12px, env(safe-area-inset-top)); }
.vrc-fs .vrc-legend { padding-bottom: max(14px, env(safe-area-inset-bottom)); }
`;

/* --------------------------------------------------------------------- card */

const SPEED_UNITS = { "km/h": 1, "km/ч": 1, "км/ч": 1, "m/s": 3.6, "м/с": 3.6, mph: 1.609344 };

function plural(n, one, few, many) {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

/**
 * Thin a trip for map matching: OSRM wants fixes spaced out along the road, not
 * a cloud of near-duplicates. Ends and stops are always kept so the matched
 * geometry still starts and finishes where the van actually did.
 */
function thinForSnap(trip, minGap) {
  const out = [];
  let last = null;
  for (let i = 0; i < trip.length; i++) {
    const node = trip[i];
    const anchor = i === 0 || i === trip.length - 1 || node.kind === "stop";
    if (!last || anchor || haversine(last, node) >= minGap) {
      out.push(node);
      last = node;
    }
  }
  return out;
}

function icon(name) {
  return `<ha-icon icon="${name}"></ha-icon>`;
}

class CrafterRouteCard extends HTMLElement {
  static getStubConfig(hass) {
    const tracker = Object.keys(hass.states).find((e) => e.startsWith("device_tracker."));
    return { type: "custom:crafter-route-card", entity: tracker || "", hours_to_show: 24 };
  }

  constructor() {
    super();
    this.attachShadow({ mode: "open" });
    this._track = { nodes: [], trips: [], stops: [], stats: {} };
    this._geometry = [];
    this._tipNode = null;
    this._loading = false;
    this._error = null;
    this._snapState = "off";
    // Bumped by every redraw: an older pass that is still waiting on a server
    // checks it and drops its result instead of drawing over a newer one.
    this._gen = 0;
    this._snapping = false;
    // Whether the map has ever been fitted to an actual route, as opposed to
    // being parked on the van because there was nothing else to fit yet.
    this._fitted = false;
  }

  setConfig(config) {
    const cfg = Object.assign({}, DEFAULTS, config || {});
    if (!cfg.entity && !(cfg.latitude && cfg.longitude)) {
      throw new Error("Нужен entity (device_tracker) или пара latitude + longitude");
    }
    // A hand-configured endpoint is never silently topped up with the public
    // ones: someone running their own OSRM does not expect the van's track to
    // leave the house. Set osrm_fallback: true to allow it anyway.
    if (config && config.osrm_url && config.osrm_fallback === undefined) cfg.osrm_fallback = false;
    this._config = cfg;
    this._hours = cfg.hours_to_show;
    this._snap = !!cfg.snap_to_roads;
    if (this.shadowRoot.childElementCount) {
      this._buildDom();
      this._reload();
    }
  }

  set hass(hass) {
    const first = !this._hass;
    this._hass = hass;
    if (first) {
      this._buildDom();
      this._reload();
      return;
    }
    this._syncLive();
  }

  getCardSize() {
    return Math.ceil((this._config?.height || DEFAULTS.height) / 50) + 3;
  }

  connectedCallback() {
    if (this._hass && !this.shadowRoot.childElementCount) this._buildDom();
    if (this._map) this._map.attach();
    // A refresh in the middle of a matching pass would cancel it and start
    // over, so the roads would never finish arriving on a slow server.
    this._timer = setInterval(() => {
      if (!this._snapping) this._reload();
    }, 120000);
  }

  disconnectedCallback() {
    clearInterval(this._timer);
    clearTimeout(this._noteTimer);
    if (this._map) this._map.detach();
    if (this._abort) this._abort.abort();
    this._closeFullscreen();
  }

  /* ------------------------------------------------------------------- dom */

  _isDark() {
    const t = this._hass && this._hass.themes;
    return t && typeof t.darkMode === "boolean" ? t.darkMode : true;
  }

  _attribution() {
    const el = document.createElement("div");
    el.className = "vrc-attr";
    el.innerHTML =
      `© <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>` +
      `, © <a href="https://carto.com/attributions" target="_blank" rel="noopener">CARTO</a>` +
      `<span class="vrc-osrm"></span>` +
      ` · v${CARD_VERSION}`;
    return el;
  }

  _buildDom() {
    const cfg = this._config;
    const root = this.shadowRoot;
    root.replaceChildren();

    const style = document.createElement("style");
    style.textContent = STYLES;

    const card = document.createElement("ha-card");

    this._head = document.createElement("div");
    this._head.className = "vrc-head";
    this._head.addEventListener("click", (e) => this._onChromeClick(e));

    this._statsEl = document.createElement("div");
    this._statsEl.className = "vrc-stats";

    this._mapWrap = document.createElement("div");
    this._mapWrap.className = "vrc-mapwrap";
    this._mapWrap.style.cssText = `position:relative;overflow:hidden;height:${cfg.height}px;`;

    this._map = new VanMap({
      minZoom: cfg.min_zoom,
      maxZoom: cfg.max_zoom,
      dark: this._isDark(),
      interactive: false,
      colorBySpeed: !!cfg.color_by_speed,
    });
    this._map.onTap = (p) => this._onTap(p);
    this._map.onViewChange = () => this._positionTip();

    this._note = document.createElement("div");
    this._note.className = "vrc-note";
    this._note.textContent = "Нажмите — карта на весь экран";

    this._ctlsEl = document.createElement("div");
    this._ctlsEl.className = "vrc-ctls";
    this._ctlsEl.addEventListener("click", (e) => this._onChromeClick(e));

    this._mapWrap.append(this._map.el, this._attribution(), this._note, this._ctlsEl);

    this._rangesEl = document.createElement("div");
    this._rangesEl.className = "vrc-ranges";
    this._rangesEl.addEventListener("click", (e) => this._onChromeClick(e));

    this._legendEl = document.createElement("div");
    this._legendEl.className = "vrc-legend";

    card.append(this._head, this._statsEl, this._mapWrap);
    if (cfg.show_range_picker) card.appendChild(this._rangesEl);
    card.appendChild(this._legendEl);
    root.append(style, card);

    this._map.attach();
    this._renderChrome();
  }

  /* ---------------------------------------------------------------- chrome */

  _chromeTargets() {
    const list = [
      {
        head: this._head,
        stats: this._statsEl,
        ranges: this._rangesEl,
        legend: this._legendEl,
        controls: this._ctlsEl,
        fullscreen: false,
      },
    ];
    if (this._fs) list.push(this._fs.chrome);
    return list;
  }

  _renderChrome() {
    const cfg = this._config;
    const stats = this._statsHtml();
    const legend = this._legendHtml();
    for (const t of this._chromeTargets()) {
      const title = t.fullscreen ? cfg.title || "Маршрут" : cfg.title || "";
      t.head.innerHTML = `<span>${escapeHtml(title)}</span>`;
      t.head.style.display = title ? "" : "none";
      t.controls.innerHTML = this._controlsHtml(t.fullscreen);
      t.stats.innerHTML = stats;
      t.ranges.innerHTML = RANGES.map(
        (r) =>
          `<button class="vrc-chip ${r.h === this._hours ? "is-on" : ""}" data-h="${r.h}">${r.label}</button>`
      ).join("");
      t.legend.innerHTML = legend;
    }
    const osrm =
      this._snapState === "full" || this._snapState === "partial"
        ? ", маршрут по дорогам — OSRM"
        : "";
    for (const el of this._allRoots()) {
      const tag = el.querySelector(".vrc-osrm");
      if (tag) tag.textContent = osrm;
    }
  }

  _allRoots() {
    const roots = [this.shadowRoot];
    if (this._fs) roots.push(this._fs.root);
    return roots;
  }

  _statsHtml() {
    if (this._error) return `<span>Ошибка: ${escapeHtml(this._error)}</span>`;
    const s = this._track.stats;
    if (!this._track.nodes.length) {
      return `<span>${this._loading ? "Загрузка истории…" : "За выбранный период точек нет"}</span>`;
    }
    const period =
      s.from && s.to
        ? `${fmtDate(s.from)} ${fmtShortTime(s.from)} — ${
            sameDay(s.from, s.to) ? "" : fmtDate(s.to) + " "
          }${fmtShortTime(s.to)}`
        : "";
    const parts = [
      `<b>${fmtDist(s.dist)}</b> ${s.snapped ? "по дорогам" : "пройдено"}`,
      `в пути <b>${fmtDur(s.movingSec)}</b>`,
      `макс <b>${num(s.maxV, 0)} км/ч</b>`,
      `<b>${s.stops}</b> ${plural(s.stops, "стоянка", "стоянки", "стоянок")}`,
    ];
    if (period) parts.push(period);
    if (this._loading) parts.push("обновление…");
    else if (this._snapping) parts.push("прокладываю по дорогам…");
    const why = this._snapReason ? ` (${escapeHtml(this._snapReason)})` : "";
    if (this._snapState === "partial") parts.push(`часть маршрута не легла на дороги${why}`);
    else if (this._snapState === "none") parts.push(`дороги недоступны — линия по точкам GPS${why}`);
    return parts.map((p) => `<span>${p}</span>`).join("");
  }

  _legendHtml() {
    if (!this._config.color_by_speed) return "";
    return (
      SPEED_BANDS.map((b) => `<span><i style="background:${b.color}"></i>${b.label} км/ч</span>`).join("") +
      `<span><i style="background:#94a3b8"></i>стоянка</span>`
    );
  }

  _onChromeClick(ev) {
    const chip = ev.target.closest ? ev.target.closest(".vrc-chip") : null;
    if (chip) {
      const h = Number(chip.dataset.h);
      if (h && h !== this._hours) {
        this._hours = h;
        this._touched = false;
        this._hideTip();
        this._reload();
      }
      return;
    }
    const btn = ev.target.closest ? ev.target.closest(".vrc-btn") : null;
    if (!btn) return;
    const act = btn.dataset.act;
    if (act === "zin" || act === "zout") {
      this._map.zoomBy(act === "zin" ? 1 : -1);
      this._touched = true;
    } else if (act === "fit") {
      this._touched = false;
      this._hideTip();
      this._fitAll();
    } else if (act === "snap") {
      this._snap = !this._snap;
      this._renderChrome();
      this._rebuild();
    } else if (act === "full") {
      this._openFullscreen();
    } else if (act === "close") {
      this._closeFullscreen();
    }
  }

  /* ------------------------------------------------------------------ data */

  _speedFactor() {
    const cfg = this._config;
    const st = cfg.speed && this._hass ? this._hass.states[cfg.speed] : null;
    const unit = st && st.attributes ? st.attributes.unit_of_measurement : null;
    return SPEED_UNITS[unit] !== undefined ? SPEED_UNITS[unit] : 1;
  }

  async _fetch() {
    const cfg = this._config;
    const useSensors = !!(cfg.latitude && cfg.longitude);
    const end = new Date();
    const start = new Date(end.getTime() - this._hours * 3600 * 1000);

    const ids = useSensors ? [cfg.latitude, cfg.longitude] : [cfg.entity];
    if (cfg.speed) ids.push(cfg.speed);
    if (cfg.altitude && useSensors) ids.push(cfg.altitude);

    const res = await this._hass.callWS({
      type: "history/history_during_period",
      start_time: start.toISOString(),
      end_time: end.toISOString(),
      entity_ids: ids,
      minimal_response: useSensors,
      no_attributes: useSensors,
      significant_changes_only: false,
    });

    const k = this._speedFactor();
    const speed = cfg.speed ? numberSeries(res[cfg.speed]) : [];
    const raw = [];

    if (useSensors) {
      const lat = numberSeries(res[cfg.latitude]);
      const lon = numberSeries(res[cfg.longitude]);
      const alt = cfg.altitude ? numberSeries(res[cfg.altitude]) : [];
      for (const p of lat) {
        const lo = lookup(lon, p.t, 15000);
        if (lo === null) continue;
        const v = speed.length ? lookup(speed, p.t, 30000) : null;
        raw.push({
          t: p.t,
          lat: p.v,
          lon: lo,
          v: v === null ? null : v * k,
          alt: alt.length ? lookup(alt, p.t, 60000) : null,
        });
      }
    } else {
      for (const it of res[cfg.entity] || []) {
        const a = it.a || it.attributes;
        if (!a || a.latitude === undefined || a.latitude === null) continue;
        const ts = it.lu !== undefined ? it.lu : it.lc;
        if (!ts) continue;
        const t = ts * 1000;
        const v = speed.length ? lookup(speed, t, 30000) : null;
        raw.push({
          t,
          lat: Number(a.latitude),
          lon: Number(a.longitude),
          v: v === null ? null : v * k,
          alt: a.altitude !== undefined && a.altitude !== null ? Number(a.altitude) : null,
        });
      }
      raw.sort((x, y) => x.t - y.t);
    }
    return raw;
  }

  /**
   * Reload the history and redraw.
   *
   * Map matching talks to a server, and a server can be slow — so the card
   * never waits on it before showing anything. The recorded fixes are drawn
   * first, and the roads arrive afterwards in a second pass. Nothing here
   * refuses to start because something else is in flight either: pressing 6 ч
   * while the roads for 1 ч are still coming back cancels that pass and starts
   * this one, which is what the button press meant.
   */
  async _reload() {
    if (!this._hass || !this._config) return;
    const gen = ++this._gen;
    if (this._abort) this._abort.abort();
    this._loading = true;
    this._error = null;
    this._renderChrome();

    let raw;
    try {
      raw = await this._fetch();
    } catch (err) {
      if (gen !== this._gen) return;
      this._loading = false;
      if (!err || err.name !== "AbortError") {
        this._error = (err && (err.message || err.error)) || String(err);
      }
      this._renderChrome();
      return;
    }
    if (gen !== this._gen) return;

    this._track = buildTrack(raw, this._config);
    this._loading = false;
    try {
      await this._buildGeometry(false);
    } catch (err) {
      if (gen !== this._gen) return;
      if (err && err.name !== "AbortError") this._error = err.message || String(err);
    }
    if (gen !== this._gen) return;
    this._draw(!this._touched);
    this._renderChrome();

    await this._snapPass(gen);
  }

  /** Re-run only the geometry stage — used when road snapping is toggled. */
  async _rebuild() {
    if (!this._track.trips.length) return;
    const gen = ++this._gen;
    if (this._abort) this._abort.abort();
    try {
      await this._buildGeometry(false);
    } catch (err) {
      if (gen !== this._gen) return;
      if (err && err.name !== "AbortError") this._error = err.message || String(err);
    }
    if (gen !== this._gen) return;
    this._draw(false);
    this._renderChrome();
    await this._snapPass(gen);
  }

  /** The road-matching pass: slow, cancellable, and never blocks the buttons. */
  async _snapPass(gen) {
    if (!this._snap || !this._track.trips.length) return;
    this._snapping = true;
    this._renderChrome();
    try {
      await this._buildGeometry(true);
    } catch (err) {
      if (err && err.name === "AbortError") return;
      if (gen === this._gen) this._error = (err && err.message) || String(err);
    } finally {
      if (gen === this._gen) this._snapping = false;
    }
    if (gen !== this._gen) return;
    this._draw(false);
    this._renderChrome();
  }

  async _buildGeometry(useSnap) {
    const cfg = this._config;
    const snapping = !!useSnap && this._snap;
    if (this._abort) this._abort.abort();
    const ac = (this._abort = new AbortController());

    // The drawn line keeps every recorded fix, so colouring by speed stays
    // honest; only a very long history is thinned, and then only to stay
    // renderable.
    const tolerance = this._track.nodes.length > 12000 ? cfg.simplify_meters : 0;

    const geometry = [];
    const health = {
      endpoints: osrmEndpoints(cfg),
      at: 0,
      strikes: 0,
      ok: 0,
      failed: 0,
      refusals: 0,
      reason: "",
    };
    let chunkBudget = 60;
    let cursor = 0;
    let prevEnd = null;

    for (const trip of this._track.trips) {
      if (trip.length < 2) continue;
      const coords = [];
      for (const run of movingRuns(trip)) {
        let piece = (tolerance && run.moving ? simplify(run.nodes, tolerance) : run.nodes).map(
          (p) => ({ lat: p.lat, lon: p.lon })
        );
        if (snapping && run.moving) {
          const thinned = thinForSnap(run.nodes, 20);
          const needed = Math.ceil(thinned.length / (SNAP_CHUNK - 1));
          if (thinned.length >= 4 && needed <= chunkBudget) {
            chunkBudget -= needed;
            const snapped = await snapRun(thinned, cfg, ac.signal, health);
            if (snapped.length >= 2) piece = snapped;
          } else if (thinned.length >= 4) {
            // Out of request budget for this redraw: this stretch stays on GPS.
            health.failed += needed;
          }
        }
        coords.push(...piece);
      }
      if (coords.length < 2) continue;

      // Whatever happened between two trips — a data hole, or a move nobody
      // recorded — still counts towards the distance covered.
      if (prevEnd) cursor += haversine(prevEnd, trip[0]);
      const s = attachSamples(coords, trip, cursor);
      cursor = s.end;
      prevEnd = trip[trip.length - 1];
      geometry.push({ coords, speeds: s.speeds, times: s.times, dists: s.dists, nodes: trip });
    }

    // A track that no trip could be cut out of — a van that only jittered in a
    // car park, or a range too short for anything to be split off — still has
    // fixes worth drawing. An empty map under a header full of numbers reads as
    // a broken card, not as "you did not go anywhere".
    if (!geometry.length && this._track.nodes.length >= 2) {
      const coords = this._track.nodes.map((p) => ({ lat: p.lat, lon: p.lon }));
      const s = attachSamples(coords, this._track.nodes, cursor);
      cursor = s.end;
      geometry.push({
        coords,
        speeds: s.speeds,
        times: s.times,
        dists: s.dists,
        nodes: this._track.nodes,
      });
    }

    if (ac.signal.aborted) throw Object.assign(new Error("aborted"), { name: "AbortError" });
    this._geometry = geometry;
    this._snapReason = health.reason;
    if (!this._snap) this._snapState = "off";
    else if (!snapping) this._snapState = "pending";
    else if (!health.failed) this._snapState = "full";
    else if (health.ok) this._snapState = "partial";
    else this._snapState = "none";

    // The header reports the route as drawn: road kilometres where the fixes
    // were matched, straight-line kilometres only for the stretches that were
    // not — and the plain GPS total when there is no geometry at all.
    const stats = this._track.stats;
    if (stats) {
      stats.snapped = this._snapState === "full" || this._snapState === "partial";
      stats.dist = geometry.length ? cursor : stats.gpsDist || 0;
    }
  }

  /* --------------------------------------------------------------- drawing */

  _livePoint() {
    const cfg = this._config;
    const states = this._hass && this._hass.states;
    if (!states) return null;
    if (cfg.latitude && cfg.longitude) {
      const la = Number(states[cfg.latitude] && states[cfg.latitude].state);
      const lo = Number(states[cfg.longitude] && states[cfg.longitude].state);
      if (isFinite(la) && isFinite(lo) && (la || lo)) return { lat: la, lon: lo };
    }
    const st = cfg.entity ? states[cfg.entity] : null;
    if (st && st.attributes && st.attributes.latitude !== undefined) {
      return { lat: Number(st.attributes.latitude), lon: Number(st.attributes.longitude) };
    }
    return null;
  }

  _initial() {
    const cfg = this._config;
    const states = this._hass && this._hass.states;
    const st = states && cfg.entity ? states[cfg.entity] : null;
    const name = (st && st.attributes && st.attributes.friendly_name) || cfg.title || "";
    return name ? name.trim().charAt(0).toUpperCase() : "•";
  }

  _buildMarkers() {
    const out = [];
    const nodes = this._track.nodes;
    if (nodes.length) {
      const first = nodes[0];
      out.push({
        lat: first.lat,
        lon: first.lon,
        className: "start",
        html: "<div>А</div>",
        title: `Начало · ${fmtShortTime(first.t)}`,
      });
    }
    const stops = this._track.stops
      .filter((s) => s.dur >= 600)
      .sort((a, b) => b.dur - a.dur)
      .slice(0, 24);
    for (const s of stops) {
      out.push({
        lat: s.lat,
        lon: s.lon,
        className: "stop",
        html: `<div>${fmtDur(s.dur)}</div>`,
        title: `Стоянка ${fmtShortTime(s.t)} — ${fmtShortTime(s.tEnd)}`,
      });
    }
    const live = this._livePoint();
    if (live) {
      out.push({
        lat: live.lat,
        lon: live.lon,
        className: "finish",
        html: `<div>${escapeHtml(this._initial())}</div>`,
        title: "Сейчас",
      });
    }
    return out;
  }

  _fitAll(padding) {
    const all = [];
    for (const g of this._geometry) all.push(...g.coords);
    const live = this._livePoint();
    if (live) all.push(live);
    const b = boundsOf(all);
    if (b && (b.n !== b.s || b.e !== b.w)) {
      this._map.fitBounds(b, padding || (this._fs ? 56 : 34));
      // Fitting the live point alone does not count: the map is then parked on
      // the van at street level, and if nothing re-fits it, a route that
      // arrives later sits outside the screen and the card looks empty.
      this._fitted = this._geometry.length > 0;
    } else if (live) {
      this._map.setView(live, 16);
    }
  }

  _draw(fit) {
    if (!this._map) return;
    this._map.setDark(this._isDark());
    this._map.setRoute(this._geometry);
    this._liveKey = null;
    this._map.setMarkers(this._buildMarkers());
    if (this._tipEl) this._map.setTooltip(this._tipEl);
    if (fit || !this._fitted) this._fitAll();
    if (this._note) {
      this._note.style.display = this._track.nodes.length ? "" : "none";
      this._note.classList.remove("is-faded");
      clearTimeout(this._noteTimer);
      this._noteTimer = setTimeout(() => this._note.classList.add("is-faded"), 4000);
    }
  }

  _syncLive() {
    if (!this._map) return;
    const dark = this._isDark();
    if (this._map.o.dark !== dark) this._map.setDark(dark);
    const p = this._livePoint();
    if (!p) return;
    const key = `${p.lat.toFixed(6)},${p.lon.toFixed(6)}`;
    if (key === this._liveKey) return;
    this._liveKey = key;
    this._map.setMarkers(this._buildMarkers());
    if (this._tipEl) this._map.setTooltip(this._tipEl);
  }

  /* -------------------------------------------------------------- probing */

  _onTap(p) {
    if (!this._fs) {
      this._openFullscreen(p);
      return;
    }
    this._touched = true;
    this._probe(p);
  }

  _probe(p) {
    const nodes = this._track.nodes;
    if (!nodes.length) return this._hideTip();
    let best = null;
    let bestD = Infinity;
    for (const n of nodes) {
      const d = haversine(p, n);
      if (d < bestD) {
        bestD = d;
        best = n;
      }
    }
    if (!best || bestD > 60 * this._map.metersPerPixel()) return this._hideTip();
    this._showTip(best);
  }

  _showTip(node) {
    this._tipNode = node;
    if (!this._tipEl) {
      this._tipEl = document.createElement("div");
      this._tipEl.className = "vrc-tip";
      this._tipEl.innerHTML = `<div class="vrc-tip-probe"></div><div class="vrc-tip-inner"></div>`;
    }
    this._map.setTooltip(this._tipEl);
    this._tipEl.querySelector(".vrc-tip-inner").innerHTML = tipHtml(node);
    this._tipEl.style.display = "";
    this._positionTip();
  }

  _hideTip() {
    this._tipNode = null;
    if (this._tipEl) this._tipEl.style.display = "none";
  }

  _positionTip() {
    if (!this._tipEl || !this._tipNode) return;
    const p = this._map.project(this._tipNode.lat, this._tipNode.lon);
    this._tipEl.style.transform = `translate(${p.x.toFixed(1)}px, ${p.y.toFixed(1)}px)`;
    this._tipEl.classList.toggle("is-below", p.y < 110);
  }

  /* ----------------------------------------------------------- fullscreen */

  _controlsHtml(fullscreen) {
    const left = fullscreen
      ? `<div class="vrc-ctl at-left">` +
        `<button class="vrc-btn" data-act="zin" title="Приблизить">${icon("mdi:plus")}</button>` +
        `<button class="vrc-btn" data-act="zout" title="Отдалить">${icon("mdi:minus")}</button>` +
        `<button class="vrc-btn" data-act="fit" title="Весь маршрут">${icon("mdi:image-filter-center-focus")}</button>` +
        `</div>`
      : "";
    return (
      left +
      `<div class="vrc-ctl at-right">` +
      `<button class="vrc-btn ${this._snap ? "is-on" : ""}" data-act="snap" ` +
      `title="Прокладывать маршрут по дорогам (OSRM)">${icon("mdi:road-variant")}</button>` +
      (fullscreen
        ? `<button class="vrc-btn" data-act="close" title="Закрыть">${icon("mdi:close")}</button>`
        : `<button class="vrc-btn" data-act="full" title="На весь экран">${icon("mdi:fullscreen")}</button>`) +
      `</div>`
    );
  }

  _openFullscreen(tap) {
    if (this._fs) return;

    const host = document.createElement("div");
    host.style.cssText = "position:fixed;inset:0;z-index:9000;";
    const root = host.attachShadow({ mode: "open" });

    const style = document.createElement("style");
    style.textContent = STYLES;

    const wrap = document.createElement("div");
    wrap.className = "vrc-fs";

    const head = document.createElement("div");
    head.className = "vrc-head";
    head.addEventListener("click", (e) => this._onChromeClick(e));

    const stats = document.createElement("div");
    stats.className = "vrc-stats";

    const controls = document.createElement("div");
    controls.className = "vrc-ctls";
    controls.addEventListener("click", (e) => this._onChromeClick(e));

    const holder = document.createElement("div");
    holder.className = "vrc-fs-map";
    holder.append(this._map.el, this._attribution(), controls);

    const ranges = document.createElement("div");
    ranges.className = "vrc-ranges";
    ranges.addEventListener("click", (e) => this._onChromeClick(e));

    const legend = document.createElement("div");
    legend.className = "vrc-legend";

    wrap.append(head, stats, holder, ranges, legend);
    root.append(style, wrap);
    document.body.appendChild(host);

    this._fs = {
      host,
      root,
      chrome: { head, stats, ranges, legend, controls, fullscreen: true },
    };
    this._prevOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";

    this._map.setInteractive(true);
    this._touched = false;
    this._renderChrome();

    requestAnimationFrame(() => {
      if (!this._fs) return;
      this._map._measure();
      this._fitAll();
      if (tap) this._probe({ lat: tap.lat, lon: tap.lon });
    });

    this._onKey = (ev) => {
      if (ev.key === "Escape") {
        ev.stopPropagation();
        this._closeFullscreen();
      }
    };
    window.addEventListener("keydown", this._onKey, true);
  }

  _closeFullscreen() {
    if (!this._fs) return;
    const fs = this._fs;
    this._fs = null;
    window.removeEventListener("keydown", this._onKey, true);
    this._onKey = null;
    document.body.style.overflow = this._prevOverflow || "";
    this._hideTip();
    this._map.setInteractive(false);
    if (this._mapWrap) this._mapWrap.insertBefore(this._map.el, this._mapWrap.firstChild);
    fs.host.remove();
    this._touched = false;
    requestAnimationFrame(() => {
      this._map._measure();
      this._fitAll();
      this._renderChrome();
    });
  }
}

/* -------------------------------------------------------------- rendering */

function escapeHtml(text) {
  return String(text === undefined || text === null ? "" : text).replace(
    /[&<>"']/g,
    (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])
  );
}

/** What a tap on the route reports: speed, time, and where along the trip. */
function tipHtml(node) {
  if (node.kind === "stop") {
    const crossesMidnight = !sameDay(node.t, node.tEnd);
    return (
      `<div class="vrc-tip-h"><span class="vrc-dot" style="background:#94a3b8"></span>Стоянка · ${fmtDur(node.dur)}</div>` +
      `<div class="vrc-tip-r">с ${fmtShortTime(node.t)} до ${fmtShortTime(node.tEnd)}` +
      `${crossesMidnight ? ` (${fmtDate(node.tEnd)})` : ""}</div>` +
      `<div class="vrc-tip-r">${fmtDate(node.t)} · ${fmtDist(node.dist)} от начала</div>`
    );
  }
  const band = bandFor(node.v);
  const speed = node.v < 10 ? num(node.v, 1) : num(node.v, 0);
  const altitude = node.alt !== undefined && node.alt !== null ? ` · ${num(node.alt, 0)} м высоты` : "";
  return (
    `<div class="vrc-tip-h"><span class="vrc-dot" style="background:${band.color}"></span>${speed} км/ч</div>` +
    `<div class="vrc-tip-r">${fmtTime(node.t)} · ${fmtDate(node.t)}</div>` +
    `<div class="vrc-tip-r">${fmtDist(node.dist)} от начала${altitude}</div>`
  );
}

if (!customElements.get("crafter-route-card")) {
  customElements.define("crafter-route-card", CrafterRouteCard);
}

window.customCards = window.customCards || [];
if (!window.customCards.some((c) => c.type === "crafter-route-card")) {
  window.customCards.push({
    type: "crafter-route-card",
    name: "Маршрут фургона",
    description:
      "Пройденный маршрут линией по дорогам: скорость и время в любой точке, карта на весь экран.",
    preview: false,
  });
}

console.info(
  `%c CRAFTER-ROUTE-CARD %c ${CARD_VERSION} `,
  "color:#0f172a;background:#38bdf8;font-weight:600",
  "color:#38bdf8;background:#0f172a"
);
