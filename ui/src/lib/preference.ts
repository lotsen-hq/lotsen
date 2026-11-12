import { untrack } from 'svelte';
import { SvelteMap } from 'svelte/reactivity';

/**
 * A reviewer's choice, stored as text under one key in the server's preference store, with browser storage as a mirror for the page's next start.
 *
 * A browser keeps its storage per origin, and the origin holds the server's port, which can change with each start. So the server's store holds each choice, and the page reads browser
 * storage only until it has read the server's.
 */
export interface Preference<T> {
   /** Returns the stored choice, or the default when none is stored or the stored text is not a choice. A read in a reactive context follows the choice's changes. */
   read(): T;
   /** Stores the choice in the page at once, then in browser storage and on the server, each when it allows it. */
   write(value: T): void;
}

/** Sends one preference's text to the server's preference store, or the delete of its key for null. */
export type PreferenceWriter = (key: string, text: string | null) => Promise<unknown>;

// Every key that a preference of the page reads.
const registered = new Set<string>();

// Each key's text as the page knows it beyond browser storage: the server's once the page has read them, and every write since. Null means the key holds no text.
const known = new SvelteMap<string, string | null>();

// The server's writer, from the page's first read of the server's preferences that succeeded. The page's writes before it stay in the page.
let writer: PreferenceWriter | null = null;

// The keys with a server write in flight, each with the text that waits to follow it, so each key has one write in flight and a burst of writes sends the last text.
const inFlight = new Map<string, { waiting?: { text: string | null } }>();

// The keys whose last write the server did not take. A read keeps their page text and sends it again.
const failed = new Set<string>();

// The order of the page's sends, by the number of the last send of each key, so a read keeps each key that the page sent after the read began.
let sends = 0;
const lastSent = new Map<string, number>();

/**
 * Returns a preference that reads and writes its choice under the key.
 *
 * @param choices The values the preference allows.
 * @param fallback The value that `read` returns when no valid choice is stored.
 */
export function preference<T extends string>(key: string, choices: readonly T[], fallback: T): Preference<T> {
   return storedPreference(key, (stored) => choices.find((choice) => choice === stored), String, fallback);
}

/**
 * Returns a preference for a whole number from `min` to `max`, read and written as {@link preference} does.
 *
 * @param fallback The value that `read` returns when no valid number is stored.
 */
export function numberPreference(key: string, { min, max }: { min: number; max: number }, fallback: number): Preference<number> {
   const parse = (stored: string) => {
      const value = /^\d+$/.test(stored) ? Number(stored) : NaN;
      return value >= min && value <= max ? value : undefined;
   };
   return storedPreference(key, parse, String, fallback);
}

/** Returns a preference for a yes-or-no choice, read and written as {@link preference} does. */
export function booleanPreference(key: string, fallback: boolean): Preference<boolean> {
   return storedPreference(key, (stored) => (stored === 'true' ? true : stored === 'false' ? false : undefined), String, fallback);
}

/**
 * Returns a preference for a value that `parse` reads from the stored text, read and written as {@link preference} does.
 *
 * @param parse Returns the value that the stored text holds, or undefined when the text holds none.
 * @param format Returns the text that stores the value.
 * @param fallback The value that `read` returns when no valid value is stored.
 */
export function storedPreference<T>(key: string, parse: (stored: string) => T | undefined, format: (value: T) => string, fallback: T): Preference<T> {
   registered.add(key);
   return {
      read() {
         const text = textOf(key);
         return (text === null ? undefined : parse(text)) ?? fallback;
      },
      write(value) {
         const text = format(value);
         // Untracked, so an effect that writes a preference does not run again on its change.
         if (text !== untrack(() => textOf(key))) {
            store(key, text);
            send(key, text);
         }
      },
   };
}

/**
 * Reads the server's preferences, then sends the page's later writes to the server through the writer and settles each preference between the page and the server.
 *
 * A key keeps the page's text, and the page sends it, when the server lacks the key or when the page's last write of it failed. So a text that browser storage kept fills a key that the
 * server lacks, and never replaces a text that the server holds. Every other key takes the server's text, which browser storage then mirrors. A key that the page sent after the read
 * began, or whose write is in flight, keeps the page's text, which that write stores.
 *
 * @param read Returns the server's preferences, or null when the page no longer wants them, which changes nothing.
 * @throws What the read throws, which changes nothing.
 */
export async function syncPreferences(read: () => Promise<Readonly<Record<string, string | undefined>> | null>, write: PreferenceWriter): Promise<void> {
   const began = sends;
   const stored = await read();
   if (stored === null) {
      return;
   }

   writer = write;
   for (const key of registered) {
      if (inFlight.has(key) || (lastSent.get(key) ?? 0) > began) {
         continue;
      }

      const server = stored[key] ?? null;
      const page = textOf(key);
      if (page !== null && page !== server && (server === null || failed.has(key))) {
         known.set(key, page);
         send(key, page);
      } else {
         failed.delete(key);
         store(key, server);
      }
   }
}

/** Applies a preference's change from the server's `preferenceChanged` event, unless a write of the page to the key is in flight, which the server stores after the change. */
export function applyPreference(key: string, text: string | null): void {
   if (registered.has(key) && !inFlight.has(key)) {
      failed.delete(key);
      store(key, text);
   }
}

/**
 * Stops sending the page's later writes to the server and forgets the server's texts, so each preference reads browser storage again, as when the page ends its session. The writes in flight
 * and the texts that wait to follow them still go out.
 */
export function detachPreferences(): void {
   writer = null;
   known.clear();
   inFlight.clear();
   failed.clear();
   lastSent.clear();
}

function textOf(key: string): string | null {
   return known.has(key) ? (known.get(key) ?? null) : readBrowser(key);
}

function store(key: string, text: string | null): void {
   known.set(key, text);
   writeBrowser(key, text);
}

// Each write waits for the one before it, so the server stores a key's texts in the order the page wrote them.
function send(key: string, text: string | null): void {
   const write = writer;
   if (write === null) {
      return;
   }

   lastSent.set(key, ++sends);
   const pending = inFlight.get(key);
   if (pending !== undefined) {
      pending.waiting = { text };
      return;
   }

   const entry: { waiting?: { text: string | null } } = {};
   inFlight.set(key, entry);
   void (async () => {
      let next: { text: string | null } | undefined = { text };
      while (next !== undefined) {
         entry.waiting = undefined;
         try {
            await write(key, next.text);
            if (inFlight.get(key) === entry) {
               failed.delete(key);
            }
         } catch {
            // The page keeps the text, and the next read of the server's preferences sends it again, unless the page detached the preferences since.
            if (inFlight.get(key) === entry) {
               failed.add(key);
            }
         }

         next = entry.waiting;
      }

      if (inFlight.get(key) === entry) {
         inFlight.delete(key);
      }
   })();
}

// Browser storage can be missing or throw on access, as in a private window, so both calls catch its errors without a report.

function readBrowser(key: string): string | null {
   try {
      return localStorage.getItem(key);
   } catch {
      return null;
   }
}

function writeBrowser(key: string, text: string | null): void {
   try {
      if (text === null) {
         localStorage.removeItem(key);
      } else {
         localStorage.setItem(key, text);
      }
   } catch {
      // The page still holds the text, and the server when it is attached.
   }
}
