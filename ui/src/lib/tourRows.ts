import { supersedes } from './revisioned.svelte';
import type { PlanApproval, TourSummary } from './types';

/** The parts of a tour's row that writes change apart from each other, each with the fields that it holds. */
export interface RowParts {
   stopCount: Pick<TourSummary, 'stopCount'>;
   pin: Pick<TourSummary, 'pin'>;
   familiarity: Pick<TourSummary, 'familiarity'>;
   planApproval: { planApproval: PlanApproval };
   state: Pick<TourSummary, 'state' | 'pullRequestState'>;
   binding: Pick<TourSummary, 'binding'>;
}

export type RowPart = keyof RowParts;

/**
 * A tour's row in the tour list, with the tour's revision of the message that each part of the row and the summary's `updatedAt` come from.
 *
 * Each part keeps the value of the message with the highest revision for that part, as {@link supersedes} orders them, so a write to one part never hides an earlier write to another part that
 * arrives after it.
 */
export interface TourRow {
   summary: TourSummary;
   revisions: Readonly<Record<RowPart | 'updatedAt', number>>;
}

/** Returns the row of a summary, each part at the summary's revision. */
export function rowOf(summary: TourSummary): TourRow {
   const at = summary.revision;
   return { summary, revisions: { stopCount: at, pin: at, familiarity: at, planApproval: at, state: at, binding: at, updatedAt: at } };
}

/**
 * Returns the row with the part's fields from a message at the revision, and its update time when the message has one, or the same row when the row holds a newer message of the part.
 */
export function withPart<P extends RowPart>(row: TourRow, part: P, revision: number, updatedAt: string | undefined, fields: RowParts[P]): TourRow {
   if (!supersedes(revision, row.revisions[part])) {
      return row;
   }

   const next = { summary: { ...row.summary, ...fields }, revisions: { ...row.revisions, [part]: revision } };
   return updatedAt === undefined ? next : touched(next, revision, updatedAt);
}

/**
 * Returns the row with each part of a summary at the summary's revision that is newer than the row's message of that part. The row's label, target and creation time stay, because they never change.
 *
 * A summary without a plan approval, which is a tour started without plan first, leaves the row's plan approval as it is.
 */
export function withSummary(row: TourRow, summary: TourSummary): TourRow {
   const { revision, updatedAt: at } = summary;
   let next = withPart(row, 'stopCount', revision, at, { stopCount: summary.stopCount });
   next = withPart(next, 'pin', revision, at, { pin: summary.pin });
   next = withPart(next, 'familiarity', revision, at, { familiarity: summary.familiarity });
   next = summary.planApproval === undefined ? next : withPart(next, 'planApproval', revision, at, { planApproval: summary.planApproval });
   next = withPart(next, 'binding', revision, at, { binding: summary.binding });
   return withPart(next, 'state', revision, at, { state: summary.state, pullRequestState: summary.pullRequestState });
}

/** Returns the row with its summary's update time from a message at the revision, unless the row holds the update time of a newer message. */
export function touched(row: TourRow, revision: number, updatedAt: string): TourRow {
   return supersedes(revision, row.revisions.updatedAt) ? { summary: { ...row.summary, updatedAt }, revisions: { ...row.revisions, updatedAt: revision } } : row;
}
