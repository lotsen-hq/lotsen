<script lang="ts">
   import { ContextMenu as ContextMenuPrimitive } from 'bits-ui';
   import { closeOnMove } from '$lib/panelDocument.svelte';

   let { open = $bindable(false), ...restProps }: ContextMenuPrimitive.RootProps = $props();

   closeOnMove(() => (open = false));
</script>

<ContextMenuPrimitive.Root bind:open {...restProps} />
