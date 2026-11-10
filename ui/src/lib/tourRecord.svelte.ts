import { idleAnswerSend, type AnswerSendState } from './answers';
import type { IterationCheck } from './iterations';
import { Revisioned, RevisionedValue } from './revisioned.svelte';
import { skippedIn } from './stops';
import { versionsOf, withEarlierVersions, type KnownVersion } from './versions';
import type {
   AvailableIteration,
   ChangedFileList,
   CheckAnswer,
   PendingWork,
   Plan,
   QuestionThread,
   ReadingPlace,
   ReadingProgress,
   ReviewSummary,
   Stop,
   StopPlacement,
   Tour,
   TourControls,
   TourPin,
} from './types';

/** The plan that a tour record knows, or a null plan when the tour has none yet. */
export interface KnownPlan {
   plan: Plan | null;
}

/**
 * What the page knows of one tour's changed files: a load in progress, the list, the pin that the server gave when it refused the list because the tour is not pinned, or a failure with a message and
 * the server's reason when it gave one.
 *
 * A transient failure of a load at a new pin keeps the earlier pin's list as `earlier`, from which the open diff and the stop-files stack still take the changed files and their old paths.
 */
export type KnownFiles =
   { state: 'loading' } | { state: 'loaded'; list: ChangedFileList } | { state: 'notPinned'; pin: TourPin } | { state: 'failed'; message: string; reason: string | null; earlier?: ChangedFileList };

/** What the page knows of one tour's review summary: a first load in progress, the summary, or a failure with a message and the server's reason when it gave one. */
export type KnownSummary = { state: 'loading' } | { state: 'loaded'; summary: ReviewSummary } | { state: 'failed'; message: string; reason: string | null };

/** Whether a file is reviewed, and for a set review mark, the head object ID it names, or null for a deleted file. */
export interface ReviewState {
   reviewed: boolean;
   headObjectId: string | null;
}

/**
 * A review mark that the reviewer set or cleared in this page, which wins over the list of changed files until a list that the server read after the mark's last request loads.
 *
 * A list that started loading before then may predate the mark. A set mark counts only for the version of the file that it names, as on the server.
 */
export interface ReviewChange {
   /** The reviewer's last toggle. */
   shown: ReviewState;
   /** What the server holds, as its last answer or the list before the first toggle showed. */
   stored: ReviewState;
   /** The number of the file's requests that the server has not answered yet. */
   pending: number;
   /** The review clock when the server answered the file's last request. */
   settled: number;
}

/** The progress write in flight for a tour, with the place to record once it ends. `reopened` is true once the reviewer opened the place in flight again. */
export interface ProgressWrite {
   sent: ReadingPlace;
   wanted: ReadingPlace;
   reopened: boolean;
}

/** The parts of a plan that a stop write leaves as they are. */
interface PlanShape {
   chapters: Plan['chapters'];
   stopIds: readonly string[];
   fileMarks: Plan['fileMarks'];
}

/**
 * Holds what the page knows of one tour: the tour's state from its loads, its events and the responses to the page's writes, and the page's own state of the tour.
 *
 * Each entity of the tour's state keeps the value of the message with the highest revision for that entity, as {@link Revisioned} does. The entities are the plan, each stop, each thread,
 * each answer, the skips, the available iteration, the controls and the reading progress. A load is a snapshot of all of them at its revision. Reads are reactive.
 */
export class TourRecord {
   readonly tourId: string;

   private readonly shape = new RevisionedValue<PlanShape | null>();
   private readonly stops = new Revisioned<string, Stop>();
   private readonly threadEntries = new Revisioned<string, QuestionThread>();
   private readonly answerEntries = new Revisioned<string, CheckAnswer>();
   private readonly skips = new RevisionedValue<readonly string[]>();
   private readonly seenVersions = new Revisioned<string, number>();
   private readonly iteration = new RevisionedValue<AvailableIteration | null>();
   private readonly knownControls = new RevisionedValue<TourControls>();
   /** The waiting work of the controls that the record heard of, oldest first, as runs of one work at consecutive revisions. {@link TourRecord.putControls} says which runs it keeps. */
   private waitingHistory: WaitingRun[] = [];
   private readonly knownProgress = new RevisionedValue<ReadingProgress | null>();

