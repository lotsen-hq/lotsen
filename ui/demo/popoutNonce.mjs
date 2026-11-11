// Gives the built demo's popout page a nonce, in Node only, for the Vite configuration that imports it.
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

/** Returns the plugin that writes the nonce into the built `popout.html`, which the build copies from public/ as it is. */
export function popoutNonce(nonce) {
   return {
      name: 'lotsen-demo-popout-nonce',
      writeBundle: async (options) => {
         const page = join(options.dir, 'popout.html');
         await writeFile(page, (await readFile(page, 'utf8')).replace('__LOTSEN_CSP_NONCE__', nonce));
      },
   };
}
