<script lang="ts">
   import { DropdownMenu as DropdownMenuPrimitive } from 'bits-ui';
   import { cn } from '$lib/utils.js';

   let { ref = $bindable(null), class: className, ...restProps }: DropdownMenuPrimitive.GroupHeadingProps = $props();
</script>

<DropdownMenuPrimitive.GroupHeading
   bind:ref
   data-slot="dropdown-menu-group-heading"
   class={cn('px-2 pt-1.5 pb-0.5 text-xs font-semibold tracking-wide text-muted-foreground uppercase', className)}
   {...restProps}
/>
