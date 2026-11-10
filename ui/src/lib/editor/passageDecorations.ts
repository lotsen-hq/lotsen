import { getChunks, getOriginalDoc } from '@codemirror/merge';
import { Facet, RangeSet, StateEffect, StateField, type EditorState, type Extension, type Range } from '@codemirror/state';
import { Decoration, EditorView, gutterLineClass, GutterMarker, ViewPlugin, WidgetType, type DecorationSet } from '@codemirror/view';
import type { BlockPosition } from './blockWidgets';
import { noPassageDecorations, type LinesAtSide, type PassageDecorations, type ThreadSpot } from '../passageMarks';
import {
   editorHoveredLines,
   editorLineMarks,
   editorThreadMarks,
   lastEditorLine,
   selectedLines,
   sideLines,
   type EditorContent,
   type EditorLineMarks,
   type EditorLines,
   type EditorPlace,
   type EditorThreadMarks,
   type LineMark,
   type PassageLabel,
} from './editorLines';
import type { FileSide, PassageRole } from '../types';

/** The texts of a file that one editor shows, as the server sent them: one side's text, or inline the head's text with the base's deleted lines. */
export type PassageSource = { side: FileSide; text: string } | { inline: { baseText: string | null; headText: string } };

/** The effect that gives an editor the decorations of its file. The editor keeps them until the next one. */
export const setPassageDecorations = StateEffect.define<PassageDecorations>();

/** Gives an editor the decorations of its file, without remounting it. */
export function decoratePassages(view: EditorView, decorations: PassageDecorations): void {
   view.dispatch({ effects: setPassageDecorations.of(decorations) });
}

/** The classes of a line inside a passage, by the passage's role, of a line inside a focused passage, of a line of the hovered reference, and of a line that dimming covers. */
const passageClass: Record<PassageRole, string> = { primary: 'lotsen-passage lotsen-passage-primary', related: 'lotsen-passage lotsen-passage-related' };
const focusedClass = 'lotsen-passage-focused';
const hoveredClass = 'lotsen-reference-hovered';
const dimmedClass = 'lotsen-dimmed';
/** The class of the line-number gutter of a line that a thread was asked about, and of such a line that a deleted chunk of the inline view draws, whose chunk has one gutter element. */
const threadClass = 'lotsen-thread';
const lineClasses = ['lotsen-passage', 'lotsen-passage-primary', 'lotsen-passage-related', focusedClass, hoveredClass, dimmedClass, threadClass];

/**
 * Returns the classes of a line, or an empty string for none. A line of the hovered reference or inside a passage never dims, highlighted or not, and neither does a line with a thread's marker.
 */
function lineClass(mark: LineMark | undefined, hovered: boolean, marked: boolean, { dim, highlight }: PassageDecorations): string {
   const classes = mark === undefined ? (dim && !hovered && !marked ? [dimmedClass] : []) : highlight ? [passageClass[mark.role], ...(mark.focused ? [focusedClass] : [])] : [];
   return [...classes, ...(hovered ? [hoveredClass] : [])].join(' ');
}

class LineClassMarker extends GutterMarker {
   override readonly elementClass: string;

   constructor(elementClass: string) {
      super();
      this.elementClass = elementClass;
   }
}

// One decoration and one gutter marker for each class combination, so equal lines share them.
const lineDecorations = new Map<string, Decoration>();
const gutterMarkers = new Map<string, LineClassMarker>();

function lineDecoration(className: string): Decoration {
   let decoration = lineDecorations.get(className);
   if (decoration === undefined) {
      decoration = Decoration.line({ class: className });
      lineDecorations.set(className, decoration);
   }

   return decoration;
}

function gutterMarker(className: string): LineClassMarker {
   let marker = gutterMarkers.get(className);
   if (marker === undefined) {
      marker = new LineClassMarker(className);
      gutterMarkers.set(className, marker);
   }

   return marker;
}

