<!--
   The form behind Dive…, in the Plan panel for a dive from the whole tour and in the Stop panel for a dive from the open stop: the dive's purpose and familiarity level as radio options, preset
   to the parent tour's, the reviewer's words, Cancel and Send to Claude. A dive from the whole tour needs the words, and a dive from a stop covers the whole stop without them.
-->
<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import * as RadioGroup from '$lib/components/ui/radio-group';
   import { familiarityLabels, familiarityLevels, purposeLabels, purposes } from '../lib/labels';
   import { diveNeedsWords, divePlaceholder, tourActionPrompts, type DiveChoice } from '../lib/tourActions';
   import type { Familiarity, TourPurpose } from '../lib/types';
   import ActionWords from './ActionWords.svelte';

   let {
      from,
      stopTitle,
      unavailable,
      sending,
      onsend,
      onclose,
   }: {
      /** The parent tour's purpose and familiarity level, which the form selects until the reviewer picks others. */
      from: DiveChoice;
      /** The title of the stop that the dive starts from, or null for a dive from the whole tour. */
      stopTitle: string | null;
      /** Why the dive cannot go now, which the page shows above the form, or null when it can. */
      unavailable: string | null;
      /** Whether a dive of the form is on its way to the server. */
      sending: boolean;
      /** Requests the dive with the reviewer's words, and returns null once the server stored it or its reason for a refusal. The page closes the form once the server stored it. */
      onsend: (choice: DiveChoice, text: string) => Promise<string | null>;
      /** Closes the form. */
      onclose: () => void;
   } = $props();

   const id = $props.id();
   let pickedPurpose = $state<TourPurpose | null>(null);
   let pickedLevel = $state<Familiarity | null>(null);
   let text = $state('');
   let refusal = $state<string | null>(null);
   let box = $state<ActionWords>();

   const purpose = $derived(pickedPurpose ?? from.purpose);
   const familiarity = $derived(pickedLevel ?? from.familiarity);
   const needsWords = $derived(stopTitle === null && text.trim() === '');

   /** Hides the server's last refusal, as when the reviewer opens the form again. */
   export function clearRefusal(): void {
      refusal = null;
   }

   /** Moves the focus to the words box. */
   export function focus(): void {
      box?.focus();
   }

   async function send(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (sending || needsWords || unavailable !== null) {
         return;
      }

      refusal = await onsend({ purpose, familiarity }, text);
   }
</script>

<form class="flex flex-col gap-2" onsubmit={send}>
   <p class="text-sm text-muted-foreground">Claude creates a new tour that goes deeper, at the commit of this tour.</p>
   <div class="flex flex-col gap-1">
      <span id="{id}-purpose" class="text-sm text-muted-foreground">Purpose</span>
      <RadioGroup.Root aria-labelledby="{id}-purpose" class="gap-1" bind:value={() => purpose, (next: string) => (pickedPurpose = next as TourPurpose)}>
         {#each purposes as option (option)}
            <div class="flex items-center gap-2">
               <RadioGroup.Item id="{id}-purpose-{option}" value={option} />
               <label for="{id}-purpose-{option}" class="text-sm">{purposeLabels[option]}</label>
            </div>
         {/each}
      </RadioGroup.Root>
   </div>
   <div class="flex flex-col gap-1">
      <span id="{id}-level" class="text-sm text-muted-foreground">Your familiarity</span>
      <RadioGroup.Root aria-labelledby="{id}-level" class="gap-1" bind:value={() => familiarity, (next: string) => (pickedLevel = next as Familiarity)}>
         {#each familiarityLevels as option (option)}
            <div class="flex items-center gap-2">
               <RadioGroup.Item id="{id}-level-{option}" value={option} />
               <label for="{id}-level-{option}" class="text-sm">{familiarityLabels[option]}</label>
            </div>
         {/each}
      </RadioGroup.Root>
   </div>
   <ActionWords bind:this={box} bind:text question={tourActionPrompts.dive} placeholder={divePlaceholder(stopTitle)} />
   {#if needsWords}
      <p id="{id}-needs-words" class="text-xs text-muted-foreground">{diveNeedsWords}</p>
   {/if}
   {#if refusal !== null}
      <p class="text-sm text-destructive" role="alert">{refusal}</p>
   {/if}
   <div class="flex justify-end gap-2">
      <Button type="button" variant="outline" size="sm" onclick={onclose}>Cancel</Button>
      <Button type="submit" size="sm" disabled={needsWords || unavailable !== null} aria-disabled={sending} aria-describedby={needsWords ? `${id}-needs-words` : undefined}>Send to Claude</Button>
   </div>
</form>
