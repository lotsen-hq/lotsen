import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { messagesOf } from '../../src/lib/threadView';
import type { QuestionThread } from '../../src/lib/types';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, cartBase, cartContent, openStop, openTour, prLabel, readTour, seedTour, startApp, withHolds, type HeldTransport } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { aboutChoices, askAbout, askedAbout } from './askBox';
import { backToList, openThreadView, threadView, threadViewElement } from './threadView';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

// A negative check needs the page to have handled what the server sent, which no condition on the page shows.
const settled = () => new Promise((resolve) => setTimeout(resolve, 50));

interface Seed {
   tourId: string;
   otherId: string;
   stopIds: [string, string];
}

const otherLabel = 'shop #43';

// Two tours without a session: the first with two written stops and a file that the change leaves alone, the second with a pending stop.
const twoTours = backend().template('askClaude-two', async (world): Promise<Seed> => {
   const { tourId, stopIds } = await seedTour(world, {
      bound: false,
      base: { ...cartBase, 'src/totals.ts': 'export const a = 1;\nexport const b = 2;\nexport const c = 3;\nexport const d = 4;\nexport const e = 5;\n' },
      chapters: [
         {
            title: 'Checkout',
            stops: [
               { title: 'Cart totals', kind: 'inspect', content: cartContent('The total adds the lines.') },
               { title: 'Rounding', kind: 'risk', content: { ...cartContent('The total may drift.'), scenario: 'A cart of many small lines totals a cent off.' } },
            ],
         },
      ],
   });
   const second = await world.claude();
   const other = (await second.call('create_tour', { prUrl: 'https://platform.test/shop/pull/43', familiarity: 'some' })) as { id: string };
   await world.idle();
   await second.call('set_plan', { tourId: other.id, chapters: [{ id: 'c1', title: 'Checkout' }], stops: [{ title: 'Cart totals', kind: 'inspect', chapter: 'c1' }] });
   await second.drop();
   return { tourId, otherId: other.id, stopIds: stopIds as [string, string] };
});

const noSession = /attached to no Claude Code session/;
const topBar = () => screen.getByRole('banner');
const threadsPanel = () => within(screen.getByRole('region', { name: 'Threads' }));
const askBox = () => threadsPanel().getByRole('textbox', { name: 'Question for Claude' }) as HTMLTextAreaElement;
const sendButton = () => threadsPanel().getByRole('button', { name: 'Send' }) as HTMLButtonElement;
const threadList = () => within(threadsPanel().getByRole('list', { name: 'Threads' }));
/** Waits for the thread list, which shows once the tour has a thread. */
const listed = async () => within(await threadsPanel().findByRole('list', { name: 'Threads' }));
/** Opens the thread view of the newest thread, once the thread list shows it. */
const opened = () => openThreadView();
const shown = () => threadView();

/** The text of the elements that describe an element, as a screen reader reads its description. */
function description(element: Element): string {
   return (element.getAttribute('aria-describedby') ?? '')
      .split(/\s+/)
      .filter(Boolean)
      .map((id) => document.getElementById(id)?.textContent?.trim() ?? '')
      .join(' ');
}

interface Started extends Seed {
   world: World;
   app: LotsenApp;
   transport: HeldTransport;
}

/** Starts the app on a copy of the two tours, after `before` ran on the world, without opening a tour. */
async function launch(before: (world: World, seed: Seed) => Promise<void> = async () => {}): Promise<Started> {
   const world = await backend().world({ template: twoTours });
   const seed = world.seed as Seed;
   await before(world, seed);
   const transport = withHolds(world.transport());
   const { app } = await startApp(world, { transport });
   return { ...seed, world, app, transport };
}

/** Starts the app with the first tour open, and with a session attached to it at the level, or none for null. */
async function start(level: 'unconfirmed' | 'confirmed' | null = 'confirmed'): Promise<Started & { claude: ClaudeSession | null }> {
   let claude: ClaudeSession | null = null;
   const started = await launch(async (world, { tourId }) => {
      claude = level === null ? null : await attachClaude(world, tourId, { confirm: level === 'confirmed' });
   });
   await select(started.app, prLabel, started.tourId);
   return { ...started, claude };
}

