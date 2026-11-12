import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { contentOf, versionOf } from '../../src/lib/stops';
import { actionOf, messagesOf, waitingQuestionOf } from '../../src/lib/threadView';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { QuestionThread, RequestTourActionInput, Tour } from '../../src/lib/types';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openStop, openTour, prUrl, readTour, seedTour, settled, startApp } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { reasonOf } from './planMenu';
import { chooseStopAction, stopMenuButton } from './stopToolbar';
import { toggleShown } from './threadFilter';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

afterEach(() => document.getSelection()?.removeAllRanges());

const how = 'Each add checks the quantity.';

interface Seed {
   tourId: string;
   /** The overview with content, an inspect stop with content, and a pending stop. */
   stopIds: [string, string, string];
}

const cart = backend().template('tourActions-cart', async (world): Promise<Seed> => {
   const seeded = await seedTour(world, {
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'Cart adds', kind: 'overview', content: { headline: 'The cart adds a line.', how } },
               { title: 'Totals', kind: 'inspect', content: cartContent('The total rounds.') },
               { title: 'Later', kind: 'inspect' },
            ],
         },
      ],
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds as Seed['stopIds'] };
});

/** A world copied from the cart template, with a bound session unless `bound` is false. */
async function cartWorld(bound = true): Promise<{ world: World; tourId: string; stopIds: Seed['stopIds']; claude: ClaudeSession | null }> {
   const world = await backend().world({ template: cart });
   const { tourId, stopIds } = world.seed as Seed;
   return { world, tourId, stopIds, claude: bound ? await attachClaude(world, tourId) : null };
}

/** Starts the app on the world and opens the tour's stop. */
async function open(world: World, stopId: string, transport?: Transport): Promise<{ app: LotsenApp; transport: Transport }> {
   const started = await startApp(world, transport === undefined ? {} : { transport });
   await openTour(started.app);
   await openStop(started.app, stopId);
   return started;
}

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));

async function sendFromPanel(action: 'Explain deeper' | 'Revise', words: string) {
   await chooseStopAction(action);
   if (words !== '') {
      const prompt = action === 'Revise' ? 'What should change?' : 'What should Claude explain? Optional';
      await fireEvent.input(stopPanel().getByRole('textbox', { name: prompt }), { target: { value: words } });
   }

   await fireEvent.click(stopPanel().getByRole('button', { name: 'Send to Claude' }));
}

function selectWords(element: Element, words: string): void {
   const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
   for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const index = node.textContent!.indexOf(words);
      if (index >= 0) {
         const range = document.createRange();
         range.setStart(node, index);
         range.setEnd(node, index + words.length);
         document.getSelection()!.removeAllRanges();
         document.getSelection()!.addRange(range);
         document.dispatchEvent(new Event('selectionchange'));
         return;
      }
   }

   throw new Error(`No text "${words}".`);
}

/** Returns the tour's threads that hold a tour action, in the server's order. */
async function actionThreads(world: World, tourId: string): Promise<QuestionThread[]> {
   return (await readTour(world, tourId)).threads.filter((thread) => actionOf(thread) !== null);
}

/** Requests the tour action as the reviewer, and returns the ID of its question. */
async function request(world: World, tourId: string, input: Omit<RequestTourActionInput, 'tourId'>): Promise<string> {
   const thread = await world.reviewer.call('request_tour_action', { tourId, ...input });
   return messagesOf(thread)[0]!.id;
}

const revise = (stopId: string): Omit<RequestTourActionInput, 'tourId'> => ({ action: { kind: 'revise', target: { kind: 'stop', stopId } }, text: 'Shorter.', stopId });

/** Answers the session's next question as done. */
async function replyDone(claude: ClaudeSession, tourId: string, text = 'Revised.'): Promise<void> {
   const question = await claude.next('question');
   await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text });
}

async function reviseOverview(claude: ClaudeSession, tourId: string, stopId: string, words = how): Promise<void> {
   await claude.call('put_stop', { tourId, stopId, headline: 'The cart adds a line, revised.', how: words });
}

