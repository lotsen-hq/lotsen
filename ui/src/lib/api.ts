import type {
   AnswerInput,
   AnswerType,
   ApprovedPlan,
   ChangedFamiliarity,
   ChangedFileList,
   CheckedIteration,
   ClearedAnswer,
   DeletedThread,
   DeletedTour,
   DirectoryListing,
   Familiarity,
   FileDiff,
   FileSide,
   FileText,
   FollowUpInput,
   OperationError,
   OperationName,
   OperationResponseBody,
   Operations,
   PinInput,
   PlanApprovalInput,
   PreferenceList,
   PrunedClones,
   QuestionInput,
   QuestionThread,
   ReadingPlace,
   RecordedProgress,
   ReviewerThreadState,
   ReviewSummary,
   SentNotes,
   Tour,
   TourActionInput,
   TourControls,
   TourList,
   UpdatedAnswer,
   UpdatedBatch,
   UpdatedDoneVersion,
   UpdatedIteration,
   UpdatedPreference,
   UpdatedSkips,
   UpdatedThread,
   UpdatedTourState,
} from './types';

/** The server refused the UI credential, so the page needs a new link from `lotsen open`. */
export class UnauthorizedError extends Error {
   constructor() {
      super('The server refused the UI credential.');
      this.name = 'UnauthorizedError';
   }
}

/** The server answered a request with an error status other than 401. */
export class ApiError extends Error {
   /**
    * @param status The HTTP status.
    * @param error The refusal that the body carries, or null when the body is not one.
    */
   constructor(
      readonly status: number,
      readonly error: OperationError | null = null,
   ) {
      super(error ? `The server answered ${status}: ${error.reason}` : `The server answered ${status}.`);
      this.name = 'ApiError';
   }
}

/** Returns the server's reason for a refused call, or `failure` for any other error. */
export function refusalReason(error: unknown, failure: string): string {
   return error instanceof ApiError && error.error !== null ? error.error.reason : failure;
}