   /** The tour's plan, null while the tour has none, or undefined before a load or an event brings it. */
   plan = $derived.by((): Plan | null | undefined => {
      const shape = this.shape.value;
      if (shape === undefined || shape === null) {
         return shape;
      }

      return { chapters: shape.chapters, stops: shape.stopIds.flatMap((id) => this.stops.get(id) ?? []), fileMarks: shape.fileMarks };
   });

   /** The plan as the Plan panel takes it, or undefined before a load or an event brings it. */
   knownPlan = $derived<KnownPlan | undefined>(this.plan === undefined ? undefined : { plan: this.plan });

   /** The earlier versions of the tour's stops that the page has loaded or heard of. {@link withEarlierVersions} says how they merge. */
   earlierVersions = $state.raw<readonly KnownVersion[]>([]);

   /** The tour's changed files, or undefined before their first load. */
   files = $state.raw<KnownFiles | undefined>(undefined);
   /** The tour's review summary, or undefined before its summary page first opens. */
   summary = $state.raw<KnownSummary | undefined>(undefined);
   /** The reviewer's last iteration check of the tour, or undefined before one. */
   iterationCheck = $state.raw<IterationCheck | undefined>(undefined);
   /** The number of the skip writes that the page waits for, from any panel. */
   skipsSaving = $state(0);
   /** The state of the tour's send of its answers. */
   answerSend = $state.raw<AnswerSendState>(idleAnswerSend);
   /** The review marks that the reviewer set or cleared in this page, by path. */
   reviewChanges = $state<Record<string, ReviewChange>>({});
   /** Where the reviewer moved each stop's explanation in mixed placement, by stop ID. The page does not remember it across page loads. */
   placements = $state<Record<string, StopPlacement>>({});

   /** The request counter of the changed files' loads, so only the last request's answer counts. */
   fileLoad = 0;
   /** The request counter of the summary's loads, so only the last request's answer counts. */
   summaryLoad = 0;
   /** The number of plan loads in progress. */
   planLoads = 0;
   /** Whether the plan loads once more when the loads in progress end. */
   planReload = false;
   /** The progress write in flight, or null for none. */
   progressWrite: ProgressWrite | null = null;
   /** The revision up to which the page heard every store of the tour, from its loads and the events of a stream that a load joined to them, or -Infinity before the first load. */
   heardThrough = -Infinity;

   /** The highest revision among the current event stream's events. */
   private streamHigh = -Infinity;
   /** Whether a load since the current event stream opened joined the stream's events to {@link TourRecord.heardThrough}. */
   private streamJoined = false;

   constructor(tourId: string) {
      this.tourId = tourId;
   }

   /** The tour's threads, in the order they were started, without the removed ones. */
   get threads(): readonly QuestionThread[] {
      return this.threadEntries.all();
   }

   /** The tour's answers, without the cleared ones. */
   get answers(): readonly CheckAnswer[] {
      return this.answerEntries.all();
   }

   /** The IDs of the tour's skipped stops, empty before a load or a write brings them. */
   get skippedStops(): readonly string[] {
      return this.skips.value ?? [];
   }

   /** The IDs of the plan's stops that have a version newer than the one the reviewer last saw. A stop the reviewer has never opened is not among them. */
   get changedStops(): readonly string[] {
      return (this.plan?.stops ?? []).filter((stop) => stop.stage === 'written' && stop.version > (this.seenVersions.get(stop.id) ?? stop.version)).map((stop) => stop.id);
   }

   /** The newer iteration that the server found against the tour's pin, null when it knows of none, or undefined before a load or an event brings it. */
   get availableIteration(): AvailableIteration | null | undefined {
      return this.iteration.value;
   }

   /** What the UI's controls can do on the tour, or undefined before a response or an event brings them. */
   get controls(): TourControls | undefined {
      return this.knownControls.value;
   }

   /** What the server holds of the tour's reading progress, null for none, or undefined before a load or a write brings it. */
   get progress(): ReadingProgress | null | undefined {
      return this.knownProgress.value;
   }

   /** Returns the newest version of the stop that the reviewer has seen, or undefined before the reviewer opened one. */
   seenVersion(stopId: string): number | undefined {
      return this.seenVersions.get(stopId);
   }

   /** Returns the thread with the ID, or undefined when the record holds none or a removal. */
   thread(threadId: string): QuestionThread | undefined {
      return this.threadEntries.get(threadId);
   }

