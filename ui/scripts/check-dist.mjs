// Fails the build when dist would not run under the server's content security policy or not fit how it is embedded.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findDistProblems } from './distRules.mjs';

const dist = process.argv[2] ?? fileURLToPath(new URL('../dist/', import.meta.url));
const files = readdirSync(dist, { recursive: true })
   .map((path) => path.replaceAll('\\', '/'))
   .filter((path) => statSync(join(dist, path)).isFile())
   .sort();
const problems = findDistProblems(files, (path) => readFileSync(join(dist, path), 'utf8'));

if (problems.length > 0) {
   console.error(problems.map((problem) => `check-dist: ${problem}`).join('\n'));
   process.exit(1);
}

console.log(`check-dist: ${files.join(', ')} fit the server's policy.`);
