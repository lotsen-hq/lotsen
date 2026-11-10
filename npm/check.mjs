// Checks lotsen's npm packages against a local publish: packs them for this platform at the publish's version, installs them into an empty directory, and runs lotsen through
// the starter. It passes when the starter prints the publish's own --version and passes on the exit code of a command that the executable refuses.
//
// node npm/check.mjs <published executable>
import { execFileSync, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { npm, pack, platforms } from "./pack.mjs";

// The exit code of a parse error, which the CLI writes to standard error.
const ParseErrorExitCode = 2;

const executable = process.argv[2];
if (executable === undefined) {
   console.error("Usage: node npm/check.mjs <published executable>");
   process.exit(2);
}

const key = `${process.platform}-${process.arch}`;
if (platforms[key] === undefined) {
   console.error(`No package holds the platform ${key}, so there is nothing to check here.`);
   process.exit(1);
}

const expected = execFileSync(executable, ["--version"], { encoding: "utf8" }).trim();
// --version prints the informational version, such as 0.2.0+e23502b…, and a digest, and the npm version is the part before the + or the space.
const version = expected.split(/[+ ]/)[0];
const directory = mkdtempSync(path.join(os.tmpdir(), "lotsen-npm-check-"));
try {
   const tarballs = pack(version, path.join(directory, "packs"), { [key]: executable });
   const install = path.join(directory, "install");
   mkdirSync(install);
   writeFileSync(path.join(install, "package.json"), '{ "private": true }\n');
   npm(["install", "--no-audit", "--no-fund", ...tarballs], install);

   const printed = npm(["exec", "--offline", "--", "lotsen", "--version"], install).trim();
   if (printed !== expected) {
      fail(`The starter printed '${printed}', and the publish prints '${expected}'.`);
   }

   const refused = spawnSync(process.execPath, [path.join(install, "node_modules", "lotsen", "bin", "lotsen.js"), "--no-such-option"], { stdio: "ignore" });
   if (refused.status !== ParseErrorExitCode) {
      fail(`The starter exited with ${refused.status ?? refused.signal} for an unknown option, and the executable exits with ${ParseErrorExitCode}.`);
   }

   console.log(`The npm packages of ${version} run ${expected} through the starter.`);
} finally {
   rmSync(directory, { recursive: true, force: true });
}

function fail(message) {
   throw new Error(message);
}
