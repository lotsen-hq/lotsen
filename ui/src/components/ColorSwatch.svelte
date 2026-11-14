<script lang="ts">
   import type { ColorTheme } from '../lib/colorThemes';

   let { family, size }: { family: ColorTheme; size: 'small' | 'large' } = $props();
</script>

<!-- data-color-swatch gives the element the family's palette whatever family the page shows (color-themes.css). -->
<span data-color-swatch={family} aria-hidden="true" class="flex shrink-0 overflow-hidden rounded-sm border bg-background {size === 'small' ? 'h-4.5 w-7' : 'h-9 w-16'}">
   {#if size === 'small'}
      <span class="w-1/3 border-r bg-card"></span>
      <span class="flex-1"></span>
      <span class="w-1/4 bg-primary"></span>
   {:else}
      <span class="flex w-1/3 flex-col gap-0.75 border-r bg-card px-1 py-1.25">
         <span class="h-0.5 rounded-full bg-muted-foreground/50"></span>
         <span class="h-0.5 w-2/3 rounded-full bg-muted-foreground/50"></span>
         <span class="h-0.5 rounded-full bg-muted-foreground/50"></span>
      </span>
      <span class="flex flex-1 flex-col gap-0.75 p-1.25">
         <span class="h-0.5 w-4/5 rounded-full bg-foreground"></span>
         <span class="h-0.5 w-3/5 rounded-full bg-muted-foreground/70"></span>
         <span class="mt-auto h-1.5 w-2/5 rounded-full bg-primary"></span>
      </span>
   {/if}
</span>
