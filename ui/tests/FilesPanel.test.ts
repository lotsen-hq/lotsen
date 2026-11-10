import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { stopOf, writtenOf } from './stops';
import FilesPanel from '../src/components/FilesPanel.svelte';
import type { KnownFiles } from '../src/lib/tourRecord.svelte';
import type { ChangedFile, FileMark, Stop, StopPassage, TourSummary } from '../src/lib/types';
import { eventFixtures } from './contract/fixtures.generated';
import { hoverTooltip } from './tooltip';

const tour: TourSummary = eventFixtures.tourCreated;
const pinnedPin = tour.pin as Extract<TourSummary['pin'], { state: 'pinned' }>;

const files: ChangedFile[] = [
   { path: 'src/pay.ts', kind: 'modified' },
   { path: 'README.md', kind: 'modified' },
   { path: 'src/app/main.ts', kind: 'added' },
   { path: 'lib/total.ts', kind: 'renamed', oldPath: 'src/total.ts' },
   { path: 'src/client.g.ts', kind: 'modified' },
   { path: 'crlf.txt', kind: 'modified', lineEndingsOnly: true },
];
const known: KnownFiles = { state: 'loaded', list: { mergeBase: pinnedPin.mergeBase, head: pinnedPin.head, files, reviewedPaths: [] } };
const generated: FileMark = { path: 'src/client.g.ts', mark: 'generated', reason: 'A tool writes it.' };

function passage(side: StopPassage['side'], path: string, role: StopPassage['role'] = 'primary'): StopPassage {
   return { side, path, startLine: 1, endLine: 2, role, commit: side === 'base' ? pinnedPin.mergeBase : pinnedPin.head, text: 'text' };
}

function stop(id: string, passages: StopPassage[] | null): Stop {
   return stopOf({ id, title: id, kind: 'inspect', chapter: 'only', content: passages === null ? null : { headline: id, passages, html: { headline: id } } });
}

// The pay stop points at a changed file at the head and at a renamed file by its old path at the base.
const payStop = stop('pay', [passage('head', 'src/pay.ts'), passage('base', 'src/total.ts', 'related')]);
const readmeStop = stop('readme', [passage('head', 'README.md')]);

function renderPanel(currentStop: Stop | null = null) {
   const onselect = vi.fn();
   const view = render(FilesPanel, { props: { tour, known, selectedPath: null, fileMarks: new Map([[generated.path, generated]]), currentStop, onselect } });
   return {
      onselect,
      rerender: (next: Stop | null) => view.rerender({ currentStop: next }),
      reload: (list: ChangedFile[]) => view.rerender({ known: { state: 'loaded', list: { mergeBase: pinnedPin.mergeBase, head: pinnedPin.head, files: list, reviewedPaths: [] } } }),
      update: (props: Partial<{ tour: TourSummary; known: KnownFiles }>) => view.rerender(props),
      unmount: view.unmount,
   };
}

const toggle = (name: string) => screen.getByRole('button', { name });
const labels = (tree: HTMLElement) =>
   within(tree)
      .getAllByRole('treeitem')
      .map((item) => item.getAttribute('aria-label'));
const rowIds = (tree: HTMLElement) =>
   within(tree)
      .getAllByRole('treeitem')
      .map((item) => item.dataset.id);

beforeEach(() => localStorage.clear());
afterEach(() => vi.restoreAllMocks());

