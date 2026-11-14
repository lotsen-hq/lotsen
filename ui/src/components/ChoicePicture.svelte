<script lang="ts">
   import type { MessageStyle } from '../lib/messageStyle';
   import type { PlacementMode } from '../lib/placement';

   let { picture }: { picture: PlacementMode | MessageStyle } = $props();
</script>

<!-- A sketch of a placement mode or a message style: gray bars for code, a primary bar for an explanation's title, and the Stop panel at the right. -->
<svg viewBox={picture === 'bubbles' || picture === 'fullWidth' ? '0 0 100 50' : '0 0 100 56'} aria-hidden="true" class="block h-auto w-full rounded-md border bg-background">
   {#if picture === 'side' || picture === 'mixed'}
      <rect class="fill-muted" x="66" y="0" width="34" height="56" />
      <rect class="fill-primary/75" x="71" y="8" width="22" height="3" rx="1.5" />
      <rect class="fill-muted-foreground/60" x="71" y="15" width="24" height="2.5" rx="1.25" />
      <rect class="fill-muted-foreground/60" x="71" y="21" width="20" height="2.5" rx="1.25" />
   {/if}
   {#if picture === 'side'}
      {#each [[6, 8, 40], [10, 15, 48], [10, 22, 34], [6, 29, 44], [10, 36, 30], [6, 43, 38]] as [x, y, width] (y)}
         <rect class="fill-muted-foreground/45" {x} {y} {width} height="3" rx="1.5" />
      {/each}
   {:else if picture === 'inline'}
      {#each [[6, 6, 44], [10, 12, 56], [10, 41, 50], [6, 47, 36]] as [x, y, width] (y)}
         <rect class="fill-muted-foreground/45" {x} {y} {width} height="3" rx="1.5" />
      {/each}
      <rect class="fill-primary/15 stroke-primary/50" x="6" y="19" width="88" height="17" rx="3" stroke-width="0.8" />
      <rect class="fill-primary/75" x="11" y="23" width="26" height="2.5" rx="1.25" />
      <rect class="fill-muted-foreground/60" x="11" y="29" width="70" height="2.5" rx="1.25" />
   {:else if picture === 'mixed'}
      {#each [[6, 6, 40], [10, 12, 46], [10, 35, 44], [6, 41, 32], [10, 47, 40]] as [x, y, width] (y)}
         <rect class="fill-muted-foreground/45" {x} {y} {width} height="3" rx="1.5" />
      {/each}
      <rect class="fill-primary/15 stroke-primary/50" x="6" y="19" width="54" height="11" rx="3" stroke-width="0.8" />
      <rect class="fill-muted-foreground/60" x="10" y="23.5" width="40" height="2.5" rx="1.25" />
   {:else if picture === 'bubbles'}
      <rect class="fill-primary" x="44" y="6" width="50" height="15" rx="7" />
      <rect class="fill-primary-foreground/80" x="51" y="12" width="36" height="2.5" rx="1.25" />
      <rect class="fill-muted" x="6" y="26" width="62" height="18" rx="7" />
      <rect class="fill-muted-foreground/60" x="13" y="31" width="46" height="2.5" rx="1.25" />
      <rect class="fill-muted-foreground/60" x="13" y="37" width="34" height="2.5" rx="1.25" />
   {:else}
      <circle class="fill-primary" cx="10" cy="10" r="4" />
      <rect class="fill-muted-foreground/60" x="18" y="7" width="20" height="2.5" rx="1.25" />
      <rect class="fill-muted-foreground/60" x="18" y="12.5" width="74" height="2.5" rx="1.25" />
      <line class="stroke-border" x1="6" y1="24" x2="94" y2="24" />
      <circle class="fill-muted-foreground" cx="10" cy="32" r="4" />
      <rect class="fill-muted-foreground/60" x="18" y="29" width="16" height="2.5" rx="1.25" />
      <rect class="fill-muted-foreground/60" x="18" y="34.5" width="74" height="2.5" rx="1.25" />
      <rect class="fill-muted-foreground/60" x="18" y="40" width="52" height="2.5" rx="1.25" />
   {/if}
</svg>
