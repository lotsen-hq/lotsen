import { describe, expect, it } from 'vitest';
import { steppedStopIndex } from '../src/lib/skips';
import type { Stop, StopContent } from '../src/lib/types';
import { stopOf } from './stops';

function content(headline: string): StopContent {
   return { headline, passages: [], html: { headline: `<p>${headline}</p>\n` } };
}

function stop(id: string, title: string, written = true): Stop {
   return stopOf({ id, title, kind: 'inspect', chapter: 'c', content: written ? content(`${title}.`) : null });
}

const stops = [stop('s1', 'Cart adds'), stop('s2', 'Line totals'), stop('s3', 'Gateway errors'), stop('s4', 'Coupons', false)];

describe('the stop that next and back open', () => {
   it('is the nearest stop in that direction that the reviewer did not skip', () => {
      expect(steppedStopIndex(stops, 0, new Set(['s2']), 1)).toBe(2);
      expect(steppedStopIndex(stops, 2, new Set(['s2']), -1)).toBe(0);
      expect(steppedStopIndex(stops, 0, new Set(), 1)).toBe(1);
   });

   it('is the first stop not skipped for next while no stop is open, and none for back', () => {
      expect(steppedStopIndex(stops, -1, new Set(['s1', 's2']), 1)).toBe(2);
      expect(steppedStopIndex(stops, -1, new Set(), -1)).toBe(-1);
   });

   it('is none when every stop in that direction is skipped, also from a skipped stop that the reviewer opened', () => {
      expect(steppedStopIndex(stops, 1, new Set(['s2', 's3', 's4']), 1)).toBe(-1);
      expect(steppedStopIndex(stops, 1, new Set(['s1', 's2']), -1)).toBe(-1);
      expect(steppedStopIndex(stops, 1, new Set(['s1', 's2']), 1)).toBe(2);
   });
});
