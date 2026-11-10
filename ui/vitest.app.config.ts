import { defineProject, mergeConfig } from 'vitest/config';
import viteConfig from './vite.config.ts';

// The flow tests, each on a world of its own on the run's one test host, which the .NET SDK builds unless LOTSEN_TEST_HOST names a built one.
export default mergeConfig(
   viteConfig,
   defineProject({
      test: {
         name: 'app',
         include: ['tests/app/**/*.test.ts'],
         globalSetup: ['tests/app/globalSetup.ts'],
         setupFiles: ['tests/app/setup.ts'],
         // Long enough for a test with several waits at tests/app/waitTimeout.mjs to fail on the wait that did not end, which names what it waited for, and not on this timeout.
         testTimeout: 60_000,
         hookTimeout: 60_000,
      },
   }),
);
