/** A run of rows of a layout, 0-based and inclusive. */
export interface RowSpan {
   from: number;
   to: number;
}

/** A run of an editor's lines, 1-based and inclusive. It is empty when `to` is less than `from`, and then sits before the line `from`. */
export interface LineSpan {
   from: number;
   to: number;
}

/**
 * The rows that a layout shows from top to bottom, and the line each of its editors draws in each row.
 *
 * Side by side, a row holds a line of each pane at the same height: in an unchanged region the lines that equal each other, in a changed chunk the lines at the same distance from the chunk's
 * start, because the merge view aligns both panes at the start of each chunk and of each unchanged region after it. Inline, a row is a head line or one line of a deleted chunk.
 */
export interface RowLayout {
   rows: number;
   /** Per editor, by row: the 1-based line the row shows, 0 when the editor draws the row without a line of its own (a deleted line inline), or -1 when it does not draw the row. */
   editors: readonly Int32Array[];
   /** Per editor, by 1-based line: the row that shows the line. */
   rowOf: readonly Int32Array[];
   /** Runs of rows that show or hide together: inline, the deleted lines of each chunk, by the chunk's index. */
   groups: readonly RowSpan[];
}

/** A changed chunk as the lines it covers in the base's document and the head's. */
export interface ChunkLines {
   a: LineSpan;
   b: LineSpan;
}

/** Returns the layout of one editor of a text with the given number of lines. */
export function singleRows(lines: number): RowLayout {
   const builder = new RowBuilder([lines]);
   for (let line = 1; line <= lines; line++) {
      builder.row([line]);
   }

   return builder.finish();
}

/**
 * Returns the layout of a merge view whose panes hold the given numbers of lines, with its chunks in document order.
 *
 * @param endAligned Whether the merge view aligns the lines after the last chunk, which it does only when the chunk ends before the end of the base's document. Otherwise those lines follow each
 * pane's own chunk lines, so they count as part of the chunk.
 */
export function sideBySideRows(lines: { a: number; b: number }, chunks: readonly ChunkLines[], endAligned = true): RowLayout {
   const builder = new RowBuilder([lines.a, lines.b]);
   const pair = (fromA: number, countA: number, fromB: number, countB: number) => {
      for (let offset = 0; offset < Math.max(countA, countB); offset++) {
         builder.row([offset < countA ? fromA + offset : -1, offset < countB ? fromB + offset : -1]);
      }
   };

   let nextA = 1;
   let nextB = 1;
   for (const [index, { a, b }] of chunks.entries()) {
      pair(nextA, a.from - nextA, nextB, b.from - nextB);
      const last = !endAligned && index === chunks.length - 1;
      pair(a.from, last ? lines.a - a.from + 1 : spanLength(a), b.from, last ? lines.b - b.from + 1 : spanLength(b));
      nextA = last ? lines.a + 1 : a.from + spanLength(a);
      nextB = last ? lines.b + 1 : b.from + spanLength(b);
   }

   pair(nextA, lines.a - nextA + 1, nextB, lines.b - nextB + 1);
   return builder.finish();
}

/** Returns the layout of an inline view whose head holds the given number of lines, with its chunks in document order: each chunk's deleted lines come before the head line it sits at. */
export function inlineRows(headLines: number, chunks: readonly ChunkLines[]): RowLayout {
   const builder = new RowBuilder([headLines]);
   const groups: RowSpan[] = [];
   let chunk = 0;
   const deleted = (beforeLine: number) => {
      while (chunk < chunks.length && Math.min(chunks[chunk]!.b.from, headLines) <= beforeLine) {
         const from = builder.rows;
         for (let line = 0; line < spanLength(chunks[chunk]!.a); line++) {
            builder.row([0]);
         }

         groups.push({ from, to: builder.rows - 1 });
         chunk++;
      }
   };

   for (let line = 1; line <= headLines; line++) {
      deleted(line);
      builder.row([line]);
   }

   deleted(Number.MAX_SAFE_INTEGER);
   return builder.finish(groups);
}

export interface CollapseOptions {
   /** The rows around each focused row that stay shown. */
   context: number;
   /** The fewest rows that a marker hides in its editor, which inline counts deleted lines toward. A shorter region shows its lines. */
   minLines: number;
}

/** One editor's part of a collapsed region: the lines its marker replaces, and the number of rows it hides, which inline includes the deleted lines between them. */
export interface CollapsedPart {
   lines: LineSpan;
   hidden: number;
}

