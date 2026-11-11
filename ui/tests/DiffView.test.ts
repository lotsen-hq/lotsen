import { language } from '@codemirror/language';
import { Chunk, getChunks } from '@codemirror/merge';
import { Text } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest';
import DiffView from '../src/components/DiffView.svelte';
import type { KnownDiff } from '../src/lib/viewState';
import type { LineFocus } from '../src/lib/openStop.svelte';
import { createDiffEditor, lineStart, linePosition, normalizeLineBreaks, scrollToPosition, serverDiffConfig, type DiffEditor } from '../src/lib/editor/diffEditor';
import type { DiffLayout } from '../src/lib/diffLayout';
import { languageNameFor, loadLanguageFor } from '../src/lib/editor/languages';
import { noPassageDecorations, type DecoratedPassage, type PassageDecorations } from '../src/lib/passageMarks';
import type { FileDiff, FileSide, TextDiff } from '../src/lib/types';
import { stubRangeLayout } from './rangeLayout';

afterEach(() => vi.restoreAllMocks());

// A change of the one line of a one-line file on each side.
const oneLine = { fromA: 0, toA: 2, fromB: 0, toB: 2 };

function loaded(path: string, diff: FileDiff): KnownDiff {
   return { state: 'loaded', path, diff };
}

function modified(base: string, head: string, changes: TextDiff['changes']): TextDiff {
   return { content: 'text', baseText: base, headText: head, changes };
}

function renderDiff(path: string, diff: FileDiff, layout: DiffLayout = 'sideBySide') {
   return render(DiffView, { props: { path, known: loaded(path, diff), layout } });
}

/** Waits for the editors to mount, after the file's language has loaded. */
async function editors(count: number): Promise<EditorView[]> {
   return waitFor(() => {
      const found = [...document.querySelectorAll<HTMLElement>('.cm-editor')];
      expect(found).toHaveLength(count);
      return found.map((dom) => EditorView.findFromDOM(dom)!);
   });
}

function diffArea(): HTMLElement {
   return document.querySelector('.editor')!;
}

function chunkRanges(view: EditorView) {
   return getChunks(view.state)!.chunks.map(({ fromA, toA, fromB, toB }) => ({ fromA, toA, fromB, toB }));
}

