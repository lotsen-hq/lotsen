import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { Transport, World } from '../backend/backend';
import { attachClaude, backend, prUrl, seedTour, startApp, openTour } from '../harness';
import { openTourList } from '../tourPicker';
import { filesPanel, resetPageEachTest, worldOf } from './appPanels';

resetPageEachTest();

const diffArea = () => within(screen.getByRole('main', { name: 'Diff' }));
const main = () => screen.getByRole('main', { name: 'Diff' });

/** Returns how many diff_file calls the page sent with exactly the input, whose values are all strings. */
function diffRequests(transport: Transport, input: Record<string, unknown>): number {
   const key = (value: Record<string, unknown> | null) => JSON.stringify(Object.entries(value ?? {}).sort());
   return transport.requests.filter((request) => request.operation === 'diff_file' && key(request.input) === key(input)).length;
}

function diffInputs(transport: Transport): (Record<string, unknown> | null)[] {
   return transport.requests.filter((request) => request.operation === 'diff_file').map((request) => request.input);
}

// One line of 1 KiB, so 1536 of them make a file of 1.5 MiB, over the server's large-file size of 1 MiB.
const largeLine = `generated line ${'x'.repeat(1024 - 'generated line '.length - 1)}\n`;
const largeText = largeLine.repeat(1536);

// A pull request with a file of each kind that the diff shows differently.
const kinds = backend().template('fileDiff-kinds', async (world) => {
   const seeded = await seedTour(world, {
      base: {
         'src/a.ts': 'export const a = 1;\n',
         'src/slow.ts': 'slow base\n',
         'src/fast.ts': 'fast base\n',
         'src/old.ts': 'one\r\ntwo\r\nthree\r\n',
         'src/indent.ts': 'a\n',
         'logo.png': 'PNG\u0000base',
         'crlf.ts': 'one\ntwo\n',
      },
      head: {
         'src/a.ts': 'export const a = 2;\n',
         'src/slow.ts': 'slow head\n',
         'src/fast.ts': 'fast head\n',
         'src/old.ts': null,
         'lib/new.ts': 'one\r\nTWO\r\nthree\r\n',
         'src/indent.ts': '  a\n',
         'logo.png': 'PNG\u0000head',
         'crlf.ts': 'one\r\ntwo\r\n',
         'data.json': largeText,
      },
      bound: false,
   });
   return { tourId: seeded.tourId };
});

async function kindsWorld(): Promise<{ world: World; tourId: string }> {
   const { world, seed } = await worldOf(kinds);
   return { world, tourId: seed.tourId };
}

async function clickFile(world: World, name: string): Promise<Transport> {
   const { app, transport } = await startApp(world);
   await openTour(app);
   fireEvent.click(await (await filesPanel()).findByText(name));
   return transport;
}

