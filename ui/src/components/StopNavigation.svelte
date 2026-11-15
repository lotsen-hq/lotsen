<script lang="ts">
   import ChevronLeft from '@lucide/svelte/icons/chevron-left';
   import ChevronRight from '@lucide/svelte/icons/chevron-right';
   import { steppedTarget } from '../lib/skips';
   import type { Plan } from '../lib/types';
   import TooltipButton from './TooltipButton.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      plan,
      index,
      skipped = new Set(),
      summary = false,
      summaryFollows,
      onprevious,
      onnext,
   }: {
      /** The selected tour's plan, or null or undefined while the tour has none or it loads. */
      plan: Plan | null | undefined;
      /** The position of the open stop in the plan, or -1 when no stop is open. */
      index: number;
      /** The IDs of the stops that the reviewer skipped, which next and back pass over. */
      skipped?: ReadonlySet<string>;
      /** Whether the summary page shows, which comes after the last stop. */
      summary?: boolean;
      /** Whether the summary page follows the last stop, so next goes there from the last stop. */
      summaryFollows: boolean;
      /** Opens the stop before the open one that is not skipped, or from the summary page the last such stop. */
      onprevious: () => void;
      /** Opens the next stop that is not skipped, the first such stop when none is open, or the summary page after the last. */
      onnext: () => void;
   } = $props();

   const count = $derived(plan?.stops.length ?? 0);
   const stop = $derived(plan?.stops[index]);
   const chapter = $derived(stop && plan?.chapters.find((candidate) => candidate.id === stop.chapter));
   const isSkipped = $derived(stop !== undefined && skipped.has(stop.id));
   const fullText = $derived(
      summary ? 'Summary · After the last stop' : stop ? [`Stop ${index + 1} of ${count}`, ...(isSkipped ? ['Skipped'] : []), ...(chapter ? [chapter.title] : []), stop.title].join(' · ') : null,
   );
   const at = $derived(summary ? 'summary' : stop === undefined ? -1 : index);
   const previous = $derived(steppedTarget(plan?.stops ?? [], at, skipped, -1, summaryFollows));
   const next = $derived(steppedTarget(plan?.stops ?? [], at, skipped, 1, summaryFollows));
   const nextLabel = $derived(next === 'summary' ? 'Summary' : 'Next stop');
</script>

<!-- A flex item drops the spaces at the start and end of its text, so the separator uses no-break spaces. -->
{#snippet separator()}
   <span class="text-muted-foreground">&nbsp;·&nbsp;</span>
{/snippet}

<!-- Both buttons come before the text, so a step that changes the text's length moves neither of them. -->
<nav class="flex min-w-0 items-center gap-2" aria-label="Stops">
   <TooltipButton tooltip="Previous stop" variant="outline" size="icon-sm" aria-label="Previous stop" disabled={previous === null} onclick={onprevious}>
      <ChevronLeft />
   </TooltipButton>
   <TooltipButton tooltip={nextLabel} variant="outline" size="icon-sm" aria-label={nextLabel} disabled={next === null} onclick={onnext}>
      <ChevronRight />
   </TooltipButton>
   <!-- The live region sits outside the tooltip, whose trigger is a new element each time a stop opens or closes, and a screen reader may not announce a new region. -->
   <div class="ml-1 flex min-w-0" aria-live="polite">
      <WithTooltip text={fullText}>
         {#snippet trigger({ props })}
            <p {...props} class="flex min-w-0 overflow-hidden whitespace-nowrap">
               {#if summary}
                  <span class="min-w-0 truncate">
                     <span class="font-semibold">Summary</span>
                     <span class="text-muted-foreground">·</span>
                     <span class="text-muted-foreground">After the last stop</span>
                  </span>
               {:else if stop}
                  <!-- The position and Skipped keep their width until the line is narrower than they are, and then end in an ellipsis. -->
                  <!-- The narrow bar shows the short position, and keeps the long one for the live region. -->
                  <span class="max-w-full shrink-0 truncate">
                     <span data-stop-position="long" class="font-semibold @max-bar-position:sr-only">Stop {index + 1} of {count}</span>
                     <span data-stop-position="short" class="hidden font-semibold @max-bar-position:inline" aria-hidden="true">{index + 1}/{count}</span>
                     {#if isSkipped}
                        <span class="text-muted-foreground">· Skipped</span>
                     {/if}
                  </span>
                  {#if chapter}
                     <!-- The chapter grows from zero into the room the other items leave, so it gives way before the title truncates. A narrow bar leaves it to the live region. -->
                     <span class="max-w-max min-w-0 grow basis-0 truncate text-muted-foreground @max-bar-chapter:sr-only" data-stop-chapter>{@render separator()}{chapter.title}</span>
                  {/if}
                  <span class="min-w-0 truncate">{@render separator()}<span>{stop.title}</span></span>
               {:else}
                  <span class="min-w-0 truncate text-muted-foreground">{count === 0 ? 'No stops yet' : count === 1 ? '1 stop' : `${count} stops`}</span>
               {/if}
            </p>
         {/snippet}
      </WithTooltip>
   </div>
</nav>
