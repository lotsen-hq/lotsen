import { describe, expect, it } from 'vitest';
import { pinnedReferenceAtPin } from '../src/lib/pin';
import type { PinnedCommits } from '../src/lib/types';

const pin: Pick<PinnedCommits, 'mergeBase' | 'head'> = { mergeBase: 'a'.repeat(40), head: 'b'.repeat(40) };

describe("Claude's synthesis on the summary page", () => {
   it('treats a ref: link as at an earlier commit once the tour has another pin', () => {
      const atPin = pinnedReferenceAtPin(pin, { base: pin.mergeBase, head: 'n'.repeat(40) });
      const atSamePin = pinnedReferenceAtPin(pin, { base: pin.mergeBase, head: pin.head });

      expect(atPin({ side: 'head', path: 'src/cart.ts', startLine: 3, endLine: 3 })).toBe(false);
      expect(atPin({ side: 'base', path: 'src/cart.ts', startLine: 3, endLine: 3 })).toBe(true);
      expect(atSamePin({ side: 'head', path: 'src/cart.ts', startLine: 3, endLine: 3 })).toBe(true);
      expect(pinnedReferenceAtPin(pin, null)({ side: 'head', path: 'src/cart.ts', startLine: 3, endLine: 3 })).toBe(true);
   });
});
