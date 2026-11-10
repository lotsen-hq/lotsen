import { EditorView } from '@codemirror/view';
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { Template, Transport } from '../backend/backend';
import { backend, openTour, seedTour, startApp, type TourSpec } from '../harness';
import { currentRows, rowParts } from '../treeRows';
import { filesPanel, resetPageEachTest, worldOf } from './appPanels';

resetPageEachTest();

const main = (name: 'File' | 'Diff') => screen.getByRole('main', { name });

function inputsOf(transport: Transport, operation: string): (Record<string, unknown> | null)[] {
   return transport.requests.filter((request) => request.operation === operation).map((request) => request.input);
}

function tourTemplate(name: string, spec: TourSpec): Template<{ tourId: string }> {
   return backend().template(name, async (world) => ({ tourId: (await seedTour(world, { ...spec, bound: false })).tourId }));
}

// The head holds README.md, src/app.ts and the added src/changed.ts, and the base holds src/gone.ts in place of src/changed.ts.
const app = 'export const app = 1;\n';
const repository = tourTemplate('repositoryView-repo', {
   base: { 'README.md': '# Shop\n', 'src/app.ts': app, 'src/gone.ts': 'gone\n' },
   head: { 'src/gone.ts': null, 'src/changed.ts': 'changed\n' },
});

// Git pairs the two paths as a rename, because only the last of their four lines differs.
const renamed = tourTemplate('repositoryView-rename', {
   base: { 'README.md': '# Shop\n', 'src/app.ts': app, 'src/gone.ts': 'one\ntwo\nthree\nfour\n' },
   head: { 'src/gone.ts': null, 'src/changed.ts': 'one\ntwo\nthree\nFOUR\n' },
});

// One line of 1 KiB, so 1536 of them make a file of 1.5 MiB, over the server's large-file size of 1 MiB.
const largeText = `generated ${'x'.repeat(1024 - 'generated '.length - 1)}\n`.repeat(1536);
const unchangedKinds = tourTemplate('repositoryView-unchangedKinds', {
   base: { 'big.json': largeText, 'logo.png': 'PNG\u0000data', 'src/app.ts': app },
   head: { 'src/app.ts': 'export const app = 2;\n' },
});

async function openRepositoryView(template: Template<{ tourId: string }> = repository) {
   const { world, seed } = await worldOf(template);
   const started = await startApp(world);
   await openTour(started.app);
   const panel = await filesPanel();
   fireEvent.click(await panel.findByRole('button', { name: 'Repository' }));
   const tree = within(await panel.findByRole('tree', { name: 'Repository files' }));
   return { ...started, tree, tourId: seed.tourId };
}

async function expand(tree: ReturnType<typeof within>, name: string) {
   fireEvent.click(within(await tree.findByRole('treeitem', { name })).getByText(name));
   await waitFor(() => expect(tree.getByRole('treeitem', { name }).getAttribute('aria-expanded')).toBe('true'));
}

async function switchToBase() {
   fireEvent.click((await filesPanel()).getByRole('button', { name: 'Base' }));
   return within(await (await filesPanel()).findByRole('tree', { name: 'Repository files' }));
}

