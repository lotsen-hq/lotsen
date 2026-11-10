import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { LotsenApp } from '../../src/lib/app.svelte';
import type { Tour } from '../../src/lib/types';
import type { World } from '../backend/backend';
import { attachClaude, backend, cartBase, cartContent, cartHead, checkIteration, pushIteration, readTour, renderApp, startApp, updateIteration, withEventHolds, withHolds } from '../harness';
import { openTourList } from '../tourPicker';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

// The app of a test that writes before the page's tour list loads, which startApp waits for.
let loading: LotsenApp | undefined;

afterEach(() => {
   loading?.stop();
   loading = undefined;
});

interface Listed {
   tourId: string;
   stopIds: string[];
}

const stops = [
   { title: 'Cart adds', kind: 'inspect' },
   { title: 'Line totals', kind: 'inspect' },
   { title: 'Gateway errors', kind: 'inspect' },
] as const;

/** Creates a tour on the pull request URL with the number, so the list labels it `shop #<number>`, with the stops of `written`'s length, written up to `written`, and no session attached. */
async function createTour(world: World, number: number, written = 3): Promise<Listed> {
   const claude = await world.claude();
   const tour = (await claude.call('create_tour', { prUrl: `https://platform.test/shop/pull/${number}`, familiarity: 'some' })) as Tour;
   await world.idle();
   const planned = (await claude.call('set_plan', { tourId: tour.id, chapters: [{ id: 'c1', title: 'Cart' }], stops: stops.map((stop) => ({ ...stop, chapter: 'c1' })) })) as Tour;
   const stopIds = planned.plan!.stops.map((stop) => stop.id);
   for (const stopId of stopIds.slice(0, written)) {
      await claude.call('put_stop', { tourId: tour.id, stopId, ...cartContent('The total rounds.') });
   }

   await claude.drop();
   await world.clock.advance(1000);
   return { tourId: tour.id, stopIds };
}

async function commitChange(world: World): Promise<{ base: string; head: string }> {
   const base = await world.repo.commit('main', 'The base', cartBase);
   const head = await world.repo.commit('feature', 'The change', cartHead, base);
   await world.pullRequest.set(head, base);
   return { base, head };
}

/** One tour in each state, newest last so the list shows them in reverse: generating, ready, in progress, finished and archived. */
const everyState = backend().template('tourStates-every', async (world) => {
   const commits = await commitChange(world);
   const generating = await createTour(world, 1, 2);
   const ready = await createTour(world, 2);
   const reading = await createTour(world, 3);
   await world.reviewer.call('record_reading_progress', { tourId: reading.tourId, stopId: reading.stopIds[1], version: 1 });
   const done = await createTour(world, 4);
   await world.reviewer.call('record_reading_progress', { tourId: done.tourId, summary: true });
   const archived = await createTour(world, 5);
   await world.reviewer.call('record_reading_progress', { tourId: archived.tourId, stopId: archived.stopIds[0] });
   await world.reviewer.call('set_tour_archived', { tourId: archived.tourId, archived: true });
   return { ...commits, generating, ready, reading, done, archived };
});

type EveryState = { base: string; head: string } & Record<'generating' | 'ready' | 'reading' | 'done' | 'archived', Listed>;

async function everyStateWorld(): Promise<{ world: World } & EveryState> {
   // The template's clock moved on as it created the tours, so the copy's clock starts after that.
   const world = await backend().world({ template: everyState, clockStart: '2026-01-01T13:00:00Z' });
   return { world, ...(world.seed as EveryState) };
}

interface ToursSeed {
   tours: Listed[];
   base: string;
   head: string;
}

/** Seeds a ready tour per number, labeled `shop #<number>`, on one change. */
async function seedTours(world: World, numbers: number[]): Promise<ToursSeed> {
   const commits = await commitChange(world);
   const tours: Listed[] = [];
   for (const number of numbers) {
      tours.push(await createTour(world, number));
   }

   return { tours, ...commits };
}

