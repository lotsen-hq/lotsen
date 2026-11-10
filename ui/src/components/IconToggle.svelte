<script lang="ts">
   import type { LucideIcon } from '@lucide/svelte';
   import { Toggle } from '$lib/components/ui/toggle';
   import { toolbarToggleClass } from './ToolbarToggle.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      label,
      icon: Icon,
      pressed,
      onclick,
      ref = $bindable(null),
   }: {
      /** The toggle's accessible name and its tooltip. */
      label: string;
      icon: LucideIcon;
      pressed: boolean;
      onclick: () => void;
      ref?: HTMLElement | null;
   } = $props();
</script>

<!-- As in ToolbarToggle, the parent owns the pressed state. -->
<WithTooltip text={label}>
   {#snippet trigger({ props })}
      <Toggle {...props} bind:ref variant="outline" size="sm" aria-label={label} bind:pressed={() => pressed, () => onclick()} class={[toolbarToggleClass, 'w-8 px-0']}>
         <Icon />
      </Toggle>
   {/snippet}
</WithTooltip>
