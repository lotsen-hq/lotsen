<script lang="ts" generics="T extends string">
   import * as RadioGroup from '$lib/components/ui/radio-group';

   let {
      label,
      description,
      options,
      value,
      onchange,
   }: {
      label: string;
      description?: string;
      options: readonly { value: T; label: string }[];
      value: T;
      onchange: (value: T) => void;
   } = $props();

   const id = $props.id();

   function choose(next: string) {
      const option = options.find((candidate) => candidate.value === next);
      if (option !== undefined) {
         onchange(option.value);
      }
   }
</script>

<!-- The parent owns the value: a choice calls onchange, and the group shows the value the parent then passes in. -->
<div class="flex flex-col gap-2">
   <span id="{id}-label" class="font-medium">{label}</span>
   {#if description}
      <p id="{id}-description" class="text-sm text-muted-foreground">{description}</p>
   {/if}
   <RadioGroup.Root aria-labelledby="{id}-label" aria-describedby={description ? `${id}-description` : undefined} class="gap-2" bind:value={() => value, choose}>
      {#each options as option (option.value)}
         <div class="flex items-center gap-2">
            <RadioGroup.Item id="{id}-{option.value}" value={option.value} />
            <label for="{id}-{option.value}">{option.label}</label>
         </div>
      {/each}
   </RadioGroup.Root>
</div>
