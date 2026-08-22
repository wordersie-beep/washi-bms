/**
 * The card has to stay usable while it is asking a server about roads: matching
 * a day of driving is dozens of sequential requests, and for that whole time
 * the range buttons, the map and the snap toggle belong to the person looking
 * at the card, not to the server.
 */
const m = require("./harness.js");
const assert = require("assert");

const fixes = [];
let t = 1.7e12;
for (let i = 0; i < 400; i++) fixes.push({ t: t + i * 8000, lat: 53.4, lon: -6.5 + i * 0.0002, v: 45, alt: 60 });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function roadFor(url) {
  const pts = url.split("/match/v1/driving/")[1].split("?")[0].split(";")
    .map((p) => p.split(",").map(Number));
  return { ok: true, status: 200, json: async () => ({ code: "Ok", matchings: [{ geometry: { coordinates: pts } }] }) };
}

/** A card with the DOM side stubbed out; draws are counted, not rendered. */
function card(latency, answer) {
  const c = Object.create(m.CrafterRouteCard.prototype);
  c._config = Object.assign({}, m.DEFAULTS, { entity: "device_tracker.van" });
  c._track = { nodes: [], trips: [], stops: [], stats: {} };
  c._geometry = [];
  c._snap = true;
  c._snapState = "off";
  c._snapping = false;
  c._loading = false;
  c._gen = 0;
  c._hass = {};
  c.draws = [];
  c.fetches = 0;
  c._draw = () => c.draws.push({ gen: c._gen, state: c._snapState });
  c._renderChrome = () => {};
  c._fetch = async () => {
    c.fetches++;
    return fixes;
  };
  global.fetch = (url, init) =>
    new Promise((res, rej) => {
      const timer = setTimeout(() => res((answer || roadFor)(url)), latency);
      init.signal.addEventListener("abort", () => {
        clearTimeout(timer);
        const err = new Error("aborted");
        err.name = "AbortError";
        rej(err);
      });
    });
  return c;
}

let pass = true;
const check = async (name, fn) => {
  try { await fn(); console.log("ok   -", name); }
  catch (e) { pass = false; console.log("FAIL -", name, "\n     ", e.message); }
};

(async () => {
  await check("the route is drawn before the roads are asked for", async () => {
    m.SNAP_CACHE.clear();
    const c = card(40);
    const run = c._reload();
    await sleep(20);
    assert.equal(c._loading, false, "the fetch is done, so nothing is 'loading'");
    assert.equal(c._snapping, true, "matching is under way");
    assert.equal(c.draws.length, 1, "the GPS line is already on screen");
    assert.equal(c.draws[0].state, "pending");
    await run;
    assert.equal(c._snapState, "full", "and the roads land in a second draw");
    assert.equal(c.draws.length, 2);
  });

  await check("pressing another range mid-match cancels it and wins", async () => {
    m.SNAP_CACHE.clear();
    const c = card(40);
    const first = c._reload();
    await sleep(20);
    assert.equal(c._snapping, true);
    const second = c._reload();          // the range button, pressed mid-match
    assert.equal(c.fetches, 2, "the press must actually reload, not be swallowed");
    await Promise.all([first, second]);
    assert.equal(c._gen, 2);
    const stale = c.draws.filter((d) => d.gen === 1 && d.state !== "pending");
    assert.equal(stale.length, 0, "a cancelled pass must not draw over the new one");
    assert.equal(c._snapState, "full");
    assert.equal(c._snapping, false, "and the card is idle again afterwards");
  });

  await check("toggling the roads button off is instant", async () => {
    m.SNAP_CACHE.clear();
    const c = card(40);
    await c._reload();
    c.draws.length = 0;
    c._snap = false;
    const started = Date.now();
    await c._rebuild();
    assert.ok(Date.now() - started < 30, "no request may stand between the tap and the redraw");
    assert.equal(c._snapState, "off");
    assert.equal(c.draws.length, 1);
  });

  await check("a server that refuses everything is dropped, not asked per chunk", async () => {
    m.SNAP_CACHE.clear();
    let calls = 0;
    const c = card(0, () => {
      calls++;
      return { ok: false, status: 400, json: async () => ({ code: "InvalidOptions" }) };
    });
    c._config = Object.assign({}, c._config, { osrm_url: "https://only.example", osrm_fallback: false });
    await c._reload();
    assert.equal(c._snapState, "none");
    assert.ok(/InvalidOptions/.test(c._snapReason), "reason: " + c._snapReason);
    assert.ok(
      calls <= m.SNAP_ATTEMPTS.length * 2,
      `a flat refusal must cost a couple of requests, not one per chunk: ${calls}`
    );
    console.log(`       ${calls} requests before matching was given up on`);
  });

  await check("history that fails to load does not leave the card stuck", async () => {
    m.SNAP_CACHE.clear();
    const c = card(0);
    c._fetch = async () => {
      c.fetches++;
      throw new Error("history/history_during_period failed");
    };
    await c._reload();
    assert.equal(c._loading, false);
    assert.ok(/history/.test(c._error));
    c._fetch = async () => fixes;
    await c._reload();
    assert.equal(c._error, null, "and a later reload clears it");
  });

  await check("a map parked on the van is re-fitted once a route exists", async () => {
    m.SNAP_CACHE.clear();
    const c = card(10);
    const calls = { fit: 0, view: 0 };
    c._map = {
      o: {},
      setDark() {}, setRoute() {}, setMarkers() {}, setTooltip() {},
      fitBounds() { calls.fit++; },
      setView() { calls.view++; },
      metersPerPixel: () => 1,
    };
    c._hass = { states: { "device_tracker.van": { attributes: { latitude: 53.4, longitude: -6.44 } } } };
    delete c._draw;                      // the real one, this time

    // Opening the map before any history has arrived parks it on the van.
    c._fitAll();
    assert.equal(calls.view, 1, "nothing to fit yet, so it sits on the van");
    assert.equal(c._fitted, undefined || c._fitted, "and that does not count as fitted");
    assert.ok(!c._fitted);

    // The route arrives, and the person has since touched the map — the redraw
    // asks for no fit at all. It must still fit this first real route.
    await c._reload();
    assert.ok(c._geometry.length > 0);
    c._touched = true;
    c._draw(false);
    assert.ok(calls.fit >= 1, "the first real route must be brought on screen");
    assert.equal(c._fitted, true);

    // From then on, a redraw leaves the view where the person put it.
    const before = calls.fit;
    c._draw(false);
    assert.equal(calls.fit, before, "later redraws must not steal the view back");
  });

  process.exitCode = pass ? 0 : 1;
})();
