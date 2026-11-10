import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { createRawSnippet } from 'svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import PlanActions from '../../src/components/PlanActions.svelte';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { versionOf } from '../../src/lib/stops';
import { messagesOf } from '../../src/lib/threadView';
import type { Familiarity } from '../../src/lib/types';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openStop, openTour, readTour, seedTour, settled, startApp, type SeededTour } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { planPanel } from './appPanels';
import {
   chooseFromPlanMenu,
   closePlanMenu,
   isItemDisabled,
   isSelected,
   levelOption,
   openPlanMenu,
   planActions,
   planMenuButton,
   planMenuItem,
   planMenuNames,
   reasonOf,
   sendButton,
   sendLevelChange,
   wordsBox,
   writeWords,
} from './planMenu';
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

const rewriting = 'Claude is rewriting the stops you have not read for the new familiarity level.';

// Four stops at the level "some", the third pending, so a change from the first stop rewrites the second and the fourth.
const plan = backend().template('familiarity-plan', async (world) => {
   const seeded = await seedTour(world, {
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'Cart adds', kind: 'inspect', content: cartContent('Cart adds.') },
               { title: 'Line totals', kind: 'inspect', content: cartContent('Line totals.') },
               { title: 'Checkout', kind: 'inspect' },
               { title: 'Gateway errors', kind: 'inspect', content: cartContent('Gateway errors.') },
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

async function boundWorld(): Promise<{ world: World; claude: ClaudeSession } & Seed> {
   const seeded = await planWorld();
   return { ...seeded, claude: await attachClaude(seeded.world, seeded.tourId) };
}

// Opens the tour with the stop open, or none, and shows the Plan panel.
async function open(world: World, stopId: string | null): Promise<{ app: LotsenApp; transport: Transport }> {
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

const threadsPanel = () => within(screen.getByRole('region', { name: 'Threads' }));

function sentChanges(transport: Transport): Record<string, unknown>[] {
   return transport.requests
      .filter((request) => request.operation === 'change_familiarity')
      .map(({ input }) => {
         const rest = { ...input };
         delete rest.tourId;
         return rest;
      });
}

// Changes the level as the reviewer in another page, from the stop, and returns the change's question as Claude receives it.
async function changeLevel(world: World, claude: ClaudeSession, tourId: string, familiarity: Familiarity, stopId: string): Promise<string> {
   await world.reviewer.call('change_familiarity', { tourId, familiarity, stopId });
   return String((await claude.next('question')).questionId);
}

function sentReplans(transport: Transport): unknown[] {
   return transport.requests.filter((request) => request.operation === 'request_tour_action').map(({ input }) => input!.action);
}

const shownLevel = (level: string) => planActions().getByText(level, { selector: 'p' });

describe('the familiarity line in the Plan panel', () => {
   it("shows the thread of the change with Claude's reply as the server rendered its prose as HTML", async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const { app } = await open(world, stopIds[0]!);

      await sendLevelChange('New to the code');
      await waitFor(() => expect(app.selectedFamiliarity).toBe('new'));
      const question = await claude.next('question');
      await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: '*Rewritten* the stops.' });
      await openThreadView();

      const emphasis = await threadsPanel().findByText('Rewritten', { selector: 'em' });
      expect(emphasis.parentElement?.textContent).toBe('Rewritten the stops.');
      expect(threadsPanel().queryByText(/\*Rewritten\*/)).toBeNull();
   });

   it("shows the level's full name with a menu and no level or plan buttons at rest", async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      expect(shownLevel('Knows some of the code')).toBeTruthy();
      expect(planMenuButton()).not.toBeNull();
      for (const name of ['Familiar', 'Some', 'New', 'Familiar with the code', 'Knows some of the code', 'New to the code', 'Re-plan', 'Reorder']) {
         expect(planPanel().queryByRole('button', { name })).toBeNull();
      }

      expect(planPanel().queryAllByRole('radio')).toEqual([]);
   });

   it('holds Re-plan… and Reorder… in its menu', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      const menu = await openPlanMenu();

      expect(planMenuNames(menu)).toEqual(['Re-plan…', 'Reorder…']);
      await closePlanMenu();
   });

   it("selects the tour's level as now in the form, and a pick sends nothing until Send", async () => {
      const { world, stopIds } = await boundWorld();
      const { app, transport } = await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Re-plan…');
      expect(isSelected(levelOption('Knows some of the code now'))).toBe(true);
      await fireEvent.click(levelOption('New to the code'));

      expect(isSelected(levelOption('New to the code'))).toBe(true);
      expect(sentChanges(transport)).toEqual([]);
      await fireEvent.click(sendButton());

      await waitFor(() => expect(sentChanges(transport)).toEqual([{ familiarity: 'new', stopId: stopIds[0] }]));
      await waitFor(() => expect(shownLevel('New to the code')).toBeTruthy());
      expect(app.selectedFamiliarity).toBe('new');
      await waitFor(() => expect(app.selectedThreads).toEqual([expect.objectContaining({ decision: { kind: 'familiarityChanged', from: 'some', to: 'new', stopIds: [stopIds[1], stopIds[3]] } })]));
      await waitFor(() => expect(planPanel().queryAllByRole('radio')).toEqual([]));
   });

   it('tells what another level does only once one is selected', async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);
      const effect = /Claude rewrites the stops after the one you view, or every stop when none is open, and may add or drop context stops\. This can take a while\./;

      await chooseFromPlanMenu('Re-plan…');
      expect(planActions().queryByText(effect)).toBeNull();

      await fireEvent.click(levelOption('Familiar with the code'));

      expect(planActions().getByText(effect)).toBeTruthy();
   });

   it('sends one change of the level with the words, from the form', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const { transport } = await open(world, stopIds[0]!);

      await sendLevelChange('New to the code', 'Skip the glossary.');

      await waitFor(() => expect(sentChanges(transport)).toEqual([{ familiarity: 'new', stopId: stopIds[0], text: 'Skip the glossary.' }]));
      const question = await claude.next('question');
      const fetched = (await claude.call('fetch_question', { tourId, questionId: question.questionId })) as { thread: { messages: { text: string }[] } };
      expect(fetched.thread.messages[0]!.text).toBe('Skip the glossary.');
      expect(sentReplans(transport)).toEqual([]);
   });

   it('leaves whitespace words out of the change', async () => {
      const { world, stopIds } = await boundWorld();
      const { transport } = await open(world, stopIds[0]!);

      await sendLevelChange('Familiar with the code', '  ');

      await waitFor(() => expect(sentChanges(transport)).toEqual([{ familiarity: 'familiar', stopId: stopIds[0] }]));
   });

   it('sends the replan at the unchanged level with words', async () => {
      const { world, stopIds } = await boundWorld();
      const { transport } = await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Re-plan…');
      await writeWords('How should the plan change?', 'Add a stop on the tests.');
      await fireEvent.click(sendButton());

      await waitFor(() => expect(sentReplans(transport)).toEqual([{ kind: 'replan' }]));
      expect(sentChanges(transport)).toEqual([]);
   });

   it("disables Send at the unchanged level without words, and the words box's label and Optional tag follow the level", async () => {
      const { world, stopIds } = await boundWorld();
      await open(world, stopIds[0]!);

      await chooseFromPlanMenu('Re-plan…');
      expect(sendButton().disabled).toBe(true);
      expect(wordsBox('How should the plan change?').hasAttribute('placeholder')).toBe(false);

      await fireEvent.click(levelOption('New to the code'));
      expect(sendButton().disabled).toBe(false);
      expect(wordsBox('Anything else Claude should change? Optional').placeholder).toBe('Leave it empty and Claude only rewrites the stops for the new level.');

      await fireEvent.click(levelOption('Knows some of the code now'));
      expect(sendButton().disabled).toBe(true);
      expect(wordsBox('How should the plan change?').hasAttribute('placeholder')).toBe(false);
   });

   it('sends no stop when none is open', async () => {
      const { world } = await boundWorld();
      const { transport } = await open(world, null);

      await sendLevelChange('Familiar with the code');

      await waitFor(() => expect(sentChanges(transport)).toEqual([{ familiarity: 'familiar' }]));
   });

   it('refuses the change without sending it while the open stop is one that the plan lacks, as when a history entry names a dropped stop', async () => {
      const { world } = await boundWorld();
      const { app, transport } = await open(world, null);
      app.openStop('missing');

      expect(await app.changeFamiliarity('new')).toBe('Open a stop first.');

      expect(sentChanges(transport)).toEqual([]);
      expect(app.selectedFamiliarity).toBe('some');
   });

   it('shows the waiting change until Claude resolves it, with Re-plan… disabled and a link to its thread', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const { app } = await open(world, stopIds[0]!);

      await sendLevelChange('New to the code');

      expect(await planActions().findByText(rewriting)).toBeTruthy();
      const menu = await openPlanMenu();
      expect(isItemDisabled(planMenuItem(menu, 'Re-plan…'))).toBe(true);
      await closePlanMenu();
      await fireEvent.click(planActions().getByRole('button', { name: 'Show the thread' }));
      const thread = (await readTour(world, tourId)).threads[0]!;
      await waitFor(() => expect(app.openedThread).toEqual({ id: thread.id }));
      await claude.call('reply', { tourId, questionId: messagesOf(thread)[0]!.id, resolution: 'done', text: 'Rewrote two stops.' });
      await waitFor(() => expect(planActions().queryByText(rewriting)).toBeNull());
      const after = await openPlanMenu();
      await waitFor(() => expect(isItemDisabled(planMenuItem(after, 'Re-plan…'))).toBe(false));
      await closePlanMenu();
   });

   it("shows a waiting change once, and the plan's actions disabled with the server's reason while it waits", async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await changeLevel(world, claude, tourId, 'new', stopIds[0]!);
      await open(world, stopIds[0]!);

      await waitFor(() => expect(planPanel().getAllByText(rewriting)).toHaveLength(1));
      expect(planActions().getAllByRole('button', { name: 'Show the thread' })).toHaveLength(1);
      const menu = await openPlanMenu();
      const replan = planMenuItem(menu, 'Re-plan…');
      expect(isItemDisabled(replan)).toBe(true);
      expect(reasonOf(replan)).toMatch(/writes stops at the familiarity level/);
      await closePlanMenu();
   });

   it("keeps Re-plan… open for a change of the level while a replan waits, and holds a replan's words back with the server's reason", async () => {
      const { world, tourId, stopIds } = await boundWorld();
      await world.reviewer.call('request_tour_action', { tourId, action: { kind: 'replan' }, text: 'Fewer stops.' });
      const { transport } = await open(world, stopIds[0]!);

      await waitFor(() => expect(planActions().getByText('Claude is re-planning the tour.')).toBeTruthy());
      const menu = await openPlanMenu();
      await waitFor(() => expect(isItemDisabled(planMenuItem(menu, 'Re-plan…'))).toBe(false));
      await closePlanMenu();
      await chooseFromPlanMenu('Re-plan…');
      await writeWords('How should the plan change?', 'Add a stop on the tests.');

      expect(sendButton().disabled).toBe(true);
      expect(planActions().getAllByText(/Claude still re-plans the tour as you asked/)).toHaveLength(1);
      await fireEvent.click(levelOption('New to the code'));
      expect(sendButton().disabled).toBe(false);
      await fireEvent.click(sendButton());
      await waitFor(() => expect(sentChanges(transport)).toEqual([{ familiarity: 'new', stopId: stopIds[0], text: 'Add a stop on the tests.' }]));
   });

   it("disables the menu's Re-plan… with the server's reason while no session is attached", async () => {
      const { world, stopIds } = await planWorld();
      await open(world, stopIds[0]!);

      await waitFor(() => expect(planActions().getByText(/is attached to no Claude Code session/)).toBeTruthy());
      const menu = await openPlanMenu();
      const replan = planMenuItem(menu, 'Re-plan…');
      expect(isItemDisabled(replan)).toBe(true);
      expect(reasonOf(replan)).toMatch(/is attached to no Claude Code session/);
      await closePlanMenu();
   });

   it('becomes available when a session attaches', async () => {
      const { world, tourId, stopIds } = await planWorld();
      await open(world, stopIds[0]!);
      await waitFor(() => expect(planActions().getByText(/is attached to no Claude Code session/)).toBeTruthy());

      await attachClaude(world, tourId);

      await waitFor(() => expect(planActions().queryByText(/is attached to no Claude Code session/)).toBeNull());
      const menu = await openPlanMenu();
      expect(isItemDisabled(planMenuItem(menu, 'Re-plan…'))).toBe(false);
      await closePlanMenu();
   });

   it("shows the server's refusal in the form and keeps the level", async () => {
      const { world, stopIds, claude } = await boundWorld();
      const { transport } = await open(world, stopIds[0]!);
      const release = transport.hold('change_familiarity', 'request');

      await sendLevelChange('New to the code');
      await waitFor(() => expect(sentChanges(transport)).toHaveLength(1));
      // The session ends before the change reaches the server, which then has no session to give it to.
      await claude.drop();
      release();

      expect((await planActions().findByRole('alert')).textContent).toContain('is attached to no Claude Code session');
      expect(shownLevel('Knows some of the code')).toBeTruthy();
      expect(isSelected(levelOption('New to the code'))).toBe(true);
   });

   it('shows before Claude writes the plan, with only Change familiarity… in its menu, which opens the form without the words box', async () => {
      const world = await backend().world();
      await seedTour(world);
      const { transport } = await open(world, null);

      expect(shownLevel('Knows some of the code')).toBeTruthy();
      expect(planPanel().getByText('Claude has not written the plan yet.')).toBeTruthy();
      const menu = await openPlanMenu();
      expect(planMenuNames(menu)).toEqual(['Change familiarity…']);
      await closePlanMenu();
      await chooseFromPlanMenu('Change familiarity…');

      expect(planActions().queryByRole('textbox')).toBeNull();
      await waitFor(() => expect(document.activeElement).toBe(levelOption('Knows some of the code')));
      expect(sendButton().disabled).toBe(true);
      await fireEvent.click(levelOption('New to the code'));
      await fireEvent.click(sendButton());

      await waitFor(() => expect(sentChanges(transport)).toEqual([{ familiarity: 'new' }]));
   });

   it("keeps a newer level from an event when the change's response arrives later", async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const { app, transport } = await open(world, stopIds[0]!);
      const release = transport.hold('change_familiarity');

      const changed = app.changeFamiliarity('new');
      await waitFor(async () => expect((await readTour(world, tourId)).familiarity).toBe('new'));
      // Claude carries the change out, and another page changes the level later than the held response.
      const question = await claude.next('question');
      await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Rewrote the stops.' });
      await world.clock.advance(60_000);
      await settled(app);
      // The server publishes the level's event before its thread's event, so the stall lets the page see the new level and brings the thread only after the held response.
      const unstall = transport.stallEventStreamsAfter('familiarityChanged');
      await world.reviewer.call('change_familiarity', { tourId, familiarity: 'familiar' });
      await waitFor(() => expect(app.selectedFamiliarity).toBe('familiar'));
      release();

      expect(await changed).toBeNull();
      expect(app.selectedFamiliarity).toBe('familiar');
      expect(app.selectedThreads).toHaveLength(1);
      unstall();
      await waitFor(() => expect(app.selectedThreads).toHaveLength(2));
      expect(shownLevel('Familiar with the code')).toBeTruthy();
   });

   it.each(['declined', 'canceled', 'unanswered'] as const)('asks Claude again at the current level when its last change was %s, from the stop under view now', async (state) => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const questionId = await changeLevel(world, claude, tourId, 'new', stopIds[0]!);
      if (state === 'declined') {
         await claude.call('reply', { tourId, questionId, resolution: 'declined', text: 'Not now.' });
      } else if (state === 'canceled') {
         await world.reviewer.call('cancel_question', { tourId, questionId });
      } else {
         await claude.drop();
         await attachClaude(world, tourId);
      }

      const { app, transport } = await open(world, stopIds[1]!);

      await waitFor(() => expect(planActions().getByText(/To ask Claude again, open Re-plan… in the ⋯ menu/)).toBeTruthy());
      await chooseFromPlanMenu('Re-plan…');
      const current = levelOption('New to the code, ask Claude again now');
      expect(isSelected(current)).toBe(true);
      expect(current.disabled).toBe(false);
      await fireEvent.click(sendButton());

      await waitFor(() => expect(sentChanges(transport)).toEqual([{ familiarity: 'new', stopId: stopIds[1] }]));
      expect(await planActions().findByText(rewriting)).toBeTruthy();
      await waitFor(() => expect(app.selectedThreads.at(-1)).toEqual(expect.objectContaining({ decision: { kind: 'familiarityChanged', from: 'some', to: 'new', stopIds: [stopIds[3]] } })));
      expect(app.selectedFamiliarity).toBe('new');
   });

   it('does not ask again at the current level when Claude carried out its latest change, though an earlier one to it was declined', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const declined = await changeLevel(world, claude, tourId, 'new', stopIds[0]!);
      await claude.call('reply', { tourId, questionId: declined, resolution: 'declined', text: 'Not now.' });
      const done = await changeLevel(world, claude, tourId, 'new', stopIds[0]!);
      await claude.call('reply', { tourId, questionId: done, resolution: 'done', text: 'Rewrote the stops.' });
      const { transport } = await open(world, stopIds[1]!);

      await waitFor(() => expect(planActions().queryByText(/is attached to/)).toBeNull());
      expect(planActions().queryByText(/ask Claude again/)).toBeNull();
      await chooseFromPlanMenu('Re-plan…');
      expect(sendButton().disabled).toBe(true);

      expect(sentChanges(transport)).toEqual([]);
   });

   it('asks again at the current level when Claude declined its latest change, though an earlier one to it was carried out', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      for (const [familiarity, resolution] of [
         ['new', 'done'],
         ['familiar', 'done'],
         ['new', 'declined'],
      ] as const) {
         const questionId = await changeLevel(world, claude, tourId, familiarity, stopIds[0]!);
         await claude.call('reply', { tourId, questionId, resolution, text: resolution === 'done' ? 'Rewrote the stops.' : 'Not now.' });
      }

      await open(world, stopIds[1]!);

      await waitFor(() => expect(planActions().getByText(/To ask Claude again/)).toBeTruthy());
      await chooseFromPlanMenu('Re-plan…');
      expect(levelOption('New to the code, ask Claude again now').disabled).toBe(false);
   });

   it("shows each tour's own level", async () => {
      const { world, claude } = await boundWorld();
      const other = (await claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/43', familiarity: 'new' })) as { id: string };
      await world.idle();
      await claude.call('set_plan', { tourId: other.id, chapters: [{ id: 'c1', title: 'Cart' }], stops: [{ title: 'Cart adds', kind: 'inspect', chapter: 'c1' }] });
      await open(world, null);

      (await openTourList())
         .getAllByRole('button', tourButton)
         .find((button) => button.textContent?.includes('shop #43'))!
         .click();
      await waitFor(() => expect(screen.getByRole('button', { name: 'Tour: shop #43, choose another' })).toBeTruthy());
      await showPlanPanel();

      await waitFor(() => expect(shownLevel('New to the code')).toBeTruthy());
   });

   it("disables the radio options with the server's reason while the level's offer is closed, and the words still send a replan", async () => {
      const { world, stopIds } = await boundWorld();
      const { app, transport } = await open(world, stopIds[0]!);
      await waitFor(() => expect(app.selectedControls).not.toBeNull());
      const controls = app.selectedControls!;
      const closed = { ...controls, offers: { ...controls.offers, level: { change: { state: 'closed' as const, code: 'questionWaiting' as const, reason: 'the level is held back.' } } } };
      const sent: unknown[] = [];
      const onaction = (action: unknown, text: string) => {
         sent.push([action, text]);
         return Promise.resolve(null);
      };
      const children = createRawSnippet(() => ({ render: () => '<p>plan</p>' }));
      render(PlanActions, {
         props: { plan: app.selectedPlan!.plan!, familiarity: 'some', threads: [], controls: closed, onaction, onfamiliarity: () => Promise.resolve(null), onopenthread: () => {}, children },
      });
      const form = within(screen.getAllByRole('region', { name: 'Plan actions' }).at(-1)!);

      await fireEvent.keyDown(form.getByRole('button', { name: 'Plan actions' }), { key: 'Enter' });
      await fireEvent.click(within(await screen.findByRole('menu', { hidden: true })).getByRole('menuitem', { name: 'Re-plan…', hidden: true }));
      const radios = await form.findAllByRole('radio');

      for (const radio of radios) {
         expect((radio as HTMLButtonElement).disabled).toBe(true);
      }

      expect(form.getByText('The level is held back.')).toBeTruthy();
      expect(form.getByRole('radiogroup', { name: 'Your familiarity' }).getAttribute('aria-describedby')).toBe(form.getByText('The level is held back.').id);
      await fireEvent.input(form.getByRole('textbox', { name: 'How should the plan change?' }), { target: { value: 'Add a stop on the tests.' } });
      await fireEvent.click(form.getByRole('button', { name: 'Send to Claude' }));

      await waitFor(() => expect(sent).toEqual([[{ kind: 'replan' }, 'Add a stop on the tests.']]));
      expect(sentChanges(transport)).toEqual([]);
   });
});