describe('FilesPanel', () => {
   it('shows the changed files as a tree by default, folders first, each level in name order', () => {
      renderPanel();

      expect(toggle('Tree').getAttribute('aria-pressed')).toBe('true');
      expect(toggle('Group by stop').getAttribute('aria-pressed')).toBe('false');
      expect(rowIds(screen.getByRole('tree', { name: 'Changed files' }))).toEqual([
         'lib/',
         'lib/total.ts',
         'src/',
         'src/app/',
         'src/app/main.ts',
         'src/client.g.ts',
         'src/pay.ts',
         'crlf.txt',
         'README.md',
      ]);
   });

   it('names each icon toggle in its tooltip', async () => {
      renderPanel();

      for (const name of ['Changed', 'Repository', 'Tree', 'Flat', 'Group by stop', 'Dim other files', 'Show reasons']) {
         expect(await hoverTooltip(toggle(name))).toBe(name);
      }
   });

   it('lists the changed files flat by their full paths in path order, and keeps the choice in the browser', async () => {
      const { unmount } = renderPanel();

      await fireEvent.click(toggle('Flat'));

      expect(toggle('Flat').getAttribute('aria-pressed')).toBe('true');
      expect(rowIds(screen.getByRole('tree', { name: 'Changed files' }))).toEqual(['crlf.txt', 'lib/total.ts', 'README.md', 'src/app/main.ts', 'src/client.g.ts', 'src/pay.ts']);
      expect(screen.getByRole('treeitem', { name: 'src/app/main.ts, Added' })).toBeTruthy();
      unmount();

      renderPanel();
      expect(toggle('Flat').getAttribute('aria-pressed')).toBe('true');
      expect(screen.getAllByRole('treeitem')).toHaveLength(files.length);
   });

   it('moves through the flat list with the arrow keys, and selects a file on Enter', async () => {
      const { onselect } = renderPanel();
      await fireEvent.click(toggle('Flat'));
      const first = screen.getByRole('treeitem', { name: 'crlf.txt, Modified, line endings only' });
      expect(first.tabIndex).toBe(0);
      first.focus();

      await fireEvent.keyDown(first, { key: 'ArrowDown' });
      await fireEvent.keyDown(document.activeElement!, { key: 'ArrowDown' });
      expect(document.activeElement?.getAttribute('aria-label')).toBe('README.md, Modified');
      await fireEvent.keyDown(document.activeElement!, { key: 'End' });
      await fireEvent.keyDown(document.activeElement!, { key: 'Enter' });

      expect(onselect).toHaveBeenCalledWith('src/pay.ts');
   });

   describe('grouped by stop', () => {
      const stopTree = () => screen.getByRole('tree', { name: 'Changed files in this stop' });
      const otherTree = () => screen.getByRole('tree', { name: 'Other changed files' });

      it("lists the current stop's files first in the tree view, under their own heading, and the rest below", async () => {
         renderPanel(payStop);

         await fireEvent.click(toggle('Group by stop'));

         expect(toggle('Group by stop').getAttribute('aria-pressed')).toBe('true');
         expect(screen.getAllByRole('heading').map((heading) => heading.textContent)).toEqual(['This stop', 'Other files']);
         expect(rowIds(stopTree())).toEqual(['lib/', 'lib/total.ts', 'src/', 'src/pay.ts']);
         expect(rowIds(otherTree())).toEqual(['src/', 'src/app/', 'src/app/main.ts', 'src/client.g.ts', 'crlf.txt', 'README.md']);
         expect(screen.queryByRole('tree', { name: 'Changed files' })).toBeNull();
         expect(stopTree().compareDocumentPosition(otherTree()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      });

      it("lists the current stop's files first in the flat view", async () => {
         renderPanel(payStop);

         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Flat'));

         expect(rowIds(stopTree())).toEqual(['lib/total.ts', 'src/pay.ts']);
         expect(rowIds(otherTree())).toEqual(['crlf.txt', 'README.md', 'src/app/main.ts', 'src/client.g.ts']);
      });

      it('follows the current stop when it changes, and shows one list without a stop', async () => {
         const { rerender } = renderPanel(payStop);
         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Flat'));

         await rerender(readmeStop);
         expect(rowIds(stopTree())).toEqual(['README.md']);
         expect(rowIds(otherTree())).toEqual(['crlf.txt', 'lib/total.ts', 'src/app/main.ts', 'src/client.g.ts', 'src/pay.ts']);

         await rerender(null);
         expect(screen.queryByRole('heading')).toBeNull();
         expect(rowIds(screen.getByRole('tree', { name: 'Changed files' }))).toHaveLength(files.length);
      });

      it.each<[string, Stop]>([
         ['a pending stop', stop('pending', null)],
         ['a stop without passages', stop('overview', [])],
         ['a stop whose passages are all in unchanged files', stop('context', [passage('head', 'src/catalog.ts')])],
      ])('shows one list for %s', async (_case, current) => {
         renderPanel(current);

         await fireEvent.click(toggle('Group by stop'));

         expect(screen.queryByRole('heading')).toBeNull();
         expect(labels(screen.getByRole('tree', { name: 'Changed files' }))).toHaveLength(9);
      });

      it.each<[string, Stop, string[] | null]>([
         [
            'groups only the files of the passages at the pin',
            stop('stale', [{ ...passage('head', 'src/pay.ts'), commit: 'c'.repeat(40) }, passage('base', 'src/total.ts', 'related')]),
            ['lib/total.ts'],
         ],
         ['shows one list when no passage is at the pin', stop('stale', [{ ...passage('head', 'src/pay.ts'), commit: 'c'.repeat(40) }]), null],
      ])('for a stop with a passage at an earlier commit, %s', async (_case, current, grouped) => {
         renderPanel({ ...writtenOf(current), staleOn: 2 });

         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Flat'));

         if (grouped === null) {
            expect(screen.queryByRole('heading')).toBeNull();
         } else {
            expect(rowIds(stopTree())).toEqual(grouped);
         }
      });

      it("leaves out the other files' heading when the stop points at every file", async () => {
         const everything = stop(
            'all',
            files.map((file) => passage('head', file.path)),
         );
         renderPanel(everything);

         await fireEvent.click(toggle('Group by stop'));

         expect(screen.getAllByRole('heading').map((heading) => heading.textContent)).toEqual(['This stop']);
         expect(screen.queryByRole('tree', { name: 'Other changed files' })).toBeNull();
      });

      it('keeps the choice in the browser', async () => {
         const { unmount } = renderPanel(payStop);
         await fireEvent.click(toggle('Group by stop'));
         unmount();

         renderPanel(payStop);

         expect(toggle('Group by stop').getAttribute('aria-pressed')).toBe('true');
         expect(stopTree()).toBeTruthy();
      });
   });

   describe('with a focused row while grouped by stop', () => {
      const focusedRow = () => ({ id: (document.activeElement as HTMLElement | null)?.dataset.id, tree: document.activeElement?.closest('[role="tree"]')?.getAttribute('aria-label') });
      const cleanups: (() => void)[] = [];
      afterEach(() => cleanups.splice(0).forEach((cleanup) => cleanup()));

      function outsideButton() {
         const button = document.body.appendChild(document.createElement('button'));
         cleanups.push(() => button.remove());
         return button;
      }

      async function renderFocused(current: Stop | null, path: string) {
         const panel = renderPanel(current);
         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Flat'));
         const row = screen.getAllByRole('treeitem').find((item) => item.dataset.id === path)!;
         row.focus();
         return { ...panel, row };
      }

      it('keeps the focus on the same row when a pending stop becomes ready and the row stays among the other files', async () => {
         const { rerender, row } = await renderFocused(stop('pay', null), 'README.md');

         await rerender(payStop);

         expect(document.activeElement).toBe(row);
         expect(focusedRow()).toEqual({ id: 'README.md', tree: 'Other changed files' });
      });

      it("keeps the focus on the same row when the stop's passages change and when the list loads again", async () => {
         const { rerender, reload, row } = await renderFocused(payStop, 'README.md');

         await rerender(stop('pay', [passage('head', 'src/app/main.ts')]));
         expect(document.activeElement).toBe(row);

         await reload([...files]);
         expect(document.activeElement).toBe(row);
      });

      it("moves the focus with a row that moves into the stop's group, and back out", async () => {
         const { rerender } = await renderFocused(payStop, 'README.md');

         await rerender(readmeStop);
         expect(focusedRow()).toEqual({ id: 'README.md', tree: 'Changed files in this stop' });

         await rerender(payStop);
         expect(focusedRow()).toEqual({ id: 'README.md', tree: 'Other changed files' });
      });

      it("keeps the focus on a row of the stop's group when the next stop points at the same file", async () => {
         const { rerender } = await renderFocused(payStop, 'src/pay.ts');

         await rerender(stop('pay-again', [passage('head', 'src/pay.ts'), passage('head', 'README.md')]));

         expect(focusedRow()).toEqual({ id: 'src/pay.ts', tree: 'Changed files in this stop' });
      });

      it('keeps the focus on a row when the stop closes and the list becomes one again', async () => {
         const { rerender } = await renderFocused(payStop, 'src/pay.ts');

         await rerender(null);

         expect(focusedRow()).toEqual({ id: 'src/pay.ts', tree: 'Changed files' });
      });

      it('lets go of the focus when the pin fails and the list goes away, and leaves it outside the panel when the list comes back', async () => {
         const errors: unknown[] = [];
         const onerror = (event: ErrorEvent) => {
            errors.push(event.error);
            event.preventDefault();
         };
         window.addEventListener('error', onerror);
         cleanups.push(() => window.removeEventListener('error', onerror));
         const { update } = await renderFocused(payStop, 'README.md');

         await update({ tour: { ...tour, pin: { state: 'failed', reason: 'The pull request was abandoned.' } }, known: { state: 'loading' } });
         const outside = outsideButton();
         outside.focus();
         await update({ tour, known: { ...known } });
         await new Promise((resolve) => setTimeout(resolve, 0));

         expect(errors).toEqual([]);
         expect(document.activeElement).toBe(outside);
      });

      it.each([true, false])('leaves the focus on the page after the reviewer blurs a row, when a stop then moves that row, with a task between them: %s', async (waits) => {
         const { rerender, row } = await renderFocused(payStop, 'README.md');

         row.blur();
         if (waits) {
            await new Promise((resolve) => setTimeout(resolve, 0));
         }

         await rerender(readmeStop);

         expect(document.activeElement).toBe(document.body);
      });

      it('leaves the focus where the reviewer moved it, on a toggle or outside the panel', async () => {
         const { rerender } = await renderFocused(payStop, 'src/pay.ts');

         toggle('Tree').focus();
         await fireEvent.click(toggle('Tree'));
         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Group by stop'));
         expect(document.activeElement).toBe(toggle('Tree'));

         const outside = outsideButton();
         outside.focus();
         await rerender(readmeStop);
         expect(document.activeElement).toBe(outside);
      });
   });

   describe('dimmed outside the stop', () => {
      const dimmedRows = () =>
         screen
            .getAllByRole('treeitem')
            .filter((item) => item.querySelector(':scope > .row')?.classList.contains('dimmed'))
            .map((item) => item.dataset.id);

      it('dims the files outside the current stop and the folders that hold only such files by default, in the tree and grouped by stop', async () => {
         renderPanel(payStop);

         expect(toggle('Dim other files').getAttribute('aria-pressed')).toBe('true');
         expect(dimmedRows()).toEqual(['src/app/', 'src/app/main.ts', 'src/client.g.ts', 'crlf.txt', 'README.md']);

         await fireEvent.click(toggle('Group by stop'));

         expect(dimmedRows()).toEqual(['src/', 'src/app/', 'src/app/main.ts', 'src/client.g.ts', 'crlf.txt', 'README.md']);
      });

      it('dims nothing for a stop whose passages are all at an earlier commit, as for a stop outside the changed files', async () => {
         renderPanel({ ...writtenOf(stop('stale', [{ ...passage('head', 'src/pay.ts'), commit: 'c'.repeat(40) }])), staleOn: 2 });
         await fireEvent.click(toggle('Flat'));

         expect(dimmedRows()).toEqual([]);
      });

      it('follows the current stop, and dims nothing without a stop, for a stop outside the changed files, or with the toggle off', async () => {
         const panel = renderPanel(payStop);
         await fireEvent.click(toggle('Flat'));
         expect(dimmedRows()).toEqual(['crlf.txt', 'README.md', 'src/app/main.ts', 'src/client.g.ts']);

         await panel.rerender(readmeStop);
         expect(dimmedRows()).toEqual(['crlf.txt', 'lib/total.ts', 'src/app/main.ts', 'src/client.g.ts', 'src/pay.ts']);

         await panel.rerender(stop('elsewhere', [passage('head', 'src/unchanged.ts')]));
         expect(dimmedRows()).toEqual([]);

         await panel.rerender(null);
         expect(dimmedRows()).toEqual([]);

         await panel.rerender(payStop);
         await fireEvent.click(toggle('Dim other files'));
         expect(toggle('Dim other files').getAttribute('aria-pressed')).toBe('false');
         expect(dimmedRows()).toEqual([]);
         await fireEvent.click(toggle('Group by stop'));
         expect(dimmedRows()).toEqual([]);
      });

      it('keeps the toggle in the browser across a reload', async () => {
         const { unmount } = renderPanel(payStop);
         await fireEvent.click(toggle('Dim other files'));
         expect(localStorage.getItem('lotsen.dimOtherFiles')).toBe('false');
         unmount();

         const panel = renderPanel(payStop);
         expect(toggle('Dim other files').getAttribute('aria-pressed')).toBe('false');
         expect(dimmedRows()).toEqual([]);

         await fireEvent.click(toggle('Dim other files'));
         expect(localStorage.getItem('lotsen.dimOtherFiles')).toBe('true');
         panel.unmount();

         renderPanel(payStop);
         expect(toggle('Dim other files').getAttribute('aria-pressed')).toBe('true');
         expect(dimmedRows()).toEqual(['src/app/', 'src/app/main.ts', 'src/client.g.ts', 'crlf.txt', 'README.md']);
      });
   });

   describe('mark reasons', () => {
      const markedRow = () => screen.getByRole('treeitem', { name: 'client.g.ts, Modified, generated: A tool writes it.' });
      const reason = () => markedRow().querySelector('.reason');

      it("keeps a marked file's row to one line by default, with the reason in its tooltip and its label, and shows the reason with the toggle on", async () => {
         renderPanel();

         expect(toggle('Show reasons').getAttribute('aria-pressed')).toBe('false');
         expect(reason()).toBeNull();
         expect(markedRow().textContent).toContain('generated');
         expect(await hoverTooltip(markedRow().firstElementChild!)).toBe('src/client.g.ts\ngenerated: A tool writes it.');

         await fireEvent.click(toggle('Show reasons'));

         expect(toggle('Show reasons').getAttribute('aria-pressed')).toBe('true');
         expect(reason()?.textContent).toBe('A tool writes it.');
         await fireEvent.click(toggle('Show reasons'));
         expect(reason()).toBeNull();
      });

      it("hides and shows the reason in the tree of the stop's files too", async () => {
         renderPanel(stop('generated', [passage('head', 'src/client.g.ts')]));
         await fireEvent.click(toggle('Group by stop'));
         const stopRow = () => within(screen.getByRole('tree', { name: 'Changed files in this stop' })).getByRole('treeitem', { name: /^client\.g\.ts, Modified, generated: A tool writes it\.$/ });

         expect(stopRow().querySelector('.reason')).toBeNull();

         await fireEvent.click(toggle('Show reasons'));
         expect(stopRow().querySelector('.reason')?.textContent).toBe('A tool writes it.');
      });

      it('keeps the toggle in the browser across a reload', async () => {
         const { unmount } = renderPanel();
         await fireEvent.click(toggle('Show reasons'));
         expect(localStorage.getItem('lotsen.showMarkReasons')).toBe('true');
         unmount();

         const panel = renderPanel();
         expect(toggle('Show reasons').getAttribute('aria-pressed')).toBe('true');
         expect(reason()?.textContent).toBe('A tool writes it.');

         await fireEvent.click(toggle('Show reasons'));
         expect(localStorage.getItem('lotsen.showMarkReasons')).toBe('false');
         panel.unmount();

         renderPanel();
         expect(toggle('Show reasons').getAttribute('aria-pressed')).toBe('false');
         expect(reason()).toBeNull();
      });
   });

   describe('review marks', () => {
      const checked = (tree: HTMLElement) =>
         within(tree)
            .getAllByRole('treeitem')
            .map((item) => item.getAttribute('aria-checked'));

      function renderReviewed(reviewed: string[], currentStop: Stop | null = null) {
         const onreview = vi.fn();
         const view = render(FilesPanel, { props: { tour, known, selectedPath: null, reviewedPaths: new Set(reviewed), currentStop, onselect: vi.fn(), onreview } });
         return { onreview, rerender: (next: string[]) => view.rerender({ reviewedPaths: new Set(next) }) };
      }

      it.each(['Tree', 'Flat'])('shows a checkbox on each changed file in the %s view and counts the reviewed files', async (layout) => {
         const { rerender } = renderReviewed(['src/pay.ts', 'crlf.txt', 'not/changed.ts']);
         await fireEvent.click(toggle(layout));

         const rows = within(screen.getByRole('tree', { name: 'Changed files' })).getAllByRole('treeitem');
         expect(rows.filter((row) => row.dataset.id!.endsWith('/')).map((row) => row.getAttribute('aria-checked'))).toEqual(layout === 'Tree' ? [null, null, null] : []);
         expect(rows.filter((row) => row.getAttribute('aria-checked') === 'true').map((row) => row.dataset.id)).toEqual(layout === 'Tree' ? ['src/pay.ts', 'crlf.txt'] : ['crlf.txt', 'src/pay.ts']);
         expect(screen.getByText('2 of 6 reviewed')).toBeTruthy();

         await rerender(['src/pay.ts']);
         expect(screen.getByText('1 of 6 reviewed')).toBeTruthy();
      });

      it('shows the checkboxes in both trees when the files are grouped by stop', async () => {
         renderReviewed(['src/pay.ts'], payStop);

         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Flat'));

         expect(checked(screen.getByRole('tree', { name: 'Changed files in this stop' }))).toEqual(['false', 'true']);
         expect(checked(screen.getByRole('tree', { name: 'Other changed files' }))).toEqual(['false', 'false', 'false', 'false']);
         expect(screen.getByText('1 of 6 reviewed')).toBeTruthy();
      });

      it('shows a failure to store a mark', () => {
         render(FilesPanel, { props: { tour, known, selectedPath: null, reviewError: 'The review mark on README.md could not be saved.', onselect: vi.fn(), onreview: vi.fn() } });

         expect(screen.getByRole('alert').textContent).toContain('The review mark on README.md could not be saved.');
      });
   });

   it('shows none of the changed-file toggles in the whole-repository view', async () => {
      renderPanel();

      await fireEvent.click(toggle('Repository'));

      expect(screen.queryByRole('button', { name: 'Flat' })).toBeNull();
      expect(screen.queryByRole('button', { name: 'Group by stop' })).toBeNull();
      expect(screen.queryByRole('button', { name: 'Dim other files' })).toBeNull();
      expect(screen.queryByRole('button', { name: 'Show reasons' })).toBeNull();
   });

   it('shows the tree when browser storage throws', () => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
         throw new DOMException('Blocked', 'SecurityError');
      });

      renderPanel(payStop);

      expect(screen.getByRole('tree', { name: 'Changed files' })).toBeTruthy();
   });
});
