<!-- The choice of the stop version that a revised stop shows: the current one, or an earlier one. -->
<script lang="ts">
   import type { KnownVersion } from '../lib/versions';

   let {
      versions,
      current,
      shown,
      onopenversion,
   }: {
      /** The stop's earlier versions, oldest first. */
      versions: readonly KnownVersion[];
      /** The number of the stop's current version. */
      current: number;
      /** The number of the version the stop shows. */
      shown: number;
      onopenversion: (number: number) => void;
   } = $props();

   const numbers = $derived([...versions.map((version) => version.number), current]);
</script>

<select
   aria-label="Stop version"
   class="h-7 rounded-md border border-input bg-background px-2 text-xs focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden"
   value={String(shown)}
   onchange={(event) => onopenversion(Number(event.currentTarget.value))}
>
   {#each numbers as number (number)}
      <option value={String(number)}>{number === current ? `Version ${number}, current` : `Version ${number}`}</option>
   {/each}
</select>
