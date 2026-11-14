<script lang="ts" generics="T extends string">
   import { RadioGroup } from 'bits-ui';
   import type { Snippet } from 'svelte';
   import { cn } from '$lib/utils.js';

   type Option = { value: T; label: string };

   let {
      label,
      description,
      options,
      value,
      onchange,
      appearance = 'segments',
      inline = false,
      hints,
      item,
   }: {
      label: string;
      description?: string;
      options: readonly Option[];
      value: T;
      onchange: (value: T) => void;
      /** Whether the options show as a row of segments or as a row of cards. */
      appearance?: 'segments' | 'cards';
      /** Whether the label and the segments share one row. */
      inline?: boolean;
      /** A sentence about each option. The line below the options shows the selected option's sentence. */
      hints?: Readonly<Record<T, string>>;
      /** Renders an option in place of its label. It must render the label's text, because that text names the option. */
      item?: Snippet<[Option]>;
   } = $props();

   const id = $props.id();
   const describedBy = $derived([description && `${id}-description`, hints && `${id}-hint`].filter(Boolean).join(' ') || undefined);

   function choose(next: string) {
      const option = options.find((candidate) => candidate.value === next);
      if (option !== undefined) {
         onchange(option.value);
      }
   }
</script>

<!-- The parent owns the value: a choice calls onchange, and the group shows the value the parent then passes in. -->
<div class={cn('flex gap-2', inline ? 'flex-wrap items-center justify-between' : 'flex-col')}>
   <span id="{id}-label" class="font-medium">{label}</span>
   {#if description}
      <p id="{id}-description" class="text-sm text-muted-foreground">{description}</p>
   {/if}
   <RadioGroup.Root
      aria-labelledby="{id}-label"
      aria-describedby={describedBy}
      orientation="horizontal"
      class={appearance === 'segments' ? 'flex gap-0.5 rounded-lg bg-muted p-0.75' : 'grid auto-cols-fr grid-flow-col gap-2'}
      bind:value={() => value, choose}
   >
      {#each options as option (option.value)}
         <RadioGroup.Item
            value={option.value}
            class={cn(
               'outline-hidden focus-visible:ring-3 focus-visible:ring-ring/50',
               appearance === 'segments'
                  ? 'flex flex-1 items-center justify-center gap-1.5 rounded-md px-2.5 py-1 font-medium whitespace-nowrap text-muted-foreground hover:text-foreground data-checked:bg-popover data-checked:text-foreground data-checked:shadow-sm data-checked:ring-1 data-checked:ring-border'
                  : 'flex flex-col gap-1.5 rounded-lg border p-1.5 text-left hover:border-input data-checked:border-primary data-checked:ring-1 data-checked:ring-primary',
            )}
         >
            {#if item}
               {@render item(option)}
            {:else}
               {option.label}
            {/if}
         </RadioGroup.Item>
      {/each}
   </RadioGroup.Root>
   {#if hints}
      <p id="{id}-hint" class="text-sm text-muted-foreground">{hints[value]}</p>
   {/if}
</div>