/** Returns the element that shows the labels of the passages that start on a line. */
function labelsElement(labels: readonly PassageLabel[]): HTMLElement {
   const element = document.createElement('span');
   element.className = 'lotsen-passageLabels';
   for (const label of labels) {
      const item = element.appendChild(document.createElement('span'));
      item.className = `lotsen-passageLabel lotsen-passageLabel-${label.role}`;
      item.textContent = label.text;
   }

   return element;
}

class LabelsWidget extends WidgetType {
   private readonly labels: readonly PassageLabel[];

   constructor(labels: readonly PassageLabel[]) {
      super();
      this.labels = labels;
   }

   override eq(other: LabelsWidget): boolean {
      return other.labels.length === this.labels.length && other.labels.every((label, index) => label.text === this.labels[index]!.text && label.role === this.labels[index]!.role);
   }

   override toDOM(): HTMLElement {
      return labelsElement(this.labels);
   }
}

/** Returns the element that holds the markers of the threads asked about lines that start on a line. Each is a button whose `data-thread-id` names its thread. */
function threadMarkersElement(threads: readonly ThreadSpot[]): HTMLElement {
   const element = document.createElement('span');
   element.className = 'lotsen-threadMarkers';
   for (const thread of threads) {
      element.appendChild(threadMarkerButton(thread.id, thread.label));
   }

   return element;
}

const svgNamespace = 'http://www.w3.org/2000/svg';

/** Returns a thread's marker: a button with Lucide's message-square icon and the label as text, which the editor's parent component opens by its `data-thread-id`. */
export function threadMarkerButton(id: string, label: string): HTMLButtonElement {
   const button = document.createElement('button');
   button.type = 'button';
   button.className = 'thread-marker';
   button.dataset.threadId = id;
   button.setAttribute('aria-label', `Open the thread: ${label}`);
   const icon = button.appendChild(document.createElementNS(svgNamespace, 'svg'));
   for (const [name, value] of Object.entries({
      viewBox: '0 0 24 24',
      fill: 'none',
      stroke: 'currentColor',
      'stroke-width': '2',
      'stroke-linecap': 'round',
      'stroke-linejoin': 'round',
      'aria-hidden': 'true',
   })) {
      icon.setAttribute(name, value);
   }

   icon.appendChild(document.createElementNS(svgNamespace, 'path')).setAttribute('d', 'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z');
   button.appendChild(document.createElement('span')).textContent = label;
   return button;
}

class ThreadMarkersWidget extends WidgetType {
   private readonly threads: readonly ThreadSpot[];

   constructor(threads: readonly ThreadSpot[]) {
      super();
      this.threads = threads;
   }

   override eq(other: ThreadMarkersWidget): boolean {
      return other.threads.length === this.threads.length && other.threads.every((thread, index) => thread.id === this.threads[index]!.id && thread.label === this.threads[index]!.label);
   }

   override toDOM(): HTMLElement {
      return threadMarkersElement(this.threads);
   }
}

interface DecoratedEditor {
   input: PassageDecorations;
   marks: EditorLineMarks;
   hovered: EditorLines;
   threads: EditorThreadMarks;
   lines: DecorationSet;
   gutter: RangeSet<GutterMarker>;
}

function editorContent(source: PassageSource, state: EditorState): EditorContent {
   if ('side' in source) {
      return { kind: 'side', side: source.side, lines: sideLines(source.text, state.doc) };
   }

   const { baseText, headText } = source.inline;
   return {
      kind: 'inline',
      base: baseText === null ? null : sideLines(baseText, getOriginalDoc(state)),
      head: sideLines(headText, state.doc),
      chunks: getChunks(state)?.chunks ?? [],
   };
}

const noLines: EditorLines = { lines: new Set(), deleted: new Map() };

