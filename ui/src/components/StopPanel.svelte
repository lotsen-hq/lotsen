<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import type { ExplanationView } from '../lib/explanationView.svelte';
   import { panelDocument } from '../lib/panelDocument.svelte';
   import { primaryPassage } from '../lib/placement';
   import { stopThreads } from '../lib/threadView';
   import type { StopActionKind } from '../lib/tourActions';
   import { staleNoticeText, versionNoticeText } from '../lib/stopNotices';
   import type { CodeReference, Stop, TourControls } from '../lib/types';
   import type { DoneState } from '../lib/tourRecord.svelte';
   import type { KnownVersion } from '../lib/versions';
   import ExplanationBlock from './ExplanationBlock.svelte';
   import StopActions from './StopActions.svelte';
   import StopKindPill from './StopKindPill.svelte';
   import StopPassages from './StopPassages.svelte';
   import StopStaleNotice from './StopStaleNotice.svelte';
   import StopStickyBar from './StopStickyBar.svelte';
   import StopToolbar from './StopToolbar.svelte';
   import StopVersionBanner from './StopVersionBanner.svelte';
   import ThreadMarkers from './ThreadMarkers.svelte';

   let {
      stop,
      view,
      onopenpassage,
      onaction = null,
      controls = null,
      versions = [],
      current,
      onopenversion = () => {},
      skipped = false,
      onskip = null,
      done = { state: 'none' },
      onsetdone = null,
      planWaits = false,
   }: {
      /** The stop at the version that the panel shows, which is the stop of the explanation. */
      stop: Stop;
      /** The explanation of the stop, which the panel shows while it does not show inline. */
      view: ExplanationView;
      /** Opens a passage. */
      onopenpassage: (passage: CodeReference) => void;
      /** Requests a tour action about the stop with the reviewer's words, or null when the page offers no action. */
      onaction?: ((kind: StopActionKind, text: string) => Promise<string | null>) | null;
      /** The tour's controls, which say what the reviewer can do now and which threads wait, or null while the page has none. */
      controls?: TourControls | null;
      /** The stop's earlier versions, oldest first. */
      versions?: readonly KnownVersion[];
      /** The number of the stop's current version, or undefined while it is pending. */
      current?: number;
      /** Shows a version of the stop by its number. */
      onopenversion?: (number: number) => void;
      /** Whether the reviewer skipped the stop. */
      skipped?: boolean;
      /** Skips the stop or takes its skip back, and returns null once the server stored it or its reason for a refusal, or null when the page offers no skip. */
      onskip?: ((skipped: boolean) => Promise<string | null>) | null;
      /** The stop's done state, which compares the reviewer's done mark with the stop's current version. */
      done?: DoneState;
      /** Marks a version of the stop done, or takes the mark back for null, and returns null once the server stored it or its reason for a refusal, or null when the page offers no done mark. */
      onsetdone?: ((version: number | null) => Promise<string | null>) | null;
      /** Whether the tour's plan waits for the reviewer's approval, so Claude writes no stop yet. */
      planWaits?: boolean;
   } = $props();

   let actions = $state<StopActions>();
   let sending = $state(false);

   const primary = $derived(primaryPassage(stop));
   const inline = $derived(view.host === 'inline');
   // The panel stands in for an inline explanation until an editor shows its primary passage.
   const fallback = $derived(view.placement === 'inline' && !inline);
   const move = $derived(view.placement === 'side' ? { label: 'Show inline', to: 'inline' as const } : { label: fallback ? 'Keep here' : 'Show here', to: 'side' as const });
   // An inline block that an editor shows has a banner and a stale notice of its own.
   const shownVersion = $derived(inline ? null : view.shownVersion);

   let barHeight = $state(0);
   const home = panelDocument();
   let headerGone = $state(false);

   // Whatever scrolls into view in the panel stops 8 pixels below the bar. The CSP blocks style attributes but not the CSSOM.
   function leaveRoomForBar(element: HTMLElement): void {
      element.style.setProperty('--stop-bar-room', `${barHeight + 8}px`);
   }

   // The scroller's overflow clips the observed box, so the box stops intersecting the window once it leaves the panel's top edge.
   function watchHeader(element: HTMLElement): () => void {
      const observer = new home.window.IntersectionObserver((entries) => {
         headerGone = !entries.at(-1)!.isIntersecting;
      });
      observer.observe(element);
      return () => observer.disconnect();
   }
</script>

<div class="stop-panel" {@attach leaveRoomForBar}>
   <StopStickyBar
      {stop}
      visible={headerGone}
      versionNotice={shownVersion === null ? null : versionNoticeText(shownVersion)}
      staleNotice={inline ? null : staleNoticeText(view.stale)}
      bind:height={barHeight}
   />
   <div class="px-4 py-3">
      <div {@attach watchHeader}>
         <!-- The header carries the title's size, so the stop's kind beside the title scales with it. -->
         <header class="flex flex-wrap items-baseline gap-x-3 text-lg">
            <StopKindPill kind={stop.kind} />
            <h3 class="min-w-40 flex-1 text-lg font-semibold">{stop.title}</h3>
            <ThreadMarkers threads={stopThreads(view.threads, stop.id)} onopenthread={(threadId) => view.openThread(threadId)} />
         </header>
         <StopToolbar
            {stop}
            move={view.movable && stop.stage === 'written' ? { ...move, run: () => view.move(move.to) } : null}
            onchoose={(kind) => actions?.open(kind)}
            onfocusform={() => actions?.focus()}
            offersActions={onaction !== null}
            {sending}
            {controls}
            {versions}
            {current}
            {onopenversion}
            {skipped}
            {onskip}
            {done}
            {onsetdone}
         />
         {#if shownVersion !== null}
            {@const current = shownVersion.current}
            <StopVersionBanner shown={shownVersion} onshowcurrent={() => onopenversion(current)} />
         {/if}
         {#if !inline}
            <StopStaleNotice stale={view.stale} />
         {/if}
      </div>
      {#if stop.stage === 'pending'}
         <p class="mt-2 text-muted-foreground">{planWaits ? 'Claude writes this stop once you approve the plan in the Plan panel.' : 'Claude has not written this stop yet.'}</p>
      {:else}
         <StopActions bind:this={actions} bind:sending stopId={stop.id} threads={view.threads} {controls} {onaction} onopenthread={(threadId) => view.openThread(threadId)} />
         {#if inline}
            <p class="mt-2 text-muted-foreground">The explanation shows in the code, below the primary passage.</p>
            {#if primary !== null}
               <Button variant="outline" size="sm" class="mt-2" onclick={() => onopenpassage(primary)}>Go to the explanation</Button>
            {/if}
         {:else}
            {#if fallback}
               <div class="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1">
                  <p class="text-muted-foreground">Shows in the code once its file shows the primary passage.</p>
                  {#if primary !== null}
                     <Button variant="outline" size="sm" onclick={() => onopenpassage(primary)}>Go to the explanation</Button>
                  {/if}
               </div>
            {/if}
            <ExplanationBlock {view} host="panel" />
         {/if}
         {#if stop.content.passages.length > 0}
            <StopPassages passages={stop.content.passages} {onopenpassage} commits={view.commits} />
         {/if}
      {/if}
   </div>
</div>