// The templates that toursWorld copies, by the numbers of their tours.
const toursTemplates = new Map([[1], [1, 2]].map((numbers) => [numbers.join('-'), backend().template(`tourStates-tours-${numbers.join('-')}`, (world) => seedTours(world, numbers))]));

/** A world with a ready tour per number, labeled `shop #<number>`, copied from the template of those numbers. */
async function toursWorld(...numbers: number[]): Promise<{ world: World } & ToursSeed> {
   const template = toursTemplates.get(numbers.join('-'));
   if (template === undefined) {
      throw new Error(`No template seeds the tours ${numbers.join(', ')}. Add the numbers to toursTemplates.`);
   }

   // The template's clock moved on as it created the tours, so the copy's clock starts after that.
   const world = await backend().world({ template, clockStart: '2026-01-01T13:00:00Z' });
   return { world, ...(world.seed as ToursSeed) };
}

/** Returns the tour's state as the server lists it. */
async function stateOf(world: World, tourId: string): Promise<string | undefined> {
   return (await world.reviewer.call('list_tours', {})).tours.find((tour) => tour.id === tourId)?.state;
}

/** Starts the page without waiting for its tour list, whose answer the transport may hold. */
function startLoading(world: World, transport: { fetch: typeof fetch }): LotsenApp {
   sessionStorage.setItem('lotsen.uiCredential', world.uiCredential);
   loading = new LotsenApp({ environment: { fetch: transport.fetch, location, history, storage: sessionStorage }, retryDelaysMs: [5] });
   renderApp(loading);
   return loading;
}

// The page shows nothing of an event that arrives while its tour list loads, so only the page's own record of such events tells that one arrived.
const eventsDuringListLoad = (app: LotsenApp) => (app as unknown as { eventsDuringListLoad: unknown[] | null }).eventsDuringListLoad?.length ?? 0;

/**
 * Opens the tour list unless it is open, and returns it. Its queries include hidden elements, because jsdom never positions the popover, so the positioner hides the list from role queries
 * once it measures it.
 */
async function tourList() {
   const nav = () => document.querySelector<HTMLElement>('nav[aria-labelledby="tour-list-heading"]');
   if (nav() === null) {
      await openTourList();
   }

   const list = within(nav()!);
   return {
      button: (name: string | RegExp) =>
         list.getByRole('button', {
            hidden: true,
            name: (accessibleName, element) => (typeof name === 'string' ? (element.getAttribute('aria-label') ?? accessibleName) === name : name.test(element.textContent.trim())),
         }),
      // A hidden label gives the checkbox no accessible name, so the label's text finds it.
      checkbox: (name: string) => {
         const checkbox = list.getByRole('checkbox', { hidden: true });
         expect(checkbox.closest('label')?.textContent.replace(/\s+/g, ' ').trim()).toBe(name);
         return checkbox;
      },
      queryCheckbox: () => list.queryByRole('checkbox', { hidden: true }),
      /** The archive or unarchive button of the tour with the label, which the button's description names. */
      archive: (label: string, name: 'Archive this tour' | 'Unarchive this tour' = 'Archive this tour') => {
         const row = [...nav()!.querySelectorAll('li')].find((item) => item.querySelector('.wrap-anywhere')?.textContent.trim() === label)!;
         const button = within(row).getByRole('button', { hidden: true, name: (_, element) => element.getAttribute('aria-label') === name });
         expect(document.getElementById(button.getAttribute('aria-describedby')!)?.textContent).toBe(label);
         return button;
      },
      text: (text: string) => list.queryByText(text),
      alert: () => list.findByRole('alert', { hidden: true }),
   };
}

/** Each listed tour's label and state, as the list reads them. */
async function listedRows(): Promise<[string, string][]> {
   await tourList();
   const items = document.querySelectorAll('nav[aria-labelledby="tour-list-heading"] li');
   return [...items].map((item) => [item.querySelector('.wrap-anywhere')!.textContent.trim(), item.querySelector('.tour-state')!.textContent.trim()]);
}

async function openFromList(app: LotsenApp, label: string) {
   (await tourList()).button(new RegExp(`^${label}`)).click();
   await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
}

