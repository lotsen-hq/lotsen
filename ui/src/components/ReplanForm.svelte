<!--
   The form behind Re-plan… and Change familiarity…: the familiarity level as radio options, the reviewer's words when the form takes them, Cancel and Send to Claude. Nothing goes to Claude
   until Send, and `planFormSend` decides what it sends.
-->
<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import * as RadioGroup from '$lib/components/ui/radio-group';
   import { familiarityLabels, familiarityLevels } from '../lib/labels';
   import { planFormSend } from '../lib/planForm';
   import { levelChangePlaceholder, levelChangePrompt, tourActionPlaceholders, tourActionPrompts } from '../lib/tourActions';
   import type { Familiarity } from '../lib/types';
   import ActionWords from './ActionWords.svelte';

   let {
      familiarity,
      askAgain,
      levelReason,
      replanReason,
      shownAbove,
      onreplan,
      onlevel,
      onclose,
   }: {
      /** The tour's familiarity level. */
      familiarity: Familiarity;
      /** Whether the tour's current level asks Claude again, which the controls say. */
      askAgain: boolean;
      /** Why the level cannot change now, which disables the radio options and shows as the options' description, or null when it can. */
      levelReason: string | null;
      /** Why a replan cannot go now, which shows once it holds a send back, or null when it can. */
      replanReason: string | null;
      /** The reasons that the page already shows above the form, which the form does not repeat. */
      shownAbove: readonly string[];
      /** Requests a replan with the reviewer's words, and returns null once the server stored it or its reason for a refusal, or null when the form takes no words. */
      onreplan: ((text: string) => Promise<string | null>) | null;
      /** Changes the level with the reviewer's words, and returns null once the server stored it or its reason for a refusal, or null when the page offers no change. */
      onlevel: ((level: Familiarity, text: string) => Promise<string | null>) | null;
      /** Closes the form. */
      onclose: () => void;
   } = $props();

   const id = $props.id();
   let selected = $state<Familiarity | null>(null);
   let text = $state('');
   let sending = $state(false);
   let refusal = $state<string | null>(null);
   let box = $state<ActionWords>();
   const radios = $state<Record<Familiarity, HTMLElement | null>>({ familiar: null, some: null, new: null });

   // The selection follows the tour's level until the reviewer picks one, so a level from another page counts as unchanged.
   const level = $derived(selected ?? familiarity);
   const changed = $derived(level !== familiarity);
   const decision = $derived(planFormSend({ current: familiarity, selected: level, words: text, askAgain, replanReason, levelReason }));
   const levelNote = $derived(levelReason !== null && !shownAbove.includes(levelReason) ? levelReason : null);
   const sendNote = $derived.by(() => {
      const reason = decision.send === null ? decision.reason : null;
      return reason !== null && reason !== levelNote && !shownAbove.includes(reason) ? reason : null;
   });

   /** Moves the focus to the words box, or to the selected level when the form takes no words. */
   export function focus(): void {
      if (box !== undefined) {
         box.focus();
      } else {
         radios[level]?.focus();
      }
   }

   function choose(next: string): void {
      selected = next as Familiarity;
   }

   async function send(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (sending || decision.send === null) {
         return;
      }

      const write = decision.send === 'replan' ? onreplan?.(text) : onlevel?.(decision.level, text);
      if (write === undefined) {
         return;
      }

      sending = true;
      refusal = await write;
      sending = false;
      if (refusal === null) {
         onclose();
      }
   }
</script>

<form class="flex flex-col gap-2" onsubmit={send}>
   <div class="flex flex-col gap-1">
      <span id="{id}-label" class="text-sm text-muted-foreground">Your familiarity</span>
      <RadioGroup.Root aria-labelledby="{id}-label" aria-describedby={levelNote === null ? undefined : `${id}-level`} class="gap-1" disabled={levelReason !== null} bind:value={() => level, choose}>
         {#each familiarityLevels as option (option)}
            <div class="flex items-center gap-2">
               <RadioGroup.Item id="{id}-{option}" value={option} bind:ref={radios[option]} />
               <label for="{id}-{option}" class="text-sm">
                  {familiarityLabels[option]}{option === familiarity && askAgain ? ', ask Claude again' : ''}
                  {#if option === familiarity}
                     <span class="rounded-sm bg-muted px-1.5 text-xs text-muted-foreground">now</span>
                  {/if}
               </label>
            </div>
         {/each}
      </RadioGroup.Root>
      {#if levelNote !== null}
         <p id="{id}-level" class="text-xs text-muted-foreground">{levelNote}</p>
      {/if}
   </div>
   {#if changed}
      <p class="text-sm text-muted-foreground">Claude rewrites the stops after the one you view, or every stop when none is open, and may add or drop context stops. This can take a while.</p>
   {/if}
   {#if onreplan !== null}
      <ActionWords bind:this={box} bind:text question={changed ? levelChangePrompt : tourActionPrompts.replan} placeholder={changed ? levelChangePlaceholder : tourActionPlaceholders.replan} />
   {/if}
   {#if sendNote !== null}
      <p class="text-xs text-muted-foreground">{sendNote}</p>
   {/if}
   {#if refusal !== null}
      <p class="text-sm text-destructive" role="alert">{refusal}</p>
   {/if}
   <div class="flex justify-end gap-2">
      <Button type="button" variant="outline" size="sm" onclick={onclose}>Cancel</Button>
      <Button type="submit" size="sm" disabled={decision.send === null} aria-disabled={sending}>Send to Claude</Button>
   </div>
</form>
