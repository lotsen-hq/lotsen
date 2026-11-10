import { stopsWithWaitingActions } from './controls';
import { atPin, type PinCommits } from './pin';
import { changedFilesAt } from './repositoryTree';
import { contentOf, staleOnOf, versionOf } from './stops';
import { decisionOf } from './threadView';
import type { TourRecord } from './tourRecord.svelte';
import type { ChangedFile, CodeReference, FileSide, Stop, StopPassage } from './types';
import { stopAt, stopVersions, type KnownVersion, type ShownReason, type ShownVersion } from './versions';
import { stepOrder } from './viewMode';

/**
 * A line that the diff view or the file view scrolls to once it shows the file: the start line of a passage.
 *
 * The path is the one the view shows: a changed file's path at the head for the diff view, or the passage's path for the file view. Each open of a passage makes a new object, so the view scrolls again.
 */
export interface LineFocus {
   path: string;
   side: FileSide;
   line: number;
}

/** A passage of a stop version, by its place in that version's passages. */
export interface PassagePlace {
   stopId: string;
   version: number | undefined;
   index: number;
}

/** What the reviewer opened last with lines: the line it scrolled to then, the pin's commit of its side then, and the open stop's passage that the lines came from, or null for none. */
export interface FocusTarget {
   lines: LineFocus;
   commit: string | null;
   passage: PassagePlace | null;
}

/**
 * Whether the stop under view is stale, from its stale marker alone. A stale stop names the iteration of the update that made it stale, the iteration that its revision is for, whether that update
 * waits for Claude and names the stop, and whether any of its passages is at an earlier commit than the pin's. A stop that stayed stale across a later update has a target past `since`, and its
 * target is null only while the page knows no iteration of the tour's pin.
 */
export type StaleStatus = { kind: 'current' } | { kind: 'stale'; since: number; target: number | null; revising: boolean; earlierPassages: boolean };

const current: StaleStatus = { kind: 'current' };

/** A passage: its place in its stop version, its lines, and which of the stop's passages over those lines it is, counted from 0 in step through's order. */
interface PassageOccurrence {
   place: PassagePlace | null;
   lines: CodeReference;
   ordinal: number;
}

/** How a plan change reaches the page: an event, which one store sent at its revision, or a load, a snapshot of the tour at its revision. */
export interface PlanSource {
   kind: 'event' | 'load';
   revision: number;
}

/** The revisions that the tour actions waiting for Claude want, from which {@link OpenStop.applyPlan} decides whether to hold the stop under view. */
interface WantedRevisions {
   /** The IDs of the stops whose own tour action waits. */
   stops: ReadonlySet<string>;
   /** Whether an action on the plan waits that asks for revisions of any stop, which every such action but an iteration update does. */
   plan: boolean;
   /** The iteration of the waiting iteration update and the IDs of the stale stops it names, or null when none waits. */
   update: { iteration: number; stopIds: readonly string[] } | null;
}

/**
 * Holds the stop under view of one tour: which stop is open, the version that the page shows of it, its passages at the pin, the focused passage and its stale status.
 *
 * The stop and its versions come from the tour record, and the pin from the page, so every read follows both. Reads are reactive.
 *
 * The page holds the version that the reviewer reads when a revision of the stop arrives, as {@link OpenStop.applyPlan} says, until the reviewer moves on, opens another version or opens a stop.
 */
export class OpenStop {
   private readonly record: TourRecord;
   private readonly pin: () => PinCommits | null;
   private readonly pinnedIteration: () => number | null;
   private openId = $state<string | null>(null);
   /** The earlier version that the page shows in place of the current one, with why, or null for the current version. It applies only while it is older than the stop's current version. */
   private held = $state.raw<{ version: KnownVersion; reason: ShownReason } | null>(null);
   /** The passage the reviewer opened last, which {@link OpenStop.focusedIndex} finds among the open stop's passages, or null before one opens. */
   private occurrence = $state.raw<PassageOccurrence | null>(null);

