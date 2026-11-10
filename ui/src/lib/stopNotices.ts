import type { StaleStatus } from './openStop.svelte';
import type { ShownVersion } from './versions';

/** Returns what the stale notice says about a stop, or null when the stop is not stale. */
export function staleNoticeText(stale: StaleStatus): string | null {
   if (stale.kind !== 'stale') {
      return null;
   }

   const target = stale.target === null ? 'the new update' : `update ${stale.target}`;
   const state = stale.revising ? `Claude is revising this stop for ${target}.` : `this stop waits for Claude to revise it for ${target}.`;
   const passages = stale.earlierPassages ? ` Its passages are lines from before update ${stale.since}, so the diff does not mark them and they do not open.` : '';
   return `Stale since update ${stale.since}: ${state}${passages}`;
}

/** Returns what the stop version notice says about the earlier stop version that the page shows. */
export function versionNoticeText(shown: ShownVersion): string {
   switch (shown.reason.kind) {
      case 'iterationUpdate':
         return `Claude revised this stop for update ${shown.reason.iteration} while you read it. This is the version you read, version ${shown.number}, which takes no answers.`;
      case 'revised':
         return `Claude revised this stop while you read it. This is the version you read, version ${shown.number}, which takes no answers.`;
      default:
         return `This is version ${shown.number} of ${shown.current}, which a revision replaced. It takes no answers.`;
   }
}
