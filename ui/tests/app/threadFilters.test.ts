import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { defaultThreadFilter } from '../../src/lib/threadFilters';
import { lastRequest } from '../../src/lib/threadView';
import type { AnchorInput, QuestionThread } from '../../src/lib/types';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, cartBase, cartContent, cartHead, openTour, prLabel, readTour, seedTour, startApp, withHolds, type HeldTransport } from '../harness';
import { askAbout } from './askBox';
import { chooseWhere, closeMenu, inMenu, itemName, openMenu, showButton, showItem, shownOnButton, toggleShown, whereChoices, whereOnButton } from './threadFilter';
import { backToList, openThreadView, shownIds, threadRow, threadView, threadViewElement } from './threadView';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

// A negative check needs the page to have handled what the server sent, which no condition on the page shows.
const settled = () => new Promise((resolve) => setTimeout(resolve, 50));

/** The threads of the first tour by their roles here, each with the server's ID. */
interface ThreadIds {
   cartCode: string;
   cartStop: string;
   roundText: string;
   roundFile: string;
   tour: string;
}

interface Seed {
   tourId: string;
   otherId: string;
   stopIds: [string, string];
   ids: ThreadIds;
}

const otherUrl = 'https://platform.test/shop/pull/43';
const otherLabel = 'shop #43';

/** Asks the question as the reviewer, has the session answer it, and returns the thread's ID. */
async function answered(world: World, claude: ClaudeSession, tourId: string, name: string, anchor: AnchorInput, stopId?: string): Promise<string> {
   const thread = (await world.reviewer.call('ask_question', { tourId, text: `Question ${name}`, anchor, ...(stopId === undefined ? {} : { stopId }) })) as QuestionThread;
   const question = await claude.next('question');
   await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: `Answer ${name}` });
   return thread.id;
}

// The first tour holds five answered threads, two of them resolved, asked in this order, and the second tour one resolved thread.
const filters = backend().template('threadFilters-five', async (world): Promise<Seed> => {
   const { tourId, stopIds, claude } = await seedTour(world, {
      base: { ...cartBase, 'src/round.ts': 'export const round = Math.round;\n' },
      head: cartHead,
      chapters: [
         {
            title: 'Checkout',
            stops: [
               { title: 'Cart totals', kind: 'inspect', content: cartContent('The total adds the lines.') },
               { title: 'Rounding', kind: 'risk', content: { ...cartContent('The total may drift.', 'It rounds the sum once.'), scenario: 'A cart of many small lines totals a cent off.' } },
            ],
         },
      ],
   });
   const [cartTotals, rounding] = stopIds as [string, string];
   const ids: ThreadIds = {
      cartCode: await answered(world, claude!, tourId, 'cart-code', { kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 1, endLine: 2 }, cartTotals),
      cartStop: await answered(world, claude!, tourId, 'cart-stop', { kind: 'stop', stopId: cartTotals }, rounding),
      roundText: await answered(world, claude!, tourId, 'round-text', { kind: 'stopText', stopId: rounding, field: 'how', text: 'rounds', version: 1 }),
      roundFile: await answered(world, claude!, tourId, 'round-file', { kind: 'file', side: 'base', path: 'src/round.ts' }, rounding),
      tour: await answered(world, claude!, tourId, 'tour', { kind: 'tour' }),
   };
   for (const resolved of [ids.cartStop, ids.roundFile]) {
      await world.reviewer.call('set_thread_state', { tourId, threadId: resolved, state: 'resolved' });
   }

   const second = await world.claude();
   const other = (await second.call('create_tour', { prUrl: otherUrl, familiarity: 'some' })) as { id: string };
   await world.idle();
   await second.call('set_plan', { tourId: other.id, chapters: [{ id: 'c1', title: 'Checkout' }], stops: [{ title: 'Cart totals', kind: 'inspect', chapter: 'c1' }] });
   const elsewhere = await answered(world, second, other.id, 'elsewhere', { kind: 'tour' });
   await world.reviewer.call('set_thread_state', { tourId: other.id, threadId: elsewhere, state: 'resolved' });
   return { tourId, otherId: other.id, stopIds: [cartTotals, rounding], ids };
});

const panel = () => within(screen.getByRole('region', { name: 'Threads' }));
/** The button of a thread's row, which opens its thread view. */
const rowButton = (threadId: string) => within(threadRow(threadId)!).getAllByRole('button')[0]!;
const stateOf = async (world: World, tourId: string, threadId: string) => (await readTour(world, tourId)).threads.find((each) => each.id === threadId)?.state;

interface Started extends Seed {
   world: World;
   app: LotsenApp;
   transport: HeldTransport;
   claude: ClaudeSession | null;
}

