import { EditorView } from '@codemirror/view';
import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import DiffView from '../src/components/DiffView.svelte';
import FileView from '../src/components/FileView.svelte';
import type { KnownDiff, PickedAnchor } from '../src/lib/viewState';
import type { DiffLayout } from '../src/lib/diffLayout';
import type { PassageDecorations } from '../src/lib/passageMarks';
import type { TextDiff } from '../src/lib/types';

function loaded(path: string, diff: TextDiff): KnownDiff {
   return { state: 'loaded', path, diff };
}

function modified(base: string | null, head: string | null, changes: TextDiff['changes']): TextDiff {
   return { content: 'text', baseText: base, headText: head, changes };
}

async function editors(count: number): Promise<EditorView[]> {
   return waitFor(() => {
      const found = [...document.querySelectorAll<HTMLElement>('.cm-editor')];
      expect(found).toHaveLength(count);
      return found.map((dom) => EditorView.findFromDOM(dom)!);
   });
}

/** The lines that an editor draws, its own and the deleted lines of the inline view, in the order it draws them. */
function drawnLines(view: EditorView): Element[] {
   return [...view.contentDOM.querySelectorAll(':scope > .cm-line, .cm-deletedLine')];
}

/** The code of a line, without the labels and the thread markers that the editor draws in it. */
function code(line: Element): string {
   const clone = line.cloneNode(true) as Element;
   clone.querySelectorAll('.lotsen-passageLabels, .lotsen-threadMarkers').forEach((element) => element.remove());
   return clone.textContent;
}

function line(view: EditorView, text: string): Element {
   const found = drawnLines(view).find((candidate) => code(candidate) === text);
   expect(found, `a line "${text}"`).toBeDefined();
   return found!;
}

/** A point of the page: a node and an offset into it. */
type Point = [Node, number];

/** Selects from one point to another, and reports the change as the browser does. */
function selectPoints([startNode, startOffset]: Point, [endNode, endOffset]: Point): void {
   const range = document.createRange();
   range.setStart(startNode, startOffset);
   range.setEnd(endNode, endOffset);
   const selection = document.getSelection()!;
   selection.removeAllRanges();
   selection.addRange(range);
   document.dispatchEvent(new Event('selectionchange'));
}

/** Selects from the start of one line to the end of another, or to the start of it with `toStart`. */
function select(from: Element, to: Element, toStart = false): void {
   selectPoints([from, 0], [to, toStart ? 0 : to.childNodes.length]);
}

function textOf(element: Element): Text {
   const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
   return walker.nextNode() as Text;
}

/** The point at the start of a line. */
const startOf = (line: Element): Point => [line, 0];

/** The points at the end of a line that a browser gives a press to its right: after its last child, and after its code's last character. */
const endsOf = (line: Element): Point[] => [
   [line, line.childNodes.length],
   [textOf(line), textOf(line).length],
];

function clearSelection(): void {
   document.getSelection()!.removeAllRanges();
   document.dispatchEvent(new Event('selectionchange'));
}

const askButton = () => screen.queryByRole('button', { name: /^Ask Claude about / });

async function asked(onask: ReturnType<typeof vi.fn>, name: string): Promise<PickedAnchor> {
   const button = await waitFor(() => screen.getByRole('button', { name: `Ask Claude about ${name}` }));
   await fireEvent.click(button);
   expect(onask).toHaveBeenCalledTimes(1);
   return onask.mock.calls[0]![0] as PickedAnchor;
}

function renderDiff(path: string, diff: TextDiff, layout: DiffLayout, onask = vi.fn(), decorations?: PassageDecorations) {
   const view = render(DiffView, { props: { path, known: loaded(path, diff), layout, onask, ...(decorations === undefined ? {} : { decorations }) } });
   return { view, onask };
}

