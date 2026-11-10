<!--
   The reviewer's answer to one check. An unanswered check offers the suggestions to pick from and a box for the reviewer's own words. An answered check shows the answer with its type as a chip that the reviewer can change, and Edit brings the suggestions and the box back.
   The suggestions show without their types, because type labels made their purpose unclear. A suggestion that the reviewer picks gives the answer its type, and the chip shows it.
   Without writes, it shows the answer and the suggestions as text. While it stays mounted, it keeps an edit in progress, and keeps the words typed for an unanswered check until they become the answer or Edit replaces them.
-->
<script lang="ts">
   import { tick } from 'svelte';
   import Check from '@lucide/svelte/icons/check';
   import { Button } from '$lib/components/ui/button';
   import { answerStateText, answerTypes, type CheckAnswerWrites } from '../lib/answers';
   import { sendOnModifiedEnter } from '../lib/dom';
   import { maxQuestionLength } from '../lib/threadView';
   import type { CheckAnswer, StopCheck } from '../lib/types';

   let {
      check,
      number,
      answer,
      boundSessionId = null,
      writes,
   }: {
      check: StopCheck;
      /** The check's number in the stop, from 1, which names the check to a screen reader. */
      number: number;
      /** The check's answer, or undefined when the reviewer has not answered it. */
      answer: CheckAnswer | undefined;
      /** The ID of the session bound to the tour, or null when none is. */
      boundSessionId?: string | null;
      /** The writes of the answer, or null when the check takes no answers here. */
      writes: CheckAnswerWrites | null;
   } = $props();

   const id = $props.id();
   const stateText = $derived(answer ? answerStateText(answer, boundSessionId) : null);
   let text = $state('');
   let refusal = $state<string | null>(null);
   let busy = $state(false);
   let editing = $state(false);
   let box = $state<HTMLTextAreaElement | null>(null);
   let editButton = $state<HTMLElement | null>(null);
   let group = $state<HTMLElement | null>(null);
   const hintShown = $derived(writes !== null && answer === undefined);

   // An answer that another view clears ends the edit, so that a later answer stays collapsed.
   $effect.pre(() => {
      if (editing && answer === undefined) {
         editing = false;
         text = '';
         refusal = null;
      }
   });

   // Runs one write at a time, and shows the server's reason when it refuses one.
   async function run(write: () => Promise<string | null>): Promise<boolean> {
      if (busy) {
         return false;
      }

      busy = true;
      try {
         refusal = await write();
         return refusal === null;
      } finally {
         busy = false;
      }
   }

   async function startEdit(current: CheckAnswer): Promise<void> {
      if (busy) {
         return;
      }

      text = current.text;
      refusal = null;
      editing = true;
      await tick();
      box?.focus();
   }

   // Collapsing to the answer removes the control that held focus. Focus that the reviewer moved elsewhere on the page stays there.
   async function focusEdit(): Promise<void> {
      await tick();
      const active = document.activeElement;
      if (active === document.body || group?.contains(active)) {
         editButton?.focus();
      }
   }

   async function finishEdit(): Promise<void> {
      editing = false;
      text = '';
      refusal = null;
      await focusEdit();
   }

   function cancelEdit(): void {
      // A write in flight can still be refused, and the box must hold the refused words then.
      if (!busy) {
         void finishEdit();
      }
   }

   async function pick(writes: CheckAnswerWrites, suggestion: string): Promise<void> {
      if (busy) {
         return;
      }

      // Picking the answer again would give it back the suggestion's type, which the reviewer may have changed on purpose.
      const settled = answer?.text === suggestion || (await run(() => writes.onanswer({ text: suggestion, fromSuggestion: true })));
      if (settled) {
         await (editing ? finishEdit() : focusEdit());
      }
   }

   async function write(writes: CheckAnswerWrites, event: SubmitEvent): Promise<void> {
      event.preventDefault();
      const words = text;
      if (words.trim() !== '' && (await run(() => writes.onanswer({ text: words })))) {
         text = '';
         await focusEdit();
      }
   }

   const singleSpaced = (words: string) => words.trim().replace(/\s+/g, ' ');

   // Words that differ only in whitespace write nothing, because a write makes a sent or received answer a draft again.
   async function save(writes: CheckAnswerWrites, event: SubmitEvent): Promise<void> {
      event.preventDefault();
      const words = text;
      if (busy || words.trim() === '') {
         return;
      }

      if (singleSpaced(words) === singleSpaced(answer?.text ?? '') || (await run(() => writes.onanswer({ text: words })))) {
         await finishEdit();
      }
   }

   async function setType(writes: CheckAnswerWrites, chip: HTMLSelectElement): Promise<void> {
      const type = answerTypes.find((option) => option.type === chip.value)?.type;
      if (type !== undefined && type !== answer?.type) {
         await run(() => writes.onsettype(type));
      }

      // `value={answer.type}` sets the chip only when the stored type changes, so a refused, failed or dropped change needs the stored type restored here.
      if (answer) {
         chip.value = answer.type;
      }
   }
</script>

