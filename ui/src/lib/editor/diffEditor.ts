import { LanguageSupport, type Language } from '@codemirror/language';
import { Change, diff, getChunks, getOriginalDoc, MergeView, unifiedMergeView, type Chunk, type DiffConfig } from '@codemirror/merge';
import { EditorState, type Extension, type Text } from '@codemirror/state';
import { EditorView, highlightSpecialChars, lineNumbers } from '@codemirror/view';
import { inlineRows, sideBySideRows, singleRows, type ChunkLines, type CollapsedRegion, type RowLayout } from './collapseRows';
import type { DiffLayout } from '../diffLayout';
import { diffTheme } from './diffTheme';
import { blockWidgets, clearBlock, showBlock, showSpacer, ShownBlock, type BlockPosition } from './blockWidgets';
import { gitLineStarts } from '../gitLines';
import { islandSelection } from './islandSelection';
import { followWindow, prepareStyles } from './editorWindow';
import { collapsedLines, PassageCollapse } from './passageCollapse';
import { blockBelow, decoratePassages, passageDecorations, selectionLines } from './passageDecorations';
import { scrollParent } from './scrollParent';
import type { LinesAtSide, PassageDecorations } from '../passageMarks';
import { inlineBaseLine, sideLines } from './editorLines';
import type { FileSide, TextDiff } from '../types';
import type { BlockHost, SideBySidePanes } from './surface';

/** The time limit of the package's own diff, which runs only when the texts are not the server's. */
const fallbackTimeoutMs = 200;

/**
 * Returns the diff with each CRLF and lone CR in its texts turned into LF, and its offsets moved to match.
 *
 * The editors split lines at all three and join them with LF, so their documents equal these texts. An offset between a CR and its LF moves before the line break.
 */
export function normalizeLineBreaks(fileDiff: TextDiff): TextDiff {
   const base = lineBreakMap(fileDiff.baseText);
   const head = lineBreakMap(fileDiff.headText);
   return {
      content: 'text',
      baseText: base.text,
      headText: head.text,
      changes: fileDiff.changes.map((change) => ({
         fromA: base.offset(change.fromA),
         toA: base.offset(change.toA),
         fromB: head.offset(change.fromB),
         toB: head.offset(change.toB),
      })),
   };
}

function lineBreakMap(text: string | null): { text: string | null; offset: (offset: number) => number } {
   if (text === null || !text.includes('\r')) {
      return { text, offset: (offset) => offset };
   }

   // The index of the CR of each CRLF, in order. Each such CR before an offset shortens the text before it by one.
   const pairs: number[] = [];
   for (let index = text.indexOf('\r\n'); index >= 0; index = text.indexOf('\r\n', index + 2)) {
      pairs.push(index);
   }

   return {
      text: text.replace(/\r\n?/g, '\n'),
      offset: (offset) => {
         let low = 0;
         let high = pairs.length;
         while (low < high) {
            const middle = (low + high) >> 1;
            if (pairs[middle]! < offset) {
               low = middle + 1;
            } else {
               high = middle;
            }
         }

         return offset - low;
      },
   };
}

/** Returns the offset where a 1-based line of the text, as git counts lines, starts in an editor's document of the text, or the start of the last line when the text has fewer lines. */
export function lineStart(text: string, line: number): number {
   const starts = gitLineStarts(text);
   return starts[Math.min(Math.max(line, 1), starts.length) - 1]!;
}

/**
 * Returns the diff configuration that makes the merge views show the server's changes.
 *
 * The package's default configuration has a scan limit that turns a large file's diff into one chunk, so the views always get this one.
 *
 * @param fileDiff The diff with LF line breaks only, as {@link normalizeLineBreaks} returns it.
 */
export function serverDiffConfig(fileDiff: TextDiff): DiffConfig {
   const base = fileDiff.baseText ?? '';
   const head = fileDiff.headText ?? '';
   return {
      // The package edits the array it gets, so each call returns a new one. It also joins hunks that fewer than 3 unchanged characters separate, so a short line between two hunks shows as changed.
      override: (a, b) => (a === base && b === head ? fileDiff.changes.map((change) => new Change(change.fromA, change.toA, change.fromB, change.toB)) : diff(a, b, { timeout: fallbackTimeoutMs })),
   };
}

/**
 * A file's diff, or one file's text, mounted in CodeMirror, with the editors that later decorations go to.
 *
 * Side by side, both editors exist. Inline, the head editor shows the base's lines as deleted chunks, so `base` is null, except for a deleted file, which has only `base`. A file's text has only the
 * editor of its side.
 */
