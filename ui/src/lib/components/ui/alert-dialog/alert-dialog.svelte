<script lang="ts">
   import { AlertDialog as AlertDialogPrimitive } from 'bits-ui';
   import { closeOnMove } from '$lib/panelDocument.svelte';

   let { open = $bindable(false), ...restProps }: AlertDialogPrimitive.RootProps = $props();

   closeOnMove(() => (open = false));
</script>

<AlertDialogPrimitive.Root bind:open {...restProps} />
