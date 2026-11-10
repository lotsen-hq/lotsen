import { createContext } from 'svelte';
import { refusalReason, UnauthorizedError, type Api, type Revised } from './api';
import type { ThreadsView } from './threadsView.svelte';
import type { TourRecord } from './tourRecord.svelte';
import type { AnchorInput, QuestionThread, ReviewerThreadState, TourActionInput } from './types';

/** The operations that a tour's thread writes call. */
export type ThreadOperations = Pick<
   Api,
   'askQuestion' | 'requestTourAction' | 'askFollowUp' | 'resendQuestion' | 'cancelQuestion' | 'setThreadState' | 'removeThread' | 'saveNote' | 'editNote' | 'discardNote' | 'sendNotes'
>;

/** How a thread write ended: stored, or refused with the reason to show. */
export type WriteResult = { ok: true } | { ok: false; refusal: string };

/** What a tour's thread writes need from the page beside the tour's record and threads view. */
export interface ThreadPage {
   /** Returns the page's operations. It throws while the page has no session. */
   operations(): ThreadOperations;
   /** Moves the tour's update time in the tour list to that of a write's response at the revision. */
   touch(revision: number, updatedAt: string): void;
   /** Signs the page out, because the server refused the UI credential. */
   signOut(): void;
}

/** What a write's response brings: the threads to store and the thread to remove at the response's revision, and the tour's update time when the output carries one. */
interface Written {
   stored: readonly QuestionThread[];
   removed?: string;
   updatedAt?: string;
}

/**
 * Writes one tour's threads: asks, follow-ups, re-sends, cancels, state changes, deletes and the note writes.
 *
 * Each write calls its operation and stores the response in the tour record at the response's revision, so the record's rule orders it with the events and loads in flight. Every write
 * but a state change, a delete, a note edit and a discard then reveals the threads it stored in the threads view, as {@link ThreadsView.revealAfterWrite} says.
 */
export class ThreadActions {
   private readonly tourId: string;
   private readonly page: ThreadPage;
   private readonly record: TourRecord;
   private readonly view: ThreadsView;

   constructor(tourId: string, page: ThreadPage, record: TourRecord, view: ThreadsView) {
      this.tourId = tourId;
      this.page = page;
      this.record = record;
      this.view = view;
   }

   /**
    * Asks the tour's bound session a question about the anchor.
    *
    * @param stopId - The stop under view, which the question names as the one the reviewer viewed, or null when none is open.
    */
   ask(anchor: AnchorInput, text: string, stopId: string | null): Promise<WriteResult> {
      return this.sendQuestion((operations) => operations.askQuestion(this.tourId, { text, anchor, ...viewed(stopId) }), 'The question could not be sent.');
   }

   /** Requests a tour action of the tour's bound session. */
   requestAction(input: TourActionInput): Promise<WriteResult> {
      return this.sendQuestion((operations) => operations.requestTourAction(this.tourId, input), 'The request could not be sent.');
   }

   /**
    * Adds a follow-up to a thread and sends it to the tour's bound session.
    *
    * @param stopId - The stop under view, which the follow-up names as the one the reviewer viewed, or null when none is open.
    */
   followUp(threadId: string, text: string, stopId: string | null): Promise<WriteResult> {
      return this.sendQuestion((operations) => operations.askFollowUp(this.tourId, threadId, { text, ...viewed(stopId) }), 'The follow-up could not be sent.');
   }

   /** Sends a question that has no answer yet again to the tour's bound session. */
   resend(questionId: string): Promise<WriteResult> {
      return this.sendQuestion((operations) => operations.resendQuestion(this.tourId, questionId), 'The question could not be sent again.');
   }

   /** Stops a question waiting for its answer. Nothing reaches Claude, which may still answer it. */
   cancel(questionId: string): Promise<WriteResult> {
      return this.sendQuestion((operations) => operations.cancelQuestion(this.tourId, questionId), 'The question could not be canceled.');
   }

   /** Resolves a thread or reopens it. It works with or without a bound session, and nothing reaches Claude. */
   setState(threadId: string, state: ReviewerThreadState): Promise<WriteResult> {
      const failure = state === 'resolved' ? 'The thread could not be resolved.' : 'The thread could not be reopened.';
      return this.write(
         (operations) => operations.setThreadState(this.tourId, threadId, state),
         ({ thread, updatedAt }) => ({ stored: [thread], updatedAt }),
         failure,
         false,
      );
   }

