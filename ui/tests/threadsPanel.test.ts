import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { rowTime } from '../src/lib/labels';
import { ApiError } from '../src/lib/api';
import { ThreadActions, type ThreadOperations } from '../src/lib/threadActions.svelte';
import { nowhere } from '../src/lib/threadFilters';
import { ThreadsView } from '../src/lib/threadsView.svelte';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { AskedThread, NoteThread, Offer, Plan, QuestionThread, StartedThread, ThreadReply, Tour, TourControls } from '../src/lib/types';
import { eventFixtures, outputFixtures } from './contract/fixtures.generated';
import ThreadsPanelHost from './fixtures/ThreadsPanelHost.svelte';

afterEach(() => cleanup());

const fixtureTour: Tour = outputFixtures.read_state;
const plan: Plan = {
   chapters: [{ id: 'c1', title: 'Checkout' }],
   stops: [{ stage: 'pending', anchorHint: null, id: 's1', title: 'Cart totals', kind: 'inspect', chapter: 'c1', status: 'pending' }],
   fileMarks: [],
};
// The view starts at noon, so a message from before then is not new.
const viewStart = Date.parse('2026-10-23T12:00:00+00:00');
const open: Offer = { state: 'open' };

/** The fixture tour's controls, with every offer of each thread open. */
function controlsFor(threads: readonly QuestionThread[]): TourControls {
   const { tourId, revision, offers } = structuredClone(eventFixtures.controlsChanged);
   return { tourId, revision, offers: { ...offers, threads: Object.fromEntries(threads.map((thread) => [thread.id, { resend: open, followUp: open, cancel: open, sendNote: open }])) } };
}

const question: AskedThread = {
   kind: 'asked',
   id: 'asked',
   anchor: { kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 1, endLine: 2, commit: 'c'.repeat(40), text: 'x' },
   state: 'open',
   status: { questions: [{ questionId: 'q1', outcome: 'answered' }] },
   messages: [
      { kind: 'question', id: 'q1', createdAt: '2026-10-23T10:00:00+00:00', text: 'Why two passes?', requests: [{ id: 'r1', state: 'done', sessionId: 's', sentAt: '2026-10-23T10:00:00+00:00' }] },
      { kind: 'reply', id: 'a1', createdAt: '2026-10-23T10:01:00+00:00', requestId: 'r1', sessionId: 's', text: 'Each pass\nrounds.', html: '<p>Each pass\nrounds.</p>' },
   ],
};
const tourAction: AskedThread = {
   kind: 'asked',
   id: 'action',
   action: { kind: 'explain' },
   anchor: { kind: 'stop', stopId: 's1' },
   state: 'open',
   status: { waiting: { questionId: 'q2', requestId: 'r2', sessionId: 's' }, questions: [{ questionId: 'q2', outcome: 'waiting' }] },
   messages: [{ kind: 'question', id: 'q2', createdAt: '2026-10-23T10:02:00+00:00', text: '', requests: [{ id: 'r2', state: 'pending', sessionId: 's', sentAt: '2026-10-23T10:02:00+00:00' }] }],
};
const started: StartedThread = {
   kind: 'started',
   id: 'started',
   anchor: { kind: 'tour' },
   state: 'resolved',
   status: { questions: [] },
   messages: [{ kind: 'agentMessage', id: 'm1', createdAt: '2026-10-23T10:03:00+00:00', sessionId: 's', text: 'The rounding needs a test.', html: '<p>The rounding needs a test.</p>' }],
};
const asksBack: StartedThread = { ...started, id: 'asksBack', state: 'open' };
const note = (id: string, text: string): NoteThread => ({ kind: 'note', id, anchor: { kind: 'tour' }, state: 'open', status: { questions: [] }, text, savedAt: '2026-10-23T10:04:00+00:00' });

interface Shown {
   view: ThreadsView;
   record: TourRecord;
   sendNotes: ReturnType<typeof vi.fn>;
   /** Moves the clock that the view counts new messages against. */
   setClock: (time: number) => void;
   /** Unmounts the panel and mounts it again on the same view, as a return to the tour does. */
   remount: () => void;
}