async function select(app: LotsenApp, label: string, tourId: string) {
   await openTour(app, label);
   await waitFor(() => expect(app.selectedTourId).toBe(tourId));
}

/** Clicks the tour's entry in the tour list, without waiting for its load. */
async function click(label: string) {
   const list = await openTourList();
   list
      .getAllByRole('button', tourButton)
      .find((candidate) => candidate.textContent?.includes(label))!
      .click();
}

/** Asks about the tour, or about the open stop with the chip's entry that names it, such as `Stop 2`. */
async function ask(text: string, about: string = 'The tour') {
   await askAbout(about);
   await waitFor(() => expect(askedAbout()).toBe(about));
   await fireEvent.input(askBox(), { target: { value: text } });
   await fireEvent.click(sendButton());
}

/** Confirms the session's channel, as an adapter does with the nonce of its probe. */
async function confirm(claude: ClaudeSession) {
   const probe = await claude.next('probe');
   await claude.call('ack', { nonce: probe.nonce });
}

const firstThread = async (world: World, tourId: string) => (await readTour(world, tourId)).threads[0]!;
const requestsOf = (thread: QuestionThread) => {
   const question = messagesOf(thread)[0];
   return question?.kind === 'question' ? question.requests : [];
};

describe('the attachment state', () => {
   it.each([
      [null, 'No session', /No Claude session/],
      ['unconfirmed', 'Unconfirmed', /not confirmed yet/],
      ['confirmed', 'Confirmed', /reach Claude/],
   ] as const)('shows %s in the top bar with what works', async (level, label, explanation) => {
      await start(level);

      await waitFor(() => expect(topBar().textContent).toContain(label));
      expect(topBar().textContent).toMatch(explanation);
   });

   it('shows what works in a tooltip when keyboard focus reaches the state, and describes it to screen readers', async () => {
      await start('unconfirmed');

      const state = await waitFor(() => within(topBar()).getByRole('button', { name: 'Claude session: Unconfirmed' }));
      expect(description(state)).toMatch(/cancel it or send it again/);
      // jsdom shows focus as visible only after a key press, as browsers do.
      fireEvent.keyDown(document.body, { key: 'Tab' });
      state.focus();

      await waitFor(() => expect(document.querySelector('[data-tooltip-content]')?.textContent).toMatch(/cancel it or send it again/));
   });

   it('shows nothing of a session while no tour is open', async () => {
      await launch(async (world, { tourId }) => void (await attachClaude(world, tourId)));

      expect(topBar().textContent).not.toMatch(/Claude session|No session|Confirmed/);
   });

   it("follows the server's binding events for the open tour only", async () => {
      const { world, tourId, otherId } = await start(null);

      await attachClaude(world, otherId);
      const claude = await attachClaude(world, tourId, { confirm: false });
      await waitFor(() => expect(topBar().textContent).toContain('Unconfirmed'));
      await confirm(claude);
      await waitFor(() => expect(topBar().textContent).toContain('Confirmed'));
      await claude.drop();

      await waitFor(() => expect(topBar().textContent).toContain('No session'));
   });

   it('keeps a binding event that arrives while the tour loads, whose answer predates it', async () => {
      const { world, app, tourId, transport } = await launch();
      const load = transport.holdAnswered('read_state');
      await click(prLabel);
      await load.answered;

      await attachClaude(world, tourId);
      await load.release();
      await waitFor(() => expect(app.selectedPlan).toBeDefined());

      await waitFor(() => expect(topBar().textContent).toContain('Confirmed'));
      await settled();
      expect(topBar().textContent).toContain('Confirmed');
   });

   it('keeps a binding event that arrives while the list reloads after the stream reconnects', async () => {
      const { transport, claude } = await start('unconfirmed');
      await waitFor(() => expect(topBar().textContent).toContain('Unconfirmed'));

      // The list answers with the binding from before the event.
      const list = transport.holdAnswered('list_tours');
      transport.cutEventStreams();
      await list.answered;
      await confirm(claude!);
      await list.release();
      await settled();

      await waitFor(() => expect(topBar().textContent).toContain('Confirmed'));
   });

   it('shows a binding that changed while the stream was down, from the reload', async () => {
      const { transport, claude } = await start('confirmed');
      await waitFor(() => expect(topBar().textContent).toContain('Confirmed'));

      const resume = transport.holdEvents();
      await claude!.drop();
      resume();

      await waitFor(() => expect(topBar().textContent).toContain('No session'));
   });
});