// The head's line 2 replaces the base's lines 2 and 3, and the head adds a line at its end.
const base = 'one\ntwo\nthree\nfour\nfive\n';
const head = 'one\nnew\nfour\nfive\nadded\n';
const replaced = modified(base, head, [
   { fromA: 4, toA: 14, fromB: 4, toB: 8 },
   { fromA: 24, toA: 24, fromB: 18, toB: 24 },
]);
const crlf = (text: string) => text.replaceAll('\n', '\r\n');
const replacedCrlf = modified(crlf(base), crlf(head), [
   { fromA: 5, toA: 17, fromB: 5, toB: 10 },
   { fromA: 29, toA: 29, fromB: 22, toB: 29 },
]);

describe('asking about selected lines', () => {
   describe.each<[string, TextDiff]>([
      ['LF', replaced],
      ['CRLF', replacedCrlf],
   ])('with %s line breaks', (_, fileDiff) => {
      it('asks with the head lines selected in the head pane, side by side', async () => {
         const { onask } = renderDiff('a.txt', fileDiff, 'sideBySide');
         const [, b] = await editors(2);

         select(line(b!, 'new'), line(b!, 'four'));

         expect(await asked(onask, 'a.txt, lines 2–3')).toEqual({ kind: 'code', side: 'head', path: 'a.txt', startLine: 2, endLine: 3 });
      });

      it('asks with the base lines selected in the base pane, side by side', async () => {
         const { onask } = renderDiff('a.txt', fileDiff, 'sideBySide');
         const [a] = await editors(2);

         select(line(a!, 'three'), line(a!, 'five'));

         expect(await asked(onask, 'a.txt, lines 3–5 at the base')).toEqual({ kind: 'code', side: 'base', path: 'a.txt', startLine: 3, endLine: 5 });
      });

      it('asks with the head lines selected inline, unchanged and added', async () => {
         const { onask } = renderDiff('a.txt', fileDiff, 'inline');
         const [view] = await editors(1);

         select(line(view!, 'four'), line(view!, 'added'));

         expect(await asked(onask, 'a.txt, lines 3–5')).toEqual({ kind: 'code', side: 'head', path: 'a.txt', startLine: 3, endLine: 5 });
      });

      it('asks with the base lines of an inline deleted chunk', async () => {
         const { onask } = renderDiff('a.txt', fileDiff, 'inline');
         const [view] = await editors(1);

         select(line(view!, 'three'), line(view!, 'three'));

         expect(await asked(onask, 'a.txt, line 3 at the base')).toEqual({ kind: 'code', side: 'base', path: 'a.txt', startLine: 3, endLine: 3 });
      });

      it('asks with base lines for an unchanged line and the deleted lines below it, inline', async () => {
         const { onask } = renderDiff('a.txt', fileDiff, 'inline');
         const [view] = await editors(1);

         select(line(view!, 'one'), line(view!, 'two'));

         expect(await asked(onask, 'a.txt, lines 1–2 at the base')).toEqual({ kind: 'code', side: 'base', path: 'a.txt', startLine: 1, endLine: 2 });
      });
   });

   it('leaves out a last line that the selection reaches only at its start', async () => {
      const { onask } = renderDiff('a.txt', replaced, 'sideBySide');
      const [, b] = await editors(2);

      select(line(b!, 'four'), line(b!, 'added'), true);

      expect(await asked(onask, 'a.txt, lines 3–4')).toMatchObject({ startLine: 3, endLine: 4 });
   });

   describe.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('at the ends of lines, %s', (layout, count) => {
      async function headLines(decorations?: PassageDecorations) {
         const { onask } = renderDiff('a.txt', replaced, layout, vi.fn(), decorations);
         const views = await editors(count);
         return { onask, at: (text: string) => line(views.at(-1)!, text) };
      }

      async function noAsk(): Promise<void> {
         await new Promise((resolve) => setTimeout(resolve, 10));
         expect(askButton()).toBeNull();
      }

      it('leaves out a first line that the selection leaves only at its end', async () => {
         const { onask, at } = await headLines();
         for (const start of endsOf(at('new'))) {
            onask.mockClear();
            selectPoints(start, endsOf(at('four'))[0]!);

            expect(await asked(onask, 'a.txt, line 3')).toMatchObject({ side: 'head', startLine: 3, endLine: 3 });
         }
      });

      it('leaves out the labels and thread markers drawn at the end of a first line', async () => {
         const { onask, at } = await headLines({
            passages: [{ side: 'head', startLine: 2, endLine: 2, role: 'primary', label: 'Label' }],
            dim: false,
            highlight: true,
            collapse: false,
            threads: [{ id: 't', side: 'head', startLine: 2, endLine: 2, label: 'Marker' }],
         });
         await waitFor(() => expect(at('new').querySelector('.lotsen-threadMarkers')).not.toBeNull());
         const marker = textOf(at('new').querySelector('.lotsen-threadMarkers')!);
         for (const start of [...endsOf(at('new')), [marker, 2] satisfies Point]) {
            onask.mockClear();
            selectPoints(start, endsOf(at('four'))[0]!);

            expect(await asked(onask, 'a.txt, line 3')).toMatchObject({ startLine: 3, endLine: 3 });
         }
      });

      it('asks about the one line between the end of a line and the start of the line after the next', async () => {
         const { onask, at } = await headLines();

         selectPoints(endsOf(at('new'))[1]!, startOf(at('five')));

         expect(await asked(onask, 'a.txt, line 3')).toMatchObject({ startLine: 3, endLine: 3 });
      });

      it('asks about one whole line selected with its line break', async () => {
         const { onask, at } = await headLines();

         select(at('four'), at('five'), true);

         expect(await asked(onask, 'a.txt, line 3')).toMatchObject({ startLine: 3, endLine: 3 });
      });

      it('asks about the line of a selection inside it', async () => {
         const { onask, at } = await headLines();

         selectPoints([textOf(at('four')), 1], [textOf(at('four')), 3]);

         expect(await asked(onask, 'a.txt, line 3')).toMatchObject({ startLine: 3, endLine: 3 });
      });

      it('offers no ask for a selection of only a line break', async () => {
         const { at } = await headLines();

         selectPoints(endsOf(at('new'))[1]!, startOf(at('four')));

         await noAsk();
      });

      it('offers no ask for a selection that starts and ends at the end of one line', async () => {
         const { at } = await headLines();

         selectPoints(endsOf(at('four'))[1]!, endsOf(at('four'))[0]!);

         await noAsk();
      });
   });

   it('leaves out a first line that the selection leaves only at its end, at an inline deleted line and at the unchanged line above one', async () => {
      const { onask } = renderDiff('a.txt', replaced, 'inline');
      const [view] = await editors(1);

      selectPoints(endsOf(line(view!, 'two'))[1]!, endsOf(line(view!, 'three'))[0]!);
      expect(await asked(onask, 'a.txt, line 3 at the base')).toMatchObject({ side: 'base', startLine: 3, endLine: 3 });

      onask.mockClear();
      selectPoints(endsOf(line(view!, 'one'))[0]!, endsOf(line(view!, 'two'))[0]!);
      expect(await asked(onask, 'a.txt, line 2 at the base')).toMatchObject({ side: 'base', startLine: 2, endLine: 2 });
   });

   it('offers no ask for a selection across both panes, and says why', async () => {
      renderDiff('a.txt', replaced, 'sideBySide');
      const [a, b] = await editors(2);

      select(line(a!, 'two'), line(b!, 'four'));

      await waitFor(() => expect(screen.getByText('Select lines of one side to ask about them.')).toBeTruthy());
      expect(askButton()).toBeNull();
   });

   it('offers no ask for deleted lines with the line that replaces them, inline, and says why', async () => {
      renderDiff('a.txt', replaced, 'inline');
      const [view] = await editors(1);

      select(line(view!, 'three'), line(view!, 'new'));

      await waitFor(() => expect(screen.getByText('Select lines of one side to ask about them.')).toBeTruthy());
      expect(askButton()).toBeNull();
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('asks about lines deleted at the end of the file, %s', async (layout, count) => {
      const { onask } = renderDiff('a.txt', modified('a\nb\nc\n', 'a\n', [{ fromA: 2, toA: 6, fromB: 2, toB: 2 }]), layout);
      const views = await editors(count);

      select(line(views[0]!, 'b'), line(views[0]!, 'c'));

      expect(await asked(onask, 'a.txt, lines 2–3 at the base')).toMatchObject({ side: 'base', startLine: 2, endLine: 3 });
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('asks about lines added at the end of the file, %s', async (layout, count) => {
      const { onask } = renderDiff('a.txt', modified('a\n', 'a\nb\nc\n', [{ fromA: 2, toA: 2, fromB: 2, toB: 6 }]), layout);
      const views = await editors(count);

      select(line(views.at(-1)!, 'b'), line(views.at(-1)!, 'c'));

      expect(await asked(onask, 'a.txt, lines 2–3')).toMatchObject({ side: 'head', startLine: 2, endLine: 3 });
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('asks about the lines of an added file and of a deleted file, %s', async (layout, count) => {
      const { view, onask } = renderDiff('new.txt', modified(null, 'x\ny\n', [{ fromA: 0, toA: 0, fromB: 0, toB: 4 }]), layout);
      let views = await editors(count);
      select(line(views.at(-1)!, 'y'), line(views.at(-1)!, 'y'));
      expect(await asked(onask, 'new.txt, line 2')).toMatchObject({ side: 'head', startLine: 2, endLine: 2 });

      await view.rerender({ path: 'old.txt', known: loaded('old.txt', modified('x\ny\n', null, [{ fromA: 0, toA: 4, fromB: 0, toB: 0 }])) });
      views = await editors(count);
      await waitFor(() => expect(views[0]!.state.doc.toString()).toBe('x\ny\n'));
      select(line(views[0]!, 'x'), line(views[0]!, 'y'));
      await fireEvent.click(await waitFor(() => screen.getByRole('button', { name: 'Ask Claude about old.txt, lines 1–2 at the base' })));
      expect(onask).toHaveBeenLastCalledWith({ kind: 'code', side: 'base', path: 'old.txt', startLine: 1, endLine: 2 }, 'x');
   });

   it('asks about the lines selected in a file’s text, at its side', async () => {
      const onask = vi.fn();
      render(FileView, {
         props: { known: { state: 'loaded', side: 'base', path: 'src/same.ts', text: { content: 'text', side: 'base', path: 'src/same.ts', commit: 'c'.repeat(40), text: 'a\r\nb\r\nc\r\n' } }, onask },
      });
      const [view] = await editors(1);

      select(line(view!, 'b'), line(view!, 'c'));

      expect(await asked(onask, 'src/same.ts, lines 2–3 at the base')).toEqual({ kind: 'code', side: 'base', path: 'src/same.ts', startLine: 2, endLine: 3 });
   });

   it('hides the ask once the selection collapses, and offers none for a selection outside the editors', async () => {
      renderDiff('a.txt', replaced, 'sideBySide');
      const [, b] = await editors(2);
      select(line(b!, 'new'), line(b!, 'new'));
      await waitFor(() => expect(askButton()).not.toBeNull());

      clearSelection();
      await waitFor(() => expect(askButton()).toBeNull());

      const outside = document.body.appendChild(document.createElement('p'));
      outside.textContent = 'elsewhere';
      select(outside, outside);
      await new Promise((resolve) => setTimeout(resolve, 10));
      expect(askButton()).toBeNull();
      outside.remove();
   });

   it('drops the ask of a file whose editors went, so a selection made before another file loads never asks about the new file', async () => {
      const { view, onask } = renderDiff('a.txt', replaced, 'sideBySide');
      const [, b] = await editors(2);
      select(line(b!, 'new'), line(b!, 'new'));
      await waitFor(() => expect(askButton()).not.toBeNull());

      await view.rerender({ path: 'b.txt', known: { state: 'loading', path: 'b.txt' } });

      await waitFor(() => expect(askButton()).toBeNull());
      expect(onask).not.toHaveBeenCalled();
   });

   it('offers no ask without a place to send it', async () => {
      render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', replaced), layout: 'sideBySide' } });
      const [, b] = await editors(2);

      select(line(b!, 'new'), line(b!, 'new'));
      await new Promise((resolve) => setTimeout(resolve, 10));

      expect(askButton()).toBeNull();
   });
});

describe('thread markers', () => {
   const decorations: PassageDecorations = {
      passages: [],
      dim: true,
      highlight: true,
      collapse: false,
      threads: [
         { id: 'base-thread', side: 'base', startLine: 3, endLine: 4, label: 'Why is three gone?' },
         { id: 'head-thread', side: 'head', startLine: 2, endLine: 2, label: 'What is new?' },
      ],
   };

   const markerOn = (line: Element) => [...line.querySelectorAll<HTMLButtonElement>('button[data-thread-id]')].map((button) => button.getAttribute('aria-label'));

   function renderMarked(layout: DiffLayout, onopenthread = vi.fn()) {
      render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', replaced), layout, decorations, onopenthread } });
      return onopenthread;
   }

   it('marks each thread on the first line of its passage at its side, undimmed, and opens it from its marker, side by side', async () => {
      const onopenthread = renderMarked('sideBySide');
      const [a, b] = await editors(2);

      await waitFor(() => expect(markerOn(line(a!, 'three'))).toEqual(['Open the thread: Why is three gone?']));
      expect(markerOn(line(b!, 'new'))).toEqual(['Open the thread: What is new?']);
      expect(line(a!, 'three').classList.contains('lotsen-dimmed')).toBe(false);
      expect(line(a!, 'two').classList.contains('lotsen-dimmed')).toBe(true);
      const threadNumbers = [...a!.dom.querySelectorAll('.cm-lineNumbers .cm-gutterElement.lotsen-thread')].map((element) => element.textContent);
      expect(threadNumbers).toEqual(['3', '4']);

      await fireEvent.click(line(a!, 'three').querySelector('button')!);

      expect(onopenthread).toHaveBeenCalledWith('base-thread');
   });

   it('marks a base thread on its deleted line, and opens it from there, inline', async () => {
      const onopenthread = renderMarked('inline');
      const [view] = await editors(1);

      await waitFor(() => expect(markerOn(line(view!, 'three'))).toEqual(['Open the thread: Why is three gone?']));
      expect(markerOn(line(view!, 'new'))).toEqual(['Open the thread: What is new?']);

      await fireEvent.click(line(view!, 'three').querySelector('button')!);

      expect(onopenthread).toHaveBeenCalledWith('base-thread');
   });

   it('marks the deleted lines and the gutter of the head lines that a base thread covers, inline', async () => {
      renderMarked('inline');
      const [view] = await editors(1);

      await waitFor(() => expect(line(view!, 'three').classList.contains('lotsen-thread')).toBe(true));
      expect(line(view!, 'two').classList.contains('lotsen-thread')).toBe(false);
      const threadNumbers = [...view!.dom.querySelectorAll('.cm-lineNumbers .cm-gutterElement.lotsen-thread')].map((element) => element.textContent);
      expect(threadNumbers).toEqual(['2', '3']);
   });

   it('clears the mark of a deleted line once its thread goes, inline', async () => {
      const { rerender } = render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', replaced), layout: 'inline', decorations } });
      const [view] = await editors(1);
      await waitFor(() => expect(line(view!, 'three').classList.contains('lotsen-thread')).toBe(true));

      await rerender({ decorations: { ...decorations, threads: [] } });

      await waitFor(() => expect(line(view!, 'three').classList.contains('lotsen-thread')).toBe(false));
   });

   it('shows a marker’s label as text', async () => {
      render(DiffView, {
         props: {
            path: 'a.txt',
            known: loaded('a.txt', replaced),
            layout: 'sideBySide',
            decorations: { ...decorations, threads: [{ id: 't', side: 'head', startLine: 1, endLine: 1, label: '<img src=x onerror=alert(1)>' }] },
         },
      });
      const [, b] = await editors(2);

      await waitFor(() => expect(line(b!, 'one').querySelector('button')?.textContent).toBe('<img src=x onerror=alert(1)>'));
      expect(document.querySelector('.editor img:not(.cm-widgetBuffer)')).toBeNull();
   });
});

describe('selecting inside the editors’ widgets', () => {
   /** Presses the mouse on an element, and returns whether the page kept the browser's own handling of the press. */
   function press(element: Element, init: MouseEventInit): boolean {
      return element.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0, detail: 1, ...init }));
   }

   it('selects the deleted lines that a drag covers, which the browser alone does not, and asks about them, inline', async () => {
      const { onask } = renderDiff('a.txt', replaced, 'inline');
      const [view] = await editors(1);
      const two = textOf(line(view!, 'two'));
      const three = textOf(line(view!, 'three'));
      // jsdom has no layout, so each point names the text it stands for: y 1 for "two", y 2 for "three".
      const points: Record<number, { offsetNode: Node; offset: number }> = { 1: { offsetNode: two, offset: 1 }, 2: { offsetNode: three, offset: 3 } };
      const document_ = document as unknown as { caretPositionFromPoint?: (x: number, y: number) => unknown };
      document_.caretPositionFromPoint = (_x: number, y: number) => points[y] ?? null;
      try {
         const kept = press(line(view!, 'two'), { clientX: 5, clientY: 1 });
         document.dispatchEvent(new MouseEvent('mousemove', { clientX: 5, clientY: 2 }));
         document.dispatchEvent(new MouseEvent('mouseup'));
         document.dispatchEvent(new Event('selectionchange'));

         expect(kept).toBe(false);
         expect(document.getSelection()!.toString()).toBe('wothr');
         expect(await asked(onask, 'a.txt, lines 2–3 at the base')).toMatchObject({ side: 'base', startLine: 2, endLine: 3 });
      } finally {
         delete document_.caretPositionFromPoint;
      }
   });

   it('stops following a drag once its editor goes', async () => {
      const { view: rendered } = renderDiff('a.txt', replaced, 'inline');
      const [view] = await editors(1);
      const two = textOf(line(view!, 'two'));
      let reads = 0;
      const document_ = document as unknown as { caretPositionFromPoint?: (x: number, y: number) => unknown };
      document_.caretPositionFromPoint = () => {
         reads++;
         return { offsetNode: two, offset: 1 };
      };
      try {
         press(line(view!, 'two'), { clientX: 5, clientY: 1 });
         document.dispatchEvent(new MouseEvent('mousemove', { clientX: 5, clientY: 2 }));
         expect(reads).toBe(2);

         rendered.unmount();
         document.dispatchEvent(new MouseEvent('mousemove', { clientX: 5, clientY: 2 }));

         expect(reads).toBe(2);
      } finally {
         delete document_.caretPositionFromPoint;
         document.dispatchEvent(new MouseEvent('mouseup'));
      }
   });

   it('leaves a press on a thread marker to the marker', async () => {
      render(DiffView, {
         props: {
            path: 'a.txt',
            known: loaded('a.txt', replaced),
            layout: 'inline',
            decorations: { passages: [], dim: false, highlight: true, collapse: false, threads: [{ id: 't', side: 'base', startLine: 2, endLine: 2, label: 'Gone?' }] },
         },
      });
      const [view] = await editors(1);
      const marker = await waitFor(() => line(view!, 'two').querySelector('button')!);

      expect(press(marker, {})).toBe(true);
   });
});
