import { untrack } from 'svelte';
import { SvelteMap } from 'svelte/reactivity';
import { idleAnswerSend, stopAnswers, unsentAnswerCount, type AnswerSendControl, type AnswerSendState } from './answers';
import { ApiError, createApi, refusalReason, UnauthorizedError, type Api, type Revised } from './api';
import { ignoreWhitespacePreference } from './diffWhitespace';
import { availableOn, iterationCheckState, type IterationCheck, type IterationCheckState } from './iterations';
import { connectEventStream, logEventFailure, type ConnectionState, type EventStreamConnection, type helloEvent, type Schedule } from './eventStream';
import { historyWrite, navigationFragment, navigationState, noNavigation, readNavigation, recordedNavigation, type LineSpan, type Navigation } from './navigation';
import { applyPreference, booleanPreference, detachPreferences, syncPreferences } from './preference';
import { changedFilePaths, inFile, pinIteration, referenceAtPin, type FilePaths, type PinCommits, pinCommits } from './pin';
import { filePassageDecorations, noPassageDecorations, withHoveredReference, withThreads, type CodeThread, type DecorationSwitches, type PassageDecorations } from './passageMarks';
import { changedFilesAt } from './repositoryTree';
import { endSession, linkCode, startSession, type SessionEnvironment } from './session';
import type { SseEvent } from './sse';
import { steppedTarget } from './skips';
import { stackFileKey, stopFileStack, type FileReference, type StackFile } from './stopFiles';
import { hiddenCount, listedTours } from './tourList';
import { TourRecord, type KnownFiles, type KnownSummary, type ReviewState } from './tourRecord.svelte';
import { rowOf, touched, withPart, withSummary, type RowPart, type RowParts, type TourRow } from './tourRows';
import { ThreadActions, type WriteResult } from './threadActions.svelte';
import { defaultThreadFilter, type ThreadFilter } from './threadFilters';
import { ThreadsView } from './threadsView.svelte';
import { codeThreads, threadsAtPin } from './threadView';
import { contentOf, staleOnOf, versionOf } from './stops';
import type { KnownVersion, ShownVersion } from './versions';
import type { ViewMode } from './viewMode';
import { CheckOpening, type KnownDiff, type KnownDirectory, type KnownText, type PickedAnchor, type PruneOutcome } from './viewState';
import { OpenStop, type FocusTarget, type LineFocus, type PassagePlace, type PlanSource, type StaleStatus } from './openStop.svelte';
import type {
   AvailableIteration,
   AvailableIterationChangedEvent,
   EventName,
   EventPayloads,
   TourControls,
   AnswerInput,
   AnswerType,
   ChangedFile,
   CheckAnswer,
   ClearedAnswer,
   CodeReference,
   PlanApprovalInput,
   Familiarity,
   FileMark,
   FileSide,
   QuestionThread,
   TourActionInput,
   RequestedTourAction,
   Stop,
   StopPlacement,
   StopPassage,
   TourBinding,
   TourPin,
   ThreadAnchor,
   TourSummary,
   UpdatedAnswer,
   UpdatedBatch,
   UpdatedFamiliarity,
   UpdatedPlanApproval,
   UpdatedSeenVersion,
   UpdatedSkips,
   ReadingPlace,
   UpdatedTourState,
} from './types';

/** Where the page is in its life: starting a session, showing tours, signed out because it has no valid UI credential, or unable to reach the server for the code exchange. */
export type AppPhase = 'starting' | 'ready' | 'signedOut' | 'unreachable';

/** An event of the event stream with its payload. */
/** The events that reach the app: every event but the hello, which the event stream client reads itself. */
type AppEventName = Exclude<EventName, typeof helloEvent>;

type KnownEvent = { [K in AppEventName]: { name: K; payload: EventPayloads[K] } }[AppEventName];

/** How the app applies each event, and whether the event can change the tour's review summary, after which an open summary page loads it again. */
type EventHandlers = { [K in AppEventName]: { apply(app: LotsenApp, payload: EventPayloads[K]): void; refreshesSummary: boolean } };

const dimmingPreference = booleanPreference('lotsen.dimming', true);
const highlightPreference = booleanPreference('lotsen.highlight', true);

/** What the page needs from the browser: what a session needs, and the history that the page adds its entries to. */
export interface AppEnvironment extends SessionEnvironment {
   history: Pick<History, 'replaceState' | 'pushState' | 'state'>;
}

export interface AppOptions {
   environment: AppEnvironment;
   retryDelaysMs?: readonly number[];
   /** The timer of the event stream's silence limit, or none for the browser's. */
   silenceTimer?: Schedule;
}

/**
 * Holds what the page shows and keeps it current from the API and the event stream.
 *
 * Each tour's state lives in its {@link TourRecord}, and the tour list in its rows. Every load, event and write response carries the tour's revision, and each entity keeps the value of the message
 * with the highest revision for it, as {@link TourRecord} says, so a slow response cannot undo a newer event. Events that arrive while the tour list reloads are applied again on top of it.
 */
export class LotsenApp {
   phase = $state<AppPhase>('starting');
   connection = $state<ConnectionState>('connecting');
   /** Every tour's row, newest first. Each part of a row keeps the value of the message with the highest revision for that part, as {@link TourRow} says. */
   private rows = $state.raw<readonly TourRow[]>([]);
   /** Every tour, newest first. */
   tours = $derived(this.rows.map((row) => row.summary));
   toursLoaded = $state(false);
   /** Whether the tour list shows its hidden tours, which `hiddenReason` in tourList.ts picks, as the reviewer last chose in this page, and false while no tour is hidden. */
   showHiddenTours = $state(false);
   /** The tours that the tour list shows, newest first, as {@link listedTours} picks them. */
   listedTours = $derived(listedTours(this.tours, this.showHiddenTours));
   /** The number of tours that the tour list hides while hidden tours do not show. */
   hiddenTourCount = $derived(hiddenCount(this.tours));
   toursError = $state<string | null>(null);
   selectedTourId = $state<string | null>(null);
   selectedTourError = $state<string | null>(null);
   /** The record of each tour that the page has loaded or heard of, by tour ID, until the tour is deleted. */
   private readonly records = new SvelteMap<string, TourRecord>();
   /** The threads view, the thread actions and the open stop of each tour in {@link records}, by tour ID. The page creates them with the record. */
   private readonly threadSides = new SvelteMap<string, { view: ThreadsView; actions: ThreadActions; openStop: OpenStop }>();
   /** The selected tour's record, or undefined while no tour is selected or the page knows nothing of it. */
   selectedRecord = $derived(this.selectedTourId === null ? undefined : this.records.get(this.selectedTourId));
   /** The path of the file the reviewer selected in the selected tour, or null when none is selected. */
   selectedFilePath = $state<string | null>(null);
   /** The diff of the selected file, or null when none is selected. Nothing changes its texts, so it is not deep state. */
   selectedDiff = $state.raw<KnownDiff | null>(null);
   /** The unchanged file that the reviewer opened from the whole-repository view, or null when none is open. It and `selectedFilePath` are never both set. */
   selectedText = $state.raw<KnownText | null>(null);
   /** Whether the center shows the selected tour's summary page, which no stop or file is open beside. Opening a stop or a file leaves it. */
   showsSummary = $state(false);
   /** The selected tour's review summary while its summary page shows, or else null. */
   selectedSummary = $derived<KnownSummary | null>(this.showsSummary ? (this.selectedRecord?.summary ?? null) : null);
   /** The check that the reviewer opened last from the summary page, or null for none. */
   openedCheck = $state.raw<CheckOpening | null>(null);
   private focusTarget = $state.raw<FocusTarget | null>(null);
   /** The focus last derived, with its target, so a change that leaves the line where it was keeps the object and the view does not scroll again. */
   private lastFocus: { target: FocusTarget | null; focus: LineFocus | null } | null = null;
   /**
    * The line that the diff view or the file view scrolls to, or null when the reviewer opened the file without lines. The open stop decides where the lines are now, as
    * {@link OpenStop.lineFocus} says.
    */
   focus = $derived.by((): LineFocus | null => {
      const target = this.focusTarget;
      const focus = target === null ? null : (this.selectedOpenStop?.lineFocus(target, this.knownFileList) ?? null);
      const last = this.lastFocus;
      if (last !== null && last.target === target && (last.focus === focus || (last.focus !== null && focus !== null && sameFocus(last.focus, focus)))) {
         return last.focus;
      }

      this.lastFocus = { target, focus };
      return focus;
   });
   /** Whether diffs leave out lines that differ only in whitespace or line endings, as the reviewer last chose in any page. */
   get ignoreWhitespace(): boolean {
      return ignoreWhitespacePreference.read();
   }
   /** The reviewer's switches for the passage decorations of the open file and the stacked files, as last chosen in any page. */
   decorationSwitches = $derived<DecorationSwitches>({ dim: dimmingPreference.read(), highlight: highlightPreference.read() });
   /** Whether the diff collapses the regions away from the open stop's passages. The page does not remember it, and it turns off when the view mode changes or another tour opens. */
   collapseToPassages = $state(false);
   /** The view mode that stops open in, which {@link LotsenApp.selectTour} sets to the default each time a tour opens. */
   viewMode = $state<ViewMode>('wholePr');
   /** The code reference of the `ref:` link in the open stop's prose that the pointer is over or that has keyboard focus, which the file it points into highlights, or null for none. */
   hoveredReference = $state.raw<CodeReference | null>(null);
   /** Why the reviewer's last review mark in the selected tour could not be stored, or null when nothing failed. */
   reviewError = $state<string | null>(null);
   /** Where the reviewer moved the open stop's explanation, or null when they did not. */
   placementOverride = $derived<StopPlacement | null>(this.selectedStopId === null ? null : (this.selectedRecord?.placements[this.selectedStopId] ?? null));
   /** The file the reviewer opened last in the selected tour, with the lines of the passage or code reference that opened it, or null before one opens. */
   private opened = $state.raw<{ file: FileReference; lines: LineSpan | null } | null>(null);
   /** What the page shows, in the terms of a history entry: the selected tour, the open stop, the file opened last, and the files added to the stack while the center shows it. */
   navigation = $derived.by((): Navigation => {
      const tourId = this.selectedTourId;
      if (tourId === null) {
         return noNavigation;
      }

      return this.showsSummary
         ? { ...noNavigation, tourId, summary: true }
         : { tourId, stopId: this.selectedStopId, file: this.opened?.file ?? null, lines: this.opened?.lines ?? null, added: this.showsStopFiles ? this.stackAdded : [], summary: false };
   });

