import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { Familiarity, Tour } from '../../src/lib/types';
import type { World } from '../backend/backend';
import { backend, cartBase, cartHead, readTour, seedTour, startApp } from '../harness';
import { hoverTooltip } from '../tooltip';
import { openTourList, tourButton, tourSwitcherName } from '../tourPicker';
import { planPanel, resetPageEachTest } from './appPanels';

resetPageEachTest();

/** Commits the cart change and makes it the pull request that every URL resolves to. */
async function cartPullRequest(world: World): Promise<{ base: string; head: string }> {
   const base = await world.repo.commit('main', 'The base', cartBase);
   const head = await world.repo.commit('feature', 'The change', cartHead, base);
   await world.pullRequest.set(head, base);
   return { base, head };
}

/** Creates a tour of the pull request URL through a session of its own, which stays open. */
async function createTour(world: World, prUrl: string, familiarity: Familiarity = 'some'): Promise<Tour> {
   const claude = await world.claude();
   return (await claude.call('create_tour', { prUrl, familiarity })) as Tour;
}

const listLoads = (requests: readonly { operation: string | null }[]) => requests.filter((request) => request.operation === 'list_tours').length;

describe('the tour list and the plan', () => {
   it('lists each tour with the label the server gives it, its URL as its tooltip and its familiarity, newest first', async () => {
      const world = await backend().world();
      await cartPullRequest(world);
      await createTour(world, 'https://platform.test/web/pull/1', 'new');
      await world.clock.advance(1000);
      await createTour(world, 'https://platform.test/api/pull/7');
      await world.idle();

      await startApp(world);

      const list = await openTourList();
      const items = await waitFor(() => {
         const found = list.getAllByRole('button', tourButton);
         expect(found).toHaveLength(2);
         return found;
      });
      expect(items[0]!.textContent).toContain('api #7');
      expect(await hoverTooltip(items[0]!)).toBe('https://platform.test/api/pull/7');
      expect(items[0]!.textContent).toContain('Knows some of the code');
      expect(items[1]!.textContent).toContain('web #1');
      expect(items[1]!.textContent).toContain('New to the code');
   });

   it("shows the selected tour's label on the tour switcher, with the label and URL as its tooltip", async () => {
      const world = await backend().world();
      await cartPullRequest(world);
      await createTour(world, 'https://platform.test/web/pull/9');
      await startApp(world);
      const switcher = screen.getByRole('button', { name: 'Choose a tour' });
      expect(switcher.textContent.trim()).toBe('Choose a tour');

      (await (await openTourList()).findByRole('button', tourButton)).click();

      await waitFor(() => expect(switcher.getAttribute('aria-label')).toBe('Tour: web #9, choose another'));
      expect(switcher.textContent.trim()).toBe('web #9');
      expect(await hoverTooltip(switcher)).toBe('web #9\nhttps://platform.test/web/pull/9');
   });

   it('chooses a tour from the switcher, which closes and marks the chosen tour when it opens again', async () => {
      const world = await backend().world();
      await cartPullRequest(world);
      const first = await createTour(world, 'https://platform.test/web/pull/1');
      await createTour(world, 'https://platform.test/api/pull/2');
      const { app } = await startApp(world);
      const picker = screen.getByRole('button', { name: tourSwitcherName });

      fireEvent.click(await (await openTourList()).findByRole('button', { name: /#1/ }));

      await waitFor(() => expect(app.selectedTourId).toBe(first.id));
      expect(await within(screen.getByRole('banner')).findByText('web #1')).toBeTruthy();
      await waitFor(() => expect(screen.queryByRole('navigation', { name: 'Tours' })).toBeNull());
      expect(picker.getAttribute('aria-expanded')).toBe('false');
      const list = await openTourList();
      expect(list.getByRole('button', { name: /#1/ }).getAttribute('aria-current')).toBe('true');
      expect(list.getByRole('button', { name: /#2/ }).getAttribute('aria-current')).toBeNull();

      fireEvent.click(list.getByRole('button', { name: /#2/ }));

      await waitFor(() => expect(picker.getAttribute('aria-label')).toBe('Tour: api #2, choose another'));
      expect(picker.textContent.trim()).toBe('api #2');
   });

   it('adds a tour from a tourCreated event without a reload', async () => {
      const world = await backend().world();
      await cartPullRequest(world);
      const { transport } = await startApp(world);
      await (await openTourList()).findByText(/No tours yet/);
      // jsdom leaves a list that an event changes while it is open unpositioned and hidden, so it closes first.
      await fireEvent.click(screen.getByRole('button', { name: tourSwitcherName }));
      await waitFor(() => expect(screen.queryByRole('navigation', { name: 'Tours' })).toBeNull());
      const loads = listLoads(transport.requests);

      await createTour(world, 'https://platform.test/web/pull/1');

      expect(await (await openTourList()).findByText('web #1')).toBeTruthy();
      expect(listLoads(transport.requests)).toBe(loads);
   });

   it('keeps each tour pin current from tourPinned events without a reload', async () => {
      const world = await backend().world();
      const { base, head } = await cartPullRequest(world);
      // The held resolve keeps the pin in progress until the release.
      await world.pullRequest.hold();
      const tour = await createTour(world, 'https://platform.test/web/pull/1');
      const { app, transport } = await startApp(world);
      await (await openTourList()).findByRole('button', tourButton);
      expect(app.tours[0]?.pin.state).not.toBe('pinned');
      const loads = listLoads(transport.requests);

      await world.pullRequest.release();
      await world.idle();

      await waitFor(() => expect(app.tours[0]?.pin).toMatchObject({ state: 'pinned', mergeBase: base, head, iteration: 1 }));
      const summary = (await world.reviewer.call('list_tours', {})).tours.find((candidate) => candidate.id === tour.id)!;
      expect(app.tours[0]?.pin).toEqual(summary.pin);
      expect(app.tours[0]?.updatedAt).toBe(summary.updatedAt);
      expect(listLoads(transport.requests)).toBe(loads);
   });

   it('shows the selected tour plan: chapters in order, each with its stops and kinds in order', async () => {
      const world = await backend().world();
      await seedTour(world, {
         chapters: [
            { title: 'Orientation', stops: [{ title: 'What this PR changes', kind: 'overview' }] },
            {
               title: 'Pricing',
               stops: [
                  { title: 'The new discount rule', kind: 'inspect' },
                  { title: 'Rounding on stacked discounts', kind: 'risk' },
               ],
            },
         ],
      });
      await startApp(world);

      (await (await openTourList()).findByRole('button', tourButton)).click();
      await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));

      const chapters = await planPanel().findAllByRole('heading');
      expect(chapters.map((heading) => heading.textContent)).toEqual(['Orientation', 'Pricing']);
      const rows = planPanel()
         .getAllByRole('listitem')
         .filter((item) => item.querySelector('h3') === null);
      expect(rows.map((row) => row.querySelector('button')!.getAttribute('aria-label') ?? row.textContent.trim())).toEqual([
         '1 Overview: What this PR changes, Writing',
         '2 Inspect: The new discount rule, Writing',
         '3 Risk: Rounding on stacked discounts, Writing',
         'Summary',
      ]);
   });

   it('fills the plan panel from a planSet event without a reload, and updates the stop count', async () => {
      const world = await backend().world();
      const { tourId, claude } = await seedTour(world);
      const { transport } = await startApp(world);
      (await (await openTourList()).findByRole('button', tourButton)).click();
      await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
      await planPanel().findByText(/has not written the plan/);
      const tourLoads = () => transport.requests.filter((request) => request.operation === 'read_state' && request.input?.tourId === tourId).length;
      const loads = tourLoads();

      await claude!.call('set_plan', {
         tourId,
         chapters: [{ id: 'intro', title: 'Introduction' }],
         stops: [
            { title: 'A map of the change', kind: 'overview', chapter: 'intro' },
            { title: 'Why it was needed', kind: 'context', chapter: 'intro' },
         ],
      });

      expect(await planPanel().findByText('A map of the change')).toBeTruthy();
      expect(planPanel().getByText('Why it was needed')).toBeTruthy();
      expect((await openTourList()).getByRole('button', tourButton).textContent).toContain('2 stops');
      expect(tourLoads()).toBe(loads);
   });

   it('replaces the plan when a newer plan is set', async () => {
      const world = await backend().world();
      const { tourId, claude } = await seedTour(world, { chapters: [{ title: 'First draft', stops: [{ title: 'Old stop', kind: 'inspect' }] }] });
      await startApp(world);
      (await (await openTourList()).findByRole('button', tourButton)).click();
      await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
      await planPanel().findByText('Old stop');

      await claude!.call('set_plan', { tourId, chapters: [{ id: 'b', title: 'Second draft' }], stops: [{ title: 'New stop', kind: 'question', chapter: 'b' }] });

      expect(await planPanel().findByText('New stop')).toBeTruthy();
      expect(planPanel().queryByText('Old stop')).toBeNull();
      expect((await readTour(world, tourId)).plan!.stops.map((stop) => stop.title)).toEqual(['New stop']);
   });
});
