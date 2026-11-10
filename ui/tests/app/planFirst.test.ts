import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { messagesOf } from '../../src/lib/threadView';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { attachClaude, backend, openStop, openTour, readTour, seedTour, startApp, type SeededTour } from '../harness';
import { dragRow } from '../stopOrderDrag';
import { openTourList, tourButton } from '../tourPicker';
import { closePlanMenu, isItemDisabled, openPlanMenu, planActions, planMenuButton, planMenuItem, reasonOf } from './planMenu';
import { openThreadView } from './threadView';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

type Seed = Pick<SeededTour, 'tourId' | 'stopIds'>;

const titles = ['Cart adds', 'Line totals', 'Checkout'];
const plannedStops = titles.map((title) => ({ title, kind: 'inspect' as const }));

// A plan-first tour whose plan of three pending stops waits for the reviewer's approval.
const waiting = backend().template('planFirst-waiting', async (world) => {
   const seeded = await seedTour(world, { planFirst: true, chapters: [{ title: 'Cart', stops: plannedStops }] });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds } satisfies Seed;
});

// A plan-first tour before Claude sets its plan.
const unplanned = backend().template('planFirst-unplanned', async (world) => {
   const seeded = await seedTour(world, { planFirst: true });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds } satisfies Seed;
});

async function waitingWorld(): Promise<{ world: World } & Seed> {
   const world = await backend().world({ template: waiting });
   return { world, ...(world.seed as Seed) };
}

async function boundWorld(): Promise<{ world: World; claude: ClaudeSession } & Seed> {
   const seeded = await waitingWorld();
   return { ...seeded, claude: await attachClaude(seeded.world, seeded.tourId) };
}

