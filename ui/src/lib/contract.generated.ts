// Generated from contract.schema.json by `just contract`. Do not edit: change the C# types, then run the recipe.

export type TourTarget = PullRequestTarget;
export type Familiarity = 'familiar' | 'some' | 'new';
export type TourPin = Pinning | Pinned | Failed;
export type TourState = 'generating' | 'ready' | 'inProgress' | 'finished' | 'archived';
export type PullRequestState = 'active' | 'completed' | 'abandoned';
export type AttachmentLevel = 'unconfirmed' | 'confirmed';
export type PlanApproval = 'notNeeded' | 'awaited' | 'given';
export type Stop = PendingStop | WrittenStop;
export type StopKind = 'overview' | 'context' | 'inspect' | 'risk' | 'question';
export type StopStatus = 'pending' | 'ready' | 'revised';
export type FileSide = 'base' | 'head';
export type PassageRole = 'primary' | 'related';
export type AnswerType = 'decision' | 'concern' | 'openQuestion';
export type StopPlacement = 'inline' | 'side';
export type FileMarkKind = 'mechanical' | 'generated';
export type AnswerState = 'draft' | 'sent' | 'received';
export type QuestionThread = NoteThread | AskedThread | DecidedThread | StartedThread;
export type ThreadAnchor = CodeRange | StopText | WholeFile | WholeStop | WholeTour;
export type ThreadState = 'open' | 'resolved' | 'outdated';
export type QuestionOutcome = 'waiting' | 'answered' | 'canceled' | 'unanswered';
export type TourAction = ExplainAction | ReviseAction | ReplanAction | ReorderAction | SummarizeAction | DraftWorkItemsAction | WalkOpenQuestionsAction | DraftCommentsAction;
export type RequestState = 'pending' | 'done' | 'needsAnswer' | 'declined' | 'unanswered' | 'canceled';
export type ThreadMessage = ThreadQuestion | ThreadReply | ThreadAgentMessage;
export type ReviewerDecision = FamiliarityChanged | PlanApproved | IterationUpdated;
export type Offer = OpenOffer | ClosedOffer;
export type OperationErrorCode =
   | 'invalidInput'
   | 'tourNotFound'
   | 'stopNotFound'
   | 'tourUnreadable'
   | 'tourNotPinned'
   | 'fileNotFound'
   | 'repositoryUnavailable'
   | 'callerNotAllowed'
   | 'attachedToAnotherSession'
   | 'noSessionAttached'
   | 'questionNotFound'
   | 'threadNotFound'
   | 'requestNotPending'
   | 'questionAnswered'
   | 'questionWaiting'
   | 'questionFollowedUp'
   | 'wrongNonce'
   | 'noteSent'
   | 'threadIsNote'
   | 'checkNotFound'
   | 'answerNotFound'
   | 'batchNotFound'
   | 'planAwaitsApproval'
   | 'noAnswersToSend'
   | 'iterationNotAvailable'
   | 'tourChanged'
   | 'tourRuleBroken'
   | 'newerPreferences'
   | 'operationNotFound'
   | 'buildMismatch'
   | 'serverExiting'
   | 'sessionUnknown';
export type ActionRequest = ExplainRequest | ReviseRequest | ReplanRequest | ReorderRequest | SummarizeRequest | DraftWorkItemsRequest | WalkOpenQuestionsRequest | DraftCommentsRequest;
export type StopTarget = WholeStopTarget | StopTextTarget;
export type AnchorInput = CodeRangeInput | StopTextInput | WholeFileInput | WholeStopInput | WholeTourInput;
export type FileChangeKind = 'added' | 'modified' | 'deleted' | 'renamed';
export type LargeFileContent = 'withhold' | 'include';
export type DiffWhitespace = 'compare' | 'ignore';
export type RepositoryEntryType = 'file' | 'directory' | 'symlink' | 'submodule';
export type EventName =
   | 'hello'
   | 'tourCreated'
   | 'planSet'
   | 'stopPut'
   | 'stopRevised'
   | 'tourPinned'
   | 'availableIterationChanged'
   | 'tourBindingChanged'
   | 'threadCreated'
   | 'threadUpdated'
   | 'threadDeleted'
   | 'answerUpdated'
   | 'answerCleared'
   | 'answerBatchUpdated'
   | 'skipsUpdated'
   | 'seenVersionUpdated'
   | 'doneVersionUpdated'
   | 'familiarityChanged'
   | 'planApproved'
   | 'tourStateChanged'
   | 'synthesisWritten'
   | 'controlsChanged'
   | 'tourDeleted'
   | 'preferenceChanged';
