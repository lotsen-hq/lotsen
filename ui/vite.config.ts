import { svelte } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import { svelteTesting } from '@testing-library/svelte/vite';
import type { Plugin } from 'vite';
import { defineConfig } from 'vitest/config';
import { thirdPartyNotices } from './scripts/thirdPartyNoticesPlugin.mjs';

/**
 * Gives paneforge's drag cursor `<style>` element the page's style nonce, which the CSP requires of every `<style>` created at run time.
 *
 * The build fails when paneforge's module or the line that creates the element is missing, so an upgrade cannot drop the nonce unnoticed.
 */
function paneforgeCursorNonce(): Plugin {
   const module = '/node_modules/paneforge/dist/internal/utils/style.js';
   const creation = 'element = doc.createElement("style");';
   let patched = false;
   return {
      name: 'lotsen-paneforge-cursor-nonce',
      apply: 'build',
      transform(code, id) {
         if (!id.endsWith(module)) {
            return null;
         }

         if (!code.includes(creation)) {
            this.error(`${module} no longer contains ${creation}, so its cursor style would lack the CSP nonce.`);
         }

         patched = true;
         const withNonce = `${creation} const nonce = cspNonce(doc); if (nonce) element.nonce = nonce;`;
         return `import { cspNonce } from '/src/lib/cspNonce.ts';\n${code.replace(creation, withNonce)}`;
      },
      buildEnd(error) {
         if (!error && !patched) {
            this.error(`The build did not include ${module}, so paneforge's cursor style was not given the CSP nonce.`);
         }
      },
   };
}

export default defineConfig({
   plugins: [tailwindcss(), svelte(), svelteTesting(), paneforgeCursorNonce(), thirdPartyNotices()],
   resolve: {
      alias: { $lib: '/src/lib' },
   },
   build: {
      // The server embeds every file in dist and the .NET tests request app.js and app.css by name.
      // Hashed names buy nothing because the server revalidates every file.
      rolldownOptions: {
         output: {
            entryFileNames: 'app.js',
            // The lazily loaded language chunks are named after the package they mostly hold, such as lang-json.
            chunkFileNames: (chunk) => `chunks/${packageName(chunk.facadeModuleId ?? chunk.moduleIds[0]) ?? '[name]'}.js`,
            assetFileNames: (asset) => (asset.names.some((name) => name.endsWith('.css')) ? 'app.css' : '[name][extname]'),
         },
      },
      modulePreload: { polyfill: false },
      assetsInlineLimit: 0,
      // The server embeds dist and serves it on the local machine, so the size of app.js costs no download.
      chunkSizeWarningLimit: 1500,
   },
   // The test projects of vitest.config.ts extend this configuration, so each runs the plugins that the build runs.
   test: {
      environment: 'jsdom',
      setupFiles: ['tests/setup.ts'],
      // Vitest gives every other stylesheet as an empty string. The settings tests apply this one to read the palettes it selects.
      css: { include: [/\/src\/syntax-colors\.css/] },
      // With a worker for nearly every CPU, the jsdom workers starve one another and tests that wait on the UI time out.
      maxWorkers: '50%',
   },
});

function packageName(moduleId: string | undefined): string | null {
   const match = moduleId && /node_modules\/(?:@[^/]+\/)?([^/]+)\//.exec(moduleId);
   return match ? match[1]! : null;
}