/** Wraps the transport so that, while closed, the page's reconnects of its event stream wait, and the writes in between reach the page only through the load after the reconnect. */
function gatedStreams(transport: Transport): { transport: Transport; close(): void; open(): void } {
   let gate: Promise<void> = Promise.resolve();
   let release = () => {};
   const fetch: typeof globalThis.fetch = async (input, init) => {
      if (input === '/api/events') {
         await gate;
      }

      return transport.fetch(input, init);
   };
   return {
      transport: { ...transport, fetch },
      close() {
         gate = new Promise((resolve) => (release = resolve));
         transport.cutEventStreams();
      },
      open: () => release(),
   };
}

const readStates = (transport: Transport, tourId: string) => transport.requests.filter((sent) => sent.operation === 'read_state' && sent.input?.tourId === tourId).length;
const tourLists = (transport: Transport) => transport.requests.filter((sent) => sent.operation === 'list_tours').length;

describe("the Stop panel's tour actions", () => {
   it("sends Explain deeper about the open stop with the reviewer's words", async () => {
      const { world, tourId, stopIds } = await cartWorld();
      await open(world, stopIds[0]);

      await sendFromPanel('Explain deeper', 'The rounding, please.');

      await waitFor(async () => expect(await actionThreads(world, tourId)).toHaveLength(1));
      const [thread] = await actionThreads(world, tourId);
      expect([actionOf(thread!), thread!.anchor]).toEqual([
         { kind: 'explain', stopVersion: 1 },
         { kind: 'stop', stopId: stopIds[0] },
      ]);
      expect(messagesOf(thread!)[0]).toMatchObject({ text: 'The rounding, please.', stopId: stopIds[0] });
   });

   it('sends Explain deeper without words when the reviewer gives none', async () => {
      const { world, tourId, stopIds } = await cartWorld();
      const { transport } = await open(world, stopIds[0]);

      await sendFromPanel('Explain deeper', '');

      await waitFor(() =>
         expect(transport.requests.filter((sent) => sent.operation === 'request_tour_action').map((sent) => sent.input)).toEqual([
            { tourId, action: { kind: 'explain', target: { kind: 'stop', stopId: stopIds[0] } }, stopId: stopIds[0] },
         ]),
      );
      await waitFor(async () => expect(await actionThreads(world, tourId)).toHaveLength(1));
   });

   it('names no viewed stop in a tour action while the open stop is one that the plan lacks', async () => {
      const { world, tourId } = await cartWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      app.openStop('gone');
      expect(app.selectedStop).toBeNull();

      expect(await app.requestAction({ kind: 'replan' }, 'tour', 'Fewer stops.')).toBeNull();

      expect(transport.requests.filter((sent) => sent.operation === 'request_tour_action').map((sent) => sent.input)).toEqual([{ tourId, action: { kind: 'replan' }, text: 'Fewer stops.' }]);
      expect(await app.requestAction('explain', 'stop', '')).toBe('Open a stop first.');
   });

   it("shows the pending action on the stop until Claude's reply resolves it", async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await open(world, stopIds[0]);

      await sendFromPanel('Revise', 'Shorter.');

      expect(await stopPanel().findByText('Claude is revising this stop.')).toBeTruthy();
      await replyDone(claude!, tourId, 'Shortened the headline.');
      await waitFor(() => expect(stopPanel().queryByText('Claude is revising this stop.')).toBeNull());
   });

   it("disables both actions with the server's reason while no session is attached", async () => {
      const { world, stopIds } = await cartWorld(false);
      await open(world, stopIds[0]);

      await waitFor(() => expect((stopMenuButton('Ask Claude') as HTMLButtonElement).disabled).toBe(true));
      expect(reasonOf(stopMenuButton('Ask Claude')!)).toMatch(/is attached to no Claude Code session/);
   });

   it("shows the server's refusal of an action, and keeps the reviewer's words", async () => {
      const { world, stopIds } = await cartWorld();
      await open(world, stopIds[0]);
      // The words break a limit that the tour's controls do not check, so the button stays enabled and the server refuses.
      const words = 'x'.repeat(10_001);

      await sendFromPanel('Explain deeper', words);

      expect((await stopPanel().findByRole('alert')).textContent).toMatch(/holds at most 10000 characters/);
      expect((stopPanel().getByRole('textbox') as HTMLTextAreaElement).value).toBe(words);
   });

   it('shows a refusal only until the reviewer picks another action, goes back to the refused one, or cancels', async () => {
      const { world, stopIds } = await cartWorld();
      await open(world, stopIds[0]);
      const refuse = async () => {
         await sendFromPanel('Explain deeper', 'x'.repeat(10_001));
         await stopPanel().findByRole('alert');
      };

      await refuse();
      await chooseStopAction('Revise');
      expect(stopPanel().queryByRole('alert')).toBeNull();
      await chooseStopAction('Explain deeper');
      expect(stopPanel().queryByRole('alert')).toBeNull();

      await fireEvent.click(stopPanel().getByRole('button', { name: 'Send to Claude' }));
      await stopPanel().findByRole('alert');
      await chooseStopAction('Explain deeper');
      expect(stopPanel().queryByRole('alert')).toBeNull();

      await fireEvent.click(stopPanel().getByRole('button', { name: 'Send to Claude' }));
      await stopPanel().findByRole('alert');
      await fireEvent.click(stopPanel().getByRole('button', { name: 'Cancel' }));
      await chooseStopAction('Explain deeper');
      expect(stopPanel().queryByRole('alert')).toBeNull();
   });

   it('holds Ask Claude back while an action is on its way to the server, so the answer reaches the form that sent it', async () => {
      const { world, stopIds } = await cartWorld();
      const { transport } = await open(world, stopIds[0]);
      const release = transport.hold('request_tour_action', 'request');

      await sendFromPanel('Explain deeper', 'x'.repeat(10_001));

      await waitFor(() => expect((stopMenuButton('Ask Claude') as HTMLButtonElement).disabled).toBe(true));
      release();
      expect((await stopPanel().findByRole('alert')).textContent).toMatch(/holds at most 10000 characters/);
      expect((stopMenuButton('Ask Claude') as HTMLButtonElement).disabled).toBe(false);
   });

   it('offers no action on a stop without content', async () => {
      const { world, stopIds } = await cartWorld();
      await open(world, stopIds[2]);

      expect(stopMenuButton('Ask Claude')).toBeNull();
   });

   it('offers no action on an earlier version of the stop', async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await reviseOverview(claude!, tourId, stopIds[0]);
      const { app } = await open(world, stopIds[0]);
      expect(stopMenuButton('Ask Claude')).toBeTruthy();

      app.openVersion(1);

      await waitFor(() => expect(stopMenuButton('Ask Claude')).toBeNull());
   });
});

