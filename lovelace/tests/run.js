/** Every test file, one process, one exit code: `node lovelace/tests/run.js`. */
const { execFileSync } = require("child_process");
const path = require("path");

for (const file of ["test-snap.js", "test-dist.js", "test-card.js"]) {
  console.log(`\n— ${file}`);
  execFileSync(process.execPath, [path.join(__dirname, file)], { stdio: "inherit" });
}
console.log("\nall tests passed");
