import type { Plan, TourState, TourSummary } from './types';

/** How far the reviewer has read a dive, which a parent's Dives row, dive marker and summary page show: a tour state without Claude's side of it. */
export type ReadingState = 'notStarted' | 'inProgress' | 'read' | 'archived';

/** The name of each reading state, as the parent's surfaces show it. */
export const readingStateLabels: Record<ReadingState, string> = {
   notStarted: 'Not started',
   inProgress: 'In progress',
   read: 'Read',
   archived: 'Archived',
};

/** Returns the reading state of a tour in the state: not started until the reviewer opens a stop or the summary page, whether or not Claude has finished writing it. */
export function readingStateOf(state: TourState): ReadingState {
   switch (state) {
      case 'inProgress':
         return 'inProgress';
      case 'finished':
         return 'read';
      case 'archived':
         return 'archived';
      default:
         return 'notStarted';
   }
}

/** Returns the name that the family's surfaces give a tour: a repository tour's title, or the label of a tour without one. */
export function tourName(tour: Pick<TourSummary, 'title' | 'label'>): string {
   return tour.title ?? tour.label;
}

/** A tour of the tour list in the order that it shows them, with its depth: 1 for a dive under its root, 0 for any other tour. */
export interface ListedTour {
   tour: TourSummary;
   depth: 0 | 1;
}

/**
 * Returns the tours with each family grouped one level deep under its root: the root, then every dive of the family, its dives' dives too, oldest first.
 *
 * The roots and the other tours keep their order. A dive whose root the tours lack, such as a root that the list hides, keeps its own place at depth 0, and so does a dive whose root is itself a dive,
 * which only a damaged store holds, so a cycle of roots loses no tour.
 */
export function groupFamilies(tours: readonly TourSummary[]): ListedTour[] {
   const roots = new Set(tours.filter((tour) => tour.diveOrigin === undefined).map((tour) => tour.id));
   const rootOf = (tour: TourSummary) => (tour.diveOrigin !== undefined && roots.has(tour.diveOrigin.rootId) ? tour.diveOrigin.rootId : null);
   const dives = new Map<string, TourSummary[]>();
   for (const tour of tours) {
      const rootId = rootOf(tour);
      if (rootId !== null) {
         dives.set(rootId, [...(dives.get(rootId) ?? []), tour]);
      }
   }

   return tours.flatMap((tour): ListedTour[] => (rootOf(tour) !== null ? [] : [{ tour, depth: 0 }, ...oldestFirst(dives.get(tour.id) ?? []).map((dive): ListedTour => ({ tour: dive, depth: 1 }))]));
}

/** Returns the tour that a dive's up button opens: its parent, or its root when the tours lack the parent, or null for a tour that is not a dive or when the tours lack both. */
export function parentOf(tour: TourSummary, tours: readonly TourSummary[]): TourSummary | null {
   if (tour.diveOrigin === undefined) {
      return null;
   }

   const { parentId, rootId } = tour.diveOrigin;
   return tours.find((candidate) => candidate.id === parentId && candidate.id !== tour.id) ?? tours.find((candidate) => candidate.id === rootId && candidate.id !== tour.id) ?? null;
}

/** Returns the dives that start from the tour, oldest first, without their own dives. */
export function divesOf(tourId: string, tours: readonly TourSummary[]): TourSummary[] {
   return oldestFirst(tours.filter((tour) => tour.diveOrigin?.parentId === tourId));
}

/** A dive of a tour's subtree, with the dives that start from it. */
export interface DiveBranch {
   tour: TourSummary;
   dives: DiveBranch[];
}

/**
 * Returns the dives below the tour, each with its own dives, oldest first: the subtree that a delete or an archive of the tour takes with it.
 *
 * Only a damaged store holds a cycle of parents, and then each tour of the cycle shows once.
 */
export function subtreeOf(tourId: string, tours: readonly TourSummary[]): DiveBranch[] {
   const seen = new Set([tourId]);
   const branchesOf = (parentId: string): DiveBranch[] => {
      const dives = divesOf(parentId, tours).filter((dive) => !seen.has(dive.id));
      dives.forEach((dive) => seen.add(dive.id));
      return dives.map((dive) => ({ tour: dive, dives: branchesOf(dive.id) }));
   };
   return branchesOf(tourId);
}

/** Returns the tours of the branches, each before its dives. */
export function flatDives(branches: readonly DiveBranch[]): TourSummary[] {
   return branches.flatMap((branch) => [branch.tour, ...flatDives(branch.dives)]);
}

/** Returns the branches cut to their first dives, at most `max` of them, in the order that {@link flatDives} lists them. */
export function firstDives(branches: readonly DiveBranch[], max: number): DiveBranch[] {
   let left = max;
   const cut = (list: readonly DiveBranch[]): DiveBranch[] => {
      const kept: DiveBranch[] = [];
      for (const branch of list) {
         if (left === 0) {
            break;
         }

         left--;
         kept.push({ tour: branch.tour, dives: cut(branch.dives) });
      }

      return kept;
   };
   return cut(branches);
}

/**
 * Returns the parent's dives by the ID of the stop that shows each one.
 *
 * A dive shows on the stop it started from. A whole-tour dive, and a dive from a stop that the plan no longer holds, show on the plan's first overview stop, or on its first stop when no stop is
 * an overview. A plan without stops shows none of them.
 */
export function divesByStop(dives: readonly TourSummary[], plan: Plan | null | undefined): Map<string, TourSummary[]> {
   const stops = plan?.stops ?? [];
   const fallback = (stops.find((stop) => stop.kind === 'overview') ?? stops[0])?.id;
   const byStop = new Map<string, TourSummary[]>();
   for (const dive of dives) {
      const from = dive.diveOrigin?.stopId;
      const stopId = from !== undefined && stops.some((stop) => stop.id === from) ? from : fallback;
      if (stopId !== undefined) {
         byStop.set(stopId, [...(byStop.get(stopId) ?? []), dive]);
      }
   }

   return byStop;
}

// The ID orders two tours of one millisecond, as the tour list does.
function oldestFirst(tours: readonly TourSummary[]): TourSummary[] {
   return [...tours].sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt) || a.id.localeCompare(b.id));
}