   selectedTour = $derived(this.tours.find((tour) => tour.id === this.selectedTourId) ?? null);
   // Strings, so a write that leaves the pin as it was does not derive the passages and decorations again.
   private pinnedBase = $derived(this.selectedTour?.pin.state === 'pinned' ? this.selectedTour.pin.mergeBase : null);
   private pinnedHead = $derived(this.selectedTour?.pin.state === 'pinned' ? this.selectedTour.pin.head : null);
   /** The commits that the selected tour's pin shows at each side, or null while no pinned tour is selected. */
   selectedPinCommits = $derived<PinCommits | null>(this.pinnedBase === null || this.pinnedHead === null ? null : { base: this.pinnedBase, head: this.pinnedHead });
   selectedPlan = $derived(this.selectedRecord?.knownPlan);
   /** The selected tour's familiarity level, as the tour list and the level's writes give it, or null when no tour is selected. */
   selectedFamiliarity = $derived<Familiarity | null>(this.selectedTour?.familiarity ?? null);
   /** The session bound to the selected tour, or null when none is or no tour is selected. */
   selectedBinding = $derived<TourBinding | null>(this.selectedTour?.binding ?? null);
   /** Whether the selected tour has a plan that waits for the reviewer's approval, as the tour list, the approval's writes and the known plan give it. */
   selectedPlanWaits = $derived(this.selectedTour?.planApproval === 'awaited' && (this.selectedPlan?.plan ?? null) !== null);
   /** Whether the selected tour's summary page follows its plan's last stop. A proposed plan is not the tour yet, so it has nothing to summarize. */
   selectedSummaryFollows = $derived(!this.selectedPlanWaits);
   /** The IDs of the selected tour's skipped stops, empty while no tour is selected. A skip of a stop that the plan no longer holds stays in the set and matches no stop. */
   // eslint-disable-next-line svelte/prefer-svelte-reactivity -- a new set for each change of the skips, which nothing changes.
   selectedSkippedStops = $derived<ReadonlySet<string>>(new Set(this.selectedRecord?.skippedStops ?? []));
   /** The IDs of the selected tour's stops that changed since the reviewer last opened them, as {@link TourRecord.changedStops} says, empty while no tour is selected. */
   // eslint-disable-next-line svelte/prefer-svelte-reactivity -- a new set for each change of the stops, which nothing changes.
   selectedChangedStops = $derived<ReadonlySet<string>>(new Set(this.selectedRecord?.changedStops ?? []));
   /** The newer iteration of the selected tour's pull request that the server found against the tour's pin, or null when it knows of none or no tour is selected. */
   selectedAvailableIteration = $derived<AvailableIteration | null>(availableOn(this.selectedRecord?.availableIteration, this.selectedTour?.pin));
   /** The state of the reviewer's iteration check of the selected tour. {@link iterationCheckState} says when a change to the available iteration hides its failure. */
   selectedIterationCheck = $derived<IterationCheckState>(iterationCheckState(this.selectedRecord?.iterationCheck, this.selectedRecord?.availableIteration));
   /** Whether the page waits for a skip write of the selected tour. An approval waits for it, because the approval sends the page's skips and the server stores them in place of the tour's. */
   selectedSkipsSaving = $derived((this.selectedRecord?.skipsSaving ?? 0) > 0);
   /** The selected tour's answers, empty while no tour is selected. */
   selectedAnswers = $derived<readonly CheckAnswer[]>(this.selectedRecord?.answers ?? []);
   /** The open stop's answers by check ID, empty while no stop is open. */
   selectedStopAnswers = $derived<ReadonlyMap<string, CheckAnswer>>(stopAnswers(this.selectedAnswers, this.selectedStopId));
   /** The number of the selected tour's answers to the checks that its stops hold now that the next send holds. */
   unsentAnswers = $derived(unsentAnswerCount(this.selectedAnswers, this.selectedPlan?.plan, this.selectedBinding?.sessionId ?? null));
   /** The state of the selected tour's send of its answers, idle while no tour is selected. */
   selectedAnswerSend = $derived<AnswerSendState>(this.selectedRecord?.answerSend ?? idleAnswerSend);
   /** The selected tour's controls, or null while the page has none for it. */
   selectedControls = $derived<TourControls | null>(this.selectedRecord?.controls ?? null);
   /** The selected tour's threads, in the order they were started. */
   selectedThreads = $derived<readonly QuestionThread[]>(this.selectedRecord?.threads ?? []);
   /** The selected tour's threads that the editors and the file headings mark: every thread but one whose code or file anchor stays on an earlier commit than the pin's. */
   selectedThreadsAtPin = $derived<QuestionThread[]>(threadsAtPin(this.selectedThreads, this.selectedPinCommits));
   /** The selected tour's threads asked about lines of code at the pin, which the editors mark. */
   private selectedCodeThreads = $derived<CodeThread[]>(codeThreads(this.selectedThreadsAtPin));
   /** The selected tour's threads view, or undefined while no tour is selected or the page knows nothing of it. */
   selectedThreadsView = $derived(this.selectedTourId === null ? undefined : this.threadSides.get(this.selectedTourId)?.view);
   /** The selected tour's thread actions, or undefined while no tour is selected or the page knows nothing of it. */
   selectedThreadActions = $derived(this.selectedTourId === null ? undefined : this.threadSides.get(this.selectedTourId)?.actions);
   /** What the reviewer picked last in the selected tour to ask about, or null when nothing waits there. */
   pickedAnchor = $derived(this.selectedThreadsView?.picked ?? null);
   /** The thread that the reviewer opened last from its marker or a link, whose thread view the Threads panel focuses, or null before one opens. Each open is a new object. */
   openedThread = $state.raw<{ id: string } | null>(null);
   /** The Threads panel's filter of the selected tour, or the default before the reviewer filters it. */
   selectedThreadFilter = $derived<ThreadFilter>(this.selectedThreadsView?.filter ?? defaultThreadFilter);
   /** The selected tour's open stop, or undefined while no tour is selected or the page knows nothing of it. */
   selectedOpenStop = $derived(this.selectedTourId === null ? undefined : this.threadSides.get(this.selectedTourId)?.openStop);
   selectedFiles = $derived(this.selectedRecord?.files);
   /**
    * The pin that the selected tour's file reads are keyed by: the pin of its loaded changed files, or the tour's pin once their load failed, or null while they load or the tour is not pinned.
    *
    * The open diff, the open file and the stop-files stack load again when it changes. So at a new pin they load once the changed files arrive, which hold a renamed file's old path there.
    */
   filesPin = $derived.by((): string | null => {
      const known = this.selectedFiles;
      return pinKey(known?.state === 'loaded' ? { base: known.list.mergeBase, head: known.list.head } : known?.state === 'failed' ? this.selectedPinCommits : null);
   });
   /**
    * The pin that a file read of the selected tour reads at: the {@link LotsenApp.filesPin}, or the tour's pin while the changed files load, because the server reads at the tour's pin then.
    * So a read made while the first list loads counts as done at that pin.
    */
   private get readPin(): string | null {
      return this.filesPin ?? pinKey(this.selectedPinCommits);
   }

   /** The selected tour's changed files, as {@link listedFiles} gives them. */
   private knownFileList = $derived(listedFiles(this.selectedFiles));
   /** The ID of the stop the reviewer has open in the selected tour, or null when none is open. Navigation stays in the page and never reaches the server. */
   get selectedStopId(): string | null {
      return this.selectedOpenStop?.stopId ?? null;
   }

   /** The position of the open stop in the selected tour's plan, as {@link OpenStop.index} says. */
   get selectedStopIndex(): number {
      return this.selectedOpenStop?.index ?? -1;
   }

   /** The open stop at its current version, as {@link OpenStop.current} says. */
   get currentStop(): Stop | null {
      return this.selectedOpenStop?.current ?? null;
   }

   /** The open stop's earlier versions, oldest first, empty while no stop is open. */
   get selectedStopVersions(): readonly KnownVersion[] {
      return this.selectedOpenStop?.versions ?? [];
   }

   /** The earlier stop version that the page shows of the open stop, as {@link OpenStop.shownVersion} says. */
   get shownVersion(): ShownVersion | null {
      return this.selectedOpenStop?.shownVersion ?? null;
   }

   /** The open stop at the version the page shows, as {@link OpenStop.stop} says. */
   get selectedStop(): Stop | null {
      return this.selectedOpenStop?.stop ?? null;
   }

   /** Whether the open stop is stale, as {@link OpenStop.stale} says. */
   get selectedStale(): StaleStatus {
      return this.selectedOpenStop?.stale ?? { kind: 'current' };
   }

   /** The open stop's passages at the pin, as {@link OpenStop.pinnedPassages} says. */
   get pinnedPassages(): readonly StopPassage[] {
      return this.selectedOpenStop?.pinnedPassages ?? [];
   }

   /** The open stop's primary passage at the pin, as {@link OpenStop.primaryPassage} says. */
   get pinnedPrimaryPassage(): StopPassage | null {
      return this.selectedOpenStop?.primaryPassage ?? null;
   }

   /** The open stop's passages at the pin in step through's order, as {@link OpenStop.stepPassages} says. */
   get stepPassages(): readonly StopPassage[] {
      return this.selectedOpenStop?.stepPassages ?? [];
   }

   /** The position of the focused passage in {@link LotsenApp.stepPassages}, as {@link OpenStop.focusedIndex} says. */
   get focusedPassageIndex(): number {
      return this.selectedOpenStop?.focusedIndex ?? -1;
   }

   /** The focused passage, or null when the open stop has no passages. */
   get focusedPassage(): StopPassage | null {
      return this.selectedOpenStop?.focusedPassage ?? null;
   }

   /** Whether the center stacks the open stop's files, which it does in the stop-files mode while a stop is open. */
   showsStopFiles = $derived(this.viewMode === 'stopFiles' && this.selectedStop !== null);
   /**
    * The files that the stop-files mode stacks: the open stop's files, then the files that the reviewer opened from outside the stop, or null while the changed files load.
    *
    * Without a loaded list of changed files, every file stacks as its text at its side, as {@link LotsenApp.openPassage} opens it.
    */
   stopFiles = $derived.by((): StackFile[] | null => {
      if (!this.showsStopFiles) {
         return [];
      }

      return this.selectedFiles?.state === 'loading' ? null : stopFileStack(this.pinnedPassages, this.knownFileList, this.stackAdded, this.decorationSwitches);
   });
   /**
    * The stacked file that the stop-files mode scrolls to, by its key, or null before the reviewer opens one. Each open makes a new object, so the stack scrolls again.
    *
    * The key follows the changed files, so a file opened before they arrive scrolls once they show whether it changed.
    */
   stackTarget = $derived.by((): { key: string } | null => {
      const key = this.stackTargetKey;
      return this.stackOpened === null || key === null ? null : { key };
   });
   /**
    * The stacked file that Files marks as open: the one the stop-files view reports that the reviewer reads, or the one it scrolled to last until the view reports one, or null for neither.
    *
    * A key that the stack no longer holds, as after a stop changes and before the view measures again, falls back the same way.
    */
   private stackShown = $derived.by((): StackFile | null => {
      const files = this.stopFiles ?? [];
      return files.find((file) => file.key === this.stackTopKey) ?? files.find((file) => file.key === this.stackTarget?.key) ?? null;
   });
   /** The path of the changed file that Files marks as open in the stop-files mode, or null when the marked stacked file is not a changed file. */
   stackFilePath = $derived(this.stackShown?.kind === 'diff' ? this.stackShown.file.path : null);
   /** The unchanged file that the whole-repository view marks as open in the stop-files mode, or null when the marked stacked file is a changed file. */
   stackText = $derived<FileReference | null>(this.stackShown?.kind === 'text' ? { side: this.stackShown.side, path: this.stackShown.path } : null);
   /** Claude's marks on the selected tour's files, by path, from the plan the page holds. A mark on a path that the pull request does not change marks nothing the page shows. */
   // eslint-disable-next-line svelte/prefer-svelte-reactivity -- a new map for each plan, which nothing changes.
   selectedFileMarks = $derived<ReadonlyMap<string, FileMark>>(new Map((this.selectedPlan?.plan?.fileMarks ?? []).map((mark) => [mark.path, mark])));
   /** The paths of the selected tour's changed files that the page shows as reviewed: the list's, with the reviewer's own marks in this page over them. */
   selectedReviewedPaths = $derived.by((): ReadonlySet<string> => {
      const known = this.selectedFiles;
      const files = known?.state === 'loaded' ? known.list.files : [];
      // eslint-disable-next-line svelte/prefer-svelte-reactivity -- a new set for each change, which nothing changes.
      const paths = new Set(known?.state === 'loaded' ? known.list.reviewedPaths : []);
      for (const [path, change] of Object.entries(this.selectedRecord?.reviewChanges ?? {})) {
         const file = files.find((candidate) => candidate.path === path);
         if (file !== undefined && marksVersion(change.shown, file)) {
            paths.add(path);
         } else {
            paths.delete(path);
         }
      }

      return paths;
   });
   /** The selected file in the selected tour's loaded list, or null when none is selected or the list lacks it. */
   selectedFile = $derived.by((): ChangedFile | null => {
      const known = this.selectedFiles;
      const path = this.selectedFilePath;
      return known?.state === 'loaded' && path !== null ? (known.list.files.find((file) => file.path === path) ?? null) : null;
   });

   /**
    * The decorations of the open file from the open stop's passages: the selected changed file's, or the opened text's, or none while no file is open. Only a diff collapses.
    *
    * The whole pull request decorates every passage of the stop. Step through decorates the focused passage alone, so the rest of the file dims and collapses around it. The lines of the hovered
    * reference get a mark of their own.
    */
   fileDecorations = $derived.by((): PassageDecorations => {
      const { decorations, paths } = this.openFilePassages;
      return paths === null ? decorations : withHoveredReference(decorations, paths, this.hoveredReference);
   });

   /**
    * The repository paths of the open file: the selected changed file's, or the opened text's, or null while no file is open.
    *
    * A text whose path the loaded list of changed files holds at neither side is the same file at both sides. Any other text, as while the list loads, is the file at its own side only.
    */
   openFilePaths = $derived.by((): FilePaths | null => {
      if (this.selectedFile !== null) {
         return changedFilePaths(this.selectedFile);
      }

      const text = this.selectedText;
      if (text === null) {
         return null;
      }

      const files = this.knownFileList;
      const unchanged = files !== null && !changedFilesAt(files, 'base').has(text.path) && !changedFilesAt(files, 'head').has(text.path);
      return unchanged ? { base: text.path, head: text.path } : { base: text.side === 'base' ? text.path : null, head: text.side === 'head' ? text.path : null };
   });

   /** The repository paths of the file that Files marks as open, which the Threads panel's current file follows: the stacked file in the stop-files mode, else the open file, or null for none. */
   currentFilePaths = $derived<FilePaths | null>(this.showsStopFiles ? (this.stackShown?.paths ?? null) : this.openFilePaths);

   /**
    * The open file's decorations without the hovered reference, and its paths, or null paths while no file is open.
    *
    * It is a derived value of its own, so a hover into another file does not decorate the open file again.
    */
   private openFilePassages = $derived.by((): { decorations: PassageDecorations; paths: FilePaths | null } => {
      const stepping = this.viewMode === 'stepThrough';
      const passages = stepping ? (this.focusedPassage === null ? [] : [this.focusedPassage]) : this.pinnedPassages;
      const paths = this.openFilePaths;
      if (paths === null) {
         return { decorations: noPassageDecorations, paths };
      }

      const decorations = filePassageDecorations(passages, paths, this.decorationSwitches, { collapse: this.selectedFile !== null && this.collapseToPassages, focused: stepping });
      return { decorations: withThreads(decorations, paths, this.selectedCodeThreads), paths };
   });

