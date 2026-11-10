<!--
   A button of the summary page that requests one of its tour actions, Summarize or a follow-up action, with the reviewer's optional words. It says why when it is disabled, and shows the server's
   reason when it refuses the request. While an action of its kind waits, it shows what Claude is doing, with a link to the action's thread.
-->
<script lang="ts">
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import { tick } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import { tourActionLabels, tourActionPending, tourActionPlaceholders, tourActionPrompts } from '../lib/tourActions';
   import type { FollowUpKind, QuestionThread } from '../lib/types';
   import ActionWords from './ActionWords.svelte';

   let {
      kind,
      unavailable,
      waiting,
      onrequest,
      onopenthread,
   }: {
      kind: 'summarize' | FollowUpKind;
      /** Why the reviewer cannot request the action now, or null when the button works. */
      unavailable: string | null;
      /** The thread of the action of this kind that waits for Claude, or null when none waits. */
      waiting: QuestionThread | null;
      /** Requests the action with the reviewer's words, and returns null once the server stored the request or its reason for a refusal. */
      onrequest: (text: string) => Promise<string | null>;
      onopenthread: (threadId: string) => void;
   } = $props();

   const id = $props.id();
   let opened = $state(false);
   let text = $state('');
   let sending = $state(false);
   let refusal = $state<string | null>(null);
   let box = $state<ActionWords>();

   const open = $derived(opened && unavailable === null);

   async function toggle(): Promise<void> {
      opened = !open;
      refusal = null;
      if (opened) {
         await tick();
         box?.focus();
      }
   }

   async function send(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (sending || !open) {
         return;
      }

      sending = true;
      try {
         refusal = await onrequest(text);
      } finally {
         sending = false;
      }

      if (refusal === null) {
         opened = false;
         text = '';
      }
   }
</script>

<div class="flex min-w-0 flex-col gap-2">
   <div class="flex flex-wrap items-center gap-2">
      <Button
         variant={open ? 'secondary' : 'outline'}
         size="sm"
         class="h-auto min-h-8 max-w-full text-left whitespace-normal"
         disabled={unavailable !== null}
         aria-expanded={open}
         aria-describedby={unavailable === null ? undefined : `${id}-unavailable`}
         onclick={toggle}>{tourActionLabels[kind]}</Button
      >
   </div>
   {#if unavailable !== null}
      <p id="{id}-unavailable" class="text-xs text-muted-foreground">{unavailable}</p>
   {/if}
   {#if waiting !== null}
      <div class="flex items-start gap-2 text-sm text-muted-foreground">
         <LoaderCircle class="mt-0.5 size-4 shrink-0 animate-spin" aria-hidden="true" />
         <div>
            <p role="status">{tourActionPending[kind]}</p>
            <Button variant="link" size="sm" class="h-auto px-0" onclick={() => onopenthread(waiting.id)}>Show the thread</Button>
         </div>
      </div>
   {/if}
   {#if open}
      <form class="flex flex-col gap-2" onsubmit={send}>
         <ActionWords bind:this={box} bind:text question={tourActionPrompts[kind]} placeholder={tourActionPlaceholders[kind]} />
         {#if refusal !== null}
            <p class="text-sm text-destructive" role="alert">{refusal}</p>
         {/if}
         <div class="flex justify-end gap-2">
            <Button type="button" variant="outline" size="sm" onclick={() => (opened = false)}>Cancel</Button>
            <Button type="submit" size="sm" aria-disabled={sending} aria-busy={sending}>
               {#if sending}
                  <LoaderCircle class="size-4 animate-spin" aria-hidden="true" />
               {/if}
               Send to Claude
            </Button>
         </div>
      </form>
   {/if}
</div>
