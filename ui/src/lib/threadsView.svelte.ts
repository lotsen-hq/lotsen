import { SvelteMap } from 'svelte/reactivity';
import { shortened } from './labels';
import { PendingWrite } from './pendingWrite.svelte';
import type { PickedAnchor } from './viewState';
import { applicableFilter, defaultThreadFilter, filteredThreads, missingPlace, revealing, threadFilterChoices, type ReviewerPlace, type ThreadFilter } from './threadFilters';
import type { TourRecord } from './tourRecord.svelte';
import type { NoteThread, QuestionThread } from './types';

/** How many threads the Threads panel lists, of how many the tour has. */
export interface FilterCounts {
   shown: number;
   total: number;
}

/** What the reviewer picked to ask about. Each pick is a new object, so the Threads panel takes it up again. */
export interface Pick {
   anchor: PickedAnchor;
   /** The first line of the text that the pick covers, which the ask box quotes, or null when it covers no text. */
   quote: string | null;
}

/** The writes that one thread's thread view starts and that keep its drafts, so a thread view that mounts again while one is in flight sees it. Reads are reactive. */
export class ThreadWrites {
   readonly followUp = new PendingWrite();
   /** The note's writes: the save of its edit, its discard and its send. */
   readonly note = new PendingWrite();
   /** The writes of the thread's menu but those of a note, which go as {@link note}: a state change, a cancel, a re-send and a copy of the question. */
   readonly menu = new PendingWrite();
   /** The words of the note's edit whose save is in flight, or null while none is. */
   saving = $state<string | null>(null);
}

const unsavedEditTitleLength = 40;

/**
 * Holds what the reviewer wrote in thread views and has not stored yet, by thread ID: the words of a follow-up, and the words of a note's edit in progress, with the writes that store
 * them. A thread view mounts anew each time it shows, so the words and the writes live here and stay through the back arrow, a pick and the opening of another thread. Reads are reactive.
 */
export class ThreadDrafts {
   private readonly followUps = new SvelteMap<string, string>();
   private readonly noteEdits = new SvelteMap<string, string>();
   private readonly writes: Record<string, ThreadWrites> = {};

   /** Returns the thread's writes, which it creates on the first call for the thread. */
   writesOf(threadId: string): ThreadWrites {
      return Object.hasOwn(this.writes, threadId) ? this.writes[threadId]! : (this.writes[threadId] = new ThreadWrites());
   }

   /** Returns the unsent words of the thread's follow-up, empty for none. */
   followUp(threadId: string): string {
      return this.followUps.get(threadId) ?? '';
   }

   /** Keeps the words of the thread's follow-up, and forgets them once they are empty. */
   setFollowUp(threadId: string, text: string): void {
      if (text === '') {
         this.followUps.delete(threadId);
      } else {
         this.followUps.set(threadId, text);
      }
   }

   /** Returns the words of the note's edit in progress, or null while the reviewer does not edit the note. */
   noteEdit(threadId: string): string | null {
      return this.noteEdits.get(threadId) ?? null;
   }

   /**
    * Starts the edit of the note, from the words that a save in flight stores, else from the note's words, and drops the last refusal of the note's writes. Starting from the words of the save in
    * flight lets its landing close the edit, so a second save cannot store the older words.
    */
   startNoteEdit(note: NoteThread): void {
      const writes = this.writesOf(note.id);
      this.setNoteEdit(note.id, writes.saving ?? note.text);
      writes.note.clear();
   }

   /** Returns why the note cannot go while the reviewer edits it, because a send takes the note's saved words, or null while no edit of the note is open. */
   unsavedEditReason(note: NoteThread): string | null {
      return this.noteEdits.has(note.id) ? `Save or cancel the edit of “${shortened(note.text, unsavedEditTitleLength)}” first.` : null;
   }

   /** Keeps the words of the note's edit in progress, or ends the edit with null. */
   setNoteEdit(threadId: string, text: string | null): void {
      if (text === null) {
         this.noteEdits.delete(threadId);
      } else {
         this.noteEdits.set(threadId, text);
      }
   }
}

/**
 * Holds the Threads panel's view of one tour's threads: the thread filter, the threads it shows, the thread whose thread view shows, the replies that the reviewer has not viewed, the drafts of
 * the thread views, and the pick that waits in the ask box.
 *
 * The threads come from the tour record, and the current stop and file from where the reviewer is, so every read follows both. The page does not remember the view across page loads. Reads
 * are reactive.
 */
export class ThreadsView {
   private readonly record: TourRecord;
   private readonly where: () => ReviewerPlace;
   private chosen = $state.raw<ThreadFilter>(defaultThreadFilter);
   private waiting = $state.raw<Pick | null>(null);
   private openedId = $state.raw<string | null>(null);
   /** When the reviewer last left each thread's thread view, in milliseconds since the epoch. */
   private readonly viewedAt = new SvelteMap<string, number>();
   private readonly now: () => number;
   /** When the view started, before which no message counts as new. */
   private readonly since: number;
   /** The count of the filters that the reviewer chose, so a write's response can tell that the reviewer chose one while the write was in flight. */
   private choices = 0;

