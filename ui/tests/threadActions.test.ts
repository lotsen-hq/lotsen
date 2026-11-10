import { describe, expect, it, vi } from 'vitest';
import { ApiError, UnauthorizedError, type Revised } from '../src/lib/api';
import { ThreadActions, type ThreadOperations, type ThreadPage, type WriteResult } from '../src/lib/threadActions.svelte';
import { defaultThreadFilter, nowhere, type ThreadFilter } from '../src/lib/threadFilters';
import { ThreadsView } from '../src/lib/threadsView.svelte';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { AnchorInput, OperationError, QuestionThread, Tour } from '../src/lib/types';
import { outputFixtures } from './contract/fixtures.generated';

const tour: Tour = outputFixtures.read_state;
const tourId = tour.id;
const at = tour.revision;
const written = at + 1;
const updatedAt = '2026-10-30T09:05:00+00:00';
const anchor: AnchorInput = { kind: 'stop', stopId: 's3' };

/** The operations, each answering with its golden fixture at the revision after the load's, and every call in the order the actions made them. */
function stubOperations() {
   const calls: [string, unknown[]][] = [];
   const answer =
      <T>(name: string, output: T) =>
      async (...args: unknown[]): Promise<Revised<T>> => {
         calls.push([name, args]);
         return { output: structuredClone(output), revision: written };
      };
   const operations: ThreadOperations = {
      askQuestion: answer('askQuestion', outputFixtures.ask_question),
      requestTourAction: answer('requestTourAction', outputFixtures.request_tour_action),
      askFollowUp: answer('askFollowUp', outputFixtures.ask_follow_up),
      resendQuestion: answer('resendQuestion', outputFixtures.resend_question),
      cancelQuestion: answer('cancelQuestion', outputFixtures.cancel_question),
      setThreadState: answer('setThreadState', outputFixtures.set_thread_state),
      removeThread: answer('removeThread', outputFixtures.remove_thread),
      saveNote: answer('saveNote', outputFixtures.save_note),
      editNote: answer('editNote', outputFixtures.edit_note),
      discardNote: answer('discardNote', outputFixtures.discard_note),
      sendNotes: answer('sendNotes', outputFixtures.send_notes),
   };
   return { operations, calls };
}

function setUp(operations: ThreadOperations = stubOperations().operations) {
   const record = new TourRecord(tourId);
   record.applyLoad(structuredClone(tour));
   const view = new ThreadsView(record, () => nowhere);
   const page = { operations: () => operations, touch: vi.fn<ThreadPage['touch']>(), signOut: vi.fn<ThreadPage['signOut']>() };
   return { record, view, page, actions: new ThreadActions(tourId, page, record, view) };
}

/** One thread write: how to run it, the call it makes, the threads its response stores, and whether it moves the tour's update time and reveals what it stored. */
interface Case {
   name: string;
   run: (actions: ThreadActions) => Promise<WriteResult>;
   call: [string, unknown[]];
   stored: readonly QuestionThread[];
   touches: boolean;
   reveals: boolean;
}

const question = outputFixtures.ask_question;
const cases: Case[] = [
   { name: 'ask', run: (a) => a.ask(anchor, 'Why?', 's2'), call: ['askQuestion', [tourId, { text: 'Why?', anchor, stopId: 's2' }]], stored: [question], touches: false, reveals: true },
   {
      name: 'ask without a stop under view',
      run: (a) => a.ask({ kind: 'tour' }, 'Why?', null),
      call: ['askQuestion', [tourId, { text: 'Why?', anchor: { kind: 'tour' } }]],
      stored: [question],
      touches: false,
      reveals: true,
   },
   {
      name: 'requestAction',
      run: (a) => a.requestAction({ action: { kind: 'replan' }, stopId: 's2' }),
      call: ['requestTourAction', [tourId, { action: { kind: 'replan' }, stopId: 's2' }]],
      stored: [outputFixtures.request_tour_action],
      touches: false,
      reveals: true,
   },
   {
      name: 'followUp',
      run: (a) => a.followUp('t2', 'And?', 's2'),
      call: ['askFollowUp', [tourId, 't2', { text: 'And?', stopId: 's2' }]],
      stored: [outputFixtures.ask_follow_up],
      touches: false,
      reveals: true,
   },
   { name: 'resend', run: (a) => a.resend('q-t2'), call: ['resendQuestion', [tourId, 'q-t2']], stored: [outputFixtures.resend_question], touches: false, reveals: true },
   { name: 'cancel', run: (a) => a.cancel('q-t2'), call: ['cancelQuestion', [tourId, 'q-t2']], stored: [outputFixtures.cancel_question], touches: false, reveals: true },
   {
      name: 'setState',
      run: (a) => a.setState('t1', 'resolved'),
      call: ['setThreadState', [tourId, 't1', 'resolved']],
      stored: [outputFixtures.set_thread_state.thread],
      touches: true,
      reveals: false,
   },
   {
      name: 'saveNote',
      run: (a) => a.saveNote(anchor, 'Mine.', 's2'),
      call: ['saveNote', [tourId, { text: 'Mine.', anchor, stopId: 's2' }]],
      stored: [outputFixtures.save_note.thread],
      touches: true,
      reveals: true,
   },
   { name: 'editNote', run: (a) => a.editNote('t1', 'Mine.'), call: ['editNote', [tourId, 't1', 'Mine.']], stored: [outputFixtures.edit_note.thread], touches: true, reveals: false },
   { name: 'sendNotes', run: (a) => a.sendNotes(['t1']), call: ['sendNotes', [tourId, ['t1']]], stored: outputFixtures.send_notes.threads, touches: true, reveals: true },
];