   private readonly options: AppOptions;
   private api: Api | null = null;
   private stream: EventStreamConnection | null = null;
   private listGeneration = 0;
   /** The number of times the event stream opened, so a load can tell whether a stream opened while it was in flight. */
   private streamOpens = 0;
   private eventsDuringListLoad: SseEvent[] | null = null;
   private reloadAfterListLoad = false;
   /**
    * The passage, code reference or file that opened while the selected tour's changed files were loading, which opens once they settle, or null when none waits.
    *
    * Only the list tells whether a file opens a diff.
    */
   private pendingOpen: { file: FileReference; passage: CodeReference | null } | null = null;
   /**
    * The history entry to restore once the tours have loaded and the plan of its tour is known, with the view mode that a tour it opens starts in, or null when none waits. `resume` is true for
    * the entry of a page load, whose tour opens where the reviewer left off when the entry names no stop, file or summary page.
    *
    * The page writes no history while one waits, so the entry keeps what it names.
    */
   private pendingNavigation = $state.raw<{ navigation: Navigation; defaultViewMode: ViewMode; resume: boolean } | null>(null);
   /** The file that a restored entry opened, which falls back to the open stop's primary passage when the server no longer has it, or null after any other file opens. */
   private restoredFile: FileReference | null = null;
   /** The files that a restored entry added to the stack, which leave it when the server no longer has them, until the stack clears. */
   private restoredAdded: readonly FileReference[] = [];
   /** Ends the effect that writes each change of {@link LotsenApp.navigation} into the current history entry. */
   private disposeHistoryEffect: (() => void) | null = null;
   /**
    * The IDs of the tours deleted while the page is open, on this page or another, so a list load, a write's answer or an event that predates the delete brings none of them back. The server
    * never gives a tour ID again.
    */
   // eslint-disable-next-line svelte/prefer-svelte-reactivity -- the IDs of the deleted tours, which nothing renders.
   private readonly deletedTours = new Set<string>();
   /**
    * The tour that opens where the reviewer left off once its load brings the reading progress, or null for none. Opening a tour from the list, or a page load whose entry names a tour but no
    * stop, file or summary page, sets it, and any move of the reviewer ends it.
    */
   private resumeTour: string | null = null;
   /** The place that the reviewer has open, which the effect in {@link LotsenApp.start} records as reading progress, or null while no stop of the plan or summary page shows. */
   private readingPlace = $derived.by((): { tourId: string; place: ReadingPlace } | null => {
      const tourId = this.selectedTourId;
      if (tourId === null || this.pendingNavigation !== null) {
         return null;
      }

      if (this.showsSummary) {
         return { tourId, place: 'summary' };
      }

      const stop = this.selectedStop;
      if (stop === null || stop === undefined) {
         return null;
      }

      // An earlier version that the page holds on screen does not count as seeing the current one.
      const version = this.shownVersion === null ? versionOf(stop) : undefined;
      return { tourId, place: version === undefined ? { stopId: stop.id } : { stopId: stop.id, version } };
   });
   /** The number of starts and stops so far, which tells a start that resumes after its session whether it is still the current one. */
   private runs = 0;
   /** The files that the reviewer opened from outside the open stop in the stop-files mode, in the order opened. A change of the stop or the mode clears them, and the history entry keeps them for its stop. */
   private stackAdded = $state.raw<readonly FileReference[]>([]);
   /** The passage or the file that the reviewer opened last in the stop-files mode, which the single-file view opens once the center no longer shows the stack, or null before one opens. */
   private stackOpened = $state.raw<{ reference: FileReference; passage: CodeReference | null } | null>(null);
   /** The key of the stacked file that the stop-files view last reported that the reviewer reads, or null before it reports one. */
   private stackTopKey = $state<string | null>(null);
   // A string, so a reload of an unchanged list of changed files does not scroll the stack again.
   private stackTargetKey = $derived(this.stackOpened === null ? null : stackFileKey(this.knownFileList, this.stackOpened.reference));
   private diffLoad = 0;
   private diffIncludesLarge = false;
   /** The pin that the open diff's last load read at, as {@link LotsenApp.readPin} says. */
   private diffPin: string | null = null;
   /** The old path that the open diff's last load read a renamed file across, or undefined when it read none. */
   private diffOldPath: string | undefined = undefined;
   private textLoad = 0;
   private textIncludesLarge = false;
   /** The pin that the open file's last load read at, as {@link LotsenApp.readPin} says. */
   private textPin: string | null = null;
   /** A counter that orders the answers to review mark requests against the starts of list loads. */
   private reviewClock = 0;
   // eslint-disable-next-line svelte/prefer-svelte-reactivity -- the last review mark request of each file, by the JSON of its tour ID and path, which nothing renders.
   private readonly reviewRequests = new Map<string, Promise<void>>();

   constructor(options: AppOptions) {
      this.options = options;
   }

   /**
    * Starts the session, and restores what the current history entry names, opening its tour in the default view mode.
    *
    * @param defaultViewMode Returns the default view mode, which the page reads again once it holds the server's preferences.
    */
   async start(defaultViewMode: () => ViewMode): Promise<void> {
      const { environment } = this.options;
      this.pendingNavigation = { navigation: readNavigation(environment.location.hash, environment.history.state), defaultViewMode: defaultViewMode(), resume: true };
      const run = ++this.runs;
      let credential: string | null;
      try {
         credential = await startSession(environment);
      } catch {
         if (run === this.runs) {
            this.phase = 'unreachable';
         }

         return;
      }

      // A stop, or a later start, while the session started ends this start.
      if (run !== this.runs) {
         return;
      }

      if (credential === null) {
         this.phase = 'signedOut';
         return;
      }

      const api = createApi(environment.fetch, credential, (controls) => this.applyControls(controls));
      this.api = api;
      // The workspace reads its layout once, as it mounts, so the page shows only once it holds the server's preferences, and a failed read leaves it unreachable.
      try {
         await this.settlePreferences(api, run);
      } catch (error) {
         if (run === this.runs) {
            this.handleError(error, () => (this.phase = 'unreachable'));
         }

         return;
      }

      if (run !== this.runs) {
         return;
      }

      // The default view mode is a preference, so a tour that the address names opens in the server's default.
      if (this.pendingNavigation !== null) {
         this.pendingNavigation = { ...this.pendingNavigation, defaultViewMode: defaultViewMode() };
      }

      this.phase = 'ready';
      this.disposeHistoryEffect?.();
      this.disposeHistoryEffect = $effect.root(() => {
         $effect(() => {
            const navigation = this.navigation;
            if (this.pendingNavigation === null) {
               untrack(() => this.writeHistory(navigation, false));
            }
         });
         // Without a hidden tour the list offers no toggle, so hidden tours stop showing and a tour archived later hides.
         $effect(() => {
            if (this.hiddenTourCount === 0) {
               this.showHiddenTours = false;
            }
         });
         $effect(() => {
            const reading = this.readingPlace;
            if (reading !== null) {
               untrack(() => this.recordPlace(reading.tourId, reading.place));
            }
         });
      });
      this.stream = connectEventStream({
         fetch: environment.fetch,
         credential,
         retryDelaysMs: this.options.retryDelaysMs,
         silenceTimer: this.options.silenceTimer,
         onOpen: () => {
            this.streamOpens++;
            // The stream carries no change from while it was closed, so the page reads the preferences again. A failed read keeps the page's choices until the next open.
            this.settlePreferences(api, run).catch(() => {});
            for (const record of this.records.values()) {
               record.streamOpened();
            }

            void this.reload();
         },
         onEvent: (event) => this.handleEvent(event),
         // Each failure gets a reload whose list request follows the event. A list load in flight may predate the event, so that reload starts once the load lands.
         onEventFailed: () => {
            if (this.eventsDuringListLoad === null) {
               void this.reload();
            } else {
               this.reloadAfterListLoad = true;
            }
         },
         onUnauthorized: () => this.signOut(),
         onStateChange: (state) => (this.connection = state),
      });
   }

   stop(): void {
      this.runs++;
      detachPreferences();
      this.reloadAfterListLoad = false;
      this.stream?.close();
      this.stream = null;
      this.disposeHistoryEffect?.();
      this.disposeHistoryEffect = null;
   }

   /**
    * Opens a tour in the default view mode, or loads the open tour's plan and changed files again, keeping its stop and its view mode. It adds a history entry.
    *
    * Another tour opens where the reviewer left off once its load brings the reading progress, as {@link LotsenApp.resume} says.
    */
   selectTour(tourId: string, defaultViewMode: ViewMode): void {
      const opening = tourId !== this.selectedTourId;
      this.move(() => this.showTour(tourId, defaultViewMode));
      this.resumeTour = opening ? tourId : null;
   }

   /** Shows the tours that the list hides, or hides them again. */
   setShowHiddenTours(show: boolean): void {
      this.showHiddenTours = show;
   }

   /**
    * Archives a tour or unarchives it, with or without a bound session. The tour's state comes from its `tourStateChanged` event alone.
    *
    * @returns Null once stored, or the reason the write failed.
    */
   async setTourArchived(tourId: string, archived: boolean): Promise<string | null> {
      try {
         await this.requireApi().setTourArchived(tourId, archived);
         return null;
      } catch (error) {
         return this.refusalOf(error, archived ? 'The tour could not be archived.' : 'The tour could not be unarchived.');
      }
   }

   /**
    * Deletes a tour for good, with or without a bound session. The page drops the tour once the server answers, as the `tourDeleted` event does, and leaves it when it shows it.
    *
    * @returns Null once the tour is gone, also when the server no longer had it, or the reason the delete failed.
    */
   async deleteTour(tourId: string): Promise<string | null> {
      try {
         await this.requireApi().deleteTour(tourId);
      } catch (error) {
         if (!(error instanceof ApiError && error.status === 404)) {
            return this.refusalOf(error, 'The tour could not be deleted.');
         }
      }

      this.dropTour(tourId);
      return null;
   }

   /**
    * Removes the repository clones that no tour refers to, and the temporary clones that an interrupted clone left.
    *
    * @returns The number of clones removed, or the reason the prune failed.
    */
   async pruneClones(): Promise<PruneOutcome> {
      try {
         return { removed: (await this.requireApi().pruneClones()).removed };
      } catch (error) {
         return { failure: this.refusalOf(error, 'The clones could not be pruned.') };
      }
   }

   /** Forgets a deleted tour's row and its record, and leaves the tour when the page shows it. */
   private dropTour(tourId: string): void {
      this.deletedTours.add(tourId);
      this.rows = this.rows.filter((row) => row.summary.id !== tourId);
      this.records.delete(tourId);
      this.threadSides.delete(tourId);
      if (this.resumeTour === tourId) {
         this.resumeTour = null;
      }

      if (this.selectedTourId === tourId) {
         this.leaveTour();
         this.selectedTourId = null;
         this.selectedTourError = null;
      }
   }

   private showTour(tourId: string, defaultViewMode: ViewMode): void {
      if (this.selectedTourId !== tourId) {
         this.showViewMode(defaultViewMode);
         this.collapseToPassages = false;
         this.leaveTour();
      }

      this.recordOf(tourId);
      this.selectedTourId = tourId;
      this.selectedTourError = null;
      void this.loadPlan(tourId);
      this.loadFilesIfPinned(tourId);
   }

   /** Returns what the page knows of a tour, or undefined before the page hears of it. */
   tourRecord(tourId: string): TourRecord | undefined {
      return this.records.get(tourId);
   }

   /** Returns the tour's record, which it creates for a tour that the page knows nothing of yet, or undefined for a deleted tour. */
   private recordOf(tourId: string): TourRecord | undefined {
      if (this.deletedTours.has(tourId)) {
         return undefined;
      }

      let record = this.records.get(tourId);
      if (record === undefined) {
         const created = new TourRecord(tourId);
         record = created;
         // A tour that the page does not show has the place that `resume` reopens, so a write's reveal there judges the thread by that place.
         const view = new ThreadsView(record, () => (tourId === this.selectedTourId ? { stopId: openStop.stopId, file: this.currentFilePaths } : { stopId: resumedStopId(created), file: null }));
         const page = {
            operations: () => this.requireApi(),
            touch: (revision: number, updatedAt: string) => this.touchTour(tourId, revision, updatedAt),
            signOut: () => this.signOut(),
         };
         const pin = () => (tourId === this.selectedTourId ? this.selectedPinCommits : pinCommits(this.tours.find((tour) => tour.id === tourId)?.pin));
         const openStop = new OpenStop(record, pin, () => pinIteration(this.tours.find((tour) => tour.id === tourId)?.pin));
         this.threadSides.set(tourId, { view, actions: new ThreadActions(tourId, page, record, view), openStop });
         this.records.set(tourId, record);
      }

      return record;
   }

   /** Returns a tour's thread actions, or undefined before the page hears of the tour. */
   private threadActions(tourId: string): ThreadActions | undefined {
      return this.threadSides.get(tourId)?.actions;
   }

   /** Clears what the page shows of the selected tour, but not the view mode. */
   private leaveTour(): void {
      this.showsSummary = false;
      this.openedCheck = null;
      this.openedThread = null;
      this.selectedThreadsView?.drop();
      this.selectedOpenStop?.open(null);
      this.hoveredReference = null;
      this.closeFile();
      this.reviewError = null;
   }

   /** Opens a stop of the selected tour's plan, and its primary passage when the stop has one. Another stop than the open one adds a history entry. */
   openStop(stopId: string): void {
      if (stopId === this.selectedStopId) {
         this.showStop(stopId);
         // The open stop leaves the reading place unchanged, so the effect that records it does not run, and a place whose write failed goes again from here.
         const reading = this.readingPlace;
         if (reading !== null) {
            this.recordPlace(reading.tourId, reading.place);
         }
      } else {
         this.move(() => this.showStop(stopId));
      }
   }

   private showStop(stopId: string): void {
      this.enterStop(stopId);
      const passage = this.pinnedPrimaryPassage;
      if (passage) {
         this.openPassage(passage);
      }
   }

   /** Makes a stop the open one, with no passage of it focused yet. A new stop's stack opens with no file, so the single-file view closes too and the navigation names none until one opens. */
   private enterStop(stopId: string | null): void {
      const changed = stopId !== this.selectedStopId;
      if (changed) {
         this.clearStack();
         this.hoveredReference = null;
         this.openedCheck = null;
      }

      this.showsSummary = false;
      this.selectedOpenStop?.open(stopId);
      this.pendingOpen = null;
      if (changed && this.showsStopFiles) {
         this.closeFile();
      }
   }