/** The lotsen operations that the UI calls, each with the UI credential. */
export interface Api {
   listTours(): Promise<TourList>;
   /** @throws ApiError with status 404 when no tour has the ID. */
   readTour(tourId: string): Promise<Tour>;
   /**
    * Returns the tour's review summary, with each question and scenario as HTML. It works with or without a bound session.
    *
    * @throws ApiError with status 404 when no tour has the ID.
    */
   readSummary(tourId: string): Promise<ReviewSummary>;
   /**
    * @throws ApiError with status 409 and code `tourNotPinned`, carrying the pin, when the tour is not pinned, and with status 404 when no tour has the ID.
    */
   listChangedFiles(tourId: string): Promise<ChangedFileList>;
   /**
    * Returns the entries of one directory at one side, or of the root directory when the path is null.
    *
    * @throws ApiError as {@link Api.listChangedFiles} does, and with code `fileNotFound` when the side has no such directory.
    */
   listDirectory(tourId: string, side: FileSide, path: string | null): Promise<DirectoryListing>;
   /**
    * Returns a file's text at one side, or a large file's text only when `includeLarge` is true.
    *
    * @throws ApiError as {@link Api.listChangedFiles} does, with code `fileNotFound` when the side lacks it, and with code `invalidInput` when an included file is above the most the server returns.
    */
   readFileText(tourId: string, side: FileSide, path: string, includeLarge?: boolean): Promise<FileText>;
   /**
    * Returns a file's diff.
    *
    * @throws ApiError as {@link Api.listChangedFiles} does, with code `fileNotFound` when no side has the file, or across a rename when a side lacks it, and with code `invalidInput` when an
    * included file is above the most the server returns.
    */
   diffFile(tourId: string, path: string, options?: DiffOptions): Promise<FileDiff>;
   /**
    * Sets the reviewer's review mark on a changed file, with the file's head object ID from the list, or null for a deleted file.
    *
    * @throws ApiError with status 404 when no tour has the ID.
    */
   markFileReviewed(tourId: string, path: string, headObjectId: string | null): Promise<void>;
   /**
    * Clears the reviewer's review mark on a changed file.
    *
    * @throws ApiError with status 404 when no tour has the ID.
    */
   clearFileReviewed(tourId: string, path: string): Promise<void>;
   /**
    * Skips a stop of the tour's plan, or takes its skip back, and returns every skipped stop with the tour's update time. It works with or without a bound session.
    *
    * @throws ApiError with status 404 and code `stopNotFound` when the plan has no such stop.
    */
   setStopSkipped(tourId: string, stopId: string, skipped: boolean): Promise<Revised<UpdatedSkips>>;
   /**
    * Marks a version of a stop with content done, or takes the done mark back for `null`, and returns the stop's done mark with the tour's update time. It works with or without a bound session.
    *
    * @throws ApiError with status 404 and code `stopNotFound` when the plan has no such stop, or with status 400 when the stop has no content or no such version.
    */
   setStopDone(tourId: string, stopId: string, version: number | null): Promise<Revised<UpdatedDoneVersion>>;
   /**
    * Records the stop that the reviewer opened, or the summary page for `'summary'`, as the tour's reading progress, and returns the stored progress with the tour's state. It works with or
    * without a bound session.
    *
    * @throws ApiError with status 404 and code `stopNotFound` when the plan has no such stop.
    */
   recordProgress(tourId: string, place: ReadingPlace): Promise<Revised<RecordedProgress>>;
   /**
    * Archives the tour or unarchives it, and returns the tour's state with its update time. It works with or without a bound session.
    *
    * @throws ApiError with status 404 when no tour has the ID.
    */
   setTourArchived(tourId: string, archived: boolean): Promise<UpdatedTourState>;
   /**
    * Deletes the tour for good, with or without a bound session. Every page receives the `tourDeleted` event.
    *
    * @throws ApiError with status 404 when no tour has the ID.
    */
   deleteTour(tourId: string): Promise<DeletedTour>;
   /** Removes the repository clones that no tour refers to, and the temporary clones that an interrupted clone left, and returns how many it removed. */
   pruneClones(): Promise<PrunedClones>;
   /** Returns every preference that the server stores, each key with its text. */
   readPreferences(): Promise<PreferenceList>;
   /**
    * Stores the preference's text under its key, or deletes the key for null, and returns the key with its stored text. Every page receives the `preferenceChanged` event of a change.
    *
    * @throws ApiError with status 400 for a key or a text that breaks the server's limits, and with status 409 and code `newerPreferences` while a newer lotsen's preferences are stored.
    */
   writePreference(key: string, value: string | null): Promise<UpdatedPreference>;
   /**
    * Changes the tour's familiarity level and returns the new level with the decided thread of the familiarity change that the same write sent to the tour's bound session.
    *
    * @param stopId The stop under view, after which Claude rewrites the stops, or null when none is open.
    * @param text The reviewer's words for Claude, or undefined for a change without words.
    * @throws ApiError as {@link Api.askQuestion} does, with status 400 for the tour's current level, and with status 409 and code `questionWaiting` while an earlier change waits for Claude.
    */
   changeFamiliarity(tourId: string, familiarity: Familiarity, stopId: string | null, text?: string): Promise<Revised<ChangedFamiliarity>>;
   /**
    * Approves the plan of a plan-first tour with the reviewer's order, skips, level and words, which reports the approval to the tour's bound session as a planApproved decision, and returns what the approval stored.
    *
    * @throws ApiError as {@link Api.askQuestion} does, with status 400 when the plan does not wait or the order is not each stop of the plan once, and with status 404 for a skip of a stop that
    * the plan lacks.
    */
   approvePlan(tourId: string, input: PlanApprovalInput): Promise<Revised<ApprovedPlan>>;
   /**
    * Checks the tour's pull request for a new iteration and returns the tour's available iteration after the check, with the reason when the check could not finish.
    *
    * @throws ApiError with status 404 when no tour has the ID, and with status 409 and code `tourNotPinned` when the tour is not pinned.
    */
   checkIteration(tourId: string): Promise<CheckedIteration>;
   /**
    * Updates the tour to its available iteration, which reports the update to the tour's bound session as an iterationUpdated decision when a stop goes stale, and returns the tour's new pin and stale stops.
    *
    * @param pin The pin of the available iteration that the page's notice shows.
    * @throws ApiError with status 409 and code `noSessionAttached` when no session is bound, `iterationNotAvailable` when the tour has no available iteration or another one than `pin` names,
    * `questionWaiting` while the thread of an earlier update waits, and `tourChanged` when the tour kept changing while the update ran, so the same update can succeed when sent again.
    */
   updateIteration(tourId: string, pin: PinInput): Promise<Revised<UpdatedIteration>>;
   /**
    * Asks the tour's bound session a question and returns the new thread.
    *
    * @throws ApiError with status 409 and code `noSessionAttached` when no session could receive the question, and with status 400 when the question or its anchor is invalid.
    */
   askQuestion(tourId: string, input: QuestionInput): Promise<Revised<QuestionThread>>;
   /**
    * Requests a tour action of the tour's bound session and returns the new thread, whose question holds the action.
    *
    * @throws ApiError as {@link Api.askQuestion} does, with status 400 when the target or the tour does not fit the action.
    */
   requestTourAction(tourId: string, input: TourActionInput): Promise<Revised<QuestionThread>>;
   /**
    * Adds a follow-up to a thread, sends it to the tour's bound session, and returns the thread, with each reply's prose as HTML.
    *
    * @throws ApiError as {@link Api.askQuestion} does, with status 404 and code `threadNotFound` when the tour has no such thread, and with status 409 and code `questionWaiting` when the
    * thread's last question waits for its answer.
    */
   askFollowUp(tourId: string, threadId: string, input: FollowUpInput): Promise<Revised<QuestionThread>>;
   /**
    * Sends a question that has no answer yet again to the tour's bound session and returns its thread, with each reply's prose as HTML.
    *
    * @throws ApiError as {@link Api.askQuestion} does, and with status 409 and code `questionAnswered` when a reply answered the question, or `questionFollowedUp` when the question has a
    * follow-up in its thread.
    */
   resendQuestion(tourId: string, questionId: string): Promise<Revised<QuestionThread>>;
   /**
    * Stops a question waiting for its answer, which changes only lotsen's record, and returns its thread, with each reply's prose as HTML.
    *
    * @throws ApiError with status 409 and code `requestNotPending` when the question's last request does not wait, or `questionAnswered` when a reply answered the question.
    */
   cancelQuestion(tourId: string, questionId: string): Promise<Revised<QuestionThread>>;
   /**
    * Resolves a thread or reopens it, which changes only lotsen's record, and returns the thread with the tour's update time, with each reply's prose as HTML.
    *
    * @throws ApiError with status 404 and code `threadNotFound` when the tour has no such thread.
    */
   setThreadState(tourId: string, threadId: string, state: ReviewerThreadState): Promise<Revised<UpdatedThread>>;
   /**
    * Deletes any thread for good, a note included, and returns the removal with the tour's update time. It works with or without a bound session. A question of the thread that waits is
    * canceled first, and the bound session is told of the delete of a thread that it received.
    *
    * @throws ApiError with status 404 and code `threadNotFound` when the tour has no such thread.
    */
   removeThread(tourId: string, threadId: string): Promise<Revised<DeletedThread>>;
   /**
    * Stores the reviewer's words as a note, a thread that reaches no session, and returns the thread with the tour's update time. It works with or without a bound session.
    *
    * @throws ApiError with status 400 when the words or the anchor are invalid.
    */
   saveNote(tourId: string, input: QuestionInput): Promise<Revised<UpdatedThread>>;
   /**
    * Replaces the words of a note that is not sent, and returns the thread with the tour's update time.
    *
    * @throws ApiError with status 409 and code `noteSent` when the thread is not a note any more, and with status 404 and code `threadNotFound` when the tour has no such thread.
    */
   editNote(tourId: string, threadId: string, text: string): Promise<Revised<UpdatedThread>>;
   /**
    * Removes a note that is not sent, with its thread, and returns the removal with the tour's update time.
    *
    * @throws ApiError as {@link Api.editNote} does.
    */
   discardNote(tourId: string, threadId: string): Promise<Revised<DeletedThread>>;
   /**
    * Sends notes, by their threads' IDs, to the tour's bound session, each as its own question, and returns the sent threads with the tour's update time. The server sends all or none.
    *
    * @throws ApiError with status 409 and code `noSessionAttached` when no session is bound, and as {@link Api.editNote} does for a thread that is gone or is not a note any more.
    */
   sendNotes(tourId: string, threadIds: readonly string[]): Promise<Revised<SentNotes>>;
   /**
    * Stores the reviewer's answer to a check of a stop as a draft, in the place of its earlier answer, and returns it with the tour's update time. It works with or without a bound session.
    *
    * @throws ApiError with status 404 and code `stopNotFound` or `checkNotFound` when the plan lacks the stop or the stop's current content lacks the check, and with status 400 when the
    * words are invalid or name no suggestion of the check.
    */
   answerCheck(tourId: string, stopId: string, checkId: string, input: AnswerInput): Promise<Revised<UpdatedAnswer>>;
   /**
    * Sets the type of a check's answer and returns the answer with the tour's update time.
    *
    * @throws ApiError as {@link Api.answerCheck} does, and with status 404 and code `answerNotFound` when the check has no answer.
    */
   setAnswerType(tourId: string, stopId: string, checkId: string, type: AnswerType): Promise<Revised<UpdatedAnswer>>;
   /**
    * Removes a check's answer and returns the removal with the tour's update time.
    *
    * @throws ApiError as {@link Api.setAnswerType} does.
    */
   clearAnswer(tourId: string, stopId: string, checkId: string): Promise<Revised<ClearedAnswer>>;
   /**
    * Sends every answer to send of the tour to its bound session as one batch, and returns the batch's answers with the tour's update time. The server sends all or none.
    *
    * @throws ApiError with status 409 and code `noSessionAttached` when no session is bound, or `noAnswersToSend` when the tour has no answer to send.
    */
   sendAnswers(tourId: string): Promise<Revised<UpdatedBatch>>;
}

