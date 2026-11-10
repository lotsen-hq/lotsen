<script lang="ts" module>
   import { cn } from '$lib/utils';

   /**
    * The look that a toolbar's text toggles and icon toggles share. The cn call puts the classes under the Tailwind lint.
    * A focused toggle rises above the next toggle in its group, which would paint over its focus ring.
    */
   export const toolbarToggleClass = cn('relative rounded-none font-normal text-muted-foreground shadow-none focus-visible:z-10 aria-pressed:bg-accent aria-pressed:text-foreground');
</script>

<script lang="ts">
   import type { Snippet } from 'svelte';
   import { Toggle } from '$lib/components/ui/toggle';

   let { pressed, onclick, compact = false, children }: { pressed: boolean; onclick: () => void; compact?: boolean; children: Snippet } = $props();
</script>

<!-- The parent owns the pressed state: a click calls onclick, and the toggle shows the state the parent then passes in. -->
<Toggle variant="outline" size="sm" bind:pressed={() => pressed, () => onclick()} class={[toolbarToggleClass, compact && 'h-6 px-2 text-xs']}>
   {@render children()}
</Toggle>