/** Rows that the layout hides behind a marker in each editor that draws them. */
export interface CollapsedRegion {
   rows: RowSpan;
   /** Per editor, its part, or null when the editor draws none of the rows. */
   parts: readonly (CollapsedPart | null)[];
}

/**
 * Returns the regions that a layout collapses: the rows outside the focused rows and their context and outside the revealed rows, in row order.
 *
 * A region starts and ends on rows that hold a line in every editor that draws them, because a marker replaces whole lines of an editor. Inline, the deleted lines at either end of a region
 * therefore stay shown, and the deleted chunks inside it hide with it.
 */
export function collapsedRegions(layout: RowLayout, focus: Iterable<number>, revealed: Iterable<number>, options: CollapseOptions): CollapsedRegion[] {
   const focused = new Uint8Array(layout.rows);
   for (const row of focus) {
      focused[row] = 1;
   }

   // A focus in a group focuses the whole group, so the context surrounds the group.
   fillGroups(layout, focused);
   const shown = new Uint8Array(layout.rows);
   focused.forEach((value, row) => value && shown.fill(1, Math.max(row - options.context, 0), Math.min(row + options.context + 1, layout.rows)));
   for (const row of revealed) {
      if (row >= 0 && row < layout.rows) {
         shown[row] = 1;
      }
   }

   fillGroups(layout, shown);

   const lineless = (row: number) => layout.editors.some((rows) => rows[row] === 0);
   const regions: CollapsedRegion[] = [];
   for (let row = 0; row < layout.rows; row++) {
      if (shown[row]) {
         continue;
      }

      let from = row;
      while (row + 1 < layout.rows && !shown[row + 1]) {
         row++;
      }

      let to = row;
      while (from <= to && lineless(from)) {
         from++;
      }

      while (to >= from && lineless(to)) {
         to--;
      }

      // A short part that ends the editor's document stays shown, because no line of that editor follows it for a marker to keep aligned.
      const parts = layout.editors.map((rows, editor) => {
         const part = collapsedPart(rows, from, to);
         return part !== null && part.hidden < options.minLines && part.lines.to === layout.rowOf[editor]!.length - 1 ? null : part;
      });
      if (parts.some((part) => part !== null && part.hidden >= options.minLines)) {
         regions.push({ rows: { from, to }, parts });
      }
   }

   return regions;
}

/** Which rows of a collapsed region expanding it shows: a number of rows from its top or its bottom, or all of them. */
export type ExpandEdge = 'top' | 'bottom' | 'all';

/** Returns the rows of a region that expanding it at one edge shows. */
export function expandedRows(region: CollapsedRegion, edge: ExpandEdge, step: number): RowSpan {
   const { from, to } = region.rows;
   return edge === 'top' ? { from, to: Math.min(to, from + step - 1) } : edge === 'bottom' ? { from: Math.max(from, to - step + 1), to } : { from, to };
}

function fillGroups(layout: RowLayout, rows: Uint8Array): void {
   for (const group of layout.groups) {
      if (rows.subarray(group.from, group.to + 1).includes(1)) {
         rows.fill(1, group.from, group.to + 1);
      }
   }
}

function collapsedPart(rows: Int32Array, from: number, to: number): CollapsedPart | null {
   let first = 0;
   let last = 0;
   let hidden = 0;
   for (let row = from; row <= to; row++) {
      const line = rows[row]!;
      if (line > 0) {
         first ||= line;
         last = line;
      }

      if (line >= 0) {
         hidden++;
      }
   }

   return first === 0 ? null : { lines: { from: first, to: last }, hidden };
}

function spanLength(span: LineSpan): number {
   return Math.max(span.to - span.from + 1, 0);
}

class RowBuilder {
   private readonly lines: number[][];
   private readonly rowOf: Int32Array[];

   constructor(lineCounts: readonly number[]) {
      this.lines = lineCounts.map(() => []);
      this.rowOf = lineCounts.map((count) => new Int32Array(count + 1).fill(-1));
   }

   get rows(): number {
      return this.lines[0]!.length;
   }

   row(lines: readonly number[]): void {
      const row = this.rows;
      lines.forEach((line, editor) => {
         if (line > 0) {
            this.rowOf[editor]![line] = row;
         }

         this.lines[editor]!.push(line);
      });
   }

   finish(groups: readonly RowSpan[] = []): RowLayout {
      return { rows: this.rows, editors: this.lines.map((lines) => Int32Array.from(lines)), rowOf: this.rowOf, groups };
   }
}
