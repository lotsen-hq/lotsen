import { Text } from '@codemirror/state';
import { describe, expect, it } from 'vitest';
import { editorHoveredLines, editorLineMarks, editorThreadMarks, inlineBaseLine, selectedLines, sideLines, type ChunkSpan, type LineMark, type SideLines } from '../src/lib/editor/editorLines';
import type { DecoratedPassage } from '../src/lib/passageMarks';
import type { FileSide } from '../src/lib/types';

function lines(raw: string): SideLines {
   return sideLines(raw, Text.of(raw.replace(/\r\n?/g, '\n').split('\n')));
}

function passage(side: FileSide, startLine: number, endLine: number, role: DecoratedPassage['role'] = 'primary', label?: string): DecoratedPassage {
   return { side, startLine, endLine, role, ...(label === undefined ? {} : { label }) };
}

function listed(marks: ReadonlyMap<number, LineMark>): [number, string, string[]][] {
   return [...marks].sort(([a], [b]) => a - b).map(([line, mark]) => [line, mark.role, mark.labels.map((label) => `${label.role}:${label.text}`)]);
}

describe('editorHoveredLines', () => {
   it('places the lines at the editor’s side, and none at the other side', () => {
      const content = { kind: 'side', side: 'head', lines: lines('one\ntwo\rstill two\nthree\n') } as const;

      expect([...editorHoveredLines(content, { side: 'head', startLine: 2, endLine: 3 }).lines].sort()).toEqual([2, 3, 4]);
      expect(editorHoveredLines(content, { side: 'base', startLine: 2, endLine: 3 }).lines.size).toBe(0);
   });

   it('places base lines inline on a deleted chunk’s lines and on the head lines that equal them', () => {
      // The head replaces the base's lines 2 and 3 with one line.
      const base = 'keep 1\ngone 2\ngone 3\nkeep 4\n';
      const head = 'keep 1\nnew 2\nkeep 4\n';
      const content = { kind: 'inline', base: lines(base), head: lines(head), chunks: [{ fromA: 7, toA: 21, fromB: 7, toB: 13 }] } as const;

      const hovered = editorHoveredLines(content, { side: 'base', startLine: 3, endLine: 4 });

      expect([...hovered.lines]).toEqual([3]);
      expect([...hovered.deleted].map(([chunk, deleted]) => [chunk, [...deleted]])).toEqual([[0, [1]]]);
   });
});

describe('editorLineMarks of one side', () => {
   const text = 'one\ntwo\nthree\nfour\nfive\n';

   it('marks the lines of each passage at its side, with the primary role where passages overlap, and each label on its passage’s first line', () => {
      const marks = editorLineMarks({ kind: 'side', side: 'head', lines: lines(text) }, [
         passage('head', 2, 3, 'primary', 'the call'),
         passage('head', 3, 4, 'related', 'the caller'),
         passage('base', 1, 5),
      ]);

      expect(listed(marks.lines)).toEqual([
         [2, 'primary', ['primary:the call']],
         [3, 'primary', ['related:the caller']],
         [4, 'related', []],
      ]);
      expect(marks.deleted.size).toBe(0);
      expect([...marks.lines.values()].some((mark) => mark.focused)).toBe(false);
   });

   it('marks the lines of a focused passage as focused, also where an unfocused passage overlaps it', () => {
      const marks = editorLineMarks({ kind: 'side', side: 'head', lines: lines(text) }, [passage('head', 1, 2), { ...passage('head', 2, 3, 'related'), focused: true }]);

      expect([...marks.lines].sort(([a], [b]) => a - b).map(([line, mark]) => [line, mark.focused])).toEqual([
         [1, false],
         [2, true],
         [3, true],
      ]);
   });

   it.each([
      ['CRLF', 'one\r\ntwo\r\nthree\r\nfour\r\n'],
      ['LF', 'one\ntwo\nthree\nfour\n'],
   ])('marks the same lines of a text with %s line breaks', (_, raw) => {
      expect(listed(editorLineMarks({ kind: 'side', side: 'base', lines: lines(raw) }, [passage('base', 2, 4)]).lines).map(([line]) => line)).toEqual([2, 3, 4]);
   });

   it('marks both editor lines of a git line that holds a lone CR', () => {
      const marks = editorLineMarks({ kind: 'side', side: 'head', lines: lines('one\ntwo\rstill two\nthree\n') }, [passage('head', 2, 2, 'primary', 'split')]);

      expect(listed(marks.lines)).toEqual([
         [2, 'primary', ['primary:split']],
         [3, 'primary', []],
      ]);
   });

   it.each([
      ['with', 'one\ntwo\n'],
      ['without', 'one\ntwo'],
   ])('marks the last line of a text %s a break after it, and not the empty line the editor shows after a break', (_, raw) => {
      expect([...editorLineMarks({ kind: 'side', side: 'head', lines: lines(raw) }, [passage('head', 2, 2)]).lines.keys()]).toEqual([2]);
   });

   it('stops at the last line of a passage that runs past the text', () => {
      expect([...editorLineMarks({ kind: 'side', side: 'head', lines: lines('one\ntwo\n') }, [passage('head', 2, 9)]).lines.keys()]).toEqual([2]);
   });
});

