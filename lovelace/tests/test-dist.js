const m = require("./harness.js");
const assert = require("assert");

// A 90° bend, as the van drove it. The recorder caught six fixes; the road is
// the same arc, sampled finely — which is what OSRM hands back.
const R = 0.004;               // ~440 m in latitude terms
const arc = (n) => {
  const out = [];
  for (let i = 0; i <= n; i++) {
    const a = (Math.PI / 2) * (i / n);
    out.push({ lat: 53.4 + R * Math.sin(a), lon: -6.44 + R * (1 - Math.cos(a)) });
  }
  return out;
};

const nodes = arc(6).map((p, i) => Object.assign(p, { t: 1.7e12 + i * 20000, v: 40, kind: "move" }));
const road = arc(120);

const chords = (list) => list.reduce((sum, p, i) => (i ? sum + m.haversine(list[i - 1], p) : 0), 0);

let pass = true;
const check = (name, fn) => {
  try { fn(); console.log("ok   -", name); }
  catch (e) { pass = false; console.log("FAIL -", name, "\n     ", e.message); }
};

check("road kilometres exceed the straight lines between fixes", () => {
  const gps = chords(nodes);
  const s = m.attachSamples(road, nodes, 0);
  assert.ok(s.end > gps, `road ${s.end.toFixed(1)} m should exceed GPS ${gps.toFixed(1)} m`);
  const arcLen = chords(road);
  assert.ok(Math.abs(s.end - arcLen) < 0.5, "measurement should equal the drawn line");
  console.log(`       GPS chords ${gps.toFixed(0)} m → road ${s.end.toFixed(0)} m (+${(100 * (s.end / gps - 1)).toFixed(1)}%)`);
});

check("distance along the route never runs backwards", () => {
  const s = m.attachSamples(road, nodes, 0);
  for (let i = 1; i < s.dists.length; i++) assert.ok(s.dists[i] >= s.dists[i - 1]);
  for (let i = 1; i < nodes.length; i++) assert.ok(nodes[i].dist >= nodes[i - 1].dist);
  assert.equal(nodes[0].dist, 0);
  assert.ok(Math.abs(nodes[nodes.length - 1].dist - s.end) < 20, "last node sits at the end of the line");
});

check("the running total carries from one trip to the next", () => {
  const s = m.attachSamples(road, nodes, 1234);
  assert.equal(s.dists[0], 1234);
  assert.ok(Math.abs(s.end - 1234 - chords(road)) < 0.5);
  assert.ok(nodes[0].dist >= 1234);
});

check("an unsnapped line measures exactly what it draws", () => {
  const raw = nodes.map((p) => ({ lat: p.lat, lon: p.lon }));
  const s = m.attachSamples(raw, nodes, 0);
  assert.ok(Math.abs(s.end - chords(nodes)) < 0.01);
});

check("empty input is harmless", () => {
  assert.equal(m.attachSamples([], nodes, 500).end, 500);
  assert.equal(m.attachSamples(road, [], 7).end, 7);
});

process.exitCode = pass ? 0 : 1;
