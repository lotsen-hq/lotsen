<script lang="ts">
   import { ContextMenu as ContextMenuPrimitive } from 'bits-ui';
   import { locksScroll, panelDocument } from '$lib/panelDocument.svelte';
   import { cn } from '$lib/utils.js';

   let { ref = $bindable(null), preventScroll, class: className, ...restProps }: ContextMenuPrimitive.ContentProps = $props();

   const home = panelDocument();
</script>

<ContextMenuPrimitive.Portal>
   <ContextMenuPrimitive.Content
      bind:ref
      preventScroll={preventScroll ?? locksScroll(home)}
      data-slot="context-menu-content"
      class={cn(
         'z-50 max-h-(--bits-context-menu-content-available-height) min-w-32 origin-(--bits-context-menu-content-transform-origin) overflow-x-hidden overflow-y-auto rounded-md bg-popover p-1 text-sm text-popover-foreground shadow-md ring-1 ring-foreground/10 outline-hidden duration-100 data-open:animate-in data-open:fade-in-0 data-open:zoom-in-95 data-closed:animate-out data-closed:fade-out-0 data-closed:zoom-out-95',
         className,
      )}
      {...restProps}
   />
</ContextMenuPrimitive.Portal>
