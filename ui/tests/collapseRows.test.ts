import { describe, expect, it } from 'vitest';
import { collapsedRegions, expandedRows, inlineRows, sideBySideRows, singleRows, type ChunkLines, type CollapsedRegion, type RowLayout } from '../src/lib/editor/collapseRows';

const options = { context: 2, minLines: 3 };

/** The regions as each editor's hidden lines, `from-to:hidden`, or `-` for an editor that draws none of them. */
function shown(regions: readonly CollapsedRegion[]): string[][] {
   return regions.map((region) => region.parts.map((part) => (part === null ? '-' : `${part.lines.from}-${part.lines.to}:${part.hidden}`)));
}

/** The rows that show the given lines of one editor. */
function rowsOf(layout: RowLayout, editor: number, ...lines: number[]): number[] {
   return lines.map((line) => layout.rowOf[editor]![line]!);
}

function chunk(a: [number, number], b: [number, number]): ChunkLines {
   return { a: { from: a[0], to: a[1] }, b: { from: b[0], to: b[1] } };
}

describe('collapsedRegions of one editor', () => {
   const layout = singleRows(30);

   it('collapses the lines outside the focused lines and their context', () => {
      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 10, 11), [], options))).toEqual([['1-7:7'], ['14-30:17']]);
   });

   it('collapses the whole file when nothing is focused', () => {
      expect(shown(collapsedRegions(layout, [], [], options))).toEqual([['1-30:30']]);
   });

   it('leaves a region shorter than the fewest lines shown', () => {
      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 6, 26), [], options))).toEqual([['1-3:3'], ['9-23:15']]);
      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 5, 26), [], options))).toEqual([['8-23:16']]);
   });

   it('shows the revealed rows without context, and splits a region around them', () => {
      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 3), rowsOf(layout, 0, 15, 16), options))).toEqual([['6-14:9'], ['17-30:14']]);
   });
});

describe('expandedRows', () => {
   const region: CollapsedRegion = { rows: { from: 10, to: 40 }, parts: [] };

   it('gives a step of rows from the top or the bottom, or every row', () => {
      expect(expandedRows(region, 'top', 20)).toEqual({ from: 10, to: 29 });
      expect(expandedRows(region, 'bottom', 20)).toEqual({ from: 21, to: 40 });
      expect(expandedRows(region, 'all', 20)).toEqual({ from: 10, to: 40 });
      expect(expandedRows(region, 'top', 50)).toEqual({ from: 10, to: 40 });
   });

   it('leaves a region that collapses again with the rest of it', () => {
      const layout = singleRows(60);
      const [region] = collapsedRegions(layout, rowsOf(layout, 0, 5), [], options);
      const top = expandedRows(region!, 'top', 20);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 5), rangeRows(top), options))).toEqual([['28-60:33']]);
   });
});

function rangeRows({ from, to }: { from: number; to: number }): number[] {
   return Array.from({ length: to - from + 1 }, (_, index) => from + index);
}

