import { contentOf } from './stops';
import type { AnswerInput, AnswerState, AnswerType, CheckAnswer, Offer, Plan } from './types';

/** What a stop's explanation needs to show the answers to the stop's checks, and to write them when the version it shows takes answers. */
export interface CheckAnswers {
   /** The stop's answers by check ID. */
   byCheck: ReadonlyMap<string, CheckAnswer>;
   /** The ID of the session bound to the tour, or null when none is, which tells a sent answer that the next send holds. */
   boundSessionId: string | null;
   /** The writes of the answers, or null when the explanation shows an earlier stop version, which takes no answers. */
   writes: AnswerWrites | null;
   /** The send of the tour's answers that the row under the checks offers, or null when the explanation shows an earlier stop version. */
   send: AnswerSend | null;
}

/** A control that starts a send of the tour's answers: the top bar's, or the row under a stop's checks. */
export type AnswerSendControl = 'topBar' | 'sendRow';

/** The state of one tour's send of its answers, which both send controls share, so a send cannot start twice. */
export interface AnswerSendState {
   /** Whether a send is in flight. */
   sending: boolean;
   /** The reason the last send failed, with the control that started the send, or null when no failure stands. */
   refusal: { control: AnswerSendControl; reason: string } | null;
}

/** The state of a tour's send before any send, or while no tour is selected. */
export const idleAnswerSend: AnswerSendState = { sending: false, refusal: null };

/** What the row under a stop's checks needs to offer the send of the tour's answers. */
export interface AnswerSend {
   /** The number of the tour's answers that the next send holds, the top bar's count. */
   unsent: number;
   /** Whether the tour takes a send of its answers, from its controls, or undefined while the page has none. */
   offer: Offer | undefined;
   state: AnswerSendState;
   /** Sends the tour's answers as one batch. The outcome reaches the row through {@link AnswerSend.state}. */
   onsend: () => Promise<string | null>;
}

/** Returns whether the control announces the standing refusal. Both controls show the reason, and only the control that started the send announces it, so a screen reader hears it once. */
export function announcesRefusal(state: AnswerSendState, control: AnswerSendControl): boolean {
   return state.refusal?.control === control;
}

/** The writes of a stop's answers. Each returns null once the server stored it, or the reason it failed. */
export interface AnswerWrites {
   onanswer: (checkId: string, input: AnswerInput) => Promise<string | null>;
   onsettype: (checkId: string, type: AnswerType) => Promise<string | null>;
   onclear: (checkId: string) => Promise<string | null>;
}

/** The writes of one check's answer. Each returns null once the server stored it, or the reason it failed. */
export interface CheckAnswerWrites {
   onanswer: (input: AnswerInput) => Promise<string | null>;
   onsettype: (type: AnswerType) => Promise<string | null>;
   onclear: () => Promise<string | null>;
}

/** The answer types in the order the type chip offers them, each with its label. */
export const answerTypes: readonly { type: AnswerType; label: string }[] = [
   { type: 'decision', label: 'Decision' },
   { type: 'concern', label: 'Concern' },
   { type: 'openQuestion', label: 'Open question' },
];

/** What each answer state is called where an answer shows, with what it means for the reviewer. */
export const answerStateTexts: Record<AnswerState, { label: string; explanation: string }> = {
   draft: { label: 'Draft', explanation: 'Not sent to Claude yet.' },
   sent: { label: 'Sent', explanation: 'Sent to Claude, which has not read it yet.' },
   received: { label: 'Received', explanation: 'Claude has read it. A change makes it a draft again.' },
};

/** What an outdated answer's state is called where the answer shows, with what it means for the reviewer. */
export const outdatedAnswerText = { label: 'Outdated', explanation: 'A revision dropped this check or gave it a new ID, so this answer is never sent.' };

/** Returns what an answer's state is called where the answer shows: outdated, not received when {@link isUnreceivedElsewhere} says so, or else its stored state. */
export function answerStateText(answer: CheckAnswer, boundSessionId: string | null): { label: string; explanation: string } {
   if (answer.outdatedOn !== undefined) {
      return outdatedAnswerText;
   }

   if (isUnreceivedElsewhere(answer, boundSessionId)) {
      return { label: 'Not received', explanation: 'The Claude session it went to is not attached any more, so the next send holds it again.' };
   }

   return answerStateTexts[answer.state];
}

/** Returns the stop's answers by check ID, or none when the stop ID is null. */
export function stopAnswers(answers: readonly CheckAnswer[], stopId: string | null): Map<string, CheckAnswer> {
   return new Map(answers.flatMap((answer) => (answer.stopId === stopId ? [[answer.checkId, answer]] : [])));
}

/**
 * Returns whether the next send holds the answer, as the server decides it: a draft, or a sent answer whose session is not the bound session, because that session ended or lost the tour
 * before it fetched the answer's batch. It never holds an outdated answer.
 */
export function awaitsSending(answer: CheckAnswer, boundSessionId: string | null): boolean {
   return answer.outdatedOn === undefined && (answer.state === 'draft' || isUnreceivedElsewhere(answer, boundSessionId));
}

/** Returns whether the answer is sent to a session that is not the bound session and that has not fetched it, so the next send holds it again. */
export function isUnreceivedElsewhere(answer: CheckAnswer, boundSessionId: string | null): boolean {
   return answer.state === 'sent' && answer.batch?.sessionId !== boundSessionId;
}

/** Returns the number of answers to the checks that the plan's stops hold now that the next send holds. An outdated answer stays stored, but the next send never holds it. */
export function unsentAnswerCount(answers: readonly CheckAnswer[], plan: Plan | null | undefined, boundSessionId: string | null): number {
   const checks = new Set((plan?.stops ?? []).flatMap((stop) => (contentOf(stop)?.checks ?? []).map((check) => JSON.stringify([stop.id, check.id]))));
   return answers.filter((answer) => awaitsSending(answer, boundSessionId) && checks.has(JSON.stringify([answer.stopId, answer.checkId]))).length;
}