/** Starts the app on a copy of the five threads with the first tour open, with a bound session unless `bound` is false. `before` runs on the world with the session before the app starts. */
async function start(options: { bound?: boolean; before?: (world: World, claude: ClaudeSession, seed: Seed) => Promise<void> } = {}): Promise<Started> {
   const world = await backend().world({ template: filters });
   const seed = world.seed as Seed;
   const claude = options.bound === false ? null : await attachClaude(world, seed.tourId);
   if (options.before !== undefined) {
      await options.before(world, claude!, seed);
   }

   const transport = withHolds(world.transport());
   const { app } = await startApp(world, { transport });
   await select(app, prLabel, seed.tourId);
   return { ...seed, world, app, transport, claude };
}

async function select(app: LotsenApp, label: string, tourId: string) {
   await openTour(app, label);
   await waitFor(() => expect(app.selectedTourId).toBe(tourId));
   await waitFor(() => expect(app.selectedThreads.length).toBeGreaterThan(0));
}

/** Asks about the tour from the ask box while `held` holds the ask's response, and returns a reader of the asked thread's ID once the response arrives. */
async function askHeld(question: string, held: { answered: Promise<unknown> }): Promise<() => string | undefined> {
   const before = new Set(shownIds());
   await askAbout('The tour');
   await fireEvent.input(panel().getByRole('textbox', { name: 'Question for Claude' }), { target: { value: question } });
   await fireEvent.click(panel().getByRole('button', { name: 'Send' }));
   await held.answered;
   return () => shownIds().find((threadId) => !before.has(threadId));
}

async function showResolvedOnly(count: number) {
   await toggleShown('Resolved');
   await toggleShown('Open');
   await waitFor(() => expect(shownIds()).toHaveLength(count));
}

/** Follows up in a thread from its thread view, which stays open, with the focus on the send button as a click puts it there. */
async function followUp(threadId: string, text: string) {
   const view = await openThreadView(threadId);
   await fireEvent.input(view.getByRole('textbox', { name: 'Follow-up for Claude' }), { target: { value: text } });
   const send = view.getByRole('button', { name: 'Send the follow-up' });
   send.focus();
   await fireEvent.click(send);
}

/** Resolves or reopens a thread from the button in its thread view's header, which stays open. */
async function setStateInView(threadId: string, name: 'Resolve' | 'Reopen') {
   const view = await openThreadView(threadId);
   await fireEvent.click(view.getByRole('button', { name }));
}

async function askAboutTheTour(app: LotsenApp, question: string, count = 6) {
   await askAbout('The tour');
   await fireEvent.input(panel().getByRole('textbox', { name: 'Question for Claude' }), { target: { value: question } });
   await fireEvent.click(panel().getByRole('button', { name: 'Send' }));
   await waitFor(() => expect(app.selectedThreads).toHaveLength(count));
   await waitFor(() => expect(threadRow(app.selectedThreads.at(-1)!.id)).not.toBeNull());
   return app.selectedThreads.at(-1)!.id;
}

