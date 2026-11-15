<!--
   A plan that waits for the reviewer's approval, as a proposal in the Plan panel: the familiarity level and a proposed order of the stops, which the reviewer changes here, the reviewer's
   words for Claude, and Approve, which sends them with the stops that the reviewer skips. The stop order editor shows from the start, with each row's skip toggle, and a row opens no stop
   because Claude has written none. Approve is disabled with the server's reason while the tour's controls close it, and while a skip saves. A request for the focus moves it to Approve, or to
   the proposal while Approve is disabled.
-->
<script lang="ts">
   import { tick, untrack, type Snippet } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import { disabledReason } from '../lib/controls';
   import { mainPage, panelDocument } from '../lib/panelDocument.svelte';
   import { skipBeforeApprovalNote } from '../lib/labels';
   import { isReordered, reconciledOrder } from '../lib/stopOrder';
   import { planApprovalPlaceholder, planApprovalPrompt } from '../lib/tourActions';
   import type { Familiarity, Plan, TourControls } from '../lib/types';
   import ActionWords from './ActionWords.svelte';
   import FamiliarityPicker from './FamiliarityPicker.svelte';
   import StopOrderEditor from './StopOrderEditor.svelte';

   let {
      plan,
      familiarity,
      controls,
      skipsSaving,
      skipped,
      skipToggle,
      skipRefusal,
      onapprove,
      focusRequests = 0,
   }: {
      plan: Plan;
      /** The tour's familiarity level, from which the proposed level starts. */
      familiarity: Familiarity;
      /** The tour's controls, which say whether the plan takes the approval, or null while the page has none. */
      controls: TourControls | null;
      /** Whether the page waits for a skip write of the tour from any panel, which Approve waits for, as `LotsenApp.selectedSkipsSaving` says. */
      skipsSaving: boolean;
      /** The IDs of the stops that the reviewer skipped, which the rows mark. */
      skipped: ReadonlySet<string>;
      /** The skip toggle of the stop with the ID, at the row's current number. */
      skipToggle: Snippet<[stopId: string, number: number]>;
      /** The server's reason for the refusal of the last skip, or null when it stored the skip. */
      skipRefusal: string | null;
      /** Approves the plan with the proposed order, level and words, and returns null once the server stored the approval or its reason for a refusal. */
      onapprove: (stopIds: readonly string[], familiarity: Familiarity, text: string) => Promise<string | null>;
      /** The number of requests that the Plan panel take the focus, which grows with each, as `WorkspaceLayout.focusRequests` counts them. */
      focusRequests?: number;
   } = $props();

   const id = $props.id();
   let draft = $state<string[]>([]);
   let level = $state<Familiarity | null>(null);
   let text = $state('');
   let sending = $state(false);
   let refusal = $state<string | null>(null);
   let region = $state<HTMLElement | null>(null);
   let approveButton = $state<HTMLButtonElement | null>(null);
   const home = panelDocument();

   // A request made before the proposal mounted was for another view of the panel. A request comes with the reveal of a panel that may still be hidden, so the focus moves once it shows.
   let followed = untrack(() => focusRequests);
   $effect(() => {
      if (focusRequests !== followed) {
         followed = focusRequests;
         // A popout window stays behind the main window unless asked forward, and a browser may refuse.
         if (home.window !== mainPage.window) {
            home.window.focus();
         }
         void tick().then(() => home.window.requestAnimationFrame(() => (approveButton?.disabled === false ? approveButton : region)?.focus()));
      }
   });

   const unavailable = $derived(disabledReason(controls?.offers.approvePlan));
   // A plan that adds or drops a stop while the reviewer edits starts the order again, because the server refuses an order that is not each stop of the latest plan once.
   const order = $derived(reconciledOrder(draft, plan.stops));
   const reordered = $derived(isReordered(order, plan.stops));
   const proposedLevel = $derived(level ?? familiarity);

   async function approve(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (sending || skipsSaving || unavailable !== null) {
         return;
      }

      sending = true;
      refusal = await onapprove(order, proposedLevel, text);
      sending = false;
   }
</script>

<section bind:this={region} class="flex flex-col gap-2 px-4 py-1 outline-hidden" aria-label="Plan proposal" tabindex="-1">
   <p class="text-sm">Claude proposes this plan and writes no stop until you approve it. Change it here, skip stops in the list, then approve. {skipBeforeApprovalNote}</p>
   <FamiliarityPicker value={proposedLevel} onchange={(picked) => (level = picked)} label="Your familiarity" />
   <form class="flex flex-col gap-2" onsubmit={approve}>
      {#if skipRefusal !== null}
         <p class="text-xs text-destructive" role="alert">{skipRefusal}</p>
      {/if}
      <div class="-mx-4">
         <StopOrderEditor stops={plan.stops} chapters={plan.chapters} {order} onchange={(next) => (draft = next)} {skipped} {skipToggle} />
      </div>
      {#if reordered}
         <p class="text-xs text-muted-foreground">Claude gets your order of the stops with the approval.</p>
      {/if}
      <ActionWords bind:text question={planApprovalPrompt} placeholder={planApprovalPlaceholder} />
      {#if refusal !== null}
         <p class="text-sm text-destructive" role="alert">{refusal}</p>
      {/if}
      <div class="flex justify-end">
         <Button
            bind:ref={approveButton}
            type="submit"
            size="sm"
            disabled={unavailable !== null || skipsSaving}
            aria-disabled={sending}
            aria-describedby={unavailable === null ? undefined : `${id}-unavailable`}>Approve plan</Button
         >
      </div>
      {#if unavailable !== null}
         <p id="{id}-unavailable" class="text-xs text-muted-foreground">{unavailable}</p>
      {/if}
   </form>
</section>