   /**
    * Deletes a thread for good. It works with or without a bound session. The server cancels a question of the thread that waits first, and tells the bound session of the delete. The removal
    * leaves a tombstone, as {@link discardNote} does.
    */
   deleteThread(threadId: string): Promise<WriteResult> {
      return this.write(
         (operations) => operations.removeThread(this.tourId, threadId),
         (deleted) => ({ stored: [], removed: deleted.threadId, updatedAt: deleted.updatedAt }),
         'The thread could not be deleted.',
         false,
      );
   }

   /**
    * Saves the reviewer's words as a note about the anchor. It works with or without a bound session, and nothing reaches Claude until the reviewer sends the note.
    *
    * @param stopId - The stop under view, which the note names as the one the reviewer viewed, or null when none is open.
    */
   saveNote(anchor: AnchorInput, text: string, stopId: string | null): Promise<WriteResult> {
      return this.write(
         (operations) => operations.saveNote(this.tourId, { text, anchor, ...viewed(stopId) }),
         ({ thread, updatedAt }) => ({ stored: [thread], updatedAt }),
         'The note could not be saved.',
         true,
      );
   }

   /** Replaces the words of a note that is not sent. It works with or without a bound session. */
   editNote(threadId: string, text: string): Promise<WriteResult> {
      return this.write(
         (operations) => operations.editNote(this.tourId, threadId, text),
         ({ thread, updatedAt }) => ({ stored: [thread], updatedAt }),
         'The note could not be saved.',
         false,
      );
   }

   /** Removes a note that is not sent, with its thread. The removal leaves a tombstone, so an older event or load in flight does not bring the note back. */
   discardNote(threadId: string): Promise<WriteResult> {
      return this.write(
         (operations) => operations.discardNote(this.tourId, threadId),
         (deleted) => ({ stored: [], removed: deleted.threadId, updatedAt: deleted.updatedAt }),
         'The note could not be discarded.',
         false,
      );
   }

   /** Sends notes, by their threads' IDs, to the tour's bound session, each as its own question in its own thread. The server sends all of them or none. */
   sendNotes(threadIds: readonly string[]): Promise<WriteResult> {
      const failure = threadIds.length === 1 ? 'The note could not be sent.' : 'The notes could not be sent.';
      return this.write(
         (operations) => operations.sendNotes(this.tourId, threadIds),
         ({ threads, updatedAt }) => ({ stored: threads, updatedAt }),
         failure,
         true,
      );
   }

   // A question's thread shows once its response lands, so the reviewer sees it wait and keeps its Cancel and Send again, also when a follow-up or a re-send reopens a resolved thread.
   private sendQuestion(send: (operations: ThreadOperations) => Promise<Revised<QuestionThread>>, failure: string): Promise<WriteResult> {
      return this.write(send, (thread) => ({ stored: [thread] }), failure, true);
   }

   private async write<T>(send: (operations: ThreadOperations) => Promise<Revised<T>>, written: (output: T) => Written, failure: string, reveals: boolean): Promise<WriteResult> {
      const reveal = this.view.revealAfterWrite();
      let response: Revised<T>;
      try {
         response = await send(this.page.operations());
      } catch (error) {
         if (error instanceof UnauthorizedError) {
            this.page.signOut();
         }

         return { ok: false, refusal: refusalReason(error, failure) };
      }

      const { revision } = response;
      const { stored, removed, updatedAt } = written(response.output);
      for (const thread of stored) {
         this.record.putThread(thread, revision);
      }

      if (removed !== undefined) {
         this.record.removeThread(removed, revision);
      }

      if (updatedAt !== undefined) {
         this.page.touch(revision, updatedAt);
      }

      if (reveals) {
         for (const thread of stored) {
            reveal(thread);
         }
      }

      return { ok: true };
   }
}

function viewed(stopId: string | null): { stopId?: string } {
   return stopId === null ? {} : { stopId };
}

const [threadActionsContext, provideThreadActions] = createContext<ThreadActions>();

/** Returns the thread actions of the tour that the component shows, which an ancestor gave with {@link setThreadActions}. */
export const getThreadActions: () => ThreadActions = threadActionsContext;

/** Gives the components below the thread actions of the tour that they show. Call it while the component initializes. */
export const setThreadActions: (actions: ThreadActions) => void = provideThreadActions;
