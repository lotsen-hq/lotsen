import { EditorView } from '@codemirror/view';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import StopFiles from '../src/components/StopFiles.svelte';
import StopFilesHost from './fixtures/StopFilesHost.svelte';
import type { KnownDiff, KnownText } from '../src/lib/viewState';
import type { DiffLayout } from '../src/lib/diffLayout';
import type { DecorationSwitches } from '../src/lib/passageMarks';
import { stopFileStack, type FileReference } from '../src/lib/stopFiles';
import type { ChangedFile, FileDiff, FileMark, FileSide, Stop, StopPassage } from '../src/lib/types';

const on: DecorationSwitches = { dim: true, highlight: true };

const commit = 'c'.repeat(40);

// Forty numbered lines, of which the line `changed` differs at the head.
const numbered = (name: string, changed = 0) => ({
   base: Array.from({ length: 40 }, (_, index) => `${name} line ${index + 1}\n`).join(''),
   head: Array.from({ length: 40 }, (_, index) => (index + 1 === changed ? `${name} head line ${index + 1}\n` : `${name} line ${index + 1}\n`)).join(''),
});
const lineStart = (text: string, line: number) =>
   text
      .split('\n')
      .slice(0, line - 1)
      .join('\n').length + (line > 1 ? 1 : 0);

function textDiff(name: string, changed: number): FileDiff {
   const { base, head } = numbered(name, changed);
   return {
      content: 'text',
      baseText: base,
      headText: head,
      changes: [{ fromA: lineStart(base, changed), toA: lineStart(base, changed + 1), fromB: lineStart(head, changed), toB: lineStart(head, changed + 1) }],
   };
}

function stopPassage(side: FileSide, path: string, startLine: number, endLine: number, role: StopPassage['role'] = 'related'): StopPassage {
   return { side, path, startLine, endLine, role, commit, text: 'text' };
}

const files: ChangedFile[] = [
   { path: 'src/cart.ts', kind: 'modified' },
   { path: 'src/pay.ts', kind: 'modified' },
   { path: 'src/big.ts', kind: 'modified' },
];
const diffs = new Map<string, FileDiff>([
   ['src/cart.ts', textDiff('cart', 20)],
   ['src/pay.ts', textDiff('pay', 21)],
]);

function stopOf(passages: StopPassage[]): Stop {
   return {
      id: 's1',
      title: 'A stop',
      kind: 'inspect',
      chapter: 'only',
      stage: 'written',
      status: 'ready',
      version: 1,
      earlier: [],
      content: { headline: 'A stop.', passages, html: { headline: '<p>A stop.</p>\n' } },
   };
}

const passages = [stopPassage('head', 'src/pay.ts', 30, 31), stopPassage('head', 'src/cart.ts', 5, 7, 'primary'), stopPassage('head', 'src/catalog.ts', 3, 4)];

let loadDiff: ReturnType<typeof vi.fn<(file: ChangedFile, options: { includeLarge: boolean; ignoreWhitespace: boolean }) => Promise<KnownDiff>>>;
let loadText: ReturnType<typeof vi.fn<(side: FileSide, path: string, includeLarge: boolean) => Promise<KnownText>>>;

beforeEach(() => {
   loadDiff = vi.fn((file, { includeLarge }) => {
      const diff: FileDiff = file.path === 'src/big.ts' ? (includeLarge ? textDiff('big', 2) : { content: 'large', size: 2_000_000 }) : diffs.get(file.path)!;
      return Promise.resolve({ state: 'loaded', path: file.path, diff });
   });
   loadText = vi.fn((side, path) => Promise.resolve({ state: 'loaded', side, path, text: { content: 'text', path, side, commit, text: numbered('catalog').head } }));
});

afterEach(() => vi.restoreAllMocks());

function renderStack(stopPassages: StopPassage[] = passages, layout: DiffLayout = 'sideBySide', added: FileReference[] = []) {
   return render(StopFiles, { props: { files: stopFileStack(stopPassages, files, added, on), stop: stopOf(stopPassages), layout, loadDiff, loadText } });
}

