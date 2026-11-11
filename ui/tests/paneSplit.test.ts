import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PaneSplit, paneSplitBounds } from '../src/lib/paneSplit.svelte';

const key = 'lotsen.paneSplit';

beforeEach(() => localStorage.clear());
afterEach(() => vi.restoreAllMocks());

describe('PaneSplit', () => {
   it('starts at half when nothing is stored', () => {
      expect(new PaneSplit().percent).toBe(50);
   });

   it('stores a share that a new split reads, to a tenth of a percent', () => {
      new PaneSplit().set(33.333);

      expect(new PaneSplit().percent).toBe(33.3);
   });

   it.each([
      [10, paneSplitBounds.min],
      [95, paneSplitBounds.max],
   ])('clamps %d percent to the bounds', (percent, clamped) => {
      const split = new PaneSplit();

      split.set(percent);

      expect(split.percent).toBe(clamped);
      expect(new PaneSplit().percent).toBe(clamped);
   });

   it('ignores a share that is not a number', () => {
      const split = new PaneSplit();
      split.set(40);

      split.set(NaN);
      split.set(Infinity);

      expect(split.percent).toBe(40);
   });

   it.each(['900', '-1', '45.5', 'wide'])('starts at half when storage holds %s', (stored) => {
      localStorage.setItem(key, stored);

      expect(new PaneSplit().percent).toBe(50);
   });

   it('resets to half', () => {
      const split = new PaneSplit();
      split.set(70);

      split.reset();

      expect(split.percent).toBe(50);
      expect(new PaneSplit().percent).toBe(50);
   });

   it('keeps a share for the page when browser storage throws', () => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
         throw new DOMException('Blocked', 'SecurityError');
      });
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
         throw new DOMException('Blocked', 'SecurityError');
      });
      const split = new PaneSplit();

      split.set(60);

      expect(split.percent).toBe(60);
   });

   describe('moves for a key', () => {
      it.each([
         ['ArrowRight', 60],
         ['ArrowLeft', 40],
         ['Home', paneSplitBounds.min],
         ['End', paneSplitBounds.max],
      ])('%s', (pressed, percent) => {
         const split = new PaneSplit();

         expect(split.key(pressed)).toBe(true);
         expect(split.percent).toBe(percent);
      });

      it.each([
         ['ArrowRight', paneSplitBounds.max],
         ['ArrowLeft', paneSplitBounds.min],
      ])('Shift+%s to the bound', (pressed, percent) => {
         const split = new PaneSplit();

         expect(split.key(pressed, true)).toBe(true);
         expect(split.percent).toBe(percent);
      });

      it('stops at the bounds', () => {
         const split = new PaneSplit();
         split.set(75);

         split.key('ArrowRight');

         expect(split.percent).toBe(paneSplitBounds.max);
      });

      it('leaves any other key alone', () => {
         const split = new PaneSplit();

         expect(split.key('ArrowUp')).toBe(false);
         expect(split.key('Enter')).toBe(false);
         expect(split.key('toString')).toBe(false);
         expect(split.percent).toBe(50);
      });
   });
});
