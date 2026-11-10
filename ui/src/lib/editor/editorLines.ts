import type { Text } from '@codemirror/state';
import { gitLineAt, gitLineStarts } from '../gitLines';
import type { DecoratedPassage, LinesAtSide, ThreadSpot } from '../passageMarks';
import type { FileSide, PassageRole } from '../types';

/** One side's text as an editor's document holds it, with the document offset where each of its git lines starts. */
export interface SideLines {
   starts: readonly number[];
   doc: Text;
}

/**
 * Returns the git lines of a text in an editor's document of it.
 *
 * @param raw The text as the server sent it.
 * @param doc The editor's document, which splits lines at CRLF, CR and LF alike.
 */
export function sideLines(raw: string, doc: Text): SideLines {
   return { starts: gitLineStarts(raw), doc };
}

/** A changed region of the inline view, in the shape of the merge package's `Chunk`: offsets into the base's document and the head's. */
export interface ChunkSpan {
   fromA: number;
   toA: number;
   fromB: number;
   toB: number;
}

/** Where the inline view shows a base line: the 0-based line of a deleted chunk, by the chunk's index, or else the 1-based git line of the head that equals it. */
export type BaseLinePlace = { chunk: number; line: number } | { headLine: number };

/** Returns where the inline view shows a 1-based git line of the base, as {@link BaseLinePlace} describes. */
export function inlineBaseLine(base: SideLines, head: SideLines, chunks: readonly ChunkSpan[], line: number): BaseLinePlace {
   const baseLine = clampLine(base, line);
   const offset = base.starts[baseLine - 1]!;
   // Outside the chunks each base line equals one head line, so the lines after a chunk keep their distance from its end on both sides, whatever their lengths.
   // The distance counts git's lines, because a line pair that whitespace makes equal can hold a lone CR, which splits a line in the editor alone.
   let headLine = baseLine;
   for (const [index, chunk] of chunks.entries()) {
      if (offset < chunk.fromA) {
         break;
      }

      // The deletion widget draws the base's editor lines, so the deleted line counts them.
      if (offset < chunk.toA) {
         return { chunk: index, line: base.doc.lineAt(offset).number - base.doc.lineAt(chunk.fromA).number };
      }

      headLine = baseLine + lineShift(base, head, chunk);
   }

   return { headLine: clampLine(head, headLine) };
}

/** Returns the 1-based git line of the base that an unchanged head line of the inline view equals, as {@link inlineBaseLine} pairs them. */
function inlineUnchangedBaseLine(base: SideLines, head: SideLines, chunks: readonly ChunkSpan[], headLine: number): number {
   const offset = head.starts[headLine - 1]!;
   let baseLine = headLine;
   for (const chunk of chunks) {
      if (offset < chunk.fromB) {
         break;
      }

      baseLine = headLine - lineShift(base, head, chunk);
   }

   return clampLine(base, baseLine);
}

/** Returns how many git lines further down the head an unchanged line after a chunk is than the base line it equals. */
function lineShift(base: SideLines, head: SideLines, chunk: ChunkSpan): number {
   return gitLineAt(head.starts, Math.min(chunk.toB, head.doc.length)) - gitLineAt(base.starts, Math.min(chunk.toA, base.doc.length));
}

/** A line that an editor draws: a 1-based line of its document with a null chunk, or a 0-based line of a deleted chunk by the chunk's index among the inline view's chunks. */
export interface EditorPlace {
   chunk: number | null;
   line: number;
}

/**
 * Returns the git lines at one side that the editor lines from `first` to `last` show, both included and in the order the editor draws them, or null when they show lines of both sides.
 *
 * Inline, lines without a deleted line among them are head lines. Lines with one are base lines when every other line is unchanged, since an unchanged line equals a base line, and else span both sides.
 */
