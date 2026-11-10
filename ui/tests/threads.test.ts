import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ThreadMarkers from '../src/components/ThreadMarkers.svelte';
import { ApiError } from '../src/lib/api';
import { threadMarkerButton } from '../src/lib/editor/passageDecorations';
import { ThreadActions, type ThreadOperations } from '../src/lib/threadActions.svelte';
import { nowhere, threadStopId } from '../src/lib/threadFilters';
import { codeThreads, fileThreads, lastQuestion, lastQuestionAnswered, lastRequest, questionOf, stopTextThreads, stopThreads, threadMarker, waitingQuestionOf } from '../src/lib/threadView';
import { ThreadDrafts, ThreadsView } from '../src/lib/threadsView.svelte';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { MessageStyle } from '../src/lib/messageStyle';
import type {
   Tour,
   AskedThread,
   NoteThread,
   Offer,
   QuestionOutcome,
   QuestionRequest,
   QuestionThread,
   RequestState,
   StartedThread,
   ThreadAnchor,
   ThreadMessage,
   ThreadOffers,
   ThreadStatus,
   TourControls,
} from '../src/lib/types';
import { eventFixtures, outputFixtures } from './contract/fixtures.generated';
import OpenThreadHost from './fixtures/OpenThreadHost.svelte';

afterEach(() => cleanup());

function request(id: string, state: RequestState, sessionId = 'session-1'): QuestionRequest {
   return { id, state, sessionId, sentAt: '2026-10-23T00:00:00+00:00' };
}

/** A thread's status as the server serves it: the question that waits, by its question and request, and each question's outcome. */
function status(waiting: [questionId: string, requestId: string] | null, ...outcomes: [questionId: string, outcome: QuestionOutcome][]): ThreadStatus {
   return {
      ...(waiting === null ? {} : { waiting: { questionId: waiting[0], requestId: waiting[1], sessionId: 'session-1' } }),
      questions: outcomes.map(([questionId, outcome]) => ({ questionId, outcome })),
   };
}

function thread(id: string, requests: QuestionRequest[], shown: ThreadStatus, answer?: string): AskedThread {
   return {
      kind: 'asked',
      id,
      anchor: { kind: 'tour' },
      state: 'open',
      status: shown,
      messages: [
         { kind: 'question', id: `q-${id}`, createdAt: '2026-10-23T00:00:00+00:00', text: `Question ${id}`, requests },
         ...(answer === undefined
            ? []
            : [{ kind: 'reply' as const, id: `a-${id}`, createdAt: '2026-10-23T00:00:00+00:00', requestId: requests.at(-1)!.id, sessionId: 'session-1', text: answer, html: `<p>${answer}</p>` }]),
      ],
   };
}

const pending = thread('t1', [request('r1', 'pending')], status(['q-t1', 'r1'], ['q-t1', 'waiting']));
const answered = thread('t1', [request('r1', 'done')], status(null, ['q-t1', 'answered']), 'Yes.');
const unanswered = thread('t1', [request('r1', 'unanswered')], status(null, ['q-t1', 'unanswered']));
const canceled = thread('t1', [request('r1', 'canceled')], status(null, ['q-t1', 'canceled']));
const needsAnswer = thread('t1', [request('r1', 'needsAnswer')], status(null, ['q-t1', 'answered']), 'Which part?');

/** The thread with the reviewer's follow-up `f-<thread ID>`, its requests and the status it then has, and a reply to the follow-up's last request when an answer is given. */
function withFollowUp(base: AskedThread, requests: QuestionRequest[], shown: ThreadStatus, answer?: string): AskedThread {
   const followUp: ThreadMessage = { kind: 'question', id: `f-${base.id}`, createdAt: '2026-10-23T00:00:01+00:00', text: `Follow-up ${base.id}`, requests };
   const reply: ThreadMessage[] =
      answer === undefined
         ? []
         : [{ kind: 'reply', id: `fa-${base.id}`, createdAt: '2026-10-23T00:00:01+00:00', requestId: requests.at(-1)!.id, sessionId: 'session-1', text: answer, html: `<p>${answer}</p>` }];
   return { ...base, status: shown, messages: [...base.messages, followUp, ...reply] };
}

