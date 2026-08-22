/**
 * Loads the card outside a browser.
 *
 * The card is a plain script meant for a <script type="module"> tag: it defines
 * no exports and touches the DOM as soon as it is evaluated. So the harness
 * stubs the handful of globals it reaches for, appends an export list to a copy
 * of the source, and hands the internals back for testing.
 */
const fs = require("fs");
const os = require("os");
const path = require("path");

global.HTMLElement = class {
  attachShadow() {
    return {};
  }
};
global.customElements = { get: () => true, define() {} };
global.window = { customCards: [], addEventListener() {}, removeEventListener() {} };
global.document = { createElement: () => ({ style: {}, classList: { add() {} } }) };

const src = fs.readFileSync(path.join(__dirname, "..", "crafter-route-card.js"), "utf8");
const exports_ = `
module.exports = {
  DEFAULTS, SNAP_ATTEMPTS, SNAP_CHUNK, SNAP_CACHE,
  osrmEndpoints, osrmUrl, osrmRequest, snapChunk, snapRun,
  attachSamples, haversine, buildTrack, thinForSnap, movingRuns, CrafterRouteCard,
};
`;
const tmp = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "crafter-card-")), "card.js");
fs.writeFileSync(tmp, src + exports_);

module.exports = require(tmp);
