<!--
   The dives that start from a stop or from a whole tour, each a link that opens the dive, with its reading state as the link's description and, on the summary page, its purpose.
-->
<script lang="ts">
   import CornerDownRight from '@lucide/svelte/icons/corner-down-right';
   import { Button } from '$lib/components/ui/button';
   import { purposeLabels } from '../lib/labels';
   import { readingStateLabels, readingStateOf, tourName } from '../lib/tourFamily';
   import type { TourSummary } from '../lib/types';

   let {
      dives,
      onopendive,
      purpose = false,
      class: className = '',
   }: {
      /** The dives, in the order that the list shows them. */
      dives: readonly TourSummary[];
      onopendive: (tourId: string) => void;
      /** Whether each dive shows its purpose beside its reading state. */
      purpose?: boolean;
      class?: string;
   } = $props();

   const id = $props.id();
</script>

<ul class={['flex flex-col gap-1', className]}>
   {#each dives as dive (dive.id)}
      {@const reading = readingStateLabels[readingStateOf(dive.state)]}
      {@const shownPurpose = purpose && dive.purpose !== undefined ? purposeLabels[dive.purpose] : null}
      <li class="flex min-w-0 items-baseline gap-1.5" data-dive={dive.id}>
         <CornerDownRight class="size-3.5 shrink-0 self-center text-muted-foreground" aria-hidden="true" />
         <Button variant="link" size="sm" class="h-auto min-w-0 justify-start p-0 text-left whitespace-normal" aria-describedby="{id}-{dive.id}" onclick={() => onopendive(dive.id)}
            >{tourName(dive)}</Button
         >
         <!-- One hidden text describes the link, because Chromium computes a description with a space before a visually hidden comma. -->
         <span id="{id}-{dive.id}" class="sr-only">{shownPurpose === null ? reading : `${shownPurpose}, ${reading}`}</span>
         <span class="flex shrink-0 items-baseline gap-1.5" aria-hidden="true">
            {#if shownPurpose !== null}
               <span class="rounded-sm bg-muted px-1.5 text-xs text-muted-foreground" data-dive-purpose>{shownPurpose}</span>
            {/if}
            <span class="rounded-sm bg-muted px-1.5 text-xs text-muted-foreground" data-reading-state={readingStateOf(dive.state)}>{reading}</span>
         </span>
      </li>
   {/each}
</ul>
