import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../src/lib/api';
import { ThreadActions, type ThreadOperations } from '../src/lib/threadActions.svelte';
import { nowhere } from '../src/lib/threadFilters';
import { ThreadsView } from '../src/lib/threadsView.svelte';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { AskedThread, NoteThread, Offer, Plan, QuestionThread, Tour, TourControls } from '../src/lib/types';
import { eventFixtures, outputFixtures } from './contract/fixtures.generated';
import ThreadsPanelHost from './fixtures/ThreadsPanelHost.svelte';

afterEach(() => cleanup());

const fixtureTour: Tour = outputFixtures.read_state;
const tourId = fixtureTour.id;
const plan: Plan = {
   chapters: [{ id: 'c1', title: 'Checkout' }],
   stops: [{ stage: 'pending', anchorHint: null, id: 's1', title: 'Cart totals', kind: 'inspect', chapter: 'c1', status: 'pending' }],
   fileMarks: [],
};
const open: Offer = { state: 'open' };

const answered: AskedThread = {
   kind: 'asked',
   id: 'asked',
   anchor: { kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 1, endLine: 2, commit: 'c'.repeat(40), text: 'x' },
   state: 'open',
   status: { questions: [{ questionId: 'q1', outcome: 'answered' }] },
   messages: [
      { kind: 'question', id: 'q1', createdAt: '2026-10-23T10:00:00+00:00', text: 'Why two passes?', requests: [{ id: 'r1', state: 'done', sessionId: 's', sentAt: '2026-10-23T10:00:00+00:00' }] },
      { kind: 'reply', id: 'a1', createdAt: '2026-10-23T10:01:00+00:00', requestId: 'r1', sessionId: 's', text: 'Each pass rounds.', html: '<p>Each pass rounds.</p>' },
   ],
};
const waiting: AskedThread = {
   kind: 'asked',
   id: 'waiting',
   anchor: { kind: 'stop', stopId: 's1' },
   state: 'open',
   status: { waiting: { questionId: 'q2', requestId: 'r2', sessionId: 's' }, questions: [{ questionId: 'q2', outcome: 'waiting' }] },
   messages: [
      { kind: 'question', id: 'q2', createdAt: '2026-10-23T10:02:00+00:00', text: 'Is it covered?', requests: [{ id: 'r2', state: 'pending', sessionId: 's', sentAt: '2026-10-23T10:02:00+00:00' }] },
   ],
};
const note: NoteThread = { kind: 'note', id: 'note', anchor: { kind: 'tour' }, state: 'open', status: { questions: [] }, text: 'Check the rounding.', savedAt: '2026-10-23T10:04:00+00:00' };

/** The fixture tour's controls, with every offer of each thread open. */
function controlsFor(threads: readonly QuestionThread[]): TourControls {
   const { revision, offers } = structuredClone(eventFixtures.controlsChanged);
   return { tourId, revision, offers: { ...offers, threads: Object.fromEntries(threads.map((thread) => [thread.id, { resend: open, followUp: open, cancel: open, sendNote: open }])) } };
}

/** Renders the Threads panel over the threads, with thread actions whose writes the overrides answer, and returns the record. */
function show(threads: QuestionThread[], overrides: Partial<ThreadOperations> = {}, controls: TourControls = controlsFor(threads)): TourRecord {
   const record = new TourRecord(tourId);
   threads.forEach((thread) => record.putThread(thread, 1));
   const view = new ThreadsView(record, () => nowhere);
   const unused = async (): Promise<never> => {
      throw new Error('The menu does not make this write in these tests.');
   };
   const operations: ThreadOperations = {
      askQuestion: unused,
      requestTourAction: unused,
      askFollowUp: unused,
      resendQuestion: unused,
      cancelQuestion: unused,
      setThreadState: unused,
      removeThread: unused,
      saveNote: unused,
      editNote: unused,
      discardNote: unused,
      sendNotes: unused,
      ...overrides,
   };
   const actions = new ThreadActions(tourId, { operations: () => operations, touch: () => {}, signOut: () => {} }, record, view);
   render(ThreadsPanelHost, { props: { actions, view, binding: null, controls, plan, stop: null, onfollowreference: () => {} } });
   return record;
}

