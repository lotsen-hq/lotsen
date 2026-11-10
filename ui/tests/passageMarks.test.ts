import { describe, expect, it } from 'vitest';
import { atSide, filePassageDecorations, withHoveredReference, withThreads, type DecorationSwitches } from '../src/lib/passageMarks';
import type { CodeReference, FileSide, StopPassage } from '../src/lib/types';

function stopPassage(side: FileSide, path: string, startLine: number, endLine: number, role: StopPassage['role'] = 'related', label?: string): StopPassage {
   return { side, path, startLine, endLine, role, commit: 'c'.repeat(40), text: 'text', ...(label === undefined ? {} : { label }) };
}

function reference(side: FileSide, path: string, startLine: number, endLine: number): CodeReference {
   return { side, path, startLine, endLine };
}

const on: DecorationSwitches = { dim: true, highlight: true };

describe('filePassageDecorations', () => {
   const passages = [
      stopPassage('head', 'src/new.ts', 3, 5, 'primary'),
      stopPassage('base', 'src/old.ts', 7, 9, 'related', 'before the move'),
      stopPassage('head', 'src/other.ts', 1, 2),
      stopPassage('base', 'src/new.ts', 1, 1),
   ];

   it('keeps the passages at each side whose path is the file’s path at that side, and dims the rest of the file', () => {
      expect(filePassageDecorations(passages, { base: 'src/old.ts', head: 'src/new.ts' }, on)).toEqual({
         passages: [
            { side: 'head', startLine: 3, endLine: 5, role: 'primary' },
            { side: 'base', startLine: 7, endLine: 9, role: 'related', label: 'before the move' },
         ],
         dim: true,
         highlight: true,
         collapse: false,
      });
   });

   it('keeps no passage at a side where the file does not exist', () => {
      expect(filePassageDecorations(passages, { base: null, head: 'src/new.ts' }, on).passages).toEqual([{ side: 'head', startLine: 3, endLine: 5, role: 'primary' }]);
   });

   it('dims nothing in a file without a passage of the stop', () => {
      expect(filePassageDecorations(passages, { base: 'src/none.ts', head: 'src/none.ts' }, on)).toEqual({ passages: [], dim: false, highlight: true, collapse: false });
   });

   it('collapses a file with a passage of the stop when asked, and never a file without one', () => {
      expect(filePassageDecorations(passages, { base: 'src/old.ts', head: 'src/new.ts' }, on, { collapse: true }).collapse).toBe(true);
      expect(filePassageDecorations(passages, { base: 'src/none.ts', head: 'src/none.ts' }, on, { collapse: true }).collapse).toBe(false);
   });

   it('keeps the passages, the dimming and the collapse but turns their highlight off when highlighting is switched off', () => {
      const decorations = filePassageDecorations(passages, { base: 'src/old.ts', head: 'src/new.ts' }, { dim: true, highlight: false }, { collapse: true });

      expect(decorations).toMatchObject({ dim: true, highlight: false, collapse: true });
      expect(decorations.passages.map((passage) => passage.role)).toEqual(['primary', 'related']);
   });

   it('keeps the passages and the collapse but dims nothing when dimming is switched off', () => {
      const decorations = filePassageDecorations(passages, { base: 'src/old.ts', head: 'src/new.ts' }, { dim: false, highlight: true }, { collapse: true });

      expect(decorations.dim).toBe(false);
      expect(decorations.passages.map((passage) => passage.role)).toEqual(['primary', 'related']);
      expect(decorations.collapse).toBe(true);
   });

   it('marks each passage it keeps as focused when asked', () => {
      expect(filePassageDecorations(passages.slice(1, 2), { base: 'src/old.ts', head: 'src/new.ts' }, on, { focused: true }).passages).toEqual([
         { side: 'base', startLine: 7, endLine: 9, role: 'related', label: 'before the move', focused: true },
      ]);
   });
});

