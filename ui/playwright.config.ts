import { defineConfig, devices } from '@playwright/test';
import { suitePort } from './e2e/port.mjs';

// A free port unless LOTSEN_E2E_PORT pins one, so runs from two checkouts at once don't collide, and the suite never reuses a demo that a developer runs on Vite's default port.
const port = await suitePort();

// The layout suite runs in a real browser against the demo, whose Vite plugin starts the test host and stops it when Vite stops. See ui/README.md.
export default defineConfig({
   testDir: 'e2e',
   forbidOnly: true,
   reporter: 'list',
   timeout: 60_000,
   use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure', ...devices['Desktop Chrome'], viewport: { width: 1600, height: 1000 } },
   projects: [{ name: 'chromium' }],
   webServer: {
      command: `npm run demo -- --port ${port} --strictPort`,
      url: `http://localhost:${port}/`,
      reuseExistingServer: false,
      // The demo builds and starts the test host before Vite answers, unless LOTSEN_TEST_HOST names a built one.
      timeout: 180_000,
      // The test output shows the host's build and the demo's own messages, so a start that fails shows its cause.
      stdout: 'pipe',
      stderr: 'pipe',
   },
});
