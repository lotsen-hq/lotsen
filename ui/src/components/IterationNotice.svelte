<!-- The banner under the top bar that tells of a newer iteration of the open tour's pull request, or of why the reviewer's last check for one could not finish. -->
<script lang="ts">
   import type { Snippet } from 'svelte';
   import { iterationNotice } from '../lib/iterations';
   import type { AvailableIteration } from '../lib/types';

   let {
      available,
      shown,
      failure,
      actions,
   }: {
      /** The newer iteration, or null when none is known. */
      available: AvailableIteration | null;
      /** The number of the iteration that the tour is pinned to, or null when the tour is not pinned. */
      shown: number | null;
      /** Why the reviewer's last check could not finish, or null. */
      failure: string | null;
      /** The actions on the newer iteration, which follow the notice. */
      actions?: Snippet<[AvailableIteration]>;
   } = $props();
</script>

{#if available !== null || failure !== null}
   <div class="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-l-4 border-l-primary bg-accent px-4 py-1.5 text-sm text-accent-foreground" data-iteration-notice>
      {#if available !== null}
         <p role="status" aria-label="New update">{iterationNotice(available)}{shown === null ? '' : `. This tour shows update ${shown} until you move it to update ${available.pin.iteration}`}.</p>
         {@render actions?.(available)}
      {/if}
      {#if failure !== null}
         <p role="alert" class="min-w-0 text-destructive">The check for a new update could not finish: {failure}</p>
      {/if}
   </div>
{/if}