   /** The position of the open stop in the plan, or -1 when no stop is open or the plan no longer has it. */
   index = $derived.by(() => (this.openId === null ? -1 : (this.record.plan?.stops.findIndex((stop) => stop.id === this.openId) ?? -1)));
   /** The open stop as the plan holds it, at its current version, or null when no stop is open or the plan no longer has it. */
   current = $derived.by((): Stop | null => this.record.plan?.stops[this.index] ?? null);
   /** The open stop's earlier versions, oldest first, empty while no stop is open. */
   versions = $derived.by((): KnownVersion[] => stopVersions(this.record.earlierVersions, this.current));
   private applied = $derived.by(() => {
      const { held, current } = this;
      return held !== null && current?.stage === 'written' && held.version.stopId === current.id && held.version.number < current.version ? { ...held, stop: current } : null;
   });
   /** The earlier stop version that the page shows of the open stop, or null when it shows the current version. */
   shownVersion = $derived<ShownVersion | null>(this.applied === null ? null : { number: this.applied.version.number, current: this.applied.stop.version, reason: this.applied.reason });
   /**
    * The open stop at the version the page shows, or null when no stop is open or the plan no longer has it.
    *
    * The open stop's passages, its focus in the diff and its explanation all follow this version, so an earlier one shows as it was until the reviewer switches.
    */
   stop = $derived<Stop | null>(this.applied === null ? this.current : stopAt(this.applied.stop, this.applied.version));
   /** The ID of the stop under view, which the page's writes name as the stop the reviewer viewed: the open stop while the plan holds it, or null. */
   viewedStopId = $derived(this.stop?.id ?? null);
   /**
    * The passages of the open stop, at the version the page shows, that are at the pin, as {@link atPin} says, in the stop's order.
    *
    * Only these get decorations, focus, step through and a place in the stop-files stack, so a stale stop's passages never point at lines of the pin that may hold other code.
    */
   pinnedPassages = $derived<StopPassage[]>((contentOf(this.stop)?.passages ?? []).filter((passage) => atPin(passage, this.pin())));
   /** The open stop's primary passage when it is at the pin, which the stop opens and its inline explanation sits below, or null when it has none there. */
   primaryPassage = $derived<StopPassage | null>(this.pinnedPassages.find((passage) => passage.role === 'primary') ?? null);
   /** The open stop's passages at the pin in the order that step through visits them, or none while no stop with such passages is open. */
   stepPassages = $derived(stepOrder(this.pinnedPassages));
   /**
    * The position in {@link OpenStop.stepPassages} of the focused passage: the passage the reviewer opened last, or -1 when the open stop has no passages.
    *
    * After a plan change, it finds the passages over the focused one's lines, and takes the same occurrence among them, or the last when fewer remain, so a new label or role keeps the focus.
    * When no passage has those lines, it takes the passage at the focused one's place in the same stop version, because an iteration update moves a passage there. It falls back to the primary
    * passage when neither is at the pin, so it always names one of the open stop's passages.
    */
   focusedIndex = $derived.by(() => {
      const fallback = this.stepPassages.length > 0 ? 0 : -1;
      const focused = this.occurrence;
      if (focused === null) {
         return fallback;
      }

      const equal = this.stepPassages.flatMap((passage, index) => (sameLines(passage, focused.lines) ? [index] : []));
      if (equal.length > 0) {
         return equal[Math.min(focused.ordinal, equal.length - 1)]!;
      }

      const moved = this.passageAt(focused.place);
      const index = moved === undefined || moved === null ? -1 : this.stepPassages.indexOf(moved);
      return index >= 0 ? index : fallback;
   });
   /** The focused passage, or null when the open stop has no passages. */
   focusedPassage = $derived<StopPassage | null>(this.stepPassages[this.focusedIndex] ?? null);
   /** The update that waits for Claude, when the controls name one and the record holds its thread, or null. */
   private waitingUpdate = $derived.by(() => this.updateOf(this.record.controls?.offers.pending.iteration));
   /** Whether the open stop, at the version the page shows, is stale: from its stale marker alone, so an earlier version is never stale. */
   stale = $derived.by((): StaleStatus => {
      const stop = this.stop;
      const since = staleOnOf(stop);
      if (stop === null || since === undefined) {
         return current;
      }

      const commits = this.pin();
      return {
         kind: 'stale',
         since,
         target: this.waitingUpdate?.iteration ?? this.pinnedIteration(),
         revising: this.waitingUpdate?.stopIds.includes(stop.id) === true,
         // A stop can be stale with every passage at the pin, as when its primary passage no longer touches the diff.
         earlierPassages: contentOf(stop)?.passages.some((passage) => !atPin(passage, commits)) === true,
      };
   });

