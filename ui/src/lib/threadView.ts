import { htmlText } from './dom';
import { shortened } from './labels';
import { atPin, inFile, type FilePaths, type PinCommits } from './pin';
import { type CodeThread } from './passageMarks';
import { subjectSummary } from './tourActions';
import type {
   NoteThread,
   Plan,
   QuestionOutcome,
   QuestionRequest,
   QuestionThread,
   RequestState,
   ReviewerDecision,
   ThreadAgentMessage,
   ThreadMessage,
   ThreadQuestion,
   ThreadReply,
   ThreadState,
   TourAction,
} from './types';

/** A reviewer's question in a thread: the one that starts it, or a follow-up. */
export type Question = ThreadQuestion;

/** What a thread's first question carries beside the reviewer's words: the tour action that the reviewer requests, or the decision that the reviewer reports. */
export type QuestionSubject = { kind: 'action'; action: TourAction } | { kind: 'decision'; decision: ReviewerDecision };

/** A message of Claude's: a reply to one request of a question, or an agent message, which has no `requestId`. */
export type AgentMessage = ThreadReply | ThreadAgentMessage;

/** The most characters that a question's words may hold, as the server's ask and follow-up allow. */
export const maxQuestionLength = 10_000;

/** Returns whether the thread is a note that the reviewer has not sent. */
export function isNote(thread: QuestionThread): thread is NoteThread {
   return thread.kind === 'note';
}

/** Returns the thread's messages in the order they were written, none for a note. */
export function messagesOf(thread: QuestionThread): readonly ThreadMessage[] {
   return thread.kind === 'note' ? [] : thread.messages;
}

/** Returns the question that starts the thread, or null when its first message is not one, as in a note or a thread that Claude started. */
export function questionOf(thread: QuestionThread): Question | null {
   return thread.kind === 'asked' || thread.kind === 'decided' ? thread.messages[0] : null;
}

/** Returns the tour action that the thread's first question requests, or null for a thread that requests none. */
export function actionOf(thread: QuestionThread): TourAction | null {
   return thread.kind === 'asked' ? (thread.action ?? null) : null;
}

/** Returns the reviewer decision that a decided thread reports, or null for any other thread. */
export function decisionOf(thread: QuestionThread): ReviewerDecision | null {
   return thread.kind === 'decided' ? thread.decision : null;
}

/** Returns the tour action or the reviewer decision that the thread's first question carries, or null for a thread with neither. */
export function subjectOf(thread: QuestionThread): QuestionSubject | null {
   const action = actionOf(thread);
   if (action !== null) {
      return { kind: 'action', action };
   }

   const decision = decisionOf(thread);
   return decision === null ? null : { kind: 'decision', decision };
}

/** Returns what a question of the thread carries: the thread's action or decision for its first question, or null for any other question. */
export function subjectOfQuestion(thread: QuestionThread, question: Question): QuestionSubject | null {
   return questionOf(thread) === question ? subjectOf(thread) : null;
}

/** Returns the reviewer's words of a question, or what the thread shows of its action or decision for one without words. */
export function questionWords(question: Question, subject: QuestionSubject | null): string {
   return question.text === '' && subject !== null ? subjectSummary(subject) : question.text;
}

/** Returns the thread's last question, the only one that the server lets wait, or null when the thread has none. */
export function lastQuestion(thread: QuestionThread): Question | null {
   return messagesOf(thread).findLast((message): message is Question => message.kind === 'question') ?? null;
}

/** Returns the last request of the thread's last question, the only request that can be pending, or null when the thread has no question. */
export function lastRequest(thread: QuestionThread): QuestionRequest | null {
   return lastQuestion(thread)?.requests.at(-1) ?? null;
}

/** Returns the thread's question that waits for Claude's reply, as the server's status of the thread names it, or null when none waits. */
export function waitingQuestionOf(thread: QuestionThread): Question | null {
   const waiting = thread.status.waiting;
   return waiting === undefined ? null : (messagesOf(thread).find((message): message is Question => message.kind === 'question' && message.id === waiting.questionId) ?? null);
}

/** Returns whether the server's status of the thread names its last question as answered by a reply. */
export function lastQuestionAnswered(thread: QuestionThread): boolean {
   const last = lastQuestion(thread);
   return last !== null && thread.status.questions.some((question) => question.questionId === last.id && question.outcome === 'answered');
}

