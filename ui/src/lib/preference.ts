/** A reviewer's choice, stored in the browser under one key. */
export interface Preference<T> {
   /** Returns the stored choice, or the default when none is stored, the stored value is not a choice, or browser storage is unavailable. */
   read(): T;
   /** Stores the choice, when browser storage allows it. */
   write(value: T): void;
}

/**
 * Returns a preference that reads and writes its choice under the key.
 *
 * Browser storage can be missing or throw on access, as in a private window, so both methods catch its errors without a report.
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

function storedPreference<T>(key: string, parse: (stored: string) => T | undefined, format: (value: T) => string, fallback: T): Preference<T> {
   return {
      read() {
         try {
            const stored = localStorage.getItem(key);
            return (stored === null ? undefined : parse(stored)) ?? fallback;
         } catch {
            return fallback;
         }
      },
      write(value) {
         try {
            localStorage.setItem(key, format(value));
         } catch {
            // The choice then lasts only until the page loads again.
         }
      },
   };
}
