import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, clickTour, openTour, readTour, startApp } from '../harness';
import { planPanel, resetPageEachTest, worldOf } from './appPanels';
import { buildTour, gated, openPinnedTour, rounding, scrolledTo, totals, tourLoads, type EditorSpies, type SeededStops } from './fourStopTour';
import { spyOnFocus } from './editorFocus';

resetPageEachTest();

const spies = {} as EditorSpies;
beforeEach(() => {
   spies.focus = spyOnFocus();
});
afterEach(() => vi.restoreAllMocks());

const four = backend().template('stopLoads-four', (world) => buildTour(world));
const twoPending = backend().template('stopLoads-twoPending', (world) =>
   buildTour(world, [
      { title: 'Cart totals', kind: 'inspect', content: null },
      { title: 'Rounding', kind: 'risk', content: null },
   ]),
);
const threePending = backend().template('stopLoads-threePending', (world) =>
   buildTour(world, [
      { title: 'One', kind: 'inspect', content: null },
      { title: 'Two', kind: 'inspect', content: null },
      { title: 'Three', kind: 'inspect', content: null },
   ]),
);

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
const planStop = (title: string) => planPanel().getByRole('button', { name: new RegExp(title) });
const planStatus = (title: string) => planStop(title).querySelector('.status')?.textContent.trim() ?? null;
/** Gives the page time to start a load that it should not start, as nothing on the page shows a load that does not happen. */
const settled = () => new Promise((resolve) => setTimeout(resolve, 50));

/** A world with Claude bound to its tour, and the app open on it through a {@link gated} transport. */
async function attached(template: typeof four) {
   const { world, seed } = await worldOf(template);
   const claude = await attachClaude(world, seed.tourId);
   const gate = gated(world.transport());
   const { app, transport } = await startApp(world, { transport: gate.transport });
   return { world, seed, claude, gate, app, transport };
}

/** Writes the stop as Claude does, one second after the write before it, and waits until the page has the stopPut event, which moves the tour's update time. */
async function putStop(world: World, app: LotsenApp, claude: ClaudeSession, seed: SeededStops, index: number, content: Record<string, unknown>): Promise<void> {
   await world.clock.advance(1000);
   const written = (await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds[index], ...content })) as { updatedAt?: string };
   const updatedAt = written.updatedAt ?? (await readTour(world, seed.tourId)).updatedAt;
   await waitFor(() => expect(Date.parse(app.tours.find((tour) => tour.id === seed.tourId)!.updatedAt)).toBe(Date.parse(updatedAt)));
}

/** Writes Claude's plan again with the stops it has, by ID, and the changes that `change` makes to the list. */
async function replan(world: World, claude: ClaudeSession, tourId: string, change: (stops: { id?: string; title: string; kind: string; chapter: string }[]) => typeof stops): Promise<void> {
   const tour = await readTour(world, tourId);
   const stops = tour.plan!.stops.map((stop) => ({ id: stop.id, title: stop.title, kind: stop.kind, chapter: stop.chapter }));
   await world.clock.advance(1000);
   await claude.call('set_plan', { tourId, chapters: tour.plan!.chapters, stops: change(stops) });
}

