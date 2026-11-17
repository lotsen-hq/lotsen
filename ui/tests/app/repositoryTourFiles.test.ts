import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { Template, Transport, World } from '../backend/backend';
import { attachClaude, backend, openStop, openTour, seedRepositoryTour, startApp } from '../harness';
import { currentRows } from '../treeRows';
import { filesPanel, resetPageEachTest, worldOf } from './appPanels';
import { modeSwitch, stackItem, stackItems } from './stopViewSupport';

resetPageEachTest();

const title = 'How the cart adds up';
const cart = 'export function total(lines: number[]): number {\n   return lines.reduce((sum, line) => sum + line, 0);\n}\n';
const money = 'export function cents(value: number): number {\n   return Math.round(value * 100);\n}\n';
const passage = (path: string, startLine: number, role: 'primary' | 'related' = 'primary') => ({ side: 'head', path, startLine, endLine: startLine, role });

interface Seed {
   tourId: string;
   stopIds: string[];
   head: string;
}

// Both stops visit the cart, and only the second visits the money helper, so src counts two stops and src/lib one. The third stop has no content yet.
const visited: Template<Seed> = backend().template('repositoryTourFiles-visited', async (world) => {
   const seeded = await seedRepositoryTour(world, {
      files: { 'README.md': '# Shop\n', 'docs/guide.md': 'Read me.\n', 'src/cart.ts': cart, 'src/lib/money.ts': money },
      bound: false,
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'The cart', kind: 'overview', content: { headline: 'The cart sums its lines.', passages: [passage('src/cart.ts', 2)] } },
               { title: 'Cents', kind: 'context', content: { headline: 'Totals count cents.', passages: [passage('src/lib/money.ts', 2), passage('src/cart.ts', 1, 'related')] } },
               { title: 'Later', kind: 'context' },
            ],
         },
      ],
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds, head: seeded.head };
});

async function openTourFiles(): Promise<{ app: LotsenApp; world: World; transport: Transport; seed: Seed; panel: ReturnType<typeof within> }> {
   const { world, seed } = await worldOf(visited);
   const { app, transport } = await startApp(world);
   await openTour(app, title);
   return { app, world, transport, seed, panel: await filesPanel() };
}

async function openVisited(): Promise<{ app: LotsenApp; world: World; transport: Transport; seed: Seed; tree: ReturnType<typeof within> }> {
   const { panel, ...opened } = await openTourFiles();
   fireEvent.click(panel.getByRole('button', { name: 'Repository' }));
   return { ...opened, tree: within(await panel.findByRole('tree', { name: 'Repository files' })) };
}

const operations = (transport: Transport) => transport.requests.map((request) => request.operation);
const labels = (tree: ReturnType<typeof within>) => tree.getAllByRole('treeitem').map((row: HTMLElement) => row.getAttribute('aria-label'));
const marker = (row: HTMLElement) => row.querySelector(':scope > .row > .visits')?.textContent ?? null;
const main = () => screen.getByRole('main', { name: 'File' });

async function expand(tree: ReturnType<typeof within>, name: string): Promise<void> {
   fireEvent.click(within(await tree.findByRole('treeitem', { name })).getByText(name.split(',')[0]!));
   await waitFor(() => expect(tree.getByRole('treeitem', { name }).getAttribute('aria-expanded')).toBe('true'));
}

