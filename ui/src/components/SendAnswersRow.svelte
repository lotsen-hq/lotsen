<!--
   The row at the end of a stop's checks, which says how many of the tour's answers wait and sends them as one batch, the same send as the top bar's.
-->
<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import { announcesRefusal, type AnswerSend } from '../lib/answers';
   import { disabledReason } from '../lib/controls';

   let { send }: { send: AnswerSend } = $props();

   const id = $props.id();
   const unavailable = $derived(disabledReason(send.offer));
   const refusal = $derived(send.state.refusal?.reason ?? null);

   function start(): void {
      if (unavailable === null) {
         void send.onsend();
      }
   }
</script>

<div class="mt-3 flex flex-wrap items-center gap-x-3 gap-y-1 border-t pt-2" data-send-row>
   <p class="text-sm text-muted-foreground">{send.unsent === 1 ? '1 answer waits to be sent.' : `${send.unsent} answers wait to be sent.`}</p>
   <Button
      variant="outline"
      size="sm"
      class="ml-auto shrink-0"
      disabled={unavailable !== null}
      aria-disabled={send.state.sending}
      aria-describedby={[unavailable === null ? null : `${id}-unavailable`, refusal === null ? null : `${id}-refusal`].filter((part) => part !== null).join(' ') || undefined}
      onclick={start}>Send answers</Button
   >
   {#if unavailable !== null}
      <p id="{id}-unavailable" class="w-full text-sm text-muted-foreground">{unavailable}</p>
   {/if}
   {#if refusal !== null}
      <p id="{id}-refusal" class="w-full text-sm text-destructive" role={announcesRefusal(send.state, 'sendRow') ? 'alert' : undefined}>{refusal}</p>
   {/if}
</div>