   /** Shows a version of the open stop by its number: the current one, or an earlier one read-only. A number that the page knows no version of changes nothing. */
   openVersion(number: number): void {
      this.selectedOpenStop?.readVersion(number);
   }

   /** Shows the open stop's current version. */
   showCurrentVersion(): void {
      this.selectedOpenStop?.moveOn();
   }

   /**
    * Opens a check of a stop of the selected tour's plan: the stop, at the earlier version that holds the check when `version` names one, and the check itself, as {@link LotsenApp.openedCheck}
    * says. A stop that the plan no longer has stays closed.
    */
   openCheck(stopId: string, checkId: string, version?: number): void {
      if (!this.selectedPlan?.plan?.stops.some((stop) => stop.id === stopId)) {
         return;
      }

      this.openStop(stopId);
      if (version !== undefined) {
         this.openVersion(version);
      }

      this.openedCheck = new CheckOpening(stopId, checkId);
   }

   /**
    * Returns a test of whether a `ref:` link in the summary's prose of a stop is at the pin, as {@link referenceAtPin} says for the stop's current version, or for its earlier version `version`.
    * Prose of a stop or a version that the page does not know opens no link while the tour is pinned.
    */
   summaryReferenceAtPin(stopId: string, version?: number): (reference: CodeReference) => boolean {
      const source =
         version === undefined
            ? this.selectedPlan?.plan?.stops.find((stop) => stop.id === stopId)
            : this.selectedRecord?.earlierVersions.find((known) => known.stopId === stopId && known.number === version);
      const prose = source === undefined ? { content: null } : 'stage' in source ? { content: contentOf(source), staleOn: staleOnOf(source) } : { content: source.content };
      return referenceAtPin(prose, version === undefined && source !== undefined, this.selectedPinCommits);
   }

   /** Opens the selected tour's summary page in place of the open stop and file, and loads the summary. It adds a history entry. */
   openSummary(): void {
      const tourId = this.selectedTourId;
      if (tourId !== null) {
         this.move(() => this.showSummary(tourId));
      }
   }

   // A summary page that opens anew loads the summary afresh, so it never shows an older one while the load runs.
   private showSummary(tourId: string): void {
      const record = this.recordOf(tourId);
      if (!this.showsSummary && record !== undefined) {
         record.summary = { state: 'loading' };
      }

      this.enterStop(null);
      this.closeFile();
      this.showsSummary = true;
      this.loadSummary(tourId);
   }

   /** Opens the code reference of a `ref:` link in the open stop's prose, as {@link LotsenApp.openPassage} does. It adds a history entry. */
   followReference(reference: CodeReference): void {
      this.move(() => this.openPassage(reference));
   }

   /**
    * Opens where a thread was asked: the lines of a code anchor as a `ref:` link opens them, a file's diff or text, or the stop of a stop text or a stop anchor. A tour anchor opens nothing.
    * A stop that the plan no longer has stays closed.
    */
   openAnchor(anchor: ThreadAnchor): void {
      switch (anchor.kind) {
         case 'code':
            this.followReference({ side: anchor.side, path: anchor.path, startLine: anchor.startLine, endLine: anchor.endLine });
            return;
         case 'file':
            this.openRepositoryFile(anchor.side, anchor.path);
            return;
         case 'stopText':
         case 'stop':
            if (this.selectedPlan?.plan?.stops.some((stop) => stop.id === anchor.stopId)) {
               this.openStop(anchor.stopId);
            }

            return;
         case 'tour':
            return;
      }
   }

   /** Hands code, a file or text in a stop to the Threads panel's ask, in the selected tour. */
   pickAnchor(anchor: PickedAnchor, quote: string | null): void {
      this.selectedThreadsView?.pick(anchor, quote);
   }

   /**
    * Opens a thread of the selected tour in the Threads panel's thread view, by its ID, and widens the panel's filter when it hides the thread.
    *
    * A thread about a file that the stop-files mode stacks makes that file the current file first, so a current-file filter keeps showing it.
    */
   openThread(threadId: string): void {
      const anchor = this.selectedRecord?.thread(threadId)?.anchor;
      const stacked = anchor?.kind === 'code' || anchor?.kind === 'file' ? this.stopFiles?.find((file) => inFile(anchor, file.paths)) : undefined;
      if (stacked !== undefined) {
         this.stackTopKey = stacked.key;
      }

      this.selectedThreadsView?.open(threadId);
      this.openedThread = { id: threadId };
   }

   /**
    * Restores what the current history entry names, as after the browser's back or forward: the tour in the default view mode when it is another tour, the stop, the file at its lines and the
    * files added to the stack.
    *
    * It adds no history entry. It waits for the tours and the tour's plan, so a tour, stop or file that is gone falls back as {@link LotsenApp.applyPendingNavigation} says. A fragment with a one-time code
    * is left to the page load that exchanges it.
    */
   restoreNavigation(defaultViewMode: ViewMode): void {
      const { location, history } = this.options.environment;
      if (linkCode(location.hash) === null) {
         this.pendingNavigation = { navigation: readNavigation(location.hash, history.state), defaultViewMode, resume: false };
         this.applyPendingNavigation();
      }
   }

   /**
    * Restores the waiting history entry once the tours have loaded and the plan of its tour is known.
    *
    * A tour that the list lacks shows the tour list. A stop that the plan lacks closes, and an entry without a file closes the open one. An entry of the summary page restores it without
    * waiting for the plan.
    */
   private applyPendingNavigation(): void {
      const pending = this.pendingNavigation;
      if (pending === null || !this.toursLoaded) {
         return;
      }

      const { tourId, stopId, file, lines, added } = pending.navigation;
      if (tourId === null || !this.tours.some((tour) => tour.id === tourId)) {
         this.pendingNavigation = null;
         this.leaveTour();
         this.selectedTourId = null;
         this.selectedTourError = null;
         return;
      }

      if (tourId !== this.selectedTourId) {
         this.showTour(tourId, pending.defaultViewMode);
      }

      // The summary page needs no plan, so it shows while the plan loads.
      if (pending.navigation.summary) {
         this.pendingNavigation = null;
         this.showSummary(tourId);
         return;
      }

      const plan = this.records.get(tourId)?.plan;
      if (plan === undefined && this.selectedTourError === null) {
         return;
      }

      this.pendingNavigation = null;
      this.resumeTour = pending.resume && stopId === null && file === null ? tourId : null;
      this.enterStop(stopId !== null && plan?.stops.some((stop) => stop.id === stopId) ? stopId : null);
      this.clearStack();
      if (file === null) {
         this.closeFile();
      }

      // The stack then holds the stop's files, the files that the entry added and the entry's file.
      if (this.showsStopFiles) {
         this.stackAdded = added;
         this.restoredAdded = added;
      }

      if (file !== null && lines !== null) {
         this.openPassage({ ...file, ...lines });
      } else if (file !== null) {
         this.opened = { file, lines: null };
         this.pendingOpen = this.records.get(tourId)?.files?.state === 'loading' ? { file, passage: null } : null;
         if (this.pendingOpen === null) {
            this.showFile(file);
         }
      }

      this.restoredFile = file;
   }

   /** Closes the open file, in the single-file view and in the stack. */
   private closeFile(): void {
      this.focusTarget = null;
      this.pendingOpen = null;
      this.opened = null;
      this.clearStack();
      this.selectedFilePath = null;
      this.selectedDiff = null;
      this.diffLoad++;
      this.closeText();
   }

   /** Makes a change that the reviewer's move causes. A change of the navigation adds a history entry and ends a waiting restore, and any other change does neither. */
   private move(change: () => void): void {
      this.resumeTour = null;
      const before = navigationFragment(this.navigation);
      change();
      if (navigationFragment(this.navigation) !== before) {
         this.pendingNavigation = null;
         this.writeHistory(this.navigation, true);
      }
   }

   private writeHistory(navigation: Navigation, move: boolean): void {
      const { location, history } = this.options.environment;
      const entry = this.currentEntry();
      const recorded = recordedNavigation(entry, navigation, this.showsStopFiles, move);
      const write = historyWrite(entry, recorded, move);
      const url = location.pathname + location.search + navigationFragment(recorded);
      if (write === 'push') {
         history.pushState(navigationState(recorded), '', url);
      } else if (write === 'replace') {
         history.replaceState(navigationState(recorded), '', url);
      }
   }

   /** The navigation that the current history entry names. */
   private currentEntry(): Navigation {
      const { location, history } = this.options.environment;
      return readNavigation(location.hash, history.state);
   }

   /**
    * Moves the open stop's explanation to a place, which wins over Claude's hint and the rule in mixed placement.
    *
    * Moving it inline also opens the primary passage, so the block shows where the reviewer asked for it.
    */
   movePlacement(placement: StopPlacement): void {
      const record = this.selectedRecord;
      if (record === undefined || this.selectedStopId === null) {
         return;
      }

      record.placements[this.selectedStopId] = placement;
      const primary = this.pinnedPrimaryPassage;
      if (placement === 'inline' && primary !== null) {
         this.openPassage(primary);
      }
   }

   /** Sets the hovered reference, or clears it with null. */
   hoverReference(reference: CodeReference | null): void {
      this.hoveredReference = reference;
   }

   /** Opens the next stop in the plan that the reviewer did not skip, from the open stop or from the start when no stop is open, or the summary page after the last, as {@link steppedTarget} says. */
   openNextStop(): void {
      this.openSteppedStop(1);
   }

   /** Opens the stop before the open one in the plan that the reviewer did not skip, when there is one, or from the summary page the last such stop. */
   openPreviousStop(): void {
      this.openSteppedStop(-1);
   }

   /** Opens the passage after the focused one in step through's order, when there is one. The last passage leaves the next stop to the stop navigation. */
   openNextPassage(): void {
      this.openStepPassageAt(this.focusedPassageIndex + 1);
   }

   /** Opens the passage before the focused one in step through's order, when there is one. */
   openPreviousPassage(): void {
      this.openStepPassageAt(this.focusedPassageIndex - 1);
   }

   /**
    * Shows stops in a view mode until another tour opens. A change of the mode shows every collapsed region again.
    *
    * Step through opens the focused passage, so the view shows the passage that it decorates. Leaving the stop-files mode opens the passage or the file that the reviewer opened last in the stack, and the
    * history entry keeps the files added to the stack, which the stack shows again on entering the mode.
    */
   setViewMode(mode: ViewMode): void {
      if (mode === this.viewMode) {
         return;
      }

      const stacked = this.showsStopFiles;
      this.showViewMode(mode);
      if (stacked) {
         this.leaveStack();
      } else {
         this.clearStack();
         this.restoreEntryStack();
      }

      if (mode === 'stepThrough' && this.focusedPassage !== null) {
         this.openPassage(this.focusedPassage);
      }
   }

   /**
    * Opens the file of a passage or a code reference at its side, as {@link LotsenApp.openRepositoryFile} does, and scrolls the view to its start line. A passage of the open stop becomes the focused passage.
    *
    * Only the list of changed files tells whether the passage opens a diff, so while the list loads, the passage waits for it. A list that fails to load opens the passage as the file at its side.
    * While the center stacks the open stop's files, the passage scrolls the stack instead, as {@link LotsenApp.showInStack} does.
    */
   openPassage(passage: CodeReference): void {
      this.selectedOpenStop?.focus(passage);
      const tourId = this.selectedTourId;
      const file = { side: passage.side, path: passage.path };
      this.showsSummary = false;
      this.opened = { file, lines: { startLine: passage.startLine, endLine: passage.endLine } };
      this.restoredFile = null;
      this.pendingOpen = tourId !== null && this.records.get(tourId)?.files?.state === 'loading' ? { file, passage } : null;
      if (this.pendingOpen === null) {
         this.showPassage(passage);
      }
   }

   private showPassage(passage: CodeReference): void {
      if (this.showsStopFiles) {
         this.showInStack(passage, passage);
         return;
      }

      this.showRepositoryFile(passage.side, passage.path);
      const path = this.selectedFilePath ?? this.selectedText?.path;
      this.focusTarget =
         path === undefined ? null : { lines: { path, side: passage.side, line: passage.startLine }, commit: this.selectedPinCommits?.[passage.side] ?? null, passage: this.placeOf(passage) };
   }

   /** Returns the place of the open stop's passage that a passage or a code reference opens, as {@link OpenStop.placeOf} says, or null while no stop is open. */
   private placeOf(reference: CodeReference): PassagePlace | null {
      return this.selectedOpenStop?.placeOf(reference) ?? null;
   }

   /**
    * Scrolls the stop-files stack to a file, and to the passage's start line when a passage opens it. The file joins the bottom of the stack first when the open stop's passages do not stack it.
    *
    * The single-file view keeps what it showed, because the stack loads its own files. {@link LotsenApp.leaveStack} opens the file there later.
    */
   private showInStack(reference: FileReference, passage: CodeReference | null): void {
      const files = this.knownFileList;
      const key = stackFileKey(files, reference);
      const added = this.stackAdded.some((candidate) => candidate.side === reference.side && candidate.path === reference.path);
      if (!added && !stopFileStack(this.pinnedPassages, files, [], this.decorationSwitches).some((file) => file.key === key)) {
         this.stackAdded = [...this.stackAdded, { side: reference.side, path: reference.path }];
      }

      this.stackOpened = { reference, passage };
      const changed = files === null ? undefined : changedFilesAt(files, reference.side).get(reference.path);
      this.focusTarget =
         passage === null
            ? null
            : { lines: { path: changed?.path ?? passage.path, side: passage.side, line: passage.startLine }, commit: this.selectedPinCommits?.[passage.side] ?? null, passage: this.placeOf(passage) };
   }

