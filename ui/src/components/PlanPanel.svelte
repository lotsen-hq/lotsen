<script lang="ts">
   import Check from '@lucide/svelte/icons/check';
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import ChevronRight from '@lucide/svelte/icons/chevron-right';
   import ClipboardList from '@lucide/svelte/icons/clipboard-list';
   import ClockAlert from '@lucide/svelte/icons/clock-alert';
   import CopyCheck from '@lucide/svelte/icons/copy-check';
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import SkipForward from '@lucide/svelte/icons/skip-forward';
   import { doneClick, type DoneClick } from '../lib/doneClick';
   import type { DoneState, KnownPlan } from '../lib/tourRecord.svelte';
   import { staleOnOf } from '../lib/stops';
   import { doneCheckboxLabel, doneRowLabel, doneTooltip, revisedTooltip, skipTooltip, stopKindLabels, stopStatusLabels, unwrittenStopLabel, type Attachment } from '../lib/labels';
   import type { PlanActionKind } from '../lib/tourActions';
   import type { Chapter, Familiarity, QuestionThread, RequestedTourAction, Stop, TourControls } from '../lib/types';
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
      done = new Map(),
      doneCount = 0,
      onsetdone = null,
      viewed = null,
      familiarity = null,
      onfamiliarity = null,
      planWaits = false,
      onapprove = null,
      folded = new Set(),
      onfold = null,
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
      /** The done state of each stop with a done mark, by stop ID. A stop that it lacks is not done. */
      done?: ReadonlyMap<string, DoneState>;
      /** The number of the plan's stops whose done mark covers their current version. */
      doneCount?: number;
      /** Marks a version of a stop done, or takes the mark back for null, and returns null once the server stored it or its reason for a refusal, or null when the page offers no done mark. */
      onsetdone?: ((stopId: string, version: number | null) => Promise<string | null>) | null;
      /** The stop under view and the number of the version that the page shows of it, which its done checkbox targets, or null when no written stop is under view. */
      viewed?: { stopId: string; version: number } | null;
      /** The tour's familiarity level, or null when the page does not know it. */
      familiarity?: Familiarity | null;
      /** Changes the tour's familiarity level with the reviewer's words, and returns null once the server stored it or its reason for a refusal, or null when the page offers no change. */
      onfamiliarity?: ((level: Familiarity, text: string) => Promise<string | null>) | null;
      /** Whether the plan waits for the reviewer's approval, which shows it as a proposal. */
      planWaits?: boolean;
      /** Approves the waiting plan with the proposed order, level and words, and returns null once the server stored the approval or its reason for a refusal, or null when the page offers no approval. */
      onapprove?: ((stopIds: readonly string[], familiarity: Familiarity, text: string) => Promise<string | null>) | null;
      /** The IDs of the plan's parts that the reviewer folded, which show only their headings. */
      folded?: ReadonlySet<string>;
      /** Folds a part by its ID, or unfolds it, or null when the page offers no folds, which shows every part unfolded with a plain heading. */
      onfold?: ((partId: string, folded: boolean) => void) | null;
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

   let marking = $state<ReadonlySet<string>>(new Set());

   async function toggleDone(stopId: string, click: DoneClick): Promise<void> {
      if (onsetdone === null || marking.has(stopId)) {
         return;
      }

      marking = new Set([...marking, stopId]);
      refusal = null;
      const reason = await onsetdone(stopId, click.clears ? null : click.version);
      marking = new Set([...marking].filter((candidate) => candidate !== stopId));
      if (reason !== null) {
         refusal = reason;
      }
   }

   type NumberedChapter = Chapter & { first: number; stops: Stop[] };

   // Stops are numbered across the whole tour, so each chapter's list starts after the stops of the chapters above it, those of folded parts included.
   const chapters = $derived.by((): NumberedChapter[] => {
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

   // Each part with its chapters, or null for a plan without parts.
   const parts = $derived(known?.plan?.parts?.map((part) => ({ ...part, chapters: chapters.filter((chapter) => chapter.part === part.id) })) ?? null);
</script>

{#snippet skipToggle(stopId: string, number: number)}
   {#if onskip !== null}
      {@const isSkipped = skipped.has(stopId)}
      <TooltipButton
         tooltip={skipTooltip(isSkipped)}
         variant={isSkipped ? 'secondary' : 'ghost'}
         size="icon-xs"
         class="skip-toggle text-base"
         aria-label={`Skip stop ${number}`}
         aria-pressed={isSkipped}
         disabled={skipping.has(stopId)}
         onclick={() => toggleSkip(stopId)}
      >
         <!-- The skip and done buttons take the title's text size, and their icons scale with it, so the two match. -->
         <SkipForward class="size-glyph" />
      </TooltipButton>
   {/if}
{/snippet}

<!-- A pending stop has nothing to read yet, so it gets no checkbox. The checked state follows the current version, so done on an earlier version is unchecked and its label names the version. -->
{#snippet doneToggle(stop: Stop, number: number)}
   {#if onsetdone !== null && stop.stage === 'written'}
      {@const doneState = done.get(stop.id) ?? { state: 'none' }}
      {@const click = doneClick(doneState, stop.version, viewed?.stopId === stop.id ? viewed.version : null)}
      <TooltipButton
         tooltip={doneTooltip(doneState, click)}
         variant="ghost"
         size="icon-xs"
         class={`done-toggle text-base ${doneState.state === 'done' ? 'checked' : doneState.state === 'earlier' ? 'earlier' : ''}`}
         role="checkbox"
         aria-label={doneCheckboxLabel(number, doneState, click)}
         aria-checked={doneState.state === 'done'}
         disabled={marking.has(stop.id)}
         onclick={() => toggleDone(stop.id, click)}
      >
         {#if doneState.state === 'earlier'}<CopyCheck class="size-glyph" />{:else}<Check class="size-glyph" />{/if}
      </TooltipButton>
   {/if}
{/snippet}

<!-- A chapter of the plan with its stops. In a plan with parts, its heading sits one level below its part's heading. -->
{#snippet chapterItem(chapter: NumberedChapter, heading: 'h3' | 'h4')}
   <li>
      <svelte:element this={heading} class="mt-2 mb-1 px-4 caps-label">{chapter.title}</svelte:element>
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
            {@const doneLabel = doneRowLabel(done.get(stop.id) ?? { state: 'none' })}
            <!-- The row keeps room only for the controls that show without the pointer: a skip, and a done mark on any version. -->
            {@const kept = (onskip !== null && isSkipped ? 1 : 0) + (onsetdone !== null && doneLabel !== null ? 1 : 0)}
            <li class="stop-item">
               <button
                  type="button"
                  class={['stop-row', current && 'current', isSkipped && 'skipped-row', kept === 1 && 'keeps-one', kept === 2 && 'keeps-two']}
                  aria-current={current ? 'step' : undefined}
                  aria-label={[
                     `${number} ${kind}: ${stop.title}`,
                     ...(status === null ? [] : [status]),
                     ...(staleOnOf(stop) === undefined ? [] : [`stale since update ${staleOnOf(stop)}`]),
                     ...(isSkipped ? ['skipped'] : []),
                     ...(doneLabel === null ? [] : [doneLabel]),
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
               <span class="stop-actions">
                  {@render doneToggle(stop, number)}
                  {@render skipToggle(stop.id, number)}
               </span>
            </li>
         {/each}
      </ol>
   </li>
{/snippet}

{#snippet planList()}
   {#if onsetdone !== null && known?.plan}
      <p class="px-4 pb-1 text-xs text-muted-foreground">{doneCount} of {known.plan.stops.length} done</p>
   {/if}
   {#if refusal !== null}
      <p class="px-4 py-1 text-xs text-destructive" role="alert">{refusal}</p>
   {/if}
   <ol class="plan">
      {#if parts === null}
         {#each chapters as chapter (chapter.id)}
            {@render chapterItem(chapter, 'h3')}
         {/each}
      {:else}
         {#each parts as part (part.id)}
            {@const isFolded = onfold !== null && folded.has(part.id)}
            <li>
               {#if onfold === null}
                  <h3 class="part-heading">{part.title}</h3>
               {:else}
                  <h3>
                     <button type="button" class="part-heading" aria-expanded={!isFolded} onclick={() => onfold(part.id, !isFolded)}>
                        {#if isFolded}<ChevronRight aria-hidden="true" />{:else}<ChevronDown aria-hidden="true" />{/if}
                        {part.title}
                     </button>
                  </h3>
               {/if}
               {#if !isFolded}
                  <ol>
                     {#each part.chapters as chapter (chapter.id)}
                        {@render chapterItem(chapter, 'h4')}
                     {/each}
                  </ol>
               {/if}
            </li>
         {/each}
      {/if}
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
