import type { CheckAnswers } from './answers';
import { disabledReason, stopOffers } from './controls';
import type { LinkHover } from './linkHover';
import { OpenSections } from './openSections.svelte';
import type { OpenStop, StaleStatus } from './openStop.svelte';
import { referenceAtPin, type PinCommits } from './pin';
import { resolvePlacement, type PlacementOptions } from './placement';
import type { SelectionExplain } from './selectionAsk';
import { stopTextThreads, stopThreads, type ThreadMarker } from './threadView';
import type { CodeReference, QuestionThread, Stop, StopPassage, StopPlacement, TourControls } from './types';
import type { ShownVersion } from './versions';
import type { CheckOpening, PickedAnchor, PickHandler } from './viewState';

/** What an explanation reads of the stop under view, as {@link OpenStop} holds it. */
export type ExplainedStop = Pick<OpenStop, 'stop' | 'primaryPassage' | 'focusedPassage' | 'shownVersion' | 'stale'>;

/** What the page gives an explanation beyond the stop under view: the tour's state that it shows, and the reviewer's actions from it. Reads are reactive. */
export interface ExplanationPage {
   /** The tour's threads, which the explanation marks where they were asked about the stop. */
   readonly threads: readonly QuestionThread[];
   /** The answers to the stop's checks, with their writes while the page shows the current stop version. */
   readonly answers: CheckAnswers;
   /** The commits of the tour's pin, against which the `ref:` links tell whether they open, or null while the tour is not pinned. */
   readonly commits: PinCommits | null;
   /** The tour's controls, which say whether Explain is offered, or null while the page has none. */
   readonly controls: TourControls | null;
   /** The check that the reviewer opened last from the summary page, or null for none. */
   readonly openedCheck: CheckOpening | null;
   /** Whether an editor can still show the inline block: the center's file has not settled into a view without an editor. */
   readonly blockCanShow: boolean;
   /** What the `ref:` links report hovering to, the page's one {@link LinkHover}. */
   readonly hover: LinkHover;
   followReference(reference: CodeReference): void;
   openThread(threadId: string): void;
   /** Hands what the reviewer selected to the Threads panel. */
   pick: PickHandler;
   /** Requests Explain about selected text, and returns null once the server stored it, or the server's reason when it refused it. */
   requestExplain(anchor: Extract<PickedAnchor, { kind: 'stopText' }>): Promise<string | null>;
   /** Moves the explanation to a place, which wins over Claude's hint and the rule in mixed placement. */
   movePlacement(placement: StopPlacement): void;
   showCurrentVersion(): void;
}

/** Where an explanation shows: in a block below its primary passage in the diff, or in the Stop panel. */
export type ExplanationHost = 'inline' | 'panel';

/** What an explanation of a written stop shows, at the version the page shows. */
export interface ExplanationContent {
   stop: Extract<Stop, { stage: 'written' }>;
   /** The markers of the threads asked about the stop as a whole. */
   markers: readonly ThreadMarker[];
   /** The markers of the threads asked about text in the stop version, each with its prose field. */
   textThreads: readonly (ThreadMarker & { field: string })[];
   /** Returns whether a `ref:` link's code reference is at the pin. */
   atPin: (reference: CodeReference) => boolean;
   /** The focused passage, whose `ref:` links the prose highlights, or null for none. */
   focusedPassage: CodeReference | null;
}

/**
 * Holds the explanation of the stop under view, which the inline block and the Stop panel both render with `ExplanationBlock`: what it shows, where it belongs and where it shows now.
 *
 * It belongs inline when the placement rule puts it there, which needs its primary passage at the pin. It shows inline only while an editor shows its block, as {@link ExplanationView.place}
 * reports, and in the Stop panel otherwise.
 */
export class ExplanationView {
   private readonly source: () => ExplainedStop | null;
   private readonly options: () => PlacementOptions;
   private readonly page: ExplanationPage;
   /** How many editors show each stop's block now, by stop ID. */
   private readonly shown = $state<Record<string, number>>({});
   private readonly stopId = $derived.by(() => this.source()?.stop?.id ?? null);
   // An editor draws a block again when it scrolls back into view, so the sections that the reviewer opened or closed live here, one set per stop and host.
   // The sets last while the stop stays under view, through a remount of a file view or the Stop panel, such as the remount of a file view in another view mode.
   private readonly opened = $derived.by(() => {
      void this.stopId;
      return { inline: new OpenSections(), panel: new OpenSections() };
   });

