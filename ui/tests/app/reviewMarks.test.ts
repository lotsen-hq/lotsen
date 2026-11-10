import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { Transport, World } from '../backend/backend';
import { attachClaude, backend, openTour, prLabel, seedTour, startApp, updateIteration } from '../harness';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { ChangedFileList } from '../../src/lib/types';
import { filesPanel, resetPageEachTest, worldOf } from './appPanels';

resetPageEachTest();

const aBase = 'export const a = 1;\n';
const goneBase = 'export const gone = 1;\n';

// A modified file and a deleted one, whose marks the server counts by the file's object ID at the head and by the file staying deleted.
const marked = backend().template('reviewMarks-two', async (world) => {
   const seeded = await seedTour(world, { base: { 'a.ts': aBase, 'gone.ts': goneBase }, head: { 'a.ts': 'export const a = 2;\n', 'gone.ts': null }, bound: false });
   const list = await world.reviewer.call('list_changed_files', { tourId: seeded.tourId });
   return { tourId: seeded.tourId, base: seeded.base, head: seeded.head, objectId: list.files.find((file) => file.path === 'a.ts')!.headObjectId! };
});

type Seed = { tourId: string; base: string; head: string; objectId: string };

/** Returns a world copied from the template, with the template's pull request set again, since a copy's platform has not answered for it yet and would keep each iteration check waiting. */
async function markedWorld(): Promise<{ world: World; seed: Seed }> {
   const { world, seed } = await worldOf(marked);
   await world.pullRequest.set(seed.head, seed.base);
   return { world, seed };
}

async function showFiles(world: World): Promise<{ app: LotsenApp; transport: Transport }> {
   const started = await startApp(world);
   await openTour(started.app);
   await (await filesPanel()).findByRole('tree', { name: 'Changed files' });
   return started;
}

const row = (name: string) => within(screen.getByRole('tree', { name: 'Changed files' })).getByRole('treeitem', { name });
const toggleMark = (name: string) => fireEvent.click(row(name).querySelector('.check')!);
const checked = (name: string) => row(name).getAttribute('aria-checked');
const reviewed = async (world: World, tourId: string) => (await world.reviewer.call('list_changed_files', { tourId })).reviewedPaths;
const markRequests = (transport: Transport) => transport.requests.filter((request) => request.operation === 'mark_file_reviewed' || request.operation === 'clear_file_reviewed');
const listRequests = (transport: Transport) => transport.requests.filter((request) => request.operation === 'list_changed_files').length;

/** Commits a new head on top of the change, and has the reviewer take it as iteration 2, which a session that holds the tour must be there to hear of. */
async function newIteration(world: World, seed: Seed, files: Record<string, string>): Promise<string> {
   const head = await world.repo.commit('feature', 'Iteration 2', files, seed.head);
   await attachClaude(world, seed.tourId);
   await world.pullRequest.set(head, seed.base, { iteration: 2 });
   await world.reviewer.call('check_iteration', { tourId: seed.tourId });
   await world.idle();
   await updateIteration(world, seed.tourId);
   return head;
}

/**
 * Returns the transport with the next call of the operation held at the network, which then fails as a dropped connection does once the test releases it. `failed` completes as the call
 * fails.
 */
function failingLater(inner: Transport, operation: string): { transport: Transport; release: () => void; failed: Promise<void> } {
   let release!: () => void;
   const gate = new Promise<void>((resolve) => (release = resolve));
   let fail!: () => void;
   const failed = new Promise<void>((resolve) => (fail = resolve));
   let armed = true;
   const fetch: typeof window.fetch = async (input, init) => {
      if (armed && input === `/api/ops/${operation}`) {
         armed = false;
         await gate;
         fail();
         throw new TypeError('fetch failed');
      }

      return inner.fetch(input, init);
   };
   return { transport: { ...inner, fetch }, release, failed };
}