/** The inputs of the page's progress writes, without the tour. */
function progressWrites(transport: { requests: readonly { operation: string | null; input: Record<string, unknown> | null }[] }): unknown[] {
   return transport.requests
      .filter((request) => request.operation === 'record_reading_progress')
      .map((request) => Object.fromEntries(Object.entries(request.input!).filter(([key]) => key !== 'tourId')));
}

/** Moves the tour to the head of a new iteration that makes no stop stale, as the reviewer's update in another page does, so the server publishes the tour's new pin. The tour needs an attached session. */
async function updateElsewhere(world: World, commits: { base: string; head: string }, tourId: string): Promise<void> {
   await pushIteration(world, { base: commits.base, parent: commits.head }, { 'docs/notes.md': 'Notes.\n' }, 2);
   await checkIteration(world, tourId);
   await updateIteration(world, tourId);
}

describe('the tour list', () => {
   it('shows each tour’s state and hides archived tours until the toggle shows them', async () => {
      const { world } = await everyStateWorld();
      await startApp(world);

      expect(await listedRows()).toEqual([
         ['shop #4', 'Finished'],
         ['shop #3', 'In progress'],
         ['shop #2', 'Ready'],
         ['shop #1', 'Generating'],
      ]);
      await fireEvent.click((await tourList()).checkbox('Show hidden tours (1)'));

      expect((await listedRows())[0]).toEqual(['shop #5', 'Archived']);
      expect(await listedRows()).toHaveLength(5);
      await fireEvent.click((await tourList()).checkbox('Show hidden tours (1)'));
      expect(await listedRows()).toHaveLength(4);
   });

   it('offers no toggle while no tour is hidden, and says so when every tour is hidden', async () => {
      const { world } = await toursWorld(1);
      await startApp(world);
      expect((await tourList()).queryCheckbox()).toBeNull();

      await fireEvent.click((await tourList()).archive('shop #1'));

      await waitFor(async () => expect((await tourList()).text('Every tour is hidden. Show hidden tours to see them.')).not.toBeNull());
      expect((await tourList()).checkbox('Show hidden tours (1)')).toBeTruthy();
   });

   it('takes the toggle away once the reviewer unarchives the last hidden tour while hidden tours show, and hides a tour archived after that', async () => {
      const { world, tours } = await toursWorld(1, 2);
      await world.reviewer.call('set_tour_archived', { tourId: tours[1]!.tourId, archived: true });
      const { app } = await startApp(world);
      await fireEvent.click((await tourList()).checkbox('Show hidden tours (1)'));

      await fireEvent.click(await waitFor(async () => (await tourList()).archive('shop #2', 'Unarchive this tour')));

      await waitFor(async () => expect((await tourList()).queryCheckbox()).toBeNull());
      expect(await listedRows()).toEqual([
         ['shop #2', 'Ready'],
         ['shop #1', 'Ready'],
      ]);
      expect(app.showHiddenTours).toBe(false);
      await fireEvent.click((await tourList()).archive('shop #1'));

      await waitFor(async () => expect(await listedRows()).toEqual([['shop #2', 'Ready']]));
      const toggle = (await tourList()).checkbox('Show hidden tours (1)') as HTMLInputElement;
      expect(toggle.checked).toBe(false);
   });

   it('archives a tour with no session attached from the server’s event, keeps it archived when the held response arrives late, and unarchives it to its derived state', async () => {
      const { world, reading } = await everyStateWorld();
      const { transport } = await startApp(world);
      const release = transport.hold('set_tour_archived', 'response');

      await fireEvent.click((await tourList()).archive('shop #3'));
      await waitFor(async () => expect((await listedRows()).map(([label]) => label)).not.toContain('shop #3'));
      release();
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect((await listedRows()).map(([label]) => label)).not.toContain('shop #3');
      expect(await stateOf(world, reading.tourId)).toBe('archived');
      await fireEvent.click((await tourList()).checkbox('Show hidden tours (2)'));
      await fireEvent.click(await waitFor(async () => (await tourList()).archive('shop #3', 'Unarchive this tour')));

      await waitFor(async () => expect((await listedRows()).find(([label]) => label === 'shop #3')).toEqual(['shop #3', 'In progress']));
      expect(await stateOf(world, reading.tourId)).toBe('inProgress');
   });

   it('applies the plan and the pin of events stored before the archive that arrive after its response, and takes the state from the archive’s event', async () => {
      const { world, tours, base, head } = await toursWorld(1);
      const [only] = tours;
      const claude = await attachClaude(world, only!.tourId);
      const transport = withEventHolds(world.transport());
      const { app } = await startApp(world, { transport });
      const release = transport.holdKinds('planSet', 'tourPinned');
      await claude.call('set_plan', {
         tourId: only!.tourId,
         chapters: [{ id: 'c1', title: 'Cart' }],
         stops: [...stops.map((stop, index) => ({ ...stop, chapter: 'c1', id: only!.stopIds[index] })), { title: 'Refunds', kind: 'inspect', chapter: 'c1' }],
      });
      await updateElsewhere(world, { base, head }, only!.tourId);
      await world.clock.advance(1000);

      expect(await app.setTourArchived(only!.tourId, true)).toBeNull();
      await waitFor(() => expect(app.tours[0]?.state).toBe('archived'));
      const archivedAt = app.tours[0]!.updatedAt;
      expect(app.tours[0]).toMatchObject({ stopCount: 3, pin: { iteration: 1 } });
      release();

      await waitFor(() => expect(app.tours[0]).toMatchObject({ stopCount: 4, pin: { iteration: 2 } }));
      expect(app.tours[0]).toMatchObject({ state: 'archived', updatedAt: archivedAt });
   });

   it('says why an archive failed and keeps the tour listed', async () => {
      const { world, tours } = await toursWorld(1);
      const transport = withEventHolds(world.transport());
      await startApp(world, { transport });
      // Another page deleted the tour, and the page hears of it only from the refusal of its archive.
      transport.holdKinds('tourDeleted');
      await world.reviewer.call('delete_tour', { tourId: tours[0]!.tourId });

      await fireEvent.click((await tourList()).archive('shop #1'));

      expect((await (await tourList()).alert()).textContent).toContain(`no tour has the ID`);
      expect(await listedRows()).toEqual([['shop #1', 'Ready']]);
   });

   it('updates a tour’s state from an event', async () => {
      const { world, ready, generating } = await everyStateWorld();
      await startApp(world);

      await world.reviewer.call('record_reading_progress', { tourId: ready.tourId, stopId: ready.stopIds[0] });
      await waitFor(async () => expect((await listedRows()).find(([label]) => label === 'shop #2')).toEqual(['shop #2', 'In progress']));
      await world.reviewer.call('set_tour_archived', { tourId: generating.tourId, archived: true });

      await waitFor(async () => expect((await listedRows()).map(([label]) => label)).not.toContain('shop #1'));
   });

   it('keeps a state change that arrives while the list loads, and a late list response does not undo it', async () => {
      const { world, ready } = await everyStateWorld();
      // The list predates the event, and the page reads it after the event.
      const transport = withHolds(world.transport());
      const list = transport.holdAnswered('list_tours');
      const app = startLoading(world, transport);
      await list.answered;
      await world.clock.advance(1000);

      await world.reviewer.call('record_reading_progress', { tourId: ready.tourId, summary: true });
      await waitFor(() => expect(eventsDuringListLoad(app)).toBeGreaterThan(0));
      await list.release();
      await waitFor(() => expect(app.toursLoaded).toBe(true));

      expect(app.tours.find((tour) => tour.id === ready.tourId)?.state).toBe('finished');
   });
});

