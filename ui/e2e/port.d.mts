// The types of port.mjs, which runs in Node only, for the TypeScript that imports it.

/**
 * Returns the port that the environment variable names, or a free port, which this stores in the variable when it is unset or empty.
 *
 * @param variable The variable, LOTSEN_E2E_PORT for the demo and LOTSEN_E2E_CSP_PORT for the built demo under the server's policy.
 * @throws When the variable names no port from 1 to 65535.
 */
export function suitePort(variable?: string): Promise<number>;
