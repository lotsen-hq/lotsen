// What the demo page and the demo server agree on, apart from demo/worlds.mjs, which only Node loads.
import type { SampleSeed } from './seed.ts';

/** The header that names the page's world on each of the page's requests, which the demo server removes before it forwards the request. */
export const worldHeader = 'Lotsen-Demo-World';

/** The path that the page posts to for a world of its own, which answers with a {@link DemoWorld}. */
export const worldPath = '/__demo/world';

/** What the page needs of its world: the world's ID, its UI credential, a one-time code for the UI, and the template's seed. */
export interface DemoWorld {
   world: string;
   uiCredential: string;
   code: string;
   seed: SampleSeed;
}