   /** The filter with anywhere in place of a stop or a file that no thread is about, as {@link applicableFilter} returns it. */
   applied = $derived.by((): ThreadFilter => applicableFilter(this.chosen, this.record.threads));
   /** The threads that the filter shows where the reviewer is, in the order they were started. */
   shown = $derived.by((): readonly QuestionThread[] => filteredThreads(this.record.threads, this.chosen, this.here));
   /** The current stop or file that the filter follows while the reviewer has none open, as {@link missingPlace} says, or null when the filter waits on none. */
   missing = $derived.by(() => missingPlace(this.applied, this.here));
   counts = $derived.by((): FilterCounts => ({ shown: this.shown.length, total: this.record.threads.length }));
   /** The stops and the paths that the filter offers, as {@link threadFilterChoices} orders them. */
   options = $derived.by(() => threadFilterChoices(this.record.threads, this.record.plan ?? null));

   /** What the reviewer wrote in the tour's thread views and has not stored yet. */
   readonly drafts = new ThreadDrafts();

   /** The thread whose thread view the Threads panel shows in place of the thread list, or null for the list. A thread that leaves the record leaves the list showing. */
   opened = $derived.by((): QuestionThread | null => (this.openedId === null ? null : (this.record.thread(this.openedId) ?? null)));

   /**
    * @param where Returns where the reviewer is in the tour, reactively.
    * @param now Returns the time in milliseconds since the epoch, against which the server's timestamps of the messages count as new.
    */
   constructor(record: TourRecord, where: () => ReviewerPlace, now: () => number = Date.now) {
      this.record = record;
      this.where = where;
      this.now = now;
      this.since = now();
   }

   /** Where the reviewer is in the tour, which the current stop and the current file follow. */
   get here(): ReviewerPlace {
      return this.where();
   }

   /** Returns how many threads a filter would show where the reviewer is, as for the counts in the filter's menus. */
   count(filter: ThreadFilter): number {
      return filteredThreads(this.record.threads, filter, this.here).length;
   }

   /** Every thread of the tour, in the order they were started. */
   get threads(): readonly QuestionThread[] {
      return this.record.threads;
   }

   /** The filter that the reviewer chose or a reveal widened, or the default before either. */
   get filter(): ThreadFilter {
      return this.chosen;
   }

   /** What the reviewer picked last to ask about, or null when nothing waits. */
   get picked(): Pick | null {
      return this.waiting;
   }

   /** Sets the reviewer's choice of filter. */
   choose(filter: ThreadFilter): void {
      this.choices++;
      this.chosen = filter;
   }

   /** Widens the filter as little as it takes to show the thread, as {@link revealing} says, and keeps the filter object when the filter shows the thread already. */
   reveal(threadId: string): void {
      const thread = this.record.thread(threadId);
      if (thread === undefined) {
         return;
      }

      const filter = this.chosen;
      const revealed = revealing(filter, thread, this.here);
      if (revealed.where !== filter.where || revealed.states !== filter.states || revealed.actions !== filter.actions) {
         this.chosen = revealed;
      }
   }

   /**
    * Returns how to reveal a thread that a write stores, for a write that starts now.
    *
    * The returned function reveals the thread only while the record holds the thread's state that the write stored, and while the reviewer chose no filter after this call. So a newer state of
    * the thread, such as a resolve that landed first, and the reviewer's own choice both win over the widening.
    */
   revealAfterWrite(): (thread: QuestionThread) => void {
      const choices = this.choices;
      return (thread) => {
         if (this.record.thread(thread.id)?.state === thread.state && this.choices === choices) {
            this.reveal(thread.id);
         }
      };
   }

   /** Opens the thread's thread view, and widens the filter as {@link reveal} does, so the thread list shows the thread once the reviewer goes back to it. */
   open(threadId: string): void {
      this.reveal(threadId);
      this.leave();
      this.openedId = threadId;
   }

   /** Shows the thread list in place of the thread view. */
   close(): void {
      this.leave();
      this.openedId = null;
   }

   /**
    * Returns whether Claude's last message in the thread is new: it arrived after the view started and after the reviewer last left the thread's thread view, and the thread view does not
    * show now.
    */
   isNew(thread: QuestionThread): boolean {
      const last = thread.kind === 'note' ? undefined : thread.messages.at(-1);
      if (last === undefined || last.kind === 'question' || thread.id === this.openedId) {
         return false;
      }

      return Date.parse(last.createdAt) > (this.viewedAt.get(thread.id) ?? this.since);
   }

   /** Hands an anchor to the ask box as a new pick, and shows the thread list, where the ask box is. */
   pick(anchor: PickedAnchor, quote: string | null): void {
      this.close();
      this.waiting = { anchor, quote };
   }

   /** Lets go of a pick that a stored question or note was about, or that the reviewer removed from the ask box, unless the reviewer picked again since. */
   release(pick: Pick): void {
      if (this.waiting === pick) {
         this.waiting = null;
      }
   }

   /** Lets go of the pick and shows the thread list, as when the reviewer leaves the tour. */
   drop(): void {
      this.close();
      this.waiting = null;
   }

   // Every message of the thread view that shows counts as viewed once the reviewer leaves it.
   private leave(): void {
      const left = this.openedId;
      if (left !== null) {
         this.viewedAt.set(left, this.now());
      }
   }
}