describe('when a stopPut arrives around the load of the plan', () => {
   const statuses = (app: LotsenApp) => app.selectedPlan?.plan?.stops.map((candidate) => candidate.status);

   /** Opens the tour with its first plan load held once the server answered it. */
   async function selectWhileHeld(template: typeof four) {
      const opened = await attached(template);
      const held = opened.gate.holdAnswer('read_state');
      await clickTour();
      await held.answered;
      return { ...opened, release: held.release };
   }

   it('shows the stop ready when the event came before the load started', async () => {
      const { world, seed, claude, app } = await attached(twoPending);

      await putStop(world, app, claude, seed, 0, totals);
      await openTour(app);

      await waitFor(() => expect(statuses(app)).toEqual(['ready', 'pending']));
   });

   it('shows the stop ready when the event came during the load, whose answer predates it', async () => {
      const { world, seed, claude, app, transport, release } = await selectWhileHeld(twoPending);

      await putStop(world, app, claude, seed, 0, totals);
      release();

      await waitFor(() => expect(statuses(app)).toEqual(['ready', 'pending']));
      await settled();
      expect(statuses(app)).toEqual(['ready', 'pending']);
      expect(tourLoads(transport, seed.tourId)).toBe(2);
   });

   it('loads the plan once more for three events during one load', async () => {
      const { world, seed, claude, app, transport, release } = await selectWhileHeld(threePending);

      await putStop(world, app, claude, seed, 0, totals);
      await putStop(world, app, claude, seed, 1, totals);
      await putStop(world, app, claude, seed, 2, totals);
      release();

      await waitFor(() => expect(statuses(app)).toEqual(['ready', 'ready', 'ready']));
      await settled();
      expect(tourLoads(transport, seed.tourId)).toBe(2);
   });

   it('loads the plan once more again for an event during the extra load', async () => {
      const { world, seed, claude, app, gate, transport, release } = await selectWhileHeld(twoPending);
      await putStop(world, app, claude, seed, 0, totals);
      const extra = gate.holdAnswer('read_state');
      release();
      await extra.answered;

      await putStop(world, app, claude, seed, 1, rounding);
      extra.release();

      await waitFor(() => expect(statuses(app)).toEqual(['ready', 'ready']));
      await settled();
      expect(tourLoads(transport, seed.tourId)).toBe(3);
   });

   it('shows both stops ready when two events came during one load', async () => {
      const { world, seed, claude, app, release } = await selectWhileHeld(twoPending);

      await putStop(world, app, claude, seed, 0, totals);
      await putStop(world, app, claude, seed, 1, rounding);
      release();

      await waitFor(() => expect(statuses(app)).toEqual(['ready', 'ready']));
   });

   it('keeps a plan that a missed planSet changed, when an event comes during the reload after a reconnect', async () => {
      const { world, seed, claude, app, gate, transport } = await attached(twoPending);
      await openTour(app);
      await waitFor(() => expect(statuses(app)).toEqual(['pending', 'pending']));

      // The planSet happens while the stream is down, so the page learns of it only from the reload.
      const reconnect = gate.holdRequest('events');
      const reload = gate.holdAnswer('read_state');
      transport.cutEventStreams();
      await waitFor(() => expect(app.connection).toBe('reconnecting'));
      await replan(world, claude, seed.tourId, ([first, second]) => [{ ...first!, title: 'Totals, retitled' }, second!]);
      reconnect();
      await reload.answered;
      await putStop(world, app, claude, seed, 1, rounding);
      reload.release();

      await waitFor(() => expect(app.selectedPlan?.plan?.stops.map((candidate) => `${candidate.title} ${candidate.status}`)).toEqual(['Totals, retitled pending', 'Rounding ready']));
   });
});

describe('when the changed files are still loading', () => {
   /** Opens the stop of the title while the tour's changed files are held before they reach the page. */
   async function openWhileListHeld(stopTitle: string, options: { failList?: boolean } = {}) {
      const opened = await attached(four);
      let release: () => void;
      if (options.failList === true) {
         release = opened.gate.holdRequest('list_changed_files');
         opened.transport.fail('list_changed_files', { status: 500 });
      } else {
         release = opened.transport.hold('list_changed_files', 'response');
      }

      await openTour(opened.app);
      fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
      fireEvent.click(await planPanel().findByRole('button', { name: new RegExp(stopTitle) }));
      await stopPanel().findByRole('heading', { name: stopTitle });
      expect(opened.app.selectedFiles?.state).toBe('loading');
      return { ...opened, release };
   }

   const textRequests = (transport: { requests: readonly { operation: string | null }[] }) => transport.requests.filter((request) => request.operation === 'read_file_text');

   it('opens the passage in the diff once the list arrives', async () => {
      const { transport, release } = await openWhileListHeld('Cart totals');

      release();

      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(textRequests(transport)).toEqual([]);
   });

   it('opens the passage of a stop written while the list loads in the diff once the list arrives', async () => {
      const { seed, claude, transport, release } = await openWhileListHeld('Rounding');

      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds[2], ...rounding });
      await stopPanel().findByText('Rounding can hide a cent.');
      release();

      expect(await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20)).toBe('cart head line 20');
      expect(textRequests(transport)).toEqual([]);
   });

   it('opens the passage as the file at its side when the list fails to load', async () => {
      const { release } = await openWhileListHeld('Cart totals', { failList: true });

      release();

      expect(await scrolledTo(spies, 'File', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
   });

   it('leaves the passage closed when a planSet drops its stop before the list arrives', async () => {
      const { world, seed, claude, app, release } = await openWhileListHeld('Cart totals');
      await replan(world, claude, seed.tourId, ([first]) => [first!]);
      await waitFor(() => expect(stopPanel().queryByText(/Open a stop from the plan/)).not.toBeNull());

      release();
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      await settled();

      expect(app.selectedFilePath).toBeNull();
      expect(app.selectedText).toBeNull();
   });

   it('leaves the passage closed when the reviewer opens another file before the list arrives', async () => {
      const { app, release } = await openWhileListHeld('Cart totals');
      app.openRepositoryFile('head', 'src/catalog.ts');

      release();
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      await settled();

      expect(app.selectedText?.path).toBe('src/catalog.ts');
      expect(app.selectedFilePath).toBeNull();
   });

   it('reads a file that the reviewer opens before the list arrives once, because the list arrives at the pin that the read used', async () => {
      const { app, transport, release } = await openWhileListHeld('Cart totals');
      app.openRepositoryFile('head', 'src/catalog.ts');
      await waitFor(() => expect(app.selectedText?.state).toBe('loaded'));

      release();
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      await settled();

      expect(textRequests(transport)).toHaveLength(1);
      expect(app.selectedText?.state).toBe('loaded');
   });

   it('loads the diff of a file that the reviewer opens before the list arrives once, because the list names it at the pin that the load used', async () => {
      const { app, transport, release } = await openWhileListHeld('Cart totals');
      app.selectFile('src/pay.ts');
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));

      release();
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      await settled();

      expect(transport.requests.filter((request) => request.operation === 'diff_file')).toHaveLength(1);
      expect(app.selectedDiff?.state).toBe('loaded');
   });
});