function decorate(source: PassageSource, state: EditorState, input: PassageDecorations): DecoratedEditor {
   const content = editorContent(source, state);
   const marks = editorLineMarks(content, input.passages);
   const hovered = input.hovered === undefined ? noLines : editorHoveredLines(content, input.hovered);
   const threads = editorThreadMarks(content, input.threads ?? []);
   const lines: Range<Decoration>[] = [];
   const gutter: Range<GutterMarker>[] = [];
   for (let number = 1; number <= state.doc.lines; number++) {
      const line = state.doc.line(number);
      const mark = marks.lines.get(number);
      const markers = threads.lines.get(number);
      const className = lineClass(mark, hovered.lines.has(number), markers !== undefined, input);
      if (className !== '') {
         lines.push(lineDecoration(className).range(line.from));
      }

      const gutterClass = [className, ...(threads.covered.lines.has(number) ? [threadClass] : [])].filter((part) => part !== '').join(' ');
      if (gutterClass !== '') {
         gutter.push(gutterMarker(gutterClass).range(line.from));
      }

      if (mark !== undefined && mark.labels.length > 0) {
         lines.push(Decoration.widget({ widget: new LabelsWidget(mark.labels), side: 1 }).range(line.to));
      }

      if (markers !== undefined) {
         lines.push(Decoration.widget({ widget: new ThreadMarkersWidget(markers), side: 2 }).range(line.to));
      }
   }

   return { input, marks, hovered, threads, lines: Decoration.set(lines, true), gutter: RangeSet.of(gutter, true) };
}

/** Sets the classes, the labels and the thread markers of one line that a deleted chunk of the inline view draws, and marks it when a thread covers it. */
function markDeletedLine(line: Element, mark: LineMark | undefined, hovered: boolean, markers: readonly ThreadSpot[] | undefined, inThread: boolean, input: PassageDecorations): void {
   line.classList.remove(...lineClasses);
   line.querySelector(':scope > .lotsen-passageLabels')?.remove();
   line.querySelector(':scope > .lotsen-threadMarkers')?.remove();
   const className = lineClass(mark, hovered, markers !== undefined, input);
   if (className !== '') {
      line.classList.add(...className.split(' '));
   }

   if (inThread) {
      line.classList.add(threadClass);
   }

   if (mark !== undefined && mark.labels.length > 0) {
      line.appendChild(labelsElement(mark.labels));
   }

   if (markers !== undefined) {
      line.appendChild(threadMarkersElement(markers));
   }
}

/** Returns the index among the editor's chunks of the deleted chunk that the inline view draws as an element, or -1 when none matches. */
function deletedChunkIndex(view: EditorView, element: Element): number {
   const position = view.posAtDOM(element);
   return getChunks(view.state)?.chunks.findIndex((candidate) => candidate.fromB === position) ?? -1;
}

/** Returns whether a selection holds any of a line's code, leaving out the labels and thread markers drawn in it. A line that the selection spans whole counts, even an empty one. */
function holdsCode(selection: globalThis.Range, line: Element): boolean {
   const startsInside = line.contains(selection.startContainer);
   const endsInside = line.contains(selection.endContainer);
   if (!startsInside && !endsInside) {
      return true;
   }

   const part = document.createRange();
   part.selectNodeContents(line);
   if (startsInside) {
      part.setStart(selection.startContainer, selection.startOffset);
   }

   if (endsInside) {
      part.setEnd(selection.endContainer, selection.endOffset);
   }

   const contents = part.cloneContents();
   contents.querySelectorAll('.lotsen-passageLabels, .lotsen-threadMarkers').forEach((element) => element.remove());
   return contents.textContent !== '';
}

/**
 * Returns the git lines at one side that a selection covers in an editor that {@link passageDecorations} set up: whole lines, both sides of a selection that spans them, or null when it covers
 * none of the editor's lines. A line counts only when the selection holds some of its code, so a selection that starts at the end of a line or ends at the start of one leaves that line out.
 */
