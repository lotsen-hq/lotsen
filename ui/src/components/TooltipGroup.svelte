<script lang="ts">
   import { Tooltip as TooltipPrimitive } from 'bits-ui';
   import type { Snippet } from 'svelte';
   import * as Tooltip from '$lib/components/ui/tooltip';
   import { setTooltipGroup } from '../lib/tooltip';
   import TooltipScope from './TooltipScope.svelte';

   let { children: rows }: { children: Snippet } = $props();

   // Every WithTooltip below triggers this one tooltip, so a long list mounts one tooltip, not one per row.
   const tether = TooltipPrimitive.createTether<() => string>();
   setTooltipGroup(tether);
</script>

{@render rows()}
<TooltipScope>
   <Tooltip.Root {tether}>
      {#snippet children({ payload })}
         <Tooltip.Content class="whitespace-pre-line">{payload?.()}</Tooltip.Content>
      {/snippet}
   </Tooltip.Root>
</TooltipScope>
