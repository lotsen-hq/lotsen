// Prints the release notes of a version: the lines of its section in CHANGELOG.md, from the heading "## <tag>" to the next "## " heading. Fails when the section is missing or empty, so a
// stable release always publishes the notes that the changelog holds.
//
// node npm/release-notes.mjs <tag>
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const tag = process.argv[2];
if (tag === undefined) {
   console.error("Usage: node npm/release-notes.mjs <tag, such as v0.2.0>");
   process.exit(2);
}

const file = path.join(path.dirname(path.dirname(fileURLToPath(import.meta.url))), "CHANGELOG.md");
const lines = readFileSync(file, "utf8").split(/\r?\n/);
// A heading may carry a date after the tag, as in "## v0.1.0 - 2026-11-10". The space keeps "## v0.1.0-rc.1" from matching v0.1.0.
const start = lines.findIndex((line) => line === `## ${tag}` || line.startsWith(`## ${tag} `));
if (start === -1) {
   console.error(`CHANGELOG.md has no section "## ${tag}". Move the entries under "## Unreleased" into it, wait for the commit's CI to pass, then start the release from that commit.`);
   process.exit(1);
}

const next = lines.findIndex((line, index) => index > start && line.startsWith("## "));
const notes = lines
   .slice(start + 1, next === -1 ? undefined : next)
   .join("\n")
   .trim();
if (notes === "") {
   console.error(`The section "## ${tag}" in CHANGELOG.md is empty. Write the release's notes in it, wait for the commit's CI to pass, then start the release from that commit.`);
   process.exit(1);
}

console.log(notes);