const followUpPending = withFollowUp(answered, [request('f1', 'pending')], status(['f-t1', 'f1'], ['q-t1', 'answered'], ['f-t1', 'waiting']));
const followUpAnswered = withFollowUp(answered, [request('f1', 'done')], status(null, ['q-t1', 'answered'], ['f-t1', 'answered']), 'It leaves out the tests.');
const fixtureTour: Tour = outputFixtures.read_state;

/** Returns a record with the threads put at their revisions, in order. */
function recordWith(...puts: [QuestionThread, number][]): TourRecord {
   const record = new TourRecord(fixtureTour.id);
   for (const [put, revision] of puts) {
      record.putThread(put, revision);
   }

   return record;
}

const load = (record: TourRecord, threads: QuestionThread[], revision: number) => record.applyLoad({ ...fixtureTour, threads, revision });
const ids = (record: TourRecord) => record.threads.map((known) => known.id);

describe('the threads that the page knows', () => {
   it("take a load in the server's order, after the threads that only an event or a response brought", () => {
      const record = recordWith([thread('t9', [request('r9', 'pending')], status(['q-t9', 'r9'], ['q-t9', 'waiting'])), 9]);

      load(record, [answered, thread('t2', [request('r2', 'pending')], status(['q-t2', 'r2'], ['q-t2', 'waiting']))], 5);

      expect(ids(record)).toEqual(['t1', 't2', 't9']);
   });

   it('keep a thread from an event or a response over a load at an older revision of the tour', () => {
      const record = recordWith([answered, 6]);
      load(record, [pending], 5);

      expect(record.threads).toEqual([answered]);
   });

   it('take a load at a newer revision than the event that brought a thread', () => {
      const record = recordWith([pending, 5]);
      load(record, [answered], 6);

      expect(record.threads).toEqual([answered]);
   });

   it('keep the thread they hold over a load or an event at its own revision, which came from the same store', () => {
      const record = recordWith([answered, 6]);
      load(record, [pending], 6);
      record.putThread(pending, 6);

      expect(record.threads).toEqual([answered]);
   });

   it('replace a thread by its ID on a newer event, without a second copy', () => {
      expect(recordWith([pending, 5], [answered, 6]).threads).toEqual([answered]);
   });

   it('drop an event at an older revision than the thread they hold', () => {
      expect(recordWith([answered, 6], [pending, 5]).threads).toEqual([answered]);
   });

   it('add the thread of a response, and keep a reply that an event brought at a newer revision over the response that arrives after it', () => {
      expect(recordWith([followUpPending, 7]).threads).toEqual([followUpPending]);
      expect(recordWith([followUpAnswered, 8], [followUpPending, 7]).threads).toEqual([followUpAnswered]);
   });

   it('remove a thread that a newer load lacks, and keep it removed against an older event', () => {
      const record = recordWith([answered, 5]);
      load(record, [], 6);
      record.putThread(answered, 5);

      expect(record.threads).toEqual([]);
   });

   it('keep a thread that only a load at an older revision lacks', () => {
      const record = recordWith([answered, 6]);
      load(record, [], 5);

      expect(record.threads).toEqual([answered]);
   });

   it('remove a deleted thread, and an older load or event does not bring it back', () => {
      const record = new TourRecord(fixtureTour.id);
      load(record, [answered], 2);
      record.removeThread('t1', 5);
      load(record, [answered], 4);
      record.putThread(followUpAnswered, 3);

      expect(record.threads).toEqual([]);
   });

   it('keep a removal that arrives before the page heard of the thread', () => {
      const record = new TourRecord(fixtureTour.id);
      record.removeThread('t1', 5);
      record.putThread(answered, 4);

      expect(record.threads).toEqual([]);
   });

   it('keep a thread over a removal at an older revision', () => {
      const record = recordWith([answered, 5]);
      record.removeThread('t1', 4);

      expect(record.threads).toEqual([answered]);
   });
});

