<script lang="ts">
   import SettingsIcon from '@lucide/svelte/icons/settings';
   import { Button } from '$lib/components/ui/button';
   import * as Sheet from '$lib/components/ui/sheet';
   import type { PruneOutcome } from '../lib/viewState';
   import type { Theme } from '../lib/colorScheme';
   import { fontSizeChoices } from '../lib/fontSize';
   import { messageStyles } from '../lib/messageStyle';
   import { mixedThresholdBounds, placementModes } from '../lib/placement';
   import type { Settings } from '../lib/settings.svelte';
   import { syntaxColorChoices } from '../lib/syntaxColors';
   import { viewModes } from '../lib/viewMode';
   import SettingChoice from './SettingChoice.svelte';
   import SettingNumber from './SettingNumber.svelte';
   import TooltipButton from './TooltipButton.svelte';

   let {
      settings,
      onprune,
   }: {
      settings: Settings;
      /** Removes the clones that no tour refers to, and returns how many it removed or why it failed. */
      onprune: () => Promise<PruneOutcome>;
   } = $props();

   const id = $props.id();
   let pruning = $state(false);
   let pruned = $state<PruneOutcome | null>(null);

   async function prune(): Promise<void> {
      if (pruning) {
         return;
      }

      pruning = true;
      pruned = null;
      try {
         pruned = await onprune();
      } finally {
         pruning = false;
      }
   }

   function prunedText(removed: number): string {
      return removed === 0 ? 'No clone needed pruning.' : removed === 1 ? 'Removed 1 clone.' : `Removed ${removed} clones.`;
   }

   const themes: { value: Theme; label: string }[] = [
      { value: 'system', label: 'System' },
      { value: 'light', label: 'Light' },
      { value: 'dark', label: 'Dark' },
   ];
</script>

<Sheet.Root>
   <Sheet.Trigger>
      {#snippet child({ props })}
         <TooltipButton {...props} tooltip="Settings" variant="ghost" size="icon-sm" aria-label="Settings">
            <SettingsIcon />
         </TooltipButton>
      {/snippet}
   </Sheet.Trigger>
   <Sheet.Content>
      <Sheet.Header>
         <Sheet.Title>Settings</Sheet.Title>
      </Sheet.Header>
      <div class="scroll-box flex min-h-0 flex-1 flex-col gap-6 px-4 pb-4">
         <SettingChoice label="Theme" options={themes} value={settings.theme} onchange={(theme) => settings.setTheme(theme)} />
         <SettingChoice
            label="Syntax colors"
            description="The theme picks the light or dark palette of the family."
            options={syntaxColorChoices}
            value={settings.syntaxColors}
            onchange={(colors) => settings.setSyntaxColors(colors)}
         />
         <SettingChoice label="Font size" options={fontSizeChoices} value={settings.fontSize} onchange={(size) => settings.setFontSize(size)} />
         <SettingChoice
            label="Default view mode"
            description="Each tour opens in this mode. A mode you choose in the diff toolbar lasts until another tour opens."
            options={viewModes}
            value={settings.defaultViewMode}
            onchange={(mode) => settings.setDefaultViewMode(mode)}
         />
         <SettingChoice label="Placement" options={placementModes} value={settings.placement} onchange={(mode) => settings.setPlacement(mode)} />
         <SettingNumber
            label="Mixed threshold"
            description="In mixed placement, a stop whose prose is longer than this shows in the Stop panel."
            unit="characters"
            value={settings.mixedThreshold}
            min={mixedThresholdBounds.min}
            max={mixedThresholdBounds.max}
            step={50}
            onchange={(threshold) => settings.setMixedThreshold(threshold)}
         />
         <SettingChoice label="Thread messages" options={messageStyles} value={settings.messageStyle} onchange={(style) => settings.setMessageStyle(style)} />
         <div class="flex flex-col gap-2">
            <h3 id="{id}-clones" class="font-medium">Repository clones</h3>
            <p id="{id}-clones-description" class="text-sm text-muted-foreground">
               lotsen keeps a clone of each tour's repository. Pruning removes the clones that no tour uses, and any that an interrupted clone left behind. A tour that needs its repository again
               clones it again.
            </p>
            <!-- The result sits beside the button, so it shows where the reviewer clicked even when the panel scrolls. -->
            <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
               <Button variant="outline" size="sm" disabled={pruning} aria-describedby="{id}-clones-description{pruning ? ` ${id}-pruning` : ''}" onclick={() => void prune()}>
                  Prune unused clones
               </Button>
               {#if pruning}
                  <p id="{id}-pruning" class="text-sm text-muted-foreground" role="status">Pruning the clones…</p>
               {:else if pruned !== null && 'removed' in pruned}
                  <p class="text-sm" role="status">{prunedText(pruned.removed)}</p>
               {/if}
            </div>
            {#if pruned !== null && 'failure' in pruned}
               <p class="text-sm text-destructive" role="alert">{pruned.failure}</p>
            {/if}
         </div>
         <a class="text-sm underline" href="/THIRD-PARTY-NOTICES.txt" target="_blank" rel="noreferrer">Third-party notices</a>
      </div>
   </Sheet.Content>
</Sheet.Root>