describe('withHoveredReference', () => {
   const decorations = filePassageDecorations([stopPassage('head', 'src/new.ts', 3, 5, 'primary')], { base: 'src/old.ts', head: 'src/new.ts' }, on, { collapse: true });

   it('adds the lines of a reference into the file at its side, and keeps the passages, the dimming and the collapse', () => {
      expect(withHoveredReference(decorations, { base: 'src/old.ts', head: 'src/new.ts' }, reference('head', 'src/new.ts', 8, 9))).toEqual({
         ...decorations,
         hovered: { side: 'head', startLine: 8, endLine: 9 },
      });
   });

   it('matches a base reference into a renamed file by the old path', () => {
      const paths = { base: 'src/old.ts', head: 'src/new.ts' };

      expect(withHoveredReference(decorations, paths, reference('base', 'src/old.ts', 1, 2)).hovered).toEqual({ side: 'base', startLine: 1, endLine: 2 });
      expect(withHoveredReference(decorations, paths, reference('base', 'src/new.ts', 1, 2))).toBe(decorations);
   });

   it.each([
      ['no reference', null],
      ['a reference into another file', reference('head', 'src/other.ts', 1, 2)],
      ['a reference at a side where the file does not exist', reference('base', 'src/new.ts', 1, 2)],
   ])('returns the same decorations for %s, so the editors are not decorated again', (_, hovered) => {
      expect(withHoveredReference(decorations, { base: null, head: 'src/new.ts' }, hovered)).toBe(decorations);
   });
});

describe('atSide', () => {
   it('moves both sides’ passages of an unchanged file and the hovered lines to one side, keeping their roles, labels and focus', () => {
      const paths = { base: 'src/same.ts', head: 'src/same.ts' };
      const passages = [stopPassage('head', 'src/same.ts', 3, 4, 'primary'), stopPassage('base', 'src/same.ts', 10, 11, 'related', 'old')];
      const decorations = withHoveredReference(filePassageDecorations(passages, paths, on, { focused: true }), paths, reference('base', 'src/same.ts', 20, 21));

      expect(atSide(decorations, 'head')).toEqual({
         passages: [
            { side: 'head', startLine: 3, endLine: 4, role: 'primary', focused: true },
            { side: 'head', startLine: 10, endLine: 11, role: 'related', label: 'old', focused: true },
         ],
         dim: true,
         highlight: true,
         collapse: false,
         hovered: { side: 'head', startLine: 20, endLine: 21 },
      });
   });
});

function noDecorations() {
   return filePassageDecorations([], { base: null, head: null }, on);
}

describe('thread spots', () => {
   const thread = (id: string, side: FileSide, path: string, startLine: number, endLine: number) => ({ id, side, path, startLine, endLine, label: `thread ${id}` });

   it('keeps the threads into the file at their side, by a renamed file’s old path at the base', () => {
      const decorations = withThreads(noDecorations(), { base: 'src/old.ts', head: 'src/new.ts' }, [
         thread('a', 'head', 'src/new.ts', 1, 2),
         thread('b', 'base', 'src/old.ts', 3, 3),
         thread('c', 'base', 'src/new.ts', 4, 4),
         thread('d', 'head', 'src/other.ts', 1, 1),
      ]);

      expect(decorations.threads).toEqual([
         { id: 'a', side: 'head', startLine: 1, endLine: 2, label: 'thread a' },
         { id: 'b', side: 'base', startLine: 3, endLine: 3, label: 'thread b' },
      ]);
   });

   it('returns the same decorations when no thread is in the file, so the editors are not decorated again', () => {
      const decorations = noDecorations();

      expect(withThreads(decorations, { base: null, head: 'a.ts' }, [thread('a', 'base', 'a.ts', 1, 1)])).toBe(decorations);
   });

   it('moves an unchanged file’s threads to one side', () => {
      const decorations = withThreads(noDecorations(), { base: 'a.ts', head: 'a.ts' }, [thread('a', 'base', 'a.ts', 2, 2)]);

      expect(atSide(decorations, 'head').threads).toEqual([{ id: 'a', side: 'head', startLine: 2, endLine: 2, label: 'thread a' }]);
   });
});