const row = (threadId: string) => document.querySelector<HTMLElement>(`li[data-thread-id="${threadId}"]`)!;
const rowOpen = (threadId: string) => within(row(threadId)).getAllByRole('button')[0]!;
const menu = () => screen.queryByRole('menu', { hidden: true });
const removal = (threadId: string, updatedAt = '2026-10-23T12:00:00+00:00') => ({ output: { tourId, updatedAt, threadId }, revision: 2 });

/** Returns the names of the open menu's items, in their order. */
async function itemNames(): Promise<string[]> {
   const shown = await waitFor(() => {
      const found = menu();
      expect(found, 'no menu shows').not.toBeNull();
      return found!;
   });
   return [...shown.querySelectorAll<HTMLElement>('[role^="menuitem"]')].map((item) => item.textContent.replace(/\s+/g, ' ').trim());
}

async function closeMenu(): Promise<void> {
   await fireEvent.keyDown(menu()!, { key: 'Escape' });
   await waitFor(() => expect(menu()).toBeNull());
}

/** Opens the row's menu from its ⋯ button and returns the items' names. */
async function fromButton(threadId: string): Promise<string[]> {
   await fireEvent.keyDown(within(row(threadId)).getByRole('button', { name: threadId === 'note' ? 'Note actions' : 'Thread actions' }), { key: 'Enter' });
   return itemNames();
}

/** Opens the row's menu with a right-click and returns the items' names. */
async function fromRightClick(threadId: string): Promise<string[]> {
   await fireEvent.contextMenu(rowOpen(threadId));
   return itemNames();
}

async function choose(name: string): Promise<void> {
   await fireEvent.click(within(menu()!).getByRole('menuitem', { name, hidden: true }));
   await waitFor(() => expect(menu()).toBeNull());
}

