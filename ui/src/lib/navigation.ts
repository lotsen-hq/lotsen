import type { FileReference } from './stopFiles';

/** Lines of the open file, both 1-based. */
export interface LineSpan {
   startLine: number;
   endLine: number;
}

/**
 * What a history entry restores: the tour, the stop, the open file at its side, the lines of the passage or code reference that opened the file, and the files added to the stop-files stack,
 * or else the tour's summary page.
 *
 * A stop and a file belong to the tour, lines to the file and the added files to the stop, so none is set without what it belongs to. The summary page belongs to the tour and replaces the stop
 * and the file, so an entry of the summary page names no stop, file, lines or added files.
 */
export interface Navigation {
   tourId: string | null;
   stopId: string | null;
   file: FileReference | null;
   lines: LineSpan | null;
   /**
    * The files that the reviewer added to the open stop's stack from outside the stop, in the order added. The entry's state holds them, not its fragment, so a reload keeps them and a link
    * opened in a new tab stacks the entry's file alone.
    */
   added: readonly FileReference[];
   /** Whether the center shows the tour's summary page. */
   summary: boolean;
}

export const noNavigation: Navigation = { tourId: null, stopId: null, file: null, lines: null, added: [], summary: false };

/** Returns the URL fragment that names a navigation, or the empty string for a page without a tour. It never holds a one-time code. */
export function navigationFragment(navigation: Navigation): string {
   const { tourId, stopId, file, lines, summary } = navigation;
   if (tourId === null) {
      return '';
   }

   const params = new URLSearchParams({ tour: tourId });
   if (summary) {
      params.set('view', 'summary');
      return `#${params.toString()}`;
   }

   if (stopId !== null) {
      params.set('stop', stopId);
   }

   if (file !== null) {
      params.set('side', file.side);
      params.set('file', file.path);
      if (lines !== null) {
         params.set('lines', `${lines.startLine}-${lines.endLine}`);
      }
   }

   return `#${params.toString()}`;
}

/** Returns the state that a history entry keeps for a navigation beside its fragment. */
export function navigationState(navigation: Navigation): { added: FileReference[] } {
   return { added: navigation.added.map(({ side, path }) => ({ side, path })) };
}

/**
 * Reads the navigation that a history entry names: its URL fragment without the one-time code, and its state. A part that is malformed, or lacks what it belongs to, is left out.
 *
 * @param state The entry's state, which any script of the page may have written.
 */
export function readNavigation(hash: string, state: unknown): Navigation {
   const params = new URLSearchParams(hash.replace(/^#/, ''));
   const tourId = params.get('tour') || null;
   if (tourId === null) {
      return noNavigation;
   }

   if (params.get('view') === 'summary') {
      return { ...noNavigation, tourId, summary: true };
   }

   const stopId = params.get('stop') || null;
   const file = readFile(params.get('side'), params.get('file'));
   return { tourId, stopId, file, lines: file === null ? null : readLines(params.get('lines')), added: stopId === null ? [] : readAdded(state), summary: false };
}

function readFile(side: unknown, path: unknown): FileReference | null {
   return (side === 'base' || side === 'head') && typeof path === 'string' && path !== '' ? { side, path } : null;
}

function readAdded(state: unknown): FileReference[] {
   const added: unknown = typeof state === 'object' && state !== null && 'added' in state ? state.added : null;
   if (!Array.isArray(added)) {
      return [];
   }

   return added.flatMap((item: unknown) => {
      const file = typeof item === 'object' && item !== null && 'side' in item && 'path' in item ? readFile(item.side, item.path) : null;
      return file === null ? [] : [file];
   });
}

function readLines(text: string | null): LineSpan | null {
   const match = /^(\d+)-(\d+)$/.exec(text ?? '');
   const startLine = Number(match?.[1]);
   const endLine = Number(match?.[2]);
   return match !== null && startLine >= 1 && startLine <= endLine ? { startLine, endLine } : null;
}

/**
 * Returns the navigation that a history write records for the page's navigation. While the page shows no stack, a rewrite of the entry of the same stop keeps the entry's added files, so a view
 * mode that hides the stack does not erase them. A new entry records the page's navigation as it is.
 *
 * @param entry The navigation that the current history entry names.
 * @param stacked Whether the center shows the stop-files stack, whose added files the page's navigation then holds.
 * @param move Whether the write adds an entry, as {@link historyWrite} takes it.
 */
export function recordedNavigation(entry: Navigation, page: Navigation, stacked: boolean, move: boolean): Navigation {
   const keep = !stacked && !move && entry.tourId === page.tourId && entry.stopId === page.stopId;
   return keep ? { ...page, added: entry.added } : page;
}

/**
 * Decides how the history follows the page to a new navigation: a new entry for a move that adds one, the current entry rewritten for any other change, or nothing when the entry holds it already.
 *
 * @param entry The navigation that the current history entry names.
 * @param move Whether the change is a move that adds an entry: opening a tour, opening another stop, following a `ref:` link or opening a file from the Files panel.
 */
export function historyWrite(entry: Navigation, next: Navigation, move: boolean): 'push' | 'replace' | null {
   if (navigationFragment(entry) === navigationFragment(next) && JSON.stringify(navigationState(entry)) === JSON.stringify(navigationState(next))) {
      return null;
   }

   return move ? 'push' : 'replace';
}
