import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import license from 'rollup-plugin-license';
import { formatNotices, isAllowed } from './thirdPartyNotices.mjs';

const uiRoot = dirname(dirname(fileURLToPath(import.meta.url)));
const read = (relativePath) => readFileSync(join(uiRoot, relativePath), 'utf8');

/**
 * Returns the Vite plugin that writes the third-party notices into dist, which the server embeds and serves, for the packages that the bundle really holds.
 *
 * `third-party/notices.txt` holds what the build cannot find: the NuGet packages, which a test checks against the restore, the .NET runtime and copied components.
 *
 * @returns {import('vite').Plugin} The plugin. It runs only in a build, and fails the build on a license that {@link isAllowed} refuses.
 */
export function thirdPartyNotices() {
   const plugin = license({
      // The plugin loads package.json from this directory, and the demo's configuration runs from another one.
      cwd: uiRoot,
      thirdParty: {
         multipleVersions: true,
         allow: { test: isAllowed, failOnUnlicensed: true, failOnViolation: true },
         output: {
            file: join(uiRoot, 'dist', 'THIRD-PARTY-NOTICES.txt'),
            template: (dependencies) =>
               formatNotices(dependencies, [
                  `VS Code's Seti file icon theme and default theme colors\n\n${read('src/seti/LICENSE.txt')}`,
                  read('src/seti/ThirdPartyNotices.txt'),
                  read('third-party/notices.txt'),
               ]),
         },
      },
   });
   return { ...plugin, apply: 'build' };
}