describe("the words box of the Stop panel's actions", () => {
   const reviseBox = () => stopPanel().getByRole('textbox', { name: 'What should change?' }) as HTMLTextAreaElement;
   const sendButton = () => stopPanel().getByRole('button', { name: 'Send to Claude' }) as HTMLButtonElement;

   it('tags the Explain deeper box as optional, says what an empty box does, gives it the focus, and offers Send without words', async () => {
      const { world, stopIds } = await cartWorld();
      await open(world, stopIds[0]);

      await chooseStopAction('Explain deeper');

      const box = stopPanel().getByRole('textbox', { name: 'What should Claude explain? Optional' }) as HTMLTextAreaElement;
      expect(box.placeholder).toBe('Leave it empty and Claude explains the whole stop deeper.');
      await waitFor(() => expect(document.activeElement).toBe(box));
      expect(sendButton().disabled).toBe(false);
   });

   it('gives the Revise box, whose words are required, no tag and no placeholder, and the focus', async () => {
      const { world, stopIds } = await cartWorld();
      await open(world, stopIds[0]);

      await chooseStopAction('Revise');

      expect(reviseBox().hasAttribute('placeholder')).toBe(false);
      await waitFor(() => expect(document.activeElement).toBe(reviseBox()));
   });

   it('offers Send for Revise only once the reviewer wrote words', async () => {
      const { world, stopIds } = await cartWorld();
      await open(world, stopIds[0]);

      await chooseStopAction('Revise');
      expect(sendButton().disabled).toBe(true);

      await fireEvent.input(reviseBox(), { target: { value: '   ' } });
      expect(sendButton().disabled).toBe(true);

      await fireEvent.input(reviseBox(), { target: { value: 'Shorter.' } });
      expect(sendButton().disabled).toBe(false);
   });

   it('sends no Revise without words on Ctrl+Enter, and sends one with words', async () => {
      const { world, tourId, stopIds } = await cartWorld();
      const { transport } = await open(world, stopIds[0]);
      await chooseStopAction('Revise');

      await fireEvent.keyDown(reviseBox(), { key: 'Enter', ctrlKey: true });
      await fireEvent.input(reviseBox(), { target: { value: 'Shorter.' } });
      await fireEvent.keyDown(reviseBox(), { key: 'Enter', ctrlKey: true });

      await waitFor(() =>
         expect(transport.requests.filter((sent) => sent.operation === 'request_tour_action').map((sent) => sent.input)).toEqual([
            { tourId, action: { kind: 'revise', target: { kind: 'stop', stopId: stopIds[0] } }, text: 'Shorter.', stopId: stopIds[0] },
         ]),
      );
   });
});

