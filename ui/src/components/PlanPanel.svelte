<script lang="ts">
   import ClipboardList from '@lucide/svelte/icons/clipboard-list';
   import ClockAlert from '@lucide/svelte/icons/clock-alert';
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import SkipForward from '@lucide/svelte/icons/skip-forward';
   import type { KnownPlan } from '../lib/tourRecord.svelte';
   import { staleOnOf } from '../lib/stops';
   import { revisedTooltip, skipTooltip, stopKindLabels, stopStatusLabels, unwrittenStopLabel, type Attachment } from '../lib/labels';
   import type { PlanActionKind } from '../lib/tourActions';
   import type { Familiarity, QuestionThread, RequestedTourAction, TourControls } from '../lib/types';
   import PlanActions from './PlanActions.svelte';
   import PlanProposal from './PlanProposal.svelte';
   import StopKindPill from './StopKindPill.svelte';
   import TooltipButton from './TooltipButton.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      tourSelected,
      known,
      error,
      attachment,
      currentStopId = null,
      onopen = () => {},
      summaryShown = false,
      onopensummary = null,
      threads = [],
      controls = null,
      onaction = null,
      onopenthread = () => {},
      skipped = new Set(),
      changed = new Set(),
      onskip = null,
      skipsSaving = false,
      familiarity = null,
      onfamiliarity = null,
      planWaits = false,
      onapprove = null,
   }: {
      tourSelected: boolean;
      known: KnownPlan | undefined;
      error: string | null;
      /** The tour's attachment state, because a pending stop with no session attached has no one to write it. */
      attachment: Attachment;
      /** The ID of the stop the reviewer has open, which the plan marks, or null when none is open. */
      currentStopId?: string | null;
      onopen?: (stopId: string) => void;
      /** Whether the summary page shows, which the plan marks. */
      summaryShown?: boolean;
      /** Opens the summary page, or null when the plan offers no entry for it. */
      onopensummary?: (() => void) | null;
      /** The tour's threads, among which the waiting actions on the plan are. */
      threads?: readonly QuestionThread[];
      /** The tour's controls, which say what the reviewer can do now and which threads wait, or null while the page has none. */
      controls?: TourControls | null;
      /** Requests a tour action on the plan with the reviewer's words, or null when the page offers no action. */
      onaction?: ((action: Extract<RequestedTourAction, { kind: PlanActionKind }>, text: string) => Promise<string | null>) | null;
      onopenthread?: (threadId: string) => void;
      /** The IDs of the stops that the reviewer skipped, which the plan marks. */
      skipped?: ReadonlySet<string>;
      /** The IDs of the stops that changed since the reviewer last opened them, the only revised stops that the plan marks. */
      changed?: ReadonlySet<string>;
      /** Skips a stop or takes its skip back, and returns null once the server stored it or its reason for a refusal, or null when the page offers no skip. */
      onskip?: ((stopId: string, skipped: boolean) => Promise<string | null>) | null;
      /** Whether the page waits for a skip write of the tour from any panel, which Approve waits for. */
      skipsSaving?: boolean;
      /** The tour's familiarity level, or null when the page does not know it. */
      familiarity?: Familiarity | null;
      /** Changes the tour's familiarity level with the reviewer's words, and returns null once the server stored it or its reason for a refusal, or null when the page offers no change. */
      onfamiliarity?: ((level: Familiarity, text: string) => Promise<string | null>) | null;
      /** Whether the plan waits for the reviewer's approval, which shows it as a proposal. */
      planWaits?: boolean;
      /** Approves the waiting plan with the proposed order, level and words, and returns null once the server stored the approval or its reason for a refusal, or null when the page offers no approval. */
      onapprove?: ((stopIds: readonly string[], familiarity: Familiarity, text: string) => Promise<string | null>) | null;
   } = $props();

   // Skips of different stops may save at the same time: the server stores them in turn, and the page keeps the newest skips by the tour's update time.
   let skipping = $state<ReadonlySet<string>>(new Set());
   let refusal = $state<string | null>(null);

   async function toggleSkip(stopId: string): Promise<void> {
      if (onskip === null || skipping.has(stopId)) {
         return;
      }

      skipping = new Set([...skipping, stopId]);
      refusal = null;
      const reason = await onskip(stopId, !skipped.has(stopId));
      skipping = new Set([...skipping].filter((candidate) => candidate !== stopId));
      // Only a new click clears a refusal, so another stop's save that ends later leaves it on screen.
      if (reason !== null) {
         refusal = reason;
      }
   }

   // Stops are numbered across the whole tour, so each chapter's list starts after the stops of the chapters above it.
   const chapters = $derived.by(() => {
      const plan = known?.plan;
      if (!plan) {
         return [];
      }

      let first = 1;
      return plan.chapters.map((chapter) => {
         const stops = plan.stops.filter((stop) => stop.chapter === chapter.id);
         const numbered = { ...chapter, first, stops };
         first += stops.length;
         return numbered;
      });
   });
</script>