describe('the diff of the selected file', () => {
   it('loads and shows the diff of a clicked file, once', async () => {
      const { world, tourId } = await kindsWorld();
      const transport = await clickFile(world, 'a.ts');

      await waitFor(() => expect(main().textContent).toContain('export const a = 2;'));
      expect(main().textContent).toContain('export const a = 1;');

      fireEvent.click((await filesPanel()).getByText('a.ts'));

      expect(diffRequests(transport, { tourId, path: 'src/a.ts' })).toBe(1);
   });

   it('shows the newer file when the diff of an earlier one answers late', async () => {
      const { world, tourId } = await kindsWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      const release = transport.hold('diff_file', 'response');
      const slowAnswered = transport.answered('diff_file');
      fireEvent.click(await (await filesPanel()).findByText('slow.ts'));
      await waitFor(() => expect(diffRequests(transport, { tourId, path: 'src/slow.ts' })).toBe(1));

      fireEvent.click((await filesPanel()).getByText('fast.ts'));
      await waitFor(() => expect(main().textContent).toContain('fast head'));
      release();
      await slowAnswered;

      await waitFor(() => expect(main().querySelectorAll('.cm-mergeView')).toHaveLength(1));
      expect(main().textContent).not.toContain('slow');
      expect(main().textContent).toContain('fast head');
   });

   /** The texts have CRLF line breaks, so the server's offsets count each break as two code units. */
   it('shows a renamed file with its old path, and opens its diff across the rename', async () => {
      const { world, tourId } = await kindsWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      const file = await (await filesPanel()).findByRole('treeitem', { name: 'new.ts, Renamed from src/old.ts' });

      fireEvent.click(within(file).getByText('new.ts'));

      await waitFor(() => expect(main().querySelectorAll('.cm-changedLine')).toHaveLength(2));
      expect(main().textContent).toContain('src/old.ts → lib/new.ts');
      expect([...main().querySelectorAll('.cm-changedLine')].map((line) => line.textContent)).toEqual(['two', 'TWO']);
      expect(diffInputs(transport)).toContainEqual({ tourId, path: 'lib/new.ts', oldPath: 'src/old.ts' });
   });

   it('loads the diff of a renamed file that the reviewer opens before the list arrives again across the rename once the list names it', async () => {
      const { world, tourId } = await kindsWorld();
      const { app, transport } = await startApp(world);
      const release = transport.hold('list_changed_files', 'response');
      await openTour(app);
      app.selectFile('lib/new.ts');
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));

      release();

      await waitFor(() =>
         expect(diffInputs(transport)).toEqual([
            { tourId, path: 'lib/new.ts' },
            { tourId, path: 'lib/new.ts', oldPath: 'src/old.ts' },
         ]),
      );
      await waitFor(() => expect(main().textContent).toContain('src/old.ts → lib/new.ts'));
   });

   it('shows "binary changed" for a binary file, without an editor', async () => {
      const { world } = await kindsWorld();
      await clickFile(world, 'logo.png');

      await waitFor(() => expect(diffArea().getByText(/binary changed/i)).toBeTruthy());
      expect(main().querySelector('.cm-editor')).toBeNull();
   });

   it('shows a large file collapsed with its size, and loads its diff only after the user asks', async () => {
      const { world, tourId } = await kindsWorld();
      const transport = await clickFile(world, 'data.json');

      const load = await diffArea().findByRole('button', { name: 'Load the diff' });
      expect(main().textContent).toContain('1.5 MiB');
      expect(main().querySelector('.cm-editor')).toBeNull();
      expect(diffInputs(transport)).toEqual([{ tourId, path: 'data.json' }]);

      fireEvent.click(load);

      await waitFor(() => expect(main().textContent).toContain('generated line'), { timeout: 10_000 });
      expect(diffInputs(transport).at(-1)).toEqual({ tourId, path: 'data.json', large: 'include' });
      expect(diffArea().queryByRole('button', { name: 'Load the diff' })).toBeNull();
   });

   it('shows a file whose only change is its line endings tagged and collapsed, and opens its diff when asked', async () => {
      const { world } = await kindsWorld();
      const { app } = await startApp(world);
      await openTour(app);
      const file = await (await filesPanel()).findByRole('treeitem', { name: 'crlf.ts, Modified, line endings only' });

      fireEvent.click(within(file).getByText('crlf.ts'));

      const show = await diffArea().findByRole('button', { name: 'Show the diff' });
      expect(main().textContent).toContain('Only the line endings changed');
      expect(main().querySelector('.cm-editor')).toBeNull();

      fireEvent.click(show);

      await waitFor(() => expect(main().querySelectorAll('.cm-changedLine')).toHaveLength(4));
      expect(diffArea().queryByRole('button', { name: 'Show the diff' })).toBeNull();
   });

   // A transport fault stands in for a repository that git cannot reach. The server's reason under the message is a rendering case in repositoryAndDiffStates.test.ts.
   it('tells the user that the diff could not be loaded, and loads it when the file is clicked again', async () => {
      const { world, tourId } = await kindsWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      transport.fail('diff_file', { status: 502 });
      fireEvent.click(await (await filesPanel()).findByText('a.ts'));

      const alert = await diffArea().findByRole('alert');
      expect(alert.textContent).toContain('The diff could not be loaded.');

      fireEvent.click((await filesPanel()).getByText('a.ts'));

      await waitFor(() => expect(main().textContent).toContain('export const a = 2;'));
      expect(diffArea().queryByRole('alert')).toBeNull();
      expect(diffRequests(transport, { tourId, path: 'src/a.ts' })).toBe(2);
   });

   describe('with the ignore-whitespace toggle', () => {
      const whitespaceKey = 'lotsen.ignoreWhitespace';

      async function editorShows(changedLines: number) {
         await waitFor(
            () => {
               expect(main().querySelector('.cm-editor')).not.toBeNull();
               expect(main().querySelectorAll('.cm-changedLine')).toHaveLength(changedLines);
            },
            { timeout: 10_000 },
         );
      }

      it('loads the diff again with whitespace ignored, and keeps the choice in browser storage', async () => {
         const { world, tourId } = await kindsWorld();
         const transport = await clickFile(world, 'indent.ts');
         await editorShows(2);
         const toggle = diffArea().getByRole('button', { name: 'Ignore whitespace' });
         expect(toggle.getAttribute('aria-pressed')).toBe('false');

         fireEvent.click(toggle);

         await editorShows(0);
         expect(diffInputs(transport).at(-1)).toEqual({ tourId, path: 'src/indent.ts', whitespace: 'ignore' });
         expect(toggle.getAttribute('aria-pressed')).toBe('true');
         expect(localStorage.getItem(whitespaceKey)).toBe('true');

         fireEvent.click(toggle);

         await editorShows(2);
         expect(diffRequests(transport, { tourId, path: 'src/indent.ts' })).toBe(2);
         expect(localStorage.getItem(whitespaceKey)).toBe('false');
      });

      it('loads the first diff with whitespace ignored when the reviewer chose it before', async () => {
         localStorage.setItem(whitespaceKey, 'true');
         const { world, tourId } = await kindsWorld();
         const transport = await clickFile(world, 'indent.ts');

         await editorShows(0);
         expect(diffArea().getByRole('button', { name: 'Ignore whitespace' }).getAttribute('aria-pressed')).toBe('true');
         expect(diffRequests(transport, { tourId, path: 'src/indent.ts' })).toBe(0);
      });

      it('keeps the texts of a large file that the user loaded', async () => {
         const { world, tourId } = await kindsWorld();
         const transport = await clickFile(world, 'data.json');
         fireEvent.click(await diffArea().findByRole('button', { name: 'Load the diff' }));
         await waitFor(() => expect(main().textContent).toContain('generated line'), { timeout: 10_000 });

         fireEvent.click(diffArea().getByRole('button', { name: 'Ignore whitespace' }));

         await waitFor(() => expect(diffInputs(transport).at(-1)).toEqual({ tourId, path: 'data.json', large: 'include', whitespace: 'ignore' }));
         await waitFor(() => expect(main().textContent).toContain('generated line'), { timeout: 10_000 });
         expect(diffArea().queryByRole('button', { name: 'Load the diff' })).toBeNull();
      });
   });
});

