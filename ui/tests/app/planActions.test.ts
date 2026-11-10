import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { versionOf } from '../../src/lib/stops';
import { messagesOf } from '../../src/lib/threadView';
import { tourActionPending } from '../../src/lib/tourActions';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openStop, openTour, readTour, seedTour, startApp, type SeededStop, type SeededTour } from '../harness';
import { dragRow } from '../stopOrderDrag';
import { openTourList, tourButton } from '../tourPicker';
import { chooseFromPlanMenu, closePlanMenu, isItemDisabled, openPlanMenu, planActions, planMenuItem, planMenuNames, reasonOf } from './planMenu';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

type Seed = Pick<SeededTour, 'tourId' | 'stopIds'>;

const titles = ['Cart adds', 'Line totals', 'Gateway errors'];

const chapters: { title: string; stops: SeededStop[] }[] = [
   {
      title: 'Cart',
      stops: [
         { title: 'Cart adds', kind: 'inspect', content: cartContent('Cart adds.') },
         { title: 'Line totals', kind: 'inspect', content: cartContent('Line totals.') },
      ],
   },
   { title: 'Payment', stops: [{ title: 'Gateway errors', kind: 'risk', content: { ...cartContent('Gateway errors.'), scenario: 'The gateway times out.' } }] },
];

// Two chapters of written stops, the last a risk.
const plan = backend().template('planActions-plan', async (world) => {
   const seeded = await seedTour(world, { chapters });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds } satisfies Seed;
});

async function planWorld(): Promise<{ world: World } & Seed> {
   const world = await backend().world({ template: plan });
   return { world, ...(world.seed as Seed) };
}

async function boundWorld(): Promise<{ world: World; claude: ClaudeSession } & Seed> {
   const seeded = await planWorld();
   return { ...seeded, claude: await attachClaude(seeded.world, seeded.tourId) };
}

