import { familiarityLine } from './labels';
import type { PullRequestState, TourState, TourSummary } from './types';

/** The name of each tour state, as the tour list shows it. */
export const tourStateLabels: Record<TourState, string> = {
   generating: 'Generating',
   ready: 'Ready',
   inProgress: 'In progress',
   finished: 'Finished',
   archived: 'Archived',
};

/** The flag that the tour list shows for a tour whose pull request is closed, or null for an active one. */
export const pullRequestFlags: Record<PullRequestState, string | null> = {
   active: null,
   completed: 'PR completed',
   abandoned: 'PR abandoned',
};

/** Returns the flag that the tour list shows for a tour of a closed pull request, or null for a tour of an active pull request or of a repository. */
export function pullRequestFlag(tour: Pick<TourSummary, 'target' | 'pullRequestState'>): string | null {
   return tour.target.kind === 'pullRequest' ? pullRequestFlags[tour.pullRequestState] : null;
}

/** Returns the line under a tour in the list: its familiarity level, after a repository tour's purpose, and its number of stops. */
export function tourLine(tour: Pick<TourSummary, 'familiarity' | 'purpose' | 'stopCount'>): string {
   const stops = tour.stopCount === 0 ? 'no plan yet' : tour.stopCount === 1 ? '1 stop' : `${tour.stopCount} stops`;
   return `${familiarityLine(tour.familiarity, tour.purpose)} · ${stops}`;
}

/** Why the tour list hides a tour: the reviewer archived it, or its pull request was completed or abandoned. A repository tour has no pull request, so only an archive hides it. */
export type HiddenReason = 'archived' | 'pullRequestCompleted' | 'pullRequestAbandoned';

/**
 * Returns why the tour list hides the tour while the reviewer does not show hidden tours, or null for a tour that it always lists.
 *
 * Every reason to hide a tour is decided here, so the list, its count of hidden tours and its toggle agree. A tour has one reason at most, so an archived and flagged tour counts once.
 */
export function hiddenReason(tour: TourSummary): HiddenReason | null {
   if (tour.state === 'archived') {
      return 'archived';
   }

   if (tour.target.kind === 'repository') {
      return null;
   }

   switch (tour.pullRequestState) {
      case 'completed':
         return 'pullRequestCompleted';
      case 'abandoned':
         return 'pullRequestAbandoned';
      default:
         return null;
   }
}

/** Returns the tours that the list shows, in their order: every tour while hidden tours show, or else those that {@link hiddenReason} does not hide. */
export function listedTours(tours: readonly TourSummary[], showHidden: boolean): TourSummary[] {
   return showHidden ? [...tours] : tours.filter((tour) => hiddenReason(tour) === null);
}

/** Returns the number of tours that {@link hiddenReason} hides. */
export function hiddenCount(tours: readonly TourSummary[]): number {
   return tours.filter((tour) => hiddenReason(tour) !== null).length;
}
