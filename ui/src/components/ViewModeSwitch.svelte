<script lang="ts">
   import { viewModesFor, type ViewMode } from '../lib/viewMode';
   import type { TargetKind } from '../lib/types';
   import ToolbarToggle from './ToolbarToggle.svelte';

   let {
      mode,
      target,
      onmode,
   }: {
      mode: ViewMode;
      /** What the open tour reviews, which names the first mode. */
      target: TargetKind;
      onmode: (mode: ViewMode) => void;
   } = $props();
</script>

<div class="flex shrink-0 *:not-first:border-l-0" role="group" aria-label="View mode">
   {#each viewModesFor(target) as option (option.value)}
      <ToolbarToggle compact pressed={mode === option.value} onclick={() => onmode(option.value)}>{option.label}</ToolbarToggle>
   {/each}
</div>