export interface DiffEditor {
   /** The editor that shows the merge base's text, or null when no editor does. */
   readonly base: EditorView | null;
   /** The editor that shows the head's text, or null when no editor does. */
   readonly head: EditorView | null;
   /** The row of the side-by-side layout and the element of each pane in it, or null for any other layout. */
   readonly panes: SideBySidePanes | null;
   /** Gives every editor of the diff the decorations of its file, as {@link decoratePassages} does, and collapses the regions away from the passages when the decorations ask for it. */
   decorate(decorations: PassageDecorations): void;
   /** The regions that the editors hide now, in the order they show them. */
   readonly collapsed: readonly CollapsedRegion[];
   /** Expands the collapsed region that hides a position, so that scrolling to it reaches its line. */
   reveal(position: EditorPosition): void;
   /**
    * Shows a block of content below the last of some lines, in place of the one shown before, or no block with null. Showing a block with the key of the shown one keeps it as it is.
    *
    * Side by side, the other pane gets a spacer as high as the block, at the row that the block follows, so the lines after it stay aligned.
    *
    * @returns Whether the editors show the block, which they do not when no editor shows the lines' side.
    */
   explain(block: BlockHost | null): boolean;
   /** Returns the git lines at one side that a selection of the page covers in the editors, `bothSides` when it covers lines of both sides, or null when it covers none of their lines. */
   selection(range: Range): LinesAtSide | 'bothSides' | null;
   /** Moves the editors' root, window listeners and size watching to the document that holds them now, as {@link followWindow} does. Call it after their element moves to another document. */
   follow(): void;
   /** Removes the editors from the page and releases them. */
   destroy(): void;
}

/** What every editor of a file needs: its language. */
export interface EditorSetup {
   /** The language that highlights the file, or null for plain text. */
   language: Language | null;
}

export interface DiffEditorOptions extends EditorSetup {
   parent: HTMLElement;
   layout: DiffLayout;
   diff: TextDiff;
}

export interface FileEditorOptions extends EditorSetup {
   parent: HTMLElement;
   /** The file's text. The editor splits lines at CRLF, CR and LF. */
   text: string;
   /** The side that the text is at, which the file's passages name. */
   side: FileSide;
}

/** Mounts a read-only view of a file's diff in the parent, in the layout given. */
export function createDiffEditor(options: DiffEditorOptions): DiffEditor {
   const { parent, layout, diff: raw } = options;
   prepareStyles(parent);
   const fileDiff = normalizeLineBreaks(options.diff);
   const extensions = editorExtensions(options);
   const base = fileDiff.baseText ?? '';
   const head = fileDiff.headText ?? '';
   const diffConfig = serverDiffConfig(fileDiff);

   if (layout === 'sideBySide') {
      const view = new MergeView({
         a: { doc: base, extensions: [extensions, passageDecorations({ side: 'base', text: raw.baseText ?? '' }), collapsedLines] },
         b: { doc: head, extensions: [extensions, passageDecorations({ side: 'head', text: raw.headText ?? '' }), collapsedLines] },
         parent,
         // The server's changes cover whole lines, so marking the changed text inside them would mark every character.
         highlightChanges: false,
         diffConfig,
      });
      const chunks = getChunks(view.b.state)?.chunks ?? [];
      const docs = { a: view.a.state.doc, b: view.b.state.doc };
      // The merge view aligns the lines after the last chunk only when they start inside the base's document.
      const endAligned = chunks.length === 0 || chunks.at(-1)!.toA < docs.a.length;
      const rows = sideBySideRows(
         { a: docs.a.lines, b: docs.b.lines },
         chunks.map((chunk) => chunkLines(chunk, docs.a, docs.b)),
         endAligned,
      );
      const panes = { row: view.dom.querySelector<HTMLElement>('.cm-mergeViewEditors')!, base: view.a.dom.parentElement!, head: view.b.dom.parentElement! };
      return diffEditor(view.a, view.b, new PassageCollapse([view.a, view.b], rows), () => view.destroy(), { rows, panes });
   }

   // Inline, a deleted file would be one deleted chunk, which the editor draws whole instead of only the visible lines.
   if (fileDiff.headText === null) {
      const view = new EditorView({
         parent,
         state: EditorState.create({
            doc: base,
            extensions: [extensions, passageDecorations({ side: 'base', text: raw.baseText ?? '' }), collapsedLines, EditorView.editorAttributes.of({ class: 'lotsen-deletedFile' })],
         }),
      });
      return diffEditor(view, null, new PassageCollapse([view], singleRows(view.state.doc.lines)), () => view.destroy());
   }

   const view = new EditorView({
      parent,
      state: EditorState.create({
         doc: head,
         extensions: [
            extensions,
            unifiedMergeView({ original: base, highlightChanges: false, mergeControls: false, diffConfig }),
            passageDecorations({ inline: { baseText: raw.baseText, headText: raw.headText ?? '' } }),
            collapsedLines,
         ],
      }),
   });
   const chunks = getChunks(view.state)?.chunks ?? [];
   const rows = inlineRows(
      view.state.doc.lines,
      chunks.map((chunk) => chunkLines(chunk, getOriginalDoc(view.state), view.state.doc)),
   );
   return diffEditor(null, view, new PassageCollapse([view], rows), () => view.destroy());
}

