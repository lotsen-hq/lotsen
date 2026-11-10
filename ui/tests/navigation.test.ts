import { describe, expect, it } from 'vitest';
import { historyWrite, navigationFragment, navigationState, noNavigation, readNavigation, recordedNavigation, type Navigation } from '../src/lib/navigation';

const full: Navigation = {
   tourId: 'tour-1',
   stopId: 'stop-2',
   file: { side: 'base', path: 'src/a b/c#d.ts' },
   lines: { startLine: 3, endLine: 9 },
   added: [{ side: 'head', path: 'src/e.ts' }],
   summary: false,
};
const fullState = navigationState(full);

describe('navigationFragment and readNavigation', () => {
   it('write no fragment for a page without a tour, and read none back', () => {
      expect(navigationFragment(noNavigation)).toBe('');
      expect(readNavigation('', null)).toEqual(noNavigation);
      expect(readNavigation('#', null)).toEqual(noNavigation);
   });

   it('round-trip a tour, a stop, a file at its side and lines, with characters that a fragment must escape', () => {
      const fragment = navigationFragment(full);

      expect(fragment.startsWith('#tour=tour-1&stop=stop-2&')).toBe(true);
      expect(fragment).not.toContain('#d');
      expect(readNavigation(fragment, fullState)).toEqual(full);
   });

   it('round-trip a tour alone, and a file without a stop or lines', () => {
      const tourOnly: Navigation = { ...noNavigation, tourId: 't' };
      const fileOnly: Navigation = { tourId: 't', stopId: null, file: { side: 'head', path: 'a.ts' }, lines: null, added: [], summary: false };

      expect(readNavigation(navigationFragment(tourOnly), null)).toEqual(tourOnly);
      expect(readNavigation(navigationFragment(fileOnly), null)).toEqual(fileOnly);
   });

   it('read the navigation beside a one-time code, and ignore the code and unknown keys', () => {
      expect(readNavigation(`#code=secret&${navigationFragment(full).slice(1)}&other=1`, fullState)).toEqual(full);
      expect(readNavigation('#code=secret', null)).toEqual(noNavigation);
   });

   it('never write a code into the fragment', () => {
      expect(navigationFragment(full)).not.toContain('code');
   });

   it('drop what has nothing to belong to: a stop or a file without a tour, and lines without a file', () => {
      expect(readNavigation('#stop=s&side=head&file=a.ts&lines=1-2', null)).toEqual(noNavigation);
      expect(readNavigation('#tour=t&stop=s&lines=1-2', null)).toEqual({ ...noNavigation, tourId: 't', stopId: 's' });
   });

   it('drop a file with an unknown side or an empty path, and lines that are not a span of 1-based lines', () => {
      const tour = { ...noNavigation, tourId: 't' };
      const file = { ...tour, file: { side: 'head' as const, path: 'a.ts' } };

      expect(readNavigation('#tour=t&side=left&file=a.ts', null)).toEqual(tour);
      expect(readNavigation('#tour=t&side=head&file=', null)).toEqual(tour);
      for (const lines of ['0-2', '5-3', '1.5-2', 'x-2', '3', '-1-2', '']) {
         expect(readNavigation(`#tour=t&side=head&file=a.ts&lines=${lines}`, null), lines).toEqual(file);
      }
   });

   it('round-trip the summary page of a tour, which names no stop, file or lines', () => {
      const summary: Navigation = { ...noNavigation, tourId: 't', summary: true };

      expect(navigationFragment(summary)).toBe('#tour=t&view=summary');
      expect(readNavigation('#tour=t&view=summary', null)).toEqual(summary);
   });

   it('read a summary page without the stop, the file, the lines and the added files that an entry may name beside it, and no summary page without a tour', () => {
      expect(readNavigation(`${navigationFragment(full)}&view=summary`, fullState)).toEqual({ ...noNavigation, tourId: 'tour-1', summary: true });
      expect(readNavigation('#view=summary', null)).toEqual(noNavigation);
      expect(readNavigation('#tour=t&view=other', null)).toEqual({ ...noNavigation, tourId: 't' });
   });

   it('drop an empty tour or stop ID', () => {
      expect(readNavigation('#tour=&stop=s', null)).toEqual(noNavigation);
      expect(readNavigation('#tour=t&stop=', null)).toEqual({ ...noNavigation, tourId: 't' });
   });
});

