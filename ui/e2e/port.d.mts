// The types of port.mjs, which runs in Node only, for the TypeScript that imports it.

/**
 * Returns the port that LOTSEN_E2E_PORT names, or a free port, which this stores in LOTSEN_E2E_PORT when the variable is unset or empty.
 *
 * @throws When LOTSEN_E2E_PORT names no port from 1 to 65535.
 */
export function suitePort(): Promise<number>;
