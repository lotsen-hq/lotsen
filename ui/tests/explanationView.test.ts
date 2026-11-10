import { describe, expect, it, vi } from 'vitest';
import { ExplanationView, type ExplanationPage } from '../src/lib/explanationView.svelte';
import { OpenStop } from '../src/lib/openStop.svelte';
import type { PinCommits } from '../src/lib/pin';
import type { PlacementMode } from '../src/lib/placement';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { QuestionThread, Tour, WrittenStop } from '../src/lib/types';
import { CheckOpening } from '../src/lib/viewState';
import { outputFixtures } from './contract/fixtures.generated';
import { explanationOf, explanationPage } from './explanation';
import { Reactive } from './reactive.svelte';

const tour: Tour = outputFixtures.read_state;
const pinned = tour.pin as Extract<Tour['pin'], { state: 'pinned' }>;
const pin: PinCommits = { base: pinned.mergeBase, head: pinned.head };

/** The fixture tour's first written stop with a primary passage at the pin. */
const shownStop = tour.plan!.stops.find(
   (stop): stop is WrittenStop => stop.stage === 'written' && stop.content.passages.some((passage) => passage.role === 'primary' && passage.commit === pin[passage.side]),
)!;

interface Shown {
   view: ExplanationView;
   open: OpenStop;
   mode: Reactive<PlacementMode>;
   commits: Reactive<PinCommits | null>;
}

/** The explanation of the fixture tour's open stop, at the tour's pin, in a placement mode that a test changes. */
function shown(mode: PlacementMode, page: Partial<ExplanationPage> = {}): Shown {
   const record = new TourRecord(tour.id);
   record.applyLoad(structuredClone(tour));
   const commits = new Reactive<PinCommits | null>(pin);
   const open = new OpenStop(
      record,
      () => commits.value,
      () => null,
   );
   open.open(shownStop.id);
   const placement = new Reactive(mode);
   const view = new ExplanationView(
      () => open,
      () => ({ mode: placement.value, threshold: 600, layout: 'inline' }),
      explanationPage(page),
   );
   return { view, open, mode: placement, commits };
}

describe('an explanation', () => {
   it('belongs in the Stop panel in side placement, with no inline passage', () => {
      const { view } = shown('side');

      expect([view.placement, view.host, view.inlinePassage]).toEqual(['side', 'panel', null]);
   });

   it('belongs inline below its primary passage at the pin in inline placement, and shows in the panel until an editor places its block', () => {
      const { view, open } = shown('inline');

      expect([view.placement, view.host, view.inlinePassage]).toEqual(['inline', 'panel', open.primaryPassage]);
      const removed = view.place();
      expect(view.host).toBe('inline');
      removed();
      expect(view.host).toBe('panel');
   });

   it('stays inline while any editor shows its block', () => {
      const { view } = shown('inline');
      const first = view.place();
      const second = view.place();

      first();

      expect(view.host).toBe('inline');
      second();
      expect(view.host).toBe('panel');
   });

   it('belongs in the panel when its primary passage is not at the pin, whatever the placement mode', () => {
      const { view, commits } = shown('inline');
      view.place();

      commits.value = { base: 'e'.repeat(40), head: 'f'.repeat(40) };

      expect([view.placement, view.host, view.inlinePassage]).toEqual(['side', 'panel', null]);
   });

   it('follows a change of the placement mode', () => {
      const { view, mode } = shown('side');

      mode.value = 'inline';

      expect(view.placement).toBe('inline');
   });

   it('shows the stop under view with the markers of its threads, and nothing once no stop is open', () => {
      const thread = { ...(tour.threads[0] as QuestionThread), id: 'stop-thread', anchor: { kind: 'stop', stopId: shownStop.id } } as QuestionThread;
      const { view, open } = shown('side', { threads: [thread] });

      expect(view.content?.stop).toBe(open.stop);
      expect(view.content?.markers.map((marker) => marker.id)).toEqual(['stop-thread']);
      open.open(null);
      expect(view.content).toBeNull();
      expect(view.stop).toBeNull();
   });

   it('is movable only in mixed placement, and moves only then', () => {
      const movePlacement = vi.fn();
      const { view, mode } = shown('inline', { movePlacement });

      view.move('side');
      expect(view.movable).toBe(false);
      mode.value = 'mixed';
      view.move('side');

      expect(view.movable).toBe(true);
      expect(movePlacement.mock.calls).toEqual([['side']]);
   });

   describe('the check that the summary page opened', () => {
      const check = new CheckOpening(shownStop.id, 'c1');

      it('goes to the inline block, and not to the panel that stands in for it while an editor can still show the block', () => {
         const { view } = shown('inline', { openedCheck: check });

         expect(view.openedCheck('inline')).toBe(check);
         expect(view.openedCheck('panel')).toBeNull();
      });

      it('goes to the panel when the explanation belongs there, or when no editor can show the block', () => {
         expect(shown('side', { openedCheck: check }).view.openedCheck('panel')).toBe(check);
         expect(shown('inline', { openedCheck: check, blockCanShow: false }).view.openedCheck('panel')).toBe(check);
      });

      it('goes nowhere for another stop', () => {
         expect(shown('side', { openedCheck: new CheckOpening('another', 'c1') }).view.openedCheck('panel')).toBeNull();
      });
   });

   it('offers no ask and no Explain on an earlier stop version', () => {
      const current = explanationOf(shownStop);
      const earlier = explanationOf(shownStop, { fields: { shownVersion: { number: 1, current: 2, reason: { kind: 'opened' } } } });

      expect([current.view.ask, current.view.explain]).not.toContain(null);
      expect([earlier.view.ask, earlier.view.explain]).toEqual([null, null]);
   });

   it('keeps the sections that the reviewer opened per stop and host', () => {
      const { view, open } = shown('side');
      const panel = view.sections('panel');

      expect(view.sections('inline')).not.toBe(panel);
      expect(view.sections('panel')).toBe(panel);
      open.open(tour.plan!.stops.find((stop) => stop.id !== shownStop.id)!.id);
      expect(view.sections('panel')).not.toBe(panel);
   });
});
