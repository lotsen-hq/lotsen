// The layout suite's port, in Node, which can probe for a free one.
import { createServer } from 'node:net';

/** Returns a port that the OS assigns on localhost, the host that Vite binds by default (https://vite.dev/config/server-options#server-host). */
function freePort() {
   return new Promise((resolve, reject) => {
      const server = createServer();
      server.once('error', reject);
      server.listen(0, 'localhost', () => {
         const { port } = server.address();
         server.close(() => resolve(port));
      });
   });
}

/**
 * Returns the port that LOTSEN_E2E_PORT names, or a free port, which this stores in LOTSEN_E2E_PORT when the variable is unset or empty.
 *
 * @throws When LOTSEN_E2E_PORT names no port from 1 to 65535.
 */
export async function suitePort() {
   // Each worker loads the config again, so the variable carries the main process's port to the workers.
   // See https://github.com/microsoft/playwright/blob/v1.64.1/packages/playwright/src/common/configLoader.ts#L89-L100.
   if (process.env.LOTSEN_E2E_PORT === undefined || process.env.LOTSEN_E2E_PORT === '') {
      process.env.LOTSEN_E2E_PORT = String(await freePort());
   }

   const port = Number(process.env.LOTSEN_E2E_PORT);
   if (!Number.isInteger(port) || port < 1 || port > 65535) {
      throw new Error(`LOTSEN_E2E_PORT is ${process.env.LOTSEN_E2E_PORT}, which is not a port from 1 to 65535.`);
   }

   return port;
}
