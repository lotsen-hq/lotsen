import { fireEvent, screen, waitFor } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { World } from '../backend/backend';
import { backend, checkIteration, openTour, prLabel, pushIteration, seedTour, startApp, withHolds } from '../harness';
import { openTourList, tourButton } from '../tourPicker';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

const cartPath = 'src/cart.ts';
const cartLines = (adds: string, totals: string, between: string[] = []): string =>
   ['export function add(cart: number[], price: number): number[] {', adds, '}', ...between, '', 'export function total(lines: number[]): number {', totals, '}', ''].join('\n');
const headAdds = '   return [...cart, Math.max(0, price)];';
const headTotals = '   return Math.round(lines.reduce((sum, line) => sum + line, 0));';

/** The cart files of a later iteration that changes the lines of the stops that it names, so the server's remap makes those stops stale. */
function iterationFiles(stale: ('adds' | 'totals')[]): Record<string, string> {
   return {
      [cartPath]: cartLines(
         stale.includes('adds') ? '   return [...cart, Math.max(1, price)];' : headAdds,
         stale.includes('totals') ? '   return Math.ceil(lines.reduce((sum, line) => sum + line, 0));' : headTotals,
      ),
   };
}

function stopContent(headline: string, line: number): Record<string, unknown> {
   return { headline, how: 'The change guards the value.', passages: [{ side: 'head', path: cartPath, startLine: line, endLine: line, role: 'primary' }] };
}

interface Seed {
   tourId: string;
   stopIds: string[];
   base: string;
   head: string;
}

const cart = backend().template('iterations-cart', async (world): Promise<Seed> => {
   const seeded = await seedTour(world, {
      base: { [cartPath]: cartLines('   return [...cart, price];', '   return lines.reduce((sum, line) => sum + line, 0);') },
      head: { [cartPath]: cartLines(headAdds, headTotals) },
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'Cart adds', kind: 'inspect', content: stopContent('Adds guard the price.', 2) },
               { title: 'Line totals', kind: 'inspect', content: stopContent('Totals round.', 6) },
            ],
         },
      ],
      bound: false,
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds, base: seeded.base, head: seeded.head };
});

async function cartWorld(): Promise<{ world: World; seed: Seed }> {
   const world = await backend().world({ template: cart });
   return { world, seed: world.seed as Seed };
}

/** Pushes iteration `iteration` onto the seeded head and lets the server's check find it, after the clock moves on so its event is newer than what the page holds. */
async function pushChecked(world: World, seed: Seed, stale: ('adds' | 'totals')[], iteration = 2): Promise<string> {
   const head = await pushIteration(world, { base: seed.base, parent: seed.head }, iterationFiles(stale), iteration);
   await world.clock.advance(1000);
   await checkIteration(world, seed.tourId);
   return head;
}

/** Points the pull request at the seeded head again and lets the server's check find no newer iteration. */
async function withdraw(world: World, seed: Seed): Promise<void> {
   await world.pullRequest.set(seed.head, seed.base);
   await world.clock.advance(1000);
   await checkIteration(world, seed.tourId);
}

const notice = () => screen.queryByRole('status', { name: 'New update' });
const checkButton = () => screen.getByRole('button', { name: 'Check the PR for a new update' }) as HTMLButtonElement;
const settle = () => new Promise((resolve) => setTimeout(resolve, 50));

describe('a newer iteration of the open tour', () => {
   it('shows the notice with the number and the affected count when the tour loads with one', async () => {
      const { world, seed } = await cartWorld();
      await pushChecked(world, seed, ['adds', 'totals']);
      const { app } = await startApp(world);

      await openTour(app);

      await waitFor(() => expect(notice()?.textContent).toBe('Update 2 is available: 2 stops affected. This tour shows update 1 until you move it to update 2.'));
   });

   it('shows the notice from the event and keeps the open stop, the diff and the pin', async () => {
      const { world, seed } = await cartWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      app.openStop(seed.stopIds[0]!);
      app.selectFile(cartPath);
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      const diff = app.selectedDiff;
      const pin = app.selectedTour?.pin;
      const tourLoads = () => transport.requests.filter((request) => ['read_state', 'list_changed_files', 'diff_file', 'read_file_text', 'list_directory'].includes(request.operation ?? '')).length;
      const loads = tourLoads();

      await pushChecked(world, seed, ['adds']);

      await waitFor(() => expect(notice()?.textContent).toContain('Update 2 is available: 1 stop affected'));
      await settle();
      expect(tourLoads()).toBe(loads);
      expect(app.selectedStopId).toBe(seed.stopIds[0]);
      expect(app.selectedDiff).toBe(diff);
      expect(app.selectedTour?.pin).toEqual(pin);
   });

   it('hides the notice when an event clears the iteration', async () => {
      const { world, seed } = await cartWorld();
      await pushChecked(world, seed, ['adds']);
      const { app } = await startApp(world);
      await openTour(app);
      await waitFor(() => expect(notice()).not.toBeNull());

      await withdraw(world, seed);

      await waitFor(() => expect(notice()).toBeNull());
   });

   it('keeps a newer event when the tour load that started before it answers late', async () => {
      const { world, seed } = await cartWorld();
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      const load = transport.holdAnswered('read_state');
      (await openTourList())
         .getAllByRole('button', tourButton)
         .find((button) => button.textContent?.includes(prLabel))!
         .click();
      await load.answered;

      await pushChecked(world, seed, ['adds']);
      await waitFor(() => expect(notice()).not.toBeNull());
      await load.release();
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
      await settle();

      expect(notice()?.textContent).toContain('Update 2 is available: 1 stop affected');
   });

   it('offers no check for a tour that is not pinned', async () => {
      const world = await backend().world();
      const claude = await world.claude();
      await claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/42', familiarity: 'some' });
      const { app } = await startApp(world);

      await openTour(app);

      expect(app.selectedTour?.pin.state).toBe('pinning');
      expect(screen.queryByRole('button', { name: 'Check the PR for a new update' })).toBeNull();
   });
});