   /**
    * Takes a load of the tour as a snapshot of every entity at its revision, as {@link Revisioned.replaceAll} does.
    *
    * @returns Whether the plan that the record shows changed.
    */
   applyLoad(tour: Tour): boolean {
      const { revision } = tour;
      const plan = tour.plan ?? null;
      this.threadEntries.replaceAll(
         tour.threads.map((thread) => [thread.id, thread]),
         revision,
      );
      this.answerEntries.replaceAll(
         tour.answers.map((answer) => [answerKey(answer.stopId, answer.checkId), answer]),
         revision,
      );
      this.skips.put(skippedIn(plan), revision);
      this.seenVersions.replaceAll(
         (plan?.stops ?? []).flatMap((stop) => (stop.stage === 'written' && stop.seenVersion !== undefined ? [[stop.id, stop.seenVersion] as const] : [])),
         revision,
      );
      this.iteration.put(tour.availableIteration ?? null, revision);
      this.knownProgress.put(tour.progress ?? null, revision);
      return this.putPlan(plan, revision);
   }

   /**
    * Stores a plan at a revision: its chapters, order and file marks as one entity, and its stops as a snapshot of the stops, as {@link Revisioned.replaceAll} does.
    *
    * @returns Whether the plan that the record shows changed.
    */
   putPlan(plan: Plan | null, revision: number): boolean {
      const shape = plan === null ? null : { chapters: plan.chapters, stopIds: plan.stops.map((stop) => stop.id), fileMarks: plan.fileMarks };
      const shaped = this.shape.put(shape, revision);
      const stopped = this.stops.replaceAll(
         (plan?.stops ?? []).map((stop) => [stop.id, stop]),
         revision,
      );
      this.addEarlierVersions(versionsOf(plan));
      return shaped || stopped;
   }

   /** Stores one stop at a revision, and returns whether it stored it. A stop that the plan does not name yet shows once a plan names it. */
   putStop(stop: Stop, revision: number): boolean {
      const stored = this.stops.put(stop.id, stop, revision);
      if (stored) {
         this.addEarlierVersions(versionsOf({ chapters: [], stops: [stop], fileMarks: [] }));
      }

      return stored;
   }

   /** Returns whether the plan names a stop. */
   hasStop(stopId: string): boolean {
      return this.shape.value?.stopIds.includes(stopId) === true;
   }

   /** Adds earlier stop versions, as {@link withEarlierVersions} merges them. */
   addEarlierVersions(versions: readonly KnownVersion[]): void {
      if (versions.length > 0) {
         this.earlierVersions = withEarlierVersions(this.earlierVersions, versions);
      }
   }

   /** Stores a thread at a revision, and returns whether it stored it. */
   putThread(thread: QuestionThread, revision: number): boolean {
      return this.threadEntries.put(thread.id, thread, revision);
   }

   /** Removes a thread at a revision, and returns whether the removal holds. */
   removeThread(threadId: string, revision: number): boolean {
      return this.threadEntries.remove(threadId, revision);
   }

   /** Stores an answer at a revision, and returns whether it stored it. */
   putAnswer(answer: CheckAnswer, revision: number): boolean {
      return this.answerEntries.put(answerKey(answer.stopId, answer.checkId), answer, revision);
   }

   /** Clears a check's answer at a revision, and returns whether the clear holds. */
   clearAnswer(stopId: string, checkId: string, revision: number): boolean {
      return this.answerEntries.remove(answerKey(stopId, checkId), revision);
   }

   /** Stores the skipped stops at a revision, and returns whether it stored them. */
   putSkips(stopIds: readonly string[], revision: number): boolean {
      return this.skips.put(stopIds, revision);
   }

   /** Stores the newest version of a stop that the reviewer has seen, at a revision, and returns whether it stored it. */
   putSeenVersion(stopId: string, version: number, revision: number): boolean {
      return this.seenVersions.put(stopId, version, revision);
   }

   /** Stores the available iteration at a revision, null for none, and returns whether it stored it. */
   putIteration(iteration: AvailableIteration | null, revision: number): boolean {
      return this.iteration.put(iteration, revision);
   }

