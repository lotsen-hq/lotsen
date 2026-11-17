import { svelte } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import { svelteTesting } from '@testing-library/svelte/vite';
import { defineConfig } from 'vitest/config';
import { thirdPartyNotices } from './scripts/thirdPartyNoticesPlugin.mjs';

export default defineConfig({
   plugins: [tailwindcss(), svelte(), svelteTesting(), thirdPartyNotices()],
   resolve: {
      alias: { $lib: '/src/lib' },
   },
   build: {
      // The server embeds every file in dist and the .NET tests request app.js and app.css by name.
      // Hashed names buy nothing because the server revalidates every file.
      rolldownOptions: {
         output: {
            entryFileNames: 'app.js',
            chunkFileNames: (chunk) => `chunks/${chunkName(chunk) ?? '[name]'}.js`,
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
      // Vitest gives every other stylesheet as an empty string. The settings and color theme tests apply these two to read the palettes they select.
      css: { include: [/\/src\/(syntax-colors|color-themes)\.css/] },
      // With a worker for nearly every CPU, the jsdom workers starve one another and tests that wait on the UI time out.
      maxWorkers: '50%',
   },
});

/**
 * Returns the name of a lazily loaded chunk: the package of its entry module, such as lang-json, or the legacy mode, such as legacy-modes-shell.
 *
 * A chunk without an entry module, such as one that Rolldown makes for the modules that several languages share, takes the name of the language package that it holds, else of its first module.
 */
function chunkName(chunk: { readonly facadeModuleId: string | null; readonly moduleIds: readonly string[] }): string | null {
   const moduleId = chunk.facadeModuleId ?? chunk.moduleIds.find((id) => /node_modules\/@codemirror\/lang-/.test(id)) ?? chunk.moduleIds[0];
   // Every legacy mode is a module of the one package legacy-modes, so the mode's name keeps the chunks' names apart.
   const mode = moduleId && /node_modules\/@codemirror\/legacy-modes\/mode\/([^/]+)\.js$/.exec(moduleId);
   if (mode) {
      return `legacy-modes-${mode[1]!}`;
   }

   const match = moduleId && /node_modules\/(?:@[^/]+\/)?([^/]+)\//.exec(moduleId);
   return match ? match[1]! : null;
}
