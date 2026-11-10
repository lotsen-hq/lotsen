import { describe, expect, it } from 'vitest';
import { stepOrder } from '../src/lib/viewMode';
import type { StopPassage } from '../src/lib/types';

function stopPassage(path: string, role: StopPassage['role'] = 'related'): StopPassage {
   return { side: 'head', path, startLine: 1, endLine: 2, role, commit: 'c'.repeat(40), text: 'text' };
}

describe('stepOrder', () => {
   it('keeps Claude’s order when the primary passage comes first', () => {
      const passages = [stopPassage('a.ts', 'primary'), stopPassage('b.ts'), stopPassage('c.ts')];

      expect(stepOrder(passages).map((passage) => passage.path)).toEqual(['a.ts', 'b.ts', 'c.ts']);
   });

   it('moves a primary passage that Claude placed later to the front, and keeps the related passages in Claude’s order', () => {
      const passages = [stopPassage('b.ts'), stopPassage('c.ts'), stopPassage('a.ts', 'primary'), stopPassage('d.ts')];

      expect(stepOrder(passages).map((passage) => passage.path)).toEqual(['a.ts', 'b.ts', 'c.ts', 'd.ts']);
   });

   it('returns no passages for a stop without any', () => {
      expect(stepOrder([])).toEqual([]);
   });
});
