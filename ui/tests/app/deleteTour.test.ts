import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { LotsenApp } from '../../src/lib/app.svelte';
import type { Tour } from '../../src/lib/types';
import type { World } from '../backend/backend';
import { backend, cartBase, cartContent, cartHead, renderApp, startApp, withEventHolds, withHolds } from '../harness';
import { openTourList, tourSwitcherName } from '../tourPicker';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

interface Listed {
   tourId: string;
   stopIds: string[];
}

/** Creates a tour on the pull request URL with the number, so the list labels it `shop #<number>`, with two written stops. */
async function createTour(world: World, number: number): Promise<Listed> {
   const claude = await world.claude();
   const tour = (await claude.call('create_tour', { prUrl: `https://platform.test/shop/pull/${number}`, familiarity: 'some' })) as Tour;
   await world.idle();
   const planned = (await claude.call('set_plan', {
      tourId: tour.id,
      chapters: [{ id: 'c1', title: 'Cart' }],
      stops: [
         { title: 'Cart adds', kind: 'inspect', chapter: 'c1' },
         { title: 'Line totals', kind: 'inspect', chapter: 'c1' },
      ],
   })) as Tour;
   const stopIds = planned.plan!.stops.map((stop) => stop.id);
   for (const stopId of stopIds) {
      await claude.call('put_stop', { tourId: tour.id, stopId, ...cartContent('The total rounds.') });
   }

   await claude.drop();
   return { tourId: tour.id, stopIds };
}

// The second tour is the newer one, so the list shows it first.
const twoTours = backend().template('deleteTour-two', async (world) => {
   const base = await world.repo.commit('main', 'The base', cartBase);
   const head = await world.repo.commit('feature', 'The change', cartHead, base);
   await world.pullRequest.set(head, base);
   const first = await createTour(world, 1);
   await world.clock.advance(1000);
   const second = await createTour(world, 2);
   return { first, second };
});

async function twoToursWorld(): Promise<{ world: World; first: Listed; second: Listed }> {
   // The template's clock moved on as it created the tours, so the copy's clock starts after that.
   const world = await backend().world({ template: twoTours, clockStart: '2026-01-01T13:00:00Z' });
   return { world, ...(world.seed as { first: Listed; second: Listed }) };
}

// The app of a test that reads the page before its tour list loads, which startApp waits for.
let loading: LotsenApp | undefined;

afterEach(() => {
   loading?.stop();
   loading = undefined;
});

const nav = () => document.querySelector<HTMLElement>('nav[aria-labelledby="tour-list-heading"]');

/** The labels of the listed tours, as the open list shows them. */
async function listedLabels(): Promise<string[]> {
   if (nav() === null) {
      await openTourList();
   }

   return [...nav()!.querySelectorAll('li')].map((item) => item.querySelector('.wrap-anywhere')!.textContent.trim());
}

/** Opens the list and clicks the delete button of the tour with the label, which the button's description names. */
async function clickDelete(label: string) {
   if (nav() === null) {
      await openTourList();
   }

   const row = [...nav()!.querySelectorAll('li')].find((item) => item.querySelector('.wrap-anywhere')?.textContent.trim() === label)!;
   const button = within(row).getByRole('button', { hidden: true, name: (_, element) => element.getAttribute('aria-label') === 'Delete this tour' });
   expect(document.getElementById(button.getAttribute('aria-describedby')!)?.textContent).toBe(label);
   await fireEvent.click(button);
}

const dialog = () => screen.findByRole('alertdialog', { name: 'Delete this tour?' });

async function listedIds(world: World): Promise<string[]> {
   return (await world.reviewer.call('list_tours', {})).tours.map((tour) => tour.id);
}