export type FileDiff = TextDiff | FileDiffBinary | FileDiffLarge;
export type FileText = Decoded | FileTextBinary | FileTextLarge;
export type OperationName =
   | 'read_state'
   | 'read_summary'
   | 'ask_question'
   | 'request_tour_action'
   | 'ask_follow_up'
   | 'resend_question'
   | 'cancel_question'
   | 'set_thread_state'
   | 'remove_thread'
   | 'save_note'
   | 'edit_note'
   | 'discard_note'
   | 'send_notes'
   | 'answer_check'
   | 'set_answer_type'
   | 'clear_answer'
   | 'send_answers'
   | 'list_tours'
   | 'list_changed_files'
   | 'read_file_text'
   | 'diff_file'
   | 'list_directory'
   | 'mark_file_reviewed'
   | 'clear_file_reviewed'
   | 'set_stop_skipped'
   | 'set_stop_done'
   | 'record_reading_progress'
   | 'set_tour_archived'
   | 'delete_tour'
   | 'prune_clones'
   | 'change_familiarity'
   | 'approve_plan'
   | 'check_iteration'
   | 'update_iteration'
   | 'read_preferences'
   | 'write_preference';
export type ReviewerThreadState = 'open' | 'resolved';

export interface EventPayloads {
   hello: HelloEvent;
   tourCreated: TourSummary & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   planSet: PlanSetEvent & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   stopPut: StopPutEvent & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   stopRevised: StopRevisedEvent & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   tourPinned: TourPinnedEvent & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   availableIterationChanged: AvailableIterationChangedEvent & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   tourBindingChanged: TourBindingChangedEvent & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   threadCreated: ThreadCreatedEvent & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   threadUpdated: ThreadUpdatedEvent & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   threadDeleted: DeletedThread & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   answerUpdated: UpdatedAnswer & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   answerCleared: ClearedAnswer & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   answerBatchUpdated: UpdatedBatch & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   skipsUpdated: UpdatedSkips & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   seenVersionUpdated: UpdatedSeenVersion & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   doneVersionUpdated: UpdatedDoneVersion & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   familiarityChanged: UpdatedFamiliarity & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   planApproved: UpdatedPlanApproval & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   tourStateChanged: UpdatedTourState & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   synthesisWritten: WrittenSynthesis & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   controlsChanged: TourControls & {
      /**
       * The tour's revision that the event shows.
       */
      revision: number;
   };
   tourDeleted: DeletedTour;
   preferenceChanged: UpdatedPreference;
}
export interface HelloEvent {
   keepAliveMs: number;
   silenceFactor: number;
}
export interface TourSummary {
   id: string;
   target: TourTarget;
   familiarity: Familiarity;
   createdAt: string;
   updatedAt: string;
   pin: TourPin;
   stopCount: number;
   label: string;
   state: TourState;
   pullRequestState: PullRequestState;
   revision: number;
   binding?: TourBinding;
   planApproval?: PlanApproval;
}
export interface PullRequestTarget {
   kind: 'pullRequest';
   platform: string;
   url: string;
}
export interface Pinning {
   state: 'pinning';
}
export interface Pinned {
   state: 'pinned';
   mergeBase: string;
   head: string;
   iteration: number;
   remoteUrl: string;
}
export interface Failed {
   state: 'failed';
   reason: string;
}
export interface TourBinding {
   sessionId: string;
   level: AttachmentLevel;
}
export interface PlanSetEvent {
   tourId: string;
   updatedAt: string;
   plan: Plan;
}
export interface Plan {
   chapters: Chapter[];
   stops: Stop[];
   fileMarks: FileMark[];
   parts?: Part[];
}
export interface Chapter {
   id: string;
   title: string;
   part?: string;
}
export interface PendingStop {
   stage: 'pending';
   anchorHint: AnchorHint | null;
   id: string;
   title: string;
   kind: StopKind;
   chapter: string;
   skipped?: boolean;
   status: StopStatus;
}
export interface AnchorHint {
   path: string;
   line: number | null;
}
export interface WrittenStop {
   stage: 'written';
   content: StopContent;
   version: number;
   earlier: StopVersion[];
   staleOn?: number;
   seenVersion?: number;
   doneVersion?: number;
   id: string;
   title: string;
   kind: StopKind;
   chapter: string;
   skipped?: boolean;
   status: StopStatus;
}
export interface StopContent {
   headline: string;
   passages: StopPassage[];
   how?: string;
   why?: string;
   designChoices?: string;
   pinnedBy?: string;
   checks?: StopCheck[];
   scenario?: string;
   tags?: string[];
   placement?: StopPlacement;
   html: StopHtml;
}
export interface StopPassage {
   side: FileSide;
   path: string;
   startLine: number;
   endLine: number;
   role: PassageRole;
   commit: string;
   text: string;
   label?: string;
}
export interface StopCheck {
   id: string;
   question: string;
   recommendation?: string;
   suggestions?: SuggestedAnswer[];
}
export interface SuggestedAnswer {
   text: string;
   type: AnswerType;
}
export interface StopHtml {
   headline: string;
   how?: string;
   why?: string;
   designChoices?: string;
   pinnedBy?: string;
   checks?: CheckHtml[];
   scenario?: string;
}
export interface CheckHtml {
   question: string;
   recommendation?: string;
}
export interface StopVersion {
   title: string;
   kind: StopKind;
   content: StopContent;
   replacedAt: string;
}
export interface FileMark {
   path: string;
   mark: FileMarkKind;
   reason: string;
}
export interface Part {
   id: string;
   title: string;
}
export interface StopPutEvent {
   tourId: string;
   updatedAt: string;
   stop: Stop;
}
export interface StopRevisedEvent {
   tourId: string;
   updatedAt: string;
   stop: Stop;
   answers: CheckAnswer[];
   threads: QuestionThread[];
}
export interface CheckAnswer {
   stopId: string;
   checkId: string;
   text: string;
   type: AnswerType;
   state: AnswerState;
   savedAt: string;
   batch?: AnswerBatch;
   outdatedOn?: number;
}
export interface AnswerBatch {
   id: string;
   sessionId: string;
   sentAt: string;
   receivedAt?: string;
}
export interface NoteThread {
   kind: 'note';
   text: string;
   savedAt: string;
   stopId?: string;
   id: string;
   anchor: ThreadAnchor;
   state: ThreadState;
   status: ThreadStatus;
}
export interface CodeRange {
   kind: 'code';
   side: FileSide;
   startLine: number;
   endLine: number;
   commit: string;
   text: string;
   path: string;
}
export interface StopText {
   kind: 'stopText';
   stopId: string;
   field: string;
   text: string;
   version: number;
}
export interface WholeFile {
   kind: 'file';
   side: FileSide;
   commit: string;
   path: string;
}
export interface WholeStop {
   kind: 'stop';
   stopId: string;
}
export interface WholeTour {
   kind: 'tour';
}
export interface ThreadStatus {
   waiting?: WaitingQuestion;
   questions: QuestionStatus[];
}
export interface WaitingQuestion {
   questionId: string;
   requestId: string;
   sessionId: string;
}
export interface QuestionStatus {
   questionId: string;
   outcome: QuestionOutcome;
}
export interface AskedThread {
   kind: 'asked';
   action?: TourAction;
   /**
    * @minItems 1
    */
   messages: [ThreadQuestion, ...ThreadMessage[]];
   id: string;
   anchor: ThreadAnchor;
   state: ThreadState;
   status: ThreadStatus;
}
export interface ExplainAction {
   kind: 'explain';
   stopVersion?: number;
}
export interface ReviseAction {
   kind: 'revise';
   stopVersion?: number;
}
export interface ReplanAction {
   kind: 'replan';
}
export interface ReorderAction {
   kind: 'reorder';
   stopIds: string[];
}
export interface SummarizeAction {
   kind: 'summarize';
}
export interface DraftWorkItemsAction {
   kind: 'draftWorkItems';
   decisions: FollowUpAnswer[];
}
export interface FollowUpAnswer {
   stopId: string;
   checkId: string;
   text: string;
   type: AnswerType;
   stopTitle?: string;
   question?: string;
}
export interface WalkOpenQuestionsAction {
   kind: 'walkOpenQuestions';
   openQuestions: FollowUpAnswer[];
}
export interface DraftCommentsAction {
   kind: 'draftComments';
   concerns: FollowUpAnswer[];
   risks: FollowUpRisk[];
}
export interface FollowUpRisk {
   stopId: string;
   title: string;
   scenario: string;
}
export interface ThreadQuestion {
   kind: 'question';
   text: string;
   requests: QuestionRequest[];
   stopId?: string;
   id: string;
   createdAt: string;
}
export interface QuestionRequest {
   id: string;
   state: RequestState;
   sessionId: string;
   sentAt: string;
   receivedAt?: string;
}
export interface ThreadReply {
   kind: 'reply';
   sessionId: string;
   requestId: string;
   text?: string;
   id: string;
   createdAt: string;
   html?: string;
}
export interface ThreadAgentMessage {
   kind: 'agentMessage';
   sessionId: string;
   text: string;
   id: string;
   createdAt: string;
   html: string;
}
export interface DecidedThread {
   kind: 'decided';
   decision: ReviewerDecision;
   /**
    * @minItems 1
    */
   messages: [ThreadQuestion, ...ThreadMessage[]];
   id: string;
   anchor: ThreadAnchor;
   state: ThreadState;
   status: ThreadStatus;
}
export interface FamiliarityChanged {
   kind: 'familiarityChanged';
   from: Familiarity;
   to: Familiarity;
   stopIds: string[];
}
export interface PlanApproved {
   kind: 'planApproved';
   stopIds: string[];
   skippedStops: string[];
   familiarity: Familiarity;
}
export interface IterationUpdated {
   kind: 'iterationUpdated';
   iteration: number;
   stopIds: string[];
}
export interface StartedThread {
   kind: 'started';
   /**
    * @minItems 1
    */
   messages: [ThreadAgentMessage, ...ThreadMessage[]];
   id: string;
   anchor: ThreadAnchor;
   state: ThreadState;
   status: ThreadStatus;
}
export interface TourPinnedEvent {
   tourId: string;
   updatedAt: string;
   pin: TourPin;
}
export interface AvailableIterationChangedEvent {
   tourId: string;
   updatedAt: string;
   availableIteration: AvailableIteration | null;
}
export interface AvailableIteration {
   pin: PinnedCommits;
   staleStops: string[];
   foundAt: string;
}
export interface PinnedCommits {
   mergeBase: string;
   head: string;
   iteration: number;
   remoteUrl: string;
}
export interface TourBindingChangedEvent {
   tourId: string;
   binding: TourBinding | null;
}
export interface ThreadCreatedEvent {
   tourId: string;
   updatedAt: string;
   thread: QuestionThread;
}
export interface ThreadUpdatedEvent {
   tourId: string;
   updatedAt: string;
   thread: QuestionThread;
}
export interface DeletedThread {
   tourId: string;
   updatedAt: string;
   threadId: string;
}
export interface UpdatedAnswer {
   tourId: string;
   updatedAt: string;
   answer: CheckAnswer;
}
export interface ClearedAnswer {
   tourId: string;
   updatedAt: string;
   stopId: string;
   checkId: string;
}
export interface UpdatedBatch {
   tourId: string;
   updatedAt: string;
   batchId: string;
   answers: CheckAnswer[];
}
export interface UpdatedSkips {
   tourId: string;
   updatedAt: string;
   stopIds: string[];
}
export interface UpdatedSeenVersion {
   tourId: string;
   updatedAt: string;
   stopId: string;
   seenVersion: number;
}
export interface UpdatedDoneVersion {
   tourId: string;
   updatedAt: string;
   stopId: string;
   doneVersion?: number;
}
export interface UpdatedFamiliarity {
   tourId: string;
   updatedAt: string;
   familiarity: Familiarity;
}
export interface UpdatedPlanApproval {
   tourId: string;
   updatedAt: string;
   planApproval: PlanApproval;
}
export interface UpdatedTourState {
   tourId: string;
   updatedAt: string;
   state: TourState;
   pullRequestState: PullRequestState;
}
export interface WrittenSynthesis {
   tourId: string;
   updatedAt: string;
   writtenAt: string;
}
export interface TourControls {
   tourId: string;
   revision: number;
   offers: TourOffers;
}
export interface TourOffers {
   ask: Offer;
   sendAnswers: Offer;
   level: LevelOffer;
   approvePlan: Offer;
   updateIteration: Offer;
   replan: Offer;
   reorder: Offer;
   stops: {
      [k: string]: StopOffers | undefined;
   };
   summarize: Offer;
   followUps: {
      [k: string]: Offer | undefined;
   };
   threads: {
      [k: string]: ThreadOffers | undefined;
   };
   pending: PendingWork;
}
export interface OpenOffer {
   state: 'open';
}
export interface ClosedOffer {
   state: 'closed';
   code: OperationErrorCode;
   reason: string;
}
export interface LevelOffer {
   change: Offer;
   askAgain?: RequestState;
}
export interface StopOffers {
   explain: Offer;
   revise: Offer;
}
export interface ThreadOffers {
   resend: Offer;
   followUp: Offer;
   cancel: Offer;
   sendNote: Offer;
}
export interface PendingWork {
   stops: {
      [k: string]: string[] | undefined;
   };
   plan: string[];
   level?: string;
   iteration?: string;
   summary: {
      [k: string]: string | undefined;
   };
}
export interface DeletedTour {
   tourId: string;
}
export interface UpdatedPreference {
   key: string;
   value: string | null;
}
export interface ExplainRequest {
   kind: 'explain';
   /**
    * The stop, or the text selected in a prose field of a stop, that the action is about.
    */
   target: StopTarget;
}
export interface WholeStopTarget {
   kind: 'stop';
   /**
    * The ID of the stop.
    */
   stopId: string;
   /**
    * The stop's version that the reviewer viewed. A version that is not the stop's current one is refused. Omit it for the current version.
    */
   version?: number | null;
}
export interface StopTextTarget {
   kind: 'stopText';
   /**
    * The ID of the stop whose text the reviewer selected.
    */
   stopId: string;
   /**
    * The prose field of the stop's content: headline, how, why, designChoices, pinnedBy, scenario, or checks[<id>].question or checks[<id>].recommendation for the check with that id.
    */
   field: string;
   /**
    * The selected text.
    */
   text: string;
   /**
    * The stop's version that the text was selected in, as the stop's version in the tour. A version that is not the stop's current one is refused. Omit it for the current version.
    */
   version?: number | null;
}
export interface ReviseRequest {
   kind: 'revise';
   /**
    * The stop, or the text selected in a prose field of a stop, that the action is about.
    */
   target: StopTarget;
}
export interface ReplanRequest {
   kind: 'replan';
}
export interface ReorderRequest {
   kind: 'reorder';
   /**
    * The IDs of every stop of the plan, each once, in the order that the reviewer proposes.
    */
   stopIds: string[];
}
export interface SummarizeRequest {
   kind: 'summarize';
}
export interface DraftWorkItemsRequest {
   kind: 'draftWorkItems';
}
export interface WalkOpenQuestionsRequest {
   kind: 'walkOpenQuestions';
}
export interface DraftCommentsRequest {
   kind: 'draftComments';
}
export interface CodeRangeInput {
   kind: 'code';
   /**
    * The side the lines are on: base for the merge base, head for the head.
    */
   side: FileSide;
   /**
    * The file's repository path at that side, relative, with forward slashes.
    */
   path: string;
   /**
    * The first line, from 1.
    */
   startLine: number;
   /**
    * The last line, inclusive, not before startLine.
    */
   endLine: number;
}
export interface StopTextInput {
   kind: 'stopText';
   /**
    * The ID of the stop whose text the reviewer selected.
    */
   stopId: string;
   /**
    * The prose field of the stop's content: headline, how, why, designChoices, pinnedBy, scenario, or checks[<id>].question or checks[<id>].recommendation for the check with that id.
    */
   field: string;
   /**
    * The selected text.
    */
   text: string;
   /**
    * The stop's version that the text was selected in, as the stop's version in the tour. A version that is not the stop's current one is refused. Omit it for the current version.
    */
   version?: number | null;
}
export interface WholeFileInput {
   kind: 'file';
   /**
    * The side the file is on: base for the merge base, head for the head.
    */
   side: FileSide;
   /**
    * The file's repository path at that side, relative, with forward slashes.
    */
   path: string;
}
export interface WholeStopInput {
   kind: 'stop';
   /**
    * The ID of the stop.
    */
   stopId: string;
}
export interface WholeTourInput {
   kind: 'tour';
}
export interface AnswerCheckInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the stop whose content holds the check.
    */
   stopId: string;
   /**
    * The ID of a check of the stop's current content.
    */
   checkId: string;
   /**
    * The answer as plain text, with no control characters but tabs and line breaks.
    */
   text: string;
   /**
    * true when the reviewer picked one of the check's suggested answers, whose text this text repeats exactly. The answer then takes that suggestion's type. Omit it for the reviewer's own words, which keep the type of the check's earlier answer, or start as a decision when the check has no answer.
    */
   fromSuggestion?: boolean;
}
export interface ApprovePlanInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The IDs of every stop of the plan, each once, in the order that the reviewer proposes.
    */
   stopIds: string[];
   /**
    * The IDs of the plan's stops that the reviewer skips, in any order, or an empty list for none. They become the tour's skipped stops.
    */
   skippedStops: string[];
   /**
    * The familiarity level for Claude to write the stops at: familiar, some or new. It becomes the tour's level.
    */
   familiarity: Familiarity;
   /**
    * The reviewer's words for Claude about the plan as plain text, with no control characters but tabs and line breaks. Omit them, or leave them empty, for an approval without words.
    */
   text?: string | null;
}
export interface ApprovedPlan {
   tourId: string;
   updatedAt: string;
   planApproval: PlanApproval;
   familiarity: Familiarity;
   skippedStops: string[];
   thread: QuestionThread;
}
export interface AskFollowUpInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the thread to follow up in.
    */
   threadId: string;
   /**
    * The reviewer's follow-up as plain text, with no control characters but tabs and line breaks.
    */
   text: string;
   /**
    * The ID of the stop that the reviewer views while asking. Omit it when no stop is open.
    */
   stopId?: string | null;
}
export interface AskQuestionInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The reviewer's question as plain text, with no control characters but tabs and line breaks.
    */
   text: string;
   /**
    * Where the reviewer asked: code for whole lines at one side, stopText for text selected in a prose field of a stop, file, stop, or tour.
    */
   anchor: AnchorInput;
   /**
    * The ID of the stop that the reviewer views while asking. Omit it when no stop is open.
    */
   stopId?: string | null;
}
export interface CancelQuestionInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the question that waits for an answer.
    */
   questionId: string;
}
export interface ChangeFamiliarityInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The tour's new familiarity level: familiar, some or new. The tour's current level asks Claude again for the rewrite when the last question in the thread of the latest change to it, the change or a follow-up, was declined, canceled or unanswered.
    */
   familiarity: Familiarity;
   /**
    * The ID of the stop that the reviewer views. Claude rewrites the stops after it. Omit it when no stop is open, and Claude rewrites every stop.
    */
   stopId?: string | null;
   /**
    * The reviewer's words for Claude about the change as plain text, with no control characters but tabs and line breaks. Omit them, or leave them empty, for a change without words.
    */
   text?: string | null;
}
export interface ChangedFamiliarity {
   tourId: string;
   updatedAt: string;
   familiarity: Familiarity;
   thread: QuestionThread;
}
export interface ChangedFile {
   path: string;
   kind: FileChangeKind;
   oldPath?: string;
   binary?: boolean;
   large?: LargeFile;
   lineEndingsOnly?: boolean;
   headObjectId?: string;
}
export interface LargeFile {
   size: number;
}
export interface ChangedFileList {
   mergeBase: string;
   head: string;
   files: ChangedFile[];
   reviewedPaths: string[];
}
export interface CheckIterationInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
}
export interface CheckedIteration {
   tourId: string;
   updatedAt: string;
   availableIteration: AvailableIteration | null;
   failure?: string;
}
export interface ClearAnswerInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the stop whose content holds the check.
    */
   stopId: string;
   /**
    * The ID of a check of the stop's current content.
    */
   checkId: string;
}
export interface ClearFileReviewedInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The changed file's repository path, as list_changed_files names it: a renamed file by its path at the head.
    */
   path: string;
}
export interface Decoded {
   content: 'text';
   text: string;
   path: string;
   side: FileSide;
   commit: string;
}
export interface DeleteTourInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
}
export interface DiffFileInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The file's repository path, relative, with forward slashes.
    */
   path: string;
   /**
    * The file's repository path at the base, for a renamed file: the oldPath that list_changed_files gives it. Then path is its path at the head. Omit it when the path is the same on both sides.
    */
   oldPath?: string | null;
   /**
    * Whether to return the content of a large file: withhold, the default, returns only its size, and include returns its content.
    */
   large?: LargeFileContent;
   /**
    * How the diff compares whitespace: compare, the default, counts every whitespace character, and ignore counts a line that differs from the other side only in whitespace or in its line ending as unchanged. The texts are the same either way.
    */
   whitespace?: DiffWhitespace;
}
export interface DirectoryListing {
   path: string | null;
   side: FileSide;
   commit: string;
   entries: RepositoryEntry[];
}
export interface RepositoryEntry {
   path: string;
   type: RepositoryEntryType;
}
export interface DiscardNoteInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the note's thread.
    */
   threadId: string;
}
export interface EditNoteInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the note's thread.
    */
   threadId: string;
   /**
    * The note's new words as plain text, with no control characters but tabs and line breaks.
    */
   text: string;
}
export interface TextDiff {
   content: 'text';
   baseText: string | null;
   headText: string | null;
   changes: TextChange[];
}
export interface TextChange {
   fromA: number;
   toA: number;
   fromB: number;
   toB: number;
}
export interface FileDiffBinary {
   content: 'binary';
}
export interface FileDiffLarge {
   content: 'large';
   size: number;
}
export interface FileTextBinary {
   content: 'binary';
   path: string;
   side: FileSide;
   commit: string;
}
export interface FileTextLarge {
   content: 'large';
   size: number;
   path: string;
   side: FileSide;
   commit: string;
}
export interface ListChangedFilesInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
}
export interface ListDirectoryInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The side to list the directory at: base for the merge base, head for the head.
    */
   side: FileSide;
   /**
    * The directory's repository path, relative, with forward slashes. Omit it for the repository's root directory.
    */
   path?: string | null;
}
export interface MarkFileReviewedInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The changed file's repository path, as list_changed_files names it: a renamed file by its path at the head.
    */
   path: string;
   /**
    * The file's headObjectId from list_changed_files, or null for a deleted file. The mark counts only while the file at the pinned head has this object ID, or for null, while the file stays deleted.
    */
   headObjectId: string | null;
}
export interface NoInput {}
export interface OperationError {
   code: OperationErrorCode;
   problem: string;
   field: string | null;
   pin?: TourPin;
   reason: string;
}
export interface OperationResponseBody {
   output: unknown;
   revision?: number;
   controls?: TourControls;
}
export interface Operations {
   read_state: {
      input: ReadStateInput;
      output: Tour;
   };
   read_summary: {
      input: ReadSummaryInput;
      output: ReviewSummary;
   };
   ask_question: {
      input: AskQuestionInput;
      output: QuestionThread;
   };
   request_tour_action: {
      input: RequestTourActionInput;
      output: QuestionThread;
   };
   ask_follow_up: {
      input: AskFollowUpInput;
      output: QuestionThread;
   };
   resend_question: {
      input: ResendQuestionInput;
      output: QuestionThread;
   };
   cancel_question: {
      input: CancelQuestionInput;
      output: QuestionThread;
   };
   set_thread_state: {
      input: SetThreadStateInput;
      output: UpdatedThread;
   };
   remove_thread: {
      input: RemoveThreadInput;
      output: DeletedThread;
   };
   save_note: {
      input: AskQuestionInput;
      output: UpdatedThread;
   };
   edit_note: {
      input: EditNoteInput;
      output: UpdatedThread;
   };
   discard_note: {
      input: DiscardNoteInput;
      output: DeletedThread;
   };
   send_notes: {
      input: SendNotesInput;
      output: SentNotes;
   };
   answer_check: {
      input: AnswerCheckInput;
      output: UpdatedAnswer;
   };
   set_answer_type: {
      input: SetAnswerTypeInput;
      output: UpdatedAnswer;
   };
   clear_answer: {
      input: ClearAnswerInput;
      output: ClearedAnswer;
   };
   send_answers: {
      input: SendAnswersInput;
      output: UpdatedBatch;
   };
   list_tours: {
      input: NoInput;
      output: TourList;
   };
   list_changed_files: {
      input: ListChangedFilesInput;
      output: ChangedFileList;
   };
   read_file_text: {
      input: ReadFileTextInput;
      output: FileText;
   };
   diff_file: {
      input: DiffFileInput;
      output: FileDiff;
   };
   list_directory: {
      input: ListDirectoryInput;
      output: DirectoryListing;
   };
   mark_file_reviewed: {
      input: MarkFileReviewedInput;
      output: ReviewMarkList;
   };
   clear_file_reviewed: {
      input: ClearFileReviewedInput;
      output: ReviewMarkList;
   };
   set_stop_skipped: {
      input: SetStopSkippedInput;
      output: UpdatedSkips;
   };
   set_stop_done: {
      input: SetStopDoneInput;
      output: UpdatedDoneVersion;
   };
   record_reading_progress: {
      input: RecordReadingProgressInput;
      output: RecordedProgress;
   };
   set_tour_archived: {
      input: SetTourArchivedInput;
      output: UpdatedTourState;
   };
   delete_tour: {
      input: DeleteTourInput;
      output: DeletedTour;
   };
   prune_clones: {
      input: NoInput;
      output: PrunedClones;
   };
   change_familiarity: {
      input: ChangeFamiliarityInput;
      output: ChangedFamiliarity;
   };
   approve_plan: {
      input: ApprovePlanInput;
      output: ApprovedPlan;
   };
   check_iteration: {
      input: CheckIterationInput;
      output: CheckedIteration;
   };
   update_iteration: {
      input: UpdateIterationInput;
      output: UpdatedIteration;
   };
   read_preferences: {
      input: NoInput;
      output: PreferenceList;
   };
   write_preference: {
      input: WritePreferenceInput;
      output: UpdatedPreference;
   };
}
export interface ReadStateInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
}
export interface Tour {
   id: string;
   target: TourTarget;
   familiarity: Familiarity;
   createdAt: string;
   updatedAt: string;
   pin: TourPin;
   plan: Plan | null;
   threads: QuestionThread[];
   answers: CheckAnswer[];
   planApproval?: PlanApproval;
   availableIteration?: AvailableIteration;
   synthesis?: TourSynthesis;
   progress?: ReadingProgress;
   archived?: boolean;
   pullRequestState?: PullRequestState;
   revision: number;
}
export interface TourSynthesis {
   text: string;
   writtenAt: string;
   pin: PinnedCommits;
   basedOn: string;
}
export interface ReadingProgress {
   finished: boolean;
   lastStop?: string;
}
export interface ReadSummaryInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
}
export interface ReviewSummary {
   tourId: string;
   updatedAt: string;
   answers: SummaryAnswers;
   risks: SummaryRisk[];
   threads: SummaryThreads;
   skippedStops: SummaryStop[];
   staleStops: SummaryStaleStop[];
   synthesis?: SummarySynthesis;
   summaryFingerprint?: string;
   followUps?: SummaryFollowUps;
}
export interface SummaryAnswers {
   decisions: SummaryAnswer[];
   concerns: SummaryAnswer[];
   openQuestions: SummaryAnswer[];
   outdated: SummaryAnswer[];
}
export interface SummaryAnswer {
   stopId: string;
   stopTitle?: string;
   checkId: string;
   question?: string;
   text: string;
   type: AnswerType;
   state: AnswerState;
   outdatedOn?: number;
   questionHtml?: string;
}
export interface SummaryRisk {
   stopId: string;
   title: string;
   scenario?: string;
   scenarioHtml?: string;
}
export interface SummaryThreads {
   open: SummaryThread[];
   resolved: SummaryThread[];
   outdated: SummaryThread[];
}
export interface SummaryThread {
   id: string;
   anchor: ThreadAnchor;
   stopId?: string;
   stopTitle?: string;
   action?: string;
   decision?: string;
   note?: boolean;
}
export interface SummaryStop {
   stopId: string;
   title: string;
}
export interface SummaryStaleStop {
   stopId: string;
   title: string;
   staleOn: number;
}
export interface SummarySynthesis {
   text: string;
   writtenAt: string;
   pin: PinnedCommits;
   predatesSummary: boolean;
   html: string;
}
export interface SummaryFollowUps {
   draftWorkItems?: string;
   walkOpenQuestions?: string;
   draftComments?: string;
}
export interface RequestTourActionInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * What the reviewer asks Claude to do: explain to explain a stop or text selected in it more deeply, or revise to revise the stop, each with its target, replan to change the plan as the words say, reorder to put the plan's stops in the order of stopIds, which names every stop of the plan once, summarize to write a synthesis of the review, or a follow-up action: draftWorkItems to propose work items for the sent decisions, walkOpenQuestions to start a thread on each sent open question, or draftComments to draft comments for the author from the sent concerns and the risk stops that Claude wrote and the reviewer did not skip. Only explain and revise take a target, and every other kind applies to the whole tour. The server collects a follow-up action's items, so the kind is all that a follow-up action holds. A reviewer decision is no tour action: change_familiarity, approve_plan and update_iteration report each.
    */
   action: ActionRequest;
   /**
    * The reviewer's words as plain text, with no control characters but tabs and line breaks. Omit them, or leave them empty, for an action without words. A revise and a replan need them.
    */
   text?: string | null;
   /**
    * The ID of the stop that the reviewer views while asking. Omit it when no stop is open.
    */
   stopId?: string | null;
}
export interface ResendQuestionInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the question that has no answer yet.
    */
   questionId: string;
}
export interface SetThreadStateInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the thread.
    */
   threadId: string;
   /**
    * The thread's new state: resolved when the reviewer considers it settled, open to reopen it.
    */
   state: ReviewerThreadState;
}
export interface UpdatedThread {
   tourId: string;
   updatedAt: string;
   thread: QuestionThread;
}
export interface RemoveThreadInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the thread.
    */
   threadId: string;
}
export interface SendNotesInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The IDs of the notes' threads, each once, in the order to send them.
    */
   threadIds: string[];
}
export interface SentNotes {
   tourId: string;
   updatedAt: string;
   threads: QuestionThread[];
}
export interface SetAnswerTypeInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the stop whose content holds the check.
    */
   stopId: string;
   /**
    * The ID of a check of the stop's current content.
    */
   checkId: string;
   /**
    * The answer's new type: decision, concern or openQuestion.
    */
   type: AnswerType;
}
export interface SendAnswersInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
}
export interface TourList {
   tours: TourSummary[];
}
export interface ReadFileTextInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The side to read the file at: base for the merge base, head for the head.
    */
   side: FileSide;
   /**
    * The file's repository path, relative, with forward slashes.
    */
   path: string;
   /**
    * Whether to return the content of a large file: withhold, the default, returns only its size, and include returns its content.
    */
   large?: LargeFileContent;
}
export interface ReviewMarkList {
   marks: ReviewMark[];
}
export interface ReviewMark {
   path: string;
   headObjectId: string | null;
}
export interface SetStopSkippedInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of a stop of the tour's plan, pending or with content.
    */
   stopId: string;
   /**
    * true to skip the stop, false to take the skip back.
    */
   skipped: boolean;
}
export interface SetStopDoneInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of a stop of the tour's plan that has content.
    */
   stopId: string;
   /**
    * The number of the stop's version that the reviewer read, from 1 up to the stop's current version, or null to take the done mark back.
    */
   version: number | null;
}
export interface RecordReadingProgressInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The ID of the stop of the tour's plan that the reviewer opened. Omit it when the reviewer opened the summary page.
    */
   stopId?: string | null;
   /**
    * true when the reviewer opened the summary page. Omit it when the reviewer opened a stop.
    */
   summary?: boolean;
   /**
    * The number of the stop's current version when the reviewer sees it. Omit it while the page shows an earlier version.
    */
   version?: number | null;
}
export interface RecordedProgress {
   tourId: string;
   updatedAt: string;
   state: TourState;
   pullRequestState: PullRequestState;
   progress: ReadingProgress;
   seenVersion?: number;
}
export interface SetTourArchivedInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * true to archive the tour, false to unarchive it.
    */
   archived: boolean;
}
export interface PrunedClones {
   removed: number;
}
export interface UpdateIterationInput {
   /**
    * The tour's ID, as create_tour or list_tours returned it, or as the user gave it.
    */
   tourId: string;
   /**
    * The pin of the available iteration that the reviewer's notice showed. The update is refused when the tour's available iteration is another one.
    */
   pin: PinInput;
}
export interface PinInput {
   /**
    * The iteration's merge base, as a full commit ID.
    */
   mergeBase: string;
   /**
    * The iteration's head, as a full commit ID.
    */
   head: string;
   /**
    * The pull request's number of the iteration, from 1.
    */
   iteration: number;
   /**
    * The URL of the repository that the commits come from, absolute.
    */
   remoteUrl: string;
}
export interface UpdatedIteration {
   tourId: string;
   updatedAt: string;
   pin: TourPin;
   staleStops: string[];
   thread?: QuestionThread;
}
export interface PreferenceList {
   preferences: {
      [k: string]: string | undefined;
   };
}
export interface WritePreferenceInput {
   /**
    * The preference's key: 1 to 100 letters, digits, '.', '_' or '-', with at most one '/' between two of them.
    */
   key: string;
   /**
    * The preference's new text, of at most 65536 characters, or null to delete the preference.
    */
   value: string | null;
}