describe("Explain on a selection in a stop's text", () => {
   const explainSelection = (words: string) => screen.findByRole('button', { name: `Explain “${words}”` });

   async function selectAndExplain(words: string): Promise<void> {
      selectWords(stopPanel().getByText(how), words);
      const button = await explainSelection(words);
      await waitFor(() => expect(button.hasAttribute('disabled')).toBe(false));
      await fireEvent.click(button);
   }

   it("sends an explain action about the selected text in the stop's version, without words", async () => {
      const { world, tourId, stopIds } = await cartWorld();
      await open(world, stopIds[0]);

      await selectAndExplain('checks the quantity');

      await waitFor(async () => expect(await actionThreads(world, tourId)).toHaveLength(1));
      const [thread] = await actionThreads(world, tourId);
      expect([actionOf(thread!), thread!.anchor]).toEqual([{ kind: 'explain' }, { kind: 'stopText', stopId: stopIds[0], field: 'how', text: 'checks the quantity', version: 1 }]);
      expect(messagesOf(thread!)[0]).toMatchObject({ text: '' });
   });

   it("closes the selection's Explain once the action is stored, so a second click cannot store it twice", async () => {
      const { world, tourId, stopIds } = await cartWorld();
      const { transport } = await open(world, stopIds[0]);
      const release = transport.hold('request_tour_action');
      const answered = transport.answered('request_tour_action');

      await selectAndExplain('checks the quantity');
      await waitFor(async () => expect(await actionThreads(world, tourId)).toHaveLength(1));
      release();
      await answered;

      await waitFor(() => expect(screen.queryByRole('button', { name: 'Explain “checks the quantity”' })).toBeNull());
      expect(transport.requests.filter((sent) => sent.operation === 'request_tour_action')).toHaveLength(1);
   });

   it('keeps the Explain of a selection that the reviewer made while the action was in flight', async () => {
      const { world, tourId, stopIds } = await cartWorld();
      const { transport } = await open(world, stopIds[0]);
      const release = transport.hold('request_tour_action');
      const answered = transport.answered('request_tour_action');

      await selectAndExplain('checks the quantity');
      await waitFor(async () => expect(await actionThreads(world, tourId)).toHaveLength(1));
      selectWords(stopPanel().getByText(how), 'Each add');
      await explainSelection('Each add');
      release();
      await answered;

      expect(screen.getByRole('button', { name: 'Explain “Each add”' })).toBeTruthy();
   });

   it("keeps the selection's Explain with the server's refusal, so the reviewer can try again", async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      const { transport } = await open(world, stopIds[0]);
      // The session ends while the request is on its way, so the server refuses it, and a session that attaches then lets the reviewer try again.
      const release = transport.hold('request_tour_action', 'request');
      const answered = transport.answered('request_tour_action');

      await selectAndExplain('checks the quantity');
      await waitFor(() => expect(transport.requests.filter((sent) => sent.operation === 'request_tour_action')).toHaveLength(1));
      await claude!.drop();
      release();
      await answered;
      await attachClaude(world, tourId);

      await waitFor(() => expect(screen.getByRole('button', { name: 'Explain “checks the quantity”' })).toHaveProperty('disabled', false));
      expect(screen.getByRole('alert').textContent).toMatch(/is attached to no Claude Code session/);
      expect(await actionThreads(world, tourId)).toEqual([]);
   });

   it("is disabled with the server's reason while no session is attached", async () => {
      const { world, stopIds } = await cartWorld(false);
      await open(world, stopIds[0]);

      selectWords(stopPanel().getByText(how), 'checks the quantity');
      const explain = (await explainSelection('checks the quantity')) as HTMLButtonElement;

      await waitFor(() => expect(explain.disabled).toBe(true));
      expect(screen.getAllByText(/is attached to no Claude Code session/).length).toBeGreaterThan(0);
   });
});

