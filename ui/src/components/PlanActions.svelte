<!--
   The top of the Plan panel: a quiet line with the tour's familiarity level, and a ⋯ menu of the plan's actions. Re-plan… opens the form that also changes the level, Reorder… shows the stop
   order editor in place of the plan list, and before Claude writes a plan the menu holds only Change familiarity…. The plan list shows as it is until Claude's set_plan arrives. Each item is
   disabled with the server's reason under the line while the tour's controls close it, and each waiting thread that holds the plan or the level back shows there with a link to it.
-->
<script lang="ts">
   import Ellipsis from '@lucide/svelte/icons/ellipsis';
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import { tick, type Snippet } from 'svelte';
   import { Button, buttonVariants } from '$lib/components/ui/button';
   import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
   import { disabledReason, threadsOf } from '../lib/controls';
   import { familiarityLabels } from '../lib/labels';
   import { isReordered, reconciledOrder } from '../lib/stopOrder';
   import { subjectOf } from '../lib/threadView';
   import { decisionLabels, subjectPending, tourActionLabels, tourActionPlaceholders, tourActionPrompts, type PlanActionKind } from '../lib/tourActions';
   import type { Familiarity, Plan, QuestionThread, RequestedTourAction, RequestState, TourControls } from '../lib/types';
   import ActionWords from './ActionWords.svelte';
   import ReplanForm from './ReplanForm.svelte';
   import StopOrderEditor from './StopOrderEditor.svelte';

   let {
      plan,
      familiarity,
      threads,
      controls,
      onaction,
      onfamiliarity,
      onopenthread,
      children,
   }: {
      /** The tour's plan, or null while Claude has not written one. */
      plan: Plan | null;
      /** The tour's familiarity level. */
      familiarity: Familiarity;
      /** The tour's threads, among which the waiting actions on the plan are. */
      threads: readonly QuestionThread[];
      /** The tour's controls, which say whether each action can go and which threads hold the plan or the level back, or null while the page has none. */
      controls: TourControls | null;
      /** Requests a tour action on the plan with the reviewer's words, and returns null once the server stored it or its reason for a refusal, or null when the page offers no action. */
      onaction: ((action: Extract<RequestedTourAction, { kind: PlanActionKind }>, text: string) => Promise<string | null>) | null;
      /** Changes the tour's familiarity level with the reviewer's words, and returns null once the server stored it or its reason for a refusal, or null when the page offers no change. */
      onfamiliarity: ((level: Familiarity, text: string) => Promise<string | null>) | null;
      onopenthread: (threadId: string) => void;
      /** The plan list, or what shows without a plan, which shows while the reviewer does not edit an order. */
      children: Snippet;
   } = $props();

   const id = $props.id();
   const ended: Partial<Record<RequestState, string>> = {
      declined: 'Claude declined the last change to this level.',
      canceled: 'You canceled the last change to this level.',
      unanswered: 'The last change to this level went unanswered.',
   };

   let chosen = $state<PlanActionKind | null>(null);
   let text = $state('');
   let draft = $state<string[]>([]);
   let sending = $state(false);
   let refusal = $state<string | null>(null);
   let form = $state<ReplanForm>();
   /** Whether the next close of the menu moves the focus into the form that it opened. */
   let focusForm = false;

   const offers = $derived(controls?.offers);
   const levelReason = $derived(disabledReason(offers?.level.change));
   const replanReason = $derived(disabledReason(offers?.replan));
   const reorderReason = $derived(disabledReason(offers?.reorder));
   const askAgain = $derived(offers?.level.askAgain ?? null);
   const formLabel = $derived(plan === null ? `${decisionLabels.familiarityChanged}…` : `${tourActionLabels.replan}…`);
   const items = $derived.by(() => {
      if (plan === null) {
         return onfamiliarity === null ? [] : [{ kind: 'replan' as const, label: formLabel, reason: levelReason }];
      }

      // The form also changes the level, so Re-plan… needs only one of the two offers open.
      return [
         ...(onaction === null && onfamiliarity === null ? [] : [{ kind: 'replan' as const, label: formLabel, reason: levelReason === null ? null : replanReason }]),
         ...(onaction === null ? [] : [{ kind: 'reorder' as const, label: `${tourActionLabels.reorder}…`, reason: reorderReason }]),
      ];
   });
   // Both actions usually share their reason, so the panel shows each reason once.
   const shownReasons = $derived([...new Set(items.flatMap((item) => item.reason ?? []))]);
   // A waiting plan approval or level change is in both lists of the controls.
   const waiting = $derived(threadsOf(threads, [...new Set([...(offers?.pending.level === undefined ? [] : [offers.pending.level]), ...(offers?.pending.plan ?? [])])]));
   const openForm = $derived(chosen === 'reorder' && (onaction === null || reorderReason !== null) ? null : chosen);
   // A plan that adds or drops a stop while the reviewer edits starts the proposal again, because the server refuses an order that is not each stop of the latest plan once.
   const order = $derived(plan === null ? [] : reconciledOrder(draft, plan.stops));
   const orderReady = $derived(plan !== null && isReordered(order, plan.stops));

   function pendingStatus(thread: QuestionThread): string {
      const subject = subjectOf(thread)!;
      const status = subjectPending(subject);
      return subject.kind === 'decision' && subject.decision.kind === 'planApproved' ? `${status} You can change the level once Claude replies.` : status;
   }

   function choose(kind: PlanActionKind): void {
      if (chosen !== kind) {
         text = '';
         draft = plan?.stops.map((stop) => stop.id) ?? [];
      }

      chosen = kind;
      refusal = null;
      focusForm = kind === 'replan';
   }

   // The menu returns the focus to its ⋯ button when it closes, so the form takes the focus only after that, once it has mounted.
   function placeFocus(event: Event): void {
      if (focusForm) {
         event.preventDefault();
         focusForm = false;
         void tick().then(() => form?.focus());
      }
   }

   async function sendOrder(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (sending || !orderReady || openForm !== 'reorder' || onaction === null) {
         return;
      }

      sending = true;
      refusal = await onaction({ kind: 'reorder', stopIds: order }, text);
      sending = false;
      if (refusal === null) {
         chosen = null;
         text = '';
      }
   }