const items = () => within(screen.getByRole('list', { name: 'Files in this stop' })).queryAllByRole('listitem');
const headers = () => items().map((item) => item.querySelector('header')!.textContent.replace(/\s+/g, ' ').trim());
const editorsOf = (item: HTMLElement) => [...item.querySelectorAll<HTMLElement>('.cm-editor')].map((dom) => EditorView.findFromDOM(dom)!);

/** The line numbers that an editor draws, with each collapsed marker as 0. */
function drawnLines(view: EditorView): number[] {
   return [...view.contentDOM.children]
      .filter((element) => element.classList.contains('cm-line') || element.classList.contains('lotsen-collapsed'))
      .map((element) => (element.classList.contains('cm-line') ? view.state.doc.lineAt(view.posAtDOM(element)).number : 0));
}

describe('the stop-files stack', () => {
   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('stacks exactly the stop’s files in passage order, each collapsed to its own passages, %s', async (layout, count) => {
      renderStack(passages, layout);

      expect(headers()).toEqual(['src/cart.ts', 'src/pay.ts', 'src/catalog.ts head']);
      await waitFor(() => expect(items().map((item) => editorsOf(item).length)).toEqual([count, count, 1]));
      const [cart, pay, catalog] = items().map((item) => editorsOf(item).at(-1)!);

      await waitFor(() => expect(drawnLines(cart!)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0]));
      expect(drawnLines(pay!)).toEqual([0, 27, 28, 29, 30, 31, 32, 33, 34, 0]);
      expect(drawnLines(catalog!)).toEqual([1, 2, 3, 4, 5, 6, 7, 0]);
      expect(cart!.contentDOM.querySelector('.cm-line.lotsen-passage-primary')?.textContent).toBe('cart line 5');
      expect(catalog!.contentDOM.querySelector('.cm-line.lotsen-passage-related')?.textContent).toBe('catalog line 3');
   });

   it('moves the splitter of every side-by-side file with the splitter of one', async () => {
      localStorage.clear();
      render(StopFilesHost, { props: { files: stopFileStack(passages.slice(0, 2), files, [], on), stop: stopOf(passages.slice(0, 2)), loadDiff, loadText } });
      const splitters = () => screen.getAllByRole('separator', { name: 'Resize the base and head panes' });
      await waitFor(() => expect(splitters()).toHaveLength(2));

      await fireEvent.keyDown(splitters()[1]!, { key: 'ArrowLeft' });

      expect(items().map((item) => [...item.querySelectorAll<HTMLElement>('.cm-mergeViewEditor')].map((pane) => pane.style.flexGrow))).toEqual([
         ['40', '60'],
         ['40', '60'],
      ]);
   });

   it('expands a region of one file and leaves the others collapsed', async () => {
      renderStack(passages, 'inline');
      await waitFor(() => expect(items().every((item) => item.querySelector('.lotsen-collapsed') !== null)).toBe(true));
      const [cart, pay] = items();

      await fireEvent.click(within(cart!.querySelector<HTMLElement>('.lotsen-collapsed')!).getByRole('button', { name: 'Show all' }));

      expect(cart!.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);
      expect(pay!.querySelectorAll('.lotsen-collapsed')).toHaveLength(2);
   });

   it('shows the mark of a marked file in its header, without collapsing the file behind the mark', async () => {
      render(StopFiles, {
         props: {
            files: stopFileStack(passages.slice(0, 2), files, [], on),
            stop: stopOf(passages.slice(0, 2)),
            fileMarks: new Map<string, FileMark>([['src/pay.ts', { path: 'src/pay.ts', mark: 'generated', reason: 'The SDK writes it.' }]]),
            loadDiff,
            loadText,
         },
      });

      expect(headers()).toEqual(['src/cart.ts', 'src/pay.ts generated']);
      await waitFor(() => expect(editorsOf(items()[1]!)).toHaveLength(2));
      expect(screen.queryByRole('button', { name: 'Show the diff' })).toBeNull();
   });

   it('marks a file added from outside the stop and stacks it last, without passages to collapse to', async () => {
      renderStack(passages.slice(1, 2), 'sideBySide', [{ side: 'head', path: 'src/pay.ts' }]);

      expect(headers()).toEqual(['src/cart.ts', 'src/pay.ts Outside the stop']);
      await waitFor(() => expect(editorsOf(items()[1]!)).toHaveLength(2));
      expect(items()[1]!.querySelectorAll('.lotsen-collapsed, .lotsen-dimmed')).toHaveLength(0);
   });

   it('keeps the editors and the content of the files that a new stack keeps, in the new order', async () => {
      const destroy = vi.spyOn(EditorView.prototype, 'destroy');
      const view = renderStack(passages, 'inline');
      await waitFor(() => expect(items().map((item) => editorsOf(item).length)).toEqual([1, 1, 1]));
      const [cartEditor, payEditor] = items().map((item) => editorsOf(item)[0]);
      const reordered = [stopPassage('head', 'src/pay.ts', 30, 31, 'primary'), stopPassage('head', 'src/cart.ts', 5, 7), stopPassage('head', 'src/big.ts', 1, 1)];

      await view.rerender({ files: stopFileStack(reordered, files, [], on), stop: stopOf(reordered) });

      expect(headers()).toEqual(['src/pay.ts', 'src/cart.ts', 'src/big.ts']);
      expect(
         items()
            .slice(0, 2)
            .map((item) => editorsOf(item)[0]),
      ).toEqual([payEditor, cartEditor]);
      expect(destroy).toHaveBeenCalledTimes(1);
      expect(loadDiff.mock.calls.map(([file]) => file.path)).toEqual(['src/cart.ts', 'src/pay.ts', 'src/big.ts']);
      expect(loadText).toHaveBeenCalledTimes(1);
   });

   it('keeps the large-file guard, and loads a large file’s diff when the reviewer asks', async () => {
      renderStack([stopPassage('head', 'src/cart.ts', 5, 7, 'primary'), stopPassage('head', 'src/big.ts', 2, 2)]);
      const big = items()[1]!;

      await fireEvent.click(await within(big).findByRole('button', { name: 'Load the diff' }));

      await waitFor(() => expect(editorsOf(big)).toHaveLength(2));
      expect(loadDiff).toHaveBeenLastCalledWith(files[2], { includeLarge: true, ignoreWhitespace: false });
   });

   it('loads the diffs again, and not the texts, when the whitespace choice changes', async () => {
      const view = render(StopFiles, { props: { files: stopFileStack(passages, files, [], on), stop: stopOf(passages), loadDiff, loadText } });
      await waitFor(() => expect(loadDiff).toHaveBeenCalledTimes(2));

      await view.rerender({ ignoreWhitespace: true });

      expect(loadDiff.mock.calls.slice(2).map(([file, options]) => [file.path, options.ignoreWhitespace])).toEqual([
         ['src/cart.ts', true],
         ['src/pay.ts', true],
      ]);
      expect(loadText).toHaveBeenCalledTimes(1);
   });

   it('loads the diffs and the texts again when the tour moves to another pin', async () => {
      const view = render(StopFiles, { props: { files: stopFileStack(passages, files, [], on), stop: stopOf(passages), loadDiff, loadText } });
      await waitFor(() => expect(loadDiff).toHaveBeenCalledTimes(2));

      await view.rerender({ pin: `${'a'.repeat(40)}..${'c'.repeat(40)}` });

      expect(loadDiff.mock.calls.slice(2).map(([file]) => file.path)).toEqual(['src/cart.ts', 'src/pay.ts']);
      expect(loadText).toHaveBeenCalledTimes(2);
   });

   it('keeps the diff of the newest load when an earlier load of the same file answers late', async () => {
      let release!: () => void;
      loadDiff.mockImplementationOnce((file) => new Promise((resolve) => (release = () => resolve({ state: 'loaded', path: file.path, diff: diffs.get(file.path)! }))));
      loadDiff.mockImplementationOnce((file) => Promise.resolve({ state: 'loaded', path: file.path, diff: textDiff('spaced', 20) }));
      const view = renderStack(passages.slice(1, 2));

      await view.rerender({ ignoreWhitespace: true });
      await waitFor(() => expect(editorsOf(items()[0]!)[0]?.state.doc.toString()).toContain('spaced line 1'));
      release();
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(editorsOf(items()[0]!)[0]!.state.doc.toString()).toContain('spaced line 1');
   });

   it('shows the remaining file when the diff of a file that left the stack arrives late', async () => {
      let release!: () => void;
      loadDiff.mockImplementationOnce((file) => new Promise((resolve) => (release = () => resolve({ state: 'loaded', path: file.path, diff: diffs.get(file.path)! }))));
      const view = renderStack(passages.slice(1, 2));

      await view.rerender({ files: stopFileStack(passages.slice(0, 1), files, [], on), stop: stopOf(passages.slice(0, 1)) });
      release();
      await waitFor(() => expect(editorsOf(items()[0]!)).toHaveLength(2));

      expect(headers()).toEqual(['src/pay.ts']);
      expect(editorsOf(items()[0]!)[0]!.state.doc.toString()).toContain('pay line 1');
   });

   it('scrolls to the file that each new target names', async () => {
      const scrolled: Element[] = [];
      Element.prototype.scrollIntoView = function (this: Element) {
         scrolled.push(this);
      };
      const view = renderStack();

      await view.rerender({ target: { key: 'text:head:src/catalog.ts' } });
      await view.rerender({ target: { key: 'diff:src/pay.ts' } });
      await view.rerender({ target: { key: 'diff:src/pay.ts' } });

      expect(scrolled).toEqual([items()[2], items()[1], items()[1]]);
      delete (Element.prototype as Partial<Element>).scrollIntoView;
   });

   describe('reporting the file the reviewer reads', () => {
      const keys = ['diff:src/cart.ts', 'diff:src/pay.ts', 'text:head:src/catalog.ts'];

      /**
       * Lays the stack out as files of the heights in `heights`, 100px each to start, in a viewport of the height in `viewport`, scrolled by `scrolled` pixels. A scroll into view scrolls as far as
       * the stack allows, and fires a scroll event only when the position changes, as a browser does.
       */
      function layOut(viewportHeight: number) {
         const layout = { scrolled: 0, viewport: viewportHeight, heights: keys.map(() => 100) };
         const topOf = (index: number) => layout.heights.slice(0, index).reduce((sum, height) => sum + height, 0);
         const measure = Element.prototype.getBoundingClientRect;
         vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
            if (this instanceof HTMLElement && this.dataset.key !== undefined) {
               const index = [...this.parentElement!.children].indexOf(this);
               return DOMRect.fromRect({ y: topOf(index) - layout.scrolled, height: layout.heights[index] });
            }

            return this.classList.contains('scroll-box') ? DOMRect.fromRect({ y: 0, height: layout.viewport }) : measure.call(this);
         });
         Element.prototype.scrollIntoView = function (this: Element) {
            const scroller = this.closest('.scroll-box')!;
            const next = Math.max(0, Math.min(topOf([...this.parentElement!.children].indexOf(this)), scroller.scrollHeight - scroller.clientHeight));
            if (next !== layout.scrolled) {
               layout.scrolled = next;
               scroller.dispatchEvent(new Event('scroll'));
            }
         };
         return { layout, contentHeight: () => topOf(keys.length) };
      }

      // The resize observers that the stack creates, which a test fires as a browser would after a layout change.
      let observers: { callback: ResizeObserverCallback; targets: Element[] }[] = [];
      beforeEach(() => {
         observers = [];
         vi.stubGlobal(
            'ResizeObserver',
            class {
               private readonly record: { callback: ResizeObserverCallback; targets: Element[] };
               constructor(callback: ResizeObserverCallback) {
                  this.record = { callback, targets: [] };
                  observers.push(this.record);
               }
               observe(target: Element) {
                  this.record.targets.push(target);
               }
               unobserve() {}
               disconnect() {
                  observers.splice(observers.indexOf(this.record), 1);
               }
            },
         );
      });
      afterEach(() => vi.unstubAllGlobals());
      const resize = () => observers.forEach((observer) => observer.callback([], {} as ResizeObserver));

      function renderHost(viewportHeight: number) {
         const { layout, contentHeight } = layOut(viewportHeight);
         const ontop = vi.fn<(key: string | null) => void>();
         const view = render(StopFilesHost, { props: { files: stopFileStack(passages, files, [], on), stop: stopOf(passages), loadDiff, loadText, ontop } });
         const scroller = screen.getByRole('list', { name: 'Files in this stop' }).parentElement!;
         Object.defineProperties(scroller, {
            scrollTop: { get: () => layout.scrolled },
            clientHeight: { get: () => layout.viewport },
            scrollHeight: { get: contentHeight },
         });
         return { layout, ontop, view, scroller };
      }

      afterEach(() => delete (Element.prototype as Partial<Element>).scrollIntoView);

      it('reports the file at the top of the viewport as the stack scrolls, and stops once it unmounts', async () => {
         const { layout, ontop, view, scroller } = renderHost(150);
         expect(items().map((item) => item.dataset.key)).toEqual(keys);

         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));
         layout.scrolled = 120;
         await fireEvent.scroll(scroller);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[1]));

         view.unmount();
         ontop.mockClear();
         layout.scrolled = 150;
         await fireEvent.scroll(scroller);
         await new Promise((resolve) => requestAnimationFrame(resolve));
         expect(ontop).not.toHaveBeenCalled();
      });

      // 300px of files fit a 400px viewport, and fill a 150px one three times over, with the last file's top at the viewport's middle at the end.
      it.each([
         [400, [2, 1, 0]],
         [150, [2, 1, 0]],
         [150, [2, 2, 0, 1]],
         [400, [1, 1, 2]],
      ])('reports each file that the reviewer opens in a %ipx viewport, opening the files %j in turn', async (viewportHeight, opened) => {
         const { ontop, view } = renderHost(viewportHeight);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));

         for (const index of opened) {
            view.component.open(keys[index]!);
            await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[index]));
         }
      });

      it('keeps reporting the file opened at the stack’s end while it grows, until the reviewer scrolls', async () => {
         const { layout, ontop, view, scroller } = renderHost(150);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));
         view.component.open(keys[2]!);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[2]));

         // The opened file's diff arrives: the file grows below the viewport's top, which leaves the stack short of its end.
         layout.heights[2] = 400;
         resize();
         await new Promise((resolve) => requestAnimationFrame(resolve));
         expect(ontop).toHaveBeenLastCalledWith(keys[2]);

         await fireEvent.wheel(scroller);
         layout.scrolled = 160;
         await fireEvent.scroll(scroller);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[1]));
         layout.heights[2] = 500;
         resize();
         await new Promise((resolve) => requestAnimationFrame(resolve));
         expect(ontop).toHaveBeenLastCalledWith(keys[1]);
      });

      it('keeps reporting the opened file when the stack is built again with the same files, as a dimming toggle does', async () => {
         const { layout, ontop, view } = renderHost(150);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));
         view.component.open(keys[2]!);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[2]));
         layout.heights[2] = 400;
         resize();
         await new Promise((resolve) => requestAnimationFrame(resolve));

         view.component.replaceFiles(stopFileStack(passages, files, [], { dim: false, highlight: true }));
         await new Promise((resolve) => requestAnimationFrame(resolve));

         expect(ontop).toHaveBeenLastCalledWith(keys[2]);
      });

      it('reports the top file once a shorter viewport hides the opened file', async () => {
         const { layout, ontop, view } = renderHost(150);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));
         view.component.open(keys[2]!);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[2]));

         // At its end at 150, the stack shows the opened file from 50px down, which a 40px viewport no longer reaches.
         layout.viewport = 40;
         resize();

         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[1]));
      });

      it('keeps reporting the opened file through the scroll that opening it causes', async () => {
         const { ontop, view } = renderHost(150);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));
         view.component.open(keys[1]!);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[1]));
         view.component.open(keys[2]!);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[2]));
         await new Promise((resolve) => requestAnimationFrame(resolve));

         expect(ontop).toHaveBeenLastCalledWith(keys[2]);
      });

      it('reports the last file once the reviewer scrolls a short stack to its end, and the first back at its top', async () => {
         const { layout, ontop, scroller } = renderHost(150);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));

         await fireEvent.wheel(scroller);
         layout.scrolled = 150;
         await fireEvent.scroll(scroller);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[2]));
         layout.scrolled = 0;
         await fireEvent.scroll(scroller);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));
      });

      it('counts a stack within a pixel of its end as at its end, and two pixels short as not', async () => {
         const { layout, ontop, scroller } = renderHost(150);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));

         await fireEvent.wheel(scroller);
         layout.scrolled = 149;
         await fireEvent.scroll(scroller);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[2]));
         layout.scrolled = 148;
         await fireEvent.scroll(scroller);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[1]));
      });

      // 300px of files fill a 300px viewport exactly, and a 400px one with room to spare.
      it.each([300, 400])('reports the first file of a stack that cannot scroll in a %ipx viewport', async (viewportHeight) => {
         const { ontop, scroller } = renderHost(viewportHeight);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));

         await fireEvent.wheel(scroller);
         await fireEvent.scroll(scroller);
         await new Promise((resolve) => requestAnimationFrame(resolve));

         expect(ontop).toHaveBeenLastCalledWith(keys[0]);
      });

      it('keeps reporting an opened file that is not the last when opening it scrolls the stack to its end', async () => {
         // A 250px viewport ends at 50px, short of the middle file's top at 100px.
         const { layout, ontop, view } = renderHost(250);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));

         view.component.open(keys[1]!);
         await waitFor(() => expect(layout.scrolled).toBe(50));
         await new Promise((resolve) => requestAnimationFrame(resolve));

         expect(ontop).toHaveBeenLastCalledWith(keys[1]);
      });

      it('measures again when the viewport resizes, as well as the stack', async () => {
         const { scroller } = renderHost(150);

         await waitFor(() => expect(observers.flatMap((observer) => observer.targets)).toEqual([screen.getByRole('list', { name: 'Files in this stop' }), scroller]));
      });

      it('stops measuring while the changed files load again, and measures once they are back', async () => {
         const { ontop, view } = renderHost(150);
         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));

         view.component.replaceFiles(null);
         await waitFor(() => expect(screen.getByText('Loading the changed files…')).toBeTruthy());
         await new Promise((resolve) => requestAnimationFrame(resolve));
         ontop.mockClear();
         view.component.replaceFiles(stopFileStack(passages, files, [], on));

         await waitFor(() => expect(ontop).toHaveBeenLastCalledWith(keys[0]));
      });
   });

   it('says that the changed files load, that a pending stop has no content and that a stop without passages has none', async () => {
      const { id, title, kind, chapter } = stopOf([]);
      const pending: Stop = { id, title, kind, chapter, stage: 'pending', anchorHint: null, status: 'pending' };
      const view = render(StopFiles, { props: { files: null, stop: pending, loadDiff, loadText } });
      expect(screen.getByText('Loading the changed files…')).toBeTruthy();

      await view.rerender({ files: [] });
      expect(screen.getByText('Claude has not written this stop yet.')).toBeTruthy();

      await view.rerender({ stop: stopOf([]) });
      expect(screen.getByText('This stop has no passages.')).toBeTruthy();
      expect(items()).toEqual([]);
   });
});