describe('deleting a tour', () => {
   it('asks for confirmation, and Cancel deletes nothing', async () => {
      const { world, first, second } = await twoToursWorld();
      const { transport } = await startApp(world);

      await clickDelete('shop #2');
      const confirm = await dialog();
      expect(confirm.textContent).toContain('shop #2');
      expect(confirm.textContent).toContain('cannot be undone');
      await fireEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));

      await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
      expect(transport.requests.filter((request) => request.operation === 'delete_tour')).toHaveLength(0);
      expect(await listedIds(world)).toEqual(expect.arrayContaining([first.tourId, second.tourId]));
      expect(await listedLabels()).toEqual(['shop #2', 'shop #1']);
   });

   it('deletes the tour once confirmed, with no session attached, keeps the dialog while the answer is on its way, and the tour leaves the list', async () => {
      const { world, first, second } = await twoToursWorld();
      const { transport } = await startApp(world);
      const release = transport.hold('delete_tour', 'request');

      await clickDelete('shop #2');
      const confirm = await dialog();
      await fireEvent.click(within(confirm).getByRole('button', { name: 'Delete tour' }));
      await waitFor(() => expect(within(confirm).getByRole('status').textContent).toBe('Deleting the tour…'));
      expect((within(confirm).getByRole('button', { name: 'Delete tour' }) as HTMLButtonElement).disabled).toBe(true);
      expect(within(confirm).getByRole('button', { name: 'Delete tour' }).getAttribute('aria-describedby')).toBe(within(confirm).getByRole('status').id);
      release();

      await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
      expect(transport.requests.filter((request) => request.operation === 'delete_tour').map((request) => request.input)).toEqual([{ tourId: second.tourId }]);
      expect(await listedIds(world)).toEqual([first.tourId]);
      expect(await listedLabels()).toEqual(['shop #1']);
   });

   it('says why a delete failed in the dialog and keeps the tour listed', async () => {
      const { world, first, second } = await twoToursWorld();
      const { app, transport } = await startApp(world);
      transport.fail('delete_tour', { status: 500 });

      await clickDelete('shop #2');
      const confirm = await dialog();
      await fireEvent.click(within(confirm).getByRole('button', { name: 'Delete tour' }));

      expect((await within(confirm).findByRole('alert')).textContent).toBe('The tour could not be deleted.');
      expect(screen.queryByRole('alertdialog')).not.toBeNull();
      expect(app.tours.map((tour) => tour.id)).toEqual([second.tourId, first.tourId]);
   });

   it('closes the dialog when the tour is already gone, as after a delete on another page', async () => {
      const { world, first, second } = await twoToursWorld();
      const transport = withEventHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await clickDelete('shop #2');
      const confirm = await dialog();
      // The other page's delete reaches this page only through the refusal of its own.
      transport.holdKinds('tourDeleted');
      await world.reviewer.call('delete_tour', { tourId: second.tourId });

      await fireEvent.click(within(confirm).getByRole('button', { name: 'Delete tour' }));

      await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
      expect(app.tours.map((tour) => tour.id)).toEqual([first.tourId]);
   });

   it('leaves the tour that the page shows when another page deletes it, and the page shows no tour', async () => {
      const { world, second } = await twoToursWorld();
      history.replaceState(null, '', `/#tour=${second.tourId}&stop=${second.stopIds[1]}`);
      const { app } = await startApp(world);
      await waitFor(() => expect(app.selectedStop?.id).toBe(second.stopIds[1]));

      await world.reviewer.call('delete_tour', { tourId: second.tourId });

      await waitFor(() => expect(app.selectedTourId).toBeNull());
      expect(app.selectedStop).toBeNull();
      expect(app.selectedTourError).toBeNull();
      await waitFor(() => expect(location.hash).toBe(''));
      expect(screen.getByRole('button', { name: tourSwitcherName }).textContent.trim()).toBe('Choose a tour');
      expect(await listedLabels()).toEqual(['shop #1']);
   });

   it('keeps a tour out that another page deleted while the list loads, though the late list response holds it', async () => {
      const { world, first, second } = await twoToursWorld();
      const transport = withHolds(world.transport());
      // The list predates the delete, and the page reads it after the delete's event.
      const list = transport.holdAnswered('list_tours');
      sessionStorage.setItem('lotsen.uiCredential', world.uiCredential);
      loading = new LotsenApp({ environment: { fetch: transport.fetch, location, history, storage: sessionStorage }, retryDelaysMs: [5] });
      renderApp(loading);
      await list.answered;

      await world.reviewer.call('delete_tour', { tourId: second.tourId });
      // The page shows nothing of a tour before its list arrives, so only the page's own record tells that the event was applied.
      await waitFor(() => expect((loading as unknown as { deletedTours: Set<string> }).deletedTours.has(second.tourId)).toBe(true));
      await list.release();
      await waitFor(() => expect(loading!.toursLoaded).toBe(true));

      expect(loading.tours.map((tour) => tour.id)).toEqual([first.tourId]);
   });

   it('keeps a deleted tour out when an event of it arrives late, or a later list load predates the delete', async () => {
      const { world, first, second } = await twoToursWorld();
      const transport = withEventHolds(withHolds(world.transport()));
      const { app } = await startApp(world, { transport });
      const releaseState = transport.holdKinds('tourStateChanged');
      await world.reviewer.call('record_reading_progress', { tourId: second.tourId, stopId: second.stopIds[0] });
      const list = transport.holdAnswered('list_tours');
      transport.cutEventStreams();
      await list.answered;

      await world.reviewer.call('delete_tour', { tourId: second.tourId });
      await waitFor(() => expect(app.tours.map((tour) => tour.id)).toEqual([first.tourId]));
      releaseState();
      await list.release();
      await waitFor(() => expect(transport.requests.filter((request) => request.operation === 'list_tours')).toHaveLength(2));
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(app.tours.map((tour) => tour.id)).toEqual([first.tourId]);
   });

   it('sends no progress write of a deleted tour after a write in flight ends', async () => {
      const { world, second } = await twoToursWorld();
      history.replaceState(null, '', `/#tour=${second.tourId}`);
      const { app, transport } = await startApp(world);
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
      const progressWrites = () => transport.requests.filter((request) => request.operation === 'record_reading_progress').map((request) => request.input);
      const release = transport.hold('record_reading_progress', 'response');
      app.openStop(second.stopIds[0]!);
      await waitFor(() => expect(progressWrites()).toHaveLength(1));
      app.openStop(second.stopIds[1]!);

      await world.reviewer.call('delete_tour', { tourId: second.tourId });
      await waitFor(() => expect(app.selectedTourId).toBeNull());
      release();
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(progressWrites()).toEqual([{ tourId: second.tourId, stopId: second.stopIds[0], version: 1 }]);
   });
});

