<!--
   The chip that chooses the type of a note, a sent thread, or the question or note that the ask box writes next: no type, or one of the answer types that a check answer's chip offers. It
   shows the owner's value again after each pick, so a pick that the owner refuses or drops leaves the chip on the type that the owner holds.
-->
<script lang="ts">
   import { answerTypes } from '../lib/answers';
   import type { AnswerType } from '../lib/types';

   let {
      value,
      onpick,
      small = false,
      label = 'Note type',
   }: {
      /** The thread's type, or null for a thread without one. */
      value: AnswerType | null;
      /** Takes the reviewer's pick, or null for no type. */
      onpick: (type: AnswerType | null) => Promise<unknown> | void;
      /** Whether the chip takes the size of the ask box's other chips, or else that of the buttons beside it. */
      small?: boolean;
      /** The chip's visible label. */
      label?: string;
   } = $props();

   const id = $props.id();

   async function pick(chip: HTMLSelectElement): Promise<void> {
      const type = answerTypes.find((option) => option.type === chip.value)?.type ?? null;
      if (type !== value) {
         await onpick(type);
      }

      // `value={...}` sets the chip only when the value changes, so a refused, failed or dropped pick needs the value restored here.
      chip.value = value ?? '';
   }
</script>

<span class="inline-flex min-w-0 items-center gap-1.5">
   <label for="{id}-type" class={small ? 'shrink-0' : 'shrink-0 text-sm'}>{label}</label>
   <select
      id="{id}-type"
      class="max-w-full min-w-0 rounded-full border border-input bg-background font-medium text-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden {small
         ? 'h-6 px-2 text-xs'
         : 'h-8 px-3 text-sm'}"
      value={value ?? ''}
      onchange={(event) => void pick(event.currentTarget)}
   >
      <option value="">No type</option>
      {#each answerTypes as option (option.type)}
         <option value={option.type}>{option.label}</option>
      {/each}
   </select>
</span>