type FileMark = { path: string; mark: 'generated' | 'mechanical'; reason: string };

const generated: FileMark = { path: 'src/client.g.ts', mark: 'generated', reason: 'NSwag writes it from the gateway API description.' };
const mechanical: FileMark = { path: 'src/app.ts', mark: 'mechanical', reason: 'Only the import path changed.' };

const markedSpec = {
   base: { 'src/client.g.ts': 'export type A = 1;\n', 'src/app.ts': "import './a';\n" },
   head: { 'src/client.g.ts': 'export type A = 2;\n', 'src/app.ts': "import './b';\n" },
   chapters: [{ title: 'Chapter', stops: [{ title: 'A stop', kind: 'inspect' as const }] }],
   bound: false,
};

// A tour with one pending stop, whose plan Claude sets again with the marks that a test needs.
const marked = backend().template('fileDiff-marked', async (world) => {
   const seeded = await seedTour(world, markedSpec);
   return { tourId: seeded.tourId, stopId: seeded.stopIds[0]! };
});

// The marked tour, and a second tour, shop #43, on a pull request whose one commit changes no files.
const markedAndEmpty = backend().template('fileDiff-markedAndEmpty', async (world) => {
   const seeded = await seedTour(world, markedSpec);
   await world.pullRequest.set(await world.repo.commit('empty', 'No change', {}, seeded.base), seeded.base);
   const second = await world.claude();
   await second.call('create_tour', { prUrl: prUrl.replace('/42', '/43'), familiarity: 'some' });
   await world.idle();
   await second.drop();
   return { tourId: seeded.tourId, stopId: seeded.stopIds[0]! };
});