   /** The stop under view at the version the page shows, or null when no stop is open. */
   readonly stop = $derived.by((): Stop | null => this.source()?.stop ?? null);
   /** Where the explanation belongs: inline when the placement rule puts it there and its primary passage is at the pin, or else in the Stop panel. */
   readonly placement = $derived.by((): StopPlacement => (this.source()?.primaryPassage == null ? 'side' : resolvePlacement(this.stop, this.options())));
   /** Whether the reviewer can move the explanation: only mixed placement lets them, and only a stop with a primary passage at the pin can show inline. */
   readonly movable = $derived.by(() => this.options().mode === 'mixed' && this.source()?.primaryPassage != null);
   /** The primary passage below whose last line the inline host places the block, or null when the explanation belongs in the Stop panel. */
   readonly inlinePassage = $derived.by((): StopPassage | null => (this.placement === 'inline' ? (this.source()?.primaryPassage ?? null) : null));
   /** Where the explanation shows now. */
   readonly host = $derived.by((): ExplanationHost => (this.inlinePassage !== null && this.stopId !== null && (this.shown[this.stopId] ?? 0) > 0 ? 'inline' : 'panel'));
   /** What the explanation shows, or null while the stop under view is pending or none is open. */
   readonly content = $derived.by((): ExplanationContent | null => {
      const source = this.source();
      const stop = source?.stop ?? null;
      if (source === null || stop?.stage !== 'written') {
         return null;
      }

      const { threads, commits } = this.page;
      return {
         stop,
         markers: stopThreads(threads, stop.id),
         textThreads: stopTextThreads(threads, stop.id, stop.version),
         atPin: referenceAtPin(stop, source.shownVersion === null, commits),
         focusedPassage: source.focusedPassage,
      };
   });
   /** The earlier stop version that the explanation shows in place of the current one, or null for the current one. */
   readonly shownVersion = $derived.by((): ShownVersion | null => this.source()?.shownVersion ?? null);
   /** Whether the stop is stale, which its notice shows. */
   readonly stale = $derived.by((): StaleStatus => this.source()?.stale ?? { kind: 'current' });
   /** Hands selected text to the Threads panel, or null for an earlier stop version, because the server refuses a stop text anchor on one. */
   readonly ask = $derived.by(() => (this.shownVersion === null ? (anchor: PickedAnchor, quote: string | null) => this.page.pick(anchor, quote) : null));
   /** The Explain that a selection offers beside the ask, or null for an earlier stop version, because a tour action acts on the current one. */
   readonly explain = $derived.by((): SelectionExplain | null => {
      const stopId = this.stopId;
      if (this.shownVersion !== null || stopId === null) {
         return null;
      }

      return { send: (anchor) => this.page.requestExplain(anchor), unavailable: disabledReason(stopOffers(this.page.controls, stopId)?.explain) };
   });

   /**
    * @param source - Returns the stop under view of the selected tour, or null while no tour is selected.
    * @param options - Returns the placement rule's inputs: the reviewer's placement mode and threshold, the diff layout, the changed files and the reviewer's move of the stop.
    * @param page - The tour's state and the reviewer's actions.
    */
   constructor(source: () => ExplainedStop | null, options: () => PlacementOptions, page: ExplanationPage) {
      this.source = source;
      this.options = options;
      this.page = page;
   }

   /** The answers to the stop's checks, as the page gives them. */
   get answers(): CheckAnswers {
      return this.page.answers;
   }

   /** The tour's threads, as the page gives them. */
   get threads(): readonly QuestionThread[] {
      return this.page.threads;
   }

   /** The commits of the tour's pin, as the page gives them. */
   get commits(): PinCommits | null {
      return this.page.commits;
   }

   /** What the `ref:` links report hovering to. */
   get hover(): LinkHover {
      return this.page.hover;
   }

   /** Returns the sections that the reviewer opened or closed in a host, one set per stop and host. */
   sections(host: ExplanationHost): OpenSections {
      return this.opened[host];
   }

   /**
    * Returns the check that a host opens, or null for none: the check that the reviewer opened from the summary page, when it is of the stop.
    *
    * The Stop panel's stand-in for an inline explanation leaves the check to the inline block while an editor can still show that block.
    */
   openedCheck(host: ExplanationHost): CheckOpening | null {
      const check = this.page.openedCheck;
      if (check === null || check.stopId !== this.stopId) {
         return null;
      }

      return host === 'panel' && this.placement === 'inline' && this.host === 'panel' && this.page.blockCanShow ? null : check;
   }

   /**
    * Records that an editor shows the block of the stop under view, and returns the function that records that it no longer does.
    *
    * A caller runs it in an effect whose cleanup is the returned function, so a change of the editors or of the block reports the block gone first.
    */
   place(): () => void {
      const stopId = this.stopId;
      if (stopId === null) {
         return () => {};
      }

      this.shown[stopId] = (this.shown[stopId] ?? 0) + 1;
      return () => {
         this.shown[stopId] = this.shown[stopId]! - 1;
      };
   }

   /** Moves the explanation to a place, when the reviewer can move it. */
   move(placement: StopPlacement): void {
      if (this.movable) {
         this.page.movePlacement(placement);
      }
   }

   followReference(reference: CodeReference): void {
      this.page.followReference(reference);
   }

   openThread(threadId: string): void {
      this.page.openThread(threadId);
   }

   showCurrentVersion(): void {
      this.page.showCurrentVersion();
   }
}
