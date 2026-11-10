import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { Template, World } from '../backend/backend';
import { attachClaude, backend, openTour, prLabel, prUrl, seedTour, startApp, updateIteration } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { currentRows } from '../treeRows';
import type { Tour } from '../../src/lib/types';
import { filesPanel, planPanel, resetPageEachTest, tab, worldOf } from './appPanels';

resetPageEachTest();

const listRequests = (requests: readonly { operation: string | null }[], operation = 'list_changed_files') => requests.filter((request) => request.operation === operation).length;

/** Creates a tour whose pin waits on the held pull request, so it stays in progress until the test releases the pull request. */
async function pinningTour(world: World, head: Record<string, string> = { 'src/index.ts': 'export const index = 2;\n' }): Promise<string> {
   const base = await world.repo.commit('main', 'The base', { 'src/index.ts': 'export const index = 1;\n' });
   const commit = await world.repo.commit('feature', 'The change', head, base);
   await world.pullRequest.set(commit, base);
   await world.pullRequest.hold();
   const claude = await world.claude();
   const tour = (await claude.call('create_tour', { prUrl, familiarity: 'some' })) as Tour;
   await claude.drop();
   return tour.id;
}

/** Releases the held pull request, and waits until the pin is done. */
async function finishPin(world: World): Promise<void> {
   await world.pullRequest.release();
   await world.idle();
}

/** Creates a second tour on the pull request of the number, pinned to the commits. */
async function addTour(world: World, number: number, head: string, base: string): Promise<string> {
   await world.pullRequest.set(head, base);
   const claude = await world.claude();
   const tour = (await claude.call('create_tour', { prUrl: `https://platform.test/shop/pull/${number}`, familiarity: 'some' })) as Tour;
   await world.idle();
   await claude.drop();
   return tour.id;
}

/** Moves the pull request to a new head, and has the reviewer take the iteration, which pins the tour to it once a session holds the tour to hear of the update. */
async function takeIteration(world: World, tourId: string, base: string, head: string): Promise<void> {
   await attachClaude(world, tourId);
   await world.pullRequest.set(head, base, { iteration: 2 });
   await world.reviewer.call('check_iteration', { tourId });
   await world.idle();
   await updateIteration(world, tourId);
   await world.idle();
}

/** Returns a world copied from the template, with the template's pull request set again, since a copy's platform has not answered for it yet and would keep each iteration check waiting. */
async function pinnedWorld<T extends { base: string; head: string }>(template: Template<T>): Promise<{ world: World; seed: T }> {
   const { world, seed } = await worldOf(template);
   await world.pullRequest.set(seed.head, seed.base);
   return { world, seed };
}

const tree = backend().template('changedFiles-tree', async (world) => {
   const seeded = await seedTour(world, {
      base: { 'src/app/old.ts': 'export const old = 1;\n', 'README.md': '# Shop\n' },
      head: { 'src/app/main.ts': 'export const main = 1;\n', 'src/app/old.ts': null, 'README.md': '# Shop\n\nSells things.\n' },
      bound: false,
   });
   return { tourId: seeded.tourId, base: seeded.base, head: seeded.head };
});

const twoFiles = backend().template('changedFiles-two', async (world) => {
   const seeded = await seedTour(world, {
      base: { 'src/index.ts': 'export const index = 1;\n', 'src/a.ts': 'export const a = 1;\n', 'README.md': '# Shop\n' },
      head: { 'src/index.ts': 'export const index = 2;\n', 'src/a.ts': 'export const a = 2;\n', 'src/b.ts': 'export const b = 1;\n', 'README.md': '# Shop\n\nSells things.\n' },
      bound: false,
   });
   return { tourId: seeded.tourId, base: seeded.base, head: seeded.head };
});

const unchanged = backend().template('changedFiles-unchanged', async (world) => {
   const base = await world.repo.commit('main', 'The base', { 'src/a.ts': 'export const a = 1;\n' });
   const head = await world.repo.commit('feature', 'An empty change', {}, base);
   await world.pullRequest.set(head, base);
   const claude = await world.claude();
   const tour = (await claude.call('create_tour', { prUrl, familiarity: 'some' })) as Tour;
   await world.idle();
   await claude.drop();
   return { tourId: tour.id, base, head };
});

