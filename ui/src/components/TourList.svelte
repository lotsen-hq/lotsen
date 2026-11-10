<script lang="ts">
   import Archive from '@lucide/svelte/icons/archive';
   import ArchiveRestore from '@lucide/svelte/icons/archive-restore';
   import Trash2 from '@lucide/svelte/icons/trash-2';
   import { familiarityLabels } from '../lib/labels';
   import { pullRequestFlags, tourStateLabels } from '../lib/tourList';
   import type { TourSummary } from '../lib/types';
   import TooltipButton from './TooltipButton.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      tours,
      hiddenCount,
      showHidden,
      loaded,
      error,
      selectedTourId,
      onselect,
      onshowhidden,
      onarchive,
      ondelete,
   }: {
      /** The tours that the list shows, as `listedTours` picks them. */
      tours: TourSummary[];
      /** The number of tours that the list hides while hidden tours do not show. */
      hiddenCount: number;
      showHidden: boolean;
      loaded: boolean;
      error: string | null;
      selectedTourId: string | null;
      onselect: (tourId: string) => void;
      onshowhidden: (show: boolean) => void;
      /** Archives a tour or unarchives it, and returns null once stored, or the reason the write failed. */
      onarchive: (tourId: string, archived: boolean) => Promise<string | null>;
      /** Asks the reviewer to confirm the delete of a tour. */
      ondelete: (tour: TourSummary) => void;
   } = $props();

   let archiveError = $state<string | null>(null);

   async function archive(tour: TourSummary) {
      archiveError = await onarchive(tour.id, tour.state !== 'archived');
   }
</script>

<nav aria-labelledby="tour-list-heading">
   <div class="flex items-center justify-between gap-3 px-4 pt-3 pb-2">
      <h2 id="tour-list-heading" class="text-xs font-semibold tracking-wider text-muted-foreground uppercase">Tours</h2>
      {#if hiddenCount > 0}
         <label class="flex cursor-pointer items-center gap-1.5 text-sm text-muted-foreground">
            <input type="checkbox" class="accent-primary" checked={showHidden} onchange={(event) => onshowhidden(event.currentTarget.checked)} />
            Show hidden tours ({hiddenCount})
         </label>
      {/if}
   </div>
   {#if error}
      <p class="px-4 py-2 text-destructive" role="alert">{error}</p>
   {/if}
   {#if archiveError}
      <p class="px-4 py-2 text-destructive" role="alert">{archiveError}</p>
   {/if}
   {#if tours.length > 0}
      <ul>
         {#each tours as tour (tour.id)}
            {@const archived = tour.state === 'archived'}
            <li class={['flex items-center border-l-3', tour.id === selectedTourId ? 'border-primary bg-accent' : 'border-transparent']}>
               <!-- The left border is the selection bar, so focus leaves its color alone. -->
               <!-- The list sits in the tour switcher's popover, which one Escape closes even while the tooltip of a tour or of one of its buttons shows. -->
               <WithTooltip text={tour.target.url} deferEscape>
                  {#snippet trigger({ props })}
                     <button
                        {...props}
                        type="button"
                        class="flex min-w-0 flex-1 cursor-pointer scroll-my-1 flex-col gap-0.5 py-2 pr-1 pl-4 text-left hover:bg-accent focus-visible:ring-inset"
                        aria-current={tour.id === selectedTourId ? 'true' : undefined}
                        onclick={() => onselect(tour.id)}
                     >
                        <span class="flex items-baseline justify-between gap-2">
                           <span id={`tour-label-${tour.id}`} class="wrap-anywhere">{tour.label}</span>
                           <span class="flex shrink-0 items-baseline gap-1">
                              {#if pullRequestFlags[tour.pullRequestState]}
                                 <span class="tour-flag" data-pull-request={tour.pullRequestState}>{pullRequestFlags[tour.pullRequestState]}</span>
                              {/if}
                              <span class="tour-state" data-state={tour.state}>{tourStateLabels[tour.state] ?? tour.state}</span>
                           </span>
                        </span>
                        <span class="text-sm text-muted-foreground">
                           {familiarityLabels[tour.familiarity] ?? tour.familiarity} ·
                           {tour.stopCount === 0 ? 'no plan yet' : tour.stopCount === 1 ? '1 stop' : `${tour.stopCount} stops`}
                        </span>
                     </button>
                  {/snippet}
               </WithTooltip>
               <TooltipButton
                  tooltip={archived ? 'Unarchive this tour' : 'Archive this tour'}
                  variant="ghost"
                  size="icon"
                  class="shrink-0"
                  aria-label={archived ? 'Unarchive this tour' : 'Archive this tour'}
                  aria-describedby={`tour-label-${tour.id}`}
                  deferEscape
                  onclick={() => void archive(tour)}
               >
                  {#if archived}<ArchiveRestore />{:else}<Archive />{/if}
               </TooltipButton>
               <TooltipButton
                  tooltip="Delete this tour"
                  variant="ghost"
                  size="icon"
                  class="mr-2 shrink-0"
                  aria-label="Delete this tour"
                  aria-describedby={`tour-label-${tour.id}`}
                  deferEscape
                  onclick={() => ondelete(tour)}
               >
                  <Trash2 />
               </TooltipButton>
            </li>
         {/each}
      </ul>
   {:else if hiddenCount > 0}
      <p class="px-4 py-2 text-muted-foreground">Every tour is hidden. Show hidden tours to see them.</p>
   {:else if loaded}
      <p class="px-4 py-2 text-muted-foreground">No tours yet. Ask Claude to start a tour of a pull request.</p>
   {:else if !error}
      <p class="px-4 py-2 text-muted-foreground">Loading the tours…</p>
   {/if}
</nav>