describe('the ask box', () => {
   it("offers only saving a note, with the server's reason that sending is disabled, while no session is bound", async () => {
      await start(null);

      await fireEvent.input(askBox(), { target: { value: 'Why?' } });
      expect(askBox().disabled).toBe(false);
      expect(sendButton().disabled).toBe(true);
      expect((threadsPanel().getByRole('button', { name: 'Save as note' }) as HTMLButtonElement).disabled).toBe(false);
      expect(description(askBox())).toMatch(noSession);
      expect(description(sendButton())).toMatch(noSession);
      expect(threadsPanel().getByText(noSession)).toBeTruthy();
   });

   it('works while the session is unconfirmed, and says the question may not arrive', async () => {
      await start('unconfirmed');

      await waitFor(() => expect(description(askBox())).toMatch(/may not arrive/));
      expect(askBox().disabled).toBe(false);
   });

   it("says that Claude acts with the session's permissions", async () => {
      await start('confirmed');

      await waitFor(() => expect(topBar().textContent).toContain('Confirmed'));
      expect(description(askBox())).toMatch(/permissions of its session/);
      expect(description(askBox())).not.toMatch(/may not arrive/);
   });

   it('enables sending when a session attaches', async () => {
      const { world, tourId } = await start(null);

      await fireEvent.input(askBox(), { target: { value: 'Why?' } });
      expect(sendButton().disabled).toBe(true);

      await attachClaude(world, tourId);

      await waitFor(() => expect(sendButton().disabled).toBe(false));
   });

   it('asks about the open stop, with the stop as its anchor', async () => {
      const { app, world, tourId, stopIds } = await start();
      await openStop(app, stopIds[1]);

      await ask('Why round here?', 'Stop 2');

      await waitFor(async () => expect((await readTour(world, tourId)).threads).toHaveLength(1));
      const thread = await firstThread(world, tourId);
      expect(thread.anchor).toEqual({ kind: 'stop', stopId: stopIds[1] });
      expect(messagesOf(thread)[0]).toMatchObject({ text: 'Why round here?', stopId: stopIds[1] });
   });

   it('asks about the tour while no stop is open, where it cannot ask about a stop', async () => {
      const { world, tourId } = await start();

      expect(askedAbout()).toBe('The tour');
      expect(await aboutChoices()).toEqual([{ name: 'The tour', checked: true }]);
      await fireEvent.input(askBox(), { target: { value: 'What is this about?' } });
      await fireEvent.click(sendButton());

      await waitFor(async () => expect((await readTour(world, tourId)).threads).toHaveLength(1));
      expect((await firstThread(world, tourId)).anchor).toEqual({ kind: 'tour' });
   });

   it("offers the tour and the open stop in the chip's menu, with the check on the current choice", async () => {
      const { app, stopIds } = await start();
      await openStop(app, stopIds[1]);

      await waitFor(() => expect(askedAbout()).toBe('Stop 2'));
      expect(await aboutChoices()).toEqual([
         { name: 'The tour', checked: false },
         { name: 'Stop 2 · Rounding', checked: true },
      ]);

      await askAbout('The tour');

      expect(askedAbout()).toBe('The tour');
      expect(await aboutChoices()).toEqual([
         { name: 'The tour', checked: true },
         { name: 'Stop 2 · Rounding', checked: false },
      ]);
   });

   it('sends on Enter from the one-line field, where Shift+Enter adds a line', async () => {
      const { world, tourId } = await start();
      await fireEvent.input(askBox(), { target: { value: 'Is this safe?' } });

      // fireEvent returns false when a handler prevented the key's default action, which adds the line.
      expect(await fireEvent.keyDown(askBox(), { key: 'Enter', shiftKey: true })).toBe(true);
      await settled();
      expect((await readTour(world, tourId)).threads).toHaveLength(0);

      expect(await fireEvent.keyDown(askBox(), { key: 'Enter' })).toBe(false);

      await waitFor(async () => expect((await readTour(world, tourId)).threads).toHaveLength(1));
      expect(messagesOf(await firstThread(world, tourId))[0]).toMatchObject({ text: 'Is this safe?' });
   });

   it('sends on Ctrl+Enter from the expanded editor, where Enter adds a line, and keeps the words when it shrinks again', async () => {
      const { world, tourId } = await start();
      await fireEvent.input(askBox(), { target: { value: 'A longer question' } });

      await fireEvent.click(threadsPanel().getByRole('button', { name: 'Write a longer question' }));

      await waitFor(() => expect(threadsPanel().getByRole('button', { name: 'Make the box smaller' })).toBeTruthy());
      expect(threadsPanel().queryByRole('button', { name: 'Write a longer question' })).toBeNull();
      expect(askBox().value).toBe('A longer question');
      expect(askBox().rows).toBeGreaterThan(1);
      expect(await fireEvent.keyDown(askBox(), { key: 'Enter' })).toBe(true);
      await settled();
      expect((await readTour(world, tourId)).threads).toHaveLength(0);

      expect(await fireEvent.keyDown(askBox(), { key: 'Enter', ctrlKey: true })).toBe(false);

      await waitFor(async () => expect((await readTour(world, tourId)).threads).toHaveLength(1));
      expect(messagesOf(await firstThread(world, tourId))[0]).toMatchObject({ text: 'A longer question' });
      await fireEvent.input(askBox(), { target: { value: 'Kept' } });
      await fireEvent.click(threadsPanel().getByRole('button', { name: 'Make the box smaller' }));
      await waitFor(() => expect(threadsPanel().getByRole('button', { name: 'Write a longer question' })).toBeTruthy());
      expect(askBox().value).toBe('Kept');
      expect(askBox().rows).toBe(1);
   });

   it('clears the question and keeps the focus in the box after sending', async () => {
      await start();

      sendButton().focus();
      await ask('Is this safe?');

      await waitFor(() => expect(askBox().value).toBe(''));
      expect(document.activeElement).toBe(askBox());
   });

   it('keeps what the reviewer typed while the question was sending', async () => {
      const { transport } = await start();
      const held = transport.holdAnswered('ask_question');

      await ask('Is this safe?');
      await held.answered;
      await fireEvent.input(askBox(), { target: { value: 'Is this safe? And fast?' } });
      await held.release();

      await (await listed()).findByRole('listitem');
      await settled();
      expect(askBox().value).toBe('Is this safe? And fast?');
   });

   it("shows the server's reason when it refuses the question, and keeps the words", async () => {
      const { transport, claude } = await start();
      // The session ends while the page hears nothing, so the page still offers the ask.
      transport.holdEvents();
      await claude!.drop();

      await ask('Still there?');

      const alert = await threadsPanel().findByRole('alert');
      expect(alert.textContent).toMatch(noSession);
      expect(askBox().value).toBe('Still there?');
   });
});