describe("a thread's menu", () => {
   it.each([
      { kind: 'an answered thread', thread: answered, items: ['Resolve', 'Show in the code', 'Copy the question', 'Delete thread…'] },
      { kind: 'a thread whose question waits', thread: waiting, items: ['Resolve', 'Show the stop', 'Copy the question', 'Cancel', 'Send again', 'Delete thread…'] },
      { kind: 'a note', thread: note, items: ['Edit', 'Send to Claude', 'Send with other notes', 'Delete'] },
   ])('opens with the same items from the ⋯ button and from a right-click on the row of $kind', async ({ thread, items }) => {
      show([thread]);

      const button = await fromButton(thread.id);
      await closeMenu();
      const rightClick = await fromRightClick(thread.id);

      expect(button).toEqual(items);
      expect(rightClick).toEqual(items);
   });

   it.each([
      { key: 'Shift+F10', init: { key: 'F10', shiftKey: true } },
      { key: 'the context-menu key', init: { key: 'ContextMenu' } },
   ])('opens on the focused row with $key, and takes the arrow keys, Enter and Escape', async ({ init }) => {
      const setThreadState = vi.fn(async (_tourId: string, threadId: string) => ({
         output: { tourId, updatedAt: '2026-10-23T12:00:00+00:00', thread: { ...answered, id: threadId, state: 'resolved' as const } },
         revision: 2,
      }));
      show([answered], { setThreadState });
      rowOpen('asked').focus();

      await fireEvent.keyDown(rowOpen('asked'), init);
      expect(await itemNames()).toEqual(['Resolve', 'Show in the code', 'Copy the question', 'Delete thread…']);
      await closeMenu();
      await fireEvent.keyDown(document.activeElement!, init);
      await itemNames();
      await fireEvent.keyDown(menu()!, { key: 'ArrowDown' });
      await waitFor(() => expect(document.activeElement?.textContent?.trim()).toBe('Resolve'));
      await fireEvent.keyDown(document.activeElement!, { key: 'Enter' });

      await waitFor(() => expect(setThreadState).toHaveBeenCalledWith(tourId, 'asked', 'resolved'));
      expect(menu()).toBeNull();
   });

   it('keeps one menu open when a contextmenu event on the row follows the context-menu key, as Windows fires one when the key comes up', async () => {
      show([answered]);
      rowOpen('asked').focus();

      await fireEvent.keyDown(rowOpen('asked'), { key: 'ContextMenu' });
      await itemNames();
      await fireEvent.contextMenu(rowOpen('asked'));

      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(screen.queryAllByRole('menu', { hidden: true })).toHaveLength(1);
   });

   it('asks before Delete thread deletes the thread, and deletes it only once the reviewer confirms', async () => {
      const removeThread = vi.fn(async (_tourId: string, threadId: string) => removal(threadId));
      const record = show([answered, waiting], { removeThread });

      await fromButton('asked');
      await choose('Delete thread…');
      const dialog = await screen.findByRole('alertdialog');
      expect(within(dialog).getByRole('heading').textContent).toBe('Delete this thread?');
      expect(removeThread).not.toHaveBeenCalled();
      await fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
      await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
      expect(removeThread).not.toHaveBeenCalled();
      expect(record.thread('asked')).toBeDefined();

      await fromRightClick('asked');
      await choose('Delete thread…');
      await fireEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete thread' }));

      await waitFor(() => expect(row('asked')).toBeNull());
      expect(removeThread.mock.calls).toEqual([[tourId, 'asked']]);
      expect(record.thread('asked')).toBeUndefined();
      expect(row('waiting')).not.toBeNull();
   });

   it('tells in the confirmation that a question that waits is canceled', async () => {
      show([waiting]);

      await fromButton('waiting');
      await choose('Delete thread…');

      expect((await screen.findByRole('alertdialog')).textContent).toContain('Its question that waits for Claude is canceled.');
   });

   it("discards a note with its Delete, as the note's discard does", async () => {
      const discardNote = vi.fn(async (_tourId: string, threadId: string) => removal(threadId));
      const record = show([note], { discardNote });

      await fromRightClick('note');
      await choose('Delete');

      await waitFor(() => expect(record.thread('note')).toBeUndefined());
      expect(discardNote.mock.calls).toEqual([[tourId, 'note']]);
      expect(screen.queryByRole('alertdialog')).toBeNull();
   });

   it.each([
      { name: 'Cancel', thread: waiting, offer: 'cancel' as const },
      { name: 'Send again', thread: waiting, offer: 'resend' as const },
      { name: 'Send to Claude', thread: note, offer: 'sendNote' as const },
   ])("shows the server's reason under $name while its offer is closed, and makes no write", async ({ name, thread, offer }) => {
      const controls = controlsFor([thread]);
      controls.offers.threads[thread.id]![offer] = { state: 'closed', code: 'noSessionAttached', reason: 'this tour is attached to no Claude Code session.' };
      const write = vi.fn(async (): Promise<never> => {
         throw new Error('The refused item made a write.');
      });
      show([thread], { cancelQuestion: write, resendQuestion: write, sendNotes: write }, controls);

      await fromButton(thread.id);
      const item = within(menu()!).getByRole('menuitem', { name, hidden: true });
      await fireEvent.click(item);

      expect(write).not.toHaveBeenCalled();
      expect(item.getAttribute('aria-disabled')).toBe('true');
      expect(document.getElementById(item.getAttribute('aria-describedby') ?? '')?.textContent).toBe('This tour is attached to no Claude Code session.');
      expect(menu()).not.toBeNull();
   });

   it("shows the refusal of the thread view's menu Cancel when the refusal lands with the answer that ends the wait", async () => {
      const answeredNow: AskedThread = {
         ...waiting,
         status: { questions: [{ questionId: 'q2', outcome: 'answered' }] },
         messages: [
            { ...waiting.messages[0]!, requests: [{ id: 'r2', state: 'done', sessionId: 's', sentAt: '2026-10-23T10:02:00+00:00' }] },
            { kind: 'reply', id: 'a2', createdAt: '2026-10-23T10:03:00+00:00', requestId: 'r2', sessionId: 's', text: 'Yes.', html: '<p>Yes.</p>' },
         ],
      };
      const reason = "question 'q2' does not wait for an answer, because its last request is done.";
      const shown: { record?: TourRecord } = {};
      const cancelQuestion = vi.fn(async (): Promise<never> => {
         shown.record!.putThread(answeredNow, 3);
         throw new ApiError(409, { code: 'requestNotPending', problem: reason, field: 'questionId', reason });
      });
      shown.record = show([waiting], { cancelQuestion });
      await fireEvent.click(rowOpen('waiting'));
      const view = await waitFor(() => document.querySelector<HTMLElement>('article[data-thread-id="waiting"]')!);

      await fireEvent.keyDown(within(view).getByRole('button', { name: 'More thread actions' }), { key: 'Enter' });
      await itemNames();
      await choose('Cancel');

      await waitFor(() => expect(cancelQuestion).toHaveBeenCalled());
      expect((await within(view).findByRole('alert')).textContent).toBe(reason);
   });

   it("sends the thread view's menu Send again while the view's own Cancel is in flight", async () => {
      const cancelQuestion = vi.fn(() => new Promise<never>(() => {}));
      const resendQuestion = vi.fn(async () => ({ output: waiting, revision: 2 }));
      show([waiting], { cancelQuestion, resendQuestion });
      await fireEvent.click(rowOpen('waiting'));
      const view = await waitFor(() => document.querySelector<HTMLElement>('article[data-thread-id="waiting"]')!);

      await fireEvent.click(within(view).getAllByRole('button', { name: 'Cancel' })[0]!);
      await fireEvent.keyDown(within(view).getByRole('button', { name: 'More thread actions' }), { key: 'Enter' });
      await itemNames();
      await choose('Send again');

      await waitFor(() => expect(resendQuestion).toHaveBeenCalledTimes(1));
   });

   it("shows the row's latest menu refusal, and none once a later menu write succeeds", async () => {
      const reason = 'The tour could not be read.';
      const setThreadState = vi.fn(async (): Promise<never> => {
         throw new ApiError(409, { code: 'tourUnreadable', problem: reason, field: null, reason });
      });
      const writeText = vi.fn<(words: string) => Promise<void>>().mockRejectedValueOnce(new Error('Denied.')).mockResolvedValue();
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
      try {
         show([answered], { setThreadState });
         const alert = () => within(row('asked')).queryByRole('alert')?.textContent ?? null;

         await fromButton('asked');
         await choose('Resolve');
         await waitFor(() => expect(alert()).toBe(reason));
         await fromButton('asked');
         await choose('Copy the question');
         await waitFor(() => expect(alert()).toBe('The question could not be copied.'));
         await fromButton('asked');
         await choose('Copy the question');

         await waitFor(() => expect(alert()).toBeNull());
      } finally {
         Reflect.deleteProperty(navigator, 'clipboard');
      }
   });

   it("makes the menu's other writes unavailable while one menu write of the thread is in flight, so none is dropped unseen", async () => {
      const setThreadState = vi.fn(() => new Promise<never>(() => {}));
      const cancelQuestion = vi.fn(async (): Promise<never> => {
         throw new Error('The unavailable Cancel made a write.');
      });
      show([waiting], { setThreadState, cancelQuestion });

      await fromButton('waiting');
      await choose('Resolve');
      await fromButton('waiting');
      const items = within(menu()!);
      const unavailable = ['Resolve', 'Copy the question', 'Cancel', 'Send again'].map((name) => items.getByRole('menuitem', { name, hidden: true }).getAttribute('aria-disabled'));
      await fireEvent.click(items.getByRole('menuitem', { name: 'Cancel', hidden: true }));

      expect(unavailable).toEqual(['true', 'true', 'true', 'true']);
      expect(cancelQuestion).not.toHaveBeenCalled();
      expect(setThreadState).toHaveBeenCalledTimes(1);
   });

   it('copies the words of the question', async () => {
      const writeText = vi.fn(async () => {});
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
      try {
         show([answered]);

         await fromButton('asked');
         await choose('Copy the question');

         await waitFor(() => expect(writeText).toHaveBeenCalledWith('Why two passes?'));
      } finally {
         Reflect.deleteProperty(navigator, 'clipboard');
      }
   });

   it("opens with the same items from the thread view's ⋯ button and from a right-click on its header", async () => {
      show([waiting]);
      await fireEvent.click(rowOpen('waiting'));
      const header = await waitFor(() => document.querySelector<HTMLElement>('article[data-thread-id="waiting"] header')!);

      await fireEvent.keyDown(within(header).getByRole('button', { name: 'More thread actions' }), { key: 'Enter' });
      const button = await itemNames();
      await closeMenu();
      await fireEvent.contextMenu(within(header).getByRole('heading'));
      const rightClick = await itemNames();

      expect(button).toEqual(['Resolve', 'Show the stop', 'Copy the question', 'Cancel', 'Send again', 'Delete thread…']);
      expect(rightClick).toEqual(button);
   });
});
