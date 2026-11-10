<!--
   The three familiarity levels as one button each, with the given level pressed. The parent owns the level: a press of another level calls onchange, and the picker shows the level that the
   parent then passes in.
-->
<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import { familiarityLabels, familiarityLevels, familiarityShortLabels } from '../lib/labels';
   import type { Familiarity } from '../lib/types';

   let {
      value,
      onchange,
      label = 'Familiarity',
   }: {
      value: Familiarity;
      onchange: (level: Familiarity) => void;
      /** The visible name of the group. */
      label?: string;
   } = $props();

   const id = $props.id();
</script>

<div class="flex flex-wrap items-center gap-x-3 gap-y-1">
   <span id="{id}-label" class="text-sm text-muted-foreground">{label}</span>
   <div role="group" aria-labelledby="{id}-label" class="flex gap-1">
      {#each familiarityLevels as level (level)}
         <Button
            variant={level === value ? 'default' : 'outline'}
            size="sm"
            aria-pressed={level === value}
            aria-label={familiarityLabels[level]}
            onclick={() => {
               if (level !== value) {
                  onchange(level);
               }
            }}>{familiarityShortLabels[level]}</Button
         >
      {/each}
   </div>
</div>