/** Renders the Threads panel over a record that holds the threads, in their order, with thread actions whose note sends the spy answers, and whose other operations the overrides replace. */
function show(threads: QuestionThread[], overrides: Partial<ThreadOperations> = {}): Shown {
   const record = new TourRecord(fixtureTour.id);
   threads.forEach((thread) => record.putThread(thread, 1));
   let clock = viewStart;
   const view = new ThreadsView(
      record,
      () => nowhere,
      () => clock,
   );
   const sendNotes = vi.fn(async (tourId: string, threadIds: string[]) => ({
      output: { tourId, updatedAt: '2026-10-23T12:00:00+00:00', threads: threadIds.map((threadId) => record.thread(threadId)!) },
      revision: 2,
   }));
   const unused = async (): Promise<never> => {
      throw new Error('The panel does not make this write in these tests.');
   };
   const operations = {
      askQuestion: unused,
      requestTourAction: unused,
      askFollowUp: async (_tourId: string, threadId: string) => ({ output: record.thread(threadId)!, revision: 2 }),
      resendQuestion: unused,
      cancelQuestion: unused,
      setThreadState: unused,
      saveNote: unused,
      editNote: unused,
      discardNote: unused,
      sendNotes,
      ...overrides,
   } as unknown as ThreadOperations;
   const actions = new ThreadActions(fixtureTour.id, { operations: () => operations, touch: () => {}, signOut: () => {} }, record, view);
   const props = { actions, view, binding: null, controls: controlsFor(threads), plan, stop: null, onfollowreference: () => {} };
   render(ThreadsPanelHost, { props });
   const remount = () => {
      cleanup();
      render(ThreadsPanelHost, { props });
   };
   return { view, record, sendNotes, setClock: (time) => (clock = time), remount };
}

const panel = () => within(document.body);
// A negative check needs the page to have handled what the write's response brought, which no condition on the page shows.
const settled = () => new Promise((resolve) => setTimeout(resolve, 50));

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
   let resolve!: (value: T) => void;
   const promise = new Promise<T>((done) => (resolve = done));
   return { promise, resolve };
}

/** A refusal of a write as the server sends it, with its reason. */
const refusal = (reason: string) => new ApiError(409, { code: 'questionWaiting', problem: reason, field: null, reason });
const list = () => within(screen.getByRole('list', { name: 'Threads' }));
const scroller = () => screen.getByRole('list', { name: 'Threads', hidden: true }).closest<HTMLElement>('.scroll-box')!;
const row = (threadId: string) => document.querySelector<HTMLElement>(`li[data-thread-id="${threadId}"]`)!;
// The row's first button opens the thread view, and the ⋯ button after it opens the thread's menu.
const openButton = (threadId: string) => within(row(threadId)).getAllByRole('button')[0]!;

/** Returns what a row shows: where, when, the title, the last line and the marker's state. */
function shownRow(threadId: string) {
   const element = row(threadId);
   const text = (selector: string) => element.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
   return { where: text('[data-where]'), time: text('[data-time]'), title: text('[data-title]'), last: text('[data-last]'), mark: element.querySelector<HTMLElement>('[data-mark]')?.dataset.mark };
}

