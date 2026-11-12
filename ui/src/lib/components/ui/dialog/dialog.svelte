<script lang="ts">
   import { Dialog as DialogPrimitive } from 'bits-ui';
   import { closeOnMove } from '$lib/panelDocument.svelte';

   let { open = $bindable(false), ...restProps }: DialogPrimitive.RootProps = $props();

   closeOnMove(() => (open = false));
</script>

<DialogPrimitive.Root bind:open {...restProps} />
