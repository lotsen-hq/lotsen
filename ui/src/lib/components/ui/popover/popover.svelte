<script lang="ts">
   import { Popover as PopoverPrimitive } from 'bits-ui';
   import { closeOnMove } from '$lib/panelDocument.svelte';

   let { open = $bindable(false), ...restProps }: PopoverPrimitive.RootProps = $props();

   closeOnMove(() => (open = false));
</script>

<PopoverPrimitive.Root bind:open {...restProps} />