   /** Stacks the files that the current history entry added to the open stop's stack, once the center shows the stack. */
   private restoreEntryStack(): void {
      const entry = this.currentEntry();
      if (this.showsStopFiles && this.pendingNavigation === null && entry.tourId === this.selectedTourId && entry.stopId === this.selectedStopId) {
         this.stackAdded = entry.added;
         this.restoredAdded = entry.added;
      }
   }

   private clearStack(): void {
      this.stackAdded = [];
      this.restoredAdded = [];
      this.stackOpened = null;
   }

   /** Sets the view mode, and shows every collapsed region again when the mode changes. */
   private showViewMode(mode: ViewMode): void {
      if (mode !== this.viewMode) {
         this.viewMode = mode;
         this.collapseToPassages = false;
      }
   }

   /** Clears the stack once the center no longer shows it, and opens the passage or the file that the reviewer opened last in it in the single-file view. */
   private leaveStack(): void {
      const opened = this.stackOpened;
      this.clearStack();
      if (opened?.passage) {
         this.showPassage(opened.passage);
      } else if (opened) {
         this.focusTarget = null;
         this.showRepositoryFile(opened.reference.side, opened.reference.path);
      }
   }

   /** Records the stacked file that the reviewer reads, by its key, or null when the stop-files view shows none. */
   setStackTop(key: string | null): void {
      this.stackTopKey = key;
   }

   /**
    * Selects a file of the selected tour and loads its diff, unless that diff is already loaded or in progress. While the center stacks the open stop's files, the file scrolls the stack instead.
    * It adds a history entry.
    */
   selectFile(path: string): void {
      this.move(() => {
         this.focusTarget = null;
         this.pendingOpen = null;
         const known = this.selectedFiles;
         const file = known?.state === 'loaded' ? known.list.files.find((candidate) => candidate.path === path) : undefined;
         const reference: FileReference = { side: file?.kind === 'deleted' ? 'base' : 'head', path };
         this.showsSummary = false;
         this.opened = { file: reference, lines: null };
         this.restoredFile = null;
         if (this.showsStopFiles && file) {
            this.showInStack(reference, null);
         } else {
            this.showDiff(path);
         }
      });
   }

   private showDiff(path: string): void {
      this.closeText();
      this.selectedFilePath = path;
      const known = this.selectedDiff;
      const current = known?.path === path && (known.state === 'loading' || known.state === 'loaded');
      if (this.selectedTourId !== null && !current) {
         this.loadDiff(this.selectedTourId, path);
      }
   }

   /**
    * Opens a file from the whole-repository view at one side of the selected tour: a changed file's diff, as {@link LotsenApp.selectFile} does, or else the file's text at that side.
    *
    * Without a loaded list of changed files, every file opens as unchanged. While the center stacks the open stop's files, the file scrolls the stack instead. It adds a history entry.
    */
   openRepositoryFile(side: FileSide, path: string): void {
      this.move(() => {
         this.pendingOpen = null;
         this.showsSummary = false;
         this.opened = { file: { side, path }, lines: null };
         this.restoredFile = null;
         this.showFile({ side, path });
      });
   }

   private showFile(file: FileReference): void {
      this.focusTarget = null;
      if (this.showsStopFiles) {
         this.showInStack(file, null);
      } else {
         this.showRepositoryFile(file.side, file.path);
      }
   }

   private openStepPassageAt(index: number): void {
      const passage = index >= 0 ? this.stepPassages[index] : undefined;
      if (passage) {
         this.openPassage(passage);
      }
   }

   private openSteppedStop(step: 1 | -1): void {
      const stops = this.selectedPlan?.plan?.stops ?? [];
      const target = steppedTarget(stops, this.showsSummary ? 'summary' : this.selectedStopIndex, this.selectedSkippedStops, step, this.selectedSummaryFollows);
      if (target === 'summary') {
         this.openSummary();
      } else if (target !== null) {
         this.openStop(stops[target]!.id);
      }
   }

   private showRepositoryFile(side: FileSide, path: string): void {
      const tourId = this.selectedTourId;
      if (tourId === null) {
         return;
      }

      const known = this.records.get(tourId)?.files;
      const changed = known?.state === 'loaded' ? changedFilesAt(known.list.files, side).get(path) : undefined;
      if (changed) {
         this.showDiff(changed.path);
         return;
      }

      this.selectedFilePath = null;
      this.selectedDiff = null;
      this.diffLoad++;
      const current = this.selectedText;
      if (current?.side !== side || current.path !== path || (current.state !== 'loading' && current.state !== 'loaded')) {
         this.loadText(tourId, side, path);
      }
   }

   /** Loads the open large file's text, which the server withholds until a request includes it. */
   loadLargeText(): void {
      const known = this.selectedText;
      if (this.selectedTourId !== null && known?.state === 'loaded' && known.text.content === 'large') {
         this.loadText(this.selectedTourId, known.side, known.path, true);
      }
   }

   /**
    * Lists one directory of a tour at one side, or the root directory when the path is null.
    *
    * @returns The listing, or the failure to show in its place. A refused UI credential signs the page out.
    */
   async listDirectory(tourId: string, side: FileSide, path: string | null): Promise<KnownDirectory> {
      try {
         return { state: 'loaded', listing: await this.requireApi().listDirectory(tourId, side, path) };
      } catch (error) {
         if (error instanceof UnauthorizedError) {
            this.signOut();
         }

         const reason = error instanceof ApiError ? (error.error?.reason ?? null) : null;
         return { state: 'failed', message: path === null ? 'The repository could not be listed.' : 'The folder could not be listed.', reason };
      }
   }

   /**
    * Loads the diff of one of a tour's changed files for the stop-files stack, which keeps each file's content itself. A renamed file's diff spans the rename.
    *
    * @returns The diff, or the failure to show in its place. A refused UI credential signs the page out.
    */
   async fetchDiff(tourId: string, file: ChangedFile, options: { includeLarge: boolean; ignoreWhitespace: boolean }): Promise<KnownDiff> {
      try {
         return { state: 'loaded', path: file.path, diff: await this.requireApi().diffFile(tourId, file.path, { ...options, oldPath: file.kind === 'renamed' ? file.oldPath : undefined }) };
      } catch (error) {
         if (error instanceof UnauthorizedError) {
            this.signOut();
         }

         return diffFailure(file.path, error);
      }
   }

   /**
    * Loads one file's text at one side of a tour for the stop-files stack, which keeps each file's content itself.
    *
    * @returns The text, or the failure to show in its place. A refused UI credential signs the page out.
    */
   async fetchText(tourId: string, side: FileSide, path: string, includeLarge: boolean): Promise<KnownText> {
      try {
         return { state: 'loaded', side, path, text: await this.requireApi().readFileText(tourId, side, path, includeLarge) };
      } catch (error) {
         if (error instanceof UnauthorizedError) {
            this.signOut();
         } else if (tourId === this.selectedTourId) {
            this.fallBackFromRestoredFile(side, path, error);
         }

         return textFailure(side, path, error);
      }
   }

   /** Loads the selected large file's diff with its texts, which the server withholds until a request includes them. */
   loadLargeDiff(): void {
      const known = this.selectedDiff;
      if (this.selectedTourId !== null && known?.state === 'loaded' && known.diff.content === 'large') {
         this.loadDiff(this.selectedTourId, known.path, true);
      }
   }

   /**
    * Sets or clears the reviewer's review mark on a changed file of the selected tour, and shows the change at once.
    *
    * The requests for one file reach the server one after another, so the server ends with the last. When the last fails, the file shows what the server last stored, and
    * {@link LotsenApp.reviewError} says so.
    */
   setFileReviewed(path: string, reviewed: boolean): void {
      const tourId = this.selectedTourId;
      const record = this.selectedRecord;
      const known = record?.files;
      const file = known?.state === 'loaded' ? known.list.files.find((candidate) => candidate.path === path) : undefined;
      if (tourId === null || record === undefined || file === undefined || this.selectedReviewedPaths.has(path) === reviewed) {
         return;
      }

      // Each change is read back from the state, so its updates go through the state's proxy.
      const headObjectId = file.headObjectId ?? null;
      const sent: ReviewState = { reviewed, headObjectId };
      record.reviewChanges[path] ??= { shown: { reviewed: !reviewed, headObjectId }, stored: { reviewed: !reviewed, headObjectId }, pending: 0, settled: 0 };
      const change = record.reviewChanges[path];
      change.shown = sent;
      change.pending++;

      const api = this.requireApi();
      const key = JSON.stringify([tourId, path]);
      const request = (this.reviewRequests.get(key) ?? Promise.resolve()).then(() => (reviewed ? api.markFileReviewed(tourId, path, headObjectId) : api.clearFileReviewed(tourId, path)));
      const last = request.catch(() => undefined);
      this.reviewRequests.set(key, last);
      void last.then(() => {
         if (this.reviewRequests.get(key) === last) {
            this.reviewRequests.delete(key);
         }
      });
      const settle = () => {
         change.pending--;
         change.settled = ++this.reviewClock;
      };

      void request.then(
         () => {
            settle();
            change.stored = sent;
            if (tourId === this.selectedTourId) {
               this.reviewError = null;
            }
         },
         (error: unknown) => {
            settle();
            this.handleError(error, () => {
               if (change.pending === 0) {
                  change.shown = change.stored;
               }

               if (tourId === this.selectedTourId) {
                  this.reviewError = `The review mark on ${path} could not be saved.`;
               }
            });
         },
      );
   }

   /**
    * Requests a tour action of the selected tour's bound session about the open stop, text that the reviewer selected in it, or the whole tour, and adds its thread to the tour's threads at once.
    * Explain and revise come as their kind alone, and `about` names their target.
    *
    * The action names the open stop as the one the reviewer viewed, while the plan has the stop. Words that hold only whitespace are left out, so the action goes without words.
    *
    * @returns Null once the server stored the action, or the server's reason when it refused it. A refused UI credential signs the page out.
    */
   async requestAction(
      action: 'explain' | 'revise' | Exclude<RequestedTourAction, { kind: 'explain' | 'revise' }>,
      about: 'stop' | 'tour' | Extract<PickedAnchor, { kind: 'stopText' }>,
      text: string,
   ): Promise<string | null> {
      const tourId = this.selectedTourId;
      const stopId = this.selectedOpenStop?.viewedStopId ?? null;
      if (tourId === null) {
         return 'Open a tour first.';
      }

      if (stopId === null && about !== 'tour') {
         return 'Open a stop first.';
      }

      // Only explain and revise name what they are about, and every other kind applies to the whole tour.
      let request: RequestedTourAction;
      if (typeof action === 'string') {
         if (about === 'tour') {
            return 'Open a stop first.';
         }

         request = { kind: action, target: about === 'stop' ? { kind: 'stop', stopId: stopId! } : about };
      } else {
         request = action;
      }

      const actions = this.threadActions(tourId);
      if (actions === undefined) {
         return 'Open a tour first.';
      }

      const input: TourActionInput = { action: request, ...(text.trim() === '' ? {} : { text }), ...(stopId === null ? {} : { stopId }) };
      return reasonOf(await actions.requestAction(input));
   }

   /**
    * Changes the selected tour's familiarity level, which reports the change to the tour's bound session as a familiarityChanged decision, and adds the decision's thread to the tour's threads at once.
    *
    * The change names the open stop, if any, as the stop under view, after which Claude rewrites the stops. With no stop open, Claude rewrites every stop. The page refuses the change
    * while the open stop is one that the plan lacks, because a change without the stop rewrites every stop. Words that hold only whitespace are left out.
    *
    * @returns Null once the server stored the level, or the server's reason when it refused it. A refused UI credential signs the page out.
    */
   async changeFamiliarity(familiarity: Familiarity, text = ''): Promise<string | null> {
      const tourId = this.selectedTourId;
      if (tourId === null) {
         return 'Open a tour first.';
      }

      const stopId = this.selectedOpenStop?.viewedStopId ?? null;
      if (stopId === null && this.selectedStopId !== null) {
         return 'Open a stop first.';
      }

      const reveal = this.revealAfterWrite(tourId);
      try {
         const { output: changed, revision } = await this.requireApi().changeFamiliarity(tourId, familiarity, stopId, text.trim() === '' ? undefined : text);
         this.recordOf(tourId)?.putThread(changed.thread, revision);
         this.applyFamiliarity(changed, revision);
         reveal(changed.thread);
         return null;
      } catch (error) {
         return this.refusalOf(error, 'The familiarity level could not be changed.');
      }
   }

   /**
    * Approves the selected tour's waiting plan with the reviewer's proposed order, the skipped stops that the page shows, the level and the reviewer's words, which sends the tour's bound
    * session the approval as a planApproved decision, and adds the decision's thread to the tour's threads at once. Words that hold only whitespace are left out.
    *
    * @returns Null once the server stored the approval, or the server's reason when it refused it. A refused UI credential signs the page out.
    */
   async approvePlan(stopIds: readonly string[], familiarity: Familiarity, text: string): Promise<string | null> {
      const tourId = this.selectedTourId;
      if (tourId === null) {
         return 'Open a tour first.';
      }

      if (this.selectedSkipsSaving) {
         return 'A skip is still saving. Approve the plan once it is saved.';
      }

      const reveal = this.revealAfterWrite(tourId);
      const input: PlanApprovalInput = { stopIds: [...stopIds], skippedStops: [...this.selectedSkippedStops], familiarity, ...(text.trim() === '' ? {} : { text }) };
      try {
         const { output: approved, revision } = await this.requireApi().approvePlan(tourId, input);
         this.recordOf(tourId)?.putThread(approved.thread, revision);
         this.applySkips({ tourId, updatedAt: approved.updatedAt, stopIds: approved.skippedStops }, revision);
         this.applyPlanApproval(approved, revision);
         this.applyFamiliarity(approved, revision);
         reveal(approved.thread);
         return null;
      } catch (error) {
         return this.refusalOf(error, 'The plan could not be approved.');
      }
   }

