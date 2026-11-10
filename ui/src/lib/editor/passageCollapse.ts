import { StateEffect, StateField, type Extension, type Range } from '@codemirror/state';
import { Decoration, EditorView, WidgetType, type DecorationSet } from '@codemirror/view';
import { collapsedMarkerHeight } from './diffTheme';
import { collapsedRegions, expandedRows, type CollapsedRegion, type ExpandEdge, type LineSpan, type RowLayout } from './collapseRows';
import { passageLineMarks } from './passageDecorations';
import { scrollParent } from './scrollParent';
import type { DecoratedPassage, PassageDecorations } from '../passageMarks';

/** The lines around a passage that stay shown, the fewest lines that a marker hides, and how many lines one step of expanding shows. */
export const collapseSettings = { context: 3, minLines: 4, step: 20 };

/** One marker of an editor: the lines it replaces and the rows it hides. */
interface CollapseMarker {
   lines: LineSpan;
   hidden: number;
}

/** Expands the region whose marker in this editor starts at a line, and returns the lines of this editor that the rest of the region still hides, or null when nothing of it is left. */
type ExpandMarker = (line: number, edge: ExpandEdge) => LineSpan | null;

const setCollapsedLines = StateEffect.define<{ markers: readonly CollapseMarker[]; expand: ExpandMarker }>();

const markerClass = 'lotsen-collapsed';

class CollapsedLinesWidget extends WidgetType {
   private readonly marker: CollapseMarker;
   private readonly expand: ExpandMarker;

   constructor(marker: CollapseMarker, expand: ExpandMarker) {
      super();
      this.marker = marker;
      this.expand = expand;
   }

   // Every expand callback of an editor calls the same collapse for the same editor, so markers with equal lines are equal.
   override eq(other: CollapsedLinesWidget): boolean {
      return other.marker.lines.from === this.marker.lines.from && other.marker.lines.to === this.marker.lines.to && other.marker.hidden === this.marker.hidden;
   }

   override toDOM(view: EditorView): HTMLElement {
      const { lines, hidden } = this.marker;
      const element = document.createElement('div');
      element.className = markerClass;
      element.dataset.from = String(lines.from);
      const count = element.appendChild(document.createElement('span'));
      count.className = 'lotsen-collapsedCount';
      count.textContent = `${hidden} ${hidden === 1 ? 'line' : 'lines'} hidden`;
      const button = (edge: ExpandEdge, text: string) => {
         const control = element.appendChild(document.createElement('button'));
         control.type = 'button';
         control.className = 'lotsen-expand';
         control.dataset.edge = edge;
         control.textContent = text;
         control.addEventListener('click', () => {
            const focused = element.contains(document.activeElement);
            const kept = keptSide(view, element, lines);
            const rest = this.expand(lines.from, edge);
            settleAfterExpand(view, lines, rest, edge, focused, kept);
         });
      };

      // "Show all" comes first, so a narrow pane that clips the marker keeps it.
      button('all', 'Show all');
      if (hidden > collapseSettings.step) {
         button('top', `Show first ${collapseSettings.step}`);
         button('bottom', `Show last ${collapseSettings.step}`);
      }

      return element;
   }

   override ignoreEvent(): boolean {
      return true;
   }

   override get estimatedHeight(): number {
      return collapsedMarkerHeight;
   }
}

/** The side of a marker whose shown lines keep their place on screen while the marker expands: the start of the shown line beside the marker on that side, and the line's top in window coordinates before the expansion. */
interface KeptSide {
   below: boolean;
   pos: number;
   top: number;
}

/**
 * Returns the side of a marker that its expansion keeps in place, so the passage that the reviewer reads stays where it was.
 *
 * A marker at the start of the document keeps the lines below it, and one at the end keeps the lines above it. A marker between shown lines keeps the side nearer the middle of the visible area.
 *
 * @returns The side, or null when no shown line of the editor borders the marker.
 */
