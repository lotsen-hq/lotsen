import { pinningLines, type Attachment } from './labels';
import type { Plan, TargetKind, TourPin, TourState } from './types';

const planningLines: Record<TargetKind, string> = {
   pullRequest: 'Claude is reading the PR and planning the tour…',
   repository: 'Claude is reading the repository and planning the tour…',
};

/** What the page can tell of Claude's work on a tour. */
export type GenerationPhase = 'awaitingApproval' | 'planning' | 'pinning' | 'writing';

/** A tour's phase and the line that tells the reviewer about it. */
export interface GenerationStatus {
   phase: GenerationPhase;
   line: string;
}

/** What {@link generationStatus} reads of a tour. */
export interface GenerationFacts {
   /** What the tour reviews, which the lines name. */
   target: TargetKind;
   state: TourState;
   pin: TourPin;
   /** The tour's plan, null when Claude has set none, or undefined while the page has not loaded it. */
   plan: Plan | null | undefined;
   /** Whether the plan waits for the reviewer's approval. */
   planWaits: boolean;
   attachment: Attachment;
}

/**
 * Returns what to tell the reviewer about Claude's work on a tour.
 *
 * @remarks The pending stops decide whether Claude still has work, because the tour state is in progress once the reviewer opens a stop, even with stops pending.
 * Reading and planning share one line because Claude reports nothing before `set_plan`.
 * @returns The status, or null when the page has not loaded the plan, the tour is archived, no stop is pending, the pin failed and the Files panel shows why,
 * or no session writes the tour and the plan waits for no approval. The top bar's status chip then says that the tour has no session.
 */
export function generationStatus({ target, state, pin, plan, planWaits, attachment }: GenerationFacts): GenerationStatus | null {
   if (plan === undefined || state === 'archived' || (plan !== null && plan.stops.every((stop) => stop.status !== 'pending'))) {
      return null;
   }

   if (planWaits) {
      return { phase: 'awaitingApproval', line: 'The plan waits for your approval' };
   }

   if (attachment === 'none') {
      return null;
   }

   if (plan === null) {
      return { phase: 'planning', line: planningLines[target] };
   }

   if (pin.state === 'pinning') {
      return { phase: 'pinning', line: pinningLines[target] };
   }

   if (pin.state === 'failed') {
      return null;
   }

   // N counts progress, the stops that are not pending plus one, so it can differ from a pending stop's place in the plan.
   const written = plan.stops.filter((stop) => stop.status !== 'pending').length;
   return { phase: 'writing', line: `Claude is writing stop ${written + 1} of ${plan.stops.length}…` };
}