/** An operation's output with the revision of the tour that it shows, for an output that the page merges by revision. */
export interface Revised<T> {
   output: T;
   revision: number;
}

/** What a diff request asks for besides the file's path. */
export interface DiffOptions {
   /** The path at the merge base of a renamed file, for its diff across the rename. */
   oldPath?: string;
   /** Whether a large file's diff comes with its texts. */
   includeLarge?: boolean;
   /** Whether the changes leave out lines that differ only in whitespace or line endings. */
   ignoreWhitespace?: boolean;
}

/** The request header with which the UI asks for the prose of each stop and reply as HTML, in `StopContent.html` and a reply's `html`. Without it the server returns the Markdown alone, as Claude reads it. */
export const proseHeader = 'Lotsen-Prose';

/** Returns the path of an operation's requests: every operation is a `POST` there, with its whole input as the JSON body. */
export const operationPath = (name: string): string => `/api/ops/${name}`;

/**
 * Returns the API of the server for the UI credential.
 *
 * @param onControls - Receives the tour's controls that a successful response carries, read after the operation, at their own revision.
 */
export function createApi(fetchFn: typeof fetch, credential: string, onControls: (controls: TourControls) => void = () => {}): Api {
   async function respond<N extends OperationName>(name: N, input: Operations[N]['input']): Promise<{ output: Operations[N]['output']; revision: number | undefined }> {
      const response = await fetchFn(operationPath(name), {
         method: 'POST',
         headers: { Authorization: `Bearer ${credential}`, Accept: 'application/json', 'Content-Type': 'application/json', [proseHeader]: 'html' },
         body: JSON.stringify(input),
         cache: 'no-store',
      });
      if (response.status === 401) {
         throw new UnauthorizedError();
      }

      if (!response.ok) {
         throw new ApiError(response.status, await readError(response));
      }

      // The server and the UI ship as one build, so the output has the shape that the operation declares.
      const body = (await response.json()) as OperationResponseBody;
      if (body.controls !== undefined) {
         onControls(body.controls);
      }

      return { output: body.output as Operations[N]['output'], revision: body.revision };
   }

   async function call<N extends OperationName>(name: N, input: Operations[N]['input']): Promise<Operations[N]['output']> {
      return (await respond(name, input)).output;
   }

   // Every write that the server answers this way stores a tour, so its body carries the revision.
   async function revised<N extends OperationName>(name: N, input: Operations[N]['input']): Promise<Revised<Operations[N]['output']>> {
      const { output, revision } = await respond(name, input);
      if (revision === undefined) {
         throw new Error(`The server answered ${name} without the tour's revision.`);
      }

      return { output, revision };
   }

   // Omits each option without a value, so a request names only what differs from the server's defaults.
   const large = (includeLarge: boolean | undefined) => (includeLarge ? { large: 'include' as const } : {});
   return {
      listTours: () => call('list_tours', {}),
      readTour: (tourId) => call('read_state', { tourId }),
      readSummary: (tourId) => call('read_summary', { tourId }),
      listChangedFiles: (tourId) => call('list_changed_files', { tourId }),
      listDirectory: (tourId, side, path) => call('list_directory', { tourId, side, ...(path === null ? {} : { path }) }),
      readFileText: (tourId, side, path, includeLarge = false) => call('read_file_text', { tourId, side, path, ...large(includeLarge) }),
      diffFile: (tourId, path, { oldPath, includeLarge, ignoreWhitespace } = {}) =>
         call('diff_file', { tourId, path, ...(oldPath === undefined ? {} : { oldPath }), ...large(includeLarge), ...(ignoreWhitespace ? { whitespace: 'ignore' as const } : {}) }),
      markFileReviewed: async (tourId, path, headObjectId) => void (await call('mark_file_reviewed', { tourId, path, headObjectId })),
      clearFileReviewed: async (tourId, path) => void (await call('clear_file_reviewed', { tourId, path })),
      setStopSkipped: (tourId, stopId, skipped) => revised('set_stop_skipped', { tourId, stopId, skipped }),
      setStopDone: (tourId, stopId, version) => revised('set_stop_done', { tourId, stopId, version }),
      recordProgress: (tourId, place) => revised('record_reading_progress', { tourId, ...(place === 'summary' ? { summary: true } : place) }),
      setTourArchived: (tourId, archived) => call('set_tour_archived', { tourId, archived }),
      deleteTour: (tourId) => call('delete_tour', { tourId }),
      pruneClones: () => call('prune_clones', {}),
      readPreferences: () => call('read_preferences', {}),
      writePreference: (key, value) => call('write_preference', { key, value }),
      changeFamiliarity: (tourId, familiarity, stopId, text) => revised('change_familiarity', { tourId, familiarity, ...(stopId === null ? {} : { stopId }), ...(text === undefined ? {} : { text }) }),
      approvePlan: (tourId, input) => revised('approve_plan', { tourId, ...input }),
      checkIteration: (tourId) => call('check_iteration', { tourId }),
      updateIteration: (tourId, pin) => revised('update_iteration', { tourId, pin }),
      askQuestion: (tourId, input) => revised('ask_question', { tourId, ...input }),
      requestTourAction: (tourId, input) => revised('request_tour_action', { tourId, ...input }),
      askFollowUp: (tourId, threadId, input) => revised('ask_follow_up', { tourId, threadId, ...input }),
      resendQuestion: (tourId, questionId) => revised('resend_question', { tourId, questionId }),
      cancelQuestion: (tourId, questionId) => revised('cancel_question', { tourId, questionId }),
      setThreadState: (tourId, threadId, state) => revised('set_thread_state', { tourId, threadId, state }),
      removeThread: (tourId, threadId) => revised('remove_thread', { tourId, threadId }),
      saveNote: (tourId, input) => revised('save_note', { tourId, ...input }),
      editNote: (tourId, threadId, text) => revised('edit_note', { tourId, threadId, text }),
      discardNote: (tourId, threadId) => revised('discard_note', { tourId, threadId }),
      sendNotes: (tourId, threadIds) => revised('send_notes', { tourId, threadIds: [...threadIds] }),
      answerCheck: (tourId, stopId, checkId, input) => revised('answer_check', { tourId, stopId, checkId, ...input }),
      setAnswerType: (tourId, stopId, checkId, type) => revised('set_answer_type', { tourId, stopId, checkId, type }),
      clearAnswer: (tourId, stopId, checkId) => revised('clear_answer', { tourId, stopId, checkId }),
      sendAnswers: (tourId) => revised('send_answers', { tourId }),
   };
}

async function readError(response: Response): Promise<OperationError | null> {
   try {
      const body: unknown = await response.json();
      return typeof body === 'object' && body !== null && 'code' in body && 'reason' in body ? (body as OperationError) : null;
   } catch {
      return null;
   }
}