function keptSide(view: EditorView, marker: HTMLElement, lines: LineSpan): KeptSide | null {
   const { doc } = view.state;
   const linesAbove = lines.from > 1;
   const linesBelow = lines.to < doc.lines;
   if (!linesAbove && !linesBelow) {
      return null;
   }

   const visible = scrollParent(view)?.getBoundingClientRect() ?? { top: 0, bottom: window.innerHeight };
   const box = marker.getBoundingClientRect();
   const below = !linesAbove || (linesBelow && box.top + box.bottom < visible.top + visible.bottom);
   const pos = doc.line(below ? lines.to + 1 : lines.from - 1).from;
   return { below, pos, top: lineTop(view, pos) };
}

/** The kept side that each editor's pending scroll after an expansion restores, the position that the scroll targets, and the control that takes the focus, which {@link keptSideScroll} reads. */
const keptSideTargets = new WeakMap<EditorView, { target: number; kept: KeptSide; focus: HTMLElement | null }>();

/**
 * Scrolls an expanded editor so that the shown line beside its marker on the kept side is back at its top before the expansion, as far as the scroller reaches.
 *
 * It then scrolls by the least amount that shows the control that takes the focus, so the kept line moves when both do not fit. The editor runs the handler after it has drawn and measured the
 * expanded lines.
 */
const keptSideScroll = EditorView.scrollHandler.of((view, range) => {
   const target = keptSideTargets.get(view);
   keptSideTargets.delete(view);
   if (target === undefined || range.head !== target.target) {
      return false;
   }

   const scroller = scrollParent(view) ?? view.scrollDOM;
   scroller.scrollTop += lineTop(view, target.kept.pos) - target.kept.top;
   if (target.focus?.isConnected) {
      const control = target.focus.getBoundingClientRect();
      const { top, bottom: scrollerBottom } = scroller.getBoundingClientRect();
      // The whole-pixel client height can reach a fraction of a pixel past the scroller's box.
      const bottom = Math.min(scrollerBottom, top + scroller.clientHeight);
      scroller.scrollTop += control.top < top ? control.top - top : control.bottom > bottom ? control.bottom - bottom : 0;
   }

   return true;
});

function lineTop(view: EditorView, pos: number): number {
   return view.documentTop + view.lineBlockAt(pos).top;
}

/**
 * Keeps the marker's kept side in place after its expansion, and moves the focus from a marker that the expansion removed.
 *
 * The focus goes to the same control of the region's rest. When nothing of the region is left, the focus goes to the editor, with the caret on the region's line beside the kept side.
 */
function settleAfterExpand(view: EditorView, lines: LineSpan, rest: LineSpan | null, edge: ExpandEdge, focused: boolean, kept: KeptSide | null): void {
   const marker = rest === null ? null : view.contentDOM.querySelector(`.${markerClass}[data-from="${rest.from}"]`);
   const control = marker?.querySelector<HTMLElement>(`[data-edge="${edge}"]`) ?? marker?.querySelector<HTMLElement>('[data-edge="all"]');
   const caret = view.state.doc.line(kept?.below ? lines.to : lines.from).from;
   const moveCaret = focused && !control && rest === null;
   if (kept !== null) {
      keptSideTargets.set(view, { target: caret, kept, focus: focused ? (control ?? null) : null });
   }

   if (kept !== null || moveCaret) {
      view.dispatch({ selection: moveCaret ? { anchor: caret } : undefined, effects: EditorView.scrollIntoView(caret) });
   }

   if (!focused) {
      return;
   }

   if (control) {
      control.focus({ preventScroll: kept !== null });
   } else {
      view.focus();
   }
}

const collapsedLinesField = StateField.define<DecorationSet>({
   create: () => Decoration.none,
   update: (value, transaction) => {
      const effect = transaction.effects.findLast((candidate) => candidate.is(setCollapsedLines));
      if (effect === undefined) {
         return value.map(transaction.changes);
      }

      const { doc } = transaction.state;
      const { markers, expand } = effect.value;
      const ranges: Range<Decoration>[] = markers.map((marker) =>
         Decoration.replace({ block: true, widget: new CollapsedLinesWidget(marker, expand) }).range(doc.line(marker.lines.from).from, doc.line(marker.lines.to).to),
      );
      return Decoration.set(ranges, true);
   },
   provide: (self) => EditorView.decorations.from(self),
});

