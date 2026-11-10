// The types of host.mjs, which runs in Node only, for the TypeScript that imports it.
import type { HostAddress } from './backend';

/** A running test host, which exits once {@link RunningHost.stop} closes its standard input. */
export interface RunningHost {
   address: HostAddress;
   stop(): Promise<void>;
}

/** Returns the host's assembly: the one that LOTSEN_TEST_HOST names, or the Debug build of the project, which this builds first. */
export function hostAssembly(): string;

/** Starts the host and returns its address once it printed it. */
export function startHost(assembly?: string): Promise<RunningHost>;