/** Returns the lines that a chunk covers in each document. A side where the chunk is empty gets an empty span before the line at the chunk's position. */
function chunkLines(chunk: Chunk, a: Text, b: Text): ChunkLines {
   const span = (doc: Text, from: number, to: number) => {
      const first = doc.lineAt(from).number;
      return { from: first, to: from < to ? doc.lineAt(Math.min(to - 1, doc.length)).number : first - 1 };
   };

   return { a: span(a, chunk.fromA, chunk.toA), b: span(b, chunk.fromB, chunk.toB) };
}

/** @param sideBySide The rows of a side-by-side layout, whose two panes align a block with a spacer, and the panes' elements, or undefined for one editor. */
function diffEditor(base: EditorView | null, head: EditorView | null, collapse: PassageCollapse, destroy: () => void, sideBySide?: { rows: RowLayout; panes: SideBySidePanes }): DiffEditor {
   const paneRows = sideBySide?.rows;
   const views = [base, head].filter((view) => view !== null);
   // A merge view builds its editors before it adds them to the parent (MergeView in @codemirror/merge 6.12.3), so they start bound to the main page, also when the parent is in a popout window.
   views.forEach(followWindow);
   let shownKey: string | null = null;
   let shown = false;
   const explain = (block: BlockHost | null): boolean => {
      if ((block?.key ?? null) === shownKey) {
         return shown;
      }

      shownKey = block?.key ?? null;
      shown = false;
      views.forEach(clearBlock);
      const editor = block === null ? -1 : views.findIndex((view) => blockBelow(view.state, block.below) !== null);
      if (block === null || editor < 0) {
         return false;
      }

      shown = true;

      const view = views[editor]!;
      const below = blockBelow(view.state, block.below)!;
      const other = paneRows !== undefined && below.line !== null ? views[1 - editor] : undefined;
      if (other === undefined) {
         showBlock(view, below, new ShownBlock(block.render));
         return true;
      }

      const spacer = spacerPosition(other, paneRows!, editor, below.line!);
      showSpacer(other, spacer, 0);
      showBlock(view, below, new ShownBlock(block.render, (height) => showSpacer(other, spacer, height)));
      return true;
   };

   return {
      base,
      head,
      panes: sideBySide?.panes ?? null,
      decorate: (decorations) => {
         for (const view of views) {
            decoratePassages(view, decorations);
         }

         collapse.update(decorations);
      },
      get collapsed() {
         return collapse.collapsed;
      },
      reveal: ({ view, pos, deletedLine }) => {
         const editor = views.indexOf(view);
         if (editor >= 0) {
            const chunk = deletedLine === undefined ? -1 : (getChunks(view.state)?.chunks.findIndex((candidate) => candidate.fromB === pos) ?? -1);
            collapse.reveal(chunk >= 0 ? collapse.chunkRow(chunk, deletedLine!) : collapse.lineRow(editor, view.state.doc.lineAt(pos).number));
         }
      },
      explain,
      selection: (range) => {
         const found = views.map((view) => selectionLines(view, range)).filter((lines) => lines !== null);
         return found.length > 1 ? 'bothSides' : (found[0] ?? null);
      },
      follow: () => views.forEach(followWindow),
      destroy: () => {
         explain(null);
         destroy();
      },
   };
}

