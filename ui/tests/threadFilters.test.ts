import { describe, expect, it } from 'vitest';
import {
   applicableFilter,
   defaultThreadFilter,
   filteredThreads,
   missingPlace,
   nowhere,
   revealing,
   shownLabel,
   threadFilterChoices,
   threadStates,
   threadStopId,
   withState,
   type ReviewerPlace,
   type ShownStates,
   type ThreadFilter,
   type ThreadPlace,
} from '../src/lib/threadFilters';
import type { AskedThread, Plan, QuestionThread, ThreadAnchor, ThreadState } from '../src/lib/types';

const plan: Plan = {
   chapters: [{ id: 'c', title: 'Checkout' }],
   stops: [
      { stage: 'pending', status: 'pending', id: 'c-0', title: 'Cart totals', kind: 'inspect', chapter: 'c', anchorHint: null },
      { stage: 'pending', status: 'pending', id: 'c-1', title: 'Rounding', kind: 'risk', chapter: 'c', anchorHint: null },
   ],
   fileMarks: [],
};
const [cartTotals, rounding] = plan.stops.map((stop) => stop.id) as [string, string];

/** A thread with one answered question, asked at the anchor while the reviewer viewed the stop. */
function thread(id: string, anchor: ThreadAnchor, state: ThreadState = 'open', viewedStopId?: string): AskedThread {
   return {
      kind: 'asked',
      id,
      anchor,
      state,
      status: { questions: [{ questionId: `q-${id}`, outcome: 'answered' }] },
      messages: [
         {
            kind: 'question',
            id: `q-${id}`,
            createdAt: '2026-10-22T10:00:00+00:00',
            text: `Question ${id}`,
            requests: [{ id: `r-${id}`, state: 'done', sessionId: 'session-1', sentAt: '2026-10-22T10:00:00+00:00' }],
            ...(viewedStopId === undefined ? {} : { stopId: viewedStopId }),
         },
         { kind: 'reply', id: `a-${id}`, createdAt: '2026-10-22T10:00:01+00:00', requestId: `r-${id}`, sessionId: 'session-1', text: `Answer ${id}`, html: `<p>Answer ${id}</p>` },
      ],
   };
}

const code = (path: string): ThreadAnchor => ({ kind: 'code', side: 'head', path, startLine: 1, endLine: 2, commit: 'b'.repeat(40), text: 'x' });
const threads: QuestionThread[] = [
   thread('cart-code', code('src/cart.ts'), 'open', cartTotals),
   thread('cart-stop', { kind: 'stop', stopId: cartTotals }, 'resolved', rounding),
   thread('round-text', { kind: 'stopText', stopId: rounding, field: 'how', text: 'rounds', version: 1 }),
   thread('round-file', { kind: 'file', side: 'base', path: 'src/round.ts', commit: 'a'.repeat(40) }, 'resolved', rounding),
   thread('tour', { kind: 'tour' }),
];
const ids = (shown: readonly QuestionThread[]) => shown.map((each) => each.id);
const filter = (overrides: Partial<ThreadFilter>): ThreadFilter => ({ ...defaultThreadFilter, ...overrides });
const every: ShownStates = ['open', 'resolved'];
const atStop = (stopId: string): ThreadPlace => ({ kind: 'stop', stopId });
const atFile = (path: string): ThreadPlace => ({ kind: 'file', path });
const currentStop: ThreadPlace = { kind: 'currentStop' };
const currentFile: ThreadPlace = { kind: 'currentFile' };
const shown = (chosen: ThreadFilter, here: ReviewerPlace = nowhere) => ids(filteredThreads(threads, chosen, here));

describe('the thread filter', () => {
   it('shows the open threads by default and hides the resolved ones until the states include them', () => {
      expect(shown(defaultThreadFilter)).toEqual(['cart-code', 'round-text', 'tour']);
      expect(shown(filter({ states: ['resolved'] }))).toEqual(['cart-stop', 'round-file']);
      expect(shown(filter({ states: every }))).toEqual(ids(threads));
   });

   it("finds a stop anchor's thread at its own stop and every other thread at the stop the reviewer viewed", () => {
      expect(threads.map(threadStopId)).toEqual([cartTotals, cartTotals, rounding, rounding, null]);
   });

   it('shows only the threads of the chosen stop, or only those of the chosen file at either side', () => {
      expect(shown(filter({ where: atStop(cartTotals), states: every }))).toEqual(['cart-code', 'cart-stop']);
      expect(shown(filter({ where: atStop(rounding) }))).toEqual(['round-text']);
      expect(shown(filter({ where: atFile('src/round.ts'), states: every }))).toEqual(['round-file']);
   });

   it('offers the stops in plan order and the paths sorted, each once', () => {
      expect(threadFilterChoices([...threads].reverse(), plan)).toEqual({ stopIds: [cartTotals, rounding], paths: ['src/cart.ts', 'src/round.ts'] });
      expect(threadFilterChoices(threads, null).stopIds).toEqual([cartTotals, rounding]);
   });

   it('falls back to anywhere when no thread is about the chosen stop or file any more', () => {
      expect(applicableFilter(filter({ where: atStop('gone'), states: ['resolved'] }), threads)).toEqual(filter({ states: ['resolved'] }));
      expect(applicableFilter(filter({ where: atFile('src/gone.ts') }), threads)).toEqual(defaultThreadFilter);
      expect(shown(filter({ where: atStop('gone'), states: ['resolved'] }))).toEqual(['cart-stop', 'round-file']);
      expect(applicableFilter(filter({ where: atStop(rounding) }), threads)).toEqual(filter({ where: atStop(rounding) }));
   });

   it('widens as little as it takes to show a thread that the reviewer opens', () => {
      const chosen = filter({ where: atStop(rounding) });

      expect(revealing(chosen, threads[1]!, nowhere)).toEqual(filter({ states: every }));
      expect(revealing(chosen, threads[3]!, nowhere)).toEqual(filter({ where: atStop(rounding), states: every }));
      expect(revealing(defaultThreadFilter, threads[0]!, nowhere)).toEqual(defaultThreadFilter);
   });
});

