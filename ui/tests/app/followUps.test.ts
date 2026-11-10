import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import { actionOf, messagesOf } from '../../src/lib/threadView';
import { followUpKinds, tourActionLabels, tourActionPending } from '../../src/lib/tourActions';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { AnswerType, FollowUpKind, QuestionThread, Tour } from '../../src/lib/types';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openTour, readTour, seedTour, startApp } from '../harness';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

interface Seed {
   tourId: string;
   stopId: string;
}

// The server's reasons for a follow-up action whose group holds no item that Claude can read.
const emptyReasons: Record<FollowUpKind, string> = {
   draftWorkItems: 'No decision has been sent to Claude yet. Answer a check with a decision, then send your answers.',
   walkOpenQuestions: 'No open question has been sent to Claude yet. Answer a check with an open question, then send your answers.',
   draftComments: 'No concern has been sent to Claude yet, and every risk stop is still unwritten or skipped. Answer a check with a concern, then send your answers.',
};

// What the server says Claude still does while a follow-up action of the kind waits.
const waitingWords: Record<FollowUpKind, RegExp> = {
   draftWorkItems: /Claude still proposes work items for the decisions/,
   walkOpenQuestions: /Claude still walks through the open questions/,
   draftComments: /Claude still drafts comments from the concerns/,
};

// The accessible name and the placeholder of each follow-up action's words box.
const wordsBoxes: Record<FollowUpKind, { name: string; placeholder: string }> = {
   draftWorkItems: { name: 'Where should the work items go, or what should they hold? Optional', placeholder: 'Leave it empty and Claude proposes work items as it sees fit.' },
   walkOpenQuestions: { name: 'What should Claude weigh in its recommendations? Optional', placeholder: 'Leave it empty and Claude weighs each question on its own.' },
   draftComments: { name: 'What tone or focus should the comments have? Optional', placeholder: 'Leave it empty and Claude chooses the tone and focus.' },
};

async function answer(world: World, tourId: string, stopId: string, checkId: string, text: string, type: AnswerType): Promise<void> {
   await world.reviewer.call('answer_check', { tourId, stopId, checkId, text });
   if (type !== 'decision') {
      await world.reviewer.call('set_answer_type', { tourId, stopId, checkId, type });
   }
}

/** Seeds a question stop with four checks, and answers the first three with a decision, a concern and an open question, sent when `send` holds, and the fourth with a draft decision. */
async function seedAnswers(world: World, send: boolean): Promise<Seed> {
   const checks = ['Round once?', 'Is a zero quantity safe?', 'Merge the lines?', 'Log the drift?'].map((question) => ({ question }));
   const seeded = await seedTour(world, { chapters: [{ title: 'Checkout', stops: [{ title: 'Cart totals', kind: 'question', content: { ...cartContent('Totals round.'), checks } }] }] });
   const {
      tourId,
      stopIds: [stopId],
   } = seeded;
   const stop = (await readTour(world, tourId)).plan!.stops[0]!;
   const [c1, c2, c3, c4] = stop.stage === 'written' ? stop.content.checks!.map((check) => check.id) : [];
   await answer(world, tourId, stopId!, c1!, 'Round once.', 'decision');
   await answer(world, tourId, stopId!, c2!, 'A zero would crash the page.', 'concern');
   await answer(world, tourId, stopId!, c3!, 'Which does the order page expect?', 'openQuestion');
   if (send) {
      await world.reviewer.call('send_answers', { tourId });
   }

   await answer(world, tourId, stopId!, c4!, 'DRAFT-WORDS', 'decision');
   return { tourId, stopId: stopId! };
}

const sent = backend().template('followUps-sent', (world) => seedAnswers(world, true));
const drafts = backend().template('followUps-drafts', (world) => seedAnswers(world, false));

/** A world copied from the template, with a bound session unless `bound` is false. */
async function followUpWorld(template = sent, bound = true): Promise<{ world: World; seed: Seed; claude: ClaudeSession | null }> {
   const world = await backend().world({ template });
   const seed = world.seed as Seed;
   return { world, seed, claude: bound ? await attachClaude(world, seed.tourId) : null };
}