// Creates a second tour, of the pull request that the label `shop #43` names, with the session that the plan of the first tour waits on.
async function secondTour(world: World, claude: ClaudeSession, options: { planFirst: boolean }): Promise<Seed> {
   const tour = (await claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/43', familiarity: 'some', planFirst: options.planFirst })) as { id: string };
   await world.idle();
   const planned = (await claude.call('set_plan', { tourId: tour.id, chapters: [{ id: 'c1', title: 'Cart' }], stops: plannedStops.map((stop) => ({ ...stop, chapter: 'c1' })) })) as {
      plan: { stops: { id: string }[] };
   };
   return { tourId: tour.id, stopIds: planned.plan.stops.map((stop) => stop.id) };
}

// Sets the plan as Claude's set_plan does, keeping the IDs of the stops that the entries name.
async function setPlan(claude: ClaudeSession, tourId: string, stops: { id?: string; title: string }[]): Promise<void> {
   await claude.call('set_plan', { tourId, chapters: [{ id: 'c1', title: 'Cart' }], stops: stops.map((stop) => ({ ...stop, kind: 'inspect', chapter: 'c1' })) });
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

// Opens the tour of the label from the tour list, and shows its Plan panel, which each tour opens on its own.
async function choose(label: string): Promise<void> {
   const list = await openTourList();
   list
      .getAllByRole('button', tourButton)
      .find((button) => button.textContent?.includes(label))!
      .click();
   await waitFor(() => expect(screen.getByRole('button', { name: `Tour: ${label}, choose another` })).toBeTruthy());
   await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   await screen.findByRole('tabpanel', { name: 'Plan' });
}

const panel = () => within(screen.getByRole('tabpanel', { name: 'Plan' }));

const proposal = () => within(panel().getByRole('region', { name: 'Plan proposal' }));

const proposedRows = () => within(proposal().getByRole('list', { name: 'Proposed order' })).getAllByRole('listitem');

const approve = () => proposal().getByRole('button', { name: 'Approve plan' }) as HTMLButtonElement;

function sentApprovals(transport: Transport): Record<string, unknown>[] {
   return transport.requests
      .filter((request) => request.operation === 'approve_plan')
      .map(({ input }) => {
         const rest = { ...input };
         delete rest.tourId;
         return rest;
      });
}

describe("a plan that waits for the reviewer's approval", () => {
   it('says in its intro that Claude still writes a stop the reviewer skips', async () => {
      const { world } = await boundWorld();
      await open(world);

      expect(
         proposal().getByText(
            'Claude proposes this plan and writes no stop until you approve it. Change it here, skip stops in the list, then approve. Claude still writes a stop you skip, so you can take the skip back later.',
         ),
      ).toBeTruthy();
   });

   it('tags the words box as optional and says what an empty box does', async () => {
      const { world } = await boundWorld();
      await open(world);

      const box = (await waitFor(() => proposal().getByRole('textbox', { name: 'Anything Claude should know about the plan? Optional' }))) as HTMLTextAreaElement;

      expect(box.placeholder).toBe('Leave it empty and Claude writes the stops as planned.');
   });

   it('shows as a proposal that the reviewer reorders, skips, gives a level and words, and Approve sends them', async () => {
      const { world, stopIds } = await boundWorld();
      const [s1, s2, s3] = stopIds;
      const { app, transport } = await open(world);

      expect(panel().queryByRole('region', { name: 'Plan actions' })).toBeNull();
      expect(panel().queryByRole('button', { name: 'Plan actions' })).toBeNull();
      for (const name of ['Familiar with the code', 'Knows some of the code', 'New to the code']) {
         expect(proposal().getByRole('button', { name })).toBeTruthy();
      }

      expect(proposal().queryByText('Claude gets your order of the stops with the approval.')).toBeNull();
      await fireEvent.click(proposal().getByRole('button', { name: 'Move “Cart adds” down' }));
      expect(proposal().getByText('Claude gets your order of the stops with the approval.')).toBeTruthy();
      await fireEvent.click(panel().getByRole('button', { name: 'Skip stop 3' }));
      await waitFor(() => expect(app.selectedSkippedStops.has(s3!)).toBe(true));
      await waitFor(() => expect(approve().disabled).toBe(false));
      await fireEvent.click(proposal().getByRole('button', { name: 'New to the code' }));
      await fireEvent.input(proposal().getByRole('textbox', { name: 'Anything Claude should know about the plan? Optional' }), { target: { value: 'Keep the checkout short.' } });
      await fireEvent.click(approve());

      await waitFor(() => expect(sentApprovals(transport)).toEqual([{ stopIds: [s2, s1, s3], skippedStops: [s3], familiarity: 'new', text: 'Keep the checkout short.' }]));
      await waitFor(() => expect(panel().queryByRole('region', { name: 'Plan proposal' })).toBeNull());
      expect(await panel().findByText('Claude is writing the stops of the plan you approved. You can change the level once Claude replies.')).toBeTruthy();
      expect(app.selectedFamiliarity).toBe('new');
      expect(app.selectedPlanWaits).toBe(false);
      expect(app.selectedThreads).toEqual([
         expect.objectContaining({
            messages: [expect.objectContaining({ text: 'Keep the checkout short.' })],
            decision: { kind: 'planApproved', stopIds: [s2, s1, s3], skippedStops: [s3], familiarity: 'new' },
         }),
      ]);
   });

   it("sends the plan's order, the tour's level and no words when the reviewer changes nothing", async () => {
      const { world, stopIds } = await boundWorld();
      const { transport } = await open(world);

      await fireEvent.click(approve());

      await waitFor(() => expect(sentApprovals(transport)).toEqual([{ stopIds, skippedStops: [], familiarity: 'some' }]));
   });

   it('shows the order editor and its skip toggles from the start, with no Reorder step and no chapter headings', async () => {
      const { world } = await boundWorld();
      await open(world);

      expect(proposal().queryByRole('button', { name: 'Reorder stops' })).toBeNull();
      expect(proposedRows().map((row) => row.querySelector('.title')!.textContent)).toEqual(titles);
      expect(proposedRows().map((row) => row.querySelector('.chapter')!.textContent)).toEqual(['Cart', 'Cart', 'Cart']);
      expect(proposal().getByRole('button', { name: 'Skip stop 1' })).toBeTruthy();
      expect(proposal().getByRole('button', { name: 'Skip stop 3' })).toBeTruthy();
      expect(panel().queryByRole('heading', { name: 'Cart' })).toBeNull();
   });

   it('opens no stop from a row, because Claude has written none yet', async () => {
      const { world } = await boundWorld();
      const { app } = await open(world);

      await fireEvent.click(proposedRows()[1]!.querySelector('.title')!);

      expect(app.selectedStopId).toBeNull();
      expect(panel().queryByRole('button', { name: /^2 Inspect: Line totals/ })).toBeNull();
   });

   it("names a row's skip toggle after the row's current number, and skips the stop on that row", async () => {
      const { world, stopIds } = await boundWorld();
      const { app } = await open(world);

      await fireEvent.click(proposal().getByRole('button', { name: 'Move “Cart adds” down' }));
      await fireEvent.click(proposal().getByRole('button', { name: 'Skip stop 2' }));

      await waitFor(() => expect(app.selectedSkippedStops.has(stopIds[0]!)).toBe(true));
      expect(proposedRows()[1]!.classList.contains('skipped-row')).toBe(true);
      expect(within(proposedRows()[1]!).getByText('Skipped')).toBeTruthy();
      expect(within(proposedRows()[0]!).queryByText('Skipped')).toBeNull();
   });

   it('shows a refused skip in the proposal', async () => {
      const { world } = await boundWorld();
      const { transport } = await open(world);
      transport.fail('set_stop_skipped', { status: 500 });

      await fireEvent.click(proposal().getByRole('button', { name: 'Skip stop 1' }));

      expect(await proposal().findByRole('alert')).toBeTruthy();
   });

   it('sends the order of a drag with the approval, and shows the hint once the order differs', async () => {
      const { world, stopIds } = await boundWorld();
      const [s1, s2, s3] = stopIds;
      const { transport } = await open(world);

      await dragRow(proposedRows()[2]!, proposedRows()[0]!, 'before');
      await waitFor(() => expect(proposedRows().map((row) => row.querySelector('.number')!.textContent)).toEqual(['1', '2', '3']));
      expect(proposedRows().map((row) => row.querySelector('.title')!.textContent)).toEqual(['Checkout', 'Cart adds', 'Line totals']);
      expect(proposal().getByText('Claude gets your order of the stops with the approval.')).toBeTruthy();
      await fireEvent.click(approve());

      await waitFor(() => expect(sentApprovals(transport)).toEqual([expect.objectContaining({ stopIds: [s3, s1, s2] })]));
   });

   it("disables Approve with the server's reason while no session is attached, and a press sends nothing", async () => {
      const { world } = await waitingWorld();
      const { transport } = await open(world);

      await waitFor(() => expect(approve().disabled).toBe(true));
      expect(proposal().getByText(/is attached to no Claude Code session/)).toBeTruthy();
      await fireEvent.click(approve());
      expect(sentApprovals(transport)).toEqual([]);
   });

   it('holds Approve while a skip saves, so the approval sends the stored skips', async () => {
      const { world, stopIds } = await boundWorld();
      const { transport } = await open(world);
      const release = transport.hold('set_stop_skipped');

      await fireEvent.click(panel().getByRole('button', { name: 'Skip stop 3' }));
      await waitFor(() => expect(approve().disabled).toBe(true));
      await fireEvent.click(approve());
      expect(sentApprovals(transport)).toEqual([]);
      release();
      await waitFor(() => expect(approve().disabled).toBe(false));
      await fireEvent.click(approve());

      await waitFor(() => expect(sentApprovals(transport)).toEqual([expect.objectContaining({ skippedStops: [stopIds[2]] })]));
   });

   it('holds Approve while a skip from the Stop panel saves', async () => {
      const { world, stopIds } = await boundWorld();
      const { transport } = await open(world, stopIds[0]!);
      const release = transport.hold('set_stop_skipped');

      await fireEvent.click(within(screen.getByRole('region', { name: 'Stop' })).getByRole('button', { name: 'Skip stop' }));
      await waitFor(() => expect(approve().disabled).toBe(true));
      release();
      await waitFor(() => expect(approve().disabled).toBe(false));
      await fireEvent.click(approve());

      await waitFor(() => expect(sentApprovals(transport)).toEqual([expect.objectContaining({ skippedStops: [stopIds[0]] })]));
   });

   it('holds Approve while a skip started before a switch to another tour and back saves', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const other = await secondTour(world, claude, { planFirst: true });
      const { app, transport } = await open(world);
      const release = transport.hold('set_stop_skipped');

      await fireEvent.click(panel().getByRole('button', { name: 'Skip stop 3' }));
      await choose('shop #43');
      await waitFor(() => expect(app.selectedTourId).toBe(other.tourId));
      await waitFor(() => expect(approve().disabled).toBe(false));
      await choose('shop #42');
      await waitFor(() => expect(app.selectedTourId).toBe(tourId));

      await waitFor(() => expect(approve().disabled).toBe(true));
      release();
      await waitFor(() => expect(approve().disabled).toBe(false));
      expect(app.selectedSkippedStops.has(stopIds[2]!)).toBe(true);
   });

   it('becomes approvable when a session attaches', async () => {
      const { world, tourId } = await waitingWorld();
      await open(world);
      await waitFor(() => expect(approve().disabled).toBe(true));

      await attachClaude(world, tourId);

      await waitFor(() => expect(approve().disabled).toBe(false));
   });

   it("shows the server's refusal and keeps the proposal", async () => {
      const { world, claude } = await boundWorld();
      const { app, transport } = await open(world);
      const release = transport.hold('approve_plan', 'request');

      await fireEvent.click(approve());
      await waitFor(() => expect(sentApprovals(transport)).toHaveLength(1));
      // The session ends before the approval reaches the server, which then has no session to give it to.
      await claude.drop();
      release();

      expect((await proposal().findByRole('alert')).textContent).toContain('is attached to no Claude Code session');
      expect(app.selectedPlanWaits).toBe(true);
   });

   it("starts the proposed order again from Claude's plan when Claude adds a stop while the reviewer edits", async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const { app, transport } = await open(world);
      await fireEvent.click(proposal().getByRole('button', { name: 'Move “Cart adds” down' }));
      await fireEvent.click(proposal().getByRole('button', { name: 'New to the code' }));

      await setPlan(claude, tourId, [...stopIds.map((id, index) => ({ id, title: titles[index]! })), { title: 'Gateway errors' }]);
      await waitFor(() => expect(app.selectedPlan?.plan?.stops).toHaveLength(4));
      const planned = app.selectedPlan!.plan!.stops.map((stop) => stop.id);
      await fireEvent.click(approve());

      await waitFor(() => expect(sentApprovals(transport)).toEqual([expect.objectContaining({ stopIds: planned, familiarity: 'new' })]));
      expect(planned.slice(0, 3)).toEqual(stopIds);
   });

   it('keeps the proposed order and level through a plan event with the same stops and a reload after the stream reconnects', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const [s1, s2, s3] = stopIds;
      const { app, transport } = await open(world);
      await fireEvent.click(proposal().getByRole('button', { name: 'Move “Cart adds” down' }));
      await fireEvent.click(proposal().getByRole('button', { name: 'New to the code' }));

      await setPlan(claude, tourId, [
         { id: s1!, title: 'Cart adds' },
         { id: s2!, title: 'Line totals' },
         { id: s3!, title: 'Checkout totals' },
      ]);
      await waitFor(() => expect(app.selectedPlan?.plan?.stops[2]?.title).toBe('Checkout totals'));
      const streams = () => transport.requests.filter((request) => request.path === '/api/events').length;
      transport.cutEventStreams();
      await waitFor(() => expect(streams()).toBe(2));
      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      await fireEvent.click(approve());

      await waitFor(() => expect(sentApprovals(transport)).toEqual([expect.objectContaining({ stopIds: [s2, s1, s3], familiarity: 'new' })]));
   });

   it('shows once Claude sets the plan of a plan-first tour', async () => {
      const world = await backend().world({ template: unplanned });
      const { tourId } = world.seed as Seed;
      const claude = await attachClaude(world, tourId);
      await open(world);
      expect(panel().getByText('Claude has not written the plan yet.')).toBeTruthy();

      await setPlan(
         claude,
         tourId,
         titles.map((title) => ({ title })),
      );

      expect(await panel().findByRole('region', { name: 'Plan proposal' })).toBeTruthy();
   });

   it('offers no level control before Claude sets the plan, because the level goes with the approval', async () => {
      const world = await backend().world({ template: unplanned });
      await attachClaude(world, (world.seed as Seed).tourId);
      await open(world);

      expect(panel().getByText('Claude has not written the plan yet.')).toBeTruthy();
      expect(planMenuButton()).toBeNull();
   });

   it('keeps the level control disabled with the reason while the approval waits, and enables it once Claude replies', async () => {
      const { world, tourId, claude } = await boundWorld();
      await open(world);

      await fireEvent.click(approve());
      await waitFor(() => expect(panel().queryByRole('region', { name: 'Plan proposal' })).toBeNull());
      expect((await planActions().findByRole('status')).textContent).toBe('Claude is writing the stops of the plan you approved. You can change the level once Claude replies.');
      const waitingMenu = await openPlanMenu();
      expect(isItemDisabled(planMenuItem(waitingMenu, 'Re-plan…'))).toBe(true);
      await closePlanMenu();
      const question = await claude.next('question');
      await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Wrote the stops.' });

      const repliedMenu = await waitFor(async () => {
         const menu = await openPlanMenu();
         if (isItemDisabled(planMenuItem(menu, 'Re-plan…'))) {
            await closePlanMenu();
            throw new Error('Re-plan… is still disabled.');
         }

         return menu;
      });
      expect(planMenuItem(repliedMenu, 'Re-plan…')).toBeTruthy();
      await closePlanMenu();
   });

   it('shows the waiting approval once in the Plan panel, with one link to its thread', async () => {
      const { world, tourId } = await boundWorld();
      const { app } = await open(world);

      await fireEvent.click(approve());
      await waitFor(() => expect(panel().getAllByText(/^Claude is writing the stops of the plan you approved\./)).toHaveLength(1));
      const links = panel().getAllByRole('button', { name: 'Show the thread' });
      expect(links).toHaveLength(1);
      await fireEvent.click(links[0]!);
      const thread = (await readTour(world, tourId)).threads[0]!;
      await waitFor(() => expect(app.openedThread).toEqual({ id: thread.id }));
   });

   it("disables Re-plan and Reorder with the approval's reason while it waits, which names no thread ID", async () => {
      const { world, tourId } = await boundWorld();
      await open(world);

      await fireEvent.click(approve());
      await waitFor(() => expect(planActions().getByText(/Claude still writes the stops of the plan that you approved/)).toBeTruthy());
      const menu = await openPlanMenu();
      const replan = planMenuItem(menu, 'Re-plan…');
      expect(isItemDisabled(replan)).toBe(true);
      const reason = reasonOf(replan) ?? '';
      expect(reason).toBe("Claude still writes the stops of the plan that you approved. Wait for Claude's reply, or cancel that request in its thread, then ask again.");
      expect(planMenuItem(menu, 'Reorder…').getAttribute('aria-describedby')).toBe(replan.getAttribute('aria-describedby'));
      expect(reason).not.toContain((await readTour(world, tourId)).threads[0]!.id);
      await closePlanMenu();
   });

   it('goes away when the plan is approved in another page', async () => {
      const { world, tourId, stopIds } = await boundWorld();
      await open(world);

      await world.reviewer.call('approve_plan', { tourId, stopIds, skippedStops: [], familiarity: 'some' });

      await waitFor(() => expect(panel().queryByRole('region', { name: 'Plan proposal' })).toBeNull());
      expect(planMenuButton()).not.toBeNull();
   });

   it("keeps a newer level from an event when the approval's response arrives later", async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const { app, transport } = await open(world);
      const release = transport.hold('approve_plan');

      const approved = app.approvePlan(stopIds, 'new', '');
      await waitFor(async () => expect((await readTour(world, tourId)).planApproval).toBe('given'));
      // Claude carries the approval out, and another page changes the level later than the held response.
      const question = await claude.next('question');
      await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Wrote the stops.' });
      await world.clock.advance(60_000);
      await world.reviewer.call('change_familiarity', { tourId, familiarity: 'familiar' });
      await waitFor(() => expect(app.selectedFamiliarity).toBe('familiar'));
      release();

      expect(await approved).toBeNull();
      expect(app.selectedFamiliarity).toBe('familiar');
      expect(app.selectedPlanWaits).toBe(false);
   });

   it('does not show for a tour without plan first, or once its plan is approved', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await secondTour(world, claude, { planFirst: false });
      await world.reviewer.call('approve_plan', { tourId, stopIds, skippedStops: [], familiarity: 'some' });
      const { app } = await startApp(world);
      await choose('shop #43');
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
      expect(await panel().findByRole('region', { name: 'Plan actions' })).toBeTruthy();
      expect(panel().queryByRole('region', { name: 'Plan proposal' })).toBeNull();

      await choose('shop #42');
      await waitFor(() => expect(app.selectedTourId).toBe(tourId));
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());

      expect(await panel().findByRole('region', { name: 'Plan actions' })).toBeTruthy();
      expect(panel().queryByRole('region', { name: 'Plan proposal' })).toBeNull();
   });

   it('tells in the Stop panel that a pending stop waits for the approval', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      const stopPanel = within(screen.getByRole('region', { name: 'Stop' }));
      expect(stopPanel.getByText('Claude writes this stop once you approve the plan in the Plan panel.')).toBeTruthy();
   });
});

describe('the thread of a plan approval', () => {
   it('names the action and offers a send again once the reviewer canceled it', async () => {
      const { world, tourId, stopIds } = await boundWorld();
      await world.reviewer.call('approve_plan', { tourId, stopIds, skippedStops: [], familiarity: 'some' });
      const question = messagesOf((await readTour(world, tourId)).threads[0]!)[0]!;
      await world.reviewer.call('cancel_question', { tourId, questionId: question.id });
      await open(world);
      await openThreadView();

      const threads = screen.getByRole('region', { name: 'Threads' });
      await waitFor(() => expect(threads.querySelector('[data-action]')?.textContent).toBe('Your decision Approve plan'));
      await waitFor(() => expect((within(threads).getByRole('button', { name: 'Send again' }) as HTMLButtonElement).disabled).toBe(false));
   });
});
