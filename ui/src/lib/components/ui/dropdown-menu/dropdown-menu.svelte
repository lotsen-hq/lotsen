<script lang="ts">
   import { DropdownMenu as DropdownMenuPrimitive } from 'bits-ui';
   import { closeOnMove } from '$lib/panelDocument.svelte';

   let { open = $bindable(false), ...restProps }: DropdownMenuPrimitive.RootProps = $props();

   closeOnMove(() => (open = false));
</script>

<DropdownMenuPrimitive.Root bind:open {...restProps} />