export function selectedLines(content: EditorContent, first: EditorPlace, last: EditorPlace): LinesAtSide | null {
   if (content.kind === 'side') {
      const { side, lines } = content;
      return { side, startLine: gitLineOf(lines, first.line), endLine: gitLineOf(lines, last.line) };
   }

   const { base, head, chunks } = content;
   // A deleted chunk draws its lines above the head line at its start.
   const widgetLine = (chunk: ChunkSpan) => head.doc.lineAt(Math.min(chunk.fromB, head.doc.length)).number;
   const docFrom = first.chunk === null ? first.line : widgetLine(chunks[first.chunk]!);
   const docTo = last.chunk === null ? last.line : widgetLine(chunks[last.chunk]!) - 1;
   const holdsDeleted = first.chunk !== null || last.chunk !== null || chunks.some((chunk) => chunk.fromA < chunk.toA && widgetLine(chunk) > docFrom && widgetLine(chunk) <= docTo);
   if (!holdsDeleted) {
      return { side: 'head', startLine: gitLineOf(head, docFrom), endLine: gitLineOf(head, docTo) };
   }

   const changed = chunks.some((chunk) => chunk.fromB < chunk.toB && head.doc.lineAt(chunk.fromB).number <= docTo && head.doc.lineAt(Math.min(chunk.toB - 1, head.doc.length)).number >= docFrom);
   if (base === null || changed) {
      return null;
   }

   const baseLine = (place: EditorPlace) =>
      place.chunk === null ? inlineUnchangedBaseLine(base, head, chunks, gitLineOf(head, place.line)) : gitLineOf(base, base.doc.lineAt(chunks[place.chunk]!.fromA).number + place.line);
   return { side: 'base', startLine: baseLine(first), endLine: baseLine(last) };
}

/** Returns the 1-based git line that holds a 1-based line of the editor's document. */
function gitLineOf({ starts, doc }: SideLines, line: number): number {
   return gitLineAt(starts, doc.line(Math.min(Math.max(line, 1), doc.lines)).from);
}

/** What an editor shows: one side's text, or inline the head's text with the base's deleted lines in chunks, where the base is null when the file does not exist there. */
export type EditorContent = { kind: 'side'; side: FileSide; lines: SideLines } | { kind: 'inline'; base: SideLines | null; head: SideLines; chunks: readonly ChunkSpan[] };

export interface PassageLabel {
   text: string;
   role: PassageRole;
}

/** How a line inside one or more passages shows. */
export interface LineMark {
   /** Primary when any passage on the line is primary. */
   role: PassageRole;
   /** Whether a focused passage covers the line. */
   focused: boolean;
   /** The labels of the passages whose first line it is, in passage order. */
   labels: PassageLabel[];
}

/** The lines of one editor that passages cover. A line without a mark is outside every passage. */
export interface EditorLineMarks {
   /** By the 1-based line of the editor's document. */
   lines: Map<number, LineMark>;
   /** By the index of a deleted chunk among the inline view's chunks, then by the 0-based line of the chunk. Empty outside the inline view. */
   deleted: Map<number, Map<number, LineMark>>;
}

/** Returns the lines of one editor that the passages cover, as {@link EditorLineMarks} describes. */
export function editorLineMarks(content: EditorContent, passages: readonly DecoratedPassage[]): EditorLineMarks {
   const marks: EditorLineMarks = { lines: new Map(), deleted: new Map() };
   for (const passage of passages) {
      let first = true;
      placeLines(content, passage, (chunk, from, to) => {
         const target = chunk === null ? marks.lines : getOrAdd(marks.deleted, chunk, () => new Map<number, LineMark>());
         for (let line = from; line <= to; line++) {
            const known = target.get(line) ?? { role: passage.role, focused: false, labels: [] };
            known.role = known.role === 'primary' ? 'primary' : passage.role;
            known.focused ||= passage.focused === true;
            if (first && passage.label !== undefined) {
               known.labels.push({ text: passage.label, role: passage.role });
            }

            first = false;
            target.set(line, known);
         }
      });
   }

   return marks;
}

/** Lines of one editor: 1-based lines of its document, and by the index of a deleted chunk, 0-based lines of the chunk. */
export interface EditorLines {
   lines: Set<number>;
   deleted: Map<number, Set<number>>;
}

/** Returns the lines of one editor that show lines at a side, placed as {@link editorLineMarks} places a passage's. */
export function editorHoveredLines(content: EditorContent, lines: LinesAtSide): EditorLines {
   const found: EditorLines = { lines: new Set(), deleted: new Map() };
   placeLines(content, lines, (chunk, from, to) => {
      const target = chunk === null ? found.lines : getOrAdd(found.deleted, chunk, () => new Set<number>());
      for (let line = from; line <= to; line++) {
         target.add(line);
      }
   });
   return found;
}

