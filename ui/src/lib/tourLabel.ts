import type { TourSummary } from './types';

/** A label split into the part before its pull request number, such as `org/project/repo`, and the number, such as `#1234`. */
export interface SplitLabel {
   repository: string;
   number: string;
}

/** Splits a label that ends in a space, `#` and digits, as both platforms' labels do, or returns null for any other label, which the bar shows whole. */
export function splitLabel(label: string): SplitLabel | null {
   const match = /^(.*\S) (#\d+)$/.exec(label);
   return match === null ? null : { repository: match[1]!, number: match[2]! };
}

/** What the top bar and the tour list show of a repository tour: its title, the repository's name, and the ref that the tour follows. */
export interface RepositoryHeading {
   title: string;
   repository: string;
   /** The branch, tag or commit ID, or null for the default branch, which the heading leaves out. */
   ref: string | null;
}

/**
 * Returns the heading of a repository tour, or null for a pull request's tour.
 *
 * The repository's name is the label without the `@` and the ref that end it. A summary without a title shows the repository's name as its title.
 */
export function repositoryHeading(tour: Pick<TourSummary, 'target' | 'label' | 'title'>): RepositoryHeading | null {
   if (tour.target.kind !== 'repository') {
      return null;
   }

   const ref = tour.target.ref ?? null;
   const suffix = ref === null ? '' : `@${ref}`;
   const repository = suffix !== '' && tour.label.endsWith(suffix) ? tour.label.slice(0, -suffix.length) : tour.label;
   return { title: tour.title ?? repository, repository, ref };
}