{#snippet skipToggle(stopId: string, number: number)}
   {#if onskip !== null}
      {@const isSkipped = skipped.has(stopId)}
      <TooltipButton
         tooltip={skipTooltip(isSkipped)}
         variant={isSkipped ? 'secondary' : 'ghost'}
         size="icon-xs"
         class="skip-toggle"
         aria-label={`Skip stop ${number}`}
         aria-pressed={isSkipped}
         disabled={skipping.has(stopId)}
         onclick={() => toggleSkip(stopId)}
      >
         <SkipForward />
      </TooltipButton>
   {/if}
{/snippet}

{#snippet planList()}
   {#if refusal !== null}
      <p class="px-4 py-1 text-xs text-destructive" role="alert">{refusal}</p>
   {/if}
   <ol class="plan">
      {#each chapters as chapter (chapter.id)}
         <li>
            <h3 class="mt-2 mb-1 px-4 caps-label">{chapter.title}</h3>
            <ol class="mb-2 px-2">
               {#each chapter.stops as stop, index (stop.id)}
                  {@const current = stop.id === currentStopId}
                  {@const writing = stop.status === 'pending'}
                  <!-- With no session attached, no one writes a pending stop. -->
                  {@const spinning = writing && attachment !== 'none'}
                  {@const status = writing && !spinning ? unwrittenStopLabel : stop.status === 'revised' && !changed.has(stop.id) ? null : (stopStatusLabels[stop.status] ?? null)}
                  {@const kind = stopKindLabels[stop.kind] ?? stop.kind}
                  {@const number = chapter.first + index}
                  {@const isSkipped = skipped.has(stop.id)}
                  <li class="stop-item">
                     <button
                        type="button"
                        class={['stop-row', current && 'current', isSkipped && 'skipped-row']}
                        aria-current={current ? 'step' : undefined}
                        aria-label={[
                           `${number} ${kind}: ${stop.title}`,
                           ...(status === null ? [] : [status]),
                           ...(staleOnOf(stop) === undefined ? [] : [`stale since update ${staleOnOf(stop)}`]),
                           ...(isSkipped ? ['skipped'] : []),
                        ].join(', ')}
                        onclick={() => onopen(stop.id)}
                     >
                        <span class="number" data-cell>{number}</span>
                        <span>
                           <!-- No space between the pill and the title: the pill's margin is the gap, and the button's label keeps the two apart. -->
                           <StopKindPill kind={stop.kind} data-cell /><span class={['title', writing && 'writing']} data-cell>{stop.title}</span>
                           {#if status !== null}
                              <WithTooltip text={stop.status === 'revised' ? revisedTooltip : null}>
                                 {#snippet trigger({ props })}
                                    <span {...props} class="status" data-cell>
                                       {#if spinning}
                                          <LoaderCircle class="spinner" aria-hidden="true" />
                                       {:else if stop.status === 'revised'}
                                          <span class="dot" aria-hidden="true"></span>
                                       {/if}
                                       {status}
                                    </span>
                                 {/snippet}
                              </WithTooltip>
                           {/if}
                           {#if staleOnOf(stop) !== undefined}
                              <span class="status stale" data-cell><ClockAlert aria-hidden="true" />Stale since update {staleOnOf(stop)}</span>
                           {/if}
                           {#if isSkipped}
                              <span class="status skipped" data-cell><SkipForward aria-hidden="true" />Skipped</span>
                           {/if}
                        </span>
                     </button>
                     {@render skipToggle(stop.id, number)}
                  </li>
               {/each}
            </ol>
         </li>
      {/each}
      {#if onopensummary !== null && chapters.length > 0}
         <li class="px-2">
            <button type="button" class={['stop-row', summaryShown && 'current']} aria-current={summaryShown ? 'step' : undefined} onclick={onopensummary}>
               <span class="number" data-cell><ClipboardList class="summary-icon" aria-hidden="true" /></span>
               <span class="title" data-cell>Summary</span>
            </button>
         </li>
      {/if}
   </ol>
{/snippet}

{#snippet planOrNotice(hasPlan: boolean)}
   {#if hasPlan}
      {@render planList()}
   {:else}
      <p class="px-4 py-1 text-muted-foreground">Claude has not written the plan yet.</p>
   {/if}
{/snippet}

<div class="py-2">
   {#if !tourSelected}
      <p class="px-4 py-1 text-muted-foreground">Select a tour to see its plan.</p>
   {:else if error}
      <p class="px-4 py-1 text-destructive" role="alert">{error}</p>
   {:else if known === undefined}
      <p class="px-4 py-1 text-muted-foreground">Loading the plan…</p>
   {:else}
      {#if planWaits && known.plan !== null && familiarity !== null && onapprove !== null}
         <PlanProposal plan={known.plan} {familiarity} {controls} {skipsSaving} {skipped} {skipToggle} skipRefusal={refusal} {onapprove} />
      {:else if familiarity !== null}
         <PlanActions plan={known.plan} {familiarity} {threads} {controls} {onaction} {onfamiliarity} {onopenthread}>
            {@render planOrNotice(known.plan !== null)}
         </PlanActions>
      {:else}
         {@render planOrNotice(known.plan !== null)}
      {/if}
   {/if}
</div>
