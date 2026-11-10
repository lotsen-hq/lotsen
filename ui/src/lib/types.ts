// The UI's own types over the lotsen API. The server's JSON shapes are generated from its C# types into contract.generated.ts, which this module exports as they are.
import type {
   ActionRequest,
   AnswerCheckInput,
   ApprovePlanInput,
   AskFollowUpInput,
   AskQuestionInput,
   ChangedFile,
   FileDiffLarge,
   NoteThread,
   QuestionThread,
   RequestTourActionInput,
   ReviewerDecision,
   StopPassage,
   TourAction,
} from './contract.generated';

export type * from './contract.generated';

/** The lines of one file at one side that a `ref:` link in prose points at, as its anchor's `data-ref-*` attributes hold them. A passage has the same fields. */
export type CodeReference = Pick<StopPassage, 'side' | 'path' | 'startLine' | 'endLine'>;

/** A place in a tour that the reviewer opens: a stop of the plan, with the number of its current version while that version shows, or the summary page. */
export type ReadingPlace = { stopId: string; version?: number } | 'summary';

/** A thread that reached Claude. */
export type SentThread = Exclude<QuestionThread, NoteThread>;

/** The kind of a tour action. */
export type TourActionKind = TourAction['kind'];

/** The kind of a follow-up action, which hands Claude one group of the review summary. */
export type FollowUpKind = Extract<TourActionKind, 'draftWorkItems' | 'walkOpenQuestions' | 'draftComments'>;

/** The kind of a reviewer decision. */
export type ReviewerDecisionKind = ReviewerDecision['kind'];

/** A tour action that the reviewer requests with `request_tour_action`. */
export type RequestedTourAction = ActionRequest;

/** What the server tells about a changed file's content in the list. */
export type ChangedFileMarks = Pick<ChangedFile, 'binary' | 'large' | 'lineEndingsOnly' | 'headObjectId'>;

/** A large file without its content, which the server withholds until a request includes it. */
export type WithheldLargeFile = Pick<FileDiffLarge, 'content' | 'size'>;

/** The input of `answer_check` besides the IDs of the tour, the stop and the check. */
export type AnswerInput = Omit<AnswerCheckInput, 'tourId' | 'stopId' | 'checkId'>;

/** The input of `ask_question` and `save_note` besides the tour's ID. */
export type QuestionInput = Omit<AskQuestionInput, 'tourId'>;

/** The input of `request_tour_action` besides the tour's ID. */
export type TourActionInput = Omit<RequestTourActionInput, 'tourId'>;

/** The input of `ask_follow_up` besides the IDs of the tour and the thread. */
export type FollowUpInput = Omit<AskFollowUpInput, 'tourId' | 'threadId'>;

/** The input of `approve_plan` besides the tour's ID. */
export type PlanApprovalInput = Omit<ApprovePlanInput, 'tourId'>;