describe('the diff view', () => {
   const scriptBase = 'const a = 1;\n<script>alert("base")</script>\n<style>body { display: none }</style>\n';
   const scriptHead = 'const a = 1;\n<script>alert("head")</script>\n<style>body { display: none }</style>\n';
   // The second line differs. It starts after "const a = 1;\n" (13 code units) and spans 31.
   const scriptDiff = modified(scriptBase, scriptHead, [{ fromA: 13, toA: 44, fromB: 13, toB: 44 }]);

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('shows <script> and <style> in the code as text, %s', async (layout, count) => {
      renderDiff('page.html', scriptDiff, layout);

      await editors(count);
      const text = diffArea().textContent;
      expect(text).toContain('<script>alert("base")</script>');
      expect(text).toContain('<script>alert("head")</script>');
      expect(text).toContain('<style>body { display: none }</style>');
      expect(diffArea().querySelector('script, style')).toBeNull();
   });

   describe('shows the server diff', () => {
      const base = 'one\ntwo\nthree\nfour\n';
      const head = 'one\nTWO\nthree\nfour\n';
      // The server's change spans lines 2 and 3, where the package's own diff finds line 2 alone.
      const server = { fromA: 4, toA: 14, fromB: 4, toB: 14 };

      it('which differs from the package diff of the same texts', () => {
         const own = Chunk.build(Text.of(base.split('\n')), Text.of(head.split('\n')));
         expect(own.map(({ fromA, toA, fromB, toB }) => ({ fromA, toA, fromB, toB }))).toEqual([{ fromA: 4, toA: 8, fromB: 4, toB: 8 }]);
      });

      it('side by side', async () => {
         renderDiff('a.txt', modified(base, head, [server]));

         const [a, b] = await editors(2);
         expect(chunkRanges(a!)).toEqual([server]);
         expect(chunkRanges(b!)).toEqual([server]);
      });

      it('inline', async () => {
         renderDiff('a.txt', modified(base, head, [server]), 'inline');

         const [view] = await editors(1);
         expect(chunkRanges(view!)).toEqual([server]);
      });

      it('through a configuration with no scan limit, which falls back to a timed diff for other texts', () => {
         const config = serverDiffConfig(modified(base, head, [server]));

         expect(config.scanLimit).toBeUndefined();
         expect(config.override!(base, head)).toEqual([server]);
         expect(config.override!('a\n', 'b\n')).toEqual([{ fromA: 0, toA: 1, fromB: 0, toB: 1 }]);
      });
   });

   // The package joins changes that fewer than 3 unchanged characters separate, line break included, so the line between two server hunks shows as changed when it is that short.
   describe.each<[string, boolean]>([
      ['', true],
      ['}', true],
      ['ab', false],
      ['    }', false],
   ])('shows two server hunks around the unchanged line "%s"', (line, joined) => {
      const base = `one\n${line}\ntwo\n`;
      const head = `ONE\n${line}\nTWO\n`;
      const second = 4 + line.length + 1;
      const server = [
         { fromA: 0, toA: 4, fromB: 0, toB: 4 },
         { fromA: second, toA: second + 4, fromB: second, toB: second + 4 },
      ];
      const shown = joined ? [{ fromA: 0, toA: second + 4, fromB: 0, toB: second + 4 }] : server;

      it.each<[DiffLayout, number]>([
         ['sideBySide', 2],
         ['inline', 1],
      ])(`as ${joined ? 'one chunk' : 'two chunks'}, %s`, async (layout, count) => {
         renderDiff('a.cs', modified(base, head, server), layout);

         const views = await editors(count);
         for (const view of views) {
            expect(chunkRanges(view)).toEqual(shown);
         }
      });
   });

   // The editors split lines at CRLF, CR and LF alike, so the server's offsets into the raw texts are mapped to them.
   describe.each<[string, string]>([
      ['CRLF', '\r\n'],
      ['CR', '\r'],
   ])('shows the server diff of a file with %s line breaks', (_, br) => {
      const base = ['one', 'two', 'three', 'four', ''].join(br);
      const head = ['one', 'TWO', 'three', 'four', ''].join(br);
      // Lines 2 and 3, "two" and "three" with their line breaks.
      const server = { fromA: 3 + br.length, toA: 11 + 3 * br.length, fromB: 3 + br.length, toB: 11 + 3 * br.length };
      const shown = { fromA: 4, toA: 14, fromB: 4, toB: 14 };

      it.each<[DiffLayout, number]>([
         ['sideBySide', 2],
         ['inline', 1],
      ])('%s', async (layout, count) => {
         renderDiff('a.txt', modified(base, head, [server]), layout);

         const views = await editors(count);
         for (const view of views) {
            expect(chunkRanges(view)).toEqual([shown]);
         }
      });
   });

   // With whitespace ignored, the server's one change sits between a base with CRLF line breaks and a head with LF, so its offsets differ on each side: "two" in the base, "  TWO" in the head.
   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('shows a diff with whitespace ignored between a CRLF base and an LF head, %s', async (layout, count) => {
      renderDiff('a.txt', modified('one\r\ntwo\r\nthree\r\n', 'one\n  TWO\nthree\n', [{ fromA: 5, toA: 10, fromB: 4, toB: 10 }]), layout);

      const views = await editors(count);
      for (const view of views) {
         expect(chunkRanges(view)).toEqual([{ fromA: 4, toA: 8, fromB: 4, toB: 10 }]);
      }
   });

   it('shows a file whose only change is its line endings collapsed until the user opens it, and collapses it again for the next such file', async () => {
      const crlf = modified('a\n', 'a\r\n', [{ fromA: 0, toA: 2, fromB: 0, toB: 3 }]);
      const view = render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', crlf), lineEndingsOnly: true } });
      expect(screen.getByText(/Only the line endings changed/)).toBeTruthy();
      expect(diffArea().hidden).toBe(true);

      await fireEvent.click(screen.getByRole('button', { name: 'Show the diff' }));

      await editors(2);
      await view.rerender({ path: 'b.txt', known: loaded('b.txt', crlf), lineEndingsOnly: true });
      expect(screen.getByRole('button', { name: 'Show the diff' })).toBeTruthy();
      expect(diffArea().hidden).toBe(true);
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('shows an added file as inserted lines, %s', async (layout, count) => {
      const head = 'export const added = true;\n';
      const changes = [{ fromA: 0, toA: 0, fromB: 0, toB: 27 }];
      renderDiff('added.ts', { content: 'text', baseText: null, headText: head, changes }, layout);

      const views = await editors(count);
      const headView = views.at(-1)!;
      expect(headView.state.doc.toString()).toBe(head);
      expect(headView.dom.querySelectorAll('.cm-changedLine')).toHaveLength(1);
      if (layout === 'sideBySide') {
         expect(views[0]!.state.doc.toString()).toBe('');
      }
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('shows a deleted file as deleted lines, %s', async (layout, count) => {
      const base = 'export const deleted = true;\nexport const gone = true;\n';
      const changes = [{ fromA: 0, toA: 55, fromB: 0, toB: 0 }];
      renderDiff('deleted.ts', { content: 'text', baseText: base, headText: null, changes }, layout);

      const views = await editors(count);
      expect(views[0]!.state.doc.toString()).toBe(base);
      if (layout === 'sideBySide') {
         expect(views[1]!.state.doc.toString()).toBe('');
         expect(views[0]!.dom.querySelectorAll('.cm-changedLine')).toHaveLength(2);
      } else {
         expect(views[0]!.dom.classList.contains('lotsen-deletedFile')).toBe(true);
      }
   });

   it('destroys the editors when another file opens and when the layout changes', async () => {
      const destroy = vi.spyOn(EditorView.prototype, 'destroy');
      const view = renderDiff('a.txt', modified('a\n', 'b\n', [oneLine]));
      await editors(2);

      await view.rerender({ path: 'b.txt', known: loaded('b.txt', modified('c\n', 'd\n', [oneLine])) });

      expect(destroy).toHaveBeenCalledTimes(2);
      const [base] = await editors(2);
      expect(base!.state.doc.toString()).toBe('c\n');

      await view.rerender({ layout: 'inline' });

      expect(destroy).toHaveBeenCalledTimes(4);
      await editors(1);
      view.unmount();
      expect(destroy).toHaveBeenCalledTimes(5);
      expect(document.querySelectorAll('.cm-editor')).toHaveLength(0);
   });

   it('mounts only the newest file when another opens before the first one’s language loads', async () => {
      const view = renderDiff('first.yaml', modified('a: 1\n', 'a: 2\n', [{ fromA: 0, toA: 5, fromB: 0, toB: 5 }]));

      await view.rerender({ path: 'second.txt', known: loaded('second.txt', modified('x\n', 'y\n', [oneLine])) });
      await loadLanguageFor('first.yaml');

      const [base] = await editors(2);
      expect(base!.state.doc.toString()).toBe('x\n');
   });

   it('shows a diff only for the file it belongs to', () => {
      render(DiffView, { props: { path: 'b.ts', known: loaded('a.ts', scriptDiff) } });

      expect(screen.getByText('Loading the diff…')).toBeTruthy();
      expect(diffArea().hidden).toBe(true);
   });
});

describe('the pane splitter', () => {
   const twoLines = modified('one\ntwo\n', 'one\nTWO\n', [{ fromA: 4, toA: 8, fromB: 4, toB: 8 }]);

   beforeEach(() => localStorage.clear());

   const splitter = () => screen.getByRole('separator', { name: 'Resize the base and head panes' });
   const grows = () => [...document.querySelectorAll<HTMLElement>('.cm-mergeViewEditor')].map((pane) => pane.style.flexGrow);

   it('sits between the panes, which share the width equally at first', async () => {
      renderDiff('a.txt', twoLines);
      await editors(2);

      const [base, head] = document.querySelectorAll('.cm-mergeViewEditor');
      expect(splitter().previousElementSibling).toBe(base);
      expect(splitter().nextElementSibling).toBe(head);
      expect(splitter().getAttribute('aria-orientation')).toBe('vertical');
      expect([splitter().getAttribute('aria-valuemin'), splitter().getAttribute('aria-valuenow'), splitter().getAttribute('aria-valuemax')]).toEqual(['20', '50', '80']);
      expect(splitter().tabIndex).toBe(0);
      expect(grows()).toEqual(['50', '50']);
   });

   it('moves with the keys, and keeps the split when the diff mounts again', async () => {
      const first = renderDiff('a.txt', twoLines);
      await editors(2);

      await fireEvent.keyDown(splitter(), { key: 'ArrowRight' });
      expect(grows()).toEqual(['60', '40']);
      expect(splitter().getAttribute('aria-valuenow')).toBe('60');
      await fireEvent.keyDown(splitter(), { key: 'Home' });
      expect(grows()).toEqual(['20', '80']);

      first.unmount();
      renderDiff('a.txt', twoLines);
      await editors(2);
      expect(grows()).toEqual(['20', '80']);
   });

   it('shows no splitter inline', async () => {
      renderDiff('a.txt', twoLines, 'inline');
      await editors(1);

      expect(screen.queryByRole('separator')).toBeNull();
   });

   it('leaves the panes of a merge view that the layout replaced unsized', async () => {
      const view = renderDiff('a.txt', twoLines);
      await editors(2);
      const old = document.querySelector<HTMLElement>('.cm-mergeViewEditor')!;

      await view.rerender({ layout: 'inline' });
      await editors(1);
      expect(screen.queryByRole('separator')).toBeNull();
      await view.rerender({ layout: 'sideBySide' });
      await editors(2);
      await fireEvent.keyDown(splitter(), { key: 'End' });

      expect(grows()).toEqual(['80', '20']);
      expect(old.style.flexGrow).toBe('50');
   });
});

describe('the passage decorations', () => {
   type Shown = [text: string, mark: 'primary' | 'related' | 'dimmed' | '', labels: string[]];

   function shown(line: Element): Shown {
      const labels = [...line.querySelectorAll('.lotsen-passageLabel')].map((label) => label.textContent);
      const text = line.textContent.slice(0, line.textContent.length - labels.join('').length);
      const mark = line.classList.contains('lotsen-passage-primary')
         ? 'primary'
         : line.classList.contains('lotsen-passage-related')
           ? 'related'
           : line.classList.contains('lotsen-dimmed')
             ? 'dimmed'
             : '';
      return [text, mark, labels];
   }

   /** The editor's own lines, without the deleted lines that the inline head editor draws. */
   function lines(view: EditorView): Shown[] {
      return [...view.contentDOM.querySelectorAll(':scope > .cm-line')].map(shown);
   }

   function deletedLines(view: EditorView): Shown[] {
      return [...view.contentDOM.querySelectorAll('.cm-deletedLine')].map(shown);
   }

   function renderDecorated(fileDiff: TextDiff, layout: DiffLayout, decorations: PassageDecorations) {
      return render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', fileDiff), layout, decorations } });
   }

   // The head's line 2 replaces the base's lines 2 and 3.
   const base = 'one\ntwo\nthree\nfour\nfive\n';
   const head = 'one\nnew\nfour\nfive\n';
   const replaced = modified(base, head, [{ fromA: 4, toA: 14, fromB: 4, toB: 8 }]);
   const decorations: PassageDecorations = {
      passages: [
         { side: 'head', startLine: 2, endLine: 3, role: 'primary', label: 'the change' },
         { side: 'base', startLine: 3, endLine: 3, role: 'related', label: 'the old line' },
         { side: 'base', startLine: 5, endLine: 5, role: 'related' },
      ],
      dim: true,
      highlight: true,
      collapse: false,
   };

   it('dims the lines outside the passages and highlights each passage in its role, with its label, side by side', async () => {
      renderDecorated(replaced, 'sideBySide', decorations);

      const [a, b] = await editors(2);
      await waitFor(() => expect(lines(a!)[2]).toEqual(['three', 'related', ['the old line']]));
      expect(lines(a!)).toEqual([
         ['one', 'dimmed', []],
         ['two', 'dimmed', []],
         ['three', 'related', ['the old line']],
         ['four', 'dimmed', []],
         ['five', 'related', []],
         ['', 'dimmed', []],
      ]);
      expect(lines(b!)).toEqual([
         ['one', 'dimmed', []],
         ['new', 'primary', ['the change']],
         ['four', 'primary', []],
         ['five', 'dimmed', []],
         ['', 'dimmed', []],
      ]);
      const numbers = [...b!.dom.querySelectorAll('.cm-lineNumbers .cm-gutterElement')].filter((element) => element.textContent === '2');
      expect(numbers.map((element) => element.classList.contains('lotsen-passage-primary'))).toEqual([true]);
   });

   it('highlights a base passage inside a deleted chunk on its deleted line, and a base passage of unchanged lines on the head line, inline', async () => {
      renderDecorated(replaced, 'inline', decorations);

      const [view] = await editors(1);
      await waitFor(() =>
         expect(deletedLines(view!)).toEqual([
            ['two', 'dimmed', []],
            ['three', 'related', ['the old line']],
         ]),
      );
      expect(lines(view!)).toEqual([
         ['one', 'dimmed', []],
         ['new', 'primary', ['the change']],
         ['four', 'primary', []],
         ['five', 'related', []],
         ['', 'dimmed', []],
      ]);
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('marks the lines of a focused passage and their line numbers as focused, at each side, %s', async (layout, count) => {
      renderDecorated(replaced, layout, {
         passages: [
            { side: 'head', startLine: 3, endLine: 3, role: 'primary', focused: true },
            { side: 'base', startLine: 3, endLine: 3, role: 'related', focused: true },
         ],
         dim: true,
         highlight: true,
         collapse: false,
      });

      const views = await editors(count);
      const focusedText = (view: EditorView, selector: string) => [...view.contentDOM.querySelectorAll(selector)].map((line) => line.textContent);
      await waitFor(() => expect(focusedText(views.at(-1)!, ':scope > .cm-line.lotsen-passage-focused')).toEqual(['four']));
      const baseFocused = layout === 'inline' ? focusedText(views[0]!, '.cm-deletedLine.lotsen-passage-focused') : focusedText(views[0]!, ':scope > .cm-line.lotsen-passage-focused');
      expect(baseFocused).toEqual(['three']);
      const numbers = [...views.at(-1)!.dom.querySelectorAll('.cm-lineNumbers .cm-gutterElement.lotsen-passage-focused')].map((element) => element.textContent);
      expect(numbers).toEqual(['3']);
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('with highlighting off, marks no passage or focused passage on lines and line numbers, keeps the labels, the dimming and the hovered reference, %s', async (layout, count) => {
      renderDecorated(replaced, layout, {
         passages: [
            { side: 'head', startLine: 2, endLine: 2, role: 'primary', label: 'the change', focused: true },
            { side: 'base', startLine: 3, endLine: 3, role: 'related', label: 'the old line' },
         ],
         dim: true,
         highlight: false,
         collapse: false,
         hovered: { side: 'head', startLine: 3, endLine: 3 },
      });

      const views = await editors(count);
      const head = views.at(-1)!;
      await waitFor(() => expect(lines(head)[1]).toEqual(['new', '', ['the change']]));
      expect(lines(head).map(([, mark]) => mark)).toEqual(['dimmed', '', '', 'dimmed', 'dimmed']);
      expect(head.contentDOM.querySelector(':scope > .cm-line.lotsen-reference-hovered')?.textContent).toBe('four');
      const base = layout === 'inline' ? deletedLines(head) : lines(views[0]!);
      expect(base.find(([text]) => text === 'three')).toEqual(['three', '', ['the old line']]);
      expect(document.querySelectorAll('.lotsen-passage, .lotsen-passage-primary, .lotsen-passage-related, .lotsen-passage-focused')).toHaveLength(0);
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('follows new decorations without remounting the editors, %s', async (layout, count) => {
      const destroy = vi.spyOn(EditorView.prototype, 'destroy');
      const view = renderDecorated(replaced, layout, decorations);
      const mounted = await editors(count);
      await waitFor(() => expect(mounted.at(-1)!.contentDOM.querySelector('.lotsen-passage-primary')).not.toBeNull());

      await view.rerender({ decorations: { passages: [{ side: 'head', startLine: 1, endLine: 1, role: 'related' }], dim: false, highlight: true, collapse: false } });

      expect(destroy).not.toHaveBeenCalled();
      expect(await editors(count)).toEqual(mounted);
      const headLines = lines(mounted.at(-1)!);
      expect(headLines[0]).toEqual(['one', 'related', []]);
      expect(headLines.slice(1).every(([, mark]) => mark === '')).toBe(true);
      expect(document.querySelectorAll('.lotsen-dimmed, .lotsen-passageLabel')).toHaveLength(0);

      await view.rerender({ decorations: noPassageDecorations });

      expect(document.querySelectorAll('.lotsen-passage, .lotsen-dimmed')).toHaveLength(0);
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('highlights the lines of a file with CRLF line breaks, %s', async (layout, count) => {
      const crlf = (text: string) => text.replaceAll('\n', '\r\n');
      // "two\r\nthree\r\n" at the base and "new\r\n" at the head, after "one\r\n".
      renderDecorated(modified(crlf(base), crlf(head), [{ fromA: 5, toA: 17, fromB: 5, toB: 10 }]), layout, decorations);

      const views = await editors(count);
      const headView = views.at(-1)!;
      await waitFor(() =>
         expect(lines(headView).map(([text, mark]) => `${text}:${mark}`)).toEqual(['one:dimmed', 'new:primary', 'four:primary', `five:${layout === 'inline' ? 'related' : 'dimmed'}`, ':dimmed']),
      );
      const baseLines = layout === 'inline' ? deletedLines(headView) : lines(views[0]!).slice(1, 3);
      expect(baseLines).toEqual([
         ['two', 'dimmed', []],
         ['three', 'related', ['the old line']],
      ]);
   });

   it('marks a deleted chunk that the editor draws only once it scrolls there, and leaves one unmarked that it draws again after the decorations cleared, inline', async () => {
      onTestFinished(stubRangeLayout());
      // Lines 2 and 3001 change, so the chunk of line 3001 is far below the first view.
      const text = (side: string) => Array.from({ length: 4000 }, (_, index) => (index === 1 || index === 3000 ? `${side} ${index + 1}` : `line ${index + 1}`)).join('\n') + '\n';
      const base = text('gone');
      const head = text('new');
      const change = (line: number) => ({ fromA: lineStart(base, line), toA: lineStart(base, line + 1), fromB: lineStart(head, line), toB: lineStart(head, line + 1) });
      const fileDiff = modified(base, head, [change(2), change(3001)]);
      const view = renderDecorated(fileDiff, 'inline', { passages: [{ side: 'base', startLine: 3001, endLine: 3001, role: 'primary', label: 'far' }], dim: true, highlight: true, collapse: false });
      const [editor] = await editors(1);
      const deleted = (name: string) => [...editor!.contentDOM.querySelectorAll('.cm-deletedLine')].find((line) => line.textContent.startsWith(name));
      await waitFor(() => expect(deleted('gone 2')?.classList.contains('lotsen-dimmed')).toBe(true));
      expect(deleted('gone 3001')).toBeUndefined();

      scrollToPosition({ view: editor!, pos: getChunks(editor!.state)!.chunks[1]!.fromB, deletedLine: 0 });

      await waitFor(() => expect(deleted('gone 3001') && shown(deleted('gone 3001')!)).toEqual(['gone 3001', 'primary', ['far']]));
      expect(deleted('gone 2')).toBeUndefined();

      await view.rerender({ decorations: noPassageDecorations });
      scrollToPosition({ view: editor!, pos: 0 });

      await waitFor(() => expect(deleted('gone 2') && shown(deleted('gone 2')!)).toEqual(['gone 2', '', []]));
   });

   it('highlights a base passage of a deleted file, inline', async () => {
      const deleted: TextDiff = { content: 'text', baseText: 'one\ntwo\n', headText: null, changes: [{ fromA: 0, toA: 8, fromB: 0, toB: 0 }] };
      renderDecorated(deleted, 'inline', { passages: [{ side: 'base', startLine: 2, endLine: 2, role: 'primary', label: 'gone' }], dim: true, highlight: true, collapse: false });

      const [view] = await editors(1);
      await waitFor(() =>
         expect(lines(view!)).toEqual([
            ['one', 'dimmed', []],
            ['two', 'primary', ['gone']],
            ['', 'dimmed', []],
         ]),
      );
   });
});

describe('collapsing to the passages', () => {
   /** What an editor draws from top to bottom: each line as its number and text, each marker as the number of rows it hides, and each deleted chunk as its lines. */
   function drawn(view: EditorView): string[] {
      return [...view.contentDOM.children].flatMap((element) => {
         if (element.classList.contains('lotsen-collapsed')) {
            return [`[${element.querySelector('.lotsen-collapsedCount')!.textContent}]`];
         }

         if (element.classList.contains('cm-deletedChunk')) {
            return [...element.querySelectorAll('.cm-deletedLine')].map((line) => `-${line.textContent}`);
         }

         return element.classList.contains('cm-line') ? [`${view.state.doc.lineAt(view.posAtDOM(element)).number}:${element.textContent}`] : [];
      });
   }

   /** The texts of the items that face each other in the panes, in order, without the head's inserted lines, which face a spacer. A marker's text is "marker". */
   function rowPairs(a: EditorView, b: EditorView): [string, string][] {
      const text = (item: string) => (item.startsWith('[') ? 'marker' : item.slice(item.indexOf(':') + 1));
      const left = drawn(a);
      const right = drawn(b).filter((item) => !text(item).startsWith('new '));
      expect(left.length).toBe(right.length);
      return left.map((item, index) => [text(item), text(right[index]!)]);
   }

   function marker(view: EditorView, index = 0): HTMLElement {
      return view.contentDOM.querySelectorAll<HTMLElement>('.lotsen-collapsed')[index]!;
   }

   function expand(view: EditorView, name: string, index = 0) {
      return fireEvent.click(within(marker(view, index)).getByRole('button', { name }));
   }

   const lines = (count: number, name: (line: number) => string = (line) => `line ${line}`) => Array.from({ length: count }, (_, index) => `${name(index + 1)}\n`).join('');
   // The head inserts three lines after the base's line 5 and changes the base's line 40, so the base's line n is the head's line n + 3 after line 5.
   const base = lines(60);
   const head = lines(60, (line) => (line === 40 ? 'changed 40' : `line ${line}`)).replace('line 6\n', 'new a\nnew b\nnew c\nline 6\n');
   const fileDiff = modified(base, head, [
      { fromA: lineStart(base, 6), toA: lineStart(base, 6), fromB: lineStart(head, 6), toB: lineStart(head, 9) },
      { fromA: lineStart(base, 40), toA: lineStart(base, 41), fromB: lineStart(head, 43), toB: lineStart(head, 44) },
   ]);
   const collapsed: PassageDecorations = { passages: [{ side: 'head', startLine: 23, endLine: 23, role: 'primary' }], dim: true, highlight: true, collapse: true };

   function renderCollapsed(layout: DiffLayout, decorations: PassageDecorations = collapsed, focus: LineFocus | null = null) {
      return render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', fileDiff), layout, decorations, focus } });
   }

   it('collapses the regions outside the passages and their context in both panes, and expands one in steps or whole, side by side', async () => {
      renderCollapsed('sideBySide');
      const [a, b] = await editors(2);

      await waitFor(() => expect(drawn(b!)).toEqual(['[19 lines hidden]', '20:line 17', '21:line 18', '22:line 19', '23:line 20', '24:line 21', '25:line 22', '26:line 23', '[38 lines hidden]']));
      expect(drawn(a!)).toEqual(['[16 lines hidden]', '17:line 17', '18:line 18', '19:line 19', '20:line 20', '21:line 21', '22:line 22', '23:line 23', '[38 lines hidden]']);
      expect(rowPairs(a!, b!).every(([left, right]) => left === right)).toBe(true);

      await expand(b!, 'Show first 20', 1);

      expect(drawn(a!).slice(8)).toEqual([...Array.from({ length: 20 }, (_, index) => `${24 + index}:${index === 16 ? 'line 40' : `line ${24 + index}`}`), '[18 lines hidden]']);
      expect(drawn(b!).slice(8, 10)).toEqual(['27:line 24', '28:line 25']);
      expect(drawn(b!).at(-1)).toBe('[18 lines hidden]');
      expect(rowPairs(a!, b!).filter(([left, right]) => left !== right)).toEqual([['line 40', 'changed 40']]);

      await expand(a!, 'Show all');

      expect(drawn(a!).slice(0, 6)).toEqual(['1:line 1', '2:line 2', '3:line 3', '4:line 4', '5:line 5', '6:line 6']);
      expect(drawn(b!).slice(0, 9)).toEqual(['1:line 1', '2:line 2', '3:line 3', '4:line 4', '5:line 5', '6:new a', '7:new b', '8:new c', '9:line 6']);
      expect(document.querySelectorAll('.lotsen-collapsed')).toHaveLength(2);

      await expand(b!, 'Show all');

      expect(document.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);
      expect(drawn(a!)).toHaveLength(61);
      expect(drawn(b!)).toHaveLength(64);
   });

   it('collapses the same region in the inline editor, with the deleted and inserted lines inside it, and expands it', async () => {
      renderCollapsed('inline');
      const [view] = await editors(1);

      await waitFor(() => expect(drawn(view!)).toEqual(['[19 lines hidden]', '20:line 17', '21:line 18', '22:line 19', '23:line 20', '24:line 21', '25:line 22', '26:line 23', '[39 lines hidden]']));
      expect(view!.contentDOM.querySelector('.cm-deletedChunk')).toBeNull();

      await expand(view!, 'Show first 20', 1);

      expect(drawn(view!).slice(8)).toEqual([
         ...Array.from({ length: 16 }, (_, index) => `${27 + index}:line ${24 + index}`),
         '-line 40',
         '43:changed 40',
         '44:line 41',
         '45:line 42',
         '[19 lines hidden]',
      ]);

      await expand(view!, 'Show all', 1);
      await expand(view!, 'Show all');

      expect(document.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);
      expect(drawn(view!)).toHaveLength(65);
   });

   it('moves the keyboard focus to the same control of the rest of a region, and to the editor once the region is gone', async () => {
      renderCollapsed('sideBySide');
      const [, b] = await editors(2);
      await waitFor(() => expect(marker(b!, 1)).toBeDefined());
      const step = within(marker(b!, 1)).getByRole('button', { name: 'Show last 20' });
      step.focus();

      await fireEvent.click(step);

      expect(document.activeElement?.textContent).toBe('Show all');
      expect(marker(b!, 1).contains(document.activeElement)).toBe(true);

      await fireEvent.click(document.activeElement!);

      expect(b!.hasFocus).toBe(true);
   });

   /** The 1-based line of an editor's caret. */
   const caretLine = (view: EditorView) => view.state.doc.lineAt(view.state.selection.main.head).number;

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('puts the caret on the first line of the region that the last Show all expands by keyboard, and scrolls to it, %s', async (layout, count) => {
      renderCollapsed(layout);
      const head = (await editors(count)).at(-1)!;
      await waitFor(() => expect(marker(head, 1)).toBeDefined());
      await expand(head, 'Show all');
      expect(document.querySelectorAll('.lotsen-collapsed')).toHaveLength(count);
      const showAll = within(marker(head)).getByRole('button', { name: 'Show all' });
      showAll.focus();
      const scroll = vi.spyOn(EditorView, 'scrollIntoView');

      await fireEvent.click(showAll);

      expect(document.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);
      expect(head.hasFocus).toBe(true);
      expect(caretLine(head)).toBe(27);
      expect(scroll).toHaveBeenCalledWith(head.state.doc.line(27).from);
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('puts the caret on the last line of the region above the passage that Show all expands by keyboard, %s', async (layout, count) => {
      renderCollapsed(layout);
      const head = (await editors(count)).at(-1)!;
      await waitFor(() => expect(marker(head, 1)).toBeDefined());
      const showAll = within(marker(head)).getByRole('button', { name: 'Show all' });
      showAll.focus();

      await fireEvent.click(showAll);

      expect(head.contentDOM.querySelectorAll('.lotsen-collapsed')).toHaveLength(1);
      expect(head.hasFocus).toBe(true);
      expect(caretLine(head)).toBe(19);
   });

   it.each(['Show first 20', 'Show last 20'])('puts the caret on the first line of a region that %s removes whole by keyboard', async (name) => {
      // A changed first line and a passage on it leave lines 5 to 27 hidden, so a step of 20 leaves too few lines for a marker.
      const text = lines(26);
      const changed = text.replace('line 1\n', 'first\n');
      const fileDiff = modified(text, changed, [{ fromA: 0, toA: lineStart(text, 2), fromB: 0, toB: lineStart(changed, 2) }]);
      const decorations: PassageDecorations = { passages: [{ side: 'head', startLine: 1, endLine: 1, role: 'primary' }], dim: true, highlight: true, collapse: true };
      render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', fileDiff), layout: 'sideBySide', decorations } });
      const [, b] = await editors(2);
      await waitFor(() => expect(drawn(b!).at(-1)).toBe('[23 lines hidden]'));
      const step = within(marker(b!)).getByRole('button', { name });
      step.focus();

      await fireEvent.click(step);

      expect(b!.contentDOM.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);
      expect(b!.hasFocus).toBe(true);
      expect(caretLine(b!)).toBe(5);
   });

   it.each<[DiffLayout, LineFocus, string]>([
      ['sideBySide', { path: 'a.txt', side: 'head', line: 50 }, '50:line 47'],
      ['sideBySide', { path: 'a.txt', side: 'base', line: 3 }, '3:line 3'],
      ['inline', { path: 'a.txt', side: 'head', line: 50 }, '50:line 47'],
      ['inline', { path: 'a.txt', side: 'base', line: 40 }, '-line 40'],
   ])('expands the region that hides a focused line before scrolling to it, %s, %o', async (layout, focus, line) => {
      renderCollapsed(layout, collapsed, focus);

      const views = await editors(layout === 'inline' ? 1 : 2);
      const view = layout === 'inline' || focus.side === 'head' ? views.at(-1)! : views[0]!;
      await waitFor(() => expect(drawn(view)).toContain(line));
      for (const editor of views) {
         expect(editor.contentDOM.querySelectorAll('.lotsen-collapsed')).toHaveLength(1);
      }

      if (layout === 'sideBySide') {
         expect(rowPairs(views[0]!, views[1]!).filter(([left, right]) => left !== right)).toEqual(focus.side === 'head' ? [['line 40', 'changed 40']] : []);
      }
   });

   it.each<[DiffLayout, number]>([
      ['sideBySide', 2],
      ['inline', 1],
   ])('keeps what the reviewer expanded while the passages stay, collapses afresh for new passages, and shows every line without collapse, in the same editors, %s', async (layout, count) => {
      const destroy = vi.spyOn(EditorView.prototype, 'destroy');
      const view = renderCollapsed(layout);
      const mounted = await editors(count);
      const head = mounted.at(-1)!;
      await waitFor(() => expect(head.contentDOM.querySelectorAll('.lotsen-collapsed')).toHaveLength(2));
      await expand(head, 'Show all');

      await view.rerender({ decorations: { ...collapsed, passages: [...collapsed.passages] } });

      expect(head.contentDOM.querySelectorAll('.lotsen-collapsed')).toHaveLength(1);

      await view.rerender({ decorations: { ...collapsed, passages: [{ side: 'head', startLine: 2, endLine: 2, role: 'primary' }] } });

      expect(drawn(head)[0]).toBe('1:line 1');
      expect(head.contentDOM.querySelectorAll('.lotsen-collapsed')).toHaveLength(1);

      await view.rerender({ decorations: { ...collapsed, collapse: false } });

      expect(document.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);
      expect(destroy).not.toHaveBeenCalled();
      expect(await editors(count)).toEqual(mounted);
   });

   it('keeps the passage decorations on the lines that stay shown', async () => {
      renderCollapsed('sideBySide');
      const [, b] = await editors(2);

      await waitFor(() => expect(b!.contentDOM.querySelector('.lotsen-passage-primary')?.textContent).toBe('line 20'));
      expect([...b!.contentDOM.querySelectorAll(':scope > .cm-line')].map((line) => line.classList.contains('lotsen-dimmed'))).toEqual([true, true, true, false, true, true, true]);
   });

   const shownLines = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, index) => `${from + index}:line ${from + index}`);
   const appended = modified(lines(30), lines(60), [{ fromA: lineStart(lines(30), 31), toA: lineStart(lines(30), 31), fromB: lineStart(lines(60), 31), toB: lineStart(lines(60), 61) }]);

   it.each<[string, TextDiff, number, string[], string[]]>([
      ['an added file', { content: 'text', baseText: null, headText: base, changes: [{ fromA: 0, toA: 0, fromB: 0, toB: base.length }] }, 20, ['1:'], ['[16 lines hidden]', ...shownLines(17, 23)]],
      // The merge package moves the insertion up a line, so its chunk ends before the base's line 30, which the merge view aligns with the head's line 60.
      ['lines appended to a file', appended, 33, ['[29 lines hidden]', '30:line 30', '31:'], ['[29 lines hidden]', ...shownLines(30, 36)]],
   ])('leaves the base pane of %s without a marker beside the head’s lines, side by side', async (_, fileDiff, line, baseDrawn, headDrawn) => {
      const decorations: PassageDecorations = { passages: [{ side: 'head', startLine: line, endLine: line, role: 'primary' }], dim: true, highlight: true, collapse: true };
      render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', fileDiff), layout: 'sideBySide', decorations } });

      const [a, b] = await editors(2);
      // jsdom's viewport may end before the head's last marker.
      await waitFor(() => expect(drawn(b!).slice(0, headDrawn.length)).toEqual(headDrawn));
      expect(drawn(a!)).toEqual(baseDrawn);
   });

   it('counts a single hidden line in the singular', async () => {
      // Twenty head lines replace the base's line 5.
      const head = `${lines(4)}${lines(20, (line) => `new ${line}`)}${lines(10).split('\n').slice(5).join('\n')}`;
      const fileDiff = modified(lines(10), head, [{ fromA: lineStart(lines(10), 5), toA: lineStart(lines(10), 6), fromB: lineStart(head, 5), toB: lineStart(head, 25) }]);
      const passages: DecoratedPassage[] = [
         { side: 'head', startLine: 1, endLine: 1, role: 'primary' },
         { side: 'head', startLine: 27, endLine: 27, role: 'related' },
      ];
      render(DiffView, { props: { path: 'a.txt', known: loaded('a.txt', fileDiff), layout: 'sideBySide', decorations: { passages, dim: true, highlight: true, collapse: true } } });

      const [a] = await editors(2);
      await waitFor(() => expect(drawn(a!).slice(0, 5)).toEqual(['1:line 1', '2:line 2', '3:line 3', '4:line 4', '[1 line hidden]']));
   });

   it('collapses a deleted file in its one editor, inline', async () => {
      const deleted: TextDiff = { content: 'text', baseText: base, headText: null, changes: [{ fromA: 0, toA: base.length, fromB: 0, toB: 0 }] };
      render(DiffView, {
         props: {
            path: 'a.txt',
            known: loaded('a.txt', deleted),
            layout: 'inline',
            decorations: { passages: [{ side: 'base', startLine: 1, endLine: 1, role: 'primary' }], dim: true, highlight: true, collapse: true },
         },
      });

      const [view] = await editors(1);
      await waitFor(() => expect(drawn(view!)).toEqual(['1:line 1', '2:line 2', '3:line 3', '4:line 4', '[57 lines hidden]']));
   });
});

describe('normalizeLineBreaks', () => {
   const change = (fromA: number, toA: number, fromB: number, toB: number) => ({ fromA, toA, fromB, toB });

   it('keeps a diff with LF line breaks as it is', () => {
      const diff = modified('a\nb\n', 'a\nc\n', [change(2, 4, 2, 4)]);

      expect(normalizeLineBreaks(diff)).toEqual(diff);
   });

   it.each<[string, TextDiff, TextDiff]>([
      ['CRLF', modified('a\r\nb\r\nc\r\n', 'a\r\nB\r\nc\r\n', [change(3, 6, 3, 6)]), modified('a\nb\nc\n', 'a\nB\nc\n', [change(2, 4, 2, 4)])],
      ['lone CR', modified('a\rb\rc\r', 'a\rB\rc\r', [change(2, 4, 2, 4)]), modified('a\nb\nc\n', 'a\nB\nc\n', [change(2, 4, 2, 4)])],
      ['mixed line breaks', modified('a\r\nb\rc\nd\r\n', 'a\r\nb\rC\nd\r\n', [change(5, 7, 5, 7)]), modified('a\nb\nc\nd\n', 'a\nb\nC\nd\n', [change(4, 6, 4, 6)])],
      ['a change from the first to the last character', modified('a\r\nb\r\n', 'c\r\n', [change(0, 6, 0, 3)]), modified('a\nb\n', 'c\n', [change(0, 4, 0, 2)])],
      [
         'an added file',
         { content: 'text', baseText: null, headText: 'a\r\nb\r\n', changes: [change(0, 0, 0, 6)] },
         { content: 'text', baseText: null, headText: 'a\nb\n', changes: [change(0, 0, 0, 4)] },
      ],
      [
         'a deleted file',
         { content: 'text', baseText: 'a\r\nb\r\n', headText: null, changes: [change(0, 6, 0, 0)] },
         { content: 'text', baseText: 'a\nb\n', headText: null, changes: [change(0, 4, 0, 0)] },
      ],
      [
         'CRLF at the base and LF at the head',
         modified('one\r\ntwo\r\nthree\r\n', 'one\n  TWO\nthree\n', [change(5, 10, 4, 10)]),
         modified('one\ntwo\nthree\n', 'one\n  TWO\nthree\n', [change(4, 8, 4, 10)]),
      ],
      [
         // An offset between CR and LF stays on its line, before the line break.
         'a change that ends between CR and LF',
         modified('a\r\nb\r\n', 'a\r\nc\r\n', [change(3, 5, 3, 5)]),
         modified('a\nb\n', 'a\nc\n', [change(2, 3, 2, 3)]),
      ],
   ])('maps the offsets of a diff with %s', (_, diff, expected) => {
      expect(normalizeLineBreaks(diff)).toEqual(expected);
   });
});

describe('lineStart', () => {
   it.each<[string, string, number, number]>([
      ['LF', 'one\ntwo\nthree\n', 2, 4],
      ['CRLF', 'one\r\ntwo\r\nthree\r\n', 3, 8],
      ['a lone CR inside a line', 'one\rstill one\ntwo\n', 2, 14],
      ['no break after the last line', 'one\ntwo', 2, 4],
   ])('finds the start of a line of a text with %s, in the editor document', (_, text, line, offset) => {
      expect(lineStart(text, line)).toBe(offset);
   });

   it('stops at the last line of a shorter text', () => {
      expect(lineStart('one\ntwo\n', 9)).toBe(4);
   });
});

describe('linePosition', () => {
   let editor: DiffEditor | null = null;

   afterEach(() => editor?.destroy());

   function mount(fileDiff: TextDiff, layout: DiffLayout): DiffEditor {
      const parent = document.body.appendChild(document.createElement('div'));
      editor = createDiffEditor({ parent, layout, diff: fileDiff, language: null });
      return editor;
   }

   function lineText(fileDiff: TextDiff, layout: DiffLayout, side: FileSide, line: number): { editor: 'base' | 'head'; text: string } | null {
      const mounted = mount(fileDiff, layout);
      const position = linePosition(mounted, fileDiff, side, line);
      return position && { editor: position.view === mounted.base ? 'base' : 'head', text: position.view.state.doc.lineAt(position.pos).text };
   }

   // The base's line 2 is deleted, and the head's line 4 is inserted.
   const base = 'keep 1\r\ngone 2\r\nkeep 3\r\nkeep 4\r\n';
   const head = 'keep 1\r\nkeep 3\r\nkeep 4\r\nnew 4\r\n';
   const changed = modified(base, head, [
      { fromA: 8, toA: 16, fromB: 8, toB: 8 },
      { fromA: 32, toA: 32, fromB: 24, toB: 31 },
   ]);

   it.each<[FileSide, number, string]>([
      ['base', 2, 'gone 2'],
      ['base', 4, 'keep 4'],
      ['head', 4, 'new 4'],
   ])('finds the %s line %i side by side in the editor of its side', (side, line, text) => {
      expect(lineText(changed, 'sideBySide', side, line)).toEqual({ editor: side, text });
   });

   it.each<[FileSide, number, string]>([
      ['head', 3, 'keep 4'],
      ['base', 1, 'keep 1'],
      ['base', 2, 'keep 3'],
      ['base', 3, 'keep 3'],
      ['base', 4, 'keep 4'],
   ])('finds the %s line %i inline in the head editor, a deleted line at the start of its chunk', (side, line, text) => {
      expect(lineText(changed, 'inline', side, line)).toEqual({ editor: 'head', text });
   });

   it('gives a base line inside a deletion inline its line within the deleted lines, which the head editor draws above the chunk start', () => {
      // The base's lines 2 to 4 are deleted.
      const deletion = modified('a\nb\nc\nd\ne\n', 'a\ne\n', [{ fromA: 2, toA: 8, fromB: 2, toB: 2 }]);
      const mounted = mount(deletion, 'inline');

      const position = linePosition(mounted, deletion, 'base', 3)!;

      expect(position.view.state.doc.lineAt(position.pos).text).toBe('e');
      expect(position.deletedLine).toBe(1);
      expect(linePosition(mounted, deletion, 'base', 5)!.deletedLine).toBeUndefined();
   });

   it('maps a base line inline by its distance from a chunk, when whitespace makes the lines between differ in length', () => {
      const indented = modified('a\nb\nc\nd\n', 'x\n    b\n    c\n    d\n', [{ fromA: 0, toA: 2, fromB: 0, toB: 2 }]);

      expect(lineText(indented, 'inline', 'base', 3)).toEqual({ editor: 'head', text: '    c' });
   });

   it.each<[string, TextDiff]>([
      ['without a chunk', modified('a\rb\nz\n', 'a b\nz\n', [])],
      ['after a chunk', modified('x\na\rb\nz\n', 'y\na b\nz\n', [{ fromA: 0, toA: 2, fromB: 0, toB: 2 }])],
   ])('maps a base line inline past a lone CR that whitespace makes equal to a head line, %s', (_, fileDiff) => {
      const lines = fileDiff.baseText!.split('\n').length - 1;

      expect(lineText(fileDiff, 'inline', 'base', lines)).toEqual({ editor: 'head', text: 'z' });
   });

   it('finds a base line of a deleted file inline in the base editor, and no base line of an added file', () => {
      expect(lineText({ content: 'text', baseText: 'one\ntwo\n', headText: null, changes: [{ fromA: 0, toA: 8, fromB: 0, toB: 0 }] }, 'inline', 'base', 2)).toEqual({ editor: 'base', text: 'two' });
      editor?.destroy();
      expect(lineText({ content: 'text', baseText: null, headText: 'one\n', changes: [{ fromA: 0, toA: 0, fromB: 0, toB: 4 }] }, 'sideBySide', 'base', 1)).toBeNull();
   });
});

describe('languages', () => {
   it.each([
      ['src/Program.cs', 'csharp'],
      ['src/app/main.ts', 'typescript'],
      ['src/App.TSX', 'tsx'],
      ['scripts/build.mjs', 'javascript'],
      ['package.json', 'json'],
      ['src/Lotsen/Lotsen.csproj', 'xml'],
      ['Directory.Build.props', 'xml'],
      ['build/Pack.targets', 'xml'],
      ['.github/ci.yml', 'yaml'],
      ['README.md', 'markdown'],
      ['src/app.css', 'css'],
      ['index.html', 'html'],
      ['db/001-init.sql', 'sql'],
   ])('highlights %s as %s', (path, name) => {
      expect(languageNameFor(path)).toBe(name);
   });

   it.each(['notes.unknownext', 'Dockerfile', '.gitignore', 'src.d/file', 'a.constructor', 'a.__proto__'])('shows %s as plain text', async (path) => {
      expect(languageNameFor(path)).toBeNull();
      await expect(loadLanguageFor(path)).resolves.toBeNull();
   });

   it('mounts a file with an unknown extension without a language', async () => {
      renderDiff('notes.unknownext', modified('a\n', 'b\n', [oneLine]));

      const views = await editors(2);
      for (const view of views) {
         expect(view.state.facet(language)).toBeNull();
         expect(view.contentDOM.hasAttribute('data-language')).toBe(false);
      }
   });

   it('mounts a C# file with its language loaded', async () => {
      renderDiff('Program.cs', modified('class A {}\n', 'class B {}\n', [{ fromA: 0, toA: 11, fromB: 0, toB: 11 }]));

      const views = await editors(2);
      for (const view of views) {
         expect(view.state.facet(language)?.name).toBe('csharp');
      }
   });

   it('colors control keywords and language constants in their own roles, and comments in the family’s style', async () => {
      const text = 'if (ok) { return true; }\nlet none = null;\nlet n = 1; // note\n';
      renderDiff('a.ts', modified(text, text, []));

      const [, head] = await editors(2);
      const styleOf = (token: string) => {
         const span = [...head!.contentDOM.querySelectorAll('span')].find((element) => element.textContent === token);
         expect(span, token).toBeDefined();
         return getComputedStyle(span!);
      };

      expect(['if', 'return', 'true', 'null', '1', 'let'].map((token) => styleOf(token).color)).toEqual([
         'var(--syntax-control)',
         'var(--syntax-control)',
         'var(--syntax-constant)',
         'var(--syntax-constant)',
         'var(--syntax-number)',
         'var(--syntax-keyword)',
      ]);
      expect(styleOf('// note').fontStyle).toBe('var(--syntax-comment-style)');
   });

   it('loads every language', async () => {
      const extensions = ['cs', 'ts', 'tsx', 'js', 'jsx', 'json', 'csproj', 'yml', 'md', 'css', 'html', 'sql'];
      const paths = extensions.map((extension) => `a.${extension}`);
      const languages = await Promise.all(paths.map((path) => loadLanguageFor(path)));

      expect(languages.every((loadedLanguage) => loadedLanguage !== null)).toBe(true);
   });
});