describe('the diff of a file that Claude marked', () => {
   const fileRow = async (name: RegExp) => (await filesPanel()).findByRole('treeitem', { name });

   async function markedWorld(fileMarks: FileMark[], template = marked) {
      const { world, seed } = await worldOf(template);
      const claude = await attachClaude(world, seed.tourId);
      const setMarks = (marks: FileMark[]) =>
         claude.call('set_plan', { tourId: seed.tourId, chapters: [{ id: 'c1', title: 'Chapter' }], stops: [{ id: seed.stopId, title: 'A stop', kind: 'inspect', chapter: 'c1' }], fileMarks: marks });
      await setMarks(fileMarks);
      return { world, ...seed, setMarks };
   }

   it('lists the next tour’s files when the reviewer switches tours with a marked file open, whose path and mark show tooltips', async () => {
      const { world } = await markedWorld([generated], markedAndEmpty);
      const { app } = await startApp(world);
      await openTour(app, 'shop #42');
      fireEvent.click(within(await fileRow(/^client\.g\.ts/)).getByText('client.g.ts'));
      await waitFor(() => expect(main().textContent).toContain('Claude marked this file as generated'));

      (await openTourList()).getByRole('button', { name: /#43/ }).click();

      expect(await (await filesPanel()).findByText(/changes no files/)).toBeTruthy();
   });

   it('lists the file in place, tagged with its mark and showing its reason once asked, and shows its diff collapsed until the reviewer expands it', async () => {
      const { world } = await markedWorld([generated, mechanical]);
      const { app } = await startApp(world);
      await openTour(app);

      const row = await fileRow(/^client\.g\.ts, Modified, generated: NSwag writes it/);
      expect(within(row).getByText('generated')).toBeTruthy();
      expect(within(row).queryByText(generated.reason)).toBeNull();
      fireEvent.click((await filesPanel()).getByRole('button', { name: 'Show reasons' }));
      expect(await within(row).findByText(generated.reason)).toBeTruthy();
      const appRow = await fileRow(/^app\.ts, Modified, mechanical: Only the import path changed\.$/);
      expect(within(appRow).getByText('mechanical')).toBeTruthy();
      expect([...(await filesPanel()).getAllByRole('treeitem')].map((item) => item.getAttribute('data-id'))).toEqual(['src/', 'src/app.ts', 'src/client.g.ts']);

      fireEvent.click(within(row).getByText('client.g.ts'));

      const show = await diffArea().findByRole('button', { name: 'Show the diff' });
      expect(main().textContent).toContain('Claude marked this file as generated');
      expect(main().textContent).toContain(generated.reason);
      expect(main().querySelector('.cm-editor')).toBeNull();

      fireEvent.click(show);

      await waitFor(() => expect(main().textContent).toContain('export type A = 2;'));
      expect(diffArea().queryByRole('button', { name: 'Show the diff' })).toBeNull();
      expect(within(main()).getByText('generated')).toBeTruthy();

      fireEvent.click(within(await fileRow(/^app\.ts/)).getByText('app.ts'));

      expect(await diffArea().findByRole('button', { name: 'Show the diff' })).toBeTruthy();
      expect(main().textContent).toContain('Claude marked this file as mechanical');
   });

   it('collapses a file whose mark a planSet adds when the file opens next, and expands an open file whose mark a planSet removes', async () => {
      const { world, setMarks } = await markedWorld([]);
      const { app } = await startApp(world);
      await openTour(app);
      fireEvent.click(within(await fileRow(/^client\.g\.ts, Modified$/)).getByText('client.g.ts'));
      await waitFor(() => expect(main().textContent).toContain('export type A = 2;'));

      await setMarks([generated]);

      // The open file keeps its diff, so the mark does not take the code from under the reviewer.
      await fileRow(/^client\.g\.ts, Modified, generated/);
      await waitFor(() => expect(within(main()).queryByText('generated')).not.toBeNull());
      expect(main().querySelector('.cm-editor')).not.toBeNull();
      fireEvent.click(within(await fileRow(/^app\.ts/)).getByText('app.ts'));
      await waitFor(() => expect(main().textContent).toContain("import './b';"));
      fireEvent.click(within(await fileRow(/^client\.g\.ts/)).getByText('client.g.ts'));
      expect(await diffArea().findByRole('button', { name: 'Show the diff' })).toBeTruthy();

      await setMarks([]);

      await waitFor(() => expect(main().textContent).toContain('export type A = 2;'));
      expect(diffArea().queryByRole('button', { name: 'Show the diff' })).toBeNull();
      expect(await fileRow(/^client\.g\.ts, Modified$/)).toBeTruthy();
   });

   it('ignores a mark on a path that the pull request does not change', async () => {
      const { world } = await markedWorld([{ path: 'src/other.ts', mark: 'generated', reason: 'Not in this pull request.' }]);
      const { app } = await startApp(world);
      await openTour(app);

      fireEvent.click(within(await fileRow(/^client\.g\.ts, Modified$/)).getByText('client.g.ts'));

      await waitFor(() => expect(main().textContent).toContain('export type A = 2;'));
      expect(main().textContent).not.toContain('Not in this pull request.');
   });
});
