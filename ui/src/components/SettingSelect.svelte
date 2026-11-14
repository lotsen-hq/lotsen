<script lang="ts" generics="T extends string">
   import type { Snippet } from 'svelte';
   import * as Select from '$lib/components/ui/select';

   type Option = { value: T; label: string };

   let {
      label,
      options,
      value,
      onchange,
      selected,
      item,
   }: {
      label: string;
      options: readonly Option[];
      value: T;
      onchange: (value: T) => void;
      /** Renders the selected option on the trigger. */
      selected: Snippet<[Option]>;
      /** Renders an option in the list. It must render the label's text, because that text names the option. */
      item: Snippet<[Option]>;
   } = $props();

   const id = $props.id();
   const current = $derived(options.find((option) => option.value === value) ?? options[0]!);

   function choose(next: string) {
      const option = options.find((candidate) => candidate.value === next);
      if (option !== undefined) {
         onchange(option.value);
      }
   }
</script>

<!-- The parent owns the value, as in SettingChoice. -->
<div class="flex flex-col gap-2">
   <span id="{id}-label" class="font-medium">{label}</span>
   <Select.Root type="single" items={[...options]} bind:value={() => value, choose}>
      <Select.Trigger aria-labelledby="{id}-label" class="w-full">
         <span class="flex min-w-0 flex-1 items-center gap-2">{@render selected(current)}</span>
      </Select.Trigger>
      <Select.Content>
         {#each options as option (option.value)}
            <Select.Item value={option.value} label={option.label}>
               {@render item(option)}
            </Select.Item>
         {/each}
      </Select.Content>
   </Select.Root>
</div>