describe("a thread's questions as the server's status names them", () => {
   it('find the last question and its last request, a follow-up included', () => {
      expect(lastQuestion(followUpPending)?.id).toBe('f-t1');
      expect(lastRequest(followUpPending)?.id).toBe('f1');
      expect(lastQuestion(answered)?.id).toBe('q-t1');
   });

   it('find the question that waits, and whether the last question was answered', () => {
      expect(waitingQuestionOf(followUpPending)?.id).toBe('f-t1');
      expect(waitingQuestionOf(answered)).toBeNull();
      expect([pending, answered, needsAnswer, canceled, unanswered, followUpPending, followUpAnswered].map(lastQuestionAnswered)).toEqual([false, true, true, false, false, false, true]);
   });
});

const open: Offer = { state: 'open' };

/** The fixture tour's controls with the thread's offers, each open unless given. */
function controlsWith(threadId: string, offers: Partial<ThreadOffers> = {}): TourControls {
   const { tourId, revision, offers: tourOffers } = structuredClone(eventFixtures.controlsChanged);
   return { tourId, revision, offers: { ...tourOffers, threads: { [threadId]: { resend: open, followUp: open, cancel: open, sendNote: open, ...offers } } } };
}

const noSession: Offer = {
   state: 'closed',
   code: 'noSessionAttached',
   reason:
      "this tour is attached to no Claude Code session, so no session can receive the question. Ask Claude in a Claude Code session that runs lotsen's channel to attach tour abcdefghijklmnop, then try again.",
};
const noSessionShown = /^This tour is attached to no Claude Code session/;

/** The writes of a thread view as a test stubs them, each returning null once the server stored it, or the server's reason when it refused it. */
interface ItemWrites {
   onresend: (questionId: string) => Promise<string | null>;
   oncancel: (questionId: string) => Promise<string | null>;
   onfollowup: (threadId: string, text: string) => Promise<string | null>;
   onsetstate: (threadId: string, state: 'open' | 'resolved') => Promise<string | null>;
}

/** Returns real thread actions whose operations hand each write to its stub, and answer with the view's thread, or refuse with the stub's reason. */
function actionsFor(item: QuestionThread, writes: ItemWrites): ThreadActions {
   const record = new TourRecord(fixtureTour.id);
   const answer = async <T>(reason: Promise<string | null>, output: T) => {
      const refused = await reason;
      if (refused !== null) {
         throw new ApiError(409, { code: 'questionWaiting', problem: refused, field: null, reason: refused });
      }

      return { output, revision: 1 };
   };
   const unused = async (): Promise<never> => {
      throw new Error('The thread view does not make this write.');
   };
   const operations: ThreadOperations = {
      askQuestion: unused,
      requestTourAction: unused,
      askFollowUp: (_, threadId, { text }) => answer(writes.onfollowup(threadId, text), item),
      resendQuestion: (_, questionId) => answer(writes.onresend(questionId), item),
      cancelQuestion: (_, questionId) => answer(writes.oncancel(questionId), item),
      setThreadState: (tourId, threadId, state) => answer(writes.onsetstate(threadId, state), { tourId, updatedAt: '2026-10-23T00:00:00+00:00', thread: item }),
      saveNote: unused,
      editNote: unused,
      discardNote: unused,
      removeThread: unused,
      sendNotes: unused,
   };
   return new ThreadActions(fixtureTour.id, { operations: () => operations, touch: () => {}, signOut: () => {} }, record, new ThreadsView(record, () => nowhere));
}