describe('the whole-repository view', () => {
   it('lists the repository at the head with folders collapsed, and opens an unchanged file at its head text, read-only', async () => {
      const { app: lotsen, transport, tree, tourId } = await openRepositoryView();

      const rows = await waitFor(() => {
         const found = tree.getAllByRole('treeitem');
         expect(found.map((row) => row.getAttribute('aria-label'))).toEqual(['src', 'README.md']);
         return found;
      });
      expect(rows[0]!.getAttribute('aria-expanded')).toBe('false');
      expect(rowParts(rows[0]!)).toEqual(['chevron-right', 'folder', 'src']);
      expect(inputsOf(transport, 'list_directory')).toEqual([{ tourId, side: 'head' }]);

      await expand(tree, 'src');
      expect(rowParts(tree.getByRole('treeitem', { name: 'src' }))).toEqual(['chevron-right down', 'folder-open', 'src']);
      fireEvent.click(await tree.findByText('app.ts'));

      await waitFor(() => expect(main('File').querySelector('.cm-editor')?.textContent).toContain('export const app = 1;'));
      const view = EditorView.findFromDOM(main('File').querySelector<HTMLElement>('.cm-editor')!)!;
      expect(view.state.readOnly).toBe(true);
      expect(main('File').textContent).toContain('src/app.ts');
      expect(within(main('File')).queryByRole('button', { name: 'Side by side' })).toBeNull();
      expect(inputsOf(transport, 'read_file_text').at(-1)).toEqual({ tourId, side: 'head', path: 'src/app.ts' });
      expect(inputsOf(transport, 'list_directory')).toEqual([
         { tourId, side: 'head' },
         { tourId, side: 'head', path: 'src' },
      ]);
      expect(tree.getByRole('treeitem', { name: 'app.ts' }).getAttribute('aria-selected')).toBe('true');
      expect(lotsen.selectedFilePath).toBeNull();
      const repositoryTree = screen.getByRole('tree', { name: 'Repository files' });
      expect(currentRows(repositoryTree)).toEqual(['app.ts']);
      fireEvent.click(tree.getByText('src'));
      await waitFor(() => expect(currentRows(repositoryTree)).toEqual(['src']));
   });

   it('lists a folder that the right arrow expands, and opens a file on Enter', async () => {
      const { tree } = await openRepositoryView();
      const folder = await tree.findByRole('treeitem', { name: 'src' });
      folder.focus();

      await fireEvent.keyDown(folder, { key: 'ArrowRight' });
      await tree.findByRole('treeitem', { name: 'app.ts' });
      await fireEvent.keyDown(folder, { key: 'ArrowRight' });
      await waitFor(() => expect(document.activeElement?.getAttribute('aria-label')).toBe('app.ts'));
      await fireEvent.keyDown(document.activeElement!, { key: 'Enter' });

      await waitFor(() => expect(main('File').textContent).toContain('export const app = 1;'));
      await fireEvent.keyDown(tree.getByRole('treeitem', { name: 'app.ts' }), { key: 'ArrowLeft' });
      await fireEvent.keyDown(document.activeElement!, { key: 'ArrowLeft' });
      await waitFor(() => expect(folder.getAttribute('aria-expanded')).toBe('false'));
   });

   it('marks a changed file with its kind and opens its diff, as the changed-file tree does', async () => {
      const { app: lotsen, transport, tree } = await openRepositoryView();
      await expand(tree, 'src');

      fireEvent.click(within(await tree.findByRole('treeitem', { name: 'changed.ts, Added' })).getByText('changed.ts'));

      await waitFor(() => expect(main('Diff').querySelector('.cm-mergeView')?.textContent).toContain('changed'));
      expect(lotsen.selectedFilePath).toBe('src/changed.ts');
      expect(tree.getByRole('treeitem', { name: 'changed.ts, Added' }).getAttribute('aria-selected')).toBe('true');
      expect(rowParts(tree.getByRole('treeitem', { name: 'changed.ts, Added' }))).toEqual(['', 'seti typescript', 'changed.ts', 'A']);
      expect(inputsOf(transport, 'read_file_text')).toEqual([]);
   });

   it('switches to the base, where a deleted file opens its diff and an unchanged file its base text', async () => {
      const { app: lotsen, transport, tree, tourId } = await openRepositoryView();
      await tree.findByRole('treeitem', { name: 'README.md' });

      const baseTree = await switchToBase();

      await waitFor(() => expect(inputsOf(transport, 'list_directory').at(-1)).toEqual({ tourId, side: 'base' }));
      await waitFor(() => expect(baseTree.getAllByRole('treeitem').map((row) => row.getAttribute('aria-label'))).toEqual(['src', 'README.md']));
      await expand(baseTree, 'src');
      fireEvent.click(within(await baseTree.findByRole('treeitem', { name: 'gone.ts, Deleted' })).getByText('gone.ts'));
      await waitFor(() => expect(lotsen.selectedFilePath).toBe('src/gone.ts'));
      await waitFor(() => expect(main('Diff').querySelector('.cm-mergeView')?.textContent).toContain('gone'));

      fireEvent.click(baseTree.getByText('app.ts'));

      await waitFor(() => expect(main('File').querySelector('.cm-editor')?.textContent).toContain('export const app = 1;'));
      expect(inputsOf(transport, 'read_file_text').at(-1)).toEqual({ tourId, side: 'base', path: 'src/app.ts' });
      expect(lotsen.selectedFilePath).toBeNull();
   });

   it('opens the diff across a rename from the old path at the base', async () => {
      const { app: lotsen, tree } = await openRepositoryView(renamed);
      await tree.findByRole('treeitem', { name: 'README.md' });
      const baseTree = await switchToBase();
      await baseTree.findByRole('treeitem', { name: 'README.md' });
      await expand(baseTree, 'src');

      fireEvent.click(within(await baseTree.findByRole('treeitem', { name: 'gone.ts, Renamed' })).getByText('gone.ts'));

      await waitFor(() => expect(main('Diff').textContent).toContain('src/gone.ts → src/changed.ts'));
      expect(lotsen.selectedFilePath).toBe('src/changed.ts');
      expect(currentRows(screen.getByRole('tree', { name: 'Repository files' }))).toEqual(['gone.ts, Renamed']);
   });

   it('opens files when the changed files could not be loaded, as every file then counts as unchanged', async () => {
      const { world } = await worldOf(repository);
      const transport = world.transport();
      transport.fail('list_changed_files', { status: 502 });
      const { app: lotsen } = await startApp(world, { transport });
      await openTour(lotsen);
      const panel = await filesPanel();
      fireEvent.click(await panel.findByRole('button', { name: 'Repository' }));
      const tree = within(await panel.findByRole('tree', { name: 'Repository files' }));
      await expand(tree, 'src');

      fireEvent.click(await tree.findByText('app.ts'));

      await waitFor(() => expect(main('File').textContent).toContain('export const app = 1;'));
   });

   it('shows an unchanged binary file without its content, and a large one collapsed until the user loads it', async () => {
      const { transport, tree, tourId } = await openRepositoryView(unchangedKinds);

      fireEvent.click(await tree.findByText('logo.png'));
      await waitFor(() => expect(main('File').textContent).toMatch(/binary/i));
      expect(main('File').querySelector('.cm-editor')).toBeNull();

      fireEvent.click(tree.getByText('big.json'));
      const load = await within(main('File')).findByRole('button', { name: 'Load the file' });
      expect(main('File').textContent).toContain('1.5 MiB');
      expect(main('File').querySelector('.cm-editor')).toBeNull();

      fireEvent.click(load);

      await waitFor(() => expect(main('File').querySelector('.cm-editor')?.textContent).toContain('generated'), { timeout: 10_000 });
      expect(inputsOf(transport, 'read_file_text').at(-1)).toEqual({ tourId, side: 'head', path: 'big.json', large: 'include' });
   });

   // A transport fault stands in for a repository that git cannot reach. The server's reason under the message is a rendering case in repositoryAndDiffStates.test.ts.
   it('tells the user that a folder could not be listed, and lists it when the folder is opened again', async () => {
      const { transport, tree } = await openRepositoryView();
      await tree.findByRole('treeitem', { name: 'src' });
      transport.fail('list_directory', { status: 502 });
      await expand(tree, 'src');

      expect(await tree.findByText('The folder could not be listed.')).toBeTruthy();

      fireEvent.click(tree.getByText('src'));
      fireEvent.click(tree.getByText('src'));

      expect(await tree.findByRole('treeitem', { name: 'app.ts' })).toBeTruthy();
      expect(tree.queryByText('The folder could not be listed.')).toBeNull();
   });

   it('goes back to the diff view when a file of the changed-file tree is clicked', async () => {
      const { tree } = await openRepositoryView();
      await expand(tree, 'src');
      fireEvent.click(await tree.findByText('app.ts'));
      await waitFor(() => expect(main('File').textContent).toContain('export const app'));

      fireEvent.click((await filesPanel()).getByRole('button', { name: 'Changed' }));
      const changedTree = within(await (await filesPanel()).findByRole('tree', { name: 'Changed files' }));
      fireEvent.click(changedTree.getByText('changed.ts'));

      await waitFor(() => expect(main('Diff').querySelector('.cm-mergeView')).not.toBeNull());
      expect(screen.queryByRole('main', { name: 'File' })).toBeNull();
   });
});
