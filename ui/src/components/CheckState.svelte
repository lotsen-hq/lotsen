<!--
   The state of a check's answer, as a pill at the head of the check: the answer state, or that the check needs an answer where the page takes one.
-->
<script lang="ts">
   import { answerStateText } from '../lib/answers';
   import type { CheckAnswer } from '../lib/types';

   let {
      answer,
      boundSessionId,
      answerable,
   }: {
      /** The check's answer, or undefined when the reviewer has not answered it. */
      answer: CheckAnswer | undefined;
      /** The ID of the session bound to the tour, or null when none is. */
      boundSessionId: string | null;
      /** Whether the page takes an answer to the check. */
      answerable: boolean;
   } = $props();

   const stateText = $derived(answer ? answerStateText(answer, boundSessionId) : null);
</script>

{#if answer && stateText}
   <span
      class={['check-state bg-muted', answer.state === 'received' && answer.outdatedOn === undefined ? 'text-success' : 'text-foreground']}
      title={stateText.explanation}
      data-answer-state={answer.outdatedOn === undefined ? answer.state : 'outdated'}>{stateText.label}</span
   >
   <span class="sr-only">{stateText.explanation}</span>
{:else if answerable}
   <span class="check-state waiting">Needs answer</span>
{/if}