describe('navigationState and readNavigation', () => {
   const stop: Navigation = { ...noNavigation, tourId: 't', stopId: 's' };
   const fragment = navigationFragment(stop);

   it('keep the added files in the state, not the fragment, as plain objects in the order added', () => {
      const added = [
         { side: 'head' as const, path: 'b.ts', extra: 1 },
         { side: 'base' as const, path: 'a.ts' },
      ];
      const state = navigationState({ ...stop, added });

      expect(navigationFragment({ ...stop, added })).toBe(fragment);
      expect(state).toEqual({
         added: [
            { side: 'head', path: 'b.ts' },
            { side: 'base', path: 'a.ts' },
         ],
      });
      expect(structuredClone(state)).toEqual(state);
      expect(readNavigation(fragment, state).added).toEqual(state.added);
   });

   it('read no added files from a missing or malformed state, and drop each malformed file', () => {
      for (const state of [null, undefined, 'added', { added: 'a.ts' }, {}]) {
         expect(readNavigation(fragment, state), JSON.stringify(state)).toEqual(stop);
      }

      const state = { added: [{ side: 'head', path: 'a.ts' }, { side: 'left', path: 'b.ts' }, { side: 'head', path: '' }, { side: 'head' }, 'c.ts', null] };
      expect(readNavigation(fragment, state).added).toEqual([{ side: 'head', path: 'a.ts' }]);
   });

   it('read no added files for an entry without a stop', () => {
      expect(readNavigation('#tour=t', { added: [{ side: 'head', path: 'a.ts' }] })).toEqual({ ...noNavigation, tourId: 't' });
   });
});

describe('historyWrite', () => {
   const tour: Navigation = { ...noNavigation, tourId: 't' };

   it('writes nothing when the entry already holds the navigation', () => {
      expect(historyWrite(full, { ...full, file: { ...full.file! }, lines: { ...full.lines! } }, true)).toBeNull();
      expect(historyWrite(full, full, false)).toBeNull();
   });

   it('pushes a move that changes the navigation', () => {
      expect(historyWrite(tour, full, true)).toBe('push');
      expect(historyWrite(noNavigation, tour, true)).toBe('push');
      expect(historyWrite(tour, { ...tour, summary: true }, true)).toBe('push');
   });

   it('replaces the entry for any other change, such as the first entry after a load or a step to another passage', () => {
      expect(historyWrite(full, tour, false)).toBe('replace');
      expect(historyWrite(full, { ...full, lines: { startLine: 4, endLine: 9 } }, false)).toBe('replace');
   });

   it('replaces the entry when only the added files change, as when a file joins the stack after the changed files load', () => {
      expect(historyWrite(full, { ...full, added: [...full.added, { side: 'base', path: 'f.ts' }] }, false)).toBe('replace');
      expect(historyWrite(full, { ...full, added: [] }, false)).toBe('replace');
      expect(historyWrite(full, { ...full, added: [{ ...full.added[0]! }] }, false)).toBeNull();
   });
});

describe('recordedNavigation', () => {
   const page: Navigation = { ...full, added: [] };

   it('records the page’s added files while the center shows the stack', () => {
      expect(recordedNavigation(full, page, true, false)).toBe(page);
   });

   it('keeps the entry’s added files in a rewrite of the same stop’s entry while the stack is hidden', () => {
      expect(recordedNavigation(full, page, false, false)).toEqual({ ...page, added: full.added });
      expect(recordedNavigation(full, { ...page, file: null, lines: null }, false, false).added).toEqual(full.added);
   });

   it('records no kept files for a new entry, or for another tour or stop', () => {
      expect(recordedNavigation(full, page, false, true)).toBe(page);
      expect(recordedNavigation(full, { ...page, stopId: 'other' }, false, false)).toEqual({ ...page, stopId: 'other' });
      expect(recordedNavigation(full, { ...page, tourId: 'other' }, false, false)).toEqual({ ...page, tourId: 'other' });
   });
});
