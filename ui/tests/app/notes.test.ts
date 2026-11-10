import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { QuestionThread, UpdatedThread } from '../../src/lib/types';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openTour, readTour, seedTour, startApp, withHolds, type HeldTransport } from '../harness';
import { backToList, chooseFromThreadMenu, openThreadView, threadRow } from './threadView';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

// A negative check needs the page to have handled what the server sent, which no condition on the page shows.
const settled = () => new Promise((resolve) => setTimeout(resolve, 50));

// The tour has a plan and no session, so each test attaches one when it needs it.
const planned = backend().template('notes-planned', async (world) => {
   const { tourId } = await seedTour(world, {
      bound: false,
      chapters: [
         {
            title: 'Checkout',
            stops: [
               { title: 'Cart totals', kind: 'inspect', content: cartContent('The total rounds.') },
               { title: 'Rounding', kind: 'risk' },
            ],
         },
      ],
   });
   return { tourId };
});

const noSession = /attached to no Claude Code session/;
const panel = () => within(screen.getByRole('region', { name: 'Threads' }));
const askBox = () => panel().getByRole('textbox', { name: 'Question for Claude' }) as HTMLTextAreaElement;
const sendButton = () => panel().getByRole('button', { name: 'Send' }) as HTMLButtonElement;
const saveButton = () => panel().getByRole('button', { name: 'Save as note' }) as HTMLButtonElement;
const batchButton = () => panel().getByRole('button', { name: 'Send chosen notes' }) as HTMLButtonElement;
/** Chooses a note for the next note batch in its thread view, and goes back to the thread list. */
async function chooseNote(threadId: string): Promise<void> {
   await fireEvent.click((await openThreadView(threadId)).getByRole('checkbox', { name: 'Send with other notes' }));
   await backToList();
}

/** Returns the words of a thread that is a note, or undefined for a sent thread. */
function noteText(thread: QuestionThread | undefined): string | undefined {
   return thread?.kind === 'note' ? thread.text : undefined;
}

/** The text of the elements that describe an element, as a screen reader reads its description. */
function description(element: Element): string {
   return (element.getAttribute('aria-describedby') ?? '')
      .split(/\s+/)
      .filter(Boolean)
      .map((id) => document.getElementById(id)?.textContent?.trim() ?? '')
      .join(' ');
}

interface Started {
   world: World;
   tourId: string;
   app: LotsenApp;
   transport: HeldTransport;
   claude: ClaudeSession | null;
   /** The IDs of the notes that the test wrote before the app started, in their order. */
   notes: string[];
}

/** Starts the app with the tour open, after the reviewer saved a note about the tour with each of the words, and with a bound session when `bound` is true. */
async function start(bound: boolean, notes: string[] = []): Promise<Started> {
   const world = await backend().world({ template: planned });
   const { tourId } = world.seed as { tourId: string };
   const claude = bound ? await attachClaude(world, tourId) : null;
   const ids: string[] = [];
   for (const text of notes) {
      ids.push(((await world.reviewer.call('save_note', { tourId, text, anchor: { kind: 'tour' } })) as UpdatedThread).thread.id);
   }

   const transport = withHolds(world.transport());
   const { app } = await startApp(world, { transport });
   await openTour(app);
   await waitFor(() => expect(app.selectedThreads).toHaveLength(notes.length));
   return { world, tourId, app, transport, claude, notes: ids };
}

const threadsOf = async (world: World, tourId: string) => (await readTour(world, tourId)).threads;

async function saveNote(app: LotsenApp, text: string): Promise<string> {
   const before = app.selectedThreads.length;
   await fireEvent.input(askBox(), { target: { value: text } });
   await fireEvent.click(saveButton());
   await waitFor(() => expect(app.selectedThreads).toHaveLength(before + 1));
   return app.selectedThreads.at(-1)!.id;
}