describe('the inline view', () => {
   // One head line replaces the base's lines 2 to 4, and the head inserts a line after the base's line 6.
   const base = 'keep 1\ngone 2\ngone 3\ngone 4\nkeep 5\nkeep 6\nkeep 7\n';
   const head = 'keep 1\nnew 2\nkeep 5\nkeep 6\nadded\nkeep 7\n';
   const chunks: ChunkSpan[] = [
      { fromA: 7, toA: 28, fromB: 7, toB: 13 },
      { fromA: 42, toA: 42, fromB: 27, toB: 33 },
   ];

   describe('inlineBaseLine', () => {
      it.each<[number, ReturnType<typeof inlineBaseLine>]>([
         [1, { headLine: 1 }],
         [2, { chunk: 0, line: 0 }],
         [4, { chunk: 0, line: 2 }],
         [5, { headLine: 3 }],
         [7, { headLine: 6 }],
      ])('places the base line %i on a head line or a line of a deleted chunk', (line, place) => {
         expect(inlineBaseLine(lines(base), lines(head), chunks, line)).toEqual(place);
      });

      it('counts the deleted chunk’s editor lines when a deleted line holds a lone CR', () => {
         const crBase = 'keep\na\rb\nc\nkeep\n';
         const crHead = 'keep\nkeep\n';
         const deletion = [{ fromA: 5, toA: 11, fromB: 5, toB: 5 }];

         expect(inlineBaseLine(lines(crBase), lines(crHead), deletion, 3)).toEqual({ chunk: 0, line: 2 });
      });
   });

   describe('editorLineMarks', () => {
      it('marks a base passage inside a deleted chunk on the chunk’s lines, with its label on the first one', () => {
         const marks = editorLineMarks({ kind: 'inline', base: lines(base), head: lines(head), chunks }, [passage('base', 3, 4, 'related', 'old')]);

         expect(marks.lines.size).toBe(0);
         expect([...marks.deleted.keys()]).toEqual([0]);
         expect(listed(marks.deleted.get(0)!)).toEqual([
            [1, 'related', ['related:old']],
            [2, 'related', []],
         ]);
      });

      it('marks a base passage of unchanged lines on the head lines that equal them', () => {
         const marks = editorLineMarks({ kind: 'inline', base: lines(base), head: lines(head), chunks }, [passage('base', 6, 7, 'primary', 'kept')]);

         expect(listed(marks.lines)).toEqual([
            [4, 'primary', ['primary:kept']],
            [6, 'primary', []],
         ]);
      });

      it('marks a base passage across a chunk edge on both, and a head passage on the head’s lines', () => {
         const marks = editorLineMarks({ kind: 'inline', base: lines(base), head: lines(head), chunks }, [passage('base', 1, 2, 'related', 'start'), passage('head', 2, 3, 'primary')]);

         expect(listed(marks.lines)).toEqual([
            [1, 'related', ['related:start']],
            [2, 'primary', []],
            [3, 'primary', []],
         ]);
         expect(listed(marks.deleted.get(0)!)).toEqual([[0, 'related', []]]);
      });

      it('marks no base passage when the file has no base', () => {
         const marks = editorLineMarks({ kind: 'inline', base: null, head: lines(head), chunks: [{ fromA: 0, toA: 0, fromB: 0, toB: head.length }] }, [passage('base', 1, 2)]);

         expect(marks.lines.size + marks.deleted.size).toBe(0);
      });
   });
});