{#snippet typeAndState(current: CheckAnswer)}
   {#if writes === null}
      <span class="rounded-full border border-input px-2 text-xs font-medium">{answerTypes.find((option) => option.type === current.type)?.label}</span>
   {:else}
      {@const chipWrites = writes}
      <label for="{id}-type" class="sr-only">Answer type</label>
      <select
         id="{id}-type"
         class="h-6 rounded-full border border-input bg-background px-2 text-xs font-medium focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden"
         value={current.type}
         onchange={(event) => void setType(chipWrites, event.currentTarget)}
      >
         {#each answerTypes as option (option.type)}
            <option value={option.type}>{option.label}</option>
         {/each}
      </select>
   {/if}
   <span
      class={['rounded-sm bg-muted px-1.5 text-xs', current.state === 'received' && current.outdatedOn === undefined ? 'text-success' : 'text-muted-foreground']}
      title={stateText?.explanation}
      data-answer-state={current.outdatedOn === undefined ? current.state : 'outdated'}>{stateText?.label}</span
   >
   <span class="sr-only">{stateText?.explanation}</span>
{/snippet}

{#snippet suggestionPicks(pickWrites: CheckAnswerWrites)}
   {#if check.suggestions?.length}
      <div class="suggestions">
         <span class="caps-label" aria-hidden="true">Suggested answers</span>
         <ul aria-label="Suggested answers">
            {#each check.suggestions as suggestion, position (position)}
               {@const chosen = answer?.text === suggestion.text}
               <!-- One line, so the item's text is the suggestion's text alone. -->
               <!-- prettier-ignore -->
               <li><button type="button" class={['suggestion', chosen && 'chosen']} aria-pressed={chosen} aria-disabled={busy} onclick={() => void pick(pickWrites, suggestion.text)}>{#if chosen}<Check class="size-3.5" aria-hidden="true" />{/if}{suggestion.text}</button></li>
            {/each}
         </ul>
      </div>
   {/if}
{/snippet}

{#snippet ownWords()}
   <label for="{id}-own" class="sr-only">Your own answer</label>
   <textarea
      bind:this={box}
      bind:value={text}
      id="{id}-own"
      rows="2"
      maxlength={maxQuestionLength}
      placeholder="Write your own answer"
      class="min-h-10 w-full resize-y rounded-md border border-input bg-transparent px-3 py-1.5 text-sm shadow-xs placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden dark:bg-input/30"
      onkeydown={sendOnModifiedEnter}></textarea>
{/snippet}

<div bind:this={group} class="answer" role="group" aria-label="Your answer to check {number}" aria-describedby={hintShown ? `${id}-hint` : undefined}>
   <span class="caps-label" aria-hidden="true">Your answer</span>
   {#if answer && writes !== null && editing}
      {@const editWrites = writes}
      <form class="chosen-answer" onsubmit={(event) => save(editWrites, event)}>
         {@render ownWords()}
         <div class="flex flex-wrap items-center gap-2">
            {@render typeAndState(answer)}
         </div>
         {@render suggestionPicks(editWrites)}
         <div class="flex justify-end gap-2">
            <Button variant="outline" size="sm" aria-disabled={busy} onclick={cancelEdit}>Cancel</Button>
            <Button type="submit" variant="outline" size="sm" disabled={text.trim() === ''} aria-disabled={busy}>Save</Button>
         </div>
      </form>
   {:else if answer}
      {@const shown = answer}
      <div class="chosen-answer">
         <p class="whitespace-pre-wrap">{shown.text}</p>
         <div class="flex flex-wrap items-center gap-2">
            {@render typeAndState(shown)}
            {#if writes !== null}
               <Button variant="outline" size="sm" class="ml-auto" aria-disabled={busy} bind:ref={editButton} onclick={() => void startEdit(shown)}>Edit</Button>
               <Button variant="outline" size="sm" aria-disabled={busy} onclick={() => void run(writes.onclear)}>Clear</Button>
            {/if}
         </div>
      </div>
   {:else if writes === null}
      <p class="text-sm text-muted-foreground">Not answered.</p>
   {/if}
   {#if writes === null}
      {#if check.suggestions?.length}
         <div class="suggestions">
            <span class="caps-label" aria-hidden="true">Suggested answers</span>
            <ul aria-label="Suggested answers">
               {#each check.suggestions as suggestion, position (position)}
                  <li>{suggestion.text}</li>
               {/each}
            </ul>
         </div>
      {/if}
   {:else}
      {#if !answer}
         {@const formWrites = writes}
         <p id="{id}-hint" class="text-sm text-muted-foreground">Pick a suggested answer or write your own.</p>
         {@render suggestionPicks(formWrites)}
         <form class="flex flex-col gap-1.5" onsubmit={(event) => write(formWrites, event)}>
            {@render ownWords()}
            <div class="flex justify-end">
               <Button type="submit" variant="outline" size="sm" disabled={text.trim() === ''} aria-disabled={busy}>Answer</Button>
            </div>
         </form>
      {/if}
      {#if refusal !== null}
         <p class="text-sm text-destructive" role="alert">{refusal}</p>
      {/if}
   {/if}
</div>