describe('tours whose pull request was completed or abandoned', () => {
   /** The flag of each listed tour, or null for a tour without one, as the list shows it. */
   async function listedFlags(): Promise<[string, string | null][]> {
      await tourList();
      const items = document.querySelectorAll('nav[aria-labelledby="tour-list-heading"] li');
      return [...items].map((item) => [item.querySelector('.wrap-anywhere')!.textContent.trim(), item.querySelector('.tour-flag')?.textContent.trim() ?? null]);
   }

   /** Four tours: an open one, a finished one whose pull request completed, a ready one whose pull request was abandoned, and an archived one whose pull request completed. */
   const flagged = backend().template('tourStates-flagged', async (world) => {
      const { tours, ...commits } = await seedTours(world, [1, 2, 3, 4]);
      const [, merged, dropped, both] = tours;
      await world.reviewer.call('record_reading_progress', { tourId: merged!.tourId, summary: true });
      await world.reviewer.call('set_tour_archived', { tourId: both!.tourId, archived: true });
      // The pull request's state is each tour's as its last check found it.
      await world.pullRequest.set(commits.head, commits.base, { state: 'completed' });
      await checkIteration(world, merged!.tourId);
      await checkIteration(world, both!.tourId);
      await world.pullRequest.set(commits.head, commits.base, { state: 'abandoned' });
      await checkIteration(world, dropped!.tourId);
      await world.pullRequest.set(commits.head, commits.base);
      return { dropped: dropped! };
   });

   it('stay hidden until the toggle shows them with their flag beside their state, and keep their archive button', async () => {
      // The template's clock moved on as it created the tours, so the copy's clock starts after that.
      const world = await backend().world({ template: flagged, clockStart: '2026-01-01T13:00:00Z' });
      const { dropped } = world.seed as { dropped: Listed };
      await startApp(world);

      expect(await listedFlags()).toEqual([['shop #1', null]]);
      await fireEvent.click((await tourList()).checkbox('Show hidden tours (3)'));

      expect(await listedFlags()).toEqual([
         ['shop #4', 'PR completed'],
         ['shop #3', 'PR abandoned'],
         ['shop #2', 'PR completed'],
         ['shop #1', null],
      ]);
      expect(await listedRows()).toEqual([
         ['shop #4', 'Archived'],
         ['shop #3', 'Ready'],
         ['shop #2', 'Finished'],
         ['shop #1', 'Ready'],
      ]);
      expect((await tourList()).archive('shop #3')).toBeTruthy();
      expect(await stateOf(world, dropped.tourId)).toBe('ready');
   });

   it('flags and hides a tour from the event of a check, and lists it again without the flag once its pull request is active again', async () => {
      const { world, tours, base, head } = await toursWorld(1, 2);
      const closing = tours[1]!;
      await world.reviewer.call('record_reading_progress', { tourId: closing.tourId, stopId: closing.stopIds[0] });
      await startApp(world);

      await world.pullRequest.set(head, base, { state: 'abandoned' });
      await checkIteration(world, closing.tourId);
      await waitFor(async () => expect(await listedFlags()).toEqual([['shop #1', null]]));
      await fireEvent.click((await tourList()).checkbox('Show hidden tours (1)'));
      expect(await listedFlags()).toEqual([
         ['shop #2', 'PR abandoned'],
         ['shop #1', null],
      ]);
      expect(await listedRows()).toContainEqual(['shop #2', 'In progress']);

      await world.clock.advance(1000);
      await world.pullRequest.set(head, base);
      await checkIteration(world, closing.tourId);

      await waitFor(async () => expect((await tourList()).queryCheckbox()).toBeNull());
      expect(await listedFlags()).toEqual([
         ['shop #2', null],
         ['shop #1', null],
      ]);
   });

   it('keeps a flag that arrives while the list loads, and a late list response does not undo it', async () => {
      const { world, tours, base, head } = await toursWorld(1);
      const closing = tours[0]!;
      const transport = withHolds(world.transport());
      const list = transport.holdAnswered('list_tours');
      const app = startLoading(world, transport);
      await list.answered;
      await world.clock.advance(1000);

      await world.pullRequest.set(head, base, { state: 'completed' });
      await checkIteration(world, closing.tourId);
      await waitFor(() => expect(eventsDuringListLoad(app)).toBeGreaterThan(0));
      await list.release();
      await waitFor(() => expect(app.toursLoaded).toBe(true));

      expect(app.tours.find((tour) => tour.id === closing.tourId)?.pullRequestState).toBe('completed');
   });
});