   /**
    * @param record - The tour's record, which holds its plan, its earlier stop versions, its threads and its controls.
    * @param pin - Returns the commits of the tour's pin as the page holds them, or null while the tour is not pinned.
    * @param pinnedIteration - Returns the number of the iteration that the tour is pinned to as the page holds it, or null while the tour is not pinned.
    */
   constructor(record: TourRecord, pin: () => PinCommits | null, pinnedIteration: () => number | null) {
      this.record = record;
      this.pin = pin;
      this.pinnedIteration = pinnedIteration;
   }

   /** The ID of the open stop, or null when none is open. It may name a stop that the plan does not hold yet, as while a history entry waits for the plan. */
   get stopId(): string | null {
      return this.openId;
   }

   /** Makes a stop the open one, or closes the open stop with null, with no passage focused. Another stop than the open one shows its current version. */
   open(stopId: string | null): void {
      if (stopId !== this.openId) {
         this.held = null;
      }

      this.openId = stopId;
      this.occurrence = null;
   }

   /** Shows a version of the open stop by its number: the current one, or an earlier one read-only. A number that the record knows no version of changes nothing. */
   readVersion(number: number): void {
      const version = this.versions.find((candidate) => candidate.number === number);
      if (this.current !== null && number === versionOf(this.current)) {
         this.moveOn();
      } else if (this.current !== null && version !== undefined) {
         this.held = { version, reason: { kind: 'opened' } };
      }
   }

   /** Shows the open stop's current version in place of a held or opened one. */
   moveOn(): void {
      this.held = null;
   }

   /** Focuses a passage of the open stop at the pin: the passage itself, or else the first over the lines of a code reference. Lines that no such passage holds leave the focus as it is. */
   focus(passage: CodeReference): void {
      const own = this.stepPassages.findIndex((candidate) => candidate === passage);
      const index = own >= 0 ? own : this.stepPassages.findIndex((candidate) => sameLines(candidate, passage));
      const focused = this.stepPassages[index];
      if (focused !== undefined) {
         this.occurrence = { place: this.placeOf(focused), lines: focused, ordinal: this.stepPassages.slice(0, index).filter((candidate) => sameLines(candidate, focused)).length };
      }
   }

   /** Returns the place of the open stop's passage that a passage or a code reference names: the passage itself, or else the first over its lines, or null when it is none of them. */
   placeOf(reference: CodeReference): PassagePlace | null {
      const stop = this.stop;
      const passages = contentOf(stop)?.passages ?? [];
      const own = passages.findIndex((passage) => passage === reference);
      const index = own >= 0 ? own : passages.findIndex((passage) => sameLines(passage, reference));
      return stop === null || index < 0 ? null : { stopId: stop.id, version: versionOf(stop), index };
   }

   /**
    * Returns the line that the view scrolls to for what the reviewer opened last.
    *
    * Lines from a passage of the version on screen follow that passage as the plan holds it now, so a passage that an iteration update moved focuses its new lines, and one that is not at the pin
    * focuses nothing. Lines from elsewhere, or from a version that a revision replaced, keep their place, but only at the pin that they were opened at, because another pin may hold other code there.
    *
    * @param files - The tour's changed files, which give a passage's file its path in the diff view, or null while none are loaded.
    * @returns The line, or null when it is not at the pin.
    */
   lineFocus({ lines, commit, passage }: FocusTarget, files: readonly ChangedFile[] | null): LineFocus | null {
      const moved = this.passageAt(passage);
      if (moved === undefined) {
         const pinned = this.pin()?.[lines.side] ?? null;
         return commit === null || pinned === null || commit === pinned ? lines : null;
      }

      if (moved === null || !atPin(moved, this.pin())) {
         return null;
      }

      const changed = files === null ? undefined : changedFilesAt(files, moved.side).get(moved.path);
      return { path: changed?.path ?? moved.path, side: moved.side, line: moved.startLine };
   }