describe('the thread list', () => {
   it('shows each thread as a row with where it is, when it last changed, its title, its last message and its state marker, newest first', () => {
      show([question, tourAction, asksBack, note('n1', 'Check the  rounding.')]);
      const time = (timestamp: string) => rowTime(timestamp, new Date());

      expect(
         list()
            .getAllByRole('listitem')
            .map((item) => item.dataset.threadId),
      ).toEqual(['n1', 'asksBack', 'action', 'asked']);
      expect(shownRow('asked')).toEqual({ where: 'src/cart.ts, lines 1–2', time: time('2026-10-23T10:01:00+00:00'), title: 'Why two passes?', last: 'Claude: Each pass rounds.', mark: undefined });
      expect(shownRow('action')).toEqual({ where: 'Stop 1 · Cart totals', time: time('2026-10-23T10:02:00+00:00'), title: 'Tour action Explain deeper', last: 'Sent to Claude', mark: 'waiting' });
      expect(shownRow('asksBack')).toEqual({
         where: expect.stringMatching(/^The tour/),
         time: time('2026-10-23T10:03:00+00:00'),
         title: 'The rounding needs a test.',
         last: 'Claude started this thread',
         mark: 'yourTurn',
      });
      expect(shownRow('n1')).toEqual({ where: 'The tour', time: time('2026-10-23T10:04:00+00:00'), title: 'Check the rounding.', last: 'Not sent to Claude', mark: 'note' });
      expect(row('asksBack').textContent).toContain('Your turn');
   });

   it('names the state of a thread that is not open on its row', async () => {
      const { view } = show([started]);
      view.choose({ ...view.filter, states: ['open', 'resolved'] });

      await waitFor(() => expect(row('started')).toBeTruthy());
      expect(within(row('started')).getByText('Resolved')).toBeTruthy();
   });

   it('marks a reply that arrived after the view started as new until the reviewer has viewed its thread', async () => {
      const late: AskedThread = { ...question, messages: [question.messages[0], { ...(question.messages[1] as ThreadReply), createdAt: '2026-10-23T12:30:00+00:00' }] };
      const { setClock } = show([late]);
      expect(shownRow('asked').mark).toBe('newReply');

      await fireEvent.click(openButton('asked'));
      setClock(Date.parse('2026-10-23T13:00:00+00:00'));
      await fireEvent.click(await screen.findByRole('button', { name: 'Back to all threads' }));

      await waitFor(() => expect(row('asked')).toBeTruthy());
      expect(shownRow('asked').mark).toBeUndefined();
   });

   it('opens the thread view of a row that the reviewer clicks, and goes back to the list with the back arrow', async () => {
      show([question, note('n1', 'Later.')]);

      await fireEvent.click(openButton('asked'));

      const view = within(await screen.findByRole('article', { name: 'Why two passes?' }));
      expect(view.getByText('Why two passes?', { selector: '[data-question]' })).toBeTruthy();
      expect(view.getByRole('textbox', { name: 'Follow-up for Claude' })).toBeTruthy();
      expect(screen.queryByRole('list', { name: 'Threads' })).toBeNull();
      expect(screen.queryByRole('textbox', { name: 'Question for Claude' })).toBeNull();
      await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('article')));

      await fireEvent.click(view.getByRole('button', { name: 'Back to all threads' }));

      await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
      expect(panel().getByRole('textbox', { name: 'Question for Claude' })).toBeTruthy();
      expect(document.activeElement).toBe(openButton('asked'));
   });

   it('keeps the words in the ask box while a thread view shows', async () => {
      show([question]);
      await fireEvent.input(screen.getByRole('textbox', { name: 'Question for Claude' }), { target: { value: 'Half typed' } });

      await fireEvent.click(openButton('asked'));
      await fireEvent.click(await screen.findByRole('button', { name: 'Back to all threads' }));

      await waitFor(() => expect((screen.getByRole('textbox', { name: 'Question for Claude' }) as HTMLTextAreaElement).value).toBe('Half typed'));
   });

   it('takes the scroll position that the list had once the back arrow shows it again', async () => {
      show([question, tourAction, asksBack]);
      scroller().scrollTop = 120;

      await fireEvent.scroll(scroller());

      await fireEvent.click(openButton('action'));
      await fireEvent.click(await screen.findByRole('button', { name: 'Back to all threads' }));

      await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
      expect(scroller().scrollTop).toBe(120);
   });

   it('takes the scroll position that the list had when a thread opened from elsewhere, as from a marker, once the back arrow shows the list again', async () => {
      const { view } = show([question, tourAction, asksBack]);
      scroller().scrollTop = 120;
      await fireEvent.scroll(scroller());

      view.open('action');
      await fireEvent.click(await screen.findByRole('button', { name: 'Back to all threads' }));

      await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
      expect(scroller().scrollTop).toBe(120);
   });

   it('takes the position that the list had before the first thread view when another thread opens from elsewhere while a thread view shows', async () => {
      const { view } = show([question, tourAction, asksBack]);
      scroller().scrollTop = 120;
      await fireEvent.scroll(scroller());
      await fireEvent.click(openButton('asked'));
      await screen.findByRole('article', { name: 'Why two passes?' });

      view.open('action');
      await screen.findByRole('article', { name: /Explain deeper/ });
      await fireEvent.click(screen.getByRole('button', { name: 'Back to all threads' }));

      await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
      expect(scroller().scrollTop).toBe(120);
   });

   it('takes the scroll position once a list that mounted while the panel was hidden shows, since a hidden box ignores a scroll position set on it', async () => {
      // A browser's observer reports each box once when it starts observing it, whether it shows or not.
      const resized: (() => void)[] = [];
      vi.stubGlobal(
         'ResizeObserver',
         class {
            private readonly callback: () => void;
            constructor(callback: () => void) {
               this.callback = callback;
               resized.push(callback);
            }
            observe() {
               this.callback();
            }
            unobserve() {}
            disconnect() {}
         },
      );
      try {
         const { record } = show([question, tourAction, asksBack]);
         const container = scroller().closest<HTMLElement>('body > div')!;
         scroller().scrollTop = 120;
         await fireEvent.scroll(scroller());
         await fireEvent.click(openButton('action'));
         await screen.findByRole('article');

         // The panel hides, and the opened thread leaves the record, so the list mounts in the hidden panel.
         container.hidden = true;
         record.removeThread('action', 5);
         await waitFor(() => expect(screen.queryByRole('article', { hidden: true })).toBeNull());
         expect(scroller().scrollTop).toBe(0);

         container.hidden = false;
         resized.forEach((callback) => callback());

         expect(scroller().scrollTop).toBe(120);
      } finally {
         vi.unstubAllGlobals();
      }
   });

   it('shows the bar of the chosen notes once the reviewer chooses notes in their thread views, and sends two chosen notes as one note batch', async () => {
      const { sendNotes } = show([note('n1', 'First.'), note('n2', 'Second.'), note('n3', 'Third.')]);
      expect(screen.queryByRole('group', { name: 'Send notes' })).toBeNull();

      for (const threadId of ['n1', 'n3']) {
         await fireEvent.click(openButton(threadId));
         await fireEvent.click(await screen.findByRole('checkbox', { name: 'Send with other notes' }));
         await fireEvent.click(screen.getByRole('button', { name: 'Back to all threads' }));
         await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
      }

      const bar = within(screen.getByRole('group', { name: 'Send notes' }));
      expect(bar.getByText('2 notes chosen.')).toBeTruthy();
      await fireEvent.click(bar.getByRole('button', { name: 'Send chosen notes' }));

      await waitFor(() => expect(sendNotes).toHaveBeenCalledTimes(1));
      expect(sendNotes).toHaveBeenCalledWith(fixtureTour.id, ['n1', 'n3']);
   });

   it('shows the thread list again when the thread whose view shows leaves the record', async () => {
      const { record } = show([question]);
      await fireEvent.click(openButton('asked'));
      await screen.findByRole('article');

      record.removeThread('asked', 5);

      await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
      expect(screen.getByRole('textbox', { name: 'Question for Claude' })).toBeTruthy();
   });
});

