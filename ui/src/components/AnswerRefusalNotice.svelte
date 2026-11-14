<!-- The strip under the top bar that shows why the open tour's last send of its answers failed, until the reviewer dismisses it or another send starts. -->
<script lang="ts">
   import CircleAlert from '@lucide/svelte/icons/circle-alert';
   import { Button } from '$lib/components/ui/button';
   import { announcesRefusal, type AnswerSendState } from '../lib/answers';

   let {
      answerSend,
      reasonId,
      ondismiss,
   }: {
      /** The state of the open tour's send of its answers, which the row under a stop's checks shares. */
      answerSend: AnswerSendState;
      /** The id of the element that holds the reason, which Send answers names in its description. */
      reasonId: string;
      /** Clears the refusal at every send control. */
      ondismiss: () => void;
   } = $props();
</script>

{#if answerSend.refusal !== null}
   <div class="flex items-center gap-3 border-b border-l-4 border-l-destructive bg-accent px-4 py-1.5 text-sm text-accent-foreground" data-answer-refusal>
      <CircleAlert class="size-4 shrink-0 text-destructive" aria-hidden="true" />
      <!-- The strip speaks for the top bar's button, so it announces only a send that the button started. -->
      <p id={reasonId} class="min-w-0 text-destructive" role={announcesRefusal(answerSend, 'topBar') ? 'alert' : undefined}>{answerSend.refusal.reason}</p>
      <Button variant="outline" size="sm" class="ml-auto shrink-0" onclick={ondismiss}>Dismiss</Button>
   </div>
{/if}
