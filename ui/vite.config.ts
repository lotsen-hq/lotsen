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