describe('the threads of tour actions', () => {
   it('can be filtered out of the Threads panel', async () => {
      const { world, tourId, stopIds } = await cartWorld();
      await request(world, tourId, { action: { kind: 'explain', target: { kind: 'stop', stopId: stopIds[0] } }, stopId: stopIds[0] });
      await world.reviewer.call('ask_question', { tourId, text: 'Why?', anchor: { kind: 'stop', stopId: stopIds[0] } });
      await open(world, stopIds[0]);
      const threadsPanel = within(screen.getByRole('region', { name: 'Threads' }));
      await threadsPanel.findByText('Why?');
      expect(threadsPanel.getAllByText('Explain deeper').length).toBeGreaterThan(0);

      await toggleShown('Tour actions');

      await waitFor(() => expect(threadsPanel.queryByText('Explain deeper')).toBeNull());
      expect(threadsPanel.getByText('Why?')).toBeTruthy();
   });
});

describe("a revision that answers the reviewer's own action", () => {
   const heldRead = { number: 1, current: 2, reason: { kind: 'revised' } };

   it('shows the new version of the stop under view without holding the one the reviewer read', async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await request(world, tourId, revise(stopIds[0]));
      const { app } = await open(world, stopIds[0]);

      await reviseOverview(claude!, tourId, stopIds[0]);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
      expect(contentOf(app.selectedStop)?.headline).toBe('The cart adds a line, revised.');
   });

   it('still holds the version the reviewer read when no action of the stop waits', async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await request(world, tourId, revise(stopIds[0]));
      await replyDone(claude!, tourId);
      await world.reviewer.call('ask_question', { tourId, text: 'Why?', anchor: { kind: 'stop', stopId: stopIds[0] } });
      const { app } = await open(world, stopIds[0]);

      await reviseOverview(claude!, tourId, stopIds[0]);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldRead);
   });

   it.each<[string, (stopId: string) => Omit<RequestTourActionInput, 'tourId'>]>([
      ['the whole stop', revise],
      ['text in the stop', (stopId) => ({ action: { kind: 'explain', target: { kind: 'stopText', stopId, field: 'how', text: 'checks the quantity', version: 1 } }, stopId })],
   ])('skips the hold when the load after a reconnect brings the revision and the reply together, for an action on %s', async (_, input) => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await request(world, tourId, input(stopIds[0]));
      const gate = gatedStreams(world.transport());
      const { app } = await open(world, stopIds[0], gate.transport);

      gate.close();
      await reviseOverview(claude!, tourId, stopIds[0], 'Each add checks the quantity and the stock.');
      await replyDone(claude!, tourId);
      gate.open();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it("skips the hold when the page's own write brings the controls after Claude's reply before the load after a reconnect brings the revision", async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await request(world, tourId, revise(stopIds[0]));
      const gate = gatedStreams(world.transport());
      // Opening the stop sends a progress write, which reaches the server only after Claude's reply.
      const releaseProgress = gate.transport.hold('record_reading_progress', 'request');
      const { app, transport } = await open(world, stopIds[0], gate.transport);

      gate.close();
      await reviseOverview(claude!, tourId, stopIds[0]);
      await replyDone(claude!, tourId);
      const answered = transport.answered('record_reading_progress');
      releaseProgress();
      await answered;
      await waitFor(() => expect(app.selectedControls?.offers.pending.stops[stopIds[0]]).toBeUndefined());
      gate.open();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it("skips the hold when the page's own write brings the controls after Claude's reply before the revision's event", async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await request(world, tourId, revise(stopIds[0]));
      const held = world.transport();
      // Opening the stop sends a progress write, which reaches the server only after Claude's reply.
      const releaseProgress = held.hold('record_reading_progress', 'request');
      const { app, transport } = await open(world, stopIds[0], held);

      const unstall = transport.stallEventStreams();
      await reviseOverview(claude!, tourId, stopIds[0]);
      await replyDone(claude!, tourId);
      const answered = transport.answered('record_reading_progress');
      releaseProgress();
      await answered;
      await waitFor(() => expect(app.selectedControls?.offers.pending.stops[stopIds[0]]).toBeUndefined());
      unstall();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it("skips the hold when older controls from the stream arrive after the page's own write brought newer ones, before the revision's event", async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      const held = world.transport();
      // Opening the stop sends a progress write, which reaches the server only after Claude's reply.
      const releaseProgress = held.hold('record_reading_progress', 'request');
      const { app, transport } = await open(world, stopIds[0], held);

      const unstall = transport.stallEventStreams();
      await request(world, tourId, revise(stopIds[0]));
      await reviseOverview(claude!, tourId, stopIds[0]);
      await replyDone(claude!, tourId);
      const replied = (await readTour(world, tourId)).revision;
      const answered = transport.answered('record_reading_progress');
      releaseProgress();
      await answered;
      await waitFor(() => expect(app.selectedControls!.revision).toBeGreaterThan(replied));
      unstall();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it('skips the hold when the reply arrives on the new stream while the load is in flight', async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await request(world, tourId, revise(stopIds[0]));
      const gate = gatedStreams(world.transport());
      const { app, transport } = await open(world, stopIds[0], gate.transport);
      const before = readStates(transport, tourId);
      const release = transport.hold('read_state');

      gate.close();
      await reviseOverview(claude!, tourId, stopIds[0]);
      gate.open();
      await waitFor(() => expect(readStates(transport, tourId)).toBe(before + 1));
      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      await replyDone(claude!, tourId);
      await waitFor(() => expect(messagesOf(app.selectedThreads.find((thread) => actionOf(thread) !== null)!)).toHaveLength(2));
      release();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it('skips the hold when the reply arrives on the new stream while the load waits for the tour list', async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await request(world, tourId, revise(stopIds[0]));
      const gate = gatedStreams(world.transport());
      const { app, transport } = await open(world, stopIds[0], gate.transport);
      const before = tourLists(transport);
      const release = transport.hold('list_tours');

      gate.close();
      await reviseOverview(claude!, tourId, stopIds[0]);
      gate.open();
      await waitFor(() => expect(tourLists(transport)).toBe(before + 1));
      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      await replyDone(claude!, tourId);
      await waitFor(() => expect(messagesOf(app.selectedThreads.find((thread) => actionOf(thread) !== null)!)).toHaveLength(2));
      release();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it('shows the stop as it was when the load after a reconnect brings no new version while an action waits', async () => {
      const { world, tourId, stopIds } = await cartWorld();
      await request(world, tourId, revise(stopIds[0]));
      const { app, transport } = await open(world, stopIds[0]);
      const before = readStates(transport, tourId);

      transport.cutEventStreams();

      await waitFor(() => expect(readStates(transport, tourId)).toBe(before + 1));
      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      expect(app.shownVersion).toBeNull();
      expect(versionOf(app.currentStop)).toBe(1);
   });

   it('skips the hold while one of two actions on the stop waits', async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await request(world, tourId, { action: { kind: 'explain', target: { kind: 'stop', stopId: stopIds[0] } }, stopId: stopIds[0] });
      await replyDone(claude!, tourId, 'Explained.');
      await request(world, tourId, revise(stopIds[0]));
      const gate = gatedStreams(world.transport());
      const { app } = await open(world, stopIds[0], gate.transport);

      gate.close();
      await reviseOverview(claude!, tourId, stopIds[0]);
      await replyDone(claude!, tourId);
      gate.open();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
   });

   it.each<[string, (world: World, tourId: string, stopIds: Seed['stopIds'], claude: ClaudeSession) => Promise<void>]>([
      [
         'an action that Claude answered before the page saw the revision',
         async (world, tourId, stopIds, claude) => {
            await request(world, tourId, revise(stopIds[0]));
            await replyDone(claude, tourId);
         },
      ],
      [
         'an action that the reviewer canceled',
         async (world, tourId, stopIds) => {
            const questionId = await request(world, tourId, revise(stopIds[0]));
            await world.reviewer.call('cancel_question', { tourId, questionId });
         },
      ],
      ['an action on another stop that waits', async (world, tourId, stopIds) => void (await request(world, tourId, revise(stopIds[1])))],
   ])('holds the version the reviewer read when the load after a reconnect brings a revision, beside %s', async (_, arrange) => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      await arrange(world, tourId, stopIds, claude!);
      const gate = gatedStreams(world.transport());
      const { app } = await open(world, stopIds[0], gate.transport);

      gate.close();
      await reviseOverview(claude!, tourId, stopIds[0]);
      gate.open();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldRead);
   });

   it.each<[string, (world: World, tourId: string, claude: ClaudeSession, questionId: string) => Promise<void>]>([
      ['the reviewer canceled', async (world, tourId, _, questionId) => void (await world.reviewer.call('cancel_question', { tourId, questionId }))],
      ['Claude answered without a revision', async (_, tourId, claude) => replyDone(claude, tourId)],
   ])('holds the version the reviewer read when only the load after a reconnect brings a revision, beside an action that the page heard end live because %s', async (_, end) => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      const questionId = await request(world, tourId, revise(stopIds[0]));
      const gate = gatedStreams(world.transport());
      // Opening the stop sends a progress write. It reaches the server after the action ends, while the stalled stream holds the end's events, so its response brings the end first.
      const releaseProgress = gate.transport.hold('record_reading_progress', 'request');
      const { app, transport } = await open(world, stopIds[0], gate.transport);
      const actionThread = () => app.selectedThreads.find((thread) => actionOf(thread) !== null)!;
      const unstall = transport.stallEventStreams();
      await end(world, tourId, claude!, questionId);
      const answered = transport.answered('record_reading_progress');
      releaseProgress();
      await answered;
      await waitFor(() => expect(app.selectedControls?.offers.pending.stops[stopIds[0]]).toBeUndefined());
      expect(waitingQuestionOf(actionThread())).not.toBeNull();
      unstall();
      // The hold at the revision counts the waiting work that the end's events bring, so the page reads every event the stream held before the stream is cut.
      await settled(app);
      expect(waitingQuestionOf(actionThread())).toBeNull();

      gate.close();
      await reviseOverview(claude!, tourId, stopIds[0]);
      gate.open();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldRead);
   });

   it("holds the version the reviewer read when the page heard the action end live before the revision's event", async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      const questionId = await request(world, tourId, revise(stopIds[0]));
      const { app } = await open(world, stopIds[0]);
      await world.reviewer.call('cancel_question', { tourId, questionId });
      await waitFor(() => expect(app.selectedControls?.offers.pending.stops[stopIds[0]]).toBeUndefined());

      await reviseOverview(claude!, tourId, stopIds[0]);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldRead);
   });

   it('holds the version the reviewer read when a revision comes beside a canceled action', async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      const questionId = await request(world, tourId, revise(stopIds[0]));
      await world.reviewer.call('cancel_question', { tourId, questionId });
      const { app } = await open(world, stopIds[0]);

      await reviseOverview(claude!, tourId, stopIds[0]);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldRead);
   });

   it('holds the version the reviewer read when the action that waits is in another tour', async () => {
      const { world, tourId, stopIds, claude } = await cartWorld();
      const otherUrl = prUrl.replace(/42$/, '43');
      const other = (await claude!.call('create_tour', { prUrl: otherUrl, familiarity: 'some' })) as Tour;
      await world.idle();
      const otherPlan = (await claude!.call('set_plan', { tourId: other.id, chapters: [{ id: 'c1', title: 'Cart' }], stops: [{ title: 'Cart adds', kind: 'overview', chapter: 'c1' }] })) as Tour;
      const otherStop = otherPlan.plan!.stops[0]!.id;
      await claude!.call('put_stop', { tourId: other.id, stopId: otherStop, headline: 'The cart adds a line.', how });
      await request(world, other.id, revise(otherStop));
      const { app } = await startApp(world);
      await openTour(app, 'shop #43');
      await waitFor(() => expect(app.selectedThreads).toHaveLength(1));
      (await openTourList())
         .getAllByRole('button', tourButton)
         .find((button) => button.textContent?.includes('shop #42'))!
         .click();
      await waitFor(() => expect(app.selectedTourId).toBe(tourId));
      await openStop(app, stopIds[0]);

      await reviseOverview(claude!, tourId, stopIds[0]);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldRead);
   });
});
