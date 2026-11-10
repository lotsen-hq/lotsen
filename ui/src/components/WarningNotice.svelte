<!-- A boxed notice that warns the reviewer, with an amber bar and fill and a warning mark before its text. The caller gives it its role, its name and any outer margin. -->
<script lang="ts">
   import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
   import type { Snippet } from 'svelte';
   import type { ClassValue } from 'svelte/elements';

   let {
      role,
      label,
      class: className,
      children,
   }: {
      role: 'note' | 'status';
      /** The notice's accessible name, or undefined when its role needs none. */
      label?: string;
      class?: ClassValue;
      children: Snippet;
   } = $props();
</script>

<div {role} aria-label={label} class={['flex items-start gap-2 rounded-md border border-l-4 border-l-warning bg-warning-surface px-3 py-2 text-sm text-foreground', className]}>
   <TriangleAlert aria-hidden="true" class="mt-0.5 size-4 shrink-0 text-warning-mark" />
   <p class="min-w-0">{@render children()}</p>
</div>
