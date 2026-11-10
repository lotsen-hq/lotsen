import { describe, expect, it } from 'vitest';
import { hiddenCount, hiddenReason, listedTours } from '../src/lib/tourList';
import type { TourSummary } from '../src/lib/types';

const summary = (id: string, state: TourSummary['state'], pullRequestState: TourSummary['pullRequestState'] = 'active'): TourSummary => ({
   id,
   target: { kind: 'pullRequest', platform: 'test', url: `https://platform.test/${id}` },
   familiarity: 'some',
   createdAt: '2026-10-30T09:00:00+00:00',
   updatedAt: '2026-10-30T09:00:00+00:00',
   pin: { state: 'pinning' },
   stopCount: 0,
   label: id,
   state,
   pullRequestState,
   revision: 1,
});

describe('the hidden tours', () => {
   it('are the archived tours, which the list leaves out unless hidden tours show, and counts', () => {
      const tours = [summary('a', 'ready'), summary('b', 'archived'), summary('c', 'finished')];

      expect(tours.map(hiddenReason)).toEqual([null, 'archived', null]);
      expect(listedTours(tours, false).map((tour) => tour.id)).toEqual(['a', 'c']);
      expect(listedTours(tours, true).map((tour) => tour.id)).toEqual(['a', 'b', 'c']);
      expect(hiddenCount(tours)).toBe(1);
   });

   it('include the tours whose pull request was completed or abandoned, and a flagged tour that is also archived counts once', () => {
      const tours = [summary('a', 'ready', 'active'), summary('b', 'inProgress', 'completed'), summary('c', 'finished', 'abandoned'), summary('d', 'archived', 'completed')];

      expect(tours.map(hiddenReason)).toEqual([null, 'pullRequestCompleted', 'pullRequestAbandoned', 'archived']);
      expect(listedTours(tours, false).map((tour) => tour.id)).toEqual(['a']);
      expect(listedTours(tours, true).map((tour) => tour.id)).toEqual(['a', 'b', 'c', 'd']);
      expect(hiddenCount(tours)).toBe(3);
   });
});
