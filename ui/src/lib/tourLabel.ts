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

/** What the top bar's tour switcher shows of a tour, on its two lines. */
export interface SwitcherLines {
   /** The bottom line: a repository tour's title, "Pull request" and the number for a pull request's tour, or a label that ends in no number, whole. */
   title: string;
   /** The top line's place: the repository and the ref of a repository tour, or the repository part of a pull request's label, or null for a label that ends in no number. */
   place: string | null;
   /** The first 8 characters of the commit that a repository tour shows, or null for a pull request's tour or a tour without a snapshot. */
   commit: string | null;
}

/** Returns the lines that the tour switcher shows for the tour. */
export function switcherLines(tour: Pick<TourSummary, 'target' | 'label' | 'title' | 'pin'>): SwitcherLines {
   const heading = repositoryHeading(tour);
   if (heading !== null) {
      const place = heading.ref === null ? heading.repository : `${heading.repository}@${heading.ref}`;
      return { title: heading.title, place, commit: tour.pin.state === 'snapshot' ? tour.pin.head.slice(0, 8) : null };
   }

   const parts = splitLabel(tour.label);
   return parts === null ? { title: tour.label, place: null, commit: null } : { title: `Pull request ${parts.number}`, place: parts.repository, commit: null };
}