describe('when a reload of the plan fails', () => {
   /**
    * Opens the tour's plan, then lets Claude add the pending stops of the titles while the page's stream is down and fails the plan's reload after the reconnect, so the page keeps a
    * plan that lacks them and loads the plan again on a stopPut for one of them.
    */
   async function openWithMissedStops(titles: string[]) {
      const opened = await attached(four);
      await openPinnedTour(opened.app);
      const reconnect = opened.gate.holdRequest('events');
      opened.transport.cutEventStreams();
      await waitFor(() => expect(opened.app.connection).toBe('reconnecting'));
      await replan(opened.world, opened.claude, opened.seed.tourId, (stops) => [...stops, ...titles.map((title) => ({ title, kind: 'inspect', chapter: 'only' }))]);
      const added = (await readTour(opened.world, opened.seed.tourId)).plan!.stops.slice(4).map((stop) => stop.id);
      const loads = tourLoads(opened.transport, opened.seed.tourId);
      opened.transport.fail('read_state', { status: 500 });
      reconnect();
      await waitFor(() => expect(tourLoads(opened.transport, opened.seed.tourId)).toBe(loads + 1));
      await waitFor(() => expect(opened.app.connection).toBe('live'), { timeout: 10_000 });
      await settled();
      expect(planPanel().queryByRole('button', { name: new RegExp(titles[0]!) })).toBeNull();
      return { ...opened, added };
   }

   it('keeps the known plan without an error when the reload that a stopPut starts fails', async () => {
      const { world, app, claude, seed, transport, added } = await openWithMissedStops(['Added quietly']);
      const loads = tourLoads(transport, seed.tourId);
      transport.fail('read_state', { status: 500 });

      await putStop(world, app, claude, { tourId: seed.tourId, stopIds: added }, 0, totals);
      await waitFor(() => expect(tourLoads(transport, seed.tourId)).toBe(loads + 1));
      await settled();

      expect(planPanel().queryByRole('alert')).toBeNull();
      expect(planStop('Cart totals')).toBeTruthy();
   });

   it('says the tour no longer exists when the reload after a reconnect finds no tour, even with a known plan', async () => {
      const { world, seed, app, gate, transport } = await attached(four);
      await openPinnedTour(app);
      expect(planStop('Cart totals')).toBeTruthy();

      // The tour goes while the stream is down, so the page learns of it only from the reload.
      const reconnect = gate.holdRequest('events');
      transport.cutEventStreams();
      await waitFor(() => expect(app.connection).toBe('reconnecting'));
      await world.reviewer.call('delete_tour', { tourId: seed.tourId });
      reconnect();

      expect((await planPanel().findByRole('alert')).textContent).toBe('This tour no longer exists.');
   });

   it('shows the plan of a later reload that succeeds after one that failed', async () => {
      const { world, app, claude, seed, transport, added } = await openWithMissedStops(['Added quietly', 'Added later']);
      const loads = tourLoads(transport, seed.tourId);
      transport.fail('read_state', { status: 500 });
      await putStop(world, app, claude, { tourId: seed.tourId, stopIds: added }, 0, totals);
      await waitFor(() => expect(tourLoads(transport, seed.tourId)).toBe(loads + 1));

      await putStop(world, app, claude, { tourId: seed.tourId, stopIds: added }, 1, totals);

      await waitFor(() => expect(planStatus('Added later')).toBeNull());
      expect(planStatus('Added quietly')).toBeNull();
      expect(planPanel().queryByRole('alert')).toBeNull();
   });
});