// A filter that hides every thread of the fixtures' writes while no stop is open, so a reveal shows in the filter.
const hiding: ThreadFilter = { ...defaultThreadFilter, where: { kind: 'currentStop' } };

describe('the thread actions of a tour', () => {
   it.each(cases)('$name calls its operation and stores the response in the record at its revision', async ({ run, call, stored, touches }) => {
      const { operations, calls } = stubOperations();
      const { record, page, actions } = setUp(operations);

      await expect(run(actions)).resolves.toEqual({ ok: true });

      expect(calls).toEqual([call]);
      for (const thread of stored) {
         expect(record.thread(thread.id)).toEqual(thread);
      }

      expect(page.touch.mock.calls).toEqual(touches ? [[written, updatedAt]] : []);
   });

   it.each(cases)('$name reveals what it stored only when it is a new question, a follow-up or a note', async ({ run, reveals }) => {
      const { view, actions } = setUp();
      view.choose(hiding);
      const before = view.filter;

      await run(actions);

      expect(view.filter === before).toBe(!reveals);
   });

   it("discardNote removes the note at the response's revision, so an older event does not bring it back", async () => {
      const { operations, calls } = stubOperations();
      const { record, page, actions } = setUp(operations);
      const note = structuredClone(record.thread('t1')!);

      await expect(actions.discardNote('t1')).resolves.toEqual({ ok: true });
      record.putThread(note, at);

      expect(calls).toEqual([['discardNote', [tourId, 't1']]]);
      expect(record.thread('t1')).toBeUndefined();
      expect(page.touch.mock.calls).toEqual([[written, updatedAt]]);
   });

   it("deleteThread removes the thread at the response's revision, so an older event does not bring it back", async () => {
      const { operations, calls } = stubOperations();
      const { record, page, actions } = setUp(operations);
      const thread = structuredClone(record.thread('t1')!);

      await expect(actions.deleteThread('t1')).resolves.toEqual({ ok: true });
      record.putThread(thread, at);

      expect(calls).toEqual([['removeThread', [tourId, 't1']]]);
      expect(record.thread('t1')).toBeUndefined();
      expect(page.touch.mock.calls).toEqual([[written, updatedAt]]);
   });

   it('keeps a newer event of the thread over a response at an older revision', async () => {
      const { record, actions } = setUp();
      const newer = { ...structuredClone(record.thread('t2')!), state: 'resolved' as const };
      record.putThread(newer, written + 1);

      await actions.resend('q-t2');

      expect(record.thread('t2')).toEqual(newer);
   });

   describe('with a refusal', () => {
      const refusal: OperationError = { code: 'questionWaiting', problem: 'The question waits.', field: null, reason: "The thread's last question waits for its answer." };

      function refusing(error: unknown): ThreadOperations {
         const fail = async (): Promise<never> => {
            throw error;
         };
         return {
            askQuestion: fail,
            requestTourAction: fail,
            askFollowUp: fail,
            resendQuestion: fail,
            cancelQuestion: fail,
            setThreadState: fail,
            removeThread: fail,
            saveNote: fail,
            editNote: fail,
            discardNote: fail,
            sendNotes: fail,
         };
      }

      it.each(cases)("$name returns the server's reason and leaves the record as it was", async ({ run }) => {
         const { record, view, page, actions } = setUp(refusing(new ApiError(409, refusal)));
         const threads = record.threads;
         const filter = view.filter;

         await expect(run(actions)).resolves.toEqual({ ok: false, refusal: refusal.reason });

         expect(record.threads).toBe(threads);
         expect(view.filter).toBe(filter);
         expect(page.touch).not.toHaveBeenCalled();
         expect(page.signOut).not.toHaveBeenCalled();
      });

      it("returns the write's own failure for an error without a reason", async () => {
         const { actions } = setUp(refusing(new Error('The network is down.')));

         await expect(actions.followUp('t2', 'And?', null)).resolves.toEqual({ ok: false, refusal: 'The follow-up could not be sent.' });
         await expect(actions.sendNotes(['t1', 't2'])).resolves.toEqual({ ok: false, refusal: 'The notes could not be sent.' });
         await expect(actions.sendNotes(['t1'])).resolves.toEqual({ ok: false, refusal: 'The note could not be sent.' });
      });

      it('signs the page out when the server refuses the UI credential', async () => {
         const { page, actions } = setUp(refusing(new UnauthorizedError()));

         await expect(actions.discardNote('t1')).resolves.toEqual({ ok: false, refusal: 'The note could not be discarded.' });

         expect(page.signOut).toHaveBeenCalledOnce();
      });
   });
});
