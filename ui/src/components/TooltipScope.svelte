<script lang="ts">
   import type { Snippet } from 'svelte';
   import * as Tooltip from '$lib/components/ui/tooltip';
   import { enterTooltipScope } from '../lib/tooltip';

   let { children }: { children: Snippet } = $props();

   const nested = enterTooltipScope();
</script>

<!--
   One provider shares its delays among every tooltip below it. A tooltip waits long enough that a pointer crossing a toolbar opens none, and for the skip
   delay after one closes, the next opens at once. Only focus that matches :focus-visible opens one.
-->
{#if nested}
   {@render children()}
{:else}
   <Tooltip.Provider delayDuration={400} skipDelayDuration={300} disableHoverableContent ignoreNonKeyboardFocus>
      {@render children()}
   </Tooltip.Provider>
{/if}
