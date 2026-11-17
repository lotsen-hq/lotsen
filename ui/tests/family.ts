import type { TourSummary } from '../src/lib/types';

/** Where a dive starts: its parent, its family's root and the parent's stop, or none for a whole-tour dive. */
export interface Origin {
   parent: string;
   root: string;
   stop?: string;
}

/** Returns the summary of a repository tour titled "Tour <id>", created `minute` minutes after the first, as a dive when it has an origin. */
export function familySummary(id: string, minute: number, origin?: Origin, fields: Partial<TourSummary> = {}): TourSummary {
   const at = new Date(Date.UTC(2026, 10, 1, 9, minute)).toISOString();
   return {
      id,
      target: { kind: 'repository', url: 'https://platform.test/shop' },
      familiarity: 'some',
      createdAt: at,
      updatedAt: at,
      pin: { state: 'pinning' },
      stopCount: 0,
      label: 'shop',
      state: 'ready',
      pullRequestState: 'active',
      revision: 1,
      purpose: 'orient',
      title: `Tour ${id}`,
      ...(origin === undefined ? {} : { diveOrigin: { parentId: origin.parent, rootId: origin.root, ...(origin.stop === undefined ? {} : { stopId: origin.stop }) } }),
      ...fields,
   };
}
