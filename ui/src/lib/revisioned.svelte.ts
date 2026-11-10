/**
 * Returns whether a value from a message at `revision` replaces the one that the page knows at `known`, or at no revision for undefined: only a higher revision does.
 *
 * Every store of a tour raises its revision, so two values at one revision come from the same store and carry the same state. This is the page's one ordering rule. Each entity compares with
 * the revision of the message that last set it, never with the tour's highest revision, because a write's response can arrive before the events of earlier revisions that are still on the stream.
 */
export function supersedes(revision: number, known: number | undefined): boolean {
   return known === undefined || revision > known;
}

/** A value with the revision of the message that set it, or a tombstone, a null value, that records a removal at its revision. */
interface Entry<T> {
   value: T | null;
   revision: number;
}

/**
 * Keeps one value per ID, each with the revision of the message that last set it, as {@link supersedes} orders them. A removal leaves a tombstone, so an older load or event does not bring the
 * value back. Reads are reactive.
 */
export class Revisioned<K, T> {
   // eslint-disable-next-line svelte/prefer-svelte-reactivity -- the change counter makes the reads reactive.
   private entries = new Map<K, Entry<T>>();
   private changes = $state(0);
   private listed: readonly T[] | null = null;

   /** Returns the value for the ID, or undefined when the store holds none or a tombstone. */
   get(id: K): T | undefined {
      void this.changes;
      return this.entries.get(id)?.value ?? undefined;
   }

   /** Returns every value, in the order the store first heard of each ID, with a snapshot's IDs ordered as {@link Revisioned.replaceAll} says. The list stays the same object until a change. */
   all(): readonly T[] {
      void this.changes;
      this.listed ??= [...this.entries.values()].flatMap((entry) => (entry.value === null ? [] : [entry.value]));
      return this.listed;
   }

   /** Returns the revision of the message that set the value for the ID, or undefined when the store holds none or a tombstone. */
   revisionOf(id: K): number | undefined {
      void this.changes;
      const entry = this.entries.get(id);
      return entry?.value === null ? undefined : entry?.revision;
   }

   /** Returns the lowest revision among the values that the store holds, or undefined when it holds none. */
   lowestRevision(): number | undefined {
      void this.changes;
      let lowest: number | undefined;
      for (const entry of this.entries.values()) {
         if (entry.value !== null && (lowest === undefined || entry.revision < lowest)) {
            lowest = entry.revision;
         }
      }

      return lowest;
   }

   /** Stores the value for the ID unless the store holds a value or a tombstone at an equal or higher revision, and returns whether it stored it. */
   put(id: K, value: T, revision: number): boolean {
      return this.set(id, { value, revision });
   }

   /** Leaves a tombstone for the ID unless the store holds a value or a tombstone at an equal or higher revision, and returns whether it left one. */
   remove(id: K, revision: number): boolean {
      return this.set(id, { value: null, revision });
   }

   /**
    * Takes a full snapshot at a revision, as pairs of an ID and its value: each of its values replaces an entry known at a lower revision, and each entry known at a lower revision that it lacks
    * becomes a tombstone. Entries at an equal or higher revision stay.
    *
    * The snapshot's IDs come first, in its order, and the entries that it lacks follow in their order, because a snapshot holds the server's order.
    *
    * @returns Whether the snapshot changed an entry.
    */
   replaceAll(snapshot: Iterable<readonly [K, T]>, revision: number): boolean {
      // eslint-disable-next-line svelte/prefer-svelte-reactivity -- the entries that replace the store's, which the change counter makes reactive.
      const next = new Map<K, Entry<T>>();
      let taken = false;
      for (const [id, value] of snapshot) {
         const known = this.entries.get(id);
         const kept = known !== undefined && !supersedes(revision, known.revision);
         next.set(id, kept ? known : { value, revision });
         taken ||= !kept;
      }

      for (const [id, known] of this.entries) {
         if (!next.has(id)) {
            const removed = supersedes(revision, known.revision);
            next.set(id, removed ? { value: null, revision } : known);
            taken ||= removed;
         }
      }

      this.entries = next;
      this.changed();
      return taken;
   }

   private set(id: K, entry: Entry<T>): boolean {
      if (!supersedes(entry.revision, this.entries.get(id)?.revision)) {
         return false;
      }

      this.entries.set(id, entry);
      this.changed();
      return true;
   }

   private changed(): void {
      this.listed = null;
      this.changes++;
   }
}

/** Keeps one value with the revision of the message that set it, as {@link supersedes} orders them, for an entity that a tour holds once, such as its controls. Reads are reactive. */
export class RevisionedValue<T> {
   private readonly store = new Revisioned<null, { value: T }>();

   /** Whether a value arrived. A null value counts, because it says that the server holds none. */
   get known(): boolean {
      return this.store.get(null) !== undefined;
   }

   /** The value, or undefined before one arrives. */
   get value(): T | undefined {
      return this.store.get(null)?.value;
   }

   /** Stores the value unless the one held came at an equal or higher revision, and returns whether it stored it. */
   put(value: T, revision: number): boolean {
      return this.store.put(null, { value }, revision);
   }
}