/** The extension that lets an editor show the markers of a {@link PassageCollapse}. */
export const collapsedLines: Extension = [collapsedLinesField, keptSideScroll];

/**
 * Collapses the rows of a layout away from its editors' passages, and expands them again.
 *
 * It reads the passages that each editor's passage decorations cover, so it updates after them. A change of the passages or of the collapse input forgets what the reviewer expanded.
 */
export class PassageCollapse {
   private readonly views: readonly EditorView[];
   private readonly layout: RowLayout;
   private readonly revealed = new Set<number>();
   private input: PassageDecorations | null = null;
   private regions: readonly CollapsedRegion[] = [];

   /** @param views The editors of the layout, in the order of its editors. */
   constructor(views: readonly EditorView[], layout: RowLayout) {
      this.views = views;
      this.layout = layout;
   }

   /** The regions that the editors hide now, in row order. */
   get collapsed(): readonly CollapsedRegion[] {
      return this.regions;
   }

   /** Collapses the editors to the decorations that they show now, keeping what the reviewer expanded while the passages and the collapse input stay the same. */
   update(decorations: PassageDecorations): void {
      if (this.input === null || this.input.collapse !== decorations.collapse || !samePassages(this.input.passages, decorations.passages)) {
         this.revealed.clear();
      }

      this.input = decorations;
      this.apply();
   }

   /** Returns the row that shows a 1-based line of one of the editors. */
   lineRow(editor: number, line: number): number {
      return this.layout.rowOf[editor]?.[line] ?? -1;
   }

   /** Returns the row that shows a 0-based line of the inline view's deleted chunk, by the chunk's index. */
   chunkRow(chunk: number, line: number): number {
      return (this.layout.groups[chunk]?.from ?? -1) + line;
   }

   /** Shows the whole region that hides a row, if one does. */
   reveal(row: number): void {
      const region = this.regions.find(({ rows }) => rows.from <= row && row <= rows.to);
      if (region !== undefined) {
         this.show(region.rows);
         this.apply();
      }
   }

   private expand(editor: number, line: number, edge: ExpandEdge): LineSpan | null {
      const region = this.regions.find((candidate) => candidate.parts[editor]?.lines.from === line);
      if (region === undefined) {
         return null;
      }

      const shown = expandedRows(region, edge, collapseSettings.step);
      this.show(shown);
      this.apply();
      // The rest of the region lies on the side of the expanded rows that the edge does not name.
      const restRow = edge === 'top' ? region.rows.to : region.rows.from;
      const rest = this.regions.find(({ rows }) => rows.from <= restRow && restRow <= rows.to);
      return rest?.parts[editor]?.lines ?? null;
   }

   private show({ from, to }: { from: number; to: number }): void {
      for (let row = from; row <= to; row++) {
         this.revealed.add(row);
      }
   }

   private apply(): void {
      this.regions = this.input?.collapse ? collapsedRegions(this.layout, this.focusRows(), this.revealed, collapseSettings) : [];
      this.views.forEach((view, editor) => {
         const markers = this.regions.flatMap((region) => {
            const part = region.parts[editor];
            return part ? [{ lines: part.lines, hidden: part.hidden }] : [];
         });
         view.dispatch({ effects: setCollapsedLines.of({ markers, expand: (line, edge) => this.expand(editor, line, edge) }) });
      });
   }

   private focusRows(): number[] {
      const rows: number[] = [];
      this.views.forEach((view, editor) => {
         const marks = passageLineMarks(view.state);
         for (const line of marks.lines.keys()) {
            rows.push(this.layout.rowOf[editor]![line]!);
         }

         for (const [chunk, deleted] of marks.deleted) {
            for (const line of deleted.keys()) {
               rows.push(this.layout.groups[chunk]!.from + line);
            }
         }
      });
      return rows.filter((row) => row >= 0);
   }
}

function samePassages(a: readonly DecoratedPassage[], b: readonly DecoratedPassage[]): boolean {
   return a.length === b.length && a.every((passage, index) => passage.side === b[index]!.side && passage.startLine === b[index]!.startLine && passage.endLine === b[index]!.endLine);
}
