import { defineConfig, devices } from '@playwright/test';
import { suitePort } from './e2e/port.mjs';

// A free port unless LOTSEN_E2E_PORT pins one, so runs from two checkouts at once don't collide, and the suite never reuses a demo that a developer runs on Vite's default port.
const port = await suitePort();
const cspPort = await suitePort('LOTSEN_E2E_CSP_PORT');

// The layout suite runs in a real browser against the demo, whose Vite plugin starts the test host and stops it when Vite stops. See ui/README.md.
// The csp project runs against the built demo, which Vite's preview serves with the server's Content-Security-Policy.
const demoServer = {
   reuseExistingServer: false,
   // The demo builds and starts the test host before Vite answers, unless LOTSEN_TEST_HOST names a built one.
   timeout: 180_000,
   // The test output shows the host's build and the demo's own messages, so a start that fails shows its cause.
   stdout: 'pipe',
   stderr: 'pipe',
} as const;

export default defineConfig({
   testDir: 'e2e',
   forbidOnly: true,
   reporter: 'list',
   timeout: 60_000,
   use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure', ...devices['Desktop Chrome'], viewport: { width: 1600, height: 1000 } },
   projects: [
      { name: 'chromium', testIgnore: 'csp.spec.ts' },
      { name: 'csp', testMatch: 'csp.spec.ts', use: { baseURL: `http://localhost:${cspPort}` } },
   ],
   // Playwright starts the servers one after another (the task loop in playwright 1.64.1's lib/runner/index.js), so the second finds the test host that the first one built.
   webServer: [
      { ...demoServer, command: `npm run demo -- --port ${port} --strictPort`, url: `http://localhost:${port}/` },
      { ...demoServer, command: `npm run demo:csp -- --port ${cspPort} --strictPort`, url: `http://localhost:${cspPort}/` },
   ],
});
