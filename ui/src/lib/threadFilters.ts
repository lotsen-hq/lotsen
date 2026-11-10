import { inFile, type FilePaths } from './pin';
import { questionOf, subjectOf, threadStateLabels } from './threadView';
import type { Plan, QuestionThread, ThreadState } from './types';

/** The states of a thread, in the order that the Threads panel's filter lists them. */
export const threadStates: readonly ThreadState[] = ['open', 'resolved', 'outdated'];

/** Where the threads that a thread filter shows are about: anywhere, the current stop or file, which follow the reviewer, or one stop or one file, which stay put. */
export type ThreadPlace = { kind: 'anywhere' } | { kind: 'currentStop' } | { kind: 'currentFile' } | { kind: 'stop'; stopId: string } | { kind: 'file'; path: string };

/** The states whose threads a thread filter shows, at least one, so the filter never hides every thread by its states alone. */
export type ShownStates = readonly [ThreadState, ...ThreadState[]];

/** Which threads the Threads panel lists. Each tour keeps its own. */
export interface ThreadFilter {
   /** Where the threads that show are about. */
   where: ThreadPlace;
   /** The states whose threads show. */
   states: ShownStates;
   /** Whether the threads of tour actions and of reviewer decisions show. */
   actions: boolean;
}

/** Where the reviewer is in a tour, which a filter's current stop and current file follow. */
export interface ReviewerPlace {
   /** The open stop's ID, or null when no stop is open. */
   stopId: string | null;
   /** The repository paths of the open file, or null when no file is open. */
   file: FilePaths | null;
}

/** The place of a reviewer who has no stop and no file open. */
export const nowhere: ReviewerPlace = { stopId: null, file: null };

/** The filter of a tour that the reviewer has not filtered: every open thread anywhere, those of the tour actions and the reviewer decisions included. */
export const defaultThreadFilter: ThreadFilter = { where: { kind: 'anywhere' }, states: ['open'], actions: true };

const anywhere: ThreadPlace = defaultThreadFilter.where;

/**
 * Returns the stop that a thread is about: the anchor's stop for a stop or a stop text anchor, otherwise the stop the reviewer viewed while asking or writing the note, or null for none.
 * `fetch_question` picks the same stop.
 */
export function threadStopId(thread: QuestionThread): string | null {
   return thread.anchor.kind === 'stop' || thread.anchor.kind === 'stopText' ? thread.anchor.stopId : thread.kind === 'note' ? (thread.stopId ?? null) : (questionOf(thread)?.stopId ?? null);
}

/** Returns the repository path of a code or a file anchor, or null for an anchor without a file. */
export function threadPath(thread: QuestionThread): string | null {
   return thread.anchor.kind === 'code' || thread.anchor.kind === 'file' ? thread.anchor.path : null;
}

/** Returns the stops and the paths that the threads are about, each once: the stops in the plan's order, then those the plan no longer has, and the paths sorted. */
export function threadFilterChoices(threads: readonly QuestionThread[], plan: Plan | null): { stopIds: string[]; paths: string[] } {
   const stopIds = new Set(threads.map(threadStopId).filter((stopId) => stopId !== null));
   const planned = plan?.stops.map((stop) => stop.id).filter((stopId) => stopIds.has(stopId)) ?? [];
   const paths = new Set(threads.map(threadPath).filter((path) => path !== null));
   return { stopIds: [...planned, ...[...stopIds].filter((stopId) => !planned.includes(stopId))], paths: [...paths].sort() };
}

/** Returns the filter with anywhere in place of a named stop or file that no thread is about, so a filter that the threads left behind hides nothing. */
export function applicableFilter(filter: ThreadFilter, threads: readonly QuestionThread[]): ThreadFilter {
   const where = filter.where;
   const kept = (where.kind !== 'stop' || threads.some((thread) => threadStopId(thread) === where.stopId)) && (where.kind !== 'file' || threads.some((thread) => threadPath(thread) === where.path));
   return kept ? filter : { ...filter, where: anywhere };
}

/** Returns the current stop or current file that the filter follows while the reviewer has none open, or null when the filter's place does not wait on one. */
export function missingPlace(filter: ThreadFilter, here: ReviewerPlace): 'stop' | 'file' | null {
   return filter.where.kind === 'currentStop' && here.stopId === null ? 'stop' : filter.where.kind === 'currentFile' && here.file === null ? 'file' : null;
}

/** Returns whether a thread is about the place, with the current stop and file where the reviewer is. A current file matches a code or a file anchor at the open file's path of its side. */
function isAt(thread: QuestionThread, where: ThreadPlace, here: ReviewerPlace): boolean {
   switch (where.kind) {
      case 'anywhere':
         return true;
      case 'currentStop':
         return here.stopId !== null && threadStopId(thread) === here.stopId;
      case 'currentFile':
         return (thread.anchor.kind === 'code' || thread.anchor.kind === 'file') && inFile(thread.anchor, here.file);
      case 'stop':
         return threadStopId(thread) === where.stopId;
      case 'file':
         return threadPath(thread) === where.path;
   }
}

/** Returns the threads that the filter shows where the reviewer is, in their order, with the filter as {@link applicableFilter} returns it. */
export function filteredThreads(threads: readonly QuestionThread[], filter: ThreadFilter, here: ReviewerPlace): QuestionThread[] {
   const applied = applicableFilter(filter, threads);
   return threads.filter((thread) => applied.states.includes(thread.state) && isAt(thread, applied.where, here) && (applied.actions || subjectOf(thread) === null));
}

/** Returns the filter with the state shown or hidden, in the order of {@link threadStates}, or the filter itself when hiding the state would leave no state shown. */
export function withState(filter: ThreadFilter, state: ThreadState, shown: boolean): ThreadFilter {
   const [first, ...rest] = threadStates.filter((candidate) => (candidate === state ? shown : filter.states.includes(candidate)));
   return first === undefined ? filter : { ...filter, states: [first, ...rest] };
}

/** Returns what the filter shows, for its Show button: the states, then the tour actions when they show, in a sentence-case list such as "Open, tour actions". */
export function shownLabel(filter: ThreadFilter): string {
   const words = [...threadStates.filter((state) => filter.states.includes(state)).map((state) => threadStateLabels[state].toLowerCase()), ...(filter.actions ? ['tour actions'] : [])].join(', ');
   return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Returns the filter widened as little as it takes to show the thread where the reviewer is: anywhere in place of a place that the thread is not about, the thread's state added, and the thread of a tour action or a reviewer decision shown. */
export function revealing(filter: ThreadFilter, thread: QuestionThread, here: ReviewerPlace): ThreadFilter {
   return {
      where: isAt(thread, filter.where, here) ? filter.where : anywhere,
      states: filter.states.includes(thread.state) ? filter.states : withState(filter, thread.state, true).states,
      actions: filter.actions || subjectOf(thread) !== null,
   };
}
