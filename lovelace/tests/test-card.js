const m = require("./harness.js");
const assert = require("assert");

// 20 minutes of driving east, one fix every 8 s, plus three minutes parked.
const fixes = [];
let t = 1.7e12;
for (let i = 0; i < 150; i++) fixes.push({ t: t + i * 8000, lat: 53.4, lon: -6.5 + i * 0.0002, v: 45, alt: 60 });
t = fixes[fixes.length - 1].t;
for (let i = 1; i <= 30; i++) fixes.push({ t: t + i * 10000, lat: 53.4, lon: -6.47, v: 0, alt: 60 });
t = fixes[fixes.length - 1].t;
for (let i = 1; i < 120; i++) fixes.push({ t: t + i * 8000, lat: 53.4 + i * 0.0002, lon: -6.47, v: 45, alt: 60 });

/** OSRM answer: the same trace, but along a road that wiggles 
 *  — a real road is always longer than the straight lines between fixes. */
function roadFor(url) {
  const pts = url.split("/match/v1/driving/")[1].split("?")[0].split(";")
    .map((p) => p.split(",").map(Number));
  const out = [];
  for (let i = 0; i < pts.length; i++) {
    const [lon, lat] = pts[i];
    out.push([lon, lat]);
    if (i < pts.length - 1) {
      const [lon2, lat2] = pts[i + 1];
      out.push([(lon + lon2) / 2, (lat + lat2) / 2 + 0.00012]);   // the bend the GPS cut
    }
  }
  return { ok: true, status: 200, json: async () => ({ code: "Ok", matchings: [{ geometry: { coordinates: out } }] }) };
}

function card(snap) {
  const c = Object.create(m.CrafterRouteCard.prototype);
  c._config = Object.assign({}, m.DEFAULTS, { entity: "device_tracker.van" });
  c._track = m.buildTrack(fixes, c._config);
  c._snap = snap;
  c._geometry = [];
  return c;
}

let pass = true;
const check = async (name, fn) => {
  try { await fn(); console.log("ok   -", name); }
  catch (e) { pass = false; console.log("FAIL -", name, "\n     ", e.message); }
};

(async () => {
  await check("snapped route reports road kilometres, not GPS chords", async () => {
    m.SNAP_CACHE.clear();
    global.fetch = async (url) => roadFor(url);
    const c = card(true);
    const gps = c._track.stats.gpsDist;
    await c._buildGeometry();
    const s = c._track.stats;
    assert.equal(c._snapState, "full", "reason: " + c._snapReason);
    assert.equal(s.snapped, true);
    assert.ok(s.dist > gps * 1.05, `road ${s.dist.toFixed(0)} m vs GPS ${gps.toFixed(0)} m`);
    assert.equal(s.gpsDist, gps, "the raw figure stays available");
    console.log(`       GPS ${(gps / 1000).toFixed(2)} км → по дорогам ${(s.dist / 1000).toFixed(2)} км`);
    const last = c._track.nodes[c._track.nodes.length - 1];
    assert.ok(Math.abs(last.dist - s.dist) < 60, "the last node sits at the end of the route");
  });

  await check("a server that refuses every request leaves the line on GPS, and says why", async () => {
    m.SNAP_CACHE.clear();
    global.fetch = async () => ({ ok: false, status: 400, json: async () => ({ code: "TooBig", message: "Too many trace coordinates" }) });
    const c = card(true);
    const gps = c._track.stats.gpsDist;
    await c._buildGeometry();
    assert.equal(c._snapState, "none");
    assert.equal(c._track.stats.snapped, false);
    assert.ok(/TooBig/.test(c._snapReason), "reason: " + c._snapReason);
    assert.ok(Math.abs(c._track.stats.dist - gps) < gps * 0.02, "falls back to the GPS figure");
    console.log(`       banner: дороги недоступны — линия по точкам GPS (${c._snapReason})`);
  });

  await check("one endpoint down, the next one carries the route", async () => {
    m.SNAP_CACHE.clear();
    global.fetch = async (url) => {
      if (url.startsWith("https://router.project-osrm.org")) throw new TypeError("Failed to fetch");
      return roadFor(url);
    };
    const c = card(true);
    await c._buildGeometry();
    assert.equal(c._snapState, "full", "state " + c._snapState + " reason " + c._snapReason);
    assert.equal(c._track.stats.snapped, true);
  });

  await check("snapping off: the card measures the line it draws", async () => {
    global.fetch = async () => { throw new Error("no requests expected"); };
    const c = card(false);
    await c._buildGeometry();
    assert.equal(c._snapState, "off");
    assert.equal(c._track.stats.snapped, false);
    assert.ok(Math.abs(c._track.stats.dist - c._track.stats.gpsDist) < c._track.stats.gpsDist * 0.02);
  });

  await check("stops still split the trip and stay on the line", async () => {
    m.SNAP_CACHE.clear();
    global.fetch = async (url) => roadFor(url);
    const c = card(true);
    await c._buildGeometry();
    assert.ok(c._track.stops.length >= 1, "the three-minute stop should be detected");
    assert.ok(c._geometry.length >= 1);
    for (const g of c._geometry) assert.equal(g.coords.length, g.speeds.length);
  });

  process.exitCode = pass ? 0 : 1;
})();
