<script lang="ts">
   import { DropdownMenu as DropdownMenuPrimitive } from 'bits-ui';
   import { cn, type WithoutChild } from '$lib/utils.js';

   let { ref = $bindable(null), class: className, children: childrenProp, 'aria-disabled': ariaDisabled, ...restProps }: WithoutChild<DropdownMenuPrimitive.ItemProps> = $props();
</script>

<DropdownMenuPrimitive.Item
   bind:ref
   data-slot="dropdown-menu-item"
   class={cn(
      "relative flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm outline-hidden select-none aria-disabled:opacity-50 data-highlighted:bg-accent data-highlighted:text-accent-foreground data-disabled:pointer-events-none data-disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
      className,
   )}
   {...restProps}
>
   {#snippet child({ props })}
      <!-- bits-ui writes its own aria-disabled over the caller's, so an item that stays focusable while it cannot act passes aria-disabled here. -->
      <div {...props} aria-disabled={ariaDisabled ?? (props['aria-disabled'] as 'true' | 'false' | undefined)}>
         {@render childrenProp?.()}
      </div>
   {/snippet}
</DropdownMenuPrimitive.Item>
