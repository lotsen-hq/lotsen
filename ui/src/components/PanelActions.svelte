<script lang="ts">
   import ChevronRight from '@lucide/svelte/icons/chevron-right';
   import Maximize from '@lucide/svelte/icons/maximize';
   import Minimize from '@lucide/svelte/icons/minimize';
   import X from '@lucide/svelte/icons/x';
   import type { FoldableId, PanelId, SideColumn, WorkspaceLayout } from '../lib/workspaceLayout.svelte';
   import TooltipButton from './TooltipButton.svelte';

   let {
      layout,
      panel,
      title,
      foldable = null,
      closes = null,
   }: {
      layout: WorkspaceLayout;
      panel: PanelId;
      /** The panel's name in the buttons' labels, such as "the stop". */
      title: string;
      /** The pane that the collapse button folds, or null when the panel has no collapse button. */
      foldable?: FoldableId | null;
      /** The side column that the close button hides, with its name in the button's label, or null when the panel has no close button. */
      closes?: { column: SideColumn; title: string } | null;
   } = $props();

   const maximized = $derived(layout.maximized === panel);
   const folded = $derived(foldable !== null && layout.folded[foldable]);
</script>

<div class="flex shrink-0 items-center">
   {#if foldable !== null && !maximized}
      <TooltipButton
         tooltip={folded ? 'Expand' : 'Collapse'}
         variant="ghost"
         size="icon-xs"
         aria-label={folded ? `Expand ${title}` : `Collapse ${title}`}
         onclick={() => foldable && layout.toggleFolded(foldable)}
      >
         <ChevronRight class={['transition-transform', !folded && 'rotate-90']} />
      </TooltipButton>
   {/if}
   {#if !folded}
      <TooltipButton
         tooltip={maximized ? 'Restore' : 'Maximize'}
         variant="ghost"
         size="icon-xs"
         aria-label={maximized ? `Restore ${title}` : `Maximize ${title}`}
         onclick={() => layout.toggleMaximized(panel)}
      >
         {#if maximized}
            <Minimize />
         {:else}
            <Maximize />
         {/if}
      </TooltipButton>
   {/if}
   {#if closes !== null && !maximized}
      {@const label = `Close ${closes.title}`}
      <TooltipButton tooltip={label} variant="ghost" size="icon-xs" aria-label={label} onclick={() => closes && layout.close(closes.column)}>
         <X />
      </TooltipButton>
   {/if}
</div>
