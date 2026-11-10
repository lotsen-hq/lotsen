<!--
   The box for the reviewer's words of a tour action or a plan approval, with the question that labels it. The words of most actions are optional, so the label carries an Optional tag and the
   empty box says what Claude does without words.
-->
<script lang="ts">
   import { sendOnModifiedEnter } from '../lib/dom';
   import { maxQuestionLength } from '../lib/threadView';

   let {
      text = $bindable(''),
      question,
      placeholder,
   }: {
      /** The reviewer's words. */
      text?: string;
      /** The question that labels the box. */
      question: string;
      /** What the empty box says Claude does without words, or null when the action needs words. The box then has no Optional tag. */
      placeholder: string | null;
   } = $props();

   const id = $props.id();
   let field = $state<HTMLTextAreaElement>();

   /** Moves the focus to the box. */
   export function focus(): void {
      field?.focus();
   }
</script>

<label for={id} class="text-sm">
   {question}
   {#if placeholder !== null}
      <span class="rounded-sm bg-muted px-1.5 text-xs text-muted-foreground">Optional</span>
   {/if}
</label>
<textarea
   bind:this={field}
   bind:value={text}
   {id}
   {placeholder}
   rows="2"
   maxlength={maxQuestionLength}
   class="min-h-12 w-full resize-y rounded-md border border-input bg-transparent px-3 py-2 text-sm shadow-xs placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden dark:bg-input/30"
   onkeydown={sendOnModifiedEnter}></textarea>