// Creates a second tour of the same plan, of the pull request that the label `shop #43` names, with the session.
async function secondTour(world: World, claude: ClaudeSession): Promise<Seed> {
   const tour = (await claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/43', familiarity: 'some' })) as { id: string };
   await world.idle();
   const planned = (await claude.call('set_plan', {
      tourId: tour.id,
      chapters: [
         { id: 'c1', title: 'Cart' },
         { id: 'c2', title: 'Payment' },
      ],
      stops: chapters.flatMap((chapter, index) => chapter.stops.map((stop) => ({ title: stop.title, kind: stop.kind, chapter: `c${index + 1}` }))),
   })) as { plan: { stops: { id: string }[] } };
   const stopIds = planned.plan.stops.map((stop) => stop.id);
   for (const [index, stop] of chapters.flatMap((chapter) => chapter.stops).entries()) {
      await claude.call('put_stop', { tourId: tour.id, stopId: stopIds[index], ...stop.content });
   }

   return { tourId: tour.id, stopIds };
}

// Sets the plan as Claude's set_plan does, keeping the IDs of the stops that the entries name.
async function setPlan(claude: ClaudeSession, tourId: string, stops: { id?: string; title: string; kind?: string; chapter?: string }[]): Promise<void> {
   await claude.call('set_plan', {
      tourId,
      chapters: [
         { id: 'c1', title: 'Cart' },
         { id: 'c2', title: 'Payment' },
      ],
      stops: stops.map((stop) => ({ kind: 'inspect', chapter: 'c1', ...stop })),
   });
}

// The plan's stops with their IDs, kinds and chapters, in the order of the IDs, each title changed by the function.
function planned(stopIds: string[], order: number[], title: (title: string) => string = (same) => same) {
   const kinds = ['inspect', 'inspect', 'risk'];
   const chapterIds = ['c1', 'c1', 'c2'];
   return order.map((index) => ({ id: stopIds[index]!, title: title(titles[index]!), kind: kinds[index], chapter: chapterIds[index] }));
}

// Opens the tour with the stop open, or none, and shows the Plan panel.
async function open(world: World, stopId: string | null, options: { retryDelaysMs?: number[] } = {}): Promise<{ app: LotsenApp; transport: Transport }> {
   const started = await startApp(world, options);
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

// Opens the tour of the label from the tour list, and shows its Plan panel, which each tour opens on its own.
async function choose(label: string): Promise<void> {
   const list = await openTourList();
   list
      .getAllByRole('button', tourButton)
      .find((button) => button.textContent?.includes(label))!
      .click();
   await waitFor(() => expect(screen.getByRole('button', { name: `Tour: ${label}, choose another` })).toBeTruthy());
   await showPlanPanel();
}

const planPanel = () => within(screen.getByRole('tabpanel', { name: 'Plan' }));

// The titles of the plan's rows, in the order the panel lists them.
const planTitles = () =>
   planPanel()
      .queryAllByRole('button', { name: /^\d+ / })
      .map((row) => row.querySelector('.title')!.textContent);

function sentActions(transport: Transport): Record<string, unknown>[] {
   return transport.requests
      .filter((request) => request.operation === 'request_tour_action')
      .map(({ input }) => {
         const rest = { ...input };
         delete rest.tourId;
         return rest;
      });
}

const proposed = () => within(planPanel().getByRole('list', { name: 'Proposed order' }));

const proposedTitles = () =>
   proposed()
      .getAllByRole('listitem')
      .map((item) => item.querySelector('.title')!.textContent);

const button = (name: string) => planPanel().getByRole('button', { name }) as HTMLButtonElement;

async function threadOf(world: World, tourId: string): Promise<{ threadId: string; questionId: string }> {
   const thread = (await readTour(world, tourId)).threads[0]!;
   return { threadId: thread.id, questionId: messagesOf(thread)[0]!.id };
}

describe('Reorder in the Plan panel', () => {
   it("sends the order that the reviewer made by moving stops, with the reviewer's words", async () => {
      const { world, stopIds } = await boundWorld();
      const [s1, s2, s3] = stopIds;
      const { transport } = await open(world, s1!);

      await chooseFromPlanMenu('Reorder…');
      await fireEvent.click(proposed().getByRole('button', { name: 'Move “Gateway errors” up' }));
      await fireEvent.click(proposed().getByRole('button', { name: 'Move “Gateway errors” up' }));
      await fireEvent.input(planPanel().getByRole('textbox', { name: 'Why this order? Optional' }), { target: { value: 'Risks first.' } });
      await fireEvent.click(button('Send to Claude'));

      await waitFor(() => expect(sentActions(transport)).toEqual([{ action: { kind: 'reorder', stopIds: [s3, s1, s2] }, text: 'Risks first.', stopId: s1 }]));
   });

   it('opens the order editor in place of the plan list, with no skip toggle, and sends the order of a drag', async () => {
      const { world, stopIds } = await boundWorld();
      const [s1, s2, s3] = stopIds;
      const { transport } = await open(world, s1!);

      await chooseFromPlanMenu('Reorder…');
      expect(planPanel().queryByRole('button', { name: /^Skip stop/ })).toBeNull();
      const rows = proposed().getAllByRole('listitem');
      await dragRow(rows[2]!, rows[0]!, 'before');
      await waitFor(() => expect(proposedTitles()).toEqual(['Gateway errors', 'Cart adds', 'Line totals']));
      await fireEvent.click(button('Send to Claude'));

      await waitFor(() => expect(sentActions(transport)).toEqual([{ action: { kind: 'reorder', stopIds: [s3, s1, s2] }, stopId: s1 }]));
   });

   it("keeps the plan as it is until Claude's set_plan arrives, and shows the waiting action until Claude resolves it", async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Reorder…');
      await fireEvent.click(proposed().getByRole('button', { name: 'Move “Cart adds” down' }));
      await fireEvent.click(button('Send to Claude'));

      expect(await planPanel().findByText('Claude is reordering the plan.')).toBeTruthy();
      expect(planTitles()).toEqual(['Cart adds', 'Line totals', 'Gateway errors']);
      await setPlan(claude, tourId, planned(stopIds, [1, 0, 2]));
      await waitFor(() => expect(planTitles()).toEqual(['Line totals', 'Cart adds', 'Gateway errors']));
      const { questionId } = await threadOf(world, tourId);
      await claude.call('reply', { tourId, questionId, resolution: 'done', text: 'Moved the totals first.' });
      await waitFor(() => expect(planPanel().queryByText('Claude is reordering the plan.')).toBeNull());
   });

   it("offers Send only once the order differs from the plan's", async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Reorder…');
      const send = button('Send to Claude');
      expect(send.disabled).toBe(true);

      await fireEvent.click(proposed().getByRole('button', { name: 'Move “Line totals” down' }));

      expect(send.disabled).toBe(false);
   });

   it('keeps the keyboard focus on the moved stop', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Reorder…');
      const down = proposed().getByRole('button', { name: 'Move “Cart adds” down' });
      down.focus();
      await fireEvent.click(down);

      await waitFor(() => expect(document.activeElement).toBe(proposed().getByRole('button', { name: 'Move “Cart adds” down' })));
      expect(planPanel().getByRole('status').textContent).toContain('Cart adds is now 2 of 3.');
   });

   it('moves the focus to the other button when the moved stop reaches an end', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Reorder…');
      const up = proposed().getByRole('button', { name: 'Move “Line totals” up' });
      up.focus();
      await fireEvent.click(up);

      await waitFor(() => expect(document.activeElement).toBe(proposed().getByRole('button', { name: 'Move “Line totals” down' })));
   });

   it('starts the proposal again from the new plan when a plan with other stops arrives while the reviewer edits', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await open(world, stopIds[0]!);
      await chooseFromPlanMenu('Reorder…');
      await fireEvent.click(proposed().getByRole('button', { name: 'Move “Gateway errors” up' }));

      await setPlan(claude, tourId, [...planned(stopIds, [0, 1, 2]), { title: 'Refunds', chapter: 'c2' }]);

      await waitFor(() => expect(proposedTitles()).toEqual(['Cart adds', 'Line totals', 'Gateway errors', 'Refunds']));
   });

   it('keeps the proposal when a plan of the same stops arrives while the reviewer edits', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await open(world, stopIds[0]!);
      await chooseFromPlanMenu('Reorder…');
      await fireEvent.click(proposed().getByRole('button', { name: 'Move “Gateway errors” up' }));

      await setPlan(
         claude,
         tourId,
         planned(stopIds, [0, 1, 2], (title) => `${title}!`),
      );

      await waitFor(() => expect(proposedTitles()).toEqual(['Cart adds!', 'Gateway errors!', 'Line totals!']));
   });

   it("shows the server's refusal and keeps the proposal", async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Reorder…');
      await fireEvent.click(proposed().getByRole('button', { name: 'Move “Gateway errors” up' }));
      // The server refuses words with a control character.
      await fireEvent.input(planPanel().getByRole('textbox', { name: 'Why this order? Optional' }), { target: { value: 'Risks\u0007 first.' } });
      await fireEvent.click(button('Send to Claude'));

      expect((await planPanel().findByRole('alert')).textContent).toContain('control character');
      expect(proposedTitles()).toEqual(['Cart adds', 'Gateway errors', 'Line totals']);
   });
});

