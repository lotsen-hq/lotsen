import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { tick } from 'svelte';
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest';
import { stopOf, writtenOf } from './stops';
import FilesPanel from '../src/components/FilesPanel.svelte';
import type { FileReference } from '../src/lib/stopFiles';
import type { KnownFiles } from '../src/lib/tourRecord.svelte';
import type { ChangedFile, CodeReference, FileMark, FileSide, RepositoryEntry, Stop, StopPassage, TourSummary } from '../src/lib/types';
import type { KnownDirectory } from '../src/lib/viewState';
import { eventFixtures } from './contract/fixtures.generated';
import { hoverTooltip } from './tooltip';
import { currentRows } from './treeRows';

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

function stop(id: string, passages: StopPassage[] | null, references: CodeReference[] = []): Stop {
   return stopOf({ id, title: id, kind: 'inspect', chapter: 'only', content: passages === null ? null : { headline: id, passages, html: { headline: id }, references } });
}

function link(side: CodeReference['side'], path: string): CodeReference {
   return { side, path, startLine: 3, endLine: 4 };
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

// The IDs of the rows that a reveal scrolled into view, in order.
let scrolls: MockInstance<Element['scrollIntoView']>;
const scrolledRows = () => scrolls.mock.contexts.map((row) => (row as HTMLElement).dataset.id);
const tabStops = (tree: HTMLElement) =>
   rowIds(tree).filter(
      (id) =>
         within(tree)
            .getAllByRole('treeitem')
            .find((item) => item.dataset.id === id)?.tabIndex === 0,
   );

/** Clicks Reveal the open file, which keeps the focus that the click gives it. */
async function revealOpenFile(): Promise<void> {
   const button = toggle('Reveal the open file');
   button.focus();
   await fireEvent.click(button);
}

beforeEach(() => {
   localStorage.clear();
   scrolls = vi.spyOn(Element.prototype, 'scrollIntoView');
});
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

      for (const name of ['Changed', 'Repository', 'Tree', 'Flat', 'Group by stop', 'Dim other files', 'Show reasons', 'Collapse all']) {
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

      it('lists a changed file that only a ref: link reaches under This stop', async () => {
         renderPanel(stop('linked', [passage('head', 'src/pay.ts')], [link('head', 'README.md')]));

         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Flat'));

         expect(rowIds(stopTree())).toEqual(['README.md', 'src/pay.ts']);
      });

      it("groups a stale stop's ref: links only on a side where one of the stop's passages is at the pin", async () => {
         const stale = stop('stale', [{ ...passage('head', 'src/pay.ts'), commit: 'c'.repeat(40) }, passage('base', 'src/pay.ts')], [link('head', 'README.md'), link('base', 'src/total.ts')]);
         renderPanel({ ...writtenOf(stale), staleOn: 2 });

         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Flat'));

         expect(rowIds(stopTree())).toEqual(['lib/total.ts', 'src/pay.ts']);
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

      it('leaves a changed file that only a ref: link reaches undimmed', async () => {
         renderPanel(stop('linked', [], [link('head', 'README.md')]));
         await fireEvent.click(toggle('Flat'));

         expect(dimmedRows()).toEqual(['crlf.txt', 'lib/total.ts', 'src/app/main.ts', 'src/client.g.ts', 'src/pay.ts']);
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

   describe('of a repository tour', () => {
      const snapshot = 'cccccccccccccccccccccccccccccccccccccccc';
      const repositoryTour: TourSummary = {
         ...tour,
         target: { kind: 'repository', platform: 'test', url: 'https://platform.test/shop' },
         pin: { state: 'snapshot', head: snapshot, remoteUrl: 'https://example.test/repo.git' },
      };
      const atSnapshot = (path: string): StopPassage => ({ ...passage('head', path), commit: snapshot });
      // The money stop reaches README.md only through a link, and its base-side link names no file of a snapshot.
      const cartStop = stop('cart', [atSnapshot('src/cart.ts')]);
      const moneyStop = stop('money', [atSnapshot('src/lib/money.ts'), atSnapshot('src/cart.ts')], [link('head', 'README.md'), link('base', 'docs/old.md')]);
      const stops = [cartStop, moneyStop];
      const entries: Record<string, RepositoryEntry[]> = {
         '': [
            { path: 'src', type: 'directory' },
            { path: 'README.md', type: 'file' },
         ],
         src: [
            { path: 'src/lib', type: 'directory' },
            { path: 'src/cart.ts', type: 'file' },
         ],
         'src/lib': [
            { path: 'src/lib/money.ts', type: 'file' },
            { path: 'src/lib/util.ts', type: 'file' },
         ],
      };

      function renderRepositoryTour({
         selectedText = null,
         currentStop = null,
         plan = stops,
         failing,
      }: { selectedText?: FileReference | null; currentStop?: Stop | null; plan?: Stop[]; failing?: string } = {}) {
         const listDirectory = vi.fn((side: FileSide, path: string | null): Promise<KnownDirectory> =>
            Promise.resolve(
               path === failing
                  ? { state: 'failed', message: `The folder ${path} could not be listed.`, reason: null }
                  : { state: 'loaded', listing: { path, side, commit: snapshot, entries: entries[path ?? '']! } },
            ),
         );
         const onopen = vi.fn();
         const view = render(FilesPanel, {
            props: { tour: repositoryTour, known: undefined, selectedPath: null, selectedText, currentStop, stops: plan, onselect: vi.fn(), onreview: vi.fn(), onopen, listDirectory },
         });
         return { listDirectory, onopen, rerender: view.rerender };
      }

      const visitedTree = () => screen.getByRole('tree', { name: 'Visited files' });
      const dimmed = () =>
         screen
            .getAllByRole('treeitem')
            .filter((item) => item.querySelector(':scope > .row')?.classList.contains('dimmed'))
            .map((item) => item.dataset.id);

      async function openRepository(): Promise<HTMLElement> {
         await fireEvent.click(toggle('Repository'));
         return screen.findByRole('tree', { name: 'Repository files' });
      }

      it('opens on the Visited view, with the layout and stop toggles and no side, reasons, review count or repository listing', () => {
         const { listDirectory } = renderRepositoryTour();

         expect(toggle('Visited').getAttribute('aria-pressed')).toBe('true');
         expect(toggle('Repository').getAttribute('aria-pressed')).toBe('false');
         expect(screen.getAllByRole('button').map((button) => button.getAttribute('aria-label'))).toEqual([
            'Visited',
            'Repository',
            'Tree',
            'Flat',
            'Group by stop',
            'Dim other files',
            'Collapse all',
            'Reveal the open file',
         ]);
         expect(visitedTree()).toBeTruthy();
         expect(screen.queryByText(/reviewed/)).toBeNull();
         expect(
            within(visitedTree())
               .getAllByRole('treeitem')
               .map((item) => item.getAttribute('aria-checked')),
         ).toEqual([null, null, null, null, null]);
         expect(screen.queryByRole('tree', { name: 'Repository files' })).toBeNull();
         expect(listDirectory).not.toHaveBeenCalled();
      });

      it('lists every file that a passage or a link of a stop visits at the head, as a tree and flat by full path', async () => {
         renderRepositoryTour();

         expect(rowIds(visitedTree())).toEqual(['src/', 'src/lib/', 'src/lib/money.ts', 'src/cart.ts', 'README.md']);

         await fireEvent.click(toggle('Flat'));

         expect(labels(visitedTree())).toEqual(['README.md, Visited by 1 stop', 'src/cart.ts, Visited by 2 stops', 'src/lib/money.ts, Visited by 1 stop']);
      });

      it('marks each file with the file icon, a pin and the number of stops, which the label and the tooltip say too, and no change', async () => {
         renderRepositoryTour();

         const cart = within(visitedTree()).getByRole('treeitem', { name: 'cart.ts, Visited by 2 stops' });
         const visits = cart.querySelector('.visits')!;
         expect(visits.getAttribute('aria-hidden')).toBe('true');
         expect([visits.querySelector('svg')?.classList.contains('lucide-map-pin'), visits.textContent]).toEqual([true, '2']);
         expect(cart.querySelector('.icon.seti')).not.toBeNull();
         expect(cart.querySelector('.marker')).toBeNull();
         expect(cart.querySelector('.check')).toBeNull();
         expect(await hoverTooltip(cart.firstElementChild!)).toBe('src/cart.ts\nVisited by 2 stops');
         expect(within(visitedTree()).getByRole('treeitem', { name: 'src' }).querySelector(':scope > .row > .visits')).toBeNull();
      });

      it('dims no file and lists every file under This stop when the current stop visits them all', async () => {
         renderRepositoryTour({ currentStop: moneyStop });
         await fireEvent.click(toggle('Flat'));

         expect(dimmed()).toEqual([]);

         await fireEvent.click(toggle('Group by stop'));

         expect(screen.getByText('This stop')).toBeTruthy();
         expect(rowIds(screen.getByRole('tree', { name: 'Visited files in this stop' }))).toEqual(['README.md', 'src/cart.ts', 'src/lib/money.ts']);
         expect(screen.queryByRole('tree', { name: 'Other visited files' })).toBeNull();
      });

      it("dims the files outside the current stop, and lists the stop's files first under Group by stop", async () => {
         const { rerender } = renderRepositoryTour({ currentStop: cartStop });

         expect(dimmed()).toEqual(['src/lib/', 'src/lib/money.ts', 'README.md']);

         await fireEvent.click(toggle('Group by stop'));

         expect(screen.getAllByRole('heading').map((heading) => heading.textContent)).toEqual(['This stop', 'Other files']);
         expect(rowIds(screen.getByRole('tree', { name: 'Visited files in this stop' }))).toEqual(['src/', 'src/cart.ts']);
         expect(rowIds(screen.getByRole('tree', { name: 'Other visited files' }))).toEqual(['src/lib/', 'src/lib/money.ts', 'README.md']);
         expect(dimmed()).toEqual(['src/lib/', 'src/lib/money.ts', 'README.md']);
         expect(localStorage.getItem('lotsen.groupFilesByStop')).toBe('true');

         await rerender({ currentStop: null });

         expect(screen.queryByText('This stop')).toBeNull();
         expect(dimmed()).toEqual([]);
         expect(rowIds(visitedTree())).toHaveLength(5);
      });

      it('opens a file at the head, and marks the open file at the head only', async () => {
         const { onopen, rerender } = renderRepositoryTour({ selectedText: { side: 'head', path: 'README.md' } });

         expect(currentRows(visitedTree())).toEqual(['README.md, Visited by 1 stop']);
         await fireEvent.click(within(visitedTree()).getByText('cart.ts'));
         expect(onopen).toHaveBeenCalledWith('head', 'src/cart.ts');

         await rerender({ selectedText: { side: 'base', path: 'README.md' } });
         expect(currentRows(visitedTree())).toEqual([]);
      });

      it('keeps the open file marked across a switch to the Repository view and back', async () => {
         renderRepositoryTour({ selectedText: { side: 'head', path: 'README.md' } });

         const repository = await openRepository();
         expect(within(repository).getByRole('treeitem', { name: 'README.md, Visited by 1 stop' }).getAttribute('aria-current')).toBe('true');
         await fireEvent.click(toggle('Visited'));

         expect(currentRows(visitedTree())).toEqual(['README.md, Visited by 1 stop']);
      });

      it('says so in one line when no stop visits a file: without a plan, or with every stop stale', async () => {
         const { rerender } = renderRepositoryTour({ plan: [] });

         expect(screen.getByText('No stop visits a file yet.')).toBeTruthy();
         expect(screen.queryByRole('tree')).toBeNull();
         expect(toggle('Flat')).toBeTruthy();

         await rerender({ stops: [stop('pending', null), { ...writtenOf(stop('stale', [{ ...atSnapshot('src/cart.ts'), commit: 'd'.repeat(40) }])), staleOn: 1 }] });

         expect(screen.getByText('No stop visits a file yet.')).toBeTruthy();
      });

      it("leaves out a stale stop's links on a side where none of its passages is at the snapshot", () => {
         const stale = { ...writtenOf(stop('stale', [{ ...atSnapshot('src/cart.ts'), commit: 'd'.repeat(40) }], [link('head', 'docs/guide.md')])), staleOn: 1 };

         renderRepositoryTour({ plan: [cartStop, stale] });

         expect(rowIds(visitedTree())).toEqual(['src/', 'src/cart.ts']);
      });

      it('still shows the whole repository at the head with tour markers in the Repository view, listed the first time it opens', async () => {
         const { listDirectory } = renderRepositoryTour();

         const tree = await openRepository();

         expect(toggle('Repository').getAttribute('aria-pressed')).toBe('true');
         expect(screen.queryByRole('button', { name: 'Flat' })).toBeNull();
         expect(screen.queryByRole('button', { name: 'Head' })).toBeNull();
         expect(labels(tree)).toEqual(['src, Visited by 2 stops', 'README.md, Visited by 1 stop']);
         expect(listDirectory.mock.calls).toEqual([['head', null]]);
      });

      it('marks each visited file and folder of the Repository view with a pin and the number of stops, which the tooltip says too', async () => {
         renderRepositoryTour();
         const tree = await openRepository();

         await fireEvent.click(within(tree).getByText('src'));

         const cart = await within(tree).findByRole('treeitem', { name: 'cart.ts, Visited by 2 stops' });
         expect(labels(tree)).toEqual(['src, Visited by 2 stops', 'lib, Visited by 1 stop', 'cart.ts, Visited by 2 stops', 'README.md, Visited by 1 stop']);
         const visits = cart.querySelector('.visits')!;
         expect(visits.getAttribute('aria-hidden')).toBe('true');
         expect([visits.querySelector('svg')?.classList.contains('lucide-map-pin'), visits.textContent]).toEqual([true, '2']);
         expect(await hoverTooltip(cart.firstElementChild!)).toBe('src/cart.ts\nVisited by 2 stops');
         expect(await hoverTooltip(within(tree).getByRole('treeitem', { name: 'lib, Visited by 1 stop' }).firstElementChild!)).toBe('src/lib\nVisited by 1 stop');
      });

      it('opens a file of the Repository view at the head, and marks the open file at the head', async () => {
         const { onopen } = renderRepositoryTour({ selectedText: { side: 'head', path: 'README.md' } });
         const tree = await openRepository();

         await fireEvent.click(within(tree).getByText('README.md'));

         expect(onopen).toHaveBeenCalledWith('head', 'README.md');
         expect(within(tree).getByRole('treeitem', { name: 'README.md, Visited by 1 stop' }).getAttribute('aria-current')).toBe('true');
      });

      it('collapse all closes every folder of the Visited view', async () => {
         renderRepositoryTour();

         await fireEvent.click(toggle('Collapse all'));

         expect(rowIds(visitedTree())).toEqual(['src/', 'README.md']);
      });

      it('collapse all closes every folder of the Repository view and keeps the listings, so no folder lists again when it expands', async () => {
         const { listDirectory } = renderRepositoryTour();
         const tree = await openRepository();
         await fireEvent.click(within(tree).getByText('src'));
         await fireEvent.click(await within(tree).findByText('lib'));
         await within(tree).findByText('money.ts');

         await fireEvent.click(toggle('Collapse all'));

         expect(rowIds(tree)).toEqual(['src/', 'README.md']);
         await fireEvent.click(within(tree).getByText('src'));
         expect(rowIds(tree)).toEqual(['src/', 'src/lib/', 'src/cart.ts', 'README.md']);
         expect(listDirectory.mock.calls).toEqual([
            ['head', null],
            ['head', 'src'],
            ['head', 'src/lib'],
         ]);
      });

      it("reveal the open file reopens its folders in a repository tour's Visited view, without a listing", async () => {
         const { listDirectory } = renderRepositoryTour({ selectedText: { side: 'head', path: 'src/lib/money.ts' } });
         await fireEvent.click(toggle('Collapse all'));

         await revealOpenFile();

         expect(rowIds(visitedTree())).toEqual(['src/', 'src/lib/', 'src/lib/money.ts', 'src/cart.ts', 'README.md']);
         await waitFor(() => expect(scrolledRows()).toEqual(['src/lib/money.ts']));
         expect(listDirectory).not.toHaveBeenCalled();
      });

      it('reveal the open file lists and expands each folder that holds it in order in the Repository view, and makes its row the tab stop', async () => {
         const { listDirectory } = renderRepositoryTour({ selectedText: { side: 'head', path: 'src/lib/money.ts' } });
         const tree = await openRepository();

         await revealOpenFile();

         await waitFor(() => expect(scrolledRows()).toEqual(['src/lib/money.ts']));
         expect(listDirectory.mock.calls).toEqual([
            ['head', null],
            ['head', 'src'],
            ['head', 'src/lib'],
         ]);
         expect(rowIds(tree)).toEqual(['src/', 'src/lib/', 'src/lib/money.ts', 'src/lib/util.ts', 'src/cart.ts', 'README.md']);
         expect(tabStops(tree)).toEqual(['src/lib/money.ts']);
      });

      it('reveal the open file stops at a folder whose listing fails, which shows its failure', async () => {
         const { listDirectory } = renderRepositoryTour({ selectedText: { side: 'head', path: 'src/lib/money.ts' }, failing: 'src/lib' });
         const tree = await openRepository();

         await revealOpenFile();

         expect(await within(tree).findByText('The folder src/lib could not be listed.')).toBeTruthy();
         expect(rowIds(tree)).toEqual(['src/', 'src/lib/', 'src/cart.ts', 'README.md']);
         expect(within(tree).getByRole('treeitem', { name: 'lib, Visited by 1 stop' }).getAttribute('aria-expanded')).toBe('true');
         expect(listDirectory.mock.calls).toEqual([
            ['head', null],
            ['head', 'src'],
            ['head', 'src/lib'],
         ]);
         expect(scrolledRows()).toEqual([]);
      });

      it('reveal the open file switches from the Visited view to the Repository view for a file that no stop visits', async () => {
         renderRepositoryTour({ selectedText: { side: 'head', path: 'src/lib/util.ts' } });

         await revealOpenFile();

         expect(toggle('Repository').getAttribute('aria-pressed')).toBe('true');
         await waitFor(() => expect(scrolledRows()).toEqual(['src/lib/util.ts']));
         expect(document.activeElement).toBe(toggle('Reveal the open file'));
      });

      it('an automatic reveal request for a file that the Visited view does not hold changes nothing', async () => {
         const { listDirectory, rerender } = renderRepositoryTour({ selectedText: { side: 'head', path: 'src/lib/util.ts' } });
         await fireEvent.click(toggle('Collapse all'));

         await rerender({ revealRequest: { side: 'head', path: 'src/lib/util.ts', sequence: 1 } });
         await tick();

         expect(toggle('Visited').getAttribute('aria-pressed')).toBe('true');
         expect(rowIds(visitedTree())).toEqual(['src/', 'README.md']);
         expect(listDirectory).not.toHaveBeenCalled();
         expect(scrolledRows()).toEqual([]);
      });
   });

   describe('collapse all', () => {
      it('closes every folder of the Changed view', async () => {
         renderPanel();

         await fireEvent.click(toggle('Collapse all'));

         expect(rowIds(screen.getByRole('tree', { name: 'Changed files' }))).toEqual(['lib/', 'src/', 'crlf.txt', 'README.md']);
      });

      it('closes every folder of both trees when the files are grouped by stop', async () => {
         renderPanel(payStop);
         await fireEvent.click(toggle('Group by stop'));

         await fireEvent.click(toggle('Collapse all'));

         expect(rowIds(screen.getByRole('tree', { name: 'Changed files in this stop' }))).toEqual(['lib/', 'src/']);
         expect(rowIds(screen.getByRole('tree', { name: 'Other changed files' }))).toEqual(['src/', 'crlf.txt', 'README.md']);
      });

      it('is disabled in the flat layout, which has no folders, and enabled in the Repository view', async () => {
         renderPanel();

         await fireEvent.click(toggle('Flat'));
         expect(toggle('Collapse all').hasAttribute('disabled')).toBe(true);

         await fireEvent.click(toggle('Repository'));
         expect(toggle('Collapse all').hasAttribute('disabled')).toBe(false);
      });
   });

   describe('reveal', () => {
      const changedTree = () => screen.getByRole('tree', { name: 'Changed files' });
      // The whole repository at either side: the same folders, with an unchanged file deep in src and the renamed file's old path.
      const prEntries: Record<string, RepositoryEntry[]> = {
         '': [
            { path: 'src', type: 'directory' },
            { path: 'README.md', type: 'file' },
         ],
         src: [
            { path: 'src/old', type: 'directory' },
            { path: 'src/pay.ts', type: 'file' },
            { path: 'src/total.ts', type: 'file' },
         ],
         'src/old': [{ path: 'src/old/legacy.ts', type: 'file' }],
      };

      function renderOpen(open: { selectedPath?: string | null; selectedText?: FileReference | null }, currentStop: Stop | null = null) {
         const listDirectory = vi.fn((side: FileSide, path: string | null): Promise<KnownDirectory> =>
            Promise.resolve({ state: 'loaded', listing: { path, side, commit: side === 'base' ? pinnedPin.mergeBase : pinnedPin.head, entries: prEntries[path ?? '']! } }),
         );
         const view = render(FilesPanel, { props: { tour, known, selectedPath: null, selectedText: null, currentStop, onselect: vi.fn(), listDirectory, ...open } });
         return { listDirectory, rerender: view.rerender };
      }

      it('reveal the open file is disabled while no file is open', async () => {
         const { rerender } = renderOpen({});

         expect(toggle('Reveal the open file').hasAttribute('disabled')).toBe(true);

         await rerender({ selectedPath: 'src/pay.ts' });
         expect(toggle('Reveal the open file').hasAttribute('disabled')).toBe(false);
      });

      it('reveal the open file reopens the collapsed folders that hold it in the Changed tree, keeps the focus on the button and makes its row the tab stop', async () => {
         renderOpen({ selectedPath: 'src/app/main.ts' });
         await fireEvent.click(toggle('Collapse all'));

         await revealOpenFile();

         expect(rowIds(changedTree())).toEqual(['lib/', 'src/', 'src/app/', 'src/app/main.ts', 'src/client.g.ts', 'src/pay.ts', 'crlf.txt', 'README.md']);
         await waitFor(() => expect(scrolledRows()).toEqual(['src/app/main.ts']));
         expect(document.activeElement).toBe(toggle('Reveal the open file'));
         expect(tabStops(changedTree())).toEqual(['src/app/main.ts']);
      });

      it.each([
         ['the stop group', 'lib/total.ts', 'Changed files in this stop', ['lib/', 'lib/total.ts', 'src/']],
         ['the other files', 'src/app/main.ts', 'Other changed files', ['src/', 'src/app/', 'src/app/main.ts', 'src/client.g.ts', 'crlf.txt', 'README.md']],
      ])('reveal the open file reopens its folders in %s when the files are grouped by stop', async (_group, path, treeName, rows) => {
         renderOpen({ selectedPath: path }, payStop);
         await fireEvent.click(toggle('Group by stop'));
         await fireEvent.click(toggle('Collapse all'));

         await revealOpenFile();

         expect(rowIds(screen.getByRole('tree', { name: treeName }))).toEqual(rows);
         await waitFor(() => expect(scrolledRows()).toEqual([path]));
      });

      it('reveal the open file scrolls to its row in the flat layout, in whichever list holds it', async () => {
         renderOpen({ selectedPath: 'src/client.g.ts' }, payStop);
         await fireEvent.click(toggle('Flat'));
         await fireEvent.click(toggle('Group by stop'));

         await revealOpenFile();

         await waitFor(() => expect(scrolledRows()).toEqual(['src/client.g.ts']));
         expect(tabStops(screen.getByRole('tree', { name: 'Other changed files' }))).toEqual(['src/client.g.ts']);
      });

      it("reveal the open file switches to the Repository view at the file's side for a file that the Changed view does not hold, and keeps the focus on the button", async () => {
         const { listDirectory } = renderOpen({ selectedText: { side: 'base', path: 'src/old/legacy.ts' } });
         toggle('Reveal the open file').focus();

         await revealOpenFile();

         expect(toggle('Repository').getAttribute('aria-pressed')).toBe('true');
         expect(toggle('Base').getAttribute('aria-pressed')).toBe('true');
         await waitFor(() => expect(scrolledRows()).toEqual(['src/old/legacy.ts']));
         expect(listDirectory.mock.calls).toEqual([
            ['base', null],
            ['base', 'src'],
            ['base', 'src/old'],
         ]);
         expect(document.activeElement).toBe(toggle('Reveal the open file'));
         expect(tabStops(screen.getByRole('tree', { name: 'Repository files' }))).toEqual(['src/old/legacy.ts']);
      });

      it("reveal the open file shows a changed file in the Repository view at the view's side, by the file's path there", async () => {
         const { listDirectory } = renderOpen({ selectedPath: 'lib/total.ts' });
         await fireEvent.click(toggle('Repository'));
         await fireEvent.click(toggle('Base'));

         await revealOpenFile();

         await waitFor(() => expect(scrolledRows()).toEqual(['src/total.ts']));
         expect(toggle('Base').getAttribute('aria-pressed')).toBe('true');
         expect(listDirectory.mock.calls.at(-1)).toEqual(['base', 'src']);
      });

      it('an automatic reveal request reopens the folders of a file that the current view holds, leaves the focus where it was, and acts once on each sequence number', async () => {
         const { rerender } = renderOpen({ selectedPath: 'src/pay.ts' });
         await fireEvent.click(toggle('Collapse all'));
         const outside = document.body.appendChild(document.createElement('button'));
         outside.focus();

         await rerender({ revealRequest: { side: 'head', path: 'src/app/main.ts', sequence: 1 } });

         await waitFor(() => expect(scrolledRows()).toEqual(['src/app/main.ts']));
         expect(rowIds(changedTree())).toContain('src/app/main.ts');
         expect(document.activeElement).toBe(outside);
         outside.remove();

         await fireEvent.click(toggle('Collapse all'));
         await rerender({ revealRequest: { side: 'head', path: 'src/app/main.ts', sequence: 1 } });
         expect(rowIds(changedTree())).toEqual(['lib/', 'src/', 'crlf.txt', 'README.md']);

         await rerender({ revealRequest: { side: 'head', path: 'src/app/main.ts', sequence: 2 } });
         expect(rowIds(changedTree())).toContain('src/app/main.ts');
      });

      it('an automatic reveal request for a file that the current view does not hold changes nothing', async () => {
         const { listDirectory, rerender } = renderOpen({ selectedPath: 'src/pay.ts' });
         await fireEvent.click(toggle('Collapse all'));

         await rerender({ revealRequest: { side: 'base', path: 'src/old/legacy.ts', sequence: 1 } });
         await tick();

         expect(toggle('Changed').getAttribute('aria-pressed')).toBe('true');
         expect(rowIds(changedTree())).toEqual(['lib/', 'src/', 'crlf.txt', 'README.md']);
         expect(listDirectory).not.toHaveBeenCalled();
         expect(scrolledRows()).toEqual([]);

         await fireEvent.click(toggle('Repository'));
         await fireEvent.click(toggle('Base'));
         await screen.findByRole('tree', { name: 'Repository files' });
         await rerender({ revealRequest: { side: 'head', path: 'src/app/main.ts', sequence: 2 } });
         await tick();

         expect(toggle('Base').getAttribute('aria-pressed')).toBe('true');
         expect(listDirectory.mock.calls).toEqual([
            ['head', null],
            ['base', null],
         ]);
         expect(scrolledRows()).toEqual([]);
      });

      it('an automatic reveal request lists the folders of a file at the side of the Repository view', async () => {
         const { listDirectory, rerender } = renderOpen({});
         await fireEvent.click(toggle('Repository'));
         await screen.findByRole('tree', { name: 'Repository files' });

         await rerender({ revealRequest: { side: 'head', path: 'src/old/legacy.ts', sequence: 1 } });

         await waitFor(() => expect(scrolledRows()).toEqual(['src/old/legacy.ts']));
         expect(listDirectory.mock.calls).toEqual([
            ['head', null],
            ['head', 'src'],
            ['head', 'src/old'],
         ]);
      });

      it('a reveal request from before the panel mounted reveals nothing', async () => {
         render(FilesPanel, { props: { tour, known, selectedPath: null, onselect: vi.fn(), revealRequest: { side: 'head', path: 'src/app/main.ts', sequence: 4 } } });
         await tick();
         await tick();

         expect(scrolledRows()).toEqual([]);
      });
   });

   it('shows the tree when browser storage throws', () => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
         throw new DOMException('Blocked', 'SecurityError');
      });

      renderPanel(payStop);

      expect(screen.getByRole('tree', { name: 'Changed files' })).toBeTruthy();
   });
});