describe('the thread of a change of the familiarity level', () => {
   it('offers a cancel while the change waits, and after it a send again that the server keeps disabled', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await changeLevel(world, claude, tourId, 'new', stopIds[0]!);
      await open(world, stopIds[0]!);
      await openThreadView();

      await fireEvent.click(await threadsPanel().findByRole('button', { name: 'Cancel' }));

      expect(await threadsPanel().findByText('Canceled. Claude may still answer it.')).toBeTruthy();
      const again = (await threadsPanel().findByRole('button', { name: 'Send again' })) as HTMLButtonElement;
      await waitFor(() => expect(again.disabled).toBe(true));
      expect(threadsPanel().getByText(/does not go again/)).toBeTruthy();
   });

   it('names the levels that the change went between', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const questionId = await changeLevel(world, claude, tourId, 'new', stopIds[0]!);
      await claude.call('reply', { tourId, questionId, resolution: 'done', text: 'Rewrote the stops.' });
      await open(world, stopIds[0]!);
      await openThreadView();

      const threads = screen.getByRole('region', { name: 'Threads' });
      await waitFor(() => expect(threads.querySelector('[data-action]')?.textContent).toBe('Your decision Change familiarity: Knows some of the code → New to the code'));
   });
});

describe('a revision while a change of the familiarity level waits', () => {
   it('shows the new version of the stop under view without holding the one the reviewer read', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      await changeLevel(world, claude, tourId, 'new', stopIds[0]!);
      const { app } = await open(world, stopIds[1]!);

      await claude.call('put_stop', { tourId, stopId: stopIds[1], ...cartContent('Line totals, for a reviewer new to the code.') });

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it('holds the version the reviewer read once Claude resolved the change', async () => {
      const { world, tourId, stopIds, claude } = await boundWorld();
      const questionId = await changeLevel(world, claude, tourId, 'new', stopIds[0]!);
      await claude.call('reply', { tourId, questionId, resolution: 'done', text: 'Rewrote the stops.' });
      const { app } = await open(world, stopIds[1]!);

      await claude.call('put_stop', { tourId, stopId: stopIds[1], ...cartContent('Line totals, later.') });

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'revised' } });
   });
});