   private applyPlanApproval({ tourId, updatedAt, planApproval }: UpdatedPlanApproval, revision: number): void {
      this.writeRow(tourId, 'planApproval', revision, updatedAt, { planApproval });
   }

   private applyFamiliarity({ tourId, updatedAt, familiarity }: UpdatedFamiliarity, revision: number): void {
      this.writeRow(tourId, 'familiarity', revision, updatedAt, { familiarity });
   }

   /**
    * Answers a check of the open stop, or answers it again, as a draft. An answer from a suggestion takes the suggestion's type. The reviewer's own words keep the type of the check's earlier
    * answer, or start as a decision when the check has no answer. It works with or without a bound session, and nothing reaches Claude.
    *
    * @returns Null once the server stored the answer, or the server's reason when it refused it, as for a check that Claude dropped meanwhile. A refused UI credential signs the page out.
    */
   async answerCheck(checkId: string, input: AnswerInput): Promise<string | null> {
      return this.writeAnswer((api, tourId, stopId) => api.answerCheck(tourId, stopId, checkId, input), 'The answer could not be saved.');
   }

   /**
    * Sets the type of the answer to a check of the open stop.
    *
    * @returns Null once the server stored the type, or the server's reason when it refused it. A refused UI credential signs the page out.
    */
   async setAnswerType(checkId: string, type: AnswerType): Promise<string | null> {
      return this.writeAnswer((api, tourId, stopId) => api.setAnswerType(tourId, stopId, checkId, type), "The answer's type could not be saved.");
   }

   /**
    * Clears the answer to a check of the open stop.
    *
    * @returns Null once the server removed the answer, or the server's reason when it refused. A refused UI credential signs the page out.
    */
   async clearAnswer(checkId: string): Promise<string | null> {
      return this.writeAnswer((api, tourId, stopId) => api.clearAnswer(tourId, stopId, checkId), 'The answer could not be cleared.');
   }

   /**
    * Sends the selected tour's answers to send to its bound session as one batch. The server sends all of them or none. A call while a send of the tour is in flight sends nothing.
    *
    * @param control - The control that starts the send, which shows a refusal.
    * @returns Null once the server sent them or another send is in flight, or the server's reason when it refused, as when no session is bound. A refused UI credential signs the page out.
    */
   async sendAnswers(control: AnswerSendControl): Promise<string | null> {
      const tourId = this.selectedTourId;
      if (tourId === null) {
         return 'Choose a tour first.';
      }

      const record = this.recordOf(tourId);
      if (record?.answerSend.sending) {
         return null;
      }

      const settle = (reason: string | null): string | null => {
         if (record !== undefined) {
            record.answerSend = { sending: false, refusal: reason === null ? null : { control, reason } };
         }

         return reason;
      };
      if (record !== undefined) {
         record.answerSend = { sending: true, refusal: null };
      }

      try {
         const { output: batch, revision } = await this.requireApi().sendAnswers(tourId);
         this.applyBatch(batch, revision);
         return settle(null);
      } catch (error) {
         return settle(this.refusalOf(error, 'The answers could not be sent.'));
      }
   }

   /**
    * Skips a stop of the selected tour, or takes its skip back, with or without a bound session. Nothing reaches Claude as an event, and the reviewer stays on the open stop.
    *
    * @returns Null once the server stored the skips, or the server's reason when it refused the write. A refused UI credential signs the page out.
    */
   async setStopSkipped(stopId: string, skipped: boolean): Promise<string | null> {
      const tourId = this.selectedTourId;
      if (tourId === null) {
         return 'Open a tour first.';
      }

      const record = this.recordOf(tourId);
      if (record !== undefined) {
         record.skipsSaving++;
      }

      try {
         const { output: skips, revision } = await this.requireApi().setStopSkipped(tourId, stopId, skipped);
         this.applySkips(skips, revision);
         return null;
      } catch (error) {
         return this.refusalOf(error, skipped ? 'The stop could not be skipped.' : 'The skip could not be taken back.');
      } finally {
         if (record !== undefined) {
            record.skipsSaving--;
         }
      }
   }

   /**
    * Checks the selected tour's pull request for a new iteration and keeps the result, or the reason the check could not finish. The diff, the stops and the open stop stay on the tour's pin,
    * whatever the check finds. A check of the tour that waits for its response already is not sent again.
    */
   async checkIteration(): Promise<void> {
      const record = this.selectedRecord;
      if (record === undefined || record.iterationCheck?.running === true) {
         return;
      }

      record.iterationCheck = { running: true, failure: null };
      // A request that fails reports no iteration, so the failure keeps the one the page knew before an event could change it.
      const knownBefore = record.availableIteration;
      let failure: IterationCheck['failure'] = null;
      try {
         // The check stores through the server's scheduler, so its response carries no revision, and its change of the iteration arrives as an event.
         const checked = await this.requireApi().checkIteration(record.tourId);
         failure = checked.failure === undefined ? null : { reason: checked.failure, iteration: checked.availableIteration };
      } catch (error) {
         const reason = this.refusalOf(error, 'The pull request could not be checked for a new update.');
         failure = knownBefore === undefined ? { reason } : { reason, iteration: knownBefore };
      } finally {
         record.iterationCheck = { running: false, failure };
      }
   }

   /**
    * Updates the selected tour to the available iteration that its notice shows, which reports the update to the tour's bound session as an iterationUpdated decision when a stop goes stale, and applies
    * the new pin, the cleared iteration and the action's thread at once. The new plan and the threads whose anchors moved come with the update's events.
    *
    * The open stop stays open, and the changed files, the open diff and the open file load again at the new pin. The server refuses the update when the tour's available iteration is another one.
    *
    * @returns Null once the server stored the update, or the reason when the page shows no available iteration or the server refused the update. A refused UI credential signs the page out.
    */
   async updateIteration(): Promise<string | null> {
      const tourId = this.selectedTourId;
      if (tourId === null) {
         return 'Open a tour first.';
      }

      const shown = this.selectedAvailableIteration;
      if (shown === null) {
         return 'The tour has no newer update to move to.';
      }

      const reveal = this.revealAfterWrite(tourId);
      try {
         const { output: updated, revision } = await this.requireApi().updateIteration(tourId, shown.pin);
         this.applyPin(updated, revision);
         this.applyIteration({ tourId, updatedAt: updated.updatedAt, availableIteration: null }, revision);
         if (updated.thread !== undefined) {
            this.recordOf(tourId)?.putThread(updated.thread, revision);
            reveal(updated.thread);
         }

         return null;
      } catch (error) {
         return this.refusalOf(error, 'The tour could not be moved to the new update.');
      }
   }

   /**
    * Stores a tour's pin unless the tour list holds a newer message of the pin, so a late response does not undo a newer event. A pin that differs from the stored one loads the selected tour's
    * changed files again, and the open diff or file loads at their pin, as {@link LotsenApp.filesPin} says. An event loads the changed files again for the same pin too, because the review marks
    * may have changed with it.
    */
   private applyPin({ tourId, updatedAt, pin }: { tourId: string; updatedAt: string; pin: TourPin }, revision: number, fromEvent = false): void {
      const stored = this.tours.find((tour) => tour.id === tourId)?.pin;
      if (!this.writeRow(tourId, 'pin', revision, updatedAt, { pin })) {
         return;
      }

      const moved = JSON.stringify(stored) !== JSON.stringify(pin);
      if (tourId !== this.selectedTourId || !this.records.has(tourId)) {
         return;
      }

      if (moved || fromEvent) {
         this.loadFilesIfPinned(tourId);
      }
   }

   // Loads the open diff or file again when its last load read at another pin than the selected tour's changed files, or the diff read across another old path than they name, with a large file's
   // content when the reviewer loaded it.
   private loadOpenFileAtPin(tourId: string): void {
      const pin = this.filesPin;
      if (tourId !== this.selectedTourId || pin === null) {
         return;
      }

      const diff = this.selectedDiff;
      const files = this.knownFileList;
      if (diff !== null && diff.path === this.selectedFilePath && (this.diffPin !== pin || (files !== null && this.diffOldPath !== oldPathIn(files, diff.path)))) {
         this.loadDiff(tourId, diff.path, this.diffIncludesLarge);
      }

      const text = this.selectedText;
      if (text !== null && this.textPin !== pin) {
         this.loadText(tourId, text.side, text.path, this.textIncludesLarge);
      }
   }

   private applyIteration({ tourId, updatedAt, availableIteration }: AvailableIterationChangedEvent, revision: number): void {
      this.recordOf(tourId)?.putIteration(availableIteration, revision);
      this.touchTour(tourId, revision, updatedAt);
   }

   private applySkips({ tourId, updatedAt, stopIds }: UpdatedSkips, revision: number): void {
      this.recordOf(tourId)?.putSkips(stopIds, revision);
      this.touchTour(tourId, revision, updatedAt);
   }

   private applySeenVersion({ tourId, updatedAt, stopId, seenVersion }: UpdatedSeenVersion, revision: number): void {
      this.recordOf(tourId)?.putSeenVersion(stopId, seenVersion, revision);
      this.touchTour(tourId, revision, updatedAt);
   }

   // The shared path of the answer writes, whose responses carry the tour's revision, so each response orders with the events and loads in flight by that revision.
   private async writeAnswer(write: (api: Api, tourId: string, stopId: string) => Promise<Revised<UpdatedAnswer | ClearedAnswer>>, failure: string): Promise<string | null> {
      const tourId = this.selectedTourId;
      const stopId = this.selectedOpenStop?.viewedStopId ?? null;
      if (tourId === null || stopId === null) {
         return 'Open a stop first.';
      }

      try {
         const { output: written, revision } = await write(this.requireApi(), tourId, stopId);
         this.applyAnswer(written, revision);
         return null;
      } catch (error) {
         return this.refusalOf(error, failure);
      }
   }

   private applyAnswer(written: UpdatedAnswer | ClearedAnswer, revision: number): void {
      const record = this.recordOf(written.tourId);
      if ('answer' in written) {
         record?.putAnswer(written.answer, revision);
      } else {
         record?.clearAnswer(written.stopId, written.checkId, revision);
      }

      this.touchTour(written.tourId, revision, written.updatedAt);
   }

   private applyBatch(batch: UpdatedBatch, revision: number): void {
      for (const answer of batch.answers) {
         this.recordOf(batch.tourId)?.putAnswer(answer, revision);
      }

      this.touchTour(batch.tourId, revision, batch.updatedAt);
   }

   // Returns how to reveal a thread that a write of the tour's starts now, as ThreadsView.revealAfterWrite says.
   private revealAfterWrite(tourId: string): (thread: QuestionThread) => void {
      return this.threadSides.get(tourId)?.view.revealAfterWrite() ?? (() => {});
   }

   // Returns the server's reason for a refused write, or the failure for any other error. A refused UI credential signs the page out.
   private refusalOf(error: unknown, failure: string): string {
      if (error instanceof UnauthorizedError) {
         this.signOut();
      }

      return refusalReason(error, failure);
   }

   setDimming(dim: boolean): void {
      dimmingPreference.write(dim);
   }

   setHighlight(highlight: boolean): void {
      highlightPreference.write(highlight);
   }

   setCollapseToPassages(collapse: boolean): void {
      this.collapseToPassages = collapse;
   }

   /** Stores the reviewer's whitespace choice and loads the selected file's diff again with it, with a large file's texts when the reviewer loaded them. */
   setIgnoreWhitespace(ignore: boolean): void {
      this.changePreferences(() => ignoreWhitespacePreference.write(ignore));
   }

   // Settles the preferences with the server's, as syncPreferences says, and throws what the read throws.
   private async settlePreferences(api: Api, run: number): Promise<void> {
      const ignored = this.ignoreWhitespace;
      try {
         await syncPreferences(
            async () => {
               const { preferences } = await api.readPreferences();
               return run === this.runs ? preferences : null;
            },
            (key, text) => api.writePreference(key, text),
         );
      } finally {
         this.whitespaceChanged(ignored);
      }
   }

   // Makes the change to the preferences, and loads the selected file's diff again when the change turned whitespace on or off.
   private changePreferences(change: () => void): void {
      const ignored = this.ignoreWhitespace;
      change();
      this.whitespaceChanged(ignored);
   }

   // Loads the selected file's diff again when ignoring whitespace changed from the value given, with a large file's texts when the reviewer loaded them.
   private whitespaceChanged(ignored: boolean): void {
      const known = this.selectedDiff;
      if (this.ignoreWhitespace !== ignored && this.selectedTourId !== null && known !== null && known.path === this.selectedFilePath) {
         this.loadDiff(this.selectedTourId, known.path, this.diffIncludesLarge);
      }
   }