describe('selectedLines', () => {
   it('gives the git lines of one side, counting a CRLF as one line break and a lone CR as none', () => {
      expect(selectedLines({ kind: 'side', side: 'base', lines: lines('one\r\ntwo\r\nthree\r\n') }, { chunk: null, line: 2 }, { chunk: null, line: 3 })).toEqual({
         side: 'base',
         startLine: 2,
         endLine: 3,
      });
      expect(selectedLines({ kind: 'side', side: 'head', lines: lines('one\ntwo\rstill two\nthree\n') }, { chunk: null, line: 3 }, { chunk: null, line: 4 })).toEqual({
         side: 'head',
         startLine: 2,
         endLine: 3,
      });
   });

   describe('inline', () => {
      // One head line replaces the base's lines 2 to 4, and the head inserts a line after the base's line 6.
      const base = 'keep 1\ngone 2\ngone 3\ngone 4\nkeep 5\nkeep 6\nkeep 7\n';
      const head = 'keep 1\nnew 2\nkeep 5\nkeep 6\nadded\nkeep 7\n';
      const content = {
         kind: 'inline',
         base: lines(base),
         head: lines(head),
         chunks: [
            { fromA: 7, toA: 28, fromB: 7, toB: 13 },
            { fromA: 42, toA: 42, fromB: 27, toB: 33 },
         ],
      } as const;

      it.each<[string, [number | null, number], [number | null, number], ReturnType<typeof selectedLines>]>([
         ['head lines, unchanged or added, with no deleted line between them', [null, 3], [null, 6], { side: 'head', startLine: 3, endLine: 6 }],
         ['deleted lines', [0, 1], [0, 2], { side: 'base', startLine: 3, endLine: 4 }],
         ['an unchanged line and the deleted lines below it', [null, 1], [0, 0], { side: 'base', startLine: 1, endLine: 2 }],
         ['deleted lines and the inserted line that replaces them', [0, 2], [null, 3], null],
         ['an unchanged line, a deleted chunk and its inserted line', [null, 1], [null, 2], null],
      ])('as %s', (_, [firstChunk, firstLine], [lastChunk, lastLine], expected) => {
         expect(selectedLines(content, { chunk: firstChunk, line: firstLine }, { chunk: lastChunk, line: lastLine })).toEqual(expected);
      });

      it('gives base lines for unchanged lines around a deletion, which hold deleted lines between them', () => {
         const deletion = { kind: 'inline', base: lines('a\nb\nc\nd\n'), head: lines('a\nd\n'), chunks: [{ fromA: 2, toA: 6, fromB: 2, toB: 2 }] } as const;

         expect(selectedLines(deletion, { chunk: null, line: 1 }, { chunk: null, line: 2 })).toEqual({ side: 'base', startLine: 1, endLine: 4 });
         expect(selectedLines(deletion, { chunk: null, line: 2 }, { chunk: null, line: 2 })).toEqual({ side: 'head', startLine: 2, endLine: 2 });
      });

      it('gives base lines for lines deleted at the end of the file', () => {
         const deletion = { kind: 'inline', base: lines('a\nb\nc\n'), head: lines('a\n'), chunks: [{ fromA: 2, toA: 6, fromB: 2, toB: 2 }] } as const;

         expect(selectedLines(deletion, { chunk: 0, line: 0 }, { chunk: 0, line: 1 })).toEqual({ side: 'base', startLine: 2, endLine: 3 });
         expect(selectedLines(deletion, { chunk: null, line: 1 }, { chunk: 0, line: 1 })).toEqual({ side: 'base', startLine: 1, endLine: 3 });
      });

      it('gives head lines for lines added at the end of the file, and in a file without a base', () => {
         const addition = { kind: 'inline', base: lines('a\n'), head: lines('a\nb\nc\n'), chunks: [{ fromA: 2, toA: 2, fromB: 2, toB: 6 }] } as const;
         const added = { kind: 'inline', base: null, head: lines('a\nb\n'), chunks: [{ fromA: 0, toA: 0, fromB: 0, toB: 4 }] } as const;

         expect(selectedLines(addition, { chunk: null, line: 2 }, { chunk: null, line: 3 })).toEqual({ side: 'head', startLine: 2, endLine: 3 });
         expect(selectedLines(added, { chunk: null, line: 1 }, { chunk: null, line: 2 })).toEqual({ side: 'head', startLine: 1, endLine: 2 });
      });

      it('maps unchanged lines after a chunk to the base by git lines when CRLF line breaks shorten the documents', () => {
         const crlf = (text: string) => text.replaceAll('\n', '\r\n');
         const crlfContent = { ...content, base: lines(crlf(base)), head: lines(crlf(head)) };

         expect(selectedLines(crlfContent, { chunk: 0, line: 2 }, { chunk: 0, line: 2 })).toEqual({ side: 'base', startLine: 4, endLine: 4 });
         expect(selectedLines(crlfContent, { chunk: 0, line: 0 }, { chunk: 0, line: 1 })).toEqual({ side: 'base', startLine: 2, endLine: 3 });
      });
   });
});

describe('thread spots', () => {
   it('marks a thread on the first editor line it shows and covers each of its lines, inline in a deleted chunk too', () => {
      const base = 'keep 1\ngone 2\ngone 3\nkeep 4\n';
      const head = 'keep 1\nnew 2\nkeep 4\n';
      const content = { kind: 'inline', base: lines(base), head: lines(head), chunks: [{ fromA: 7, toA: 21, fromB: 7, toB: 13 }] } as const;
      const spots = [
         { id: 'a', side: 'base', startLine: 3, endLine: 4, label: 'a' },
         { id: 'b', side: 'head', startLine: 2, endLine: 3, label: 'b' },
         { id: 'c', side: 'head', startLine: 2, endLine: 2, label: 'c' },
      ] as const;

      const marks = editorThreadMarks(content, spots);

      expect([...marks.lines].map(([line, threads]) => [line, threads.map((spot) => spot.id)])).toEqual([[2, ['b', 'c']]]);
      expect([...marks.deleted].map(([chunk, deleted]) => [chunk, [...deleted].map(([line, threads]) => [line, threads.map((spot) => spot.id)])])).toEqual([[0, [[1, ['a']]]]]);
      expect([...marks.covered.lines].sort()).toEqual([2, 3]);
      expect([...marks.covered.deleted.get(0)!]).toEqual([1]);
   });
});
