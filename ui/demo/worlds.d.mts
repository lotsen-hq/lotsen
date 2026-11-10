// The types of worlds.mjs, which runs in Node only, for the Vite configuration that imports it.
import type { Plugin } from 'vite';

/** Returns the plugin that serves the demo's worlds: `POST /__demo/world`, the page's `/api/*` requests to its world, and `/control/*` to the host with the host's token. */
export function demoWorlds(): Plugin;

/** Returns why the demo refuses a request for a world or for the host's control API, or null when it takes it: one to a loopback name, from no page or from a page of that origin. */
export function foreignPageRefusal(headers: { host?: string; origin?: string }): string | null;

/** Holds the world that the next load of the page takes, which starts before that load comes, so the load need not wait for the host's build and the template's seed. */
export class SpareWorld<T> {
   /**
    * @param start - Starts a world.
    * @param report - Reports the failure of a spare that no load took.
    */
   constructor(start: () => Promise<T>, report?: (error: unknown) => void);

   /** Returns the spare world, and starts the next spare in the same step, so loads that come together never share a world. */
   take(): Promise<T>;
}