   private async reload(): Promise<void> {
      const generation = ++this.listGeneration;
      let restoredTour = false;
      const events: SseEvent[] = [];
      this.eventsDuringListLoad = events;
      try {
         const list = await this.requireApi().listTours();
         if (generation !== this.listGeneration) {
            return;
         }

         // The stream may have ended before the tourPinned event of the list's pin, so the changed files that the reload loads below bring the open diff or file to that pin.
         this.rows = sortNewestFirst(list.tours.filter((tour) => !this.deletedTours.has(tour.id)).map((summary) => rowOf(summary)));
         this.toursLoaded = true;
         this.toursError = null;
         for (const event of events) {
            const known = LotsenApp.known(event);
            if (known === null) {
               continue;
            }

            // A replay's failure starts no reload, so only a stream's open or a live failure starts one.
            try {
               this.applyEvent(known);
            } catch (error) {
               logEventFailure(event, error);
            }
         }

         const selected = this.selectedTourId;
         this.applyPendingNavigation();
         restoredTour = this.selectedTourId !== selected;
      } catch (error) {
         if (generation === this.listGeneration) {
            this.handleError(error, () => (this.toursError = 'The tours could not be loaded.'));
         }
      } finally {
         if (this.eventsDuringListLoad === events) {
            this.eventsDuringListLoad = null;
            if (this.reloadAfterListLoad) {
               this.reloadAfterListLoad = false;
               void this.reload();
            }
         }
      }

      // A restore that opened a tour has started its loads already.
      if (this.selectedTourId !== null && !restoredTour && generation === this.listGeneration) {
         this.loadFilesIfPinned(this.selectedTourId);
         if (this.showsSummary) {
            this.loadSummary(this.selectedTourId);
         }

         await this.loadPlan(this.selectedTourId);
      }
   }

   // Each request supersedes the earlier ones for the same tour, so a slow response cannot replace a newer list.
   private loadFilesIfPinned(tourId: string): void {
      if (this.tours.find((tour) => tour.id === tourId)?.pin.state !== 'pinned') {
         return;
      }

      const record = this.recordOf(tourId);
      if (record === undefined) {
         return;
      }

      const request = ++record.fileLoad;
      const started = ++this.reviewClock;
      if (record.files?.state !== 'loaded') {
         record.files = { state: 'loading' };
      }

      void this.requireApi()
         .listChangedFiles(tourId)
         .then(
            (list) => {
               if (record.fileLoad === request) {
                  record.files = { state: 'loaded', list };
                  dropSettledReviewChanges(record, started, list.files);
                  this.openPendingPassage(tourId);
                  this.loadOpenFileAtPin(tourId);
               }
            },
            (error: unknown) => {
               if (record.fileLoad === request) {
                  this.handleError(error, () => {
                     // A list of another pin stays loaded only while a load at the tour's pin runs, so filesPin moves the open file's reads to the tour's pin once that load fails. The failure keeps
                     // the list as `earlier`, as KnownFiles says.
                     const { known, transient } = filesFailure(error);
                     const pin = pinCommits(this.tours.find((tour) => tour.id === tourId)?.pin);
                     const kept = record.files?.state === 'loaded' ? record.files.list : null;
                     if (!transient || kept === null) {
                        record.files = known;
                     } else if (kept.mergeBase !== pin?.base || kept.head !== pin.head) {
                        record.files = known.state === 'failed' ? { ...known, earlier: kept } : known;
                     }

                     this.openPendingPassage(tourId);
                     this.loadOpenFileAtPin(tourId);
                  });
               }
            },
         );
   }

   private openPendingPassage(tourId: string): void {
      const pending = this.pendingOpen;
      if (pending !== null && tourId === this.selectedTourId) {
         this.pendingOpen = null;
         if (pending.passage === null) {
            this.showFile(pending.file);
         } else {
            this.showPassage(pending.passage);
         }
      }
   }

   // Each request supersedes the earlier ones, so a response for a file that is no longer selected is dropped. A renamed file's diff spans the rename.
   private loadDiff(tourId: string, path: string, includeLarge = false): void {
      const request = ++this.diffLoad;
      const previousOldPath = this.selectedDiff?.path === path ? this.diffOldPath : undefined;
      this.selectedDiff = { state: 'loading', path };
      this.diffIncludesLarge = includeLarge;
      this.diffPin = this.readPin;
      // Without a list, a load of the same file keeps the old path that its last load read across.
      const files = listedFiles(this.records.get(tourId)?.files);
      this.diffOldPath = files !== null ? oldPathIn(files, path) : previousOldPath;
      void this.requireApi()
         .diffFile(tourId, path, { oldPath: this.diffOldPath, includeLarge, ignoreWhitespace: this.ignoreWhitespace })
         .then(
            (diff) => {
               if (this.diffLoad === request) {
                  this.selectedDiff = { state: 'loaded', path, diff };
               }
            },
            (error: unknown) => {
               if (this.diffLoad === request) {
                  this.handleError(error, () => (this.selectedDiff = diffFailure(path, error)));
               }
            },
         );
   }

   // Each request supersedes the earlier ones, so a response for a file that is no longer open is dropped.
   private loadText(tourId: string, side: FileSide, path: string, includeLarge = false): void {
      const request = ++this.textLoad;
      this.textIncludesLarge = includeLarge;
      this.textPin = this.readPin;
      this.selectedText = { state: 'loading', side, path };
      void this.requireApi()
         .readFileText(tourId, side, path, includeLarge)
         .then(
            (text) => {
               if (this.textLoad === request) {
                  this.selectedText = { state: 'loaded', side, path, text };
               }
            },
            (error: unknown) => {
               if (this.textLoad === request) {
                  this.handleError(error, () => {
                     this.selectedText = textFailure(side, path, error);
                     this.fallBackFromRestoredFile(side, path, error);
                  });
               }
            },
         );
   }

   /**
    * Drops a file that a restored entry opened or added from the stack when the side the entry names no longer has it. For the file that the entry opened, it opens the open stop's primary passage
    * or closes the file.
    */
   private fallBackFromRestoredFile(side: FileSide, path: string, error: unknown): void {
      if (!(error instanceof ApiError && error.status === 404 && error.error?.code === 'fileNotFound')) {
         return;
      }

      const kept = (file: FileReference) => file.side !== side || file.path !== path;
      const restored = this.restoredFile;
      if (!this.restoredAdded.every(kept)) {
         this.restoredAdded = this.restoredAdded.filter(kept);
         this.stackAdded = this.stackAdded.filter(kept);
      }

      if (restored === null || kept(restored)) {
         return;
      }

      this.restoredFile = null;
      this.stackAdded = this.stackAdded.filter(kept);
      const primary = this.pinnedPrimaryPassage;
      if (primary === null) {
         this.closeFile();
      } else {
         this.openPassage(primary);
      }
   }

   private closeText(): void {
      this.selectedText = null;
      this.textLoad++;
   }

   /**
    * Loads a tour's review summary. Each request supersedes the earlier ones for the same tour, so a slow response cannot replace the summary that a later request, sent after a newer event, brings.
    * A failure keeps a summary that the page holds, unless the tour is gone.
    */
   private loadSummary(tourId: string): void {
      const record = this.recordOf(tourId);
      if (record === undefined) {
         return;
      }

      const request = ++record.summaryLoad;
      void this.requireApi()
         .readSummary(tourId)
         .then(
            (summary) => {
               if (record.summaryLoad === request) {
                  record.summary = { state: 'loaded', summary };
               }
            },
            (error: unknown) => {
               if (record.summaryLoad === request) {
                  this.handleError(error, () => {
                     const gone = error instanceof ApiError && error.status === 404;
                     if (gone || record.summary?.state !== 'loaded') {
                        const reason = error instanceof ApiError && !gone ? (error.error?.reason ?? null) : null;
                        record.summary = { state: 'failed', message: gone ? 'This tour no longer exists.' : 'The summary could not be loaded.', reason };
                     }
                  });
               }
            },
         );
   }

   /** Loads a tour's plan, with its stops' earlier versions, threads and answers. */
   private async loadPlan(tourId: string): Promise<void> {
      const record = this.recordOf(tourId);
      if (record === undefined) {
         return;
      }

      record.planLoads++;
      try {
         const opens = this.streamOpens;
         const tour = await this.requireApi().readTour(tourId);
         this.storePlan(tourId, (known) => known.applyLoad(tour), tour.updatedAt, { kind: 'load', revision: tour.revision });
         record.heardLoad(tour.revision, opens === this.streamOpens);
         this.resume(tourId);
      } catch (error) {
         if (this.selectedTourId !== tourId) {
            return;
         }

         // A failed load says nothing about a plan the page already holds, unless the tour is gone.
         this.handleError(error, () => {
            const gone = error instanceof ApiError && error.status === 404;
            if (gone || this.records.get(tourId)?.plan === undefined) {
               this.selectedTourError = gone ? 'This tour no longer exists.' : 'The tour could not be loaded.';
            }

            this.applyPendingNavigation();
         });
      } finally {
         record.planLoads--;
         const reload = record.planLoads === 0 && record.planReload;
         if (reload) {
            record.planReload = false;
         }

         if (reload && tourId === this.selectedTourId) {
            void this.loadPlan(tourId);
         }
      }
   }

   private static readonly eventHandlers: EventHandlers = {
      tourCreated: { apply: (app, summary) => app.upsertTour(summary), refreshesSummary: false },
      planSet: {
         apply: (app, { tourId, updatedAt, plan, revision }) => {
            app.storePlan(tourId, (record) => record.putPlan(plan, revision), updatedAt, { kind: 'event', revision });
            app.writeRow(tourId, 'stopCount', revision, updatedAt, { stopCount: plan.stops.length });
         },
         refreshesSummary: true,
      },
      stopPut: { apply: (app, put) => app.putStop(put), refreshesSummary: true },
      stopRevised: { apply: (app, revised) => app.reviseStop(revised), refreshesSummary: true },
      tourPinned: { apply: (app, pinned) => app.applyPin(pinned, pinned.revision, true), refreshesSummary: true },
      availableIterationChanged: { apply: (app, changed) => app.applyIteration(changed, changed.revision), refreshesSummary: false },
      tourBindingChanged: { apply: (app, { tourId, binding, revision }) => app.writeRow(tourId, 'binding', revision, undefined, { binding: binding ?? undefined }), refreshesSummary: false },
      threadCreated: { apply: (app, created) => app.applyThread(created), refreshesSummary: true },
      threadUpdated: { apply: (app, updated) => app.applyThread(updated), refreshesSummary: true },
      threadDeleted: {
         apply: (app, { tourId, updatedAt, threadId, revision }) => {
            app.recordOf(tourId)?.removeThread(threadId, revision);
            app.touchTour(tourId, revision, updatedAt);
         },
         refreshesSummary: true,
      },
      answerUpdated: { apply: (app, updated) => app.applyAnswer(updated, updated.revision), refreshesSummary: true },
      answerCleared: { apply: (app, cleared) => app.applyAnswer(cleared, cleared.revision), refreshesSummary: true },
      answerBatchUpdated: { apply: (app, batch) => app.applyBatch(batch, batch.revision), refreshesSummary: true },
      skipsUpdated: { apply: (app, skips) => app.applySkips(skips, skips.revision), refreshesSummary: true },
      seenVersionUpdated: { apply: (app, seen) => app.applySeenVersion(seen, seen.revision), refreshesSummary: false },
      familiarityChanged: { apply: (app, changed) => app.applyFamiliarity(changed, changed.revision), refreshesSummary: false },
      planApproved: { apply: (app, approved) => app.applyPlanApproval(approved, approved.revision), refreshesSummary: false },
      tourStateChanged: { apply: (app, state) => app.applyTourState(state, state.revision), refreshesSummary: false },
      // The review summary holds the synthesis, so the open summary page loads it again.
      synthesisWritten: { apply: () => {}, refreshesSummary: true },
      controlsChanged: { apply: (app, controls) => app.applyControls(controls), refreshesSummary: false },
      tourDeleted: { apply: (app, { tourId }) => app.dropTour(tourId), refreshesSummary: false },
      preferenceChanged: { apply: (app, { key, value }) => app.changePreferences(() => applyPreference(key, value)), refreshesSummary: false },
   };

   /**
    * Returns the event with its payload, or null for an event of a name that the UI does not handle, or whose data is not JSON.
    *
    * @remarks The server and the UI ship as one build, so the payload of a known name has the shape that its event kind declares.
    */
   private static known(event: SseEvent): KnownEvent | null {
      if (!Object.hasOwn(LotsenApp.eventHandlers, event.type)) {
         return null;
      }

      try {
         return { name: event.type, payload: JSON.parse(event.data) } as KnownEvent;
      } catch {
         return null;
      }
   }

   // A replay of the events after a list load loads no summary, because the reload loads it once.
   private handleEvent(event: SseEvent): void {
      this.eventsDuringListLoad?.push(event);
      const known = LotsenApp.known(event);
      if (known === null) {
         return;
      }

      this.applyEvent(known);
      const { payload } = known;
      if ('tourId' in payload && 'revision' in payload) {
         this.records.get(payload.tourId)?.heardEvent(payload.revision);
      }

      if (LotsenApp.eventHandlers[known.name].refreshesSummary && 'tourId' in payload && payload.tourId === this.selectedTourId && this.showsSummary) {
         this.loadSummary(payload.tourId);
      }
   }

   private applyEvent<K extends AppEventName>({ name, payload }: { name: K; payload: EventPayloads[K] }): void {
      LotsenApp.eventHandlers[name].apply(this, payload);
   }

   private applyThread({ tourId, updatedAt, thread, revision }: EventPayloads['threadCreated'] | EventPayloads['threadUpdated']): void {
      this.recordOf(tourId)?.putThread(thread, revision);
      this.touchTour(tourId, revision, updatedAt);
   }

   /** Writes a tour's state and pull request state into its row of the list, unless the list holds a newer message of them. */
   private applyTourState({ tourId, updatedAt, state, pullRequestState }: UpdatedTourState, revision: number): void {
      this.writeRow(tourId, 'state', revision, updatedAt, { state, pullRequestState });
   }

