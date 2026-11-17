import { describe, expect, it } from 'vitest';
import { hiddenCount, hiddenReason, listedTours, pullRequestFlag, tourLine } from '../src/lib/tourList';
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

// A repository tour with a closed pull request state, which only a pull request tour's flag and hiding may read.
const repositoryTour = (id: string, state: TourSummary['state']): TourSummary => ({
   ...summary(id, state, 'completed'),
   target: { kind: 'repository', platform: 'test', url: 'https://platform.test/shop' },
   purpose: 'orient',
   title: 'How the cart adds up',
});

describe('a repository tour in the tour list', () => {
   it('is hidden only when archived, and has no pull request flag', () => {
      const tours = [repositoryTour('a', 'ready'), repositoryTour('b', 'archived')];

      expect(tours.map(hiddenReason)).toEqual([null, 'archived']);
      expect(tours.map(pullRequestFlag)).toEqual([null, null]);
      expect(pullRequestFlag(summary('c', 'ready', 'completed'))).toBe('PR completed');
   });

   it('shows its purpose and the short name of its familiarity level before its stops', () => {
      expect(tourLine({ ...repositoryTour('a', 'ready'), stopCount: 3 })).toBe('Orient · Some · 3 stops');
      expect(tourLine({ ...summary('b', 'ready'), stopCount: 1 })).toBe('Knows some of the code · 1 stop');
      expect(tourLine({ ...repositoryTour('c', 'generating'), purpose: 'evaluate', familiarity: 'new' })).toBe('Evaluate · New · no plan yet');
   });
});