describe('the refresh button', () => {
   it('runs a check, is disabled and busy while it runs, and shows what the check found', async () => {
      const { world, seed } = await cartWorld();
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await pushIteration(world, { base: seed.base, parent: seed.head }, iterationFiles(['adds', 'totals']), 3);
      const check = transport.holdAnswered('check_iteration');
      // The check's response enables the button, and only the check's event brings the notice, so the stalled stream delivers the event after the response.
      const unstall = transport.stallEventStreams();

      await fireEvent.click(checkButton());

      await waitFor(() => expect(checkButton().disabled).toBe(true));
      expect(checkButton().getAttribute('aria-busy')).toBe('true');
      await check.answered;
      await check.release();
      await waitFor(() => expect(checkButton().disabled).toBe(false));
      expect(checkButton().getAttribute('aria-busy')).toBe('false');
      expect(notice()).toBeNull();
      unstall();
      await waitFor(() => expect(notice()?.textContent).toContain('Update 3 is available: 2 stops affected'));
      expect(transport.requests.filter((request) => request.operation === 'check_iteration')).toHaveLength(1);
   });

   it('sends one check while one waits for its response', async () => {
      const { world } = await cartWorld();
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      const check = transport.holdAnswered('check_iteration');

      void app.checkIteration();
      void app.checkIteration();
      await check.answered;
      await check.release();
      await waitFor(() => expect(app.selectedIterationCheck.running).toBe(false));

      expect(transport.requests.filter((request) => request.operation === 'check_iteration')).toHaveLength(1);
   });

   it('shows why a check could not finish and keeps the notice of the earlier one', async () => {
      const { world, seed } = await cartWorld();
      await pushChecked(world, seed, ['adds']);
      const { app } = await startApp(world);
      await openTour(app);
      await world.pullRequest.fail('The sign-in expired. Run az login, then try again.');

      await fireEvent.click(checkButton());

      expect((await screen.findByRole('alert')).textContent).toBe('The check for a new update could not finish: The sign-in expired. Run az login, then try again.');
      expect(notice()?.textContent).toContain('Update 2 is available: 1 stop affected');
   });

   it('clears the failure once a later check finishes', async () => {
      const { world, seed } = await cartWorld();
      const { app } = await startApp(world);
      await openTour(app);
      await world.pullRequest.fail('The pull request could not be reached.');
      await fireEvent.click(checkButton());
      await screen.findByRole('alert');
      await world.pullRequest.set(seed.head, seed.base);

      await fireEvent.click(checkButton());

      await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
      expect(notice()).toBeNull();
   });

   it('clears the failure when a later check’s event brings a newer iteration', async () => {
      const { world, seed } = await cartWorld();
      const { app } = await startApp(world);
      await openTour(app);
      await world.pullRequest.fail('The pull request could not be reached.');
      await fireEvent.click(checkButton());
      await screen.findByRole('alert');

      await pushChecked(world, seed, ['adds'], 3);

      await waitFor(() => expect(notice()?.textContent).toContain('Update 3 is available: 1 stop affected'));
      expect(screen.queryByRole('alert')).toBeNull();
   });

   it('shows no failure whose response arrives after a newer event', async () => {
      const { world, seed } = await cartWorld();
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await world.pullRequest.fail('The pull request could not be reached.');
      const check = transport.holdAnswered('check_iteration');
      await fireEvent.click(checkButton());
      await check.answered;

      await pushChecked(world, seed, ['adds'], 3);
      await waitFor(() => expect(notice()).not.toBeNull());
      await check.release();
      await waitFor(() => expect(checkButton().disabled).toBe(false));
      await settle();

      expect(screen.queryByRole('alert')).toBeNull();
      expect(notice()?.textContent).toContain('Update 3 is available: 1 stop affected');
   });

   it('keeps the failure through an unrelated update of the tour and a reload', async () => {
      const { world, seed } = await cartWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      await world.pullRequest.fail('The pull request could not be reached.');
      await fireEvent.click(checkButton());
      await screen.findByRole('alert');
      await world.clock.advance(1000);
      await world.reviewer.call('save_note', { tourId: seed.tourId, anchor: { kind: 'tour' }, text: 'A concern.' });
      const loads = () => transport.requests.filter((request) => request.operation === 'read_state').length;

      app.selectTour(seed.tourId, 'wholePr');
      await waitFor(() => expect(loads()).toBe(2));
      await settle();

      expect(screen.getByRole('alert').textContent).toBe('The check for a new update could not finish: The pull request could not be reached.');
   });

   it('keeps the failure through a switch to another tour and back', async () => {
      const { world, seed } = await cartWorld();
      await pushChecked(world, seed, ['adds']);
      const claude = await world.claude();
      const other = (await claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/7', familiarity: 'some' })) as { id: string };
      await world.idle();
      const { app, transport } = await startApp(world);
      await openTour(app);
      await world.pullRequest.fail('The pull request could not be reached.');
      await fireEvent.click(checkButton());
      await screen.findByRole('alert');

      app.selectTour(other.id, 'wholePr');
      await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
      app.selectTour(seed.tourId, 'wholePr');
      await waitFor(() => expect(transport.requests.filter((request) => request.operation === 'read_state' && request.input?.tourId === seed.tourId)).toHaveLength(2));
      await settle();

      expect(screen.getByRole('alert').textContent).toBe('The check for a new update could not finish: The pull request could not be reached.');
      expect(notice()?.textContent).toContain('Update 2 is available: 1 stop affected');
   });

   it('hides the failure when an event clears the iteration', async () => {
      const { world, seed } = await cartWorld();
      await pushChecked(world, seed, ['adds']);
      const { app } = await startApp(world);
      await openTour(app);
      await world.pullRequest.fail('The pull request could not be reached.');
      await fireEvent.click(checkButton());
      await screen.findByRole('alert');

      await withdraw(world, seed);

      await waitFor(() => expect(notice()).toBeNull());
      expect(screen.queryByRole('alert')).toBeNull();
   });

   it('shows no failed request whose error arrives after a newer event', async () => {
      const { world, seed } = await cartWorld();
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      transport.fail('check_iteration', { status: 500 });
      const check = transport.holdAnswered('check_iteration');
      await fireEvent.click(checkButton());
      await check.answered;

      await pushChecked(world, seed, ['adds'], 3);
      await waitFor(() => expect(notice()).not.toBeNull());
      await check.release();
      await waitFor(() => expect(checkButton().disabled).toBe(false));
      await settle();

      expect(screen.queryByRole('alert')).toBeNull();
   });

   it('shows a failed request whose error arrives with no event', async () => {
      const { world } = await cartWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      transport.fail('check_iteration', { status: 500 });

      await fireEvent.click(checkButton());

      expect((await screen.findByRole('alert')).textContent).toContain('The check for a new update could not finish');
   });

   it('clears a failed request on a tour whose iteration the page never loaded when an event brings one', async () => {
      const { world, seed } = await cartWorld();
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      const load = transport.holdAnswered('read_state');
      (await openTourList())
         .getAllByRole('button', tourButton)
         .find((button) => button.textContent?.includes(prLabel))!
         .click();
      await load.answered;
      transport.fail('check_iteration', { status: 500 });
      await fireEvent.click(checkButton());
      expect((await screen.findByRole('alert')).textContent).toContain('The check for a new update could not finish');

      await pushChecked(world, seed, ['adds']);

      await waitFor(() => expect(notice()?.textContent).toContain('Update 2 is available: 1 stop affected'));
      expect(screen.queryByRole('alert')).toBeNull();
      await load.release();
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
   });

   it('keeps a newer event when the check’s response arrives after it', async () => {
      const { world, seed } = await cartWorld();
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await pushIteration(world, { base: seed.base, parent: seed.head }, iterationFiles(['adds']), 2);
      const check = transport.holdAnswered('check_iteration');
      await fireEvent.click(checkButton());
      await check.answered;
      await waitFor(() => expect(notice()).not.toBeNull());

      await withdraw(world, seed);
      await waitFor(() => expect(notice()).toBeNull());
      await check.release();
      await waitFor(() => expect(checkButton().disabled).toBe(false));
      await settle();

      expect(notice()).toBeNull();
      expect(app.selectedIterationCheck.failure).toBeNull();
   });
});
