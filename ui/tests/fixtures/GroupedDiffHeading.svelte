<script lang="ts">
   import TooltipGroup from '../../src/components/TooltipGroup.svelte';
   import WithTooltip from '../../src/components/WithTooltip.svelte';
   import type { FileMark } from '../../src/lib/types';

   let { path, mark = null }: { path: string; mark?: FileMark | null } = $props();
</script>

<!-- DiffHeading with its mark's tooltip in a tooltip group, so the group's trigger unmounts in the update that changes its text. -->
<code>{path}</code>
{#if mark !== null}
   <TooltipGroup>
      <WithTooltip text={mark.reason}>
         {#snippet trigger({ props })}
            <span {...props}>{mark.mark}</span>
         {/snippet}
      </WithTooltip>
   </TooltipGroup>
{/if}
