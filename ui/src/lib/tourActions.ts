import { familiarityLabels } from './labels';
import type { QuestionSubject } from './threadView';
import type { FollowUpKind, RequestedTourAction, ReviewerDecision, ReviewerDecisionKind, SummaryThread, TourActionKind } from './types';

/** The kinds of the tour actions on one stop, which the Stop panel offers. */
export type StopActionKind = Extract<TourActionKind, 'explain' | 'revise'>;

/** The kinds of the tour actions on the plan, which the Plan panel offers. */
export type PlanActionKind = Extract<TourActionKind, 'replan' | 'reorder'>;

/**
 * The kinds of the tour actions on the review summary, which the summary page offers. They change no stop, so a waiting one neither holds the stop under view back nor blocks an action on the
 * plan.
 */
export const summaryActionKinds: ReadonlySet<TourActionKind> = new Set<TourActionKind>(['summarize', 'draftWorkItems', 'walkOpenQuestions', 'draftComments']);

/** The kinds of the follow-up actions, in the order that the summary page offers them. */
export const followUpKinds: readonly FollowUpKind[] = ['draftWorkItems', 'walkOpenQuestions', 'draftComments'];

/** The name of each tour action, as its button and its thread show it. */
export const tourActionLabels: Record<TourActionKind, string> = {
   explain: 'Explain deeper',
   revise: 'Revise',
   replan: 'Re-plan',
   reorder: 'Reorder',
   summarize: 'Summarize',
   draftWorkItems: 'Draft work items',
   walkOpenQuestions: 'Walk through open questions',
   draftComments: 'Draft PR comments',
};

/** What the Stop panel or the Plan panel says while a tour action of the stop or of the plan waits for Claude. */
export const tourActionPending: Record<TourActionKind, string> = {
   explain: 'Claude is explaining this stop deeper.',
   revise: 'Claude is revising this stop.',
   replan: 'Claude is re-planning the tour.',
   reorder: 'Claude is reordering the plan.',
   summarize: 'Claude is summarizing the review.',
   draftWorkItems: 'Claude is drafting work items for your decisions. Approve each in the terminal.',
   walkOpenQuestions: 'Claude is starting a thread for each open question.',
   draftComments: 'Claude is drafting PR comments from your concerns.',
};

/** The name of each reviewer decision, as its thread shows it. */
export const decisionLabels: Record<ReviewerDecisionKind, string> = {
   familiarityChanged: 'Change familiarity',
   planApproved: 'Approve plan',
   iterationUpdated: 'Move to a new update',
};

/** Returns the label of a summary thread's tour action or reviewer decision, which the review summary names by its kind, or null for a thread with neither. */
export function summaryThreadKindLabel(entry: Pick<SummaryThread, 'action' | 'decision'>): string | null {
   // The summary holds each kind as a string that names one of the server's own kinds.
   return entry.action !== undefined ? tourActionLabels[entry.action as TourActionKind] : entry.decision !== undefined ? decisionLabels[entry.decision as ReviewerDecisionKind] : null;
}

/** What the Plan panel says while Claude acts on a reviewer decision. */
export const decisionPending: Record<ReviewerDecisionKind, string> = {
   familiarityChanged: 'Claude is rewriting the stops you have not read for the new familiarity level.',
   planApproved: 'Claude is writing the stops of the plan you approved.',
   iterationUpdated: 'Claude is revising the stops that the new update made stale.',
};

/** The label of the box for the reviewer's words of each tour action that the reviewer requests. */
export const tourActionPrompts: Record<RequestedTourAction['kind'], string> = {
   explain: 'What should Claude explain?',
   revise: 'What should change?',
   replan: 'How should the plan change?',
   reorder: 'Why this order?',
   summarize: 'What should the synthesis focus on?',
   draftWorkItems: 'Where should the work items go, or what should they hold?',
   walkOpenQuestions: 'What should Claude weigh in its recommendations?',
   draftComments: 'What tone or focus should the comments have?',
};

/** What the empty box for the reviewer's words says Claude does without them, for each tour action that the reviewer requests, or null for a revise or a replan, which need the words. */
export const tourActionPlaceholders: Record<RequestedTourAction['kind'], string | null> = {
   explain: 'Leave it empty and Claude explains the whole stop deeper.',
   revise: null,
   replan: null,
   reorder: 'Leave it empty and Claude gets only your order.',
   summarize: 'Leave it empty and Claude summarizes without guidance.',
   draftWorkItems: 'Leave it empty and Claude proposes work items as it sees fit.',
   walkOpenQuestions: 'Leave it empty and Claude weighs each question on its own.',
   draftComments: 'Leave it empty and Claude chooses the tone and focus.',
};

/** The label of the box for the reviewer's words of a change of the familiarity level. */
export const levelChangePrompt = 'Anything else Claude should change?';

/** What the empty box for the reviewer's words of a change of the familiarity level says Claude does without them. */
export const levelChangePlaceholder = 'Leave it empty and Claude only rewrites the stops for the new level.';

/** The label of the box for the reviewer's words of a plan approval. */
export const planApprovalPrompt = 'Anything Claude should know about the plan?';

/** What the empty box for the reviewer's words of a plan approval says Claude does without them. */
export const planApprovalPlaceholder = 'Leave it empty and Claude writes the stops as planned.';

/** Returns what a thread shows of its reviewer decision: the decision's name, for a change of the familiarity level with the levels it changed between, and for an update with its iteration. */
export function decisionSummary(decision: ReviewerDecision): string {
   switch (decision.kind) {
      case 'familiarityChanged':
         return `${decisionLabels.familiarityChanged}: ${familiarityLabels[decision.from]} → ${familiarityLabels[decision.to]}`;
      case 'iterationUpdated':
         return `Move to update ${decision.iteration}`;
      case 'planApproved':
         return decisionLabels.planApproved;
   }
}

/** Returns what a thread shows of its tour action or its reviewer decision. */
export function subjectSummary(subject: QuestionSubject): string {
   return subject.kind === 'action' ? tourActionLabels[subject.action.kind] : decisionSummary(subject.decision);
}

/** Returns what the Stop panel or the Plan panel says while Claude works on the thread's tour action or reviewer decision. */
export function subjectPending(subject: QuestionSubject): string {
   return subject.kind === 'action' ? tourActionPending[subject.action.kind] : decisionPending[subject.decision.kind];
}
