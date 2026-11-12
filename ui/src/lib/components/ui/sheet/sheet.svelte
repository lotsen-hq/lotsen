<script lang="ts">
   import { Dialog as SheetPrimitive } from 'bits-ui';
   import { closeOnMove } from '$lib/panelDocument.svelte';

   let { open = $bindable(false), ...restProps }: SheetPrimitive.RootProps = $props();

   closeOnMove(() => (open = false));
</script>

<SheetPrimitive.Root bind:open {...restProps} />
