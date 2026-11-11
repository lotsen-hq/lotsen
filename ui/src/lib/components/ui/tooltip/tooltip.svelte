<script lang="ts" generics="T = never">
   import { Tooltip as TooltipPrimitive } from 'bits-ui';
   import { closeOnMove } from '$lib/panelDocument.svelte';

   let { open = $bindable(false), ...restProps }: TooltipPrimitive.RootProps<T> = $props();

   closeOnMove(() => (open = false));
</script>

<TooltipPrimitive.Root bind:open {...restProps} />