export function selectionLines(view: EditorView, selection: globalThis.Range): LinesAtSide | 'bothSides' | null {
   const covered = [...view.contentDOM.querySelectorAll(':scope > .cm-line, .cm-deletedChunk > .cm-deletedLine')].filter((line) => selection.intersectsNode(line) && holdsCode(selection, line));
   const place = (line: Element): EditorPlace | null => {
      if (!line.classList.contains('cm-deletedLine')) {
         return { chunk: null, line: view.state.doc.lineAt(view.posAtDOM(line)).number };
      }

      const chunk = deletedChunkIndex(view, line.parentElement!);
      return chunk < 0 ? null : { chunk, line: [...line.parentElement!.querySelectorAll(':scope > .cm-deletedLine')].indexOf(line) };
   };
   const first = covered.length === 0 ? null : place(covered[0]!);
   const last = covered.length === 0 ? null : place(covered.at(-1)!);
   if (first === null || last === null) {
      return null;
   }

   return selectedLines(editorContent(view.state.facet(passageSource), view.state), first, last) ?? 'bothSides';
}

/** The texts that an editor's passage decorations read. */
const passageSource = Facet.define<PassageSource, PassageSource>({ combine: (sources) => sources[0]! });

const decoratedField = StateField.define<DecoratedEditor>({
   create: (state) => decorate(state.facet(passageSource), state, noPassageDecorations),
   update: (value, transaction) => {
      const effect = transaction.effects.findLast((candidate) => candidate.is(setPassageDecorations));
      return effect === undefined ? value : decorate(transaction.state.facet(passageSource), transaction.state, effect.value);
   },
   provide: (self) => [EditorView.decorations.from(self, (value) => value.lines), gutterLineClass.from(self, (value) => value.gutter)],
});

/** Returns the lines of an editor that its file's passages cover now. */
export function passageLineMarks(state: EditorState): EditorLineMarks {
   return state.field(decoratedField).marks;
}

/**
 * Returns where a block below lines at a side goes in an editor that {@link passageDecorations} set up, or null when the editor does not show the side.
 *
 * @returns The position, and the 1-based line of the document that the block follows, which is null below a deleted chunk of the inline view.
 */
export function blockBelow(state: EditorState, lines: LinesAtSide): (BlockPosition & { line: number | null }) | null {
   const last = lastEditorLine(editorContent(state.facet(passageSource), state), lines);
   if (last === null) {
      return null;
   }

   if (last.chunk === null) {
      return { pos: state.doc.line(last.line).to, before: false, line: last.line };
   }

   const chunk = getChunks(state)?.chunks[last.chunk];
   return chunk === undefined ? null : { pos: chunk.fromB, before: true, line: null };
}

/**
 * Returns the extension that decorates an editor's lines from the passages of its file, which {@link setPassageDecorations} gives it.
 *
 * Lines inside a passage get the class of its role while the decorations highlight, the first line of a labeled passage shows the label at its end, the lines of the hovered reference get a class of
 * their own, and with dimming every other line gets the dimmed class. The line-number gutter gets the same classes.
 */
export function passageDecorations(source: PassageSource): Extension {
   if ('side' in source) {
      return [passageSource.of(source), decoratedField];
   }

   // Deleted lines are widget elements that line decorations cannot reach, so the plugin marks them when the decorations change and after each redraw, which may draw a chunk for the first time.
   const markDeletedChunks = (view: EditorView) => {
      const { input, marks, hovered, threads } = view.state.field(decoratedField);
      for (const element of view.contentDOM.querySelectorAll('.cm-deletedChunk')) {
         const chunk = deletedChunkIndex(view, element);
         const chunkMarks = marks.deleted.get(chunk);
         const chunkHovered = hovered.deleted.get(chunk);
         const chunkThreads = threads.deleted.get(chunk);
         const chunkCovered = threads.covered.deleted.get(chunk);
         element
            .querySelectorAll(':scope > .cm-deletedLine')
            .forEach((line, index) => markDeletedLine(line, chunkMarks?.get(index), chunkHovered?.has(index) === true, chunkThreads?.get(index), chunkCovered?.has(index) === true, input));
      }
   };

   const plugin = ViewPlugin.define((view) => {
      markDeletedChunks(view);
      return {
         update: (update) => {
            if (update.state.field(decoratedField) !== update.startState.field(decoratedField)) {
               markDeletedChunks(update.view);
            }
         },
         docViewUpdate: markDeletedChunks,
      };
   });
   return [passageSource.of(source), decoratedField, plugin];
}
