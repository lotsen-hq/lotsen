import type { StaleStatus } from './openStop.svelte';
import { pinStepName } from './pin';
import type { ShownVersion } from './versions';

/** Returns what the stale notice says about a stop, or null when the stop is not stale. */
export function staleNoticeText(stale: StaleStatus): string | null {
   if (stale.kind !== 'stale') {
      return null;
   }

   const since = pinStepName(stale.step, stale.since);
   const target = stale.target !== null ? pinStepName(stale.step, stale.target) : stale.step === 'update' ? 'the new update' : 'the latest commit';
   const state = stale.revising ? `Claude is revising this stop for ${target}.` : `this stop waits for Claude to revise it for ${target}.`;
   const view = stale.step === 'update' ? 'the diff' : 'the file view';
   const passages = stale.earlierPassages ? ` Its passages are lines from before ${since}, so ${view} does not mark them and they do not open.` : '';
   return `Stale since ${since}: ${state}${passages}`;
}

/** Returns what the stop version notice says about the earlier stop version that the page shows. */
export function versionNoticeText(shown: ShownVersion): string {
   switch (shown.reason.kind) {
      case 'iterationUpdate':
         return `Claude revised this stop for update ${shown.reason.iteration} while you read it. This is the version you read, version ${shown.number}, which takes no answers.`;
      case 'tourMove':
         return `Claude revised this stop for move ${shown.reason.move} while you read it. This is the version you read, version ${shown.number}, which takes no answers.`;
      case 'revised':
         return `Claude revised this stop while you read it. This is the version you read, version ${shown.number}, which takes no answers.`;
      default:
         return `This is version ${shown.number} of ${shown.current}, which a revision replaced. It takes no answers.`;
   }
}