/** Adds a risk stop with content to the plan as Claude, after the question stop. */
async function addRisk(claude: ClaudeSession, seed: Seed): Promise<string> {
   const plan = (await claude.call('set_plan', {
      tourId: seed.tourId,
      chapters: [{ id: 'c1', title: 'Checkout' }],
      stops: [
         { id: seed.stopId, title: 'Cart totals', kind: 'question', chapter: 'c1' },
         { title: 'Gateway errors', kind: 'risk', chapter: 'c1' },
      ],
   })) as Tour;
   const riskId = plan.plan!.stops[1]!.id;
   await claude.call('put_stop', { tourId: seed.tourId, stopId: riskId, ...cartContent('It fails.'), scenario: 'The gateway times out.' });
   return riskId;
}

async function openSummary(world: World, transport?: Transport): Promise<LotsenApp> {
   const { app } = await startApp(world, { transport });
   await openTour(app);
   app.openSummary();
   await waitFor(() => expect(app.selectedSummary?.state).toBe('loaded'));
   return app;
}

const actions = () => within(within(screen.getByRole('main', { name: 'Summary' })).getByRole('group', { name: 'Summary actions' }));
const button = (kind: FollowUpKind) => actions().getByRole('button', { name: tourActionLabels[kind] }) as HTMLButtonElement;
const enabled = (kind: FollowUpKind) => waitFor(() => expect(button(kind).disabled).toBe(false));

async function followUpThreads(world: World, tourId: string, kind: FollowUpKind): Promise<QuestionThread[]> {
   return (await readTour(world, tourId)).threads.filter((thread) => actionOf(thread)?.kind === kind);
}

