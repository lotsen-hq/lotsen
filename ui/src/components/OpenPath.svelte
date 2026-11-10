<script lang="ts">
   import WithTooltip from './WithTooltip.svelte';

   let { path }: { path: string } = $props();

   const directory = $derived(path.slice(0, path.lastIndexOf('/') + 1));
   const name = $derived(path.slice(directory.length));
</script>

<!--
   The directory gets only the room the file name leaves, so it gives way before the name, and a name too long for the row gives way too. Each loses its start: right to left, the ellipsis goes on
   the left, and the bdi keeps the text in reading order.
-->
<WithTooltip text={path}>
   {#snippet trigger({ props })}
      <code {...props} class="flex min-w-0 grow whitespace-nowrap"
         ><span dir="rtl" class="w-0 max-w-fit grow truncate"><bdi>{directory}</bdi></span><span dir="rtl" class="min-w-0 truncate"><bdi>{name}</bdi></span></code
      >
   {/snippet}
</WithTooltip>
