<script lang="ts">
   import MonitorIcon from '@lucide/svelte/icons/monitor';
   import MoonIcon from '@lucide/svelte/icons/moon';
   import SettingsIcon from '@lucide/svelte/icons/settings';
   import SunIcon from '@lucide/svelte/icons/sun';
   import type { Component } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import * as Sheet from '$lib/components/ui/sheet';
   import type { PruneOutcome } from '../lib/viewState';
   import type { Theme } from '../lib/colorScheme';
   import { colorThemeChoices } from '../lib/colorThemes';
   import { fontSizeChoices, type FontSize } from '../lib/fontSize';
   import { messageStyles, type MessageStyle } from '../lib/messageStyle';
   import { mixedThresholdBounds, placementModes, type PlacementMode } from '../lib/placement';
   import type { Settings } from '../lib/settings.svelte';
   import { syntaxColorChoices } from '../lib/syntaxColors';
   import { viewModes, type ViewMode } from '../lib/viewMode';
   import AppearancePreview from './AppearancePreview.svelte';
   import ChoicePicture from './ChoicePicture.svelte';
   import ColorSwatch from './ColorSwatch.svelte';
   import SettingChoice from './SettingChoice.svelte';
   import SettingNumber from './SettingNumber.svelte';
   import SettingSelect from './SettingSelect.svelte';
   import SettingsSection from './SettingsSection.svelte';
   import SyntaxSwatch from './SyntaxSwatch.svelte';
   import TooltipButton from './TooltipButton.svelte';

   let {
      settings,
      onprune,
      onversion,
   }: {
      settings: Settings;
      /** Removes the clones that no tour refers to, and returns how many it removed or why it failed. */
      onprune: () => Promise<PruneOutcome>;
      /** Returns the server's build, such as `0.3.0+e23502b… (8549f42b…)`, or null when it could not be read. */
      onversion: () => Promise<string | null>;
   } = $props();

   const id = $props.id();
   let pruning = $state(false);
   let pruned = $state<PruneOutcome | null>(null);
   let build = $state<string | null>(null);

   function opened(open: boolean): void {
      if (open && build === null) {
         void onversion().then((read) => (build = read));
      }
   }

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
   const themeIcons: Record<Theme, Component> = { system: MonitorIcon, light: SunIcon, dark: MoonIcon };
   const fontSizeGlyphs: Record<FontSize, string> = { small: 'text-xs', medium: 'text-sm', large: 'text-lg leading-none' };

   const viewModeHints: Record<ViewMode, string> = {
      wholePr: 'The whole pull request, with the rest dimmed around the stop.',
      stopFiles: "Only the stop's files, collapsed to its passages.",
      stepThrough: "One of the stop's passages at a time.",
   };
   const placementHints: Record<PlacementMode, string> = {
      side: 'Every explanation shows in the Stop panel.',
      inline: 'Each explanation shows below its primary passage. A stop without one shows in the Stop panel.',
      mixed: 'Each stop shows inline or in the Stop panel, as its kind and length suit it.',
   };
   const messageStyleHints: Record<MessageStyle, string> = {
      bubbles: "Your messages on the right, Claude's on the left.",
      fullWidth: "Each message spans the panel's width.",
   };
</script>

<Sheet.Root onOpenChange={opened}>
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
      <div class="scroll-box flex min-h-0 flex-1 flex-col px-4 pb-4">
         <SettingsSection title="Appearance">
            <AppearancePreview />
            <SettingChoice label="Theme" inline options={themes} value={settings.theme} onchange={(theme) => settings.setTheme(theme)}>
               {#snippet item(option)}
                  {@const Icon = themeIcons[option.value]}
                  <Icon aria-hidden="true" class="size-4 shrink-0" />{option.label}
               {/snippet}
            </SettingChoice>
            <SettingSelect label="Color theme" options={colorThemeChoices} value={settings.colorTheme} onchange={(theme) => settings.setColorTheme(theme)}>
               {#snippet selected(option)}
                  <ColorSwatch family={option.value} size="small" />{option.label}
               {/snippet}
               {#snippet item(option)}
                  <ColorSwatch family={option.value} size="large" />{option.label}
               {/snippet}
            </SettingSelect>
            <SettingSelect label="Syntax colors" options={syntaxColorChoices} value={settings.syntaxColors} onchange={(colors) => settings.setSyntaxColors(colors)}>
               {#snippet selected(option)}
                  <span class="flex-1 truncate text-left">{option.label}</span><SyntaxSwatch family={option.value} kind="dots" />
               {/snippet}
               {#snippet item(option)}
                  <span class="flex flex-col gap-0.5">{option.label}<SyntaxSwatch family={option.value} kind="code" /></span>
               {/snippet}
            </SettingSelect>
            <SettingChoice label="Font size" inline options={fontSizeChoices} value={settings.fontSize} onchange={(size) => settings.setFontSize(size)}>
               {#snippet item(option)}
                  <span aria-hidden="true" class="w-4 text-center {fontSizeGlyphs[option.value]}">A</span><span class="sr-only">{option.label}</span>
               {/snippet}
            </SettingChoice>
         </SettingsSection>
         <SettingsSection title="Tour">
            <SettingChoice
               label="Default view mode"
               description="Each tour opens in this mode. A mode you choose in the diff toolbar lasts until another tour opens."
               options={viewModes}
               hints={viewModeHints}
               value={settings.defaultViewMode}
               onchange={(mode) => settings.setDefaultViewMode(mode)}
            />
            <div class="flex flex-col gap-3">
               <SettingChoice label="Placement" appearance="cards" options={placementModes} hints={placementHints} value={settings.placement} onchange={(mode) => settings.setPlacement(mode)}>
                  {#snippet item(option)}
                     <ChoicePicture picture={option.value} />{option.label}
                  {/snippet}
               </SettingChoice>
               <!-- The threshold matters only to the mixed rule (resolvePlacement). -->
               {#if settings.placement === 'mixed'}
                  <div class="rounded-lg border border-dashed p-3">
                     <SettingNumber
                        label="Mixed threshold"
                        description="A stop whose prose is longer than this shows in the Stop panel."
                        unit="characters"
                        value={settings.mixedThreshold}
                        min={mixedThresholdBounds.min}
                        max={mixedThresholdBounds.max}
                        step={50}
                        onchange={(threshold) => settings.setMixedThreshold(threshold)}
                     />
                  </div>
               {/if}
            </div>
         </SettingsSection>
         <SettingsSection title="Threads">
            <SettingChoice
               label="Thread messages"
               appearance="cards"
               options={messageStyles}
               hints={messageStyleHints}
               value={settings.messageStyle}
               onchange={(style) => settings.setMessageStyle(style)}
            >
               {#snippet item(option)}
                  <ChoicePicture picture={option.value} />{option.label}
               {/snippet}
            </SettingChoice>
         </SettingsSection>
         <SettingsSection title="Storage">
            <div class="flex flex-col gap-2">
               <h4 id="{id}-clones" class="font-medium">Repository clones</h4>
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
         </SettingsSection>
         <footer class="flex flex-wrap items-center justify-between gap-x-3 gap-y-1 border-t pt-4 text-sm">
            <a class="underline" href="/THIRD-PARTY-NOTICES.txt" target="_blank" rel="noreferrer">Third-party notices</a>
            {#if build !== null}
               <!-- The version leaves out the build metadata after the '+'. The tooltip holds the whole build, for a bug report. -->
               <span class="text-muted-foreground" title={build}>lotsen {build.split(/[+ ]/, 1)[0]}</span>
            {/if}
         </footer>
      </div>
   </Sheet.Content>
</Sheet.Root>
