const m = require("./harness.js");
const assert = require("assert");

const AC = () => new AbortController();
const health = (opts = {}) => Object.assign(
  { endpoints: ["https://a.example", "https://b.example"], at: 0, strikes: 0, ok: 0, failed: 0, refusals: 0, reason: "" },
  opts
);

// A straight synthetic drive: 200 fixes, ~10 m apart, heading east.
const track = [];
for (let i = 0; i < 200; i++) {
  track.push({ lat: 53.4, lon: -6.44 + i * 0.00015, t: 1.7e12 + i * 4000, v: 50 });
}

const json = (body, status = 200) => ({
  ok: status >= 200 && status < 300,
  status,
  json: async () => body,
});

/** OSRM-shaped success: a line offset a little north, as a road would be. */
function matchOk(url) {
  const coords = url
    .split("/match/v1/driving/")[1]
    .split("?")[0]
    .split(";")
    .map((pair) => pair.split(",").map(Number))
    .map(([lon, lat]) => [lon, lat + 0.0001]);
  return json({ code: "Ok", matchings: [{ geometry: { type: "LineString", coordinates: coords }, distance: 1 }] });
}

const calls = [];
function install(handler) {
  calls.length = 0;
  m.SNAP_CACHE.clear();
  global.fetch = async (url, init) => {
    calls.push(url);
    return handler(url, init);
  };
}

async function run(name, fn) {
  try {
    await fn();
    console.log("ok   -", name);
  } catch (err) {
    console.log("FAIL -", name, "\n     ", err.message);
    process.exitCode = 1;
  }
}

(async () => {
  await run("happy path: every chunk matched, no fallback", async () => {
    install((url) => matchOk(url));
    const h = health();
    const out = await m.snapRun(track, m.DEFAULTS, AC().signal, h);
    assert.equal(h.failed, 0, "no chunk should fall back");
    assert.ok(h.ok >= 4, "expected several matched chunks, got " + h.ok);
    assert.ok(out.length > 100);
    assert.ok(Math.abs(out[0].lat - 53.4001) < 1e-6, "geometry must be the matched line");
  });

  await run("400 on the first parameter set falls through to a plainer one", async () => {
    install((url) => (url.includes("radiuses=25") ? json({ code: "TooBig", message: "Radius too large" }, 400) : matchOk(url)));
    const h = health();
    const out = await m.snapRun(track, m.DEFAULTS, AC().signal, h);
    assert.equal(h.failed, 0, "a 400 must not cost the stretch its roads");
    assert.ok(h.ok >= 4);
    assert.equal(h.at, 0, "a 400 must not retire the endpoint");
    assert.ok(/TooBig/.test(h.reason), "reason should name the server's own code: " + h.reason);
    assert.ok(out.length > 100);
  });

  await run("400 for long traces only: the chunk is halved until it fits", async () => {
    install((url) => {
      const n = url.split("/match/v1/driving/")[1].split("?")[0].split(";").length;
      return n > 20 ? json({ code: "TooBig" }, 400) : matchOk(url);
    });
    const h = health();
    const out = await m.snapRun(track, m.DEFAULTS, AC().signal, h);
    assert.equal(h.failed, 0, "halving should rescue every chunk, missed=" + h.failed);
    assert.ok(out.length > 100);
  });

  await run("unmatchable trace falls back to raw GPS but keeps trying", async () => {
    install(() => json({ code: "NoSegment", message: "Could not find a matching segment" }, 200));
    const h = health();
    const out = await m.snapRun(track, m.DEFAULTS, AC().signal, h);
    assert.equal(h.ok, 0);
    assert.ok(h.failed > 0);
    assert.equal(out.length, track.length, "raw fallback keeps every fix");
    assert.equal(h.at, 0, "NoSegment is not the endpoint's fault");
  });

  await run("dead endpoint is abandoned for the next one", async () => {
    install((url) => {
      if (url.startsWith("https://a.example")) throw new TypeError("Failed to fetch");
      return matchOk(url);
    });
    const h = health();
    const out = await m.snapRun(track, m.DEFAULTS, AC().signal, h);
    assert.equal(h.at, 1, "should have moved to the second endpoint");
    assert.equal(h.failed, 0, "the fallback endpoint should carry the route");
    assert.ok(calls.filter((u) => u.startsWith("https://a.example")).length <= 2, "no hammering a dead server");
    assert.ok(out.length > 100);
  });

  await run("all endpoints dead: raw line, no infinite loop", async () => {
    install(() => { throw new TypeError("Failed to fetch"); });
    const h = health();
    const out = await m.snapRun(track, m.DEFAULTS, AC().signal, h);
    assert.equal(out.length, track.length);
    assert.ok(h.failed > 0);
    assert.ok(calls.length <= 4, "at most two strikes per endpoint, got " + calls.length);
  });

  await run("matched geometry is cached across redraws", async () => {
    install((url) => matchOk(url));
    const h1 = health();
    await m.snapRun(track, m.DEFAULTS, AC().signal, h1);
    const first = calls.length;
    const h2 = health();
    await m.snapRun(track, m.DEFAULTS, AC().signal, h2);
    assert.equal(calls.length, first, "a redraw must not re-ask the server");
    assert.equal(h2.failed, 0);
    assert.ok(h2.ok >= 4);
  });

  await run("HTTP 429 is the server's problem, not the request's", async () => {
    install((url) => (url.startsWith("https://a.example") ? json({ code: "TooManyRequests" }, 429) : matchOk(url)));
    const h = health();
    await m.snapRun(track, m.DEFAULTS, AC().signal, h);
    assert.equal(h.at, 1, "429 should retire the endpoint, not degrade the request");
    assert.equal(h.failed, 0);
  });
  await run("a server refusing every request hands the route to the next one", async () => {
    install((url) =>
      url.startsWith("https://a.example")
        ? json({ code: "InvalidOptions", message: "Query string malformed" }, 400)
        : matchOk(url)
    );
    const h = health();
    const out = await m.snapRun(track, m.DEFAULTS, AC().signal, h);
    assert.equal(h.at, 1, "should move on to the endpoint that accepts the request");
    assert.equal(h.failed, 0, "the route must still end up on the roads");
    assert.ok(h.ok >= 4);
    const wasted = calls.filter((u) => u.startsWith("https://a.example")).length;
    assert.ok(wasted <= m.SNAP_ATTEMPTS.length, "no halving a flat refusal: " + wasted + " requests");
    assert.ok(out.length > 100);
  });
})();