   /**
    * Records the place that the reviewer opened as the tour's reading progress, unless the server holds it already. While a write of the tour is in flight, the place waits, and only the last
    * place that waited goes once the write ends.
    */
   private recordPlace(tourId: string, place: ReadingPlace): void {
      const record = this.recordOf(tourId);
      const write = record?.progressWrite ?? null;
      if (write !== null) {
         write.wanted = place;
         write.reopened ||= samePlace(place, write.sent);
      } else if (record !== undefined && !holdsPlace(record, place)) {
         this.sendProgress(record, place);
      }
   }

   /**
    * Sends one progress write. A failed write shows nothing, because the progress only tells where to resume, and the page sends the place again once the reviewer opens it again, during the
    * write or after it.
    *
    * The tour's state comes from its `tourStateChanged` event alone.
    */
   private sendProgress(record: TourRecord, place: ReadingPlace): void {
      const write = { sent: place, wanted: place, reopened: false };
      record.progressWrite = write;
      void this.requireApi()
         .recordProgress(record.tourId, place)
         .then(
            ({ output: recorded, revision }) => {
               record.putProgress(recorded.progress, revision);
               if (place !== 'summary' && recorded.seenVersion !== undefined) {
                  record.putSeenVersion(place.stopId, recorded.seenVersion, revision);
               }
            },
            (error: unknown) => this.handleError(error, () => undefined),
         )
         .finally(() => {
            // A delete during the write forgot the tour's record, so nothing goes again.
            if (record.progressWrite !== write || this.records.get(record.tourId) !== record) {
               return;
            }

            record.progressWrite = null;
            const again = write.reopened || !samePlace(write.wanted, place);
            if (this.phase === 'ready' && again && !holdsPlace(record, write.wanted)) {
               this.sendProgress(record, write.wanted);
            }
         });
   }

   /**
    * Opens the tour that the reviewer opened from the list where the reviewer left off, once its load brought the reading progress: the stop opened last, or the summary page when that was
    * last. It adds no history entry, so the entry of the open names the place.
    *
    * A tour never opened stays without an open stop, as before any reading. A last stop that the plan no longer holds opens the summary page of a finished tour, and nothing otherwise. A page
    * write in flight wins over the load, because the load may predate it.
    */
   private resume(tourId: string): void {
      if (this.resumeTour !== tourId) {
         return;
      }

      this.resumeTour = null;
      if (this.selectedTourId !== tourId || this.selectedStopId !== null || this.showsSummary || this.opened !== null || this.pendingNavigation !== null) {
         return;
      }

      const record = this.records.get(tourId);
      const place = record === undefined ? null : leftPlace(record);
      if (record === undefined || place === null) {
         return;
      }

      const stopId = resumedStopId(record);
      if (stopId !== null) {
         this.showStop(stopId);
      } else if ((place === 'summary' || record.progress?.finished === true) && this.selectedSummaryFollows) {
         this.showSummary(tourId);
      }
   }

   /** Moves a tour's update time in the list to a message's, unless the list holds the update time of a newer one. No part of the row orders by that time, so the move hides no event. */
   private touchTour(tourId: string, revision: number, updatedAt: string): void {
      this.updateRow(tourId, (row) => touched(row, revision, updatedAt));
   }

   /** Writes a part of a tour's row, as {@link withPart} does, and returns whether the row took the write. */
   private writeRow<P extends RowPart>(tourId: string, part: P, revision: number, updatedAt: string | undefined, fields: RowParts[P]): boolean {
      return this.updateRow(tourId, (row) => withPart(row, part, revision, updatedAt, fields));
   }

   /** Replaces a tour's row with what `update` returns, and returns whether the row changed. Nothing changes when the list has no row for the tour. */
   private updateRow(tourId: string, update: (row: TourRow) => TourRow): boolean {
      const index = this.rows.findIndex((row) => row.summary.id === tourId);
      const row = this.rows[index];
      if (row === undefined) {
         return false;
      }

      const next = update(row);
      if (next === row) {
         return false;
      }

      this.rows = this.rows.with(index, next);
      return true;
   }

   /**
    * Writes the event's stop into the tour's known plan, and loads the selected tour's plan again when that plan may lack the write.
    *
    * A plan load in progress may answer with the plan from before the write, or bring a newer plan that the merged one would hide behind the event's time. A plan without the stop is out of date.
    * A load that starts after the event answers with the write, so it settles both cases. While loads are in progress, the events share one load that starts when they end.
    */
   private putStop({ tourId, updatedAt, stop, revision }: EventPayloads['stopPut']): void {
      const record = this.recordOf(tourId);
      if (record === undefined) {
         return;
      }

      const merged = record.hasStop(stop.id);
      this.storePlan(tourId, (known) => known.putStop(stop, revision), updatedAt, { kind: 'event', revision });
      if (tourId === this.selectedTourId && record.planLoads > 0) {
         record.planReload = true;
      } else if (tourId === this.selectedTourId && !merged) {
         void this.loadPlan(tourId);
      }

      this.touchTour(tourId, revision, updatedAt);
   }

   /**
    * Applies a revision of a stop: the answers that it made outdated and the threads that it settled, each at the event's time, then the stop, with the version it replaced among its earlier
    * versions, as {@link putStop} does. The plan that {@link putStop} stores holds the stop under view, as {@link OpenStop.applyPlan} says.
    */
   private reviseStop({ tourId, updatedAt, stop, answers, threads, revision }: EventPayloads['stopRevised']): void {
      const record = this.recordOf(tourId);
      for (const answer of answers) {
         record?.putAnswer(answer, revision);
      }

      for (const thread of threads) {
         record?.putThread(thread, revision);
      }

      this.putStop({ tourId, updatedAt, stop, revision });
   }

   private upsertTour(summary: TourSummary): void {
      if (this.deletedTours.has(summary.id)) {
         return;
      }

      if (this.rows.some((row) => row.summary.id === summary.id)) {
         this.updateRow(summary.id, (row) => withSummary(row, summary));
      } else {
         this.rows = sortNewestFirst([...this.rows, rowOf(summary)]);
      }
   }

   /**
    * Makes a change to a tour's plan in its record, which keeps it unless the record holds newer messages, and clears the selected tour's load error, because the page now holds a plan for it.
    *
    * When the open stop gets its content, its primary passage opens, as if the reviewer opened the stop then. A plan without the open stop closes it, with any passage of it that waits to open.
    * A newer version of the stop under view keeps the version that the reviewer read on screen, as {@link OpenStop.applyPlan} says.
    *
    * @param change - Stores the plan's message in the record, and returns whether the plan that the record shows changed.
    * @param updatedAt - The tour's update time of the message, which a held stop version keeps as the time it was replaced.
    * @param source - How the plan reaches the page, with the revision of its message.
    */
   private storePlan(tourId: string, change: (record: TourRecord) => boolean, updatedAt: string, source: PlanSource): void {
      const record = this.recordOf(tourId);
      const openStop = this.threadSides.get(tourId)?.openStop;
      const written = tourId === this.selectedTourId && this.selectedStop?.stage === 'pending';
      if (record !== undefined && (openStop?.applyPlan(() => change(record), updatedAt, source) ?? change(record))) {
         const stop = this.selectedStop;
         if (tourId === this.selectedTourId && this.selectedStopId !== null && stop === null) {
            openStop?.open(null);
            this.pendingOpen = null;
            this.leaveStack();
         } else if (written && stop?.stage === 'written') {
            this.showStop(stop.id);
         }
      }

      if (this.selectedTourId === tourId) {
         this.selectedTourError = null;
         this.applyPendingNavigation();
      }
   }

   /** Keeps the tour's controls with the higher revision. Two controls of one revision come from the same stored tour and binding, so the record keeps the ones it holds. */
   private applyControls(controls: TourControls): void {
      this.recordOf(controls.tourId)?.putControls(controls);
   }

   private handleError(error: unknown, otherwise: () => void): void {
      if (error instanceof UnauthorizedError) {
         this.signOut();
      } else {
         otherwise();
      }
   }

   private signOut(): void {
      this.stop();
      endSession(this.options.environment.storage);
      this.phase = 'signedOut';
   }

   private requireApi(): Api {
      if (this.api === null) {
         throw new Error('The session has not started.');
      }

      return this.api;
   }
}

/** Returns null for a stored write, or the reason of its refusal. */
function reasonOf(result: WriteResult): string | null {
   return result.ok ? null : result.refusal;
}

/**
 * Returns what a failed load of the changed files tells about the tour.
 *
 * @returns The state to show, and whether the failure is transient. A transient failure says nothing about the list, so a list that is already loaded at the tour's pin stays.
 */
function filesFailure(error: unknown): { known: KnownFiles; transient: boolean } {
   if (error instanceof ApiError && error.status === 409 && error.error?.pin) {
      return { known: { state: 'notPinned', pin: error.error.pin }, transient: false };
   }

   if (error instanceof ApiError && error.status === 404 && error.error?.code === 'tourNotFound') {
      return { known: { state: 'failed', message: 'This tour no longer exists.', reason: null }, transient: false };
   }

   const reason = error instanceof ApiError ? (error.error?.reason ?? null) : null;
   return { known: { state: 'failed', message: 'The changed files could not be loaded.', reason }, transient: true };
}

function pinKey(commits: PinCommits | null): string | null {
   return commits === null ? null : `${commits.base}..${commits.head}`;
}

/** Returns the changed files that the file reads use: the loaded list, or the earlier pin's list after a transient failure at a new pin, or null while the page holds neither. */
function listedFiles(known: KnownFiles | undefined): readonly ChangedFile[] | null {
   return known?.state === 'loaded' ? known.list.files : known?.state === 'failed' ? (known.earlier?.files ?? null) : null;
}

/** Returns the old path of the file at the path when the changed files name it renamed, or undefined when they do not. */
function oldPathIn(files: readonly ChangedFile[], path: string): string | undefined {
   const file = files.find((candidate) => candidate.path === path);
   return file?.kind === 'renamed' ? file.oldPath : undefined;
}

function diffFailure(path: string, error: unknown): KnownDiff {
   if (error instanceof ApiError && error.status === 409 && error.error?.pin) {
      return { state: 'notPinned', path, pin: error.error.pin };
   }

   if (error instanceof ApiError && error.status === 404 && error.error?.code === 'tourNotFound') {
      return { state: 'failed', path, message: 'This tour no longer exists.', reason: null };
   }

   const reason = error instanceof ApiError ? (error.error?.reason ?? null) : null;
   return { state: 'failed', path, message: 'The diff could not be loaded.', reason };
}

function textFailure(side: FileSide, path: string, error: unknown): KnownText {
   if (error instanceof ApiError && error.status === 409 && error.error?.pin) {
      return { state: 'notPinned', side, path, pin: error.error.pin };
   }

   if (error instanceof ApiError && error.status === 404 && error.error?.code === 'tourNotFound') {
      return { state: 'failed', side, path, message: 'This tour no longer exists.', reason: null };
   }

   const reason = error instanceof ApiError ? (error.error?.reason ?? null) : null;
   return { state: 'failed', side, path, message: 'The file could not be loaded.', reason };
}

/** Returns whether a review state marks the file as reviewed at the version that the list holds. */
function marksVersion(state: ReviewState, file: ChangedFile): boolean {
   return state.reviewed && state.headObjectId === (file.headObjectId ?? null);
}

function sameFocus(a: LineFocus, b: LineFocus): boolean {
   return a.path === b.path && a.side === b.side && a.line === b.line;
}

// The list only shows the creation times, so their milliseconds order the tours, and the ID orders two tours of one millisecond.
function sortNewestFirst(rows: readonly TourRow[]): TourRow[] {
   return [...rows].sort(({ summary: a }, { summary: b }) => Date.parse(b.createdAt) - Date.parse(a.createdAt) || a.id.localeCompare(b.id));
}

/**
 * Drops the reviewer's settled review marks that a list, whose load started at the review clock's value, makes current: those whose last request the server answered before then, and
 * those on a file that the list lacks or holds at another version.
 */
function dropSettledReviewChanges(record: TourRecord, started: number, files: readonly ChangedFile[]): void {
   const changes = record.reviewChanges;
   for (const [path, change] of Object.entries(changes)) {
      const file = files.find((candidate) => candidate.path === path);
      if (change.pending === 0 && (change.settled < started || file === undefined || (change.shown.reviewed && !marksVersion(change.shown, file)))) {
         delete changes[path];
      }
   }
}

/** Returns whether the server holds the place as the one opened last, and for a place with a version that version as seen. */
function holdsPlace(record: TourRecord, place: ReadingPlace): boolean {
   const progress = record.progress ?? null;
   if (progress === null) {
      return false;
   }

   return place === 'summary' ? progress.lastStop === undefined : progress.lastStop === place.stopId && (place.version === undefined || place.version <= (record.seenVersion(place.stopId) ?? 0));
}

function samePlace(a: ReadingPlace, b: ReadingPlace): boolean {
   return a === 'summary' || b === 'summary' ? a === b : a.stopId === b.stopId && a.version === b.version;
}

/** Returns where the reviewer left a tour: the place of the reading progress write in flight, else of the reading progress, or null for a tour never read. */
function leftPlace(record: TourRecord): ReadingPlace | null {
   const progress = record.progress ?? null;
   return record.progressWrite?.wanted ?? (progress === null ? null : progress.lastStop === undefined ? 'summary' : { stopId: progress.lastStop });
}

/** Returns the stop that {@link LotsenApp.resume} opens when the reviewer opens the tour again from the list: the stop the reviewer left, while the plan holds it, or null for none. */
function resumedStopId(record: TourRecord): string | null {
   const place = leftPlace(record);
   return place !== null && place !== 'summary' && record.plan?.stops.some((stop) => stop.id === place.stopId) === true ? place.stopId : null;
}
