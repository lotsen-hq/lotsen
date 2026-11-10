import { describe, expect, it } from 'vitest';
import { rowOf, touched, withPart, withSummary } from '../src/lib/tourRows';
import type { TourSummary } from '../src/lib/types';

const pinnedPin = { state: 'pinned', mergeBase: 'a'.repeat(40), head: 'b'.repeat(40), iteration: 1, remoteUrl: 'https://platform.test/shop.git' } as const;

const listedAt = '2026-10-30T10:00:00+00:00';
const writtenAt = '2026-10-30T10:00:05+00:00';
const earlierAt = '2026-10-30T10:00:01+00:00';

const summary: TourSummary = {
   id: 'only',
   target: { kind: 'pullRequest', platform: 'azureDevOps', url: 'https://dev.azure.com/contoso/shop/_git/web/pullrequest/1' },
   familiarity: 'some',
   createdAt: listedAt,
   updatedAt: listedAt,
   pin: pinnedPin,
   stopCount: 2,
   label: 'shop #1',
   state: 'ready',
   pullRequestState: 'active',
   revision: 3,
};

describe('a tour row', () => {
   it('drops a message of a part older than the row holds of that part, and takes an older message of another part', () => {
      const pinned = withPart(rowOf(summary), 'pin', 5, writtenAt, { pin: { state: 'failed', reason: 'Sign in again.' } });

      expect(withPart(pinned, 'pin', 4, earlierAt, { pin: pinnedPin })).toBe(pinned);
      expect(withPart(pinned, 'pin', 5, writtenAt, { pin: pinnedPin })).toBe(pinned);
      const counted = withPart(pinned, 'stopCount', 4, earlierAt, { stopCount: 3 });
      expect(counted.summary).toMatchObject({ stopCount: 3, pin: { state: 'failed' }, updatedAt: writtenAt });
   });

   it('takes from a summary only the parts that are newer than the row’s messages of them', () => {
      const row = withPart(rowOf(summary), 'state', 5, writtenAt, { state: 'archived', pullRequestState: 'active' });

      const merged = withSummary(row, { ...summary, revision: 4, updatedAt: earlierAt, stopCount: 3, state: 'inProgress' });

      expect(merged.summary).toMatchObject({ stopCount: 3, state: 'archived', updatedAt: writtenAt });
   });

   it('moves only the summary’s update time when touched, so no part drops an older message after it', () => {
      const row = touched(rowOf(summary), 5, writtenAt);

      expect(row.summary.updatedAt).toBe(writtenAt);
      expect(withPart(row, 'stopCount', 4, earlierAt, { stopCount: 3 }).summary.stopCount).toBe(3);
      expect(touched(row, 4, earlierAt)).toBe(row);
   });

   // The server publishes a binding change at revision 0 when it cannot store the tour's next revision.
   it('holds each part of a row from the tour list at the summary’s revision, so it drops an older message and one at revision 0, and takes a newer one', () => {
      const row = rowOf(summary);

      expect(withPart(row, 'familiarity', 2, earlierAt, { familiarity: 'new' })).toBe(row);
      expect(withPart(row, 'familiarity', 3, earlierAt, { familiarity: 'new' })).toBe(row);
      expect(withPart(row, 'binding', 0, undefined, { binding: { sessionId: 's1', level: 'confirmed' } })).toBe(row);
      expect(withPart(row, 'familiarity', 4, writtenAt, { familiarity: 'new' }).summary).toMatchObject({ familiarity: 'new', updatedAt: writtenAt });
      expect(touched(row, 2, earlierAt)).toBe(row);
   });

   it('drops a message at revision 0 of a part that a message at a revision holds', () => {
      const bound = withPart(rowOf(summary), 'binding', 5, undefined, { binding: { sessionId: 's1', level: 'confirmed' } });

      expect(withPart(bound, 'binding', 0, undefined, { binding: undefined })).toBe(bound);
   });
});