describe('the drafts of a thread view', () => {
   const followUpField = () => screen.getByRole('textbox', { name: 'Follow-up for Claude' }) as HTMLTextAreaElement;
   const pickCode = (view: ThreadsView) => view.pick({ kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 1, endLine: 1 }, 'x');

   async function openRow(threadId: string) {
      await fireEvent.click(openButton(threadId));
      return screen.findByRole('article');
   }

   async function back() {
      await fireEvent.click(screen.getByRole('button', { name: 'Back to all threads' }));
      await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
   }

   it('keeps the unsent words of a follow-up when the reviewer goes back to the list and opens the thread again', async () => {
      show([question]);
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'Half a follow-up' } });

      await back();
      await openRow('asked');

      expect(followUpField().value).toBe('Half a follow-up');
   });

   it('keeps the unsent words of a follow-up through a pick, which shows the list', async () => {
      const { view } = show([question]);
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'Half a follow-up' } });

      pickCode(view);
      await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
      await openRow('asked');

      expect(followUpField().value).toBe('Half a follow-up');
   });

   it("keeps a note's edit in progress through a pick, with the editor open", async () => {
      const { view } = show([note('n1', 'First words.')]);
      await openRow('n1');
      await fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
      await fireEvent.input(screen.getByRole('textbox', { name: 'Note' }), { target: { value: 'Second words.' } });

      pickCode(view);
      await waitFor(() => expect(screen.queryByRole('article')).toBeNull());
      await openRow('n1');

      expect((screen.getByRole('textbox', { name: 'Note' }) as HTMLTextAreaElement).value).toBe('Second words.');
   });

   it("keeps each thread's follow-up words when another thread opens from elsewhere, and the first thread opens again", async () => {
      const { view } = show([question, { ...question, id: 'other', messages: [{ ...question.messages[0], id: 'q9', text: 'Other?' }, question.messages[1]!] }]);
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'For the first' } });

      view.open('other');
      await screen.findByRole('article', { name: 'Other?' });
      expect(followUpField().value).toBe('');
      view.open('asked');
      await screen.findByRole('article', { name: 'Why two passes?' });

      expect(followUpField().value).toBe('For the first');
   });

   it("keeps the unsent words of a follow-up when the panel mounts again on the tour's threads view, as on a return to the tour", async () => {
      const { view, remount } = show([question]);
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'Half a follow-up' } });

      view.drop();
      remount();
      await openRow('asked');

      expect(followUpField().value).toBe('Half a follow-up');
   });

   it('keeps the words typed while a follow-up was sending, and clears the words that went', async () => {
      const sent = deferred<void>();
      show([question], { askFollowUp: async () => (await sent.promise, { output: question, revision: 2 }) });
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'And the tax?' } });
      await fireEvent.click(screen.getByRole('button', { name: 'Send the follow-up' }));

      await fireEvent.input(followUpField(), { target: { value: 'And the tax? And shipping?' } });
      sent.resolve();

      await waitFor(() => expect(screen.getByRole('button', { name: 'Send the follow-up' }).getAttribute('aria-disabled')).toBe('false'));
      expect(followUpField().value).toBe('And the tax? And shipping?');
   });

   it('keeps the words of a follow-up that the server refused, with the reason', async () => {
      show([question], { askFollowUp: async () => Promise.reject(refusal('No session can receive it.')) });
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'And the tax?' } });

      await fireEvent.click(screen.getByRole('button', { name: 'Send the follow-up' }));

      expect((await screen.findByRole('alert')).textContent).toContain('No session can receive it.');
      expect(followUpField().value).toBe('And the tax?');
   });

   it('shows the refusal of a follow-up in the view where it landed, and not again once the reviewer went back and opened the thread again', async () => {
      show([question], { askFollowUp: async () => Promise.reject(refusal('No session can receive it.')) });
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'And the tax?' } });
      await fireEvent.click(screen.getByRole('button', { name: 'Send the follow-up' }));
      await screen.findByRole('alert');

      await back();
      await openRow('asked');

      expect(screen.queryByRole('alert')).toBeNull();
      expect(followUpField().value).toBe('And the tax?');
   });

   it('shows the refusal of a follow-up that landed while no thread view showed once the thread opens again, and not on the next open', async () => {
      const sent = deferred<void>();
      show([question], { askFollowUp: async () => (await sent.promise, Promise.reject(refusal('No session can receive it.'))) });
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'And the tax?' } });
      await fireEvent.click(screen.getByRole('button', { name: 'Send the follow-up' }));

      await back();
      sent.resolve();
      await settled();
      await openRow('asked');

      expect((await screen.findByRole('alert')).textContent).toContain('No session can receive it.');
      await back();
      await openRow('asked');
      expect(screen.queryByRole('alert')).toBeNull();
   });

   it("shows the refusal of a note's send in the view where it landed, and not again once the reviewer went back and opened the note again", async () => {
      show([note('n1', 'Later.')], { sendNotes: async () => Promise.reject(refusal('No session can receive it.')) });
      await openRow('n1');
      await fireEvent.click(screen.getByRole('button', { name: 'Send to Claude' }));
      await screen.findByRole('alert');

      await back();
      await openRow('n1');

      expect(screen.queryByRole('alert')).toBeNull();
   });

   it('holds back a second send of a follow-up whose first send is in flight after the reviewer went back and opened the thread again', async () => {
      const sent = deferred<void>();
      const askFollowUp = vi.fn(async () => (await sent.promise, { output: question, revision: 2 }));
      show([question], { askFollowUp });
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'And the tax?' } });
      await fireEvent.click(screen.getByRole('button', { name: 'Send the follow-up' }));

      await back();
      await openRow('asked');
      const send = screen.getByRole('button', { name: 'Send the follow-up' });
      expect(send.getAttribute('aria-disabled')).toBe('true');
      await fireEvent.click(send);
      sent.resolve();

      await waitFor(() => expect(followUpField().value).toBe(''));
      expect(askFollowUp).toHaveBeenCalledTimes(1);
   });

   it('clears the words of a follow-up that the server stored after the reviewer went back, once the thread opens again', async () => {
      const sent = deferred<void>();
      show([question], { askFollowUp: async () => (await sent.promise, { output: question, revision: 2 }) });
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'And the tax?' } });
      await fireEvent.click(screen.getByRole('button', { name: 'Send the follow-up' }));

      await back();
      sent.resolve();
      await settled();
      await openRow('asked');

      expect(followUpField().value).toBe('');
   });

   describe("a note's edit", () => {
      const noteField = () => screen.getByRole('textbox', { name: 'Note' }) as HTMLTextAreaElement;
      const old = note('n1', 'Old.');

      /** Renders the note with an edit whose response waits for `saved`, and answers with the note holding the words that the edit sent. */
      function showHeld(saved: Promise<void>) {
         const editNote = vi.fn(async (tourId: string, _threadId: string, text: string) => {
            await saved;
            return { output: { tourId, updatedAt: '2026-10-23T12:00:00+00:00', thread: { ...old, text } }, revision: 3 };
         });
         return { ...show([old], { editNote }), editNote };
      }

      async function editTo(words: string) {
         await fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
         await fireEvent.input(noteField(), { target: { value: words } });
         await fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      }

      it('closes the editor once the save lands, also when the reviewer canceled and opened the edit again while the save was in flight', async () => {
         const saved = deferred<void>();
         showHeld(saved.promise);
         await openRow('n1');
         await editTo('New.');

         await fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
         await fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
         expect(noteField().value).toBe('New.');
         saved.resolve();

         await waitFor(() => expect(screen.queryByRole('textbox', { name: 'Note' })).toBeNull());
         expect(screen.getByText('New.', { selector: '[data-question]' })).toBeTruthy();
      });

      it('keeps the editor closed when the reviewer canceled it while the save was in flight, with the saved words shown', async () => {
         const saved = deferred<void>();
         showHeld(saved.promise);
         await openRow('n1');
         await editTo('New.');

         await fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
         saved.resolve();

         await waitFor(() => expect(screen.getByText('New.', { selector: '[data-question]' })).toBeTruthy());
         expect(screen.queryByRole('textbox', { name: 'Note' })).toBeNull();
      });

      it('keeps the words of the save in flight for an edit that opens again, when the reviewer pressed Save a second time meanwhile', async () => {
         const saved = deferred<void>();
         showHeld(saved.promise);
         await openRow('n1');
         await editTo('New.');

         await fireEvent.click(screen.getByRole('button', { name: 'Save' }));
         await fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
         await fireEvent.click(screen.getByRole('button', { name: 'Edit' }));

         expect(noteField().value).toBe('New.');
         saved.resolve();
         await waitFor(() => expect(screen.queryByRole('textbox', { name: 'Note' })).toBeNull());
      });

      it('keeps the editor open with the words typed while the save was in flight', async () => {
         const saved = deferred<void>();
         const { record } = showHeld(saved.promise);
         await openRow('n1');
         await editTo('New.');

         await fireEvent.input(noteField(), { target: { value: 'Newer.' } });
         saved.resolve();

         await waitFor(() => expect(screen.getByRole('button', { name: 'Save' }).getAttribute('aria-disabled')).toBe('false'));
         expect(record.thread('n1')).toMatchObject({ text: 'New.' });
         expect(noteField().value).toBe('Newer.');
      });

      it('keeps the editor open with the words of a save that the server refused, with the reason', async () => {
         show([old], { editNote: async () => Promise.reject(refusal('The note was sent.')) });
         await openRow('n1');

         await editTo('New.');

         expect((await screen.findByRole('alert')).textContent).toContain('The note was sent.');
         expect(noteField().value).toBe('New.');
      });

      it('holds back a second save in flight after the reviewer went back and opened the note again, and closes the editor once the first lands', async () => {
         const saved = deferred<void>();
         const { editNote } = showHeld(saved.promise);
         await openRow('n1');
         await editTo('New.');

         await back();
         await openRow('n1');
         const save = screen.getByRole('button', { name: 'Save' });
         expect(save.getAttribute('aria-disabled')).toBe('true');
         await fireEvent.click(save);
         saved.resolve();

         await waitFor(() => expect(screen.queryByRole('textbox', { name: 'Note' })).toBeNull());
         expect(editNote).toHaveBeenCalledTimes(1);
      });
   });

   it('clears the words of a follow-up that the server stored, also for the next time the thread opens', async () => {
      show([question]);
      await openRow('asked');
      await fireEvent.input(followUpField(), { target: { value: 'And the tax?' } });

      await fireEvent.click(screen.getByRole('button', { name: 'Send the follow-up' }));
      await waitFor(() => expect(followUpField().value).toBe(''));
      await back();
      await openRow('asked');

      expect(followUpField().value).toBe('');
   });
});

