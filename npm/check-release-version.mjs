// Fails unless plugin/.mcp.json starts the npm version of the release, such as lotsen@0.2.0 for v0.2.0, so that the plugin at the release's tag runs the executable that the release
// publishes to npm.
//
// node npm/check-release-version.mjs <tag>
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const tag = process.argv[2];
if (tag === undefined) {
   console.error("Usage: node npm/check-release-version.mjs <tag, such as v0.2.0>");
   process.exit(2);
}

const file = path.join(path.dirname(path.dirname(fileURLToPath(import.meta.url))), "plugin", ".mcp.json");
const args = JSON.parse(readFileSync(file, "utf8")).mcpServers?.lotsen?.args ?? [];
const spec = args.find((arg) => typeof arg === "string" && arg.startsWith("lotsen@"));
const expected = `lotsen@${tag.replace(/^v/, "")}`;
if (spec === undefined) {
   console.error(`plugin/.mcp.json starts no lotsen@<version>, and the release ${tag} publishes ${expected}. Commit ${expected} into its args, then start the release again.`);
   process.exit(1);
}

if (spec !== expected) {
   console.error(`plugin/.mcp.json starts ${spec}, and the release ${tag} publishes ${expected}. Commit ${expected} into plugin/.mcp.json, wait for its CI to pass, then start the release from that commit.`);
   process.exit(1);
}

console.log(`plugin/.mcp.json starts ${expected}, the version that the release ${tag} publishes.`);