describe('the follow-up actions on the summary page', () => {
   it.each(followUpKinds)("%s sends its action on the tour with the reviewer's words, and shows it waiting with a link to its thread", async (kind) => {
      const { world, seed } = await followUpWorld();
      const app = await openSummary(world);
      await enabled(kind);

      await fireEvent.click(button(kind));
      await fireEvent.input(actions().getByRole('textbox', { name: wordsBoxes[kind].name }), { target: { value: 'Keep it short.' } });
      await fireEvent.click(actions().getByRole('button', { name: 'Send to Claude' }));

      expect(await actions().findByText(tourActionPending[kind])).toBeTruthy();
      const [thread] = await followUpThreads(world, seed.tourId, kind);
      expect(messagesOf(thread!)[0]!.text).toBe('Keep it short.');
      expect(actions().queryByRole('alert')).toBeNull();
      await waitFor(() => expect(button(kind).disabled).toBe(true));
      expect(actions().getByText(waitingWords[kind])).toBeTruthy();
      await fireEvent.click(actions().getByRole('button', { name: 'Show the thread' }));
      await waitFor(() => expect(app.openedThread).toEqual({ id: thread!.id }));
   });

   it('names the work items action Draft work items and requests it with the kind draftWorkItems', async () => {
      const { world, seed } = await followUpWorld();
      const transport = world.transport();
      await openSummary(world, transport);
      await enabled('draftWorkItems');

      await fireEvent.click(actions().getByRole('button', { name: 'Draft work items' }));
      await fireEvent.click(actions().getByRole('button', { name: 'Send to Claude' }));

      await waitFor(() => expect(transport.requests.find((request) => request.operation === 'request_tour_action')?.input).toMatchObject({ tourId: seed.tourId, action: { kind: 'draftWorkItems' } }));
   });

   it.each(followUpKinds)('%s tags its words box as optional, says what an empty box does, and gives it the focus', async (kind) => {
      const { world } = await followUpWorld();
      await openSummary(world);
      await enabled(kind);

      await fireEvent.click(button(kind));

      const box = actions().getByRole('textbox', { name: wordsBoxes[kind].name }) as HTMLTextAreaElement;
      expect(box.placeholder).toBe(wordsBoxes[kind].placeholder);
      await waitFor(() => expect(document.activeElement).toBe(box));
   });

   it('leaves the other kinds enabled while one waits, and Summarize too', async () => {
      const { world, seed } = await followUpWorld();
      await world.reviewer.call('request_tour_action', { tourId: seed.tourId, action: { kind: 'draftWorkItems' } });
      await openSummary(world);

      await waitFor(() => expect(button('draftWorkItems').disabled).toBe(true));
      expect(button('walkOpenQuestions').disabled).toBe(false);
      expect(button('draftComments').disabled).toBe(false);
      expect((actions().getByRole('button', { name: 'Summarize' }) as HTMLButtonElement).disabled).toBe(false);
   });

   it.each(followUpKinds)("%s is disabled with the server's reason while its group holds only drafts", async (kind) => {
      const { world } = await followUpWorld(drafts);
      await openSummary(world);

      const reason = await actions().findByText(emptyReasons[kind]);
      expect(button(kind).disabled).toBe(true);
      expect(button(kind).getAttribute('aria-describedby')).toBe(reason.id);
   });

   it('counts a risk stop that Claude wrote as a concern', async () => {
      const { world, seed, claude } = await followUpWorld(drafts);
      await addRisk(claude!, seed);
      await openSummary(world);

      await enabled('draftComments');
      expect(button('draftWorkItems').disabled).toBe(true);
   });

   it("disables Draft PR comments with the server's reason when no concern is sent and the only risk stop is skipped", async () => {
      const { world, seed, claude } = await followUpWorld(drafts);
      const riskId = await addRisk(claude!, seed);
      await world.reviewer.call('set_stop_skipped', { tourId: seed.tourId, stopId: riskId, skipped: true });
      await openSummary(world);

      const reason = await actions().findByText(emptyReasons.draftComments);
      expect(button('draftComments').disabled).toBe(true);
      expect(button('draftComments').getAttribute('aria-describedby')).toBe(reason.id);
   });

   it.each(followUpKinds)("%s is disabled with the server's reason while no session is attached", async (kind) => {
      const { world } = await followUpWorld(sent, false);
      await openSummary(world);

      await waitFor(() => expect(button(kind).disabled).toBe(true));
      const reason = within(button(kind).closest('div')!.parentElement!).getByText(/is attached to no Claude Code session/);
      expect(button(kind).getAttribute('aria-describedby')).toBe(reason.id);
   });

   it('is enabled once the reviewer sends the answers', async () => {
      const { world, seed } = await followUpWorld(drafts);
      await openSummary(world);
      await waitFor(() => expect(button('draftWorkItems').disabled).toBe(true));

      await world.reviewer.call('send_answers', { tourId: seed.tourId });

      await enabled('draftWorkItems');
      expect(actions().queryByText(emptyReasons.draftWorkItems)).toBeNull();
   });

   it('shows a follow-up that waits from the load, and is enabled again once Claude replies', async () => {
      const { world, seed, claude } = await followUpWorld();
      await world.reviewer.call('request_tour_action', { tourId: seed.tourId, action: { kind: 'walkOpenQuestions' } });
      await openSummary(world);

      expect(await actions().findByText(tourActionPending.walkOpenQuestions)).toBeTruthy();
      expect(button('walkOpenQuestions').disabled).toBe(true);

      const question = await claude!.next('question');
      await claude!.call('reply', { tourId: seed.tourId, questionId: question.questionId, resolution: 'done', text: 'Started a thread.' });

      await enabled('walkOpenQuestions');
      expect(actions().queryByText(tourActionPending.walkOpenQuestions)).toBeNull();
   });

   it("shows the server's refusal and keeps the reviewer's words", async () => {
      const { world } = await followUpWorld();
      await openSummary(world);
      await enabled('draftComments');
      // The words break a limit that the tour's controls do not check, so the button stays enabled and the server refuses.
      const words = 'x'.repeat(10_001);

      await fireEvent.click(button('draftComments'));
      await fireEvent.input(actions().getByRole('textbox'), { target: { value: words } });
      await fireEvent.click(actions().getByRole('button', { name: 'Send to Claude' }));

      expect((await actions().findByRole('alert')).textContent).toMatch(/holds at most 10000 characters/);
      expect((actions().getByRole('textbox') as HTMLTextAreaElement).value).toBe(words);
   });
});
