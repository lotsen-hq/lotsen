const storageKey = 'lotsen.ignoreWhitespace';

// Browser storage can be missing or throw on access, as in a private window, so both calls catch its errors without a report.

/** Returns whether the reviewer last chose to ignore whitespace in diffs in this browser, or false when no choice is stored. */
export function readIgnoreWhitespace(): boolean {
   try {
      return localStorage.getItem(storageKey) === 'true';
   } catch {
      return false;
   }
}

/** Stores whether the reviewer chose to ignore whitespace in diffs, when browser storage allows it. */
export function writeIgnoreWhitespace(ignore: boolean): void {
   try {
      localStorage.setItem(storageKey, String(ignore));
   } catch {
      // The choice then lasts only until the page loads again.
   }
}
