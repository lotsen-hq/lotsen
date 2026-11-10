<script lang="ts">
   import CheckIcon from '@lucide/svelte/icons/check';
   import { DropdownMenu as DropdownMenuPrimitive } from 'bits-ui';
   import { cn, type WithoutChild } from '$lib/utils.js';

   let {
      ref = $bindable(null),
      checked = $bindable(false),
      class: className,
      children: childrenProp,
      'aria-disabled': ariaDisabled,
      ...restProps
   }: WithoutChild<DropdownMenuPrimitive.CheckboxItemProps> = $props();
</script>

<DropdownMenuPrimitive.CheckboxItem
   bind:ref
   bind:checked
   data-slot="dropdown-menu-checkbox-item"
   class={cn(
      "relative flex cursor-default items-center gap-2 rounded-sm py-1.5 pr-2 pl-8 text-sm outline-hidden select-none aria-disabled:opacity-50 data-highlighted:bg-accent data-highlighted:text-accent-foreground data-disabled:pointer-events-none data-disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
      className,
   )}
   {...restProps}
>
   {#snippet child({ props, checked, indeterminate })}
      <!-- bits-ui writes its own aria-disabled over the caller's, so an item that stays focusable while it cannot change passes aria-disabled here. -->
      <div {...props} aria-disabled={ariaDisabled ?? (props['aria-disabled'] as 'true' | 'false' | undefined)}>
         <span class="pointer-events-none absolute left-2 flex size-4 items-center justify-center">
            {#if checked}
               <CheckIcon aria-hidden="true" />
            {/if}
         </span>
         {@render childrenProp?.({ checked, indeterminate })}
      </div>
   {/snippet}
</DropdownMenuPrimitive.CheckboxItem>