   /**
    * Makes a change to the tour's plan, and keeps the stop under view on screen at the version that the reviewer read when the change brings a newer version of it.
    *
    * Every plan change passes here, so a revision that only a load brings, as after the event stream reconnects, holds as one from its event does. The held version joins the record's earlier
    * versions, so the version picker offers it before any load brings it. A version that the reviewer already reads in place of the current one stays.
    *
    * A revision that the reviewer asked for is not held: one of a stop with its own waiting action, or of any stop while an action on the plan waits. A waiting iteration update is the exception,
    * because Claude revises the stale stops without the reviewer's words. So a revision of a stop that the update names holds and names the update's iteration, even while another action on the
    * plan waits.
    *
    * The actions that count are those that waited when Claude stored the revision, from the controls that held then, as {@link TourRecord.waitingWithin} gives them. The controls that the record
    * holds now can be newer than the revision, and then show the reply that ended the wait. An event names the store of the revision, so it counts the controls just before that store. A load
    * cannot tell which store after the ones the page heard held the revision, so it counts every action that waited between {@link TourRecord.heardThrough}, or the version on screen when its
    * message is later, and the load.
    *
    * @param change - Stores the plan's message in the record, and returns whether the plan that the record shows changed.
    * @param replacedAt - The tour's update time of the message, which a held version keeps as the time it was replaced.
    * @param source - How the plan reaches the page, with the revision of its message.
    * @returns Whether the plan changed.
    */
   applyPlan(change: () => boolean, replacedAt: string, source: PlanSource): boolean {
      const viewed = this.applied === null ? this.stop : null;
      const heard = viewed === null ? -Infinity : Math.max(this.record.stopRevision(viewed.id) ?? -Infinity, this.record.heardThrough);
      const after = source.kind === 'event' ? source.revision - 1 : Math.min(source.revision - 1, heard);
      if (!change()) {
         return false;
      }

      const now = this.current;
      if (viewed?.stage === 'written' && now?.id === viewed.id && viewed.version < (versionOf(now) ?? 0)) {
         this.hold(viewed, replacedAt, this.wantedWithin(after, source.revision));
      }

      return true;
   }

   private hold(viewed: Extract<Stop, { stage: 'written' }>, replacedAt: string, wanted: WantedRevisions): void {
      const update = wanted.update !== null && wanted.update.stopIds.includes(viewed.id) ? wanted.update : null;
      if (wanted.stops.has(viewed.id) || (wanted.plan && update === null)) {
         return;
      }

      const version: KnownVersion = { stopId: viewed.id, number: viewed.version, title: viewed.title, kind: viewed.kind, content: viewed.content, replacedAt };
      this.record.addEarlierVersions([version]);
      this.held = { version, reason: update !== null ? { kind: 'iterationUpdate', iteration: update.iteration } : { kind: 'revised' } };
   }

   /** Returns the revisions that the tour actions wanted at any store after revision `after` and up to revision `upTo`, with the latest of their iteration updates. */
   private wantedWithin(after: number, upTo: number): WantedRevisions {
      const held = this.record.waitingWithin(after, upTo);
      return {
         // eslint-disable-next-line svelte/prefer-svelte-reactivity -- a new set for each union, which nothing renders.
         stops: new Set(held.flatMap((pending) => [...stopsWithWaitingActions(pending)])),
         plan: held.some((pending) => pending.plan.some((threadId) => threadId !== pending.iteration)),
         update: held.reduce<WantedRevisions['update']>((latest, pending) => this.updateOf(pending.iteration) ?? latest, null),
      };
   }

   /** Returns the iteration update of the thread with the ID, when the record holds its thread, or null. */
   private updateOf(iteration: string | undefined): WantedRevisions['update'] {
      const thread = iteration === undefined ? undefined : this.record.thread(iteration);
      const decision = thread === undefined ? null : decisionOf(thread);
      return decision?.kind === 'iterationUpdated' ? { iteration: decision.iteration, stopIds: decision.stopIds } : null;
   }

   /** Returns the passage at a place of the version on screen as the plan holds it now, null when that version has no passage there, or undefined when the place is of no such version. */
   private passageAt(place: PassagePlace | null): StopPassage | null | undefined {
      const stop = this.stop;
      if (place === null || stop?.id !== place.stopId || versionOf(stop) !== place.version) {
         return undefined;
      }

      return contentOf(stop)?.passages[place.index] ?? null;
   }
}

function sameLines(a: CodeReference, b: CodeReference): boolean {
   return a.side === b.side && a.path === b.path && a.startLine === b.startLine && a.endLine === b.endLine;
}
