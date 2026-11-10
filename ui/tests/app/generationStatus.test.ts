import { fireEvent, screen, waitFor } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { Tour } from '../../src/lib/types';
import { attachClaude, backend, cartContent, openTour, seedTour, startApp } from '../harness';
import { planPanel, resetPageEachTest, tab, worldOf } from './appPanels';

resetPageEachTest();

const stops = [
   { title: 'Cart adds', kind: 'inspect' },
   { title: 'Line totals', kind: 'inspect' },
] as const;

interface Seed {
   tourId: string;
   stopIds: string[];
}

const unplanned = backend().template('generationStatus-unplanned', async (world) => {
   const { tourId, stopIds } = await seedTour(world);
   return { tourId, stopIds } satisfies Seed;
});

const planned = backend().template('generationStatus-planned', async (world) => {
   const { tourId, stopIds } = await seedTour(world, { chapters: [{ title: 'Cart', stops: [...stops] }] });
   return { tourId, stopIds } satisfies Seed;
});

function statusLine(): string | null {
   const status = screen.getAllByRole('status').find((element) => element.hasAttribute('data-generation-status'));
   return status?.textContent.trim() ?? null;
}

async function openPlanPanel(): Promise<void> {
   await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   await waitFor(() => expect(tab('Plan').getAttribute('aria-selected')).toBe('true'));
}

describe("the top bar's generation status", () => {
   it('shows no line while no tour is selected', async () => {
      const { world } = await worldOf(unplanned);
      await startApp(world);

      expect(statusLine()).toBeNull();
   });

   it('follows an attached session from planning through each stop, and is gone after the last stop', async () => {
      const { world, seed } = await worldOf(unplanned);
      const claude = await attachClaude(world, seed.tourId);
      const { app } = await startApp(world);
      await openTour(app);

      await waitFor(() => expect(statusLine()).toBe('Claude is reading the PR and planning the tour…'));

      const tour = (await claude.call('set_plan', { tourId: seed.tourId, chapters: [{ id: 'c1', title: 'Cart' }], stops: stops.map((stop) => ({ ...stop, chapter: 'c1' })) })) as Tour;
      const [first, second] = tour.plan!.stops.map((stop) => stop.id);
      await waitFor(() => expect(statusLine()).toBe('Claude is writing stop 1 of 2…'));

      await claude.call('put_stop', { tourId: seed.tourId, stopId: first, ...cartContent('The cart adds.') });
      await waitFor(() => expect(statusLine()).toBe('Claude is writing stop 2 of 2…'));

      await claude.call('put_stop', { tourId: seed.tourId, stopId: second, ...cartContent('The total rounds.') });
      await waitFor(() => expect(statusLine()).toBeNull());
   });

   it('says that no session writes the tour and stops the Plan panel’s spinners once the session detaches', async () => {
      const { world, seed } = await worldOf(planned);
      const claude = await attachClaude(world, seed.tourId);
      const { app } = await startApp(world);
      await openTour(app);
      await openPlanPanel();
      await waitFor(() => expect(statusLine()).toBe('Claude is writing stop 1 of 2…'));
      expect(
         planPanel()
            .getAllByRole('button', { name: /Writing/ })
            .filter((row) => row.querySelector('.spinner') !== null),
      ).toHaveLength(2);

      await claude.drop();

      await waitFor(() => expect(statusLine()).toBe('No Claude session is writing this tour'));
      const pending = planPanel().getAllByRole('button', { name: /Not written yet/ });
      expect(pending).toHaveLength(2);
      expect(pending.filter((row) => row.querySelector('.spinner') !== null)).toHaveLength(0);
   });
});
