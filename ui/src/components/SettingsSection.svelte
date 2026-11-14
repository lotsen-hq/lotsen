<script lang="ts">
   import type { Snippet } from 'svelte';

   let { title, children }: { title: string; children: Snippet } = $props();

   const id = $props.id();
</script>

<!-- The group role keeps the section out of the page's landmarks, where a panel such as Threads already holds the name. -->
<div role="group" aria-labelledby="{id}-title" class="flex flex-col gap-5 border-t py-5 first:border-t-0 first:pt-1">
   <h3 id="{id}-title" class="text-xs font-semibold tracking-wider text-muted-foreground uppercase">{title}</h3>
   {@render children()}
</div>
