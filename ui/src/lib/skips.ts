import type { Stop } from './types';

/**
 * Returns the position of the stop that next or back opens: the nearest stop in that direction that the reviewer did not skip.
 *
 * Next and back pass over skipped stops, because a skip says that the reviewer does not want to read the stop in order. The reviewer still opens a skipped stop by picking it.
 *
 * @param index The position of the open stop in `stops`, or -1 when none is open. Next then opens the first stop that is not skipped, and back opens none.
 * @param step 1 for next, -1 for back.
 * @returns The stop's position, or -1 when every stop in that direction is skipped.
 */
export function steppedStopIndex(stops: readonly Stop[], index: number, skipped: ReadonlySet<string>, step: 1 | -1): number {
   if (index < 0 && step < 0) {
      return -1;
   }

   for (let candidate = index + step; candidate >= 0 && candidate < stops.length; candidate += step) {
      if (!skipped.has(stops[candidate]!.id)) {
         return candidate;
      }
   }

   return -1;
}

/** Where next or back goes: a stop's position in the plan, the summary page, or nowhere. */
export type StepTarget = number | 'summary' | null;

/**
 * Returns where next or back goes from a stop or from the summary page, which comes after the plan's last stop. Next goes to the summary page once no stop after the open one is left to read
 * in order, and back from the summary page goes to the last stop that the reviewer did not skip.
 *
 * @param at The position of the open stop in `stops`, -1 when none is open, or `'summary'` while the summary page shows.
 * @param step 1 for next, -1 for back.
 * @param summaryFollows Whether the summary page follows the last stop. Without it, next from the last stop goes nowhere.
 */
export function steppedTarget(stops: readonly Stop[], at: number | 'summary', skipped: ReadonlySet<string>, step: 1 | -1, summaryFollows: boolean): StepTarget {
   if (at === 'summary') {
      const last = step < 0 ? steppedStopIndex(stops, stops.length, skipped, -1) : -1;
      return last < 0 ? null : last;
   }

   const index = steppedStopIndex(stops, at, skipped, step);
   return index >= 0 ? index : step > 0 && stops.length > 0 && summaryFollows ? 'summary' : null;
}