describe('the changed files', () => {
   it('shows the Plan tab first and the pin in progress on the Files tab while the tour is pinning', async () => {
      const world = await backend().world();
      await pinningTour(world);
      const { app, transport } = await startApp(world);
      await openTour(app);
      await planPanel().findByText(/has not written the plan/);

      expect(tab('Plan').getAttribute('aria-selected')).toBe('true');
      tab('Files').click();

      expect(await (await filesPanel()).findByText(/Pinning the pull request/)).toBeTruthy();
      expect(listRequests(transport.requests)).toBe(0);
   });

   it('shows why the pin failed', async () => {
      const world = await backend().world();
      const base = await world.repo.commit('main', 'The base', { 'a.ts': '1\n' });
      await world.repo.commit('feature', 'The change', { 'a.ts': '2\n' }, base);
      await world.pullRequest.fail('The pull request was abandoned.');
      const claude = await world.claude();
      await claude.call('create_tour', { prUrl, familiarity: 'some' });
      await world.idle();
      const { app } = await startApp(world);
      await openTour(app);
      tab('Files').click();

      const alert = await (await filesPanel()).findByRole('alert');
      expect(alert.textContent).toContain('could not be pinned');
      expect(alert.textContent).toContain('The pull request was abandoned.');
   });

   it('opens a pinned tour on the Files tab with its changed files as a tree that shows each change kind', async () => {
      const { world } = await pinnedWorld(tree);
      const { app } = await startApp(world);
      await openTour(app);

      const files = await (await filesPanel()).findByRole('tree', { name: 'Changed files' });
      expect(tab('Files').getAttribute('aria-selected')).toBe('true');
      const items = within(files).getAllByRole('treeitem');
      expect(items.map((item) => item.getAttribute('aria-label'))).toEqual(['src/app', 'main.ts, Added', 'old.ts, Deleted', 'README.md, Modified']);
      expect(items[0]!.getAttribute('aria-expanded')).toBe('true');
      expect(within(items[0]!).getByRole('group')).toBeTruthy();
   });

   it('marks the file of a passage opened before the changed files arrive, once they do', async () => {
      const { world } = await pinnedWorld(twoFiles);
      const { app, transport } = await startApp(world);
      const release = transport.hold('list_changed_files');
      await openTour(app);
      await waitFor(() => expect(listRequests(transport.requests)).toBe(1));

      app.openPassage({ side: 'head', path: 'README.md', startLine: 1, endLine: 1 });
      release();

      await (await filesPanel()).findByRole('treeitem', { name: 'README.md, Modified' });
      expect(currentRows(screen.getByRole('tabpanel', { name: 'Files' }))).toEqual(['README.md, Modified']);
   });

   it('says so when the pull request changes no files', async () => {
      const { world } = await pinnedWorld(unchanged);
      const { app } = await startApp(world);
      await openTour(app);

      expect(await (await filesPanel()).findByText(/changes no files/)).toBeTruthy();
   });

   it('loads the changed files from a tourPinned event without a reload, and turns to the Files tab', async () => {
      const world = await backend().world();
      await pinningTour(world);
      const { app, transport } = await startApp(world);
      await openTour(app);
      await planPanel().findByText(/has not written the plan/);
      const listLoads = listRequests(transport.requests, 'list_tours');
      const tourLoads = listRequests(transport.requests, 'read_state');

      await finishPin(world);

      expect(await (await filesPanel()).findByRole('treeitem', { name: 'index.ts, Modified' })).toBeTruthy();
      expect(tab('Files').getAttribute('aria-selected')).toBe('true');
      expect(listRequests(transport.requests, 'list_tours')).toBe(listLoads);
      expect(listRequests(transport.requests, 'read_state')).toBe(tourLoads);
   });

   it('keeps the tab the user chose when the tour is pinned', async () => {
      const world = await backend().world();
      await pinningTour(world);
      const { app, transport } = await startApp(world);
      await openTour(app);
      tab('Plan').click();

      await finishPin(world);
      await waitFor(() => expect(listRequests(transport.requests)).toBe(1));

      expect(tab('Plan').getAttribute('aria-selected')).toBe('true');
   });

   it.each(['Enter', ' '])('keeps the tab the user chose with the key %j when the tour is pinned', async (key) => {
      const world = await backend().world();
      await pinningTour(world);
      const { app, transport } = await startApp(world);
      await openTour(app);
      await fireEvent.keyDown(tab('Plan'), { key });

      await finishPin(world);
      await waitFor(() => expect(listRequests(transport.requests)).toBe(1));

      expect(tab('Plan').getAttribute('aria-selected')).toBe('true');
   });

   it('tells the user that the changed files could not be loaded when the load fails', async () => {
      const { world } = await pinnedWorld(twoFiles);
      const { app, transport } = await startApp(world);
      transport.fail('list_changed_files', { status: 502 });
      await openTour(app);

      const alert = await (await filesPanel()).findByRole('alert');
      expect(alert.textContent).toContain('The changed files could not be loaded.');
   });

   it('loads the list again when the tour is selected again after a failure', async () => {
      const { world } = await pinnedWorld(twoFiles);
      const { app, transport } = await startApp(world);
      transport.fail('list_changed_files', { status: 500 });
      await openTour(app);
      await (await filesPanel()).findByRole('alert');

      (await openTourList()).getByRole('button', tourButton).click();

      expect(await (await filesPanel()).findByRole('treeitem', { name: 'b.ts, Added' })).toBeTruthy();
      expect((await filesPanel()).queryByRole('alert')).toBeNull();
   });

   describe('when loading a loaded list again fails', () => {
      it('keeps the list and the selected file when the load after a reconnect fails', async () => {
         const { world } = await pinnedWorld(twoFiles);
         const { app, transport } = await startApp(world);
         await openTour(app);
         fireEvent.click(await (await filesPanel()).findByText('b.ts'));
         const loads = listRequests(transport.requests);
         transport.fail('list_changed_files', { status: 502 });
         // The reload of the plan starts after the list's, whose fault answers at once, so the plan's answer comes after the page handled the fault.
         const planReloaded = transport.answered('read_state');

         transport.cutEventStreams();
         await waitFor(() => expect(listRequests(transport.requests)).toBe(loads + 1));
         await planReloaded;

         expect((await filesPanel()).getByRole('treeitem', { name: 'b.ts, Added' })).toBeTruthy();
         expect((await filesPanel()).queryByRole('alert')).toBeNull();
         expect(app.selectedFile).toMatchObject({ path: 'src/b.ts', kind: 'added' });
      });

      it('drops the list when the tour no longer exists', async () => {
         const { world, seed } = await pinnedWorld(twoFiles);
         // A long retry delay keeps the page from reconnecting, so it learns of the deletion only from the list's refusal.
         const { app, transport } = await startApp(world, { retryDelaysMs: [60_000] });
         await openTour(app);
         fireEvent.click(await (await filesPanel()).findByText('b.ts'));
         transport.cutEventStreams();
         await waitFor(() => expect(app.connection).toBe('reconnecting'));
         await world.reviewer.call('delete_tour', { tourId: seed.tourId });

         (await openTourList()).getByRole('button', tourButton).click();

         expect((await (await filesPanel()).findByRole('alert')).textContent).toContain('This tour no longer exists.');
         expect((await filesPanel()).queryByRole('tree')).toBeNull();
      });
   });

   it('keeps a newer list over a slower response to an earlier request', async () => {
      const { world, seed } = await pinnedWorld(twoFiles);
      const newHead = await world.repo.commit('feature', 'Iteration 2', { 'src/c.ts': 'export const c = 1;\n' }, seed.base);
      const { app, transport } = await startApp(world);
      const release = transport.hold('list_changed_files');
      await openTour(app);
      await waitFor(() => expect(listRequests(transport.requests)).toBe(1));

      await takeIteration(world, seed.tourId, seed.base, newHead);
      await (await filesPanel()).findByRole('treeitem', { name: 'c.ts, Added' });
      const answered = transport.answered('list_changed_files');
      release();
      await answered;

      expect((await filesPanel()).getByRole('treeitem', { name: 'c.ts, Added' })).toBeTruthy();
      expect((await filesPanel()).queryByRole('treeitem', { name: 'b.ts, Added' })).toBeNull();
   });

   it('selects a clicked file, highlights it and names it in the main area, across a switch to the Plan tab', async () => {
      const { world } = await pinnedWorld(twoFiles);
      const { app } = await startApp(world);
      await openTour(app);
      const file = await (await filesPanel()).findByRole('treeitem', { name: 'b.ts, Added' });

      fireEvent.click(within(file).getByText('b.ts'));

      expect(app.selectedFilePath).toBe('src/b.ts');
      expect(app.selectedFile).toMatchObject({ path: 'src/b.ts', kind: 'added' });
      await waitFor(() => expect(file.getAttribute('aria-selected')).toBe('true'));
      expect((await filesPanel()).getByRole('treeitem', { name: 'a.ts, Modified' }).getAttribute('aria-selected')).toBe('false');
      expect(screen.getByRole('main', { name: 'Diff' }).textContent).toContain('src/b.ts');

      tab('Plan').click();
      await waitFor(() => expect(tab('Plan').getAttribute('aria-selected')).toBe('true'));
      tab('Files').click();

      expect((await (await filesPanel()).findByRole('treeitem', { name: 'b.ts, Added' })).getAttribute('aria-selected')).toBe('true');
   });

   it('clears the selected file when another tour is selected', async () => {
      const { world, seed } = await pinnedWorld(twoFiles);
      const empty = await world.repo.commit('feature-2', 'An empty change', {}, seed.base);
      await addTour(world, 9, empty, seed.base);
      const { app } = await startApp(world);
      await openTour(app, prLabel);
      fireEvent.click(await (await filesPanel()).findByText('b.ts'));
      expect(app.selectedFilePath).toBe('src/b.ts');

      await openTour(app, 'shop #9');

      expect(app.selectedFilePath).toBeNull();
      expect(await (await filesPanel()).findByText(/changes no files/)).toBeTruthy();
   });
});
