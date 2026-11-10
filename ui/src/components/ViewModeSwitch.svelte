<script lang="ts">
   import { viewModes, type ViewMode } from '../lib/viewMode';
   import ToolbarToggle from './ToolbarToggle.svelte';

   let { mode, onmode }: { mode: ViewMode; onmode: (mode: ViewMode) => void } = $props();
</script>

<div class="flex shrink-0 *:not-first:border-l-0" role="group" aria-label="View mode">
   {#each viewModes as option (option.value)}
      <ToolbarToggle compact pressed={mode === option.value} onclick={() => onmode(option.value)}>{option.label}</ToolbarToggle>
   {/each}
</div>
