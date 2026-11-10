import type { Plan, Stop, StopContent } from './types';

/** Returns the stop's content, or null while the stop is pending or when there is no stop. */
export function contentOf(stop: Stop | null | undefined): StopContent | null {
   return stop?.stage === 'written' ? stop.content : null;
}

/** Returns the number of the stop's content version, from 1, or undefined while the stop is pending or when there is no stop. */
export function versionOf(stop: Stop | null | undefined): number | undefined {
   return stop?.stage === 'written' ? stop.version : undefined;
}

/** Returns the number of the iteration whose update made the stop's content stale, or undefined when its content is not stale, the stop is pending or there is no stop. */
export function staleOnOf(stop: Stop | null | undefined): number | undefined {
   return stop?.stage === 'written' ? stop.staleOn : undefined;
}

/** Returns the IDs of the plan's stops that the reviewer skipped, in plan order, or none without a plan. */
export function skippedIn(plan: Plan | null | undefined): string[] {
   return (plan?.stops ?? []).filter((stop) => stop.skipped === true).map((stop) => stop.id);
}
