import type { DoneState } from './tourRecord.svelte';

/** What a click on a stop's done checkbox does: mark a version done, or take the mark back. */
export interface DoneClick {
   /** The number of the version that the click targets. */
   version: number;
   /** Whether the done mark covers the version already, so the click takes the mark back. */
   clears: boolean;
   /** Whether the version is earlier than the stop's current version. */
   earlier: boolean;
}

/**
 * Returns what a click on a stop's done checkbox does. It targets the version that the page shows of the stop under view, which a revision that the reviewer did not ask for leaves earlier
 * than the current one, and the current version of every other stop.
 *
 * @param done - The stop's done state, which compares the mark with the current version.
 * @param current - The number of the stop's current version.
 * @param shown - The number of the version that the page shows when the stop is the stop under view, or null for every other stop.
 */
export function doneClick(done: DoneState, current: number, shown: number | null): DoneClick {
   const version = shown ?? current;
   const marked = done.state === 'done' ? current : done.state === 'earlier' ? done.version : null;
   return { version, clears: marked === version, earlier: version < current };
}
