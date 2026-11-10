import type { AvailableIteration, TourPin } from './types';

/** The state of the reviewer's iteration check of one tour. */
export interface IterationCheckState {
   /** Whether the page waits for the check's response. */
   running: boolean;
   /** Why the last check could not finish, or null when it finished or none ran. */
   failure: string | null;
}

/** The reviewer's last iteration check of one tour, as the page keeps it. */
export interface IterationCheck {
   /** Whether the page waits for the check's response. */
   running: boolean;
   /**
    * Why the last check could not finish, with the available iteration that the failed check saw, or null when it finished or none ran. The iteration is null when the check saw none, and absent
    * when the page knew none.
    */
   failure: { reason: string; iteration?: AvailableIteration | null } | null;
}

/**
 * Returns the check's state, with its failure only while the page knows the available iteration that the failed check saw.
 *
 * @remarks Only a change to the available iteration hides the failure, whatever order the two arrive in. An update to another part of the tour leaves the iteration and the failure as they were.
 * A failure from a check that saw no iteration, because the page knew none, hides once the page knows an available iteration.
 *
 * @param known - The available iteration that the page knows, null when the server knows of none, or undefined before the page knows either.
 */
export function iterationCheckState(check: IterationCheck | undefined, known: AvailableIteration | null | undefined): IterationCheckState {
   const failure = check?.failure ?? null;
   const superseded = failure !== null && known !== undefined && (failure.iteration === undefined ? known !== null : !sameIteration(failure.iteration, known));
   return { running: check?.running ?? false, failure: failure === null || superseded ? null : failure.reason };
}

// The server serializes the fields of an available iteration in one order, and a check that finds the same result keeps the stored value with its foundAt.
function sameIteration(a: AvailableIteration | null, b: AvailableIteration | null): boolean {
   return JSON.stringify(a) === JSON.stringify(b);
}

/**
 * Returns the available iteration of a tour on this pin, or null when the page knows none, the tour is not pinned, or the iteration is the pin itself.
 *
 * @remarks The server clears the available iteration in the same store that changes the pin, so an iteration with the pin's commits is one that the page holds from before an update.
 */
export function availableOn(known: AvailableIteration | null | undefined, pin: TourPin | undefined): AvailableIteration | null {
   const iteration = known ?? null;
   return iteration !== null && pin?.state === 'pinned' && (iteration.pin.head !== pin.head || iteration.pin.mergeBase !== pin.mergeBase) ? iteration : null;
}

/** Returns the notice's text, such as `Update 3 is available: 2 stops affected`. */
export function iterationNotice(iteration: AvailableIteration): string {
   const count = iteration.staleStops.length;
   return `Update ${iteration.pin.iteration} is available: ${count} ${count === 1 ? 'stop' : 'stops'} affected`;
}