describe('Re-plan in the Plan panel', () => {
   it("sends the reviewer's words about the whole tour", async () => {
      const { world } = await boundWorld();
      const { transport } = await open(world, null);

      await chooseFromPlanMenu('Re-plan…');
      await fireEvent.input(planPanel().getByRole('textbox', { name: 'How should the plan change?' }), { target: { value: 'Add a stop on the tests.' } });
      await fireEvent.click(button('Send to Claude'));

      await waitFor(() => expect(sentActions(transport)).toEqual([{ action: { kind: 'replan' }, text: 'Add a stop on the tests.' }]));
      expect(await planPanel().findByText('Claude is re-planning the tour.')).toBeTruthy();
   });

   it('starts with the form closed on another tour, so words typed for one tour never go to another', async () => {
      const { world, tourId, claude } = await boundWorld();
      const other = await secondTour(world, claude);
      const { app } = await open(world, null);
      await choose('shop #43');
      await waitFor(() => expect(app.selectedTourId).toBe(other.tourId));
      await choose('shop #42');
      await waitFor(() => expect(app.selectedTourId).toBe(tourId));
      await chooseFromPlanMenu('Re-plan…');
      await fireEvent.input(planPanel().getByRole('textbox', { name: 'How should the plan change?' }), { target: { value: 'Add a stop on the tests.' } });

      await choose('shop #43');
      await waitFor(() => expect(app.selectedTourId).toBe(other.tourId));

      expect(planPanel().queryByRole('textbox', { name: 'How should the plan change?' })).toBeNull();
      await chooseFromPlanMenu('Re-plan…');
      expect((planPanel().getByRole('textbox', { name: 'How should the plan change?' }) as HTMLTextAreaElement).value).toBe('');
   });

   it('offers Send only once the reviewer wrote words', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Re-plan…');

      expect(button('Send to Claude').disabled).toBe(true);
   });
});

