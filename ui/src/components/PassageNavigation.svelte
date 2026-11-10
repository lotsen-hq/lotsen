<script lang="ts">
   import ChevronLeft from '@lucide/svelte/icons/chevron-left';
   import ChevronRight from '@lucide/svelte/icons/chevron-right';
   import { linesLabel } from '../lib/labels';
   import { inFile, type FilePaths } from '../lib/pin';
   import type { StopPassage } from '../lib/types';
   import TooltipButton from './TooltipButton.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      passages,
      index,
      openFile,
      onprevious,
      onnext,
   }: {
      /** The open stop's passages in the order that step through visits them. */
      passages: readonly StopPassage[];
      /** The position of the focused passage in the passages, or -1 when there are none. */
      index: number;
      /** The paths of the file that the center shows, or null while none shows. */
      openFile: FilePaths | null;
      onprevious: () => void;
      onnext: () => void;
   } = $props();

   const focused = $derived(passages[index]);
   // The toolbar's path names the passage's file while the open file holds the passage.
   const otherFile = $derived(focused !== undefined && !inFile(focused, openFile));
   // The count's width follows the number of passages alone, so the buttons stay in place while stepping.
   const countWidth = $derived(passages.length < 10 ? 'w-[3ch]' : passages.length < 100 ? 'w-[5ch]' : 'w-[7ch]');
   // The status names in words what the terse row shows and its tooltips hold, and holds no buttons, so a step announces the position alone.
   const spoken = $derived(
      focused === undefined
         ? 'This stop has no passages.'
         : [`Passage ${index + 1} of ${passages.length}`, ...(focused.label ? [focused.label] : []), `${otherFile ? `${focused.path} ` : ''}${linesLabel(focused)}`].join(', '),
   );
</script>

<!-- Its label gives way before the open file's path in the diff toolbar, which keeps the file name. -->
<nav class="flex min-w-0 shrink-1000 items-center gap-1" aria-label="Passages">
   <TooltipButton tooltip="Previous passage" variant="outline" size="icon-sm" aria-label="Previous passage" disabled={index <= 0} onclick={onprevious}>
      <ChevronLeft />
   </TooltipButton>
   {#if focused}
      <span class={['shrink-0 text-center text-sm font-semibold tabular-nums', countWidth]} aria-hidden="true">{index + 1}/{passages.length}</span>
   {/if}
   <TooltipButton tooltip="Next passage" variant="outline" size="icon-sm" aria-label="Next passage" disabled={index + 1 >= passages.length} onclick={onnext}>
      <ChevronRight />
   </TooltipButton>
   <p class="ml-1 min-w-0 truncate text-sm" aria-hidden="true">
      {#if focused}
         {#if otherFile}
            <WithTooltip text={focused.path}>
               {#snippet trigger({ props })}
                  <span {...props} class="font-mono">{focused.path.slice(focused.path.lastIndexOf('/') + 1)}</span>
               {/snippet}
            </WithTooltip>
         {/if}
         <WithTooltip text={focused.label ?? null}>
            {#snippet trigger({ props })}
               <span {...props}>{linesLabel(focused)}</span>
            {/snippet}
         </WithTooltip>
      {:else}
         <span class="text-muted-foreground">This stop has no passages.</span>
      {/if}
   </p>
   <span class="sr-only" role="status">{spoken}</span>
</nav>
