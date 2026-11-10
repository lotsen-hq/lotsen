// Starts one test host for the run of the app project. Each flow test starts its own world on it.
import type { TestProject } from 'vitest/node';
import type { HostAddress } from '../backend/backend';
import { startHost } from '../backend/host.mjs';

declare module 'vitest' {
   export interface ProvidedContext {
      lotsenHost: HostAddress;
   }
}

export default async function setup(project: TestProject): Promise<() => Promise<void>> {
   const host = await startHost();
   project.provide('lotsenHost', host.address);
   return () => host.stop();
}