describe("a repository tour's Files panel", () => {
   it('opens on the Files panel in the Visited view, which lists the files the stops visit at the head, with no side, review count or repository listing', async () => {
      const { app, transport, panel } = await openTourFiles();

      const tree = within(await panel.findByRole('tree', { name: 'Visited files' }));
      expect(labels(tree)).toEqual(['src', 'lib', 'money.ts, Visited by 1 stop', 'cart.ts, Visited by 2 stops']);
      expect(screen.getByRole('tab', { name: 'Files' }).getAttribute('aria-selected')).toBe('true');
      expect(panel.getByRole('button', { name: 'Visited' }).getAttribute('aria-pressed')).toBe('true');
      for (const name of ['Changed', 'Head', 'Base', 'Show reasons']) {
         expect(panel.queryByRole('button', { name }), name).toBeNull();
      }
      expect(panel.queryByText(/reviewed/)).toBeNull();
      expect(operations(transport)).not.toContain('list_directory');
      expect(operations(transport)).not.toContain('list_changed_files');

      fireEvent.click(tree.getByText('cart.ts'));

      await waitFor(() => expect(app.selectedText).toMatchObject({ side: 'head', path: 'src/cart.ts' }));
      await waitFor(() => expect(currentRows(panel.getByRole('tree', { name: 'Visited files' }))).toEqual(['cart.ts, Visited by 2 stops']));
   });

   it('shows the repository view at the head when the reviewer opens it, with no side or changed-file list', async () => {
      const { transport, seed, tree } = await openVisited();
      const panel = await filesPanel();

      await waitFor(() => expect(labels(tree)).toEqual(['docs', 'src, Visited by 2 stops', 'README.md']));
      for (const name of ['Changed', 'Head', 'Base', 'Tree', 'Group by stop']) {
         expect(panel.queryByRole('button', { name }), name).toBeNull();
      }
      expect(panel.queryByText(/reviewed/)).toBeNull();
      expect(panel.queryByRole('tree', { name: 'Changed files' })).toBeNull();
      expect(transport.requests.filter((request) => request.operation === 'list_directory').map((request) => request.input)).toEqual([{ tourId: seed.tourId, side: 'head' }]);
      expect(operations(transport)).not.toContain('list_changed_files');
   });

   it('marks each file and folder that a stop visits with the number of stops, in its label and its marker, before the folder lists', async () => {
      const { tree } = await openVisited();

      const src = await tree.findByRole('treeitem', { name: 'src, Visited by 2 stops' });
      expect(marker(src)).toBe('2');
      expect(marker(tree.getByRole('treeitem', { name: 'docs' }))).toBeNull();
      await expand(tree, 'src, Visited by 2 stops');

      const lib = await tree.findByRole('treeitem', { name: 'lib, Visited by 1 stop' });
      expect(marker(lib)).toBe('1');
      const cartRow = tree.getByRole('treeitem', { name: 'cart.ts, Visited by 2 stops' });
      expect(marker(cartRow)).toBe('2');
   });

   it('counts a stop that Claude writes while the tour is open', async () => {
      const { world, seed, tree } = await openVisited();
      const claude = await attachClaude(world, seed.tourId);

      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds[2], headline: 'The guide.', passages: [passage('docs/guide.md', 1)] });

      await waitFor(() => expect(labels(tree)).toEqual(['docs, Visited by 1 stop', 'src, Visited by 2 stops', 'README.md']));
   });

   it("opens a stop's passage as the file's text at the snapshot's head, decorated, without a diff or a changed-file list", async () => {
      const { app, transport, seed, tree } = await openVisited();

      await openStop(app, seed.stopIds[0]!);

      await waitFor(() => expect(main().querySelector('.cm-line.lotsen-passage-primary')?.textContent).toContain('return lines.reduce'));
      expect(app.selectedText).toMatchObject({ state: 'loaded', side: 'head', path: 'src/cart.ts', text: { commit: seed.head } });
      expect(operations(transport)).not.toContain('diff_file');
      expect(operations(transport)).not.toContain('list_changed_files');
      expect(currentRows((await filesPanel()).getByRole('tree', { name: 'Repository files' }))).toEqual(['src, Visited by 2 stops']);
      await expand(tree, 'src, Visited by 2 stops');
      await waitFor(() => expect(currentRows(tree.getByRole('treeitem', { name: 'src, Visited by 2 stops' }))).toEqual(['cart.ts, Visited by 2 stops']));
   });

   it("opens a file from the tree as its text at the head, and names the center's empty state for a repository", async () => {
      const { app, tree } = await openVisited();

      expect(screen.getByText('Select a file to see its text.')).toBeTruthy();
      fireEvent.click(await tree.findByText('README.md'));

      await waitFor(() => expect(main().querySelector('.cm-editor')?.textContent).toContain('# Shop'));
      expect(app.selectedText).toMatchObject({ side: 'head', path: 'README.md' });
   });

   it("stacks a stop's files as texts in stop files, with no diff options, and names the first view mode for a repository", async () => {
      const { app, seed } = await openVisited();

      expect(within(modeSwitch().getAllByRole('button')[0]!).getByText('Whole repo')).toBeTruthy();
      fireEvent.click(modeSwitch().getByRole('button', { name: 'Stop files' }));
      await openStop(app, seed.stopIds[1]!);

      await waitFor(() => expect(stackItems().map((item) => item.querySelector('header code')?.textContent)).toEqual(['src/lib/money.ts', 'src/cart.ts']));
      await waitFor(() => expect(stackItem('src/lib/money.ts')!.querySelector('.cm-line.lotsen-passage-primary')?.textContent).toContain('Math.round'));
      expect(stackItem('src/cart.ts')!.querySelector('.cm-mergeView')).toBeNull();
      expect(screen.queryByRole('button', { name: 'Side by side' })).toBeNull();
      expect(screen.queryByRole('button', { name: 'Ignore whitespace' })).toBeNull();
   });

   it("marks a thread asked about the snapshot's code in the file's text", async () => {
      const { world, seed } = await worldOf(visited);
      await attachClaude(world, seed.tourId);
      await world.reviewer.call('ask_question', { tourId: seed.tourId, text: 'Why reduce?', anchor: { kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 2 } } as never);
      const { app } = await startApp(world);
      await openTour(app, title);

      await openStop(app, seed.stopIds[0]!);

      await waitFor(() => expect(main().querySelector('.lotsen-threadMarkers [data-thread-id]')).not.toBeNull());
   });
});