</script>

<section class="flex flex-col gap-1 border-b border-border px-4 pb-2" aria-label="Plan actions">
   <div class="flex min-h-7 items-center justify-between gap-2">
      <p class="text-xs text-muted-foreground">{familiarityLabels[familiarity]}</p>
      {#if items.length > 0}
         <DropdownMenu.Root>
            <DropdownMenu.Trigger class="{buttonVariants({ variant: 'ghost', size: 'icon-sm' })} shrink-0" aria-label="Plan actions">
               <Ellipsis aria-hidden="true" />
            </DropdownMenu.Trigger>
            <DropdownMenu.Content align="end" class="w-44" onCloseAutoFocus={placeFocus}>
               {#each items as item (item.label)}
                  <DropdownMenu.Item
                     disabled={item.reason !== null}
                     aria-describedby={item.reason === null ? undefined : `${id}-unavailable-${shownReasons.indexOf(item.reason)}`}
                     onSelect={() => choose(item.kind)}>{item.label}</DropdownMenu.Item
                  >
               {/each}
            </DropdownMenu.Content>
         </DropdownMenu.Root>
      {/if}
   </div>
   {#each shownReasons as reason, index (reason)}
      <p id="{id}-unavailable-{index}" class="text-xs text-muted-foreground">{reason}</p>
   {/each}
   {#if askAgain !== null && levelReason === null}
      <p class="text-xs text-muted-foreground">{ended[askAgain] ?? ''} To ask Claude again, open {formLabel} in the ⋯ menu and send it at this level.</p>
   {/if}
   {#each waiting as thread (thread.id)}
      <div class="flex items-start gap-2 text-sm text-muted-foreground">
         <LoaderCircle class="mt-0.5 size-4 shrink-0 animate-spin" aria-hidden="true" />
         <div>
            <p role="status">{pendingStatus(thread)}</p>
            <Button variant="link" size="sm" class="h-auto px-0" onclick={() => onopenthread(thread.id)}>Show the thread</Button>
         </div>
      </div>
   {/each}
   {#if openForm === 'replan'}
      <ReplanForm
         bind:this={form}
         {familiarity}
         askAgain={askAgain !== null}
         {levelReason}
         {replanReason}
         shownAbove={shownReasons}
         onreplan={plan === null || onaction === null ? null : (words) => onaction({ kind: 'replan' }, words)}
         onlevel={onfamiliarity}
         onclose={() => (chosen = null)}
      />
   {:else if openForm === 'reorder' && plan !== null}
      <form class="flex flex-col gap-2" onsubmit={sendOrder}>
         <p class="text-sm text-muted-foreground">Move the stops into the order you propose. Claude keeps a stop after the stops it relies on and says where its order differs.</p>
         <div class="-mx-4">
            <StopOrderEditor stops={plan.stops} chapters={plan.chapters} {order} onchange={(next) => (draft = next)} />
         </div>
         <ActionWords bind:text question={tourActionPrompts.reorder} placeholder={tourActionPlaceholders.reorder} />
         {#if refusal !== null}
            <p class="text-sm text-destructive" role="alert">{refusal}</p>
         {/if}
         <div class="flex justify-end gap-2">
            <Button type="button" variant="outline" size="sm" onclick={() => (chosen = null)}>Cancel</Button>
            <Button type="submit" size="sm" disabled={!orderReady} aria-disabled={sending}>Send to Claude</Button>
         </div>
      </form>
   {/if}
</section>
{#if openForm !== 'reorder'}
   {@render children()}
{/if}