describe("a question's thread", () => {
   it('appears at once with the words, the anchor and a pending request, and an event for it adds no second thread', async () => {
      const { app } = await start();

      await ask('<b>Is</b> this safe?');

      const item = await (await listed()).findByRole('listitem');
      expect(item.textContent).toContain('<b>Is</b> this safe?');
      expect(item.querySelector('b')).toBeNull();
      expect(item.textContent).toContain('The tour');
      await opened();
      expect(threadViewElement()!.textContent).toContain('Claude may ask in the terminal');
      await settled();
      await backToList();
      expect(threadList().getAllByRole('listitem')).toHaveLength(1);
      expect(app.selectedThreads).toHaveLength(1);
   });

   it('names the stop it was asked about', async () => {
      const { app, stopIds } = await start();
      await openStop(app, stopIds[1]);

      await ask('Why?', 'Stop 2');

      expect((await (await listed()).findByRole('listitem')).textContent).toContain('Stop 2 · Rounding');
   });

   it("stays pending until its reply arrives, then shows the answer from the server's HTML", async () => {
      const { claude, tourId } = await start();
      await ask('Is this safe?');
      await (await opened()).findByText(/Claude may ask in the terminal/);

      const question = await claude!.next('question');
      await claude!.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Yes, **it is**.' });

      const item = threadViewElement()!;
      await waitFor(() => expect(item.querySelector('strong')?.textContent).toBe('it is'));
      expect(item.textContent).not.toContain('Claude may ask in the terminal');
   });

   it("shows the answer when the reply arrives before the ask's response", async () => {
      const { claude, tourId, transport } = await start();
      const held = transport.holdAnswered('ask_question');

      await ask('Is this safe?');
      await held.answered;
      const question = await claude!.next('question');
      await claude!.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Answered first.' });
      await (await opened()).findByText('Answered first.');
      await held.release();
      await settled();

      expect(shown().getByText('Answered first.')).toBeTruthy();
      expect(shown().queryByText(/Claude may ask in the terminal/)).toBeNull();
      await backToList();
      expect(threadList().getAllByRole('listitem')).toHaveLength(1);
   });

   it.each([
      ['needsAnswer', 'Which currency?', /Claude.+ · Needs an answer/],
      ['declined', 'Out of scope.', /Claude.+ · Declined/],
      ['declined', null, /Claude.+ · Declined/],
   ] as const)('shows a reply that resolves the request as %s', async (resolution, text, shown) => {
      const { claude, tourId } = await start();
      await ask('Fix the rounding?');
      await opened();

      const question = await claude!.next('question');
      await claude!.call('reply', { tourId, questionId: question.questionId, resolution, ...(text === null ? {} : { text }) });

      const item = threadViewElement()!;
      await waitFor(() => expect(item.textContent?.replace(/\s+/g, ' ')).toMatch(shown));
      if (text !== null) {
         expect(item.textContent).toContain(text);
      }

      expect(item.textContent).not.toContain('Claude may ask in the terminal');
   });

   it("follows a ref: link in the answer to its code reference, as a stop's link does", async () => {
      const { app, claude, tourId } = await start();
      await ask('Where?');
      await opened();
      const question = await claude!.next('question');
      await claude!.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'In [the totals](ref:head/src/totals.ts#L3-5).' });

      const link = await (await opened()).findByRole('link', { name: 'the totals' });
      await fireEvent.click(link);

      await waitFor(() => expect(app.selectedText).toMatchObject({ side: 'head', path: 'src/totals.ts' }));
      expect(location.hash).toContain('src%2Ftotals.ts');
   });

   it('shows threads stored with the tour, newest first', async () => {
      const { app, tourId } = await launch(async (world, { tourId }) => {
         await attachClaude(world, tourId);
         await world.reviewer.call('ask_question', { tourId, text: 'First question', anchor: { kind: 'tour' } });
         await world.reviewer.call('ask_question', { tourId, text: 'Second question', anchor: { kind: 'tour' } });
      });
      await select(app, prLabel, tourId);

      const items = await (await listed()).findAllByRole('listitem');
      expect(items.map((item) => item.querySelector('[data-title]')?.textContent?.trim())).toEqual(['Second question', 'First question']);
   });
});