   /**
    * Stores the controls at their own revision, and returns whether it stored them.
    *
    * Their waiting work joins the history that {@link TourRecord.waitingWithin} reads, also when older controls than the ones held arrive late. The history merges only work at consecutive
    * revisions, because later controls can arrive at any revision that the record has not heard of. It drops the runs before the last one that starts at or before the lowest revision of a stop that
    * the record holds or {@link TourRecord.heardThrough}, whichever is higher, because {@link OpenStop.applyPlan} asks only about later stores. A record without stops keeps only its newest run.
    */
   putControls(controls: TourControls): boolean {
      this.keepWaiting(controls.revision, controls.offers.pending);
      return this.knownControls.put(controls, controls.revision);
   }

   /**
    * Returns the waiting work that held for a store after revision `after` and up to revision `upTo`, oldest first: the last at or before `after`, and every one between.
    *
    * Controls at a revision tell the waiting work once that store is done, so a store at revision r meets the controls of the highest revision below r. A response can bring controls of a later
    * revision than the events that the stream still holds, so the controls held now can be newer than a store that the page has yet to hear of.
    */
   waitingWithin(after: number, upTo: number): readonly PendingWork[] {
      const history = this.waitingHistory;
      const first = Math.max(
         0,
         history.findLastIndex((run) => run.from <= after),
      );
      return history.slice(first).flatMap((run) => (run.from < upTo ? [run.pending] : []));
   }

   /** Starts the account of a new event stream, whose events can follow a gap after the stores that the page heard. */
   streamOpened(): void {
      this.streamHigh = -Infinity;
      this.streamJoined = false;
   }

   /** Takes an event of the current stream at its revision. The stream brings a tour's events in the order of their stores. */
   heardEvent(revision: number): void {
      this.streamHigh = Math.max(this.streamHigh, revision);
      if (this.streamJoined) {
         this.heardThrough = Math.max(this.heardThrough, revision);
      }
   }

   /**
    * Takes a load at its revision, which holds every store up to it.
    *
    * @param sinceStreamOpen - Whether the page asked for the load after the current stream opened. The server subscribes a stream before it answers its open, so such a load joins the stream.
    */
   heardLoad(revision: number, sinceStreamOpen: boolean): void {
      this.heardThrough = Math.max(this.heardThrough, revision);
      if (sinceStreamOpen) {
         this.streamJoined = true;
         this.heardThrough = Math.max(this.heardThrough, this.streamHigh);
      }
   }

   private keepWaiting(revision: number, pending: PendingWork): void {
      const history = this.waitingHistory;
      const found = history.findIndex((run) => run.to >= revision);
      const index = found < 0 ? history.length : found;
      const previous = history[index - 1];
      const next = history[index];
      if (next !== undefined && next.from <= revision) {
         return;
      }

      const joinsPrevious = previous !== undefined && previous.to === revision - 1 && sameWork(previous.pending, pending);
      const joinsNext = next !== undefined && next.from === revision + 1 && sameWork(next.pending, pending);
      if (joinsPrevious && joinsNext) {
         history.splice(index, 1);
         previous.to = next.to;
      } else if (joinsPrevious) {
         previous.to = revision;
      } else if (joinsNext) {
         next.from = revision;
      } else {
         history.splice(index, 0, { from: revision, to: revision, pending });
      }

      const lowest = this.stops.lowestRevision();
      const floor = lowest === undefined ? undefined : Math.max(lowest, this.heardThrough);
      const first = floor === undefined ? history.length - 1 : history.findLastIndex((run) => run.from <= floor);
      if (first > 0) {
         this.waitingHistory = history.slice(first);
      }
   }

   /** Returns the revision of the message that set the stop, or undefined when the record holds no such stop. */
   stopRevision(stopId: string): number | undefined {
      return this.stops.revisionOf(stopId);
   }

   /** Stores the reading progress at a revision, null for none, and returns whether it stored it. */
   putProgress(progress: ReadingProgress | null, revision: number): boolean {
      return this.knownProgress.put(progress, revision);
   }
}

/** One waiting work that the record heard of at every revision from `from` to `to`. */
interface WaitingRun {
   from: number;
   to: number;
   pending: PendingWork;
}

function sameWork(a: PendingWork, b: PendingWork): boolean {
   return JSON.stringify(a) === JSON.stringify(b);
}

function answerKey(stopId: string, checkId: string): string {
   return JSON.stringify([stopId, checkId]);
}