/** Returns the question that holds the request, or null when no question of the thread does. */
export function questionOfRequest(thread: QuestionThread, requestId: string): Question | null {
   return messagesOf(thread).find((message): message is Question => message.kind === 'question' && message.requests.some((request) => request.id === requestId)) ?? null;
}

/** A thread as its marker shows it at its anchor: its ID and the start of its question's words. */
export interface ThreadMarker {
   id: string;
   label: string;
}

/** The longest label of a marker, in code points, with the ellipsis. */
const markerLabelLength = 48;

/** Returns a thread's marker, labeled with its words on one line, shortened to {@link markerLabelLength} code points. */
export function threadMarker(thread: QuestionThread): ThreadMarker {
   return { id: thread.id, label: shortened(threadWords(thread), markerLabelLength) };
}

/** Returns the words that a thread starts with: those of its first message, or its note's. */
export function threadWords(thread: QuestionThread): string {
   return (thread.kind === 'note' ? thread.text : shownWords(thread.messages[0], subjectOf(thread))) ?? '';
}

/** Returns the words that the Threads panel shows for a message: Claude's as the text of the server's HTML, which holds no Markdown or link target, and the reviewer's as {@link questionWords} says. */
function shownWords(message: ThreadMessage, subject: QuestionSubject | null): string | undefined {
   if (message.kind === 'question') {
      return questionWords(message, subject);
   }

   return message.html !== undefined ? htmlText(message.html) : message.text;
}

/**
 * Returns the commit that a thread's code or file anchor stays on when that commit is not the pin's commit of its side, as {@link atPin} says, or null when the anchor is at the pin or names no file.
 * An iteration update leaves such an anchor where it was asked, because its lines changed or its file is gone at the new pin.
 */
export function earlierCommitOf(thread: QuestionThread, commits: PinCommits | null): string | null {
   const { anchor } = thread;
   return (anchor.kind === 'code' || anchor.kind === 'file') && !atPin(anchor, commits) ? anchor.commit : null;
}

/** Returns whether the page can open the thread's anchor: a tour anchor names no place, a stop anchor's stop may have left the plan, and lines or a file may stay on an earlier commit. */
export function anchorOpens(thread: QuestionThread, plan: Plan | null, commits: PinCommits | null): boolean {
   const { anchor } = thread;
   if (anchor.kind === 'tour' || earlierCommitOf(thread, commits) !== null) {
      return false;
   }

   return (anchor.kind !== 'stop' && anchor.kind !== 'stopText') || (plan?.stops.some((stop) => stop.id === anchor.stopId) ?? false);
}

/** Returns the label of the control that opens the thread's anchor. */
export function showAnchorLabel(thread: QuestionThread): 'Show the stop' | 'Show in the code' {
   return thread.anchor.kind === 'stop' || thread.anchor.kind === 'stopText' ? 'Show the stop' : 'Show in the code';
}

/** Returns the threads that the diff and the file headings mark: all but those whose anchor stays on an earlier commit, as {@link earlierCommitOf} finds them. */
export function threadsAtPin(threads: readonly QuestionThread[], commits: PinCommits | null): QuestionThread[] {
   return threads.filter((thread) => earlierCommitOf(thread, commits) === null);
}

/** Returns the threads asked about lines of code, for the editors to mark. */
export function codeThreads(threads: readonly QuestionThread[]): CodeThread[] {
   return threads.flatMap(({ anchor, ...thread }) =>
      anchor.kind === 'code' ? [{ ...threadMarker({ anchor, ...thread }), side: anchor.side, path: anchor.path, startLine: anchor.startLine, endLine: anchor.endLine }] : [],
   );
}

/** Returns the markers of the threads asked about a file, at the paths it has at each side, for its heading. */
export function fileThreads(threads: readonly QuestionThread[], paths: FilePaths | null): ThreadMarker[] {
   return threads.flatMap((thread) => (thread.anchor.kind === 'file' && inFile(thread.anchor, paths) ? [threadMarker(thread)] : []));
}

/** Returns the markers of the threads asked about a stop as a whole, for its heading. */
export function stopThreads(threads: readonly QuestionThread[], stopId: string): ThreadMarker[] {
   return threads.flatMap((thread) => (thread.anchor.kind === 'stop' && thread.anchor.stopId === stopId ? [threadMarker(thread)] : []));
}