describe('a question without an answer', () => {
   it('is offered once the session detaches, and disabled while no session is bound', async () => {
      const { claude } = await start();
      await ask('Still there?');
      await opened();

      await claude!.drop();

      const resend = await (await opened()).findByRole('button', { name: 'Send again' });
      await waitFor(() => expect((resend as HTMLButtonElement).disabled).toBe(true));
      await waitFor(() => expect(threadViewElement()!.textContent).toMatch(/Unanswered/));
      expect(description(resend)).toMatch(noSession);
   });

   it('sends the question again to the session that attaches, as a new pending request', async () => {
      const { claude, world, tourId } = await start();
      await ask('Still there?');
      await opened();
      await claude!.drop();
      await (await opened()).findByText(/Unanswered/);

      const second = await attachClaude(world, tourId, { confirm: false });
      const resend = shown().getByRole('button', { name: 'Send again' });
      await waitFor(() => expect((resend as HTMLButtonElement).disabled).toBe(false));
      await fireEvent.click(resend);

      await (await opened()).findByText(/Claude may ask in the terminal/);
      const requests = requestsOf(await firstThread(world, tourId));
      expect(requests.map((request) => `${request.sessionId} ${request.state}`)).toEqual([`${claude!.sessionId} unanswered`, `${second.sessionId} pending`]);
   });

   it.each(['unconfirmed', 'confirmed'] as const)('waits with cancel and send again while the session is %s, and cancel stops it waiting', async (level) => {
      const { world, tourId } = await start(level);
      await ask('Still there?');
      await (await opened()).findByText(/Waiting for Claude's reply/);

      await fireEvent.click(shown().getByRole('button', { name: 'Cancel' }));

      await (await opened()).findByText(/Canceled\. Claude may still answer it\./);
      expect(shown().queryByText(/Waiting for Claude's reply/)).toBeNull();
      expect(shown().queryByRole('button', { name: 'Cancel' })).toBeNull();
      expect(shown().getByRole('button', { name: 'Send again' })).toBeTruthy();
      expect(requestsOf(await firstThread(world, tourId)).map((request) => request.state)).toEqual(['canceled']);
   });

   it('sends a waiting question again as a new pending request, which cancels the earlier one', async () => {
      const { world, tourId } = await start('unconfirmed');
      await ask('Still there?');
      await (await opened()).findByText(/Waiting for Claude's reply/);
      const resend = shown().getByRole('button', { name: 'Send again' });
      await waitFor(() => expect((resend as HTMLButtonElement).disabled).toBe(false));

      await fireEvent.click(resend);

      await waitFor(async () => expect(requestsOf(await firstThread(world, tourId)).map((request) => request.state)).toEqual(['canceled', 'pending']));
      expect(shown().getByText(/Waiting for Claude's reply/)).toBeTruthy();
   });

   it('shows a late answer to a canceled question, which ends the cancel and send again', async () => {
      const { claude, tourId } = await start();
      await ask('Still there?');
      await (await opened()).findByText(/Waiting for Claude's reply/);
      await fireEvent.click(shown().getByRole('button', { name: 'Cancel' }));
      await (await opened()).findByText(/Canceled/);

      const question = await claude!.next('question');
      await claude!.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Still here.' });

      await (await opened()).findByText('Still here.');
      expect(shown().queryByText(/Canceled/)).toBeNull();
      expect(shown().queryByRole('button', { name: /Cancel|Send again/ })).toBeNull();
   });

   it("shows the server's reason when it refuses the cancel", async () => {
      const { claude, tourId, transport } = await start();
      await ask('Still there?');
      await (await opened()).findByText(/Waiting for Claude's reply/);
      // Claude answers while the page hears nothing, so the page still offers the cancel.
      transport.holdEvents();
      const question = await claude!.next('question');
      await claude!.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Here.' });

      await fireEvent.click(shown().getByRole('button', { name: 'Cancel' }));

      expect((await (await opened()).findByRole('alert')).textContent).toContain('already has its answer');
   });

   it("shows the server's reason when it refuses the re-send", async () => {
      const { claude, world, tourId, transport } = await start();
      await ask('Still there?');
      await opened();
      await claude!.drop();
      await (await opened()).findByText(/Unanswered/);
      const second = await attachClaude(world, tourId);
      const resend = shown().getByRole('button', { name: 'Send again' });
      await waitFor(() => expect((resend as HTMLButtonElement).disabled).toBe(false));
      // The new session ends while the page hears nothing, so the page still offers the re-send.
      transport.holdEvents();
      await second.drop();

      await fireEvent.click(resend);

      expect((await (await opened()).findByRole('alert')).textContent).toMatch(noSession);
   });
});

describe('thread events around a tour load', () => {
   /** Starts the app without opening a tour, after the reviewer asked about the first tour with a session attached that has not answered. */
   async function waitingQuestion(): Promise<Started & { claude: ClaudeSession }> {
      let claude!: ClaudeSession;
      const started = await launch(async (world, { tourId }) => {
         claude = await attachClaude(world, tourId);
         await world.reviewer.call('ask_question', { tourId, text: 'Asked earlier', anchor: { kind: 'tour' } });
      });
      return { ...started, claude };
   }

   async function reply(claude: ClaudeSession, tourId: string, text: string) {
      const question = await claude.next('question');
      await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text });
   }

   it('shows an answer that arrives during the load once the load ends', async () => {
      const { app, claude, tourId, transport } = await waitingQuestion();
      const load = transport.holdAnswered('read_state');
      await click(prLabel);
      await load.answered;

      await reply(claude, tourId, 'Answered during the load.');
      await load.release();
      await waitFor(() => expect(app.selectedPlan).toBeDefined());

      expect(await (await opened()).findByText('Answered during the load.')).toBeTruthy();
      await settled();
      expect(shown().queryByText(/Claude may ask in the terminal/)).toBeNull();
   });

   it('shows a thread created during the load once the load ends, once', async () => {
      const { app, world, tourId, transport } = await launch(async (world, { tourId }) => void (await attachClaude(world, tourId)));
      const load = transport.holdAnswered('read_state');
      await click(prLabel);
      await load.answered;

      // Another page of the same reviewer asks while this one loads.
      await world.reviewer.call('ask_question', { tourId, text: 'From another page', anchor: { kind: 'tour' } });
      await load.release();
      await waitFor(() => expect(app.selectedPlan).toBeDefined());

      expect(await (await listed()).findByText('From another page')).toBeTruthy();
      await settled();
      expect(threadList().getAllByRole('listitem')).toHaveLength(1);
   });

   it('keeps an answer that arrives during the reload after the stream reconnects', async () => {
      const { app, claude, tourId, transport } = await waitingQuestion();
      await select(app, prLabel, tourId);
      await (await opened()).findByText(/Claude may ask in the terminal/);

      const reload = transport.holdAnswered('read_state');
      transport.cutEventStreams();
      await reload.answered;
      await reply(claude, tourId, 'Answered during the reload.');
      await (await opened()).findByText('Answered during the reload.');
      await reload.release();
      await settled();

      expect(shown().getByText('Answered during the reload.')).toBeTruthy();
      expect(shown().queryByText(/Claude may ask in the terminal/)).toBeNull();
   });

   it('shows an answer that arrived while the stream was down, from the reload', async () => {
      const { app, claude, tourId, transport } = await waitingQuestion();
      await select(app, prLabel, tourId);
      await (await opened()).findByText(/Claude may ask in the terminal/);

      const resume = transport.holdEvents();
      await reply(claude, tourId, 'Missed while away.');
      resume();

      expect(await (await opened()).findByText('Missed while away.')).toBeTruthy();
   });

   it("keeps each tour's threads to that tour when a load for the tour left behind ends after a switch", async () => {
      const { app, claude, tourId, otherId, transport } = await waitingQuestion();
      const load = transport.holdAnswered('read_state');
      await click(prLabel);
      await load.answered;
      await select(app, otherLabel, otherId);

      await reply(claude, tourId, 'For the first tour.');
      await load.release();
      await settled();

      expect(threadsPanel().queryByRole('listitem')).toBeNull();
      expect(threadsPanel().getByText('No questions yet.')).toBeTruthy();
      await select(app, prLabel, tourId);
      expect(await (await opened()).findByText('For the first tour.')).toBeTruthy();
   });

   it("keeps an ask's thread to the tour it was asked in when the reviewer switches tours before the response", async () => {
      const { app, tourId, otherId, transport } = await start();
      const held = transport.holdAnswered('ask_question');
      await ask('Asked in the first tour');
      await held.answered;

      await select(app, otherLabel, otherId);
      await held.release();
      await settled();

      expect(threadsPanel().queryByRole('listitem')).toBeNull();
      await select(app, prLabel, tourId);
      expect(await (await listed()).findByText('Asked in the first tour')).toBeTruthy();
   });
});