describe('collapsedRegions side by side', () => {
   it('collapses the same lines in both panes of an unchanged file', () => {
      const layout = sideBySideRows({ a: 20, b: 20 }, []);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 1, 10), [], options))).toEqual([
         ['1-7:7', '1-7:7'],
         ['13-20:8', '13-20:8'],
      ]);
   });

   it('shows the lines of a focus in one pane in the other, where they equal each other', () => {
      // The base's lines 5 and 6 are the head's lines 5 to 8, so the base's line 15 is the head's line 17.
      const layout = sideBySideRows({ a: 20, b: 22 }, [chunk([5, 6], [5, 8])]);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 15), [], options))).toEqual([
         ['1-12:12', '1-14:14'],
         ['18-20:3', '20-22:3'],
      ]);
   });

   it('shows the lines at the same height of a chunk in both panes, as the merge view aligns them', () => {
      // The base's lines 10 to 13 change into the head's lines 10 to 19. The head's line 12 is the chunk's third line, beside the base's line 12.
      const layout = sideBySideRows({ a: 30, b: 36 }, [chunk([10, 13], [10, 19])]);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 1, 12), [], options))).toEqual([
         ['1-9:9', '1-9:9'],
         ['14-30:17', '15-36:22'],
      ]);
   });

   it('collapses a chunk with the unchanged lines around it, so each region starts and ends on lines that face each other', () => {
      // The head inserts lines 11 to 15, which the base lacks.
      const layout = sideBySideRows({ a: 30, b: 35 }, [chunk([11, 10], [11, 15])]);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 2, 29), [], options))).toEqual([['5-26:22', '5-31:27']]);
   });

   it('collapses rows of a chunk that only one pane draws in that pane alone', () => {
      // The base's lines 10 and 11 change into the head's lines 10 to 29.
      const layout = sideBySideRows({ a: 20, b: 38 }, [chunk([10, 11], [10, 29])]);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 1, 10, 31), [], options))).toEqual([
         ['1-7:7', '1-7:7'],
         ['-', '13-28:16'],
         ['16-20:5', '34-38:5'],
      ]);
   });

   it('collapses an added file in the head pane alone, leaving the base pane its one empty line', () => {
      const layout = sideBySideRows({ a: 1, b: 40 }, [chunk([1, 0], [1, 39])], false);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 1, 20), [], options))).toEqual([
         ['-', '1-17:17'],
         ['-', '23-40:18'],
      ]);
   });

   it('collapses a deleted file in the base pane alone', () => {
      const layout = sideBySideRows({ a: 40, b: 1 }, [chunk([1, 39], [1, 0])], false);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 20), [], options))).toEqual([
         ['1-17:17', '-'],
         ['23-40:18', '-'],
      ]);
   });

   it('places the lines after a last chunk that reaches the end of the base right below each pane’s own chunk lines, as the merge view leaves them unaligned', () => {
      // The head appends lines 31 to 60. The base's empty last line follows its line 30, beside the head's line 31.
      const layout = sideBySideRows({ a: 31, b: 61 }, [chunk([31, 30], [31, 60])], false);

      expect(rowsOf(layout, 0, 30, 31)).toEqual(rowsOf(layout, 1, 30, 31));
      expect(shown(collapsedRegions(layout, rowsOf(layout, 1, 33), [], options))).toEqual([
         ['1-30:30', '1-30:30'],
         ['-', '36-61:26'],
      ]);
   });

   it('pairs the lines after a last chunk that ends before the end of the base', () => {
      // The head's lines 38 to 40 replace the base's line 38, and the base's lines 39 to 41 equal the head's lines 41 to 43.
      const layout = sideBySideRows({ a: 41, b: 43 }, [chunk([38, 38], [38, 40])]);

      expect(rowsOf(layout, 0, 39, 41)).toEqual(rowsOf(layout, 1, 41, 43));
      expect(shown(collapsedRegions(layout, rowsOf(layout, 1, 38), [], options))).toEqual([
         ['1-35:35', '1-35:35'],
         ['39-41:3', '41-43:3'],
      ]);
   });

   it('lays out a last chunk on the last line of texts without a final line break', () => {
      const layout = sideBySideRows({ a: 2, b: 3 }, [chunk([2, 2], [2, 3])], false);

      expect([...layout.editors[0]!]).toEqual([1, 2, -1]);
      expect([...layout.editors[1]!]).toEqual([1, 2, 3]);
   });
});

describe('collapsedRegions inline', () => {
   it('hides the deleted chunks inside a region and counts their lines', () => {
      // Three deleted lines sit before the head's line 10.
      const layout = inlineRows(20, [chunk([10, 12], [10, 9])]);

      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 2, 19), [], options))).toEqual([['5-16:15']]);
   });

   it('leaves the deleted lines at either end of a region shown', () => {
      // Deleted lines before the head's lines 6 and 15.
      const layout = inlineRows(20, [chunk([6, 7], [6, 5]), chunk([17, 18], [15, 14])]);

      // The context of line 2 ends at line 4, line 5 starts the region, and the region ends at line 14, before the deleted lines of line 15.
      const regions = collapsedRegions(layout, rowsOf(layout, 0, 2, 17), [], options);

      expect(shown(regions)).toEqual([['5-14:12']]);
      expect(regions[0]!.rows).toEqual({ from: rowsOf(layout, 0, 5)[0], to: rowsOf(layout, 0, 14)[0] });
   });

   it('shows a whole deleted chunk with context around it when a focus falls in it, and without when a revealed row does', () => {
      const layout = inlineRows(30, [chunk([10, 19], [10, 9])]);
      const chunkRows = layout.groups[0]!;

      expect(shown(collapsedRegions(layout, [chunkRows.from + 5], [], options))).toEqual([['1-7:7'], ['12-30:19']]);
      expect(shown(collapsedRegions(layout, rowsOf(layout, 0, 1), [chunkRows.to], options))).toEqual([['4-9:6'], ['10-30:21']]);
   });

   it('places a chunk at the end of the file before the last line', () => {
      const layout = inlineRows(3, [chunk([3, 5], [3, 2])]);

      expect([...layout.editors[0]!]).toEqual([1, 2, 0, 0, 0, 3]);
      expect(layout.groups).toEqual([{ from: 2, to: 4 }]);
   });
});