describe("the words box of the Plan panel's actions", () => {
   const replanBox = () => planPanel().getByRole('textbox', { name: 'How should the plan change?' }) as HTMLTextAreaElement;
   const reorderBox = () => planPanel().getByRole('textbox', { name: 'Why this order? Optional' }) as HTMLTextAreaElement;

   it('tags the Reorder box as optional and says what an empty box does', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Reorder…');

      expect(reorderBox().placeholder).toBe('Leave it empty and Claude gets only your order.');
   });

   it('gives the Re-plan box, whose words are required, no tag and no placeholder, and the focus', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Re-plan…');

      expect(replanBox().hasAttribute('placeholder')).toBe(false);
      await waitFor(() => expect(document.activeElement).toBe(replanBox()));
   });

   it('changes the tag and the placeholder as the reviewer switches between the two actions', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Reorder…');
      await chooseFromPlanMenu('Re-plan…');
      expect(replanBox().hasAttribute('placeholder')).toBe(false);

      await chooseFromPlanMenu('Reorder…');
      expect(reorderBox().placeholder).toBe('Leave it empty and Claude gets only your order.');
   });
});

describe("the Plan panel's actions", () => {
   it("are both disabled with the server's reason while no session is attached", async () => {
      const { world, stopIds } = await planWorld();
      await open(world, stopIds[0]!);

      await waitFor(() => expect(planActions().getByText(/is attached to no Claude Code session/)).toBeTruthy());
      const menu = await openPlanMenu();
      const replan = planMenuItem(menu, 'Re-plan…');
      const reorder = planMenuItem(menu, 'Reorder…');
      expect(isItemDisabled(replan)).toBe(true);
      expect(isItemDisabled(reorder)).toBe(true);
      expect(reasonOf(replan)).toMatch(/is attached to no Claude Code session/);
      expect(reasonOf(reorder)).toBe(reasonOf(replan));
      await closePlanMenu();
   });

   it("disable Reorder… with the server's reason while an action on the plan waits, keep Re-plan… open for a level change, and link to the thread", async () => {
      const { world, tourId, stopIds } = await boundWorld();
      await world.reviewer.call('request_tour_action', { tourId, action: { kind: 'replan' }, text: 'Fewer stops.' });
      const { threadId } = await threadOf(world, tourId);
      const { app } = await open(world, stopIds[0]!);

      await waitFor(() => expect(planPanel().getByText('Claude is re-planning the tour.')).toBeTruthy());
      const menu = await openPlanMenu();
      expect(isItemDisabled(planMenuItem(menu, 'Reorder…'))).toBe(true);
      expect(reasonOf(planMenuItem(menu, 'Reorder…'))).toBe("Claude still re-plans the tour as you asked. Wait for Claude's reply, or cancel that request in its thread, then ask again.");
      expect(isItemDisabled(planMenuItem(menu, 'Re-plan…'))).toBe(false);
      await closePlanMenu();
      await fireEvent.click(planPanel().getByRole('button', { name: 'Show the thread' }));
      await waitFor(() => expect(app.openedThread).toEqual({ id: threadId }));
   });

   it('stay enabled while a summarize waits, which does not show in the Plan panel', async () => {
      const { world, tourId, stopIds } = await boundWorld();
      await world.reviewer.call('request_tour_action', { tourId, action: { kind: 'summarize' } });
      await open(world, stopIds[0]!);

      await waitFor(() => expect(planActions().queryByText(/is attached to/)).toBeNull());
      const menu = await openPlanMenu();
      await waitFor(() => expect(isItemDisabled(planMenuItem(menu, 'Re-plan…'))).toBe(false));
      expect(isItemDisabled(planMenuItem(menu, 'Reorder…'))).toBe(false);
      expect(planPanel().queryByText(tourActionPending.summarize)).toBeNull();
      await closePlanMenu();
   });

   it('hold only Change familiarity… before Claude writes the plan', async () => {
      const world = await backend().world();
      await seedTour(world);
      await open(world, null);

      expect(planMenuNames(await openPlanMenu())).toEqual(['Change familiarity…']);
      await closePlanMenu();
   });
});