/** Returns where one editor shows the last of the lines at a side, or null when it shows none. */
export function lastEditorLine(content: EditorContent, lines: LinesAtSide): EditorPlace | null {
   let last: EditorPlace | null = null;
   placeLines(content, lines, (chunk, _from, to) => {
      last = { chunk, line: to };
   });
   return last;
}

/** Where one editor marks threads: each thread on the first editor line that shows it, and the lines that show any thread. */
export interface EditorThreadMarks {
   /** By the 1-based line of the editor's document, in the order of the threads given. */
   lines: Map<number, ThreadSpot[]>;
   /** By the index of a deleted chunk, then by the 0-based line of the chunk. Empty outside the inline view. */
   deleted: Map<number, Map<number, ThreadSpot[]>>;
   covered: EditorLines;
}

/** Returns where one editor marks the threads, placed as {@link editorLineMarks} places a passage's lines. */
export function editorThreadMarks(content: EditorContent, threads: readonly ThreadSpot[]): EditorThreadMarks {
   const marks: EditorThreadMarks = { lines: new Map(), deleted: new Map(), covered: { lines: new Set(), deleted: new Map() } };
   for (const thread of threads) {
      let first = true;
      placeLines(content, thread, (chunk, from, to) => {
         if (first) {
            const target = chunk === null ? marks.lines : getOrAdd(marks.deleted, chunk, () => new Map<number, ThreadSpot[]>());
            getOrAdd(target, from, () => []).push(thread);
            first = false;
         }

         const covered = chunk === null ? marks.covered.lines : getOrAdd(marks.covered.deleted, chunk, () => new Set<number>());
         for (let line = from; line <= to; line++) {
            covered.add(line);
         }
      });
   }

   return marks;
}

/** Calls `visit` with each run of editor lines that show the lines at a side: lines of the document with a null chunk, or 0-based lines of a deleted chunk by its index. */
function placeLines(content: EditorContent, lines: LinesAtSide, visit: (chunk: number | null, from: number, to: number) => void): void {
   const own = content.kind === 'side' ? (content.side === lines.side ? content.lines : null) : lines.side === 'head' ? content.head : null;
   if (own !== null) {
      eachGitLine(own, lines, (from, to) => visit(null, from, to));
      return;
   }

   if (content.kind === 'side' || content.base === null) {
      return;
   }

   const { base, head, chunks } = content;
   for (let line = lines.startLine; line <= Math.min(lines.endLine, base.starts.length); line++) {
      const place = inlineBaseLine(base, head, chunks, line);
      if ('headLine' in place) {
         const [from, to] = editorLines(head, place.headLine);
         visit(null, from, to);
      } else {
         const chunk = chunks[place.chunk]!;
         const [from, to] = editorLines(base, line);
         const chunkLast = base.doc.lineAt(Math.min(chunk.toA - 1, base.doc.length)).number - base.doc.lineAt(chunk.fromA).number;
         visit(place.chunk, place.line, Math.min(place.line + to - from, chunkLast));
      }
   }
}

function getOrAdd<K, V>(map: Map<K, V>, key: K, create: () => V): V {
   let value = map.get(key);
   if (value === undefined) {
      value = create();
      map.set(key, value);
   }

   return value;
}

function clampLine(lines: SideLines, line: number): number {
   return Math.min(Math.max(line, 1), lines.starts.length);
}

function eachGitLine(lines: SideLines, at: LinesAtSide, visit: (from: number, to: number) => void): void {
   for (let line = at.startLine; line <= Math.min(at.endLine, lines.starts.length); line++) {
      const [from, to] = editorLines(lines, line);
      visit(from, to);
   }
}

/** Returns the first and the last 1-based editor line of a git line, which differ when the git line holds a lone CR. */
function editorLines({ starts, doc }: SideLines, line: number): [number, number] {
   const from = starts[line - 1]!;
   // The editor shows an empty line after a final line break, which belongs to no git line.
   const end = line < starts.length ? starts[line]! - 1 : doc.length > 0 && doc.sliceString(doc.length - 1) === '\n' ? doc.length - 1 : doc.length;
   return [doc.lineAt(from).number, doc.lineAt(Math.max(from, end)).number];
}
