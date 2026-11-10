#!/usr/bin/env node
// Runs the lotsen executable of this platform's package with this process's arguments and standard streams, and exits as it exits.
"use strict";

const { spawn } = require("node:child_process");
const path = require("node:path");

// The packages that pack.mjs builds, by `${process.platform}-${process.arch}`.
const platforms = {
   "linux-x64": { name: "@lotsen-hq/lotsen-linux-x64", executable: "lotsen" },
   "win32-x64": { name: "@lotsen-hq/lotsen-win32-x64", executable: "lotsen.exe" },
};

const key = `${process.platform}-${process.arch}`;
const platform = platforms[key];
if (platform === undefined) {
   console.error(`lotsen runs on Linux x64 with glibc and on Windows x64, and this system is ${key}.`);
   process.exit(1);
}

let executable;
try {
   executable = path.join(path.dirname(require.resolve(`${platform.name}/package.json`)), platform.executable);
} catch {
   console.error(`The package ${platform.name}, which holds lotsen for ${key}, is not installed. Install lotsen again, with its optional dependencies.`);
   process.exit(1);
}

// The terminal sends Ctrl-C to the executable as well, so this process only waits for the executable's exit.
process.on("SIGINT", () => {});
const child = spawn(executable, process.argv.slice(2), { stdio: "inherit" });
for (const signal of ["SIGTERM", "SIGHUP"]) {
   process.on(signal, () => child.kill(signal));
}

child.on("error", (error) => {
   console.error(`lotsen could not start ${executable}: ${error.message}`);
   process.exit(1);
});

child.on("exit", (code, signal) => {
   if (signal !== null) {
      // Ends this process by the same signal, so its parent sees how the executable ended.
      process.removeAllListeners(signal);
      process.kill(process.pid, signal);
      return;
   }

   process.exit(code ?? 1);
});
