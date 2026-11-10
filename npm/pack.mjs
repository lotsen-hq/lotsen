// Packs lotsen's npm packages at one version: a package for each platform whose executable it gets, then the package lotsen, whose starter runs them.
//
// node npm/pack.mjs <version> <out directory> [linux-x64=<executable>] [win32-x64=<executable>]
//
// It prints the path of each tarball, one per line, in the order to publish them: the platforms first, so the package lotsen never names a version that npm lacks.
import { execFileSync } from "node:child_process";
import { chmodSync, copyFileSync, cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.dirname(here);

/** The platform packages by the key that the starter, bin/lotsen.js, looks them up by, with the directory of each one's template and its executable's name. */
export const platforms = {
   "linux-x64": { directory: "lotsen-linux-x64", executable: "lotsen" },
   "win32-x64": { directory: "lotsen-win32-x64", executable: "lotsen.exe" },
};

/**
 * Packs the package of each platform in `executables`, then the package lotsen, all at `version`.
 *
 * @param {string} version The npm version, such as 0.2.0 or 0.2.0-rc.1.
 * @param {string} out The directory that receives the tarballs. Its subdirectory `stage` holds the packages before they are packed.
 * @param {Record<string, string>} executables The path of the executable for each platform key to pack.
 * @returns {string[]} The paths of the tarballs, in the order to publish them.
 */
export function pack(version, out, executables) {
   const stage = path.join(out, "stage");
   rmSync(stage, { recursive: true, force: true });
   const tarballs = [];
   for (const [key, executable] of Object.entries(executables)) {
      const platform = platforms[key];
      if (platform === undefined) {
         throw new Error(`No package holds the platform ${key}. The platforms are ${Object.keys(platforms).join(", ")}.`);
      }

      const directory = stageTemplate(platform.directory, stage, version);
      // A downloaded artifact loses its mode, and npm pack keeps the mode it finds in the tarball.
      const target = path.join(directory, platform.executable);
      copyFileSync(executable, target);
      chmodSync(target, 0o755);
      tarballs.push(packDirectory(directory, out));
   }

   const main = stageTemplate("lotsen", stage, version);
   tarballs.push(packDirectory(main, out));
   return tarballs;
}

// Copies the template into the stage with the version, which the package lotsen also gives each of its platform packages, and the license and the copyright notice beside it.
function stageTemplate(name, stage, version) {
   const directory = path.join(stage, name);
   cpSync(path.join(here, name), directory, { recursive: true });
   for (const file of ["LICENSE", "NOTICE"]) {
      copyFileSync(path.join(root, file), path.join(directory, file));
   }

   const manifestPath = path.join(directory, "package.json");
   const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
   manifest.version = version;
   for (const dependency of Object.keys(manifest.optionalDependencies ?? {})) {
      manifest.optionalDependencies[dependency] = version;
   }

   writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
   return directory;
}

function packDirectory(directory, out) {
   mkdirSync(out, { recursive: true });
   const packed = JSON.parse(npm(["pack", "--json", "--pack-destination", path.resolve(out)], directory));
   return path.join(path.resolve(out), packed[0].filename);
}

/** Runs npm with the arguments in the directory and returns its standard output. */
export function npm(args, cwd) {
   // npm is a batch file on Windows, which only a shell runs.
   return execFileSync("npm", args, { cwd, encoding: "utf8", shell: process.platform === "win32", stdio: ["ignore", "pipe", "inherit"] });
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
   const [version, out, ...pairs] = process.argv.slice(2);
   if (version === undefined || out === undefined) {
      console.error("Usage: node npm/pack.mjs <version> <out directory> [linux-x64=<executable>] [win32-x64=<executable>]");
      process.exit(2);
   }

   const executables = Object.fromEntries(pairs.map((pair) => [pair.slice(0, pair.indexOf("=")), pair.slice(pair.indexOf("=") + 1)]));
   for (const tarball of pack(version, out, executables)) {
      console.log(tarball);
   }
}
