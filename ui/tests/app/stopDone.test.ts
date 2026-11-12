import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { versionOf } from '../../src/lib/stops';
import type { Transport, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openStop, openTour, seedTour, startApp, type SeededTour } from '../harness';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

type Seed = Pick<SeededTour, 'tourId' | 'stopIds'>;

// Three written stops and a pending fourth, which has nothing to mark done.
const plan = backend().template('stopDone-plan', async (world) => {
   const seeded = await seedTour(world, {
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'Cart adds', kind: 'inspect', content: cartContent('Cart adds.') },
               { title: 'Line totals', kind: 'inspect', content: cartContent('Line totals.') },
               { title: 'Gateway errors', kind: 'inspect', content: cartContent('Gateway errors.') },
               { title: 'Coupons', kind: 'inspect' },
            ],
         },
      ],
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds } satisfies Seed;
});

async function planWorld(): Promise<{ world: World } & Seed> {
   const world = await backend().world({ template: plan });
   return { world, ...(world.seed as Seed) };
}

// Opens the tour with the stop open, or none, and shows the Plan panel.
async function open(world: World, stopId: string | null = null): Promise<{ app: LotsenApp; transport: Transport }> {
   const started = await startApp(world);
   await openTour(started.app);
   if (stopId !== null) {
      await openStop(started.app, stopId);
   }

   await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   await screen.findByRole('tabpanel', { name: 'Plan' });
   return started;
}

// Writes the stop's next version, as Claude's put_stop does.
async function revise(world: World, tourId: string, stopId: string): Promise<void> {
   const claude = await attachClaude(world, tourId);
   await world.clock.advance(1000);
   await claude.call('put_stop', { tourId, stopId, ...cartContent('Revised.') });
}

const planPanel = () => within(screen.getByRole('tabpanel', { name: 'Plan' }));
const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
const checkbox = (number: number) => planPanel().getByRole('checkbox', { name: new RegExp(`^Stop ${number} done`) });
const row = (number: number) => planPanel().getByRole('button', { name: new RegExp(`^${number} `) });
const doneLine = () =>
   planPanel()
      .getByText(/ of \d+ done$/)
      .textContent.trim();

function doneWrites(transport: Transport): { stopId: unknown; version: unknown }[] {
   return transport.requests.filter((request) => request.operation === 'set_stop_done').map(({ input }) => ({ stopId: input?.stopId, version: input?.version }));
}

