import type { CheckedMove, MoveDive } from './types';

/**
 * Whether a repository tour's ref can move, as the page's last check of a move learned it: checking while the first check runs, movable for a branch, fixed for a tag or a commit ID.
 * A check that failed counts as movable, so the reviewer can check again from the move dialog, which shows the reason.
 */
export type MoveOffer = 'checking' | 'movable' | 'fixed';

/** Returns what a check of a move tells of the tour's ref. */
export function moveOfferOf(checked: CheckedMove): MoveOffer {
   return checked.move === 'tag' || checked.move === 'commit' ? 'fixed' : 'movable';
}

/** The outcome of a check of a move: the check, or the reason that it failed. */
export type MoveCheckOutcome = { checked: CheckedMove } | { failure: string };

/** The outcome of a move that the server refused: its reason, and whether the move dialog checks again, because the refusal says that the ref or the tours changed since the check. */
export interface MoveRefusal {
   reason: string;
   recheck: boolean;
}

/** A dive that a move offers, with the offered dives that dive from it. */
export interface MoveBranch {
   dive: MoveDive;
   dives: MoveBranch[];
}

/**
 * Returns the offered dives nested as the family nests them, in the order of the check, which lists each tour before its dives.
 *
 * A dive whose parent the check does not list, because the parent is the checked tour or is at the latest commit already, starts a branch of its own.
 */
export function moveBranches(dives: readonly MoveDive[]): MoveBranch[] {
   const branches = new Map(dives.map((dive): [string, MoveBranch] => [dive.tourId, { dive, dives: [] }]));
   const top: MoveBranch[] = [];
   for (const dive of dives) {
      const branch = branches.get(dive.tourId)!;
      const parent = branches.get(dive.parentId);
      (parent?.dives ?? top).push(branch);
   }

   return top;
}

/**
 * Returns the IDs of the offered dives that move, in the order of the check: each dive that the reviewer did not untick, whose parent moves too or is not offered.
 *
 * A dive moves only with the tour that it dives from, unless that tour is at the latest commit already. So an untick of a dive also unticks the offered dives below it.
 */
export function tickedDives(dives: readonly MoveDive[], unticked: ReadonlySet<string>): string[] {
   const offered = new Set(dives.map((dive) => dive.tourId));
   const ticked = new Set<string>();
   // The check lists each tour before its dives, so a parent's tick is known before its dives'.
   for (const dive of dives) {
      if (!unticked.has(dive.tourId) && (!offered.has(dive.parentId) || ticked.has(dive.parentId))) {
         ticked.add(dive.tourId);
      }
   }

   return [...ticked];
}

/** Returns the unticked dives after the reviewer ticks an offered dive: the dive and every offered dive above it then move, as {@link tickedDives} requires. */
export function tickDive(dives: readonly MoveDive[], unticked: ReadonlySet<string>, tourId: string): Set<string> {
   const parents = new Map(dives.map((dive) => [dive.tourId, dive.parentId]));
   const next = new Set(unticked);
   for (let id: string | undefined = tourId; id !== undefined; id = parents.get(id)) {
      next.delete(id);
   }

   return next;
}