describe("the filter's Show menu", () => {
   it('shows or hides each state, in the order the menu lists them', () => {
      const all = withState(withState(defaultThreadFilter, 'outdated', true), 'resolved', true);

      expect(all.states).toEqual(['open', 'resolved', 'outdated']);
      expect(withState(all, 'resolved', false).states).toEqual(['open', 'outdated']);
      expect(withState(all, 'open', false).states).toEqual(['resolved', 'outdated']);
   });

   it('keeps the last shown state when the reviewer hides it', () => {
      const resolvedOnly = withState(filter({ states: every }), 'open', false);

      expect(withState(defaultThreadFilter, 'open', false)).toBe(defaultThreadFilter);
      expect(withState(resolvedOnly, 'resolved', false)).toBe(resolvedOnly);
   });

   it('names what shows on its button', () => {
      expect(shownLabel(defaultThreadFilter)).toBe('Open, tour actions');
      expect(shownLabel(filter({ actions: false }))).toBe('Open');
      expect(shownLabel(filter({ states: ['resolved', 'outdated'], actions: false }))).toBe('Resolved, outdated');
      expect(shownLabel(filter({ states: ['open', 'resolved', 'outdated'] }))).toBe('Open, resolved, outdated, tour actions');
   });
});

describe("the filter's Where menu", () => {
   const atCartTotals: ReviewerPlace = { stopId: cartTotals, file: null };
   const atRounding: ReviewerPlace = { stopId: rounding, file: null };

   it('follows the stop that the reviewer opens with the current stop', () => {
      const chosen = filter({ where: currentStop, states: every });

      expect(shown(chosen, atCartTotals)).toEqual(['cart-code', 'cart-stop']);
      expect(shown(chosen, atRounding)).toEqual(['round-text', 'round-file']);
   });

   it('follows the file that the reviewer opens with the current file, at the side of each anchor', () => {
      const chosen = filter({ where: currentFile, states: every });

      expect(shown(chosen, { stopId: null, file: { base: 'src/cart.ts', head: 'src/cart.ts' } })).toEqual(['cart-code']);
      expect(shown(chosen, { stopId: null, file: { base: 'src/round.ts', head: 'src/round.ts' } })).toEqual(['round-file']);
      expect(shown(chosen, { stopId: null, file: { base: 'src/old.ts', head: 'src/round.ts' } })).toEqual([]);
   });

   it('keeps a named stop or file wherever the reviewer is', () => {
      expect(shown(filter({ where: atStop(cartTotals), states: every }), atRounding)).toEqual(['cart-code', 'cart-stop']);
      expect(shown(filter({ where: atFile('src/round.ts'), states: every }), { stopId: cartTotals, file: { base: 'src/cart.ts', head: 'src/cart.ts' } })).toEqual(['round-file']);
   });

   it('shows no threads, and names what is missing, while no stop or no file is open for the current one', () => {
      expect(shown(filter({ where: currentStop }))).toEqual([]);
      expect(missingPlace(filter({ where: currentStop }), nowhere)).toBe('stop');
      expect(shown(filter({ where: currentFile }), atCartTotals)).toEqual([]);
      expect(missingPlace(filter({ where: currentFile }), atCartTotals)).toBe('file');
      expect(missingPlace(filter({ where: currentStop }), atCartTotals)).toBeNull();
      expect(missingPlace(defaultThreadFilter, nowhere)).toBeNull();
   });

   it('widens the current stop or file to anywhere only for a thread that is elsewhere', () => {
      expect(revealing(filter({ where: currentStop }), threads[2]!, atRounding)).toEqual(filter({ where: currentStop }));
      expect(revealing(filter({ where: currentStop }), threads[2]!, atCartTotals)).toEqual(defaultThreadFilter);
      expect(revealing(filter({ where: currentFile }), threads[4]!, atCartTotals)).toEqual(defaultThreadFilter);
   });
});

describe('an outdated thread', () => {
   const outdated: QuestionThread = { ...thread('stale', { kind: 'tour' }), state: 'outdated' };
   const all = [...threads, outdated];

   it('is a state that the filter lists last, hidden by default', () => {
      expect(threadStates).toEqual(['open', 'resolved', 'outdated']);
      expect(defaultThreadFilter.states).toEqual(['open']);
   });

   it('is hidden until the state filter includes outdated threads', () => {
      expect(ids(filteredThreads(all, defaultThreadFilter, nowhere))).not.toContain('stale');
      expect(ids(filteredThreads(all, filter({ states: every }), nowhere))).not.toContain('stale');
      expect(ids(filteredThreads(all, filter({ states: ['outdated'] }), nowhere))).toEqual(['stale']);
   });

   it('widens the filter to show it when the reviewer opens it', () => {
      expect(revealing(defaultThreadFilter, outdated, nowhere)).toEqual(filter({ states: ['open', 'outdated'] }));
   });
});