/** Returns where the other pane of a side-by-side layout puts the spacer for a block below a line: after its last line at the line's row or above it, or above its first line when it has none there. */
function spacerPosition(other: EditorView, rows: RowLayout, editor: number, line: number): BlockPosition {
   const otherRows = rows.editors[1 - editor]!;
   for (let row = rows.rowOf[editor]![line]!; row >= 0; row--) {
      const otherLine = otherRows[row]!;
      if (otherLine > 0) {
         return { pos: other.state.doc.line(otherLine).to, before: false };
      }
   }

   return { pos: 0, before: true };
}

/** An editor of a mounted diff or file and a position in its document. */
export interface EditorPosition {
   view: EditorView;
   pos: number;
   /** The 0-based index of the line among the deleted lines that the inline head editor draws above `pos`, or undefined when the line is not one of them. */
   deletedLine?: number;
}

/** The space above a line that the editor scrolls to, in pixels. */
const scrollMargin = 32;

/**
 * Returns where a 1-based line of one side starts in a mounted diff.
 *
 * Inline, the head editor shows the base: a base line that it shows as deleted maps to the start of its chunk, and any other base line to the head line it equals.
 *
 * @param fileDiff The diff as the server sent it, with the texts that the editors show.
 * @returns The editor and the position, or null when the side has no text.
 */
export function linePosition(editor: DiffEditor, fileDiff: TextDiff, side: FileSide, line: number): EditorPosition | null {
   const text = side === 'base' ? fileDiff.baseText : fileDiff.headText;
   const own = side === 'base' ? editor.base : editor.head;
   if (text === null) {
      return null;
   }

   if (own !== null) {
      return { view: own, pos: lineStart(text, line) };
   }

   const head = editor.head;
   if (side === 'head' || head === null) {
      return null;
   }

   const headLines = sideLines(fileDiff.headText ?? '', head.state.doc);
   const chunks = getChunks(head.state)?.chunks ?? [];
   const place = inlineBaseLine(sideLines(text, getOriginalDoc(head.state)), headLines, chunks, line);
   if ('chunk' in place) {
      return { view: head, pos: chunks[place.chunk]!.fromB, deletedLine: place.line };
   }

   return { view: head, pos: headLines.starts[place.headLine - 1]! };
}

/** The deleted line that each editor's pending scroll goes to, which {@link deletedLineScroll} reads. */
const deletedLineTargets = new WeakMap<EditorView, { pos: number; deletedLine: number }>();

/**
 * Scrolls to a deleted line that an inline head editor draws above a position, which the default scroll cannot reach because it scrolls to the position's own line.
 *
 * The editor runs the handler after it has drawn and measured the lines at the target. The line block at the position includes the deletion widget before it, so the block's top is the top of the
 * first deleted line. It scrolls the element that {@link scrollParent} returns.
 */
const deletedLineScroll = EditorView.scrollHandler.of((view, range) => {
   const target = deletedLineTargets.get(view);
   deletedLineTargets.delete(view);
   if (target === undefined || range.head !== target.pos) {
      return false;
   }

   const scroller = scrollParent(view) ?? view.scrollDOM;
   const top = view.documentTop + view.lineBlockAt(target.pos).top + target.deletedLine * view.defaultLineHeight;
   scroller.scrollTop += top - scroller.getBoundingClientRect().top - scrollMargin;
   return true;
});

/** Scrolls the editor so that the position's line, or its deleted line, starts near the top of the view. */
export function scrollToPosition({ view, pos, deletedLine }: EditorPosition): void {
   if (deletedLine === undefined) {
      deletedLineTargets.delete(view);
   } else {
      deletedLineTargets.set(view, { pos, deletedLine });
   }

   view.dispatch({ effects: EditorView.scrollIntoView(pos, { y: 'start', yMargin: scrollMargin }) });
}

/** Mounts a read-only view of a file's text in the parent, with the diff editors' setup and theme, as the one editor of its side. */
export function createFileEditor(options: FileEditorOptions): DiffEditor {
   prepareStyles(options.parent);
   const extensions = [editorExtensions(options), passageDecorations({ side: options.side, text: options.text }), collapsedLines];
   const view = new EditorView({ parent: options.parent, state: EditorState.create({ doc: options.text, extensions }) });
   const collapse = new PassageCollapse([view], singleRows(view.state.doc.lines));
   return options.side === 'base' ? diffEditor(view, null, collapse, () => view.destroy()) : diffEditor(null, view, collapse, () => view.destroy());
}

function editorExtensions({ language }: EditorSetup): Extension {
   return [EditorState.readOnly.of(true), lineNumbers(), highlightSpecialChars(), language === null ? [] : new LanguageSupport(language), diffTheme, deletedLineScroll, blockWidgets, islandSelection];
}
