import type { Attachment } from './labels';
import type { Plan, TourPin, TourState } from './types';

/** What the page can tell of Claude's work on a tour. */
export type GenerationPhase = 'awaitingApproval' | 'noSession' | 'planning' | 'pinning' | 'writing';

/** A tour's phase and the line that tells the reviewer about it. */
export interface GenerationStatus {
   phase: GenerationPhase;
   line: string;
}

/** What {@link generationStatus} reads of a tour. */
export interface GenerationFacts {
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
 * @returns The status, or null when the page has not loaded the plan, the tour is archived, no stop is pending, or the pin failed and the Files panel shows why.
 */
export function generationStatus({ state, pin, plan, planWaits, attachment }: GenerationFacts): GenerationStatus | null {
   if (plan === undefined || state === 'archived' || (plan !== null && plan.stops.every((stop) => stop.status !== 'pending'))) {
      return null;
   }

   if (planWaits) {
      return { phase: 'awaitingApproval', line: 'The plan waits for your approval' };
   }

   if (attachment === 'none') {
      return { phase: 'noSession', line: 'No Claude session is writing this tour' };
   }

   if (plan === null) {
      return { phase: 'planning', line: 'Claude is reading the PR and planning the tour…' };
   }

   if (pin.state === 'pinning') {
      return { phase: 'pinning', line: 'Pinning the pull request to its commits…' };
   }

   if (pin.state === 'failed') {
      return null;
   }

   // N counts progress, the stops that are not pending plus one, so it can differ from a pending stop's place in the plan.
   const written = plan.stops.filter((stop) => stop.status !== 'pending').length;
   return { phase: 'writing', line: `Claude is writing stop ${written + 1} of ${plan.stops.length}…` };
}
