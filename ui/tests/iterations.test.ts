import { describe, expect, it } from 'vitest';
import { availableOn, iterationCheckState, iterationNotice } from '../src/lib/iterations';
import type { AvailableIteration, PinnedCommits, TourPin } from '../src/lib/types';

const pinnedPin: Extract<TourPin, { state: 'pinned' }> = { state: 'pinned', mergeBase: 'a'.repeat(40), head: 'b'.repeat(40), iteration: 1, remoteUrl: 'https://platform.test/shop.git' };

function availableIteration(iteration: number, staleStops: string[]): AvailableIteration {
   const from: PinnedCommits = { mergeBase: pinnedPin.mergeBase, head: pinnedPin.head, iteration: pinnedPin.iteration, remoteUrl: pinnedPin.remoteUrl };
   return { pin: { ...from, head: String(iteration % 10).repeat(40), iteration }, staleStops, foundAt: '2026-10-29T12:00:00+00:00' };
}

describe('the notice text', () => {
   it('names the iteration and counts the stale stops, one stop in the singular', () => {
      expect(iterationNotice(availableIteration(3, ['s1', 's2']))).toBe('Update 3 is available: 2 stops affected');
      expect(iterationNotice(availableIteration(2, ['s1']))).toBe('Update 2 is available: 1 stop affected');
      expect(iterationNotice(availableIteration(2, []))).toBe('Update 2 is available: 0 stops affected');
   });

   it('hides a failure once the available iteration differs from the one the failed check saw, or once one arrives after a check that saw none, and only then', () => {
      const seen = availableIteration(2, ['s1']);
      const failed = (iteration?: AvailableIteration | null) => ({ running: false, failure: iteration === undefined ? { reason: 'Unreachable.' } : { reason: 'Unreachable.', iteration } });
      const known = (iteration: AvailableIteration | null) => iteration;

      expect(iterationCheckState(failed(seen), undefined).failure).toBe('Unreachable.');
      expect(iterationCheckState(failed(seen), known({ ...seen })).failure).toBe('Unreachable.');
      expect(iterationCheckState(failed(seen), known({ ...seen, staleStops: ['s1', 's2'] })).failure).toBeNull();
      expect(iterationCheckState(failed(seen), known(availableIteration(3, ['s1']))).failure).toBeNull();
      expect(iterationCheckState(failed(seen), known(null)).failure).toBeNull();
      expect(iterationCheckState(failed(null), known(null)).failure).toBe('Unreachable.');
      expect(iterationCheckState(failed(null), known(seen)).failure).toBeNull();
      expect(iterationCheckState(failed(), undefined).failure).toBe('Unreachable.');
      expect(iterationCheckState(failed(), known(null)).failure).toBe('Unreachable.');
      expect(iterationCheckState(failed(), known(seen)).failure).toBeNull();
      expect(iterationCheckState(undefined, known(seen))).toEqual({ running: false, failure: null });
   });

   it("shows no iteration that is the tour's pin, or of a tour that is not pinned", () => {
      const known = availableIteration(2, ['s1']);

      expect(availableOn(known, pinnedPin)).toEqual(known);
      expect(availableOn(known, { ...pinnedPin, head: '2'.repeat(40), iteration: 2 })).toBeNull();
      expect(availableOn(known, { ...pinnedPin, head: '2'.repeat(40), mergeBase: 'e'.repeat(40), iteration: 2 })).toEqual(known);
      expect(availableOn(known, { state: 'pinning' })).toBeNull();
   });
});