describe('the rows of the thread list after a thread view', () => {
   it('count the days from when the list showed last, also after a pick shows it', async () => {
      vi.useFakeTimers({ toFake: ['Date'] });
      try {
         vi.setSystemTime(new Date(2026, 9, 23, 23, 0));
         const asked = { ...question, messages: [{ ...question.messages[0], createdAt: new Date(2026, 9, 23, 22, 0).toISOString() }] } as AskedThread;
         const { view } = show([asked]);
         await fireEvent.click(openButton('asked'));
         await screen.findByRole('article');

         vi.setSystemTime(new Date(2026, 9, 24, 9, 0));
         view.pick({ kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 1, endLine: 1 }, 'x');

         await waitFor(() => expect(row('asked')).toBeTruthy());
         expect(shownRow('asked').time).toBe('Yesterday');
      } finally {
         vi.useRealTimers();
      }
   });
});

describe('the time of a row', () => {
   const now = new Date(2026, 9, 23, 15, 0);

   it('shows the clock time on the same day, then yesterday, the weekday within the week and the date before that', () => {
      const at = (day: number, hour = 9) => new Date(2026, 9, day, hour, 30).toISOString();

      expect(rowTime(at(23), now)).toBe(new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date(2026, 9, 23, 9, 30)));
      expect(rowTime(at(22, 23), now)).toBe('Yesterday');
      expect(rowTime(at(18), now)).toBe(new Intl.DateTimeFormat(undefined, { weekday: 'short' }).format(new Date(2026, 9, 18, 9, 30)));
      expect(rowTime(at(10), now)).toBe(new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' }).format(new Date(2026, 9, 10, 9, 30)));
      expect(rowTime('not a time', now)).toBe('not a time');
   });
});
