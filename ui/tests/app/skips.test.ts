import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { skippedIn } from '../../src/lib/stops';
import type { Transport, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openStop, openTour, readTour, seedTour, startApp, type SeededTour } from '../harness';
import { hoverTooltip } from '../tooltip';
import { openTourList, tourButton } from '../tourPicker';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

type Seed = Pick<SeededTour, 'tourId' | 'stopIds'>;

// Three written stops and a pending fourth, so a skip applies to both stages.
const plan = backend().template('skips-plan', async (world) => {
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

const skipTooltip =
   'Next and Back pass over this stop, and the summary lists it as skipped. Claude leaves it out of rewrites for a new familiarity level, and leaves its risk out of PR comments. You can take this back at any time.';
const takeBackTooltip = 'Take the skip back, so Next and Back stop here again.';

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

   await showPlanPanel();
   return started;
}

async function showPlanPanel(): Promise<void> {
   await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   await screen.findByRole('tabpanel', { name: 'Plan' });
}

const planPanel = () => within(screen.getByRole('tabpanel', { name: 'Plan' }));
const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
const navigation = () => screen.getByRole('navigation', { name: 'Stops' });
const navigationText = () => navigation().querySelector('p')!.textContent.replace(/\s+/g, ' ').trim();
const row = (number: number) => planPanel().getByRole('button', { name: new RegExp(`^${number} `) });

function skipWrites(transport: Transport): { stopId: unknown; skipped: unknown }[] {
   return transport.requests.filter((request) => request.operation === 'set_stop_skipped').map(({ input }) => ({ stopId: input?.stopId, skipped: input?.skipped }));
}

async function storedSkips(world: World, tourId: string): Promise<string[]> {
   return skippedIn((await readTour(world, tourId)).plan);
}

// Sets the plan without the stop, as Claude's set_plan does, keeping the IDs of the other stops.
async function planWithout(world: World, tourId: string, stopIds: string[], left: string): Promise<void> {
   const titles = ['Cart adds', 'Line totals', 'Gateway errors', 'Coupons'];
   const claude = await attachClaude(world, tourId);
   await claude.call('set_plan', {
      tourId,
      chapters: [{ id: 'c1', title: 'Cart' }],
      stops: stopIds.flatMap((id, index) => (id === left ? [] : [{ id, title: titles[index], kind: 'inspect', chapter: 'c1' }])),
   });
}

// The helper reads the tooltip through the button's aria-describedby, which is how a screen reader gets it. The button carries that attribute once it is enabled again.
async function expectBothTooltips(button: () => HTMLElement): Promise<void> {
   expect(await hoverTooltip(button())).toBe(skipTooltip);

   await fireEvent.click(button());

   await waitFor(() => expect(button().getAttribute('aria-pressed')).toBe('true'));
   await waitFor(() => expect(button().hasAttribute('disabled')).toBe(false));
   await fireEvent.pointerLeave(button());
   expect(await hoverTooltip(button())).toBe(takeBackTooltip);
}

describe('skipping a stop', () => {
   it('says what a skip does and what taking it back does, in the tooltip of the skip button on a Plan row', async () => {
      const { world } = await planWorld();
      await open(world);

      await expectBothTooltips(() => planPanel().getByRole('button', { name: 'Skip stop 2' }));
   });

   it('says what a skip does and what taking it back does, in the tooltip of the skip button in the Stop panel', async () => {
      const { world, stopIds } = await planWorld();
      await open(world, stopIds[1]!);

      await expectBothTooltips(() => stopPanel().getByRole('button', { name: 'Skip stop' }));
   });

   it('works from the Plan panel with no session, marks the stop as skipped, and unskip clears the mark', async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { transport } = await open(world);

      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 2' }));

      await waitFor(() => expect(row(2).getAttribute('aria-label')).toMatch(/, skipped$/));
      expect(row(2).querySelector('.skipped')?.textContent.trim()).toBe('Skipped');
      expect(row(1).querySelector('.skipped')).toBeNull();
      expect(await storedSkips(world, tourId)).toEqual([stopIds[1]]);
      expect(skipWrites(transport)).toEqual([{ stopId: stopIds[1], skipped: true }]);

      expect(planPanel().getByRole('button', { name: 'Skip stop 2' }).getAttribute('aria-pressed')).toBe('true');
      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 2' }));

      await waitFor(() => expect(row(2).querySelector('.skipped')).toBeNull());
      expect(row(2).getAttribute('aria-label')).not.toMatch(/skipped/);
      expect(planPanel().getByRole('button', { name: 'Skip stop 2' }).getAttribute('aria-pressed')).toBe('false');
      await waitFor(async () => expect(await storedSkips(world, tourId)).toEqual([]));
   });

   it('skips a stop that Claude has not written yet', async () => {
      const { world } = await planWorld();
      await open(world);

      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 4' }));

      await waitFor(() => expect(row(4).querySelector('.skipped')).not.toBeNull());
      expect(row(4).getAttribute('aria-label')).toBe('4 Inspect: Coupons, Not written yet, skipped');
   });

   it('works from the stop under view with no session, keeps the reviewer on the stop, and marks it in the stop navigation', async () => {
      const { world, stopIds } = await planWorld();
      const { app } = await open(world, stopIds[1]!);

      await fireEvent.click(stopPanel().getByRole('button', { name: 'Skip stop' }));

      await waitFor(() => expect(stopPanel().getByRole('button', { name: 'Skip stop' }).getAttribute('aria-pressed')).toBe('true'));
      expect(app.selectedStopId).toBe(stopIds[1]);
      expect(navigationText()).toBe('Stop 2 of 4 · Skipped · Cart · Line totals');
      expect(row(2).querySelector('.skipped')).not.toBeNull();
      await waitFor(() => expect(stopPanel().getByRole('button', { name: 'Skip stop' }).hasAttribute('disabled')).toBe(false));

      await fireEvent.click(stopPanel().getByRole('button', { name: 'Skip stop' }));

      await waitFor(() => expect(stopPanel().getByRole('button', { name: 'Skip stop' }).getAttribute('aria-pressed')).toBe('false'));
      expect(navigationText()).toBe('Stop 2 of 4 · Cart · Line totals');
   });

   it('has next and back pass over a skipped stop, which still opens when the reviewer picks it', async () => {
      const { world, tourId, stopIds } = await planWorld();
      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[1]!, skipped: true });
      const { app } = await open(world, stopIds[0]!);

      await fireEvent.click(within(navigation()).getByRole('button', { name: 'Next stop' }));
      await waitFor(() => expect(app.selectedStopId).toBe(stopIds[2]));
      await fireEvent.click(within(navigation()).getByRole('button', { name: 'Previous stop' }));
      await waitFor(() => expect(app.selectedStopId).toBe(stopIds[0]));

      await fireEvent.click(row(2));
      await waitFor(() => expect(app.selectedStopId).toBe(stopIds[1]));
      expect(navigationText()).toBe('Stop 2 of 4 · Skipped · Cart · Line totals');
   });

   it('goes from next to the summary page when every later stop is skipped', async () => {
      const { world, tourId, stopIds } = await planWorld();
      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[2]!, skipped: true });
      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[3]!, skipped: true });
      const { app } = await open(world, stopIds[1]!);

      await fireEvent.click(within(navigation()).getByRole('button', { name: 'Summary' }));

      await waitFor(() => expect(app.showsSummary).toBe(true));
      expect(within(navigation()).getByRole('button', { name: 'Previous stop' }).hasAttribute('disabled')).toBe(false);
   });

   it('applies a skip from another view of the tour, which the server announces', async () => {
      const { world, tourId, stopIds } = await planWorld();
      await open(world);

      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[2]!, skipped: true });

      await waitFor(() => expect(row(3).querySelector('.skipped')).not.toBeNull());
   });

   it('keeps a newer skip when a load that started before it answers with the older skips', async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { app, transport } = await startApp(world);
      const release = transport.hold('read_state');
      const list = await openTourList();
      list.getAllByRole('button', tourButton)[0]!.click();
      await waitFor(() => expect(transport.requests.some((request) => request.operation === 'read_state')).toBe(true));

      // The later clock makes the skip newer than the load's answer.
      await world.clock.advance(60_000);
      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[1]!, skipped: true });
      await waitFor(() => expect(app.selectedSkippedStops.has(stopIds[1]!)).toBe(true));
      release();
      await waitFor(() => expect(app.selectedPlan).toBeDefined());
      await showPlanPanel();

      expect(row(2).querySelector('.skipped')).not.toBeNull();
   });

   it('keeps the skips of a newer event when the response to an earlier skip arrives after it', async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { transport } = await open(world);
      const release = transport.hold('set_stop_skipped');

      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 2' }));
      await waitFor(() => expect(row(2).querySelector('.skipped')).not.toBeNull());
      await waitFor(async () => expect(await storedSkips(world, tourId)).toEqual([stopIds[1]]));
      // Another view takes the skip back and skips stop 3, later than the held response.
      await world.clock.advance(60_000);
      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[1]!, skipped: false });
      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[2]!, skipped: true });
      await waitFor(() => expect(row(3).querySelector('.skipped')).not.toBeNull());
      release();

      await waitFor(() => expect(planPanel().getByRole('button', { name: 'Skip stop 2' }).hasAttribute('disabled')).toBe(false));
      expect(row(2).querySelector('.skipped')).toBeNull();
      expect(row(3).querySelector('.skipped')).not.toBeNull();
   });

   it('keeps the mark of a skipped stop that a new plan keeps, at its new number, when the plan leaves out another skipped stop', async () => {
      const { world, tourId, stopIds } = await planWorld();
      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[1]!, skipped: true });
      await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[2]!, skipped: true });
      await open(world);

      await planWithout(world, tourId, stopIds, stopIds[1]!);

      await waitFor(() => expect(planPanel().queryAllByRole('button', { name: /^\d+ / })).toHaveLength(3));
      expect(row(2).getAttribute('aria-label')).toBe('2 Inspect: Gateway errors, skipped');
      expect(screen.getByRole('tabpanel', { name: 'Plan' }).querySelectorAll('.skipped')).toHaveLength(1);
      expect(planPanel().getByRole('button', { name: 'Skip stop 2' }).getAttribute('aria-pressed')).toBe('true');
   });

   it("saves a skip of another stop while one stop's skip is saving, and keeps a stop's toggle busy only for its own save", async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { transport } = await open(world);
      const release = transport.hold('set_stop_skipped');

      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 2' }));
      await waitFor(() => expect(planPanel().getByRole('button', { name: 'Skip stop 2' }).hasAttribute('disabled')).toBe(true));
      expect(planPanel().getByRole('button', { name: 'Skip stop 3' }).hasAttribute('disabled')).toBe(false);
      // The server stores stop 2's skip and its event shows it while the response waits. The clock then moves on, so that held response is older than stop 3's write, as on a real clock.
      await waitFor(() => expect(row(2).querySelector('.skipped')).not.toBeNull());
      await world.clock.advance(1000);
      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 2' }));
      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 3' }));

      await waitFor(() =>
         expect(skipWrites(transport)).toEqual([
            { stopId: stopIds[1], skipped: true },
            { stopId: stopIds[2], skipped: true },
         ]),
      );
      release();
      await waitFor(() => expect(planPanel().getByRole('button', { name: 'Skip stop 2' }).hasAttribute('disabled')).toBe(false));
      expect(row(2).querySelector('.skipped')).not.toBeNull();
      await waitFor(() => expect(row(3).querySelector('.skipped')).not.toBeNull());
      expect(await storedSkips(world, tourId)).toEqual([stopIds[1], stopIds[2]]);
   });

   it('takes back from the Stop panel a skip that the Plan panel is still saving, and the late response leaves the stop unskipped', async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { transport } = await open(world, stopIds[1]!);
      const release = transport.hold('set_stop_skipped');

      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 2' }));
      await waitFor(() => expect(stopPanel().getByRole('button', { name: 'Skip stop' }).getAttribute('aria-pressed')).toBe('true'));
      expect(planPanel().getByRole('button', { name: 'Skip stop 2' }).hasAttribute('disabled')).toBe(true);
      await waitFor(async () => expect(await storedSkips(world, tourId)).toEqual([stopIds[1]]));
      // The later clock makes the take-back newer than the held response.
      await world.clock.advance(60_000);
      await fireEvent.click(stopPanel().getByRole('button', { name: 'Skip stop' }));
      await waitFor(() => expect(stopPanel().getByRole('button', { name: 'Skip stop' }).getAttribute('aria-pressed')).toBe('false'));
      await waitFor(async () => expect(await storedSkips(world, tourId)).toEqual([]));
      release();

      await waitFor(() => expect(planPanel().getByRole('button', { name: 'Skip stop 2' }).hasAttribute('disabled')).toBe(false));
      expect(skipWrites(transport)).toEqual([
         { stopId: stopIds[1], skipped: true },
         { stopId: stopIds[1], skipped: false },
      ]);
      expect(stopPanel().getByRole('button', { name: 'Skip stop' }).getAttribute('aria-pressed')).toBe('false');
      expect(row(2).querySelector('.skipped')).toBeNull();
   });

   it("keeps the reason for a refused skip while another stop's skip finishes saving", async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { transport } = await open(world);
      const releaseFirst = transport.hold('set_stop_skipped', 'response');
      const releaseSecond = transport.hold('set_stop_skipped', 'request');

      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 2' }));
      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 3' }));
      await waitFor(() => expect(skipWrites(transport)).toHaveLength(2));
      // Claude drops stop 3 before its skip reaches the server, so the server refuses the skip.
      await planWithout(world, tourId, stopIds, stopIds[2]!);
      await waitFor(() => expect(planPanel().queryAllByRole('button', { name: /^\d+ / })).toHaveLength(3));
      releaseSecond();
      expect((await planPanel().findByRole('alert')).textContent).toContain(`the tour's plan has no stop '${stopIds[2]}' any more.`);
      releaseFirst();

      await waitFor(() => expect(row(2).querySelector('.skipped')).not.toBeNull());
      expect(planPanel().getByRole('alert').textContent).toContain(`the tour's plan has no stop '${stopIds[2]}' any more.`);
      expect(await storedSkips(world, tourId)).toEqual([stopIds[1]]);
   });

   it("shows the server's reason when it refuses a skip of a stop that Claude dropped meanwhile", async () => {
      const { world, tourId, stopIds } = await planWorld();
      const { transport } = await open(world);
      const release = transport.hold('set_stop_skipped', 'request');

      await fireEvent.click(planPanel().getByRole('button', { name: 'Skip stop 2' }));
      await waitFor(() => expect(skipWrites(transport)).toHaveLength(1));
      await planWithout(world, tourId, stopIds, stopIds[1]!);
      await waitFor(() => expect(planPanel().queryAllByRole('button', { name: /^\d+ / })).toHaveLength(3));
      release();

      expect((await planPanel().findByRole('alert')).textContent).toContain(`the tour's plan has no stop '${stopIds[1]}' any more`);
      expect(screen.getByRole('tabpanel', { name: 'Plan' }).querySelectorAll('.skipped')).toHaveLength(0);
   });
});
