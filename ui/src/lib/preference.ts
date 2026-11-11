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

/**
 * The reviewer's values under every key that starts with one prefix, such as the named layouts, held on the server and in the page alone.
 *
 * Browser storage holds none of them, because a text that browser storage kept fills a key that the server lacks, so it would bring back a value that a page of another origin deleted.
 */
export interface PreferenceFamily<T> {
   /** Returns each key of the family that holds a text, with its value, or undefined for a text that holds no value. A read in a reactive context follows the family's changes. */
   entries(): { key: string; value: T | undefined }[];
   /**
    * Stores the value under the key on the server, or deletes the key for null, and then in the page.
    *
    * The page keeps its text when the server refuses the write.
    *
    * @throws The server's refusal, or an error when the key lacks the family's prefix or the page has not read the server's preferences.
    */
   write(key: string, value: T | null): Promise<void>;
}

/** Sends one preference's text to the server's preference store, or the delete of its key for null. */
export type PreferenceWriter = (key: string, text: string | null) => Promise<unknown>;

// Every key that a preference of the page reads.
const registered = new Set<string>();

// The prefix of every preference family.
const families = new Set<string>();

// Each key's text as the page knows it beyond browser storage: the server's once the page has read them, and every write since. Null means the key holds no text, and a family key
// without a text is absent.
const known = new SvelteMap<string, string | null>();

// The server's writer, from the page's first read of the server's preferences that succeeded. The page's writes before it stay in the page.
let writer: PreferenceWriter | null = null;

// The keys with a server write in flight. A preference's key holds the text that waits to follow the write, so each key has one write in flight and a burst of writes sends the last text.
const inFlight = new Map<string, { waiting?: { text: string | null } }>();

// The keys whose last write the server did not take. A read keeps their page text and sends it again.
const failed = new Set<string>();

// The order of the page's sends, by the number of the last send of each key, so a read keeps each key that the page sent after the read began.
let sends = 0;
const lastSent = new Map<string, number>();

// Each family write of a key waits for the one before it, so the server stores the key's texts in the order the page wrote them.
const familyWrites = new Map<string, Promise<void>>();

// The number of detaches, so a family write that the server answers after a detach leaves the page's texts alone.
let sessions = 0;

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
 * @throws An error when the key starts with the prefix of a {@link preferenceFamily}.
 */
export function storedPreference<T>(key: string, parse: (stored: string) => T | undefined, format: (value: T) => string, fallback: T): Preference<T> {
   const family = [...families].find((prefix) => key.startsWith(prefix));
   if (family !== undefined) {
      throw new Error(`The preference key ${key} overlaps the preference family ${family}.`);
   }

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
 * Returns the preference family of every key that starts with the prefix.
 *
 * A key belongs to one preference or one family, because the family's settling deletes each of its keys that the server lacks.
 *
 * @param parse Returns the value that a key's text holds, or undefined when the text holds none.
 * @param format Returns the text that stores a value.
 * @throws An error when the prefix starts a preference's key, or starts or extends another family's prefix.
 */
export function preferenceFamily<T>(prefix: string, parse: (stored: string) => T | undefined, format: (value: T) => string): PreferenceFamily<T> {
   const overlap = [...registered].find((key) => key.startsWith(prefix)) ?? [...families].find((other) => other !== prefix && (other.startsWith(prefix) || prefix.startsWith(other)));
   if (overlap !== undefined) {
      throw new Error(`The preference family ${prefix} overlaps ${overlap}.`);
   }

   families.add(prefix);
   return {
      entries() {
         const found: { key: string; value: T | undefined }[] = [];
         for (const [key, text] of known) {
            if (key.startsWith(prefix) && text !== null) {
               found.push({ key, value: parse(text) });
            }
         }

         return found;
      },
      write(key, value) {
         if (!key.startsWith(prefix)) {
            return Promise.reject(new Error(`The key ${key} is not of the family ${prefix}.`));
         }

         return writeThrough(key, value === null ? null : format(value));
      },
   };
}

/**
 * Reads the server's preferences, then sends the page's later writes to the server through the writer and settles each preference between the page and the server.
 *
 * A key keeps the page's text, and the page sends it, when the server lacks the key or when the page's last write of it failed. So a text that browser storage kept fills a key that the
 * server lacks, and never replaces a text that the server holds. Every other key takes the server's text, which browser storage then mirrors. A key of a preference family takes the
 * server's text, or none when the server lacks the key. A key that the page sent after the read began, or whose write is in flight, keeps the page's text, which that write stores.
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
   const pageWins = (key: string) => inFlight.has(key) || (lastSent.get(key) ?? 0) > began;
   for (const key of registered) {
      if (pageWins(key)) {
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

   const familyKeys = new Set([...Object.keys(stored), ...known.keys()].filter(inFamily));
   for (const key of familyKeys) {
      if (!pageWins(key)) {
         storeInFamily(key, stored[key] ?? null);
      }
   }
}

/** Applies a preference's change from the server's `preferenceChanged` event, unless a write of the page to the key is in flight, which the server stores after the change. */
export function applyPreference(key: string, text: string | null): void {
   if (inFlight.has(key)) {
      return;
   }

   if (registered.has(key)) {
      failed.delete(key);
      store(key, text);
   } else if (inFamily(key)) {
      storeInFamily(key, text);
   }
}

/**
 * Stops sending the page's later writes to the server and forgets the server's texts, so each preference reads browser storage again and each preference family is empty, as when the page
 * ends its session. The writes in flight and the texts that wait to follow them still go out.
 */
export function detachPreferences(): void {
   sessions++;
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

function inFamily(key: string): boolean {
   return [...families].some((prefix) => key.startsWith(prefix));
}

function storeInFamily(key: string, text: string | null): void {
   if (text === null) {
      known.delete(key);
   } else {
      known.set(key, text);
   }
}

async function writeThrough(key: string, text: string | null): Promise<void> {
   const write = writer;
   if (write === null) {
      throw new Error('The page has not read the preferences from the server yet. Try again.');
   }

   const session = sessions;
   lastSent.set(key, ++sends);
   const entry = {};
   inFlight.set(key, entry);
   const before = familyWrites.get(key);
   const done = (async () => {
      try {
         await before;
         await write(key, text);
         if (sessions === session) {
            storeInFamily(key, text);
         }
      } finally {
         if (inFlight.get(key) === entry) {
            inFlight.delete(key);
         }
      }
   })();
   const settled = done.catch(() => {});
   familyWrites.set(key, settled);
   void settled.then(() => familyWrites.get(key) === settled && familyWrites.delete(key));
   return done;
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
