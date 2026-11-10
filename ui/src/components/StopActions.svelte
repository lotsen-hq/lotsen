<!--
   The tour actions of the open stop in the Stop panel: Explain deeper with the reviewer's optional words, Revise with the words it needs, and the actions of the stop that wait for Claude, each with a link
   to its thread. Each button is disabled with the server's reason while the tour's controls close it, and absent where the page offers no action.
-->
<script lang="ts">
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import { tick } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import { disabledReason, stopOffers, threadsOf } from '../lib/controls';
   import { actionOf } from '../lib/threadView';
   import { tourActionLabels, tourActionPending, tourActionPlaceholders, tourActionPrompts, type StopActionKind } from '../lib/tourActions';
   import type { QuestionThread, TourControls } from '../lib/types';
   import ActionWords from './ActionWords.svelte';

   let {
      stopId,
      threads,
      controls,
      onaction,
      onopenthread,
   }: {
      stopId: string;
      /** The tour's threads, among which the stop's waiting actions are. */
      threads: readonly QuestionThread[];
      /** The tour's controls, which say whether each action can go and which threads of the stop wait, or null while the page has none. */
      controls: TourControls | null;
      /** Requests a tour action about the stop with the reviewer's words, and returns null once the server stored it or its reason for a refusal, or null when the page offers no action. */
      onaction: ((kind: StopActionKind, text: string) => Promise<string | null>) | null;
      onopenthread: (threadId: string) => void;
   } = $props();

   const id = $props.id();
   const kinds: readonly StopActionKind[] = ['explain', 'revise'];
   let chosen = $state<StopActionKind | null>(null);
   let text = $state('');
   let sending = $state(false);
   let refusal = $state<string | null>(null);
   let box = $state<ActionWords>();

   const offers = $derived(stopOffers(controls, stopId));
   const reasons = $derived({ explain: disabledReason(offers?.explain), revise: disabledReason(offers?.revise) });
   // Both actions usually share their reason, such as an unattached session, so the panel shows each reason once.
   const shownReasons = $derived([...new Set(kinds.map((kind) => reasons[kind]).filter((reason) => reason !== null))]);
   const waiting = $derived(threadsOf(threads, controls?.offers.pending.stops[stopId]));
   const ready = $derived(chosen !== 'revise' || text.trim() !== '');

   async function choose(kind: StopActionKind): Promise<void> {
      chosen = chosen === kind ? null : kind;
      refusal = null;
      await tick();
      box?.focus();
   }

   async function send(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (sending || !ready || chosen === null || onaction === null) {
         return;
      }

      sending = true;
      refusal = await onaction(chosen, text);
      sending = false;
      if (refusal === null) {
         chosen = null;
         text = '';
      }
   }
</script>

<section class="mt-3 flex flex-col gap-2" aria-label="Tour actions">
   {#if onaction !== null}
      <div class="flex flex-wrap items-center gap-2">
         {#each kinds as kind (kind)}
            <Button
               variant={chosen === kind ? 'secondary' : 'outline'}
               size="sm"
               disabled={reasons[kind] !== null}
               aria-expanded={chosen === kind}
               aria-describedby={reasons[kind] === null ? undefined : `${id}-unavailable-${shownReasons.indexOf(reasons[kind])}`}
               onclick={() => choose(kind)}>{tourActionLabels[kind]}</Button
            >
         {/each}
      </div>
      {#each shownReasons as reason, index (reason)}
         <p id="{id}-unavailable-{index}" class="text-xs text-muted-foreground">{reason}</p>
      {/each}
      {#if chosen !== null && reasons[chosen] === null}
         <form class="flex flex-col gap-2" onsubmit={send}>
            <ActionWords bind:this={box} bind:text question={tourActionPrompts[chosen]} placeholder={tourActionPlaceholders[chosen]} />
            {#if refusal !== null}
               <p class="text-sm text-destructive" role="alert">{refusal}</p>
            {/if}
            <div class="flex justify-end gap-2">
               <Button type="button" variant="outline" size="sm" onclick={() => (chosen = null)}>Cancel</Button>
               <Button type="submit" size="sm" disabled={!ready} aria-disabled={sending}>Send to Claude</Button>
            </div>
         </form>
      {/if}
   {/if}
   {#each waiting as thread (thread.id)}
      {@const kind = actionOf(thread)!.kind}
      <div class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
         <LoaderCircle class="size-4 shrink-0 animate-spin" aria-hidden="true" />
         <p role="status">{tourActionPending[kind]}</p>
         <Button variant="link" size="sm" class="h-auto px-0" onclick={() => onopenthread(thread.id)}>Show the thread</Button>
      </div>
   {/each}
</section>