describe('marking a stop done', () => {
   it('marks the current version of a stop that is not done, and counts it', async () => {
      const { world, stopIds } = await planWorld();
      const { transport } = await open(world);
      expect(doneLine()).toBe('0 of 4 done');

      await fireEvent.click(checkbox(2));

      await waitFor(() => expect(checkbox(2).getAttribute('aria-checked')).toBe('true'));
      expect(doneWrites(transport)).toEqual([{ stopId: stopIds[1], version: 1 }]);
      expect(doneLine()).toBe('1 of 4 done');
      expect(row(2).getAttribute('aria-label')).toBe('2 Inspect: Line totals, done');
   });

   it('takes the mark back from a done stop', async () => {
      const { world, tourId, stopIds } = await planWorld();
      await world.reviewer.call('set_stop_done', { tourId, stopId: stopIds[1]!, version: 1 });
      const { transport } = await open(world);
      expect(checkbox(2).getAttribute('aria-checked')).toBe('true');

      await fireEvent.click(checkbox(2));

      await waitFor(() => expect(checkbox(2).getAttribute('aria-checked')).toBe('false'));
      expect(doneWrites(transport)).toEqual([{ stopId: stopIds[1], version: null }]);
      expect(doneLine()).toBe('0 of 4 done');
   });

   it('shows a stop done on an earlier version as unchecked, names that version, and marks the current version on a click', async () => {
      const { world, tourId, stopIds } = await planWorld();
      await world.reviewer.call('set_stop_done', { tourId, stopId: stopIds[1]!, version: 1 });
      await revise(world, tourId, stopIds[1]!);
      const { transport } = await open(world);

      expect(checkbox(2).getAttribute('aria-checked')).toBe('false');
      expect(checkbox(2).getAttribute('aria-label')).toBe('Stop 2 done. Done on version 1. Claude revised it since. Mark the current version done.');
      expect(row(2).getAttribute('aria-label')).toMatch(/, done on version 1$/);
      expect(doneLine()).toBe('0 of 4 done');

      await fireEvent.click(checkbox(2));

      await waitFor(() => expect(checkbox(2).getAttribute('aria-checked')).toBe('true'));
      expect(doneWrites(transport)).toEqual([{ stopId: stopIds[1], version: 2 }]);
      expect(doneLine()).toBe('1 of 4 done');
   });

   it('marks the version that the stop under view shows after a revision that the reviewer did not ask for, and a second click takes the mark back', async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { app, transport } = await open(world, stopIds[1]!);

      await revise(world, tourId, stopIds[1]!);
      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(versionOf(app.selectedStop)).toBe(1);
      expect(checkbox(2).getAttribute('aria-label')).toBe('Stop 2 done. Mark version 1 done. Claude revised it since.');
      await fireEvent.click(checkbox(2));

      await waitFor(() => expect(row(2).getAttribute('aria-label')).toMatch(/, done on version 1$/));
      expect(checkbox(2).getAttribute('aria-checked')).toBe('false');
      expect(checkbox(2).getAttribute('aria-label')).toBe('Stop 2 done. Done on version 1. Claude revised it since. Take the mark back.');
      await waitFor(() => expect(checkbox(2).hasAttribute('disabled')).toBe(false));
      await fireEvent.click(checkbox(2));

      await waitFor(() => expect(row(2).getAttribute('aria-label')).not.toMatch(/done/));
      expect(doneWrites(transport)).toEqual([
         { stopId: stopIds[1], version: 1 },
         { stopId: stopIds[1], version: null },
      ]);
   });

   it('offers no checkbox on a pending stop', async () => {
      const { world } = await planWorld();
      await open(world);

      expect(planPanel().queryByRole('checkbox', { name: /^Stop 4 done/ })).toBeNull();
      expect(planPanel().getAllByRole('checkbox')).toHaveLength(3);
   });

   it('offers Mark done in the Stop panel on the current version alone, and marks that version', async () => {
      const { world, tourId, stopIds } = await planWorld();
      await revise(world, tourId, stopIds[1]!);
      const { app, transport } = await open(world, stopIds[1]!);
      const markDone = () => stopPanel().queryByRole('button', { name: 'Mark done' });
      expect(markDone()!.getAttribute('aria-pressed')).toBe('false');

      app.openVersion(1);

      await waitFor(() => expect(markDone()).toBeNull());
      app.openVersion(2);
      await waitFor(() => expect(markDone()).not.toBeNull());
      await fireEvent.click(markDone()!);
      await waitFor(() => expect(markDone()!.getAttribute('aria-pressed')).toBe('true'));
      expect(doneWrites(transport)).toEqual([{ stopId: stopIds[1], version: 2 }]);
      await openStop(app, stopIds[3]!);
      expect(markDone()).toBeNull();
   });

   it("shows the server's reason when it refuses a mark of a stop that Claude dropped meanwhile", async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { transport } = await open(world);
      const release = transport.hold('set_stop_done', 'request');

      await fireEvent.click(checkbox(2));
      await waitFor(() => expect(doneWrites(transport)).toHaveLength(1));
      const claude = await attachClaude(world, tourId);
      const titles = ['Cart adds', 'Line totals', 'Gateway errors', 'Coupons'];
      await claude.call('set_plan', {
         tourId,
         chapters: [{ id: 'c1', title: 'Cart' }],
         stops: stopIds.flatMap((id, index) => (index === 1 ? [] : [{ id, title: titles[index], kind: 'inspect', chapter: 'c1' }])),
      });
      await waitFor(() => expect(planPanel().queryAllByRole('button', { name: /^\d+ / })).toHaveLength(3));
      release();

      expect((await planPanel().findByRole('alert')).textContent).toContain(`the tour's plan has no stop '${stopIds[1]}' any more`);
   });

   it('asks for a tour first when none is selected', async () => {
      const { world, stopIds } = await planWorld();
      const { app, transport } = await startApp(world);

      expect(await app.setStopDone(stopIds[1]!, 1)).toBe('Open a tour first.');
      expect(doneWrites(transport)).toEqual([]);
   });
});