describe('notes in the Threads panel', () => {
   it("offers only saving a note in the ask box with no session attached, with the server's reason", async () => {
      await start(false);
      await fireEvent.input(askBox(), { target: { value: 'Later.' } });

      expect(askBox().disabled).toBe(false);
      expect(sendButton().disabled).toBe(true);
      expect(saveButton().disabled).toBe(false);
      expect(description(sendButton())).toMatch(noSession);
      expect(panel().getByText(noSession)).toBeTruthy();
   });

   it('saves a note from the one-line field and from the expanded editor', async () => {
      const { world, tourId, app } = await start(true);

      await saveNote(app, 'From the line.');
      // The box clears once the save's response arrives, which ends the write.
      await waitFor(() => expect(askBox().value).toBe(''));
      await fireEvent.click(panel().getByRole('button', { name: 'Write a longer question' }));
      await waitFor(() => expect(panel().getByRole('button', { name: 'Make the box smaller' })).toBeTruthy());
      await saveNote(app, 'From the editor.');

      await waitFor(async () => expect((await threadsOf(world, tourId)).map(noteText)).toEqual(['From the line.', 'From the editor.']));
      expect(askBox().value).toBe('');
   });

   it('saves a note with Ctrl+Enter while no session is attached', async () => {
      const { world, tourId, transport } = await start(false);
      await fireEvent.input(askBox(), { target: { value: 'Look at this later.' } });

      await fireEvent.keyDown(askBox(), { key: 'Enter', ctrlKey: true });

      await waitFor(async () => expect(await threadsOf(world, tourId)).toHaveLength(1));
      expect(noteText((await threadsOf(world, tourId))[0])).toBe('Look at this later.');
      expect(transport.requests.some((request) => request.operation === 'ask_question' || request.operation === 'ask_follow_up')).toBe(false);
   });

   it('writes a note with no session attached, shows it as a draft, and empties the box', async () => {
      const { world, tourId, app } = await start(false);

      const id = await saveNote(app, 'Is the rounding right?');

      const [stored] = await threadsOf(world, tourId);
      expect(stored?.kind).toBe('note');
      expect(noteText(stored)).toBe('Is the rounding right?');
      expect(askBox().value).toBe('');
      await waitFor(() => expect(threadRow(id)?.textContent).toContain('Is the rounding right?'));
      const view = await openThreadView(id);
      expect(view.getByText('Is the rounding right?', { selector: '[data-question]' })).toBeTruthy();
      expect(view.getByText('Draft')).toBeTruthy();
      expect(view.queryByRole('textbox', { name: 'Follow-up for Claude' })).toBeNull();
      expect((view.getByRole('button', { name: 'Send to Claude' }) as HTMLButtonElement).disabled).toBe(true);
   });

   it("edits a note's words and keeps it a draft", async () => {
      const {
         world,
         tourId,
         notes: [n1],
      } = await start(false, ['First words.']);

      const view = await openThreadView(n1);
      await fireEvent.click(view.getByRole('button', { name: 'Edit' }));
      await fireEvent.input(view.getByRole('textbox', { name: 'Note' }), { target: { value: 'Second words.' } });
      await fireEvent.click(view.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(view.queryByRole('textbox', { name: 'Note' })).toBeNull());
      expect(view.getByText('Second words.', { selector: '[data-question]' })).toBeTruthy();
      expect(noteText((await threadsOf(world, tourId))[0])).toBe('Second words.');
      expect(view.getByText('Draft')).toBeTruthy();
   });

   it('keeps the note as it was when the reviewer cancels an edit', async () => {
      const {
         transport,
         notes: [n1],
      } = await start(false, ['First words.']);

      const view = await openThreadView(n1);
      await fireEvent.click(view.getByRole('button', { name: 'Edit' }));
      await fireEvent.input(view.getByRole('textbox', { name: 'Note' }), { target: { value: 'Unsaved.' } });
      await fireEvent.click(view.getByRole('button', { name: 'Cancel' }));

      expect(view.getByText('First words.', { selector: '[data-question]' })).toBeTruthy();
      expect(transport.requests.some((request) => request.operation === 'edit_note')).toBe(false);
   });

   it("discards a note from its thread view's menu, and its thread leaves the list", async () => {
      const {
         world,
         tourId,
         app,
         notes: [n1, n2],
      } = await start(false, ['Note one', 'Note two']);

      await openThreadView(n1);
      await chooseFromThreadMenu('Delete');

      await waitFor(() => expect(threadRow(n2!)).not.toBeNull());
      expect(threadRow(n1!)).toBeNull();
      expect((await threadsOf(world, tourId)).map((thread) => thread.id)).toEqual([n2]);
      expect(app.selectedThreads.map((thread) => thread.id)).toEqual([n2]);
      await waitFor(() => expect(document.activeElement?.textContent).toBe('Showing 1 of 1 threads.'));
   });

   it('drops a note that another view discards, and keeps it gone when a load from before the discard lands after it', async () => {
      const {
         world,
         tourId,
         app,
         transport,
         notes: [n1, n2],
      } = await start(false, ['Note one', 'Note two']);
      const reload = transport.holdAnswered('read_state');
      transport.cutEventStreams();
      await reload.answered;

      await world.reviewer.call('discard_note', { tourId, threadId: n1! });
      await waitFor(() => expect(threadRow(n1!)).toBeNull());
      await reload.release();
      await settled();

      expect(app.selectedThreads.map((thread) => thread.id)).toEqual([n2]);
   });

   it('shows a refusal when the note was sent elsewhere before the edit reached the server', async () => {
      const {
         world,
         tourId,
         transport,
         notes: [n1],
      } = await start(true, ['First words.']);
      // Another view sends the note while this page hears nothing of it.
      transport.holdEvents();
      await world.reviewer.call('send_notes', { tourId, threadIds: [n1!] });

      const view = await openThreadView(n1);
      await fireEvent.click(view.getByRole('button', { name: 'Edit' }));
      await fireEvent.input(view.getByRole('textbox', { name: 'Note' }), { target: { value: 'Too late.' } });
      await fireEvent.click(view.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(view.getByRole('alert').textContent).toMatch(/not a note any more/));
   });

   it('sends one note alone, which then waits for Claude', async () => {
      const {
         world,
         tourId,
         transport,
         notes: [n1, n2],
      } = await start(true, ['First.', 'Second.']);

      const view = await openThreadView(n2);
      await fireEvent.click(view.getByRole('button', { name: 'Send to Claude' }));

      await waitFor(async () => expect((await threadsOf(world, tourId))[1]?.kind).toBe('asked'));
      expect(transport.requests.find((request) => request.operation === 'send_notes')?.input).toEqual({ tourId, threadIds: [n2] });
      await waitFor(() => expect(view.queryByText('Draft')).toBeNull());
      expect(view.getByText(/Waiting for Claude's reply/)).toBeTruthy();
      expect(noteText((await threadsOf(world, tourId)).find((thread) => thread.id === n1))).toBe('First.');
   });

   it('sends two chosen notes as a note batch from the bar above the ask box, each in its own thread', async () => {
      const {
         world,
         tourId,
         transport,
         notes: [n1, n2, n3],
      } = await start(true, ['First.', 'Second.', 'Third.']);
      expect(panel().queryByRole('button', { name: 'Send chosen notes' })).toBeNull();

      await chooseNote(n1!);
      await chooseNote(n3!);
      await waitFor(() => expect(batchButton().disabled).toBe(false));
      expect(panel().getByText('2 notes chosen.')).toBeTruthy();
      await fireEvent.click(batchButton());

      await waitFor(async () => expect((await threadsOf(world, tourId)).filter((thread) => thread.kind !== 'note').map((thread) => thread.id)).toEqual([n1, n3]));
      expect(transport.requests.find((request) => request.operation === 'send_notes')?.input).toEqual({ tourId, threadIds: [n1, n3] });
      await waitFor(() => expect(threadRow(n1!)?.textContent).not.toContain('Not sent to Claude'));
      expect(threadRow(n3!)?.textContent).not.toContain('Not sent to Claude');
      expect(threadRow(n2!)?.textContent).toContain('Not sent to Claude');
      expect((await threadsOf(world, tourId)).map((thread) => thread.kind === 'asked' && thread.messages[0].requests.length)).toEqual([1, false, 1]);
      await waitFor(() => expect(panel().queryByText(/notes chosen/)).toBeNull());
      expect(panel().queryByRole('button', { name: 'Send chosen notes' })).toBeNull();
   });

   it('refuses to send a note while its edit is open, in its thread view, its menu and the bar of chosen notes, and sends once the edit ends', async () => {
      const {
         transport,
         notes: [n1, n2],
      } = await start(true, ['First words.', 'Second words.']);
      const unsaved = 'Save or cancel the edit of “First words.” first.';
      await chooseNote(n1!);
      await chooseNote(n2!);
      await waitFor(() => expect(batchButton().disabled).toBe(false));

      const view = await openThreadView(n1);
      await fireEvent.click(view.getByRole('button', { name: 'Edit' }));
      await fireEvent.input(view.getByRole('textbox', { name: 'Note' }), { target: { value: 'Unsaved words.' } });
      const single = view.getByRole('button', { name: 'Send to Claude' }) as HTMLButtonElement;
      expect(single.disabled).toBe(true);
      expect(description(single)).toBe(unsaved);
      await backToList();

      expect(batchButton().disabled).toBe(true);
      expect(description(batchButton())).toBe(unsaved);
      await fireEvent.keyDown(within(threadRow(n1!)!).getByRole('button', { name: 'Note actions' }), { key: 'Enter' });
      const item = await screen.findByRole('menuitem', { name: 'Send to Claude', hidden: true });
      expect(item.getAttribute('aria-disabled')).toBe('true');
      expect(description(item)).toBe(unsaved);
      await fireEvent.click(item);
      await fireEvent.keyDown(screen.getByRole('menu', { hidden: true }), { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('menu', { hidden: true })).toBeNull());
      expect(transport.requests.some((request) => request.operation === 'send_notes')).toBe(false);

      await fireEvent.click((await openThreadView(n1)).getByRole('button', { name: 'Cancel' }));
      await backToList();
      expect(batchButton().disabled).toBe(false);
      await fireEvent.click(batchButton());
      await waitFor(() => expect(transport.requests.find((request) => request.operation === 'send_notes')?.input).toMatchObject({ threadIds: [n1, n2] }));
   });

   it("disables sending notes with the server's reason while no session is attached, and enables it when one attaches", async () => {
      const {
         world,
         tourId,
         notes: [n1],
      } = await start(false, ['Note one']);
      const view = await openThreadView(n1);
      await fireEvent.click(view.getByRole('checkbox', { name: 'Send with other notes' }));
      expect((view.getByRole('button', { name: 'Send to Claude' }) as HTMLButtonElement).disabled).toBe(true);
      await backToList();

      expect(batchButton().disabled).toBe(true);
      expect(description(batchButton())).toMatch(noSession);

      await attachClaude(world, tourId);

      await waitFor(() => expect(batchButton().disabled).toBe(false));
      expect(sendButton().disabled).toBe(true);
      await fireEvent.input(askBox(), { target: { value: 'Now?' } });
      expect(sendButton().disabled).toBe(false);
   });

   it('shows a sent question as sent, and as received once Claude fetched it', async () => {
      const { app, tourId, claude } = await start(true);
      await fireEvent.input(askBox(), { target: { value: 'Why two passes?' } });
      await fireEvent.click(sendButton());
      await waitFor(() => expect(app.selectedThreads).toHaveLength(1));
      const id = app.selectedThreads[0]!.id;
      const view = await openThreadView(id);
      await waitFor(() => expect(view.getByText('Sent')).toBeTruthy());

      const question = await claude!.next('question');
      await claude!.call('fetch_question', { tourId, questionId: question.questionId });

      await waitFor(() => expect(view.getByText('Received')).toBeTruthy());
      expect(view.queryByText('Sent')).toBeNull();
      expect(view.getByText(/Waiting for Claude's reply/)).toBeTruthy();
   });

   it("keeps a received mark that arrives before the ask's response", async () => {
      const { app, tourId, claude, transport } = await start(true);
      const held = transport.holdAnswered('ask_question');
      await fireEvent.input(askBox(), { target: { value: 'Why two passes?' } });
      await fireEvent.click(sendButton());
      await held.answered;
      await waitFor(() => expect(app.selectedThreads).toHaveLength(1));
      const id = app.selectedThreads[0]!.id;
      const view = await openThreadView(id);

      const question = await claude!.next('question');
      await claude!.call('fetch_question', { tourId, questionId: question.questionId });
      await waitFor(() => expect(view.getByText('Received')).toBeTruthy());
      await held.release();
      await settled();

      expect(view.getByText('Received')).toBeTruthy();
   });
});
