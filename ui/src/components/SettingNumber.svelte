<script lang="ts">
   import { untrack } from 'svelte';

   let {
      label,
      description,
      unit,
      value,
      min,
      max,
      step = 1,
      onchange,
   }: {
      label: string;
      description: string;
      /** The unit that follows the field, such as "characters". */
      unit: string;
      value: number;
      min: number;
      max: number;
      step?: number;
      /** Receives each whole number from `min` to `max` that the reviewer enters. */
      onchange: (value: number) => void;
   } = $props();

   const id = $props.id();
   // The field keeps what the reviewer typed, so an invalid entry stays for them to correct while the setting keeps its last valid value. A new value from the parent replaces the text.
   let text = $state('');
   let shownValue: number | null = null;
   $effect.pre(() => {
      if (value !== shownValue) {
         shownValue = value;
         if (untrack(() => Number(text) !== value || text.trim() === '')) {
            text = String(value);
         }
      }
   });
   const valid = $derived(/^\d+$/.test(text.trim()) && Number(text) >= min && Number(text) <= max);

   function input(next: string): void {
      text = next;
      if (valid) {
         onchange(Number(next));
      }
   }
</script>

<div class="flex flex-col gap-2">
   <label for="{id}-input" class="font-medium">{label}</label>
   <p id="{id}-description" class="text-sm text-muted-foreground">{description}</p>
   <div class="flex items-center gap-2">
      <input
         id="{id}-input"
         type="number"
         inputmode="numeric"
         {min}
         {max}
         {step}
         class={['h-8 w-28 rounded-md border bg-transparent px-2 text-sm', valid ? 'border-input focus-visible:border-ring' : 'border-destructive']}
         aria-describedby="{id}-description{valid ? '' : ` ${id}-error`}"
         aria-invalid={!valid}
         value={text}
         oninput={(event) => input(event.currentTarget.value)}
      />
      <span class="text-sm text-muted-foreground">{unit}</span>
   </div>
   {#if !valid}
      <p id="{id}-error" class="text-sm text-destructive" role="alert">Enter a whole number from {min} to {max}.</p>
   {/if}
</div>