/** Returns the markers of the threads asked about text in one version of a stop's prose, each with its field, for that version's text. A thread that a revision left on an earlier version has none in a later one. */
export function stopTextThreads(threads: readonly QuestionThread[], stopId: string, version: number | undefined): (ThreadMarker & { field: string })[] {
   return threads.flatMap(({ anchor, ...thread }) =>
      anchor.kind === 'stopText' && anchor.stopId === stopId && anchor.version === version ? [{ ...threadMarker({ anchor, ...thread }), field: anchor.field }] : [],
   );
}

export const threadStateLabels: Record<ThreadState, string> = { open: 'Open', resolved: 'Resolved', outdated: 'Outdated' };

/** Who wrote a message of a thread, as the Threads panel names the writer. */
export type Writer = 'You' | 'Claude';

/** Returns who wrote the message: the reviewer a question, Claude a reply or an agent message. */
export function writerOf(message: ThreadMessage): Writer {
   return message.kind === 'question' ? 'You' : 'Claude';
}

/** Returns when the thread last changed for the thread list: the time of its last message, or of a note's save, as the server wrote it. */
export function lastActivity(thread: QuestionThread): string {
   return thread.kind === 'note' ? thread.savedAt : (thread.messages.at(-1) ?? thread.messages[0]).createdAt;
}

/** The title of a thread in the thread list: its tour action or reviewer decision with the badge that names which, or else the words that the thread starts with. */
export interface ThreadTitle {
   badge: 'Tour action' | 'Your decision' | null;
   text: string;
}

/** Returns the thread's title, as {@link ThreadTitle} says. */
export function threadTitle(thread: QuestionThread): ThreadTitle {
   const subject = subjectOf(thread);
   return subject === null ? { badge: null, text: threadWords(thread) } : { badge: subject.kind === 'action' ? 'Tour action' : 'Your decision', text: subjectSummary(subject) };
}

/** Returns the resolution of the request that a reply answers, done when no question of the thread holds the request. */
export function replyResolution(thread: QuestionThread, reply: ThreadReply): RequestState {
   return questionOfRequest(thread, reply.requestId)?.requests.find((request) => request.id === reply.requestId)?.state ?? 'done';
}

/**
 * Returns whether Claude waits on the reviewer in an open thread: its last message is a reply that needs an answer, or the thread is one that Claude started and no question of the
 * reviewer's follows Claude's messages.
 */
export function awaitsReviewer(thread: QuestionThread): boolean {
   const last = messagesOf(thread).at(-1);
   if (thread.state !== 'open' || last === undefined || last.kind === 'question') {
      return false;
   }

   return last.kind === 'reply' ? replyResolution(thread, last) === 'needsAnswer' : thread.kind === 'started' && lastQuestion(thread) === null;
}

/** The state that a row's marker shows: a question that waits for Claude, a note, Claude waiting on the reviewer, a new reply, or null for none. */
export type ThreadMark = 'waiting' | 'note' | 'yourTurn' | 'newReply' | null;

/**
 * Returns the state that the thread's row marks, the first that holds of waiting, note, Claude waiting on the reviewer and a new reply.
 *
 * @param newReply Whether Claude's last message arrived after the reviewer last viewed the thread, as {@link ThreadsView.isNew} says.
 */
export function threadMark(thread: QuestionThread, newReply: boolean): ThreadMark {
   return thread.status.waiting !== undefined ? 'waiting' : thread.kind === 'note' ? 'note' : awaitsReviewer(thread) ? 'yourTurn' : newReply ? 'newReply' : null;
}

/** What the outcome of a lone question says in its thread's row, while no message follows it. */
const loneQuestionLines: Record<QuestionOutcome, string> = { waiting: 'Sent to Claude', answered: 'Answered', canceled: 'Canceled', unanswered: 'Unanswered' };

/**
 * Returns the last line of the thread's row: the last message on one line after its writer's name.
 *
 * A thread whose one message is also its title shows what became of that message, so the row does not repeat the title. A note shows that it was not sent.
 */
export function lastLine(thread: QuestionThread): string {
   if (thread.kind === 'note') {
      return 'Not sent to Claude';
   }

   const last = thread.messages.at(-1) ?? thread.messages[0];
   if (thread.messages.length > 1) {
      return `${writerOf(last)}: ${(shownWords(last, null) ?? '').replace(/\s+/g, ' ').trim()}`;
   }

   if (last.kind !== 'question') {
      return 'Claude started this thread';
   }

   const outcome = thread.status.questions.find((question) => question.questionId === last.id)?.outcome ?? 'waiting';
   return outcome === 'waiting' && last.requests.at(-1)?.receivedAt !== undefined ? 'Received by Claude' : loneQuestionLines[outcome];
}