describe("the Threads panel's filters in the app", () => {
   it('lists the open threads newest first and hides the resolved ones until the reviewer shows them', async () => {
      const { ids } = await start();

      expect(shownIds()).toEqual([ids.tour, ids.roundText, ids.cartCode]);
      expect(shownOnButton()).toBe('Open, tour actions');
      expect(panel().getByText('Showing 3 of 5 threads.')).toBeTruthy();

      await toggleShown('Resolved');
      await waitFor(() => expect(shownIds()).toEqual([ids.tour, ids.roundFile, ids.roundText, ids.cartStop, ids.cartCode]));
      await toggleShown('Open');

      await waitFor(() => expect(shownIds()).toEqual([ids.roundFile, ids.cartStop]));
      expect(within(threadRow(ids.cartStop)!).getByText('Resolved')).toBeTruthy();
   });

   it('shows only the threads of the chosen stop or file, and says when none match', async () => {
      const { ids } = await start();
      expect((await whereChoices()).map((choice) => choice.name)).toEqual([
         'Anywhere',
         'Current stop',
         'Current file',
         'Stop 1 · Cart totals 1',
         'Stop 2 · Rounding 1',
         'src/cart.ts 1',
         'src/round.ts 0',
      ]);

      await chooseWhere('Stop 1 · Cart totals');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      await chooseWhere('Anywhere');
      await chooseWhere('src/round.ts');

      await waitFor(() => expect(shownIds()).toEqual([]));
      expect(panel().getByText('No threads match the filters.')).toBeTruthy();
      await toggleShown('Resolved');
      await waitFor(() => expect(shownIds()).toEqual([ids.roundFile]));
   });

   it("toggles each state and the tour actions from the Show menu, which stays open, with each state's count, and names what shows on its button", async () => {
      const { ids } = await start();
      const menu = await openMenu(showButton());
      const items = () =>
         within(menu)
            .getAllByRole('menuitemcheckbox', inMenu)
            .map((item) => [itemName(item), item.getAttribute('aria-checked')]);
      expect(items()).toEqual([
         ['Open 3', 'true'],
         ['Resolved 2', 'false'],
         ['Outdated 0', 'false'],
         ['Tour actions and decisions', 'true'],
      ]);

      await fireEvent.click(showItem(menu, 'Resolved'));
      await waitFor(() => expect(shownIds()).toHaveLength(5));
      expect(shownOnButton()).toBe('Open, resolved, tour actions');
      await fireEvent.click(showItem(menu, 'Open'));
      await waitFor(() => expect(shownIds()).toEqual([ids.roundFile, ids.cartStop]));
      await fireEvent.click(showItem(menu, 'Outdated'));
      await fireEvent.click(showItem(menu, 'Tour actions'));

      await waitFor(() => expect(shownOnButton()).toBe('Resolved, outdated'));
      expect(menu.dataset.state).toBe('open');
      expect(items()).toEqual([
         ['Open 3', 'false'],
         ['Resolved 2', 'true'],
         ['Outdated 0', 'true'],
         ['Tour actions and decisions', 'false'],
      ]);
   });

   it('keeps the last shown state from the Show menu, and says that one stays shown', async () => {
      const { ids } = await start();
      const menu = await openMenu(showButton());
      const open = showItem(menu, 'Open');
      expect(open.getAttribute('aria-disabled')).toBe('true');
      expect(open.getAttribute('aria-describedby')).toBe(within(menu).getByText('At least one state stays shown.').id);

      await fireEvent.click(open);
      await settled();

      expect(open.getAttribute('aria-checked')).toBe('true');
      expect(menu.dataset.state).toBe('open');
      expect(shownIds()).toEqual([ids.tour, ids.roundText, ids.cartCode]);
      await fireEvent.click(showItem(menu, 'Resolved'));
      await waitFor(() => expect(showItem(menu, 'Open').getAttribute('aria-disabled')).toBe('false'));
      expect(within(menu).queryByText('At least one state stays shown.')).toBeNull();
      await fireEvent.click(showItem(menu, 'Open'));
      await waitFor(() => expect(showItem(menu, 'Resolved').getAttribute('aria-disabled')).toBe('true'));
      expect(shownOnButton()).toBe('Resolved, tour actions');
   });

   it('lets the keyboard reach the last shown state, which Enter and Space leave shown', async () => {
      const { ids } = await start();
      const menu = await openMenu(showButton());
      const open = showItem(menu, 'Open');

      await waitFor(() => expect(document.activeElement).toBe(open));
      await fireEvent.keyDown(open, { key: 'ArrowDown' });
      expect(document.activeElement).toBe(showItem(menu, 'Resolved'));
      await fireEvent.keyDown(document.activeElement!, { key: 'ArrowUp' });
      expect(document.activeElement).toBe(open);
      expect(open.getAttribute('aria-disabled')).toBe('true');
      expect(open.getAttribute('aria-describedby')).toBe(within(menu).getByText('At least one state stays shown.').id);
      await fireEvent.keyDown(open, { key: 'Enter' });
      await fireEvent.keyDown(open, { key: ' ' });
      await settled();

      expect(open.getAttribute('aria-checked')).toBe('true');
      expect(menu.dataset.state).toBe('open');
      expect(shownOnButton()).toBe('Open, tour actions');
      expect(shownIds()).toEqual([ids.tour, ids.roundText, ids.cartCode]);
   });

   it('counts a click on the last shown state as no choice of filter, so a follow-up in flight still widens the filter to show its thread', async () => {
      const { ids, transport } = await start();
      await showResolvedOnly(2);
      const held = transport.holdAnswered('ask_follow_up');
      await followUp(ids.cartStop, 'And the tax?');
      await held.answered;
      await backToList();
      const menu = await openMenu(showButton());
      await fireEvent.click(showItem(menu, 'Resolved'));
      await closeMenu(menu);

      await held.release();

      await waitFor(() => expect(shownOnButton()).toBe('Open, resolved, tour actions'));
      expect(shownIds()).toContain(ids.cartStop);
   });

   it('follows the stop that the reviewer opens with the current stop, and keeps a named stop wherever the reviewer is', async () => {
      const { app, ids, stopIds } = await start();
      app.openStop(stopIds[0]);
      await chooseWhere('Current stop');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      expect(whereOnButton()).toBe('Current stop');

      app.openStop(stopIds[1]);

      await waitFor(() => expect(shownIds()).toEqual([ids.roundText]));
      expect((await whereChoices()).find((choice) => choice.checked)?.name).toBe('Current stop · Stop 2');
      await chooseWhere('Stop 2 · Rounding');
      app.openStop(stopIds[0]);
      await settled();
      expect(shownIds()).toEqual([ids.roundText]);
      expect(whereOnButton()).toBe('Stop 2 · Rounding');
   });

   it('says that there is no current stop, and lists no thread, while the summary page shows', async () => {
      const { app, ids, stopIds } = await start();
      app.openStop(stopIds[0]);
      await chooseWhere('Current stop');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));

      app.openSummary();

      await waitFor(() => expect(panel().getByText('There is no current stop. Open a stop to see its threads.')).toBeTruthy());
      expect(shownIds()).toEqual([]);
      app.openStop(stopIds[1]);
      await waitFor(() => expect(shownIds()).toEqual([ids.roundText]));
   });

   it('follows the file that the reviewer opens with the current file, says when none is open, and keeps a named file wherever the reviewer is', async () => {
      const { app, ids } = await start();
      await toggleShown('Resolved');
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      app.openSummary();
      await chooseWhere('Current file');
      await waitFor(() => expect(panel().getByText('There is no current file. Open a file to see its threads.')).toBeTruthy());
      expect(shownIds()).toEqual([]);

      app.openRepositoryFile('base', 'src/round.ts');
      await waitFor(() => expect(shownIds()).toEqual([ids.roundFile]));
      app.selectFile('src/cart.ts');

      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      expect((await whereChoices()).find((choice) => choice.checked)?.name).toBe('Current file · cart.ts');
      await chooseWhere('src/round.ts');
      app.selectFile('src/cart.ts');
      await settled();
      expect(shownIds()).toEqual([ids.roundFile]);
      expect(whereOnButton()).toBe('round.ts');
   });

   it("keeps each tour's filter while another tour is open", async () => {
      const { app, ids, tourId, otherId } = await start();
      await chooseWhere('Stop 2 · Rounding');
      await waitFor(() => expect(shownIds()).toEqual([ids.roundText]));

      await select(app, otherLabel, otherId);
      expect(shownIds()).toEqual([]);
      expect(whereOnButton()).toBe('Anywhere');
      await select(app, prLabel, tourId);

      expect(whereOnButton()).toBe('Stop 2 · Rounding');
      expect(shownIds()).toEqual([ids.roundText]);
   });

   it("opens where a thread was asked from the link in its thread view's header", async () => {
      const { app, ids, stopIds } = await start();
      await chooseWhere('Stop 2 · Rounding');
      await waitFor(() => expect(shownIds()).toEqual([ids.roundText]));

      await fireEvent.click((await openThreadView(ids.roundText)).getByRole('button', { name: /Stop 2 · Rounding/ }));

      await waitFor(() => expect(app.selectedStopId).toBe(stopIds[1]));
   });

   it('opens the thread view of a thread that the reviewer opens from its marker, and widens the filter to show its row once the reviewer goes back', async () => {
      const { app, ids, stopIds } = await start();
      await chooseWhere('Stop 2 · Rounding');
      await waitFor(() => expect(shownIds()).toEqual([ids.roundText]));
      app.openStop(stopIds[0]);
      const marker = await within(screen.getByRole('region', { name: 'Stop' })).findByRole('button', { name: 'Open the thread: Question cart-stop' });

      await fireEvent.click(marker);

      await waitFor(() => expect(threadViewElement()?.dataset.threadId).toBe(ids.cartStop));
      await waitFor(() => expect(document.activeElement).toBe(threadViewElement()));
      await backToList();
      expect(shownIds()).toContain(ids.cartStop);
      expect(whereOnButton()).toBe('Anywhere');
      expect(shownOnButton()).toBe('Open, resolved, tour actions');
   });

   it('widens the filter to show the thread of a question that the reviewer asks, with its Cancel', async () => {
      const { app, ids } = await start();
      await chooseWhere('src/cart.ts');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));

      const asked = await askAboutTheTour(app, 'Why two passes?');

      expect(shownIds()).toEqual([asked, ids.tour, ids.roundText, ids.cartCode]);
      expect(whereOnButton()).toBe('Anywhere');
      expect(shownOnButton()).toBe('Open, tour actions');
      expect((await openThreadView(asked)).getByRole('button', { name: 'Cancel' })).toBeTruthy();
   });

   it('keeps the filter as it is when it already shows the thread of a question that the reviewer asks', async () => {
      const { app } = await start();
      await toggleShown('Resolved');
      await waitFor(() => expect(shownIds()).toHaveLength(5));
      const before = app.selectedThreadFilter;

      const asked = await askAboutTheTour(app, 'Why two passes?');

      expect(app.selectedThreadFilter).toBe(before);
      expect(shownIds()[0]).toBe(asked);
   });

   it('keeps the default filter when it shows the thread of a question that the reviewer asks', async () => {
      const { app } = await start();

      const asked = await askAboutTheTour(app, 'Why two passes?');

      expect(app.selectedThreadFilter).toBe(defaultThreadFilter);
      expect(shownIds()[0]).toBe(asked);
   });

   it('keeps the thread view focused, and the thread listed once the reviewer goes back, when a follow-up reopens a resolved thread under a filter of resolved threads only', async () => {
      const { app, ids } = await start();
      await showResolvedOnly(2);

      await followUp(ids.cartStop, 'And the tax?');

      await waitFor(() => expect(app.selectedThreads.find((each) => each.id === ids.cartStop)!.state).toBe('open'));
      await waitFor(() => expect(document.activeElement).toBe(threadViewElement()));
      expect(threadView().getByRole('button', { name: 'Cancel' })).toBeTruthy();
      await backToList();
      expect(shownIds()).toEqual([ids.tour, ids.roundFile, ids.roundText, ids.cartStop, ids.cartCode]);
      expect(shownOnButton()).toMatch(/^Open/);
      expect(document.activeElement).toBe(rowButton(ids.cartStop));
   });

   it('keeps the focus in the thread view, and the thread listed once the reviewer goes back, when a re-send reopens a resolved thread under a filter of resolved threads only', async () => {
      let stuck = '';
      const { app, ids } = await start({
         // A resolved thread whose only question the reviewer canceled before Claude answered.
         before: async (world, _, { tourId }) => {
            const thread = (await world.reviewer.call('ask_question', { tourId, text: 'Still there?', anchor: { kind: 'tour' } })) as QuestionThread;
            stuck = thread.id;
            await world.reviewer.call('cancel_question', { tourId, questionId: lastQuestionId(thread) });
            await world.reviewer.call('set_thread_state', { tourId, threadId: stuck, state: 'resolved' });
         },
      });
      await showResolvedOnly(3);
      expect(shownIds()).toEqual([stuck, ids.roundFile, ids.cartStop]);

      const view = await openThreadView(stuck);
      const resend = view.getByRole('button', { name: 'Send again' });
      resend.focus();
      await fireEvent.click(resend);

      await waitFor(() => expect(app.selectedThreads.find((each) => each.id === stuck)!.state).toBe('open'));
      await waitFor(() => expect(app.selectedThreadFilter.states).toContain('open'));
      expect(view.getByRole('button', { name: 'Cancel' })).toBeTruthy();
      expect(document.activeElement).toBe(resend);
      const widened = app.selectedThreadFilter;
      (document.activeElement as HTMLElement).blur();
      await fireEvent.click(view.getByRole('button', { name: 'Cancel' }));

      await waitFor(() => expect(document.activeElement).toBe(threadViewElement()));
      expect(app.selectedThreadFilter).toBe(widened);
      await backToList();
      expect(shownIds()).toEqual([stuck, ids.tour, ids.roundFile, ids.roundText, ids.cartStop, ids.cartCode]);
   });

   it('keeps the filter as it is when the server refuses a follow-up in a resolved thread', async () => {
      const { app, ids, transport, claude } = await start();
      await showResolvedOnly(2);
      const before = app.selectedThreadFilter;
      // The session ends while the page hears nothing, so the page still offers the follow-up.
      transport.holdEvents();
      await claude!.drop();

      await followUp(ids.cartStop, 'And the tax?');

      expect((await threadView().findByRole('alert')).textContent).toMatch(/attached to no Claude Code session/);
      expect(app.selectedThreadFilter).toBe(before);
      await backToList();
      expect(shownIds()).toEqual([ids.roundFile, ids.cartStop]);
   });

   it('widens the filter of the tour that asked when its response arrives after another tour opens', async () => {
      const { app, ids, transport, tourId, otherId } = await start();
      await chooseWhere('src/cart.ts');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      const held = transport.holdAnswered('ask_question');
      await askAbout('The tour');
      await fireEvent.input(panel().getByRole('textbox', { name: 'Question for Claude' }), { target: { value: 'Why two passes?' } });
      await fireEvent.click(panel().getByRole('button', { name: 'Send' }));
      await held.answered;

      await select(app, otherLabel, otherId);
      await held.release();
      await settled();

      expect(app.selectedThreadFilter).toBe(defaultThreadFilter);
      await select(app, prLabel, tourId);
      expect(whereOnButton()).toBe('Anywhere');
      expect(shownIds()).toHaveLength(4);
   });

   it('keeps the current stop of the tour that asked when its response arrives after another tour opens', async () => {
      const { app, ids, stopIds, transport, tourId, otherId } = await start();
      app.openStop(stopIds[0]);
      await chooseWhere('Current stop');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      const held = transport.holdAnswered('ask_question');
      const asked = await askHeld('Why two passes?', held);

      await select(app, otherLabel, otherId);
      await held.release();
      await settled();
      await select(app, prLabel, tourId);

      await waitFor(() => expect(app.selectedStopId).toBe(stopIds[0]));
      expect(whereOnButton()).toBe('Current stop');
      await waitFor(() => expect(shownIds()).toEqual([asked(), ids.cartCode]));
   });

   it('widens the current file of the tour that asked to anywhere when its response arrives after another tour opens, since no file opens again on the return', async () => {
      const { app, ids, transport, tourId, otherId } = await start();
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      app.selectFile('src/cart.ts');
      await chooseWhere('Current file');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      const held = transport.holdAnswered('ask_question');
      await askHeld('Why two passes?', held);

      await select(app, otherLabel, otherId);
      await held.release();
      await settled();
      await select(app, prLabel, tourId);

      expect(whereOnButton()).toBe('Anywhere');
      await waitFor(() => expect(shownIds()).toEqual([app.selectedThreads.at(-1)!.id, ids.tour, ids.roundText, ids.cartCode]));
   });

   it('widens the current stop of the tour that asked from its summary page to anywhere when its response arrives after another tour opens', async () => {
      const { app, ids, stopIds, transport, tourId, otherId } = await start();
      app.openStop(stopIds[0]);
      await chooseWhere('Current stop');
      app.openSummary();
      await waitFor(() => expect(shownIds()).toEqual([]));
      const held = transport.holdAnswered('ask_question');
      await askHeld('Why two passes?', held);

      await select(app, otherLabel, otherId);
      await held.release();
      await settled();
      await select(app, prLabel, tourId);

      await waitFor(() => expect(app.showsSummary).toBe(true));
      expect(whereOnButton()).toBe('Anywhere');
      expect(shownIds()).toEqual([app.selectedThreads.at(-1)!.id, ids.tour, ids.roundText, ids.cartCode]);
   });

   it('widens the current file to anywhere when the response of a question about the tour arrives while the tour stays open', async () => {
      const { app, ids, transport } = await start();
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      app.selectFile('src/cart.ts');
      await chooseWhere('Current file');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      const held = transport.holdAnswered('ask_question');
      const asked = await askHeld('Why two passes?', held);

      await held.release();

      await waitFor(() => expect(shownIds()).toEqual([asked(), ids.tour, ids.roundText, ids.cartCode]));
      expect(whereOnButton()).toBe('Anywhere');
   });

   it('keeps the current stop when the response of a question asked there arrives while the tour stays open', async () => {
      const { app, ids, stopIds, transport } = await start();
      app.openStop(stopIds[0]);
      await chooseWhere('Current stop');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      const held = transport.holdAnswered('ask_question');
      const asked = await askHeld('Why two passes?', held);

      await held.release();

      await waitFor(() => expect(shownIds()).toEqual([asked(), ids.cartCode]));
      expect(whereOnButton()).toBe('Current stop');
   });

   it('widens the current stop to anywhere for a thread at another stop that the reviewer opens from its marker', async () => {
      const { app, ids, stopIds } = await start();
      app.openStop(stopIds[1]);
      await chooseWhere('Current stop');
      await waitFor(() => expect(shownIds()).toEqual([ids.roundText]));

      app.openThread(ids.cartCode);

      await waitFor(() => expect(threadViewElement()?.dataset.threadId).toBe(ids.cartCode));
      await backToList();
      expect(shownIds()).toContain(ids.cartCode);
      expect(whereOnButton()).toBe('Anywhere');
   });

   it('widens the filter of the tour that asked when the reviewer chose a filter in another tour while the ask was in flight', async () => {
      const { app, ids, transport, tourId, otherId } = await start();
      await chooseWhere('src/cart.ts');
      await waitFor(() => expect(shownIds()).toEqual([ids.cartCode]));
      const held = transport.holdAnswered('ask_question');
      await askAbout('The tour');
      await fireEvent.input(panel().getByRole('textbox', { name: 'Question for Claude' }), { target: { value: 'Why two passes?' } });
      await fireEvent.click(panel().getByRole('button', { name: 'Send' }));
      await held.answered;

      await select(app, otherLabel, otherId);
      await toggleShown('Resolved');
      await held.release();
      await settled();

      await select(app, prLabel, tourId);
      expect(whereOnButton()).toBe('Anywhere');
      expect(shownIds()).toHaveLength(4);
   });

   it('keeps a resolve that lands while a cancel is in flight, with the thread hidden from the list and the focus on the button that resolved it', async () => {
      let waiting = '';
      const { app, world, transport, tourId } = await start({
         before: async (world, _, seed) => {
            waiting = ((await world.reviewer.call('ask_question', { tourId: seed.tourId, text: 'Still there?', anchor: { kind: 'tour' } })) as QuestionThread).id;
         },
      });
      const held = transport.holdAnswered('cancel_question');
      const view = await openThreadView(waiting);
      await fireEvent.click(view.getByRole('button', { name: 'Cancel' }));
      await held.answered;
      expect(lastRequest((await readTour(world, tourId)).threads.find((each) => each.id === waiting)!)?.state).toBe('canceled');

      const resolve = view.getByRole('button', { name: 'Resolve' });
      resolve.focus();
      await fireEvent.click(resolve);
      await waitFor(() => expect(view.getByRole('button', { name: 'Reopen' })).toBe(resolve));
      await held.release();
      await settled();

      expect(app.selectedThreadFilter).toBe(defaultThreadFilter);
      expect(document.activeElement).toBe(resolve);
      await backToList();
      expect(shownIds()).not.toContain(waiting);
      expect(document.activeElement?.textContent).toBe('Showing 3 of 6 threads.');
   });

   it('keeps the filter that the reviewer chose while a follow-up was in flight', async () => {
      const { app, ids, transport } = await start();
      const held = transport.holdAnswered('ask_follow_up');
      await followUp(ids.tour, 'And the tax?');
      await held.answered;
      await backToList();

      await showResolvedOnly(2);
      expect(shownIds()).toEqual([ids.roundFile, ids.cartStop]);
      const chosen = app.selectedThreadFilter;
      await held.release();
      await settled();

      expect(app.selectedThreadFilter).toBe(chosen);
      expect(shownIds()).toEqual([ids.roundFile, ids.cartStop]);
      expect(shownOnButton()).toBe('Resolved, tour actions');
   });

   it("widens the filter and focuses the thread view when a follow-up's response reaches the page before its event", async () => {
      const { ids, transport } = await start();
      await showResolvedOnly(2);
      transport.holdEvents();

      await followUp(ids.cartStop, 'And the tax?');

      await waitFor(() => expect(document.activeElement).toBe(threadViewElement()));
      await backToList();
      expect(shownOnButton()).toMatch(/^Open/);
      expect(shownIds()).toContain(ids.cartStop);
   });

   it('resolves and reopens a thread with no session attached, and sends nothing that reaches Claude', async () => {
      const { ids, world, tourId, transport } = await start({ bound: false });

      await setStateInView(ids.tour, 'Resolve');
      await waitFor(async () => expect(await stateOf(world, tourId, ids.tour)).toBe('resolved'));
      await backToList();
      await waitFor(() => expect(shownIds()).toEqual([ids.roundText, ids.cartCode]));
      await toggleShown('Resolved');
      await waitFor(() => expect(shownIds()).toContain(ids.tour));
      await setStateInView(ids.tour, 'Reopen');

      await waitFor(() => expect(threadView().queryByText('Resolved')).toBeNull());
      expect(await stateOf(world, tourId, ids.tour)).toBe('open');
      const toClaude = new Set(['ask_question', 'ask_follow_up', 'resend_question', 'request_tour_action', 'send_notes', 'send_answers']);
      expect(transport.requests.filter((request) => request.operation !== null && toClaude.has(request.operation))).toEqual([]);
      expect(transport.requests.filter((request) => request.operation === 'set_thread_state').map((request) => request.input)).toEqual([
         { tourId, threadId: ids.tour, state: 'resolved' },
         { tourId, threadId: ids.tour, state: 'open' },
      ]);
   });

   it("keeps a resolved thread's view, and moves the focus to the list's status on the way back when the filter hides the thread", async () => {
      const { ids } = await start();

      await setStateInView(ids.tour, 'Resolve');
      await waitFor(() => expect(threadView().getByRole('button', { name: 'Reopen' })).toBeTruthy());
      await backToList();

      expect(shownIds()).not.toContain(ids.tour);
      expect(document.activeElement?.textContent).toBe('Showing 2 of 5 threads.');
   });

   it('keeps the focus on the Resolve button, which turns into Reopen with the state change', async () => {
      const { ids } = await start();
      const resolve = (await openThreadView(ids.tour)).getByRole('button', { name: 'Resolve' });
      resolve.focus();

      await fireEvent.click(resolve);

      await waitFor(() => expect(resolve.textContent?.trim()).toBe('Reopen'));
      expect(document.activeElement).toBe(resolve);
   });

   it('keeps the focus where the reviewer moved it while a state change was in flight', async () => {
      const { ids, transport } = await start();
      await toggleShown('Resolved');
      await waitFor(() => expect(shownIds()).toHaveLength(5));
      const held = transport.holdAnswered('set_thread_state');
      await setStateInView(ids.tour, 'Resolve');
      await held.answered;
      const box = threadView().getByRole('textbox', { name: 'Follow-up for Claude' });
      box.focus();

      await held.release();
      await settled();

      expect(threadView().getByRole('button', { name: 'Reopen' })).toBeTruthy();
      expect(document.activeElement).toBe(box);
   });

   it('shows the reason when the server refuses the state change', async () => {
      const { ids, transport, claude, tourId } = await start();
      // Claude deletes the thread while the page hears nothing, so the page still lists it.
      transport.holdEvents();
      await claude!.call('delete_thread', { tourId, threadId: ids.tour });

      await setStateInView(ids.tour, 'Resolve');

      expect((await threadView().findByRole('alert')).textContent).toContain('has no thread');
      await backToList();
      expect(shownIds()).toContain(ids.tour);
   });

   it('keeps a resolve that the page learns from its response alone, and after the reload once the stream reconnects', async () => {
      const { app, ids, transport } = await start();
      const resume = transport.holdEvents();

      await setStateInView(ids.tour, 'Resolve');
      await waitFor(() => expect(threadView().getByRole('button', { name: 'Reopen' })).toBeTruthy());
      await backToList();
      expect(shownIds()).not.toContain(ids.tour);
      resume();
      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      await settled();

      expect(shownIds()).not.toContain(ids.tour);
      expect(app.selectedThreads.find((each) => each.id === ids.tour)!.state).toBe('resolved');
   });

   it("keeps a resolve's response over a load that the server answered before the resolve, and takes the event of a later reopen", async () => {
      const { app, ids, transport, world, tourId } = await start();
      const reload = transport.holdAnswered('read_state');
      transport.cutEventStreams();
      await reload.answered;

      await setStateInView(ids.tour, 'Resolve');
      await waitFor(() => expect(threadView().getByRole('button', { name: 'Reopen' })).toBeTruthy());
      await backToList();
      expect(shownIds()).not.toContain(ids.tour);
      await reload.release();
      await settled();

      expect(app.selectedThreads.find((each) => each.id === ids.tour)!.state).toBe('resolved');
      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      // Another view of the tour reopens the thread.
      await world.reviewer.call('set_thread_state', { tourId, threadId: ids.tour, state: 'open' });
      await waitFor(() => expect(shownIds()).toContain(ids.tour));
   });

   it('hides an outdated thread until the reviewer shows outdated threads, and lets the reviewer reopen or resolve it', async () => {
      let stale = '';
      const { world, tourId } = await start({
         before: async (world, claude, seed) => {
            stale = await answered(world, claude, seed.tourId, 'stale', { kind: 'tour' });
            await claude.call('outdate_thread', { tourId: seed.tourId, threadId: stale });
         },
      });
      expect(shownIds()).not.toContain(stale);
      expect(shownOnButton()).toBe('Open, tour actions');

      await toggleShown('Outdated');
      await waitFor(() => expect(shownIds()).toContain(stale));
      expect(within(threadRow(stale)!).getByText('Outdated')).toBeTruthy();
      const view = await openThreadView(stale);
      expect(view.getByText('Outdated')).toBeTruthy();
      expect(view.getByRole('button', { name: 'Resolve' })).toBeTruthy();
      await fireEvent.click(view.getByRole('button', { name: 'Reopen' }));

      await waitFor(async () => expect(await stateOf(world, tourId, stale)).toBe('open'));
      await waitFor(() => expect(view.queryByText('Outdated')).toBeNull());
   });

   it("shows a thread that Claude starts, adds Claude's message to it, and hides it when Claude outdates it, each live", async () => {
      const { claude, tourId } = await start();

      const started = ((await claude!.call('start_thread', { tourId, text: 'The rounding needs a test.', anchor: { kind: 'tour' } })) as { thread: QuestionThread }).thread;
      await waitFor(() => expect(shownIds()).toContain(started.id));
      expect(threadRow(started.id)?.textContent).toContain('The rounding needs a test.');
      await claude!.call('add_thread_message', { tourId, threadId: started.id, text: 'Folded into the revised stop.' });
      await waitFor(() => expect(threadRow(started.id)?.textContent).toContain('Claude: Folded into the revised stop.'));
      await openThreadView(started.id);
      expect([...threadViewElement()!.querySelectorAll('[data-reply]')].map((reply) => reply.textContent?.trim())).toEqual(['The rounding needs a test.', 'Folded into the revised stop.']);
      await backToList();
      await claude!.call('outdate_thread', { tourId, threadId: started.id });

      await waitFor(() => expect(shownIds()).not.toContain(started.id));
      expect(panel().getByText('Showing 3 of 6 threads.')).toBeTruthy();
   });

   it('removes a thread that Claude deletes from the list and from the count', async () => {
      const { claude, tourId, ids } = await start();

      await claude!.call('delete_thread', { tourId, threadId: ids.roundText });

      await waitFor(() => expect(shownIds()).toEqual([ids.tour, ids.cartCode]));
      expect(panel().getByText('Showing 2 of 4 threads.')).toBeTruthy();
   });

   it('keeps the threads that the filter hides counted when an event changes one of them', async () => {
      const { world, tourId, ids } = await start();

      // Another view of the tour reopens a resolved thread.
      await world.reviewer.call('set_thread_state', { tourId, threadId: ids.cartStop, state: 'open' });

      await waitFor(() => expect(shownIds()).toEqual([ids.tour, ids.roundText, ids.cartStop, ids.cartCode]));
      expect(panel().getByText('Showing 4 of 5 threads.')).toBeTruthy();
   });
});

function lastQuestionId(thread: QuestionThread): string {
   return thread.kind === 'note' ? thread.id : thread.messages[0].id;
}