describe('opening a tour from the list', () => {
   it('resumes at the stop the reviewer opened last, in the history entry that the open added', async () => {
      const { world, reading } = await everyStateWorld();
      const { app, transport } = await startApp(world);
      const entries = history.length;

      await openFromList(app, 'shop #3');

      await waitFor(() => expect(app.selectedStopId).toBe(reading.stopIds[1]));
      // The stop's primary passage adds its lines to the entry.
      expect(location.hash).toBe(`#tour=${reading.tourId}&stop=${reading.stopIds[1]}&side=head&file=src%2Fcart.ts&lines=2-2`);
      expect(history.length).toBe(entries + 1);
      expect(progressWrites(transport)).toEqual([]);
   });

   it('resumes at the summary page when the reviewer opened it last', async () => {
      const { world, done } = await everyStateWorld();
      const { app } = await startApp(world);

      await openFromList(app, 'shop #4');

      expect(await screen.findByRole('main', { name: 'Summary' })).toBeTruthy();
      expect(location.hash).toBe(`#tour=${done.tourId}&view=summary`);
   });

   it('opens a tour never opened with no stop open, as before any reading', async () => {
      const { world } = await everyStateWorld();
      const { app } = await startApp(world);

      await openFromList(app, 'shop #2');
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(app.selectedStopId).toBeNull();
      expect(app.showsSummary).toBe(false);
   });

   it('falls back when a re-plan dropped the last stop: to the summary page of a finished tour, and to no open stop otherwise', async () => {
      const { world, tours } = await toursWorld(1, 2);
      const [dropped, finishedDropped] = tours;
      await world.reviewer.call('record_reading_progress', { tourId: dropped!.tourId, stopId: dropped!.stopIds[2] });
      await world.reviewer.call('record_reading_progress', { tourId: finishedDropped!.tourId, stopId: finishedDropped!.stopIds[2] });
      await world.reviewer.call('record_reading_progress', { tourId: finishedDropped!.tourId, summary: true });
      await world.reviewer.call('record_reading_progress', { tourId: finishedDropped!.tourId, stopId: finishedDropped!.stopIds[2] });
      for (const tour of tours) {
         const claude = await attachClaude(world, tour.tourId);
         await claude.call('set_plan', {
            tourId: tour.tourId,
            chapters: [{ id: 'c1', title: 'Cart' }],
            stops: stops.slice(0, 2).map((stop, index) => ({ ...stop, chapter: 'c1', id: tour.stopIds[index] })),
         });
         await claude.drop();
      }

      const { app } = await startApp(world);

      await openFromList(app, 'shop #1');
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(app.selectedStopId).toBeNull();
      await openFromList(app, 'shop #2');

      expect(await screen.findByRole('main', { name: 'Summary' })).toBeTruthy();
   });

   it('resumes on a page load whose entry names the tour alone, and a deep link to a stop wins', async () => {
      const { world, tours } = await toursWorld(1);
      const [reading] = tours;
      await world.reviewer.call('record_reading_progress', { tourId: reading!.tourId, stopId: reading!.stopIds[2] });
      history.replaceState(null, '', `/#tour=${reading!.tourId}`);
      const first = await startApp(world);
      await waitFor(() => expect(first.app.selectedStopId).toBe(reading!.stopIds[2]));
      first.app.stop();
      cleanup();

      history.replaceState(null, '', `/#tour=${reading!.tourId}&stop=${reading!.stopIds[0]}`);
      const { app } = await startApp(world);
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(app.selectedStopId).toBe(reading!.stopIds[0]);
      expect(location.hash).toBe(`#tour=${reading!.tourId}&stop=${reading!.stopIds[0]}`);
   });

   it('leaves a stop that the reviewer opened while the tour loads where it is', async () => {
      const { world, tours } = await toursWorld(1);
      const [reading] = tours;
      await world.reviewer.call('record_reading_progress', { tourId: reading!.tourId, stopId: reading!.stopIds[2] });
      const { app, transport } = await startApp(world);
      const release = transport.hold('read_state', 'response');

      (await tourList()).button(/^shop #1/).click();
      await waitFor(() => expect(app.selectedTourId).toBe(reading!.tourId));
      app.openSummary();
      release();
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(app.showsSummary).toBe(true);
      expect(app.selectedStopId).toBeNull();
   });
});

describe('recording reading progress', () => {
   it('records each stop and the summary page that the reviewer opens, and the tour’s state follows', async () => {
      const { world, tours } = await toursWorld(1);
      const [tour] = tours;
      const { app, transport } = await startApp(world);
      await openFromList(app, 'shop #1');

      app.openStop(tour!.stopIds[0]!);
      await waitFor(() => expect(progressWrites(transport)).toEqual([{ stopId: tour!.stopIds[0], version: 1 }]));
      await waitFor(() => expect(app.selectedTour?.state).toBe('inProgress'));
      app.openSummary();

      await waitFor(() => expect(progressWrites(transport)).toEqual([{ stopId: tour!.stopIds[0], version: 1 }, { summary: true }]));
      await waitFor(() => expect(app.selectedTour?.state).toBe('finished'));
      expect((await readTour(world, tour!.tourId)).progress).toMatchObject({ finished: true });
   });

   it('leaves the list’s update time to events, so an event stored before a move to another stop still applies after the move’s response', async () => {
      const { world, tours, base, head } = await toursWorld(1);
      const [tour] = tours;
      await world.reviewer.call('record_reading_progress', { tourId: tour!.tourId, stopId: tour!.stopIds[0] });
      await attachClaude(world, tour!.tourId);
      const transport = withEventHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openFromList(app, 'shop #1');
      await waitFor(() => expect(app.selectedStopId).toBe(tour!.stopIds[0]));
      // The stream delivers a pin that the server stored before the move, after the move's response.
      const release = transport.holdKinds('tourPinned');
      await updateElsewhere(world, { base, head }, tour!.tourId);
      await world.clock.advance(1000);
      const moved = transport.answered('record_reading_progress');

      app.openStop(tour!.stopIds[1]!);
      await moved;
      await waitFor(() => expect(app.tours[0]?.state).toBe('inProgress'));
      release();

      await waitFor(() => expect(app.selectedTour?.pin).toMatchObject({ iteration: 2 }));
   });

   it('sends one write at a time, and after a burst of stop openings only the last stop', async () => {
      const { world, tours } = await toursWorld(1);
      const [tour] = tours;
      const { app, transport } = await startApp(world);
      await openFromList(app, 'shop #1');
      const release = transport.hold('record_reading_progress', 'response');

      app.openStop(tour!.stopIds[0]!);
      await waitFor(() => expect(progressWrites(transport)).toHaveLength(1));
      app.openNextStop();
      app.openNextStop();
      app.openPreviousStop();
      app.openNextStop();
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toHaveLength(1);
      release();

      await waitFor(() =>
         expect(progressWrites(transport)).toEqual([
            { stopId: tour!.stopIds[0], version: 1 },
            { stopId: tour!.stopIds[2], version: 1 },
         ]),
      );
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toHaveLength(2);
      await waitFor(async () => expect((await readTour(world, tour!.tourId)).progress).toEqual({ finished: false, lastStop: tour!.stopIds[2] }));
   });

   it('sends a place again when the reviewer opens it again after its write failed, so a failed summary write still finishes the tour', async () => {
      const { world, tours } = await toursWorld(1);
      const [tour] = tours;
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openFromList(app, 'shop #1');
      transport.fail('record_reading_progress', { status: 500 });
      const held = transport.holdAnswered('record_reading_progress');

      app.openSummary();
      await waitFor(() => expect(progressWrites(transport)).toEqual([{ summary: true }]));
      await held.release();
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toHaveLength(1);
      expect((await readTour(world, tour!.tourId)).progress).toBeUndefined();
      app.openSummary();

      await waitFor(() => expect(progressWrites(transport)).toEqual([{ summary: true }, { summary: true }]));
      await waitFor(() => expect(app.selectedTour?.state).toBe('finished'));
      app.openSummary();
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toHaveLength(2);
   });

   it('sends a stop again after its write failed when the reviewer opened it again while the write was in flight', async () => {
      const { world, tours } = await toursWorld(1);
      const [tour] = tours;
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openFromList(app, 'shop #1');
      transport.fail('record_reading_progress', { status: 500 });
      const held = transport.holdAnswered('record_reading_progress');

      app.openStop(tour!.stopIds[0]!);
      await waitFor(() => expect(progressWrites(transport)).toHaveLength(1));
      app.openStop(tour!.stopIds[0]!);
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toHaveLength(1);
      await held.release();

      await waitFor(async () => expect((await readTour(world, tour!.tourId)).progress).toEqual({ finished: false, lastStop: tour!.stopIds[0] }));
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toEqual([
         { stopId: tour!.stopIds[0], version: 1 },
         { stopId: tour!.stopIds[0], version: 1 },
      ]);
   });

   it('sends nothing more when the write of a stop that the reviewer opened again while it was in flight succeeds', async () => {
      const { world, tours } = await toursWorld(1);
      const [tour] = tours;
      const { app, transport } = await startApp(world);
      await openFromList(app, 'shop #1');
      const release = transport.hold('record_reading_progress', 'response');

      app.openStop(tour!.stopIds[0]!);
      await waitFor(() => expect(progressWrites(transport)).toHaveLength(1));
      app.openStop(tour!.stopIds[0]!);
      release();

      await waitFor(async () => expect((await readTour(world, tour!.tourId)).progress).toEqual({ finished: false, lastStop: tour!.stopIds[0] }));
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toHaveLength(1);
   });

   it('sends a stop again when the reviewer opens it again after its write failed', async () => {
      const { world, tours } = await toursWorld(1);
      const [tour] = tours;
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openFromList(app, 'shop #1');
      transport.fail('record_reading_progress', { status: 500 });
      const held = transport.holdAnswered('record_reading_progress');

      app.openStop(tour!.stopIds[0]!);
      await waitFor(() => expect(progressWrites(transport)).toHaveLength(1));
      await held.release();
      await new Promise((resolve) => setTimeout(resolve, 20));
      app.openStop(tour!.stopIds[0]!);

      await waitFor(() =>
         expect(progressWrites(transport)).toEqual([
            { stopId: tour!.stopIds[0], version: 1 },
            { stopId: tour!.stopIds[0], version: 1 },
         ]),
      );
      await waitFor(async () => expect((await readTour(world, tour!.tourId)).progress).toEqual({ finished: false, lastStop: tour!.stopIds[0] }));
      await new Promise((resolve) => setTimeout(resolve, 20));
      app.openStop(tour!.stopIds[0]!);
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toHaveLength(2);
   });

   it('sends nothing for the stop that the server holds already, and resumes at the last stop after a switch to another tour', async () => {
      const { world, tours } = await toursWorld(1, 2);
      const [tour] = tours;
      await world.reviewer.call('record_reading_progress', { tourId: tour!.tourId, stopId: tour!.stopIds[1], version: 1 });
      const { app, transport } = await startApp(world);
      await openFromList(app, 'shop #1');
      await waitFor(() => expect(app.selectedStopId).toBe(tour!.stopIds[1]));
      const release = transport.hold('record_reading_progress', 'response');

      app.openStop(tour!.stopIds[2]!);
      await waitFor(() => expect(progressWrites(transport)).toEqual([{ stopId: tour!.stopIds[2], version: 1 }]));
      await openFromList(app, 'shop #2');
      await openFromList(app, 'shop #1');

      await waitFor(() => expect(app.selectedStopId).toBe(tour!.stopIds[2]));
      release();
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(progressWrites(transport)).toEqual([{ stopId: tour!.stopIds[2], version: 1 }]);
   });
});