describe('a revision while an action on the plan waits', () => {
   it('shows the new version of the stop under view without holding the one the reviewer read', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await world.reviewer.call('request_tour_action', { tourId, action: { kind: 'replan' }, text: 'Fewer stops.' });
      const { app } = await open(world, stopIds[0]!);

      await claude.call('put_stop', { tourId, stopId: stopIds[0], ...cartContent('Cart adds, for the new plan.') });

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it('holds the version the reviewer read while only a summarize waits, because it changes no stop', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await world.reviewer.call('request_tour_action', { tourId, action: { kind: 'summarize' } });
      const { app } = await open(world, stopIds[0]!);

      await claude.call('put_stop', { tourId, stopId: stopIds[0], ...cartContent('Cart adds, rewritten.') });

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion?.number).toBe(1);
   });

   it('skips the hold when the reload after a reconnect brings the revision and the reply together', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await world.reviewer.call('request_tour_action', { tourId, action: { kind: 'reorder', stopIds: [stopIds[1]!, stopIds[0]!, stopIds[2]!] } });
      const { questionId } = await threadOf(world, tourId);
      // The page waits before it reconnects, so the revision and the reply reach it with the reload, not as events.
      const { app, transport } = await startApp(world, { retryDelaysMs: [1500] });
      await openTour(app);
      // Every response carries the tour's controls, so the writes that opening the stop makes end before Claude replies.
      const opened = Promise.all([transport.answered('record_reading_progress'), transport.answered('diff_file')]);
      await openStop(app, stopIds[0]!);
      await opened;

      transport.cutEventStreams();
      await claude.call('put_stop', { tourId, stopId: stopIds[0], ...cartContent('Cart adds, for the new order.') });
      await claude.call('reply', { tourId, questionId, resolution: 'done', text: 'Reordered.' });

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it('holds the version the reviewer read while the waiting action is on another tour', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const other = await secondTour(world, claude);
      await world.reviewer.call('request_tour_action', { tourId: other.tourId, action: { kind: 'replan' }, text: 'Fewer stops.' });
      const { app } = await open(world, null);
      await choose('shop #43');
      await waitFor(() => expect(app.selectedThreads).toHaveLength(1));
      await choose('shop #42');
      await waitFor(() => expect(app.selectedTourId).toBe(tourId));
      await openStop(app, stopIds[0]!);

      await claude.call('put_stop', { tourId, stopId: stopIds[0], ...cartContent('Cart adds, later.') });

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'revised' } });
   });

   it('holds the version the reviewer read once Claude resolved the action', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await world.reviewer.call('request_tour_action', { tourId, action: { kind: 'replan' }, text: 'Fewer stops.' });
      const { questionId } = await threadOf(world, tourId);
      await claude.call('reply', { tourId, questionId, resolution: 'done', text: 'Kept the plan.' });
      const { app } = await open(world, stopIds[0]!);

      await claude.call('put_stop', { tourId, stopId: stopIds[0], ...cartContent('Cart adds, later.') });

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'revised' } });
   });
});