/** The props of a thread view that the tests leave as they are. */
const itemProps = { plan: null, focusedPassage: null, hover: null, onback: () => {}, onfollowreference: () => {}, onopenanchor: () => {} };

describe('the thread view', () => {
   function show(item: QuestionThread, controls: TourControls | null, onfollowup: ItemWrites['onfollowup'] = vi.fn(async () => null), messageStyle?: MessageStyle) {
      const onresend = vi.fn(async () => null);
      const oncancel = vi.fn(async () => null);
      const onsetstate = vi.fn(async () => null);
      render(OpenThreadHost, {
         props: {
            actions: actionsFor(item, { onresend, oncancel, onfollowup, onsetstate }),
            thread: item,
            controls,
            ...itemProps,
            drafts: new ThreadDrafts(),
            ...(messageStyle === undefined ? {} : { messageStyle }),
         },
      });
      return { onresend, oncancel, onfollowup, onsetstate, item: within(screen.getByRole('article')) };
   }

   const followUpBox = (item: ReturnType<typeof within>) => item.getByRole('textbox', { name: 'Follow-up for Claude' }) as HTMLTextAreaElement;
   /** Each message's writer with the state that the line under the message names, empty for none, in order. */
   const writersAndStates = () =>
      [...screen.getByRole('article').querySelectorAll<HTMLElement>('[data-writer]')].map(
         (message) => `${message.dataset.writer}:${message.querySelector('[data-delivery]')?.textContent?.trim() ?? ''}`,
      );

   const claudeStarted: StartedThread = {
      kind: 'started',
      id: 'c1',
      anchor: { kind: 'tour' },
      state: 'open',
      status: status(null),
      messages: [
         {
            kind: 'agentMessage',
            id: 'm1',
            createdAt: '2026-10-23T00:00:00+00:00',
            sessionId: 'session-1',
            text: 'The **rounding** needs a test.',
            html: '<p>The <strong>rounding</strong> needs a test.</p>',
         },
      ],
   };

   it("labels a thread that Claude started with Claude's message under Claude's name, and offers a follow-up that becomes its first question", async () => {
      const { onfollowup, item } = show(claudeStarted, controlsWith('c1'));

      const label = document.getElementById(screen.getByRole('article').getAttribute('aria-labelledby')!);
      expect(label?.textContent?.trim()).toBe('The rounding needs a test.');
      expect(writersAndStates()).toEqual(['Claude:']);
      expect(item.queryByRole('button', { name: 'Send again' })).toBeNull();
      expect(questionOf(claudeStarted)).toBeNull();
      expect(threadMarker(claudeStarted).label).toBe('The rounding needs a test.');

      await fireEvent.input(followUpBox(item), { target: { value: 'Which test?' } });
      await fireEvent.click(item.getByRole('button', { name: 'Send the follow-up' }));
      await waitFor(() => expect(onfollowup).toHaveBeenCalledWith('c1', 'Which test?'));
   });

   it("labels the marker of a thread that Claude started with the words of its message, without the Markdown or a code reference's link", () => {
      const text = 'A line in another currency reaches [`total`](ref:head/src/pricing/totals.ts#L7-9) unchecked.';
      const html =
         '<p>A line in another currency reaches <a data-ref-side="head" data-ref-path="src/pricing/totals.ts" data-ref-start-line="7" data-ref-end-line="9"><code>total</code></a> unchecked.</p>\n';
      const started: StartedThread = {
         ...claudeStarted,
         anchor: { kind: 'code', side: 'head', path: 'src/pricing/totals.ts', startLine: 7, endLine: 9, commit: 'c'.repeat(40), text: 'total' },
         messages: [{ ...claudeStarted.messages[0], text, html }],
      };
      const [marker] = codeThreads([started]);

      const button = threadMarkerButton(marker!.id, marker!.label);
      render(ThreadMarkers, { props: { threads: [threadMarker(started)], onopenthread: () => {} } });
      const rendered = screen.getByRole('button');

      const words = 'A line in another currency reaches total unchec…';
      expect([button.textContent, button.getAttribute('aria-label')]).toEqual([words, `Open the thread: ${words}`]);
      expect([rendered.textContent?.trim(), rendered.getAttribute('aria-label')]).toEqual([words, `Open the thread: ${words}`]);
   });

   it("names the writer and the state of every message under it, and gives Claude's agent message no state", () => {
      const added = withFollowUp(needsAnswer, [request('f1', 'pending')], status(['f-t1', 'f1'], ['q-t1', 'answered'], ['f-t1', 'waiting']));
      added.messages.splice(2, 0, { kind: 'agentMessage', id: 'm1', createdAt: '2026-10-23T00:00:00+00:00', sessionId: 'session-1', text: 'Noted.', html: '<p>Noted.</p>' });
      show(added, controlsWith('t1'));

      expect(writersAndStates()).toEqual(['You:Sent', 'Claude:Needs an answer', 'Claude:', 'You:Sent']);
   });

   it.each([
      ['chat bubbles', 'bubbles'],
      ['full width', 'fullWidth'],
   ] as const)("opens each of Claude's messages with Claude's name and keeps the reviewer's name in the line under the reviewer's, as %s", (_, style) => {
      show(answered, controlsWith('t1'), undefined, style);

      const [question, reply] = [...screen.getByRole('article').querySelectorAll<HTMLElement>('[data-writer]')];
      const meta = (message: HTMLElement | undefined) => message?.querySelector('[data-meta]')?.textContent?.trim();
      const opening = (message: HTMLElement | undefined) => message?.querySelector('[data-bubble]')?.firstElementChild;
      expect(opening(reply)?.matches('[data-writer-name]')).toBe(true);
      expect(opening(reply)?.textContent?.trim()).toBe('Claude');
      expect(meta(reply)).toMatch(/^\S.* · Done$/);
      expect(meta(reply)).not.toContain('Claude');
      expect(question?.querySelector('[data-writer-name]')).toBeNull();
      expect(opening(question)?.matches('[data-question]')).toBe(true);
      expect(meta(question)).toMatch(/^You · .+ · Sent$/);
   });

   it('names a question that Claude fetched as received, and a reply that declined it as declined', () => {
      const declined = thread('t1', [{ ...request('r1', 'declined'), receivedAt: '2026-10-23T00:00:01+00:00' }], status(null, ['q-t1', 'answered']), 'Not here.');
      show(declined, controlsWith('t1'));

      expect(writersAndStates()).toEqual(['You:Received', 'Claude:Declined']);
   });

   it.each([
      ['chat bubbles by default', undefined, 'items-end', 'items-start'],
      ['chat bubbles', 'bubbles', 'items-end', 'items-start'],
      ['full width', 'fullWidth', 'items-stretch', 'items-stretch'],
   ] as const)("lays out the reviewer's and Claude's messages as %s", (_, style, mine, claude) => {
      show(answered, controlsWith('t1'), undefined, style);

      const [question, reply] = [...screen.getByRole('article').querySelectorAll('[data-writer]')];
      expect([question?.classList.contains(mine), reply?.classList.contains(claude)]).toEqual([true, true]);
   });

   it("shows an outdated thread's state with both a reopen and a resolve", async () => {
      const { onsetstate, item } = show({ ...answered, state: 'outdated' }, null);

      expect(item.getByText('Outdated')).toBeTruthy();
      await fireEvent.click(item.getByRole('button', { name: 'Resolve' }));
      await fireEvent.click(item.getByRole('button', { name: 'Reopen' }));

      await waitFor(() =>
         expect(onsetstate.mock.calls).toEqual([
            ['t1', 'resolved'],
            ['t1', 'open'],
         ]),
      );
   });

   it('shows a waiting question with the waiting indicator, and cancel and send again while the controls offer them', async () => {
      const { onresend, oncancel, item } = show(pending, controlsWith('t1'));

      expect(item.getByText(/Waiting for Claude's reply/)).toBeTruthy();
      expect(document.querySelector('[data-waiting] svg')?.getAttribute('class')).toMatch(/animate-spin/);
      const cancel = item.getByRole('button', { name: 'Cancel' }) as HTMLButtonElement;
      const resend = item.getByRole('button', { name: 'Send again' }) as HTMLButtonElement;
      expect([cancel.disabled, resend.disabled]).toEqual([false, false]);

      await fireEvent.click(cancel);
      await waitFor(() => expect(oncancel).toHaveBeenCalledWith('q-t1'));
      await fireEvent.click(resend);
      await waitFor(() => expect(onresend).toHaveBeenCalledWith('q-t1'));
   });

   it("lets the reviewer cancel a waiting question while the controls close send again, and shows send again disabled with the server's reason", () => {
      const { item } = show(pending, controlsWith('t1', { resend: noSession }));

      expect((item.getByRole('button', { name: 'Cancel' }) as HTMLButtonElement).disabled).toBe(false);
      const resend = item.getByRole('button', { name: 'Send again' }) as HTMLButtonElement;
      expect(resend.disabled).toBe(true);
      expect(item.getByText(noSessionShown)).toBeTruthy();
   });

   it.each([
      ['canceled', canceled, /Canceled\. Claude may still answer it\./],
      ['unanswered', unanswered, /Unanswered: the session detached/],
   ] as const)('shows a %s question without the waiting indicator, with send again and no cancel', (_, item, line) => {
      const { item: shownItem } = show(item, controlsWith('t1'));

      expect(shownItem.getByText(line)).toBeTruthy();
      expect(shownItem.queryByText(/Waiting for Claude's reply/)).toBeNull();
      expect(shownItem.queryByRole('button', { name: 'Cancel' })).toBeNull();
      expect(shownItem.getByRole('button', { name: 'Send again' })).toBeTruthy();
   });

   it('shows an answered question with its answer and neither cancel nor send again', () => {
      const { item } = show(thread('t1', [request('r1', 'done'), request('r2', 'canceled')], status(null, ['q-t1', 'answered']), 'Yes.'), controlsWith('t1'));

      expect(item.getByText('Yes.')).toBeTruthy();
      expect(item.queryByText(/Waiting for Claude's reply|Canceled/)).toBeNull();
      expect(item.queryByRole('button', { name: /Cancel|Send again/ })).toBeNull();
   });

   it('shows the reason when the server refuses the cancel', async () => {
      const oncancel = vi.fn(async () => "questionId: question 'q-t1' already has its answer.");
      const writes = { onresend: async () => null, oncancel, onfollowup: async () => null, onsetstate: async () => null };
      render(OpenThreadHost, { props: { actions: actionsFor(pending, writes), thread: pending, controls: controlsWith('t1'), ...itemProps, drafts: new ThreadDrafts() } });

      await fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

      expect((await screen.findByRole('alert')).textContent).toContain('already has its answer');
   });

   it.each([
      ['answered', answered],
      ['needs an answer', needsAnswer],
   ] as const)('sends a follow-up typed in an %s thread with its thread ID, and clears the box', async (_, item) => {
      const { onfollowup, item: shownItem } = show(item, controlsWith('t1'));

      await fireEvent.input(followUpBox(shownItem), { target: { value: 'And the parser?' } });
      await fireEvent.click(shownItem.getByRole('button', { name: 'Send the follow-up' }));

      await waitFor(() => expect(onfollowup).toHaveBeenCalledWith('t1', 'And the parser?'));
      await waitFor(() => expect(followUpBox(shownItem).value).toBe(''));
   });

   it('shows a waiting follow-up below the earlier messages, with cancel and send again for the follow-up, and no follow-up box', async () => {
      const { onresend, oncancel, item } = show(followUpPending, controlsWith('t1'));

      const texts = [...document.querySelectorAll('[data-question], [data-reply], [data-waiting]')].map((element) => element.textContent?.trim());
      expect(texts).toEqual(['Question t1', 'Yes.', 'Follow-up t1', expect.stringMatching(/Waiting for Claude's reply/)]);
      expect(item.queryByRole('textbox', { name: 'Follow-up for Claude' })).toBeNull();

      await fireEvent.click(item.getByRole('button', { name: 'Cancel' }));
      await waitFor(() => expect(oncancel).toHaveBeenCalledWith('f-t1'));
      await fireEvent.click(item.getByRole('button', { name: 'Send again' }));
      await waitFor(() => expect(onresend).toHaveBeenCalledWith('f-t1'));
   });

   it("shows the reply to a follow-up below it, and names the reviewer as the follow-up's author", () => {
      const { item } = show(followUpAnswered, controlsWith('t1'));

      const texts = [...document.querySelectorAll('[data-question], [data-reply]')].map((element) => element.textContent?.trim());
      expect(texts).toEqual(['Question t1', 'Yes.', 'Follow-up t1', 'It leaves out the tests.']);
      expect(writersAndStates()).toEqual(['You:Sent', 'Claude:Done', 'You:Sent', 'Claude:Done']);
      expect(item.queryByText(/Waiting for Claude's reply/)).toBeNull();
   });

   it('says which question a late reply answers when a follow-up stands between them', () => {
      const followed = withFollowUp(canceled, [request('f1', 'pending')], status(['f-t1', 'f1'], ['q-t1', 'answered'], ['f-t1', 'waiting']));
      const late: AskedThread = {
         ...followed,
         messages: [
            { ...canceled.messages[0], requests: [request('r1', 'done')] },
            ...followed.messages.slice(1),
            { kind: 'reply', id: 'late', createdAt: '2026-10-23T00:00:02+00:00', requestId: 'r1', sessionId: 'session-1', text: 'Late.', html: '<p>Late.</p>' },
         ],
      };
      const { item } = show(late, controlsWith('t1'));

      expect(item.getByText(/Answers “Question t1”/)).toBeTruthy();
      expect(item.getByText(/Waiting for Claude's reply/)).toBeTruthy();
   });

   it.each([
      ['canceled', canceled, 'canceled', /^Canceled\. Claude may still answer it\.$/],
      ['unanswered', unanswered, 'unanswered', /^Unanswered: the session detached/],
   ] as const)('keeps the state line of a %s question below it once the reviewer follows up', (_, item, outcome, line) => {
      const followed = withFollowUp(item, [request('f1', 'done')], status(null, ['q-t1', outcome], ['f-t1', 'answered']), 'It leaves out the tests.');
      const { item: shownItem } = show(followed, controlsWith('t1'));

      const texts = [...screen.getByRole('article').querySelectorAll('[data-messages] p:not([data-meta]), [data-reply]')].map((element) => element.textContent?.trim());
      expect(texts.slice(0, 3)).toEqual(['Question t1', expect.stringMatching(line), 'Follow-up t1']);
      expect(shownItem.queryByRole('button', { name: 'Send again' })).toBeNull();
   });

   it('shows no state line for an earlier question that a late reply answered', () => {
      const lateAnswered: AskedThread = { ...canceled, messages: [{ ...canceled.messages[0], requests: [request('r1', 'done')] }] };
      const { item } = show(withFollowUp(lateAnswered, [request('f1', 'pending')], status(['f-t1', 'f1'], ['q-t1', 'answered'], ['f-t1', 'waiting'])), controlsWith('t1'));

      expect(item.queryByText(/Canceled|Unanswered/)).toBeNull();
   });

   it("disables the follow-up box with the server's reason while the controls close the follow-up", () => {
      const { item } = show(answered, controlsWith('t1', { followUp: noSession }));

      expect(followUpBox(item).disabled).toBe(true);
      expect((item.getByRole('button', { name: 'Send the follow-up' }) as HTMLButtonElement).disabled).toBe(true);
      expect(item.getByText(noSessionShown)).toBeTruthy();
   });

   it('shows the reason when the server refuses the follow-up, and keeps the words', async () => {
      const { item } = show(answered, controlsWith('t1'), async () => "threadId: question 'q-t1' of thread 't1' still waits for its answer.");

      await fireEvent.input(followUpBox(item), { target: { value: 'And the parser?' } });
      await fireEvent.click(item.getByRole('button', { name: 'Send the follow-up' }));

      expect((await item.findByRole('alert')).textContent).toContain('still waits for its answer');
      expect(followUpBox(item).value).toBe('And the parser?');
   });
});

describe('thread markers', () => {
   const at = (id: string, anchor: ThreadAnchor, text = `Question ${id}`): AskedThread => {
      const base = thread(id, [request(`r-${id}`, 'pending')], status([`q-${id}`, `r-${id}`], [`q-${id}`, 'waiting']));
      return { ...base, anchor, messages: [{ ...base.messages[0], text }] };
   };
   const threads = [
      at('code', { kind: 'code', side: 'base', path: 'src/old.ts', startLine: 2, endLine: 3, commit: 'a'.repeat(40), text: 'x' }),
      at('file', { kind: 'file', side: 'base', path: 'src/old.ts', commit: 'a'.repeat(40) }),
      at('other', { kind: 'file', side: 'head', path: 'src/other.ts', commit: 'b'.repeat(40) }),
      at('stop', { kind: 'stop', stopId: 's1' }),
      at('stopText', { kind: 'stopText', stopId: 's1', field: 'checks[chk-1].question', text: 'the part', version: 2 }),
      at('earlier', { kind: 'stopText', stopId: 's1', field: 'how', text: 'the old part', version: 1 }),
      at('tour', { kind: 'tour' }),
   ];

   it('label a note with its words, at the stop the reviewer viewed while writing it', () => {
      const note: NoteThread = {
         kind: 'note',
         id: 'n1',
         anchor: { kind: 'tour' },
         state: 'open',
         status: status(null),
         text: 'Check the rounding.',
         savedAt: '2026-10-28T10:00:00+00:00',
         stopId: 'c-0',
      };

      expect(threadMarker(note).label).toBe('Check the rounding.');
      expect(threadStopId(note)).toBe('c-0');
   });

   it('label a thread with its question on one line, shortened with an ellipsis', () => {
      expect(threadMarker(at('a', { kind: 'tour' }, '  Why\n\tthis?  ')).label).toBe('Why this?');
      expect(threadMarker(at('a', { kind: 'tour' }, `${'x'.repeat(60)}`)).label).toBe(`${'x'.repeat(47)}…`);
   });

   it('find each thread at its anchor: lines of code, a file at the paths of its sides, a stop and text in one version of a stop', () => {
      expect(codeThreads(threads)).toEqual([{ id: 'code', label: 'Question code', side: 'base', path: 'src/old.ts', startLine: 2, endLine: 3 }]);
      expect(fileThreads(threads, { base: 'src/old.ts', head: 'src/new.ts' })).toEqual([{ id: 'file', label: 'Question file' }]);
      expect(fileThreads(threads, { base: null, head: 'src/old.ts' })).toEqual([]);
      expect(stopThreads(threads, 's1')).toEqual([{ id: 'stop', label: 'Question stop' }]);
      expect(stopTextThreads(threads, 's1', 2)).toEqual([{ id: 'stopText', label: 'Question stopText', field: 'checks[chk-1].question' }]);
      expect(stopTextThreads(threads, 's1', 1)).toEqual([{ id: 'earlier', label: 'Question earlier', field: 'how' }]);
      expect(stopTextThreads(threads, 's2', 2)).toEqual([]);
   });
});