describe('review marks', () => {
   it('stores a mark with the file’s head object ID through the API, and the count follows', async () => {
      const { world, seed } = await markedWorld();
      const { transport } = await showFiles(world);
      expect(screen.getByText('0 of 2 reviewed')).toBeTruthy();

      await toggleMark('a.ts, Modified');
      expect(checked('a.ts, Modified')).toBe('true');
      expect(screen.getByText('1 of 2 reviewed')).toBeTruthy();
      await waitFor(async () => expect(await reviewed(world, seed.tourId)).toEqual(['a.ts']));

      await toggleMark('gone.ts, Deleted');
      await waitFor(async () => expect([...(await reviewed(world, seed.tourId))].sort()).toEqual(['a.ts', 'gone.ts']));
      expect(screen.getByText('2 of 2 reviewed')).toBeTruthy();

      await toggleMark('a.ts, Modified');
      await waitFor(async () => expect(await reviewed(world, seed.tourId)).toEqual(['gone.ts']));
      expect(checked('a.ts, Modified')).toBe('false');
      expect(screen.getByText('1 of 2 reviewed')).toBeTruthy();
      expect(markRequests(transport).map((request) => [request.operation, request.input?.headObjectId])).toEqual([
         ['mark_file_reviewed', seed.objectId],
         ['mark_file_reviewed', null],
         ['clear_file_reviewed', undefined],
      ]);
   });

   it('shows the marks that the server counts when the list loads', async () => {
      const { world, seed } = await markedWorld();
      await world.reviewer.call('mark_file_reviewed', { tourId: seed.tourId, path: 'a.ts', headObjectId: seed.objectId });
      // A deleted file's mark counts only without an object ID, so this one does not.
      await world.reviewer.call('mark_file_reviewed', { tourId: seed.tourId, path: 'gone.ts', headObjectId: 'f'.repeat(40) });

      await showFiles(world);

      expect(checked('a.ts, Modified')).toBe('true');
      expect(checked('gone.ts, Deleted')).toBe('false');
      expect(screen.getByText('1 of 2 reviewed')).toBeTruthy();
   });

   it('puts a mark back and says so when the request fails, until a later mark is stored', async () => {
      const { world, seed } = await markedWorld();
      const { transport } = await showFiles(world);
      transport.fail('mark_file_reviewed', { status: 500 });

      await toggleMark('a.ts, Modified');

      await waitFor(() => expect(checked('a.ts, Modified')).toBe('false'));
      expect((await filesPanel()).getByRole('alert').textContent).toContain('could not be saved');
      expect(screen.getByText('0 of 2 reviewed')).toBeTruthy();
      await toggleMark('a.ts, Modified');
      await waitFor(async () => expect(await reviewed(world, seed.tourId)).toEqual(['a.ts']));
      await waitFor(async () => expect((await filesPanel()).queryByRole('alert')).toBeNull());
   });

   it('sends two quick toggles of one file one after the other, so the server ends with the last', async () => {
      const { world, seed } = await markedWorld();
      const { transport } = await showFiles(world);
      const release = transport.hold('mark_file_reviewed');

      await toggleMark('a.ts, Modified');
      await toggleMark('a.ts, Modified');
      expect(checked('a.ts, Modified')).toBe('false');
      expect(markRequests(transport)).toHaveLength(1);
      release();

      await waitFor(() => expect(markRequests(transport).map((request) => request.operation)).toEqual(['mark_file_reviewed', 'clear_file_reviewed']));
      await waitFor(async () => expect(await reviewed(world, seed.tourId)).toEqual([]));
      expect(checked('a.ts, Modified')).toBe('false');
   });

   it('shows the mark the server last stored when a later toggle of the file fails', async () => {
      const { world, seed } = await markedWorld();
      const { transport } = await showFiles(world);
      const release = transport.hold('mark_file_reviewed');
      await toggleMark('a.ts, Modified');
      await toggleMark('a.ts, Modified');
      transport.fail('clear_file_reviewed', { status: 500 });

      release();

      await waitFor(async () => expect((await filesPanel()).getByRole('alert')).toBeTruthy());
      expect(checked('a.ts, Modified')).toBe('true');
      expect(await reviewed(world, seed.tourId)).toEqual(['a.ts']);
   });

   it('keeps a mark set while a reload of the list after a reconnect is in progress over the older list that the reload brings', async () => {
      const { world, seed } = await markedWorld();
      const { transport } = await showFiles(world);
      const release = transport.hold('list_changed_files');
      transport.cutEventStreams();
      await waitFor(() => expect(listRequests(transport)).toBe(2));

      await toggleMark('a.ts, Modified');
      await waitFor(async () => expect(await reviewed(world, seed.tourId)).toEqual(['a.ts']));
      const answered = transport.answered('list_changed_files');
      release();
      await answered;

      expect(checked('a.ts, Modified')).toBe('true');
      expect(screen.getByText('1 of 2 reviewed')).toBeTruthy();
   });

   it('takes the server’s marks from a list that starts loading after a mark is stored', async () => {
      const { world, seed } = await markedWorld();
      const { transport } = await showFiles(world);
      await toggleMark('a.ts, Modified');
      await waitFor(async () => expect(await reviewed(world, seed.tourId)).toEqual(['a.ts']));
      // Another tab of the reviewer clears the mark, which this page learns of only from the list that its reconnect loads.
      await world.reviewer.call('clear_file_reviewed', { tourId: seed.tourId, path: 'a.ts' });

      transport.cutEventStreams();

      await waitFor(() => expect(checked('a.ts, Modified')).toBe('false'));
   });

   describe('when a new head arrives', () => {
      it('drops a mark set during the load on a file that the new head changes, and keeps one on a file that stays deleted', async () => {
         const { world, seed } = await markedWorld();
         const { app, transport } = await showFiles(world);
         const release = transport.hold('list_changed_files');
         const head = await newIteration(world, seed, { 'a.ts': 'export const a = 3;\n' });
         await waitFor(() => expect(listRequests(transport)).toBe(2));

         let answered = transport.answered('mark_file_reviewed');
         await toggleMark('a.ts, Modified');
         await answered;
         answered = transport.answered('mark_file_reviewed');
         await toggleMark('gone.ts, Deleted');
         await answered;
         expect(markRequests(transport).map((request) => request.input?.headObjectId)).toEqual([seed.objectId, null]);
         release();

         await waitFor(() => expect(app.selectedFiles?.state === 'loaded' && app.selectedFiles.list.head).toBe(head));
         expect(checked('a.ts, Modified')).toBe('false');
         expect(checked('gone.ts, Deleted')).toBe('true');
         expect(screen.getByText('1 of 2 reviewed')).toBeTruthy();
      });

      it('drops a mark still in flight when the new list arrives, and counts no file that left the list', async () => {
         const { world, seed } = await markedWorld();
         const { transport } = await showFiles(world);
         const release = transport.hold('mark_file_reviewed');
         await toggleMark('a.ts, Modified');

         await newIteration(world, seed, { 'a.ts': 'export const a = 3;\n', 'gone.ts': goneBase });
         await waitFor(() => expect(screen.getByText('0 of 1 reviewed')).toBeTruthy());
         const answered = transport.answered('mark_file_reviewed');
         release();
         await answered;

         expect(markRequests(transport).map((request) => request.input?.headObjectId)).toEqual([seed.objectId]);
         expect(await reviewed(world, seed.tourId)).toEqual([]);
         expect(checked('a.ts, Modified')).toBe('false');
         expect(screen.getByText('0 of 1 reviewed')).toBeTruthy();
      });

      it('marks the new version when the reviewer checks the file again', async () => {
         const { world, seed } = await markedWorld();
         const { transport } = await showFiles(world);
         await toggleMark('a.ts, Modified');
         await waitFor(async () => expect(await reviewed(world, seed.tourId)).toEqual(['a.ts']));
         await newIteration(world, seed, { 'a.ts': 'export const a = 3;\n' });
         await waitFor(() => expect(checked('a.ts, Modified')).toBe('false'));
         const list: ChangedFileList = await world.reviewer.call('list_changed_files', { tourId: seed.tourId });

         await toggleMark('a.ts, Modified');

         await waitFor(async () => expect(await reviewed(world, seed.tourId)).toEqual(['a.ts']));
         expect(markRequests(transport).at(-1)?.input?.headObjectId).toBe(list.files.find((file) => file.path === 'a.ts')!.headObjectId);
         expect(list.files.find((file) => file.path === 'a.ts')!.headObjectId).not.toBe(seed.objectId);
         expect(checked('a.ts, Modified')).toBe('true');
      });
   });

   it('shows a failed mark’s alert on no other tour', async () => {
      const { world, seed } = await markedWorld();
      const other = await world.repo.commit('feature-2', 'Another change', { 'b.ts': 'export const b = 1;\n' }, seed.base);
      await world.pullRequest.set(other, seed.base);
      const claude = await world.claude();
      await claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/9', familiarity: 'some' });
      await world.idle();
      await claude.drop();
      const { transport, release, failed } = failingLater(world.transport(), 'mark_file_reviewed');
      const { app } = await startApp(world, { transport });
      await openTour(app, prLabel);
      await (await filesPanel()).findByRole('tree', { name: 'Changed files' });
      await toggleMark('a.ts, Modified');

      await openTour(app, 'shop #9');
      await (await filesPanel()).findByRole('treeitem', { name: 'b.ts, Added' });
      release();
      await failed;
      // The page handles the failure in the task after it.
      await new Promise((resolve) => setTimeout(resolve, 0));

      expect((await filesPanel()).queryByRole('alert')).toBeNull();
      await openTour(app, prLabel);
      await waitFor(() => expect(checked('a.ts, Modified')).toBe('false'));
      expect((await filesPanel()).queryByRole('alert')).toBeNull();
   });
});
