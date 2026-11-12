<script lang="ts">
   import EyeOff from '@lucide/svelte/icons/eye-off';
   import Maximize from '@lucide/svelte/icons/maximize';
   import Minimize from '@lucide/svelte/icons/minimize';
   import PopOut from '@lucide/svelte/icons/square-arrow-out-up-right';
   import type { DockviewGroupPanel } from 'dockview';
   import type { WorkspaceLayout } from '../lib/workspaceLayout.svelte';
   import type { PanelId } from '../lib/workspacePanels';
   import TooltipButton from './TooltipButton.svelte';

   let { layout, group }: { layout: WorkspaceLayout; group: DockviewGroupPanel } = $props();

   let active = $state<PanelId | null>(null);
   $effect(() => {
      active = layout.activeIn(group);
      const subscription = group.api.onDidActivePanelChange(() => (active = layout.activeIn(group)));
      return () => subscription.dispose();
   });

   const maximized = $derived(layout.maximizedGroup === group.id);
</script>

<!-- The buttons act on the group's active panel, the one that its tab row shows. A popout window holds no buttons, because closing the window returns its panel, and a panel whose window opens has none either. -->
{#if active !== null && !layout.poppedOut[active] && !layout.opening[active]}
   {@const panel = active}
   {@const title = layout.label(panel)}
   <div class="flex h-full items-center pr-1">
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
      <TooltipButton tooltip="Pop out" variant="ghost" size="icon-xs" aria-label={`Pop out ${title}`} onclick={() => void layout.popOut(panel)}>
         <PopOut />
      </TooltipButton>
      {#if !maximized}
         {@const label = `Hide ${title}`}
         <TooltipButton tooltip={label} variant="ghost" size="icon-xs" aria-label={label} onclick={() => layout.hide(panel, true)}>
            <EyeOff />
         </TooltipButton>
      {/if}
   </div>
{/if}