describe('pruning the clones', () => {
   async function openSettings() {
      await fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
      return within(await screen.findByRole('dialog', { name: 'Settings' }));
   }

   // A pin that fails on a head the clone lacks leaves the clone it made, which no tour refers to.
   const unusedClone = backend().template('deleteTour-unusedClone', async (world) => {
      const base = await world.repo.commit('main', 'The base', cartBase);
      await world.pullRequest.set('0'.repeat(40), base);
      const claude = await world.claude();
      await claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/1', familiarity: 'some' });
      await world.idle();
      await claude.drop();
   });

   it('says how many clones the prune removed, and keeps the button busy while the answer is on its way', async () => {
      const world = await backend().world({ template: unusedClone });
      const { transport } = await startApp(world);
      const settings = await openSettings();
      const release = transport.hold('prune_clones', 'response');

      await fireEvent.click(settings.getByRole('button', { name: 'Prune unused clones' }));
      await waitFor(() => expect(settings.getByRole('status').textContent).toBe('Pruning the clones…'));
      expect((settings.getByRole('button', { name: 'Prune unused clones' }) as HTMLButtonElement).disabled).toBe(true);
      release();

      await waitFor(() => expect(settings.getByRole('status').textContent).toBe('Removed 1 clone.'));
      await fireEvent.click(settings.getByRole('button', { name: 'Prune unused clones' }));
      await waitFor(() => expect(settings.getByRole('status').textContent).toBe('No clone needed pruning.'));
   });

   it('says why a prune failed', async () => {
      const world = await backend().world();
      const { transport } = await startApp(world);
      transport.fail('prune_clones', { status: 500 });
      const settings = await openSettings();

      await fireEvent.click(settings.getByRole('button', { name: 'Prune unused clones' }));

      expect((await settings.findByRole('alert')).textContent).toBe('The clones could not be pruned.');
   });
});
