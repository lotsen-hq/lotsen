import { describe, expect, it } from 'vitest';
import { OpenStop, type PlanSource } from '../src/lib/openStop.svelte';
import type { PinCommits } from '../src/lib/pin';
import { contentOf } from '../src/lib/stops';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { PendingWork, QuestionThread, Stop, StopPassage, Tour, TourControls, WrittenStop } from '../src/lib/types';
import { eventFixtures, outputFixtures } from './contract/fixtures.generated';
import { Reactive } from './reactive.svelte';

// The fixtures show one tour at one revision, so each test moves the revisions where its order needs them.
const tour: Tour = outputFixtures.read_state;
const at = tour.revision;
const pinned = tour.pin as Extract<Tour['pin'], { state: 'pinned' }>;
const pin: PinCommits = { base: pinned.mergeBase, head: pinned.head };
// The update to iteration 2 moves the head to `c…` and names s4, and its decided thread waits for Claude.
const update = outputFixtures.update_iteration;
const updateThread = update.thread as QuestionThread;
const newHead = (update.pin as typeof pinned).head;
const updatedAt = '2026-10-30T09:10:00+00:00';

function controls(pending: Partial<PendingWork>, revision = at): TourControls {
   const offers = eventFixtures.controlsChanged.offers;
   return { ...eventFixtures.controlsChanged, revision, offers: { ...offers, pending: { stops: {}, plan: [], summary: {}, ...pending } } };
}

interface Open {
   record: TourRecord;
   commits: Reactive<PinCommits | null>;
   iteration: Reactive<number | null>;
   open: OpenStop;
}

/** The fixture tour with no waiting work, and its open-stop state with the stop open, at the tour's pin. */
function opened(stopId: string | null, pending: Partial<PendingWork> = {}): Open {
   const record = new TourRecord(tour.id);
   record.applyLoad(structuredClone(tour));
   record.putControls(controls(pending));
   const commits = new Reactive<PinCommits | null>(pin);
   const iteration = new Reactive<number | null>(pinned.iteration);
   const open = new OpenStop(
      record,
      () => commits.value,
      () => iteration.value,
   );
   open.open(stopId);
   return { record, commits, iteration, open };
}

function stopOf(record: TourRecord, stopId: string): WrittenStop {
   return structuredClone(record.plan!.stops.find((stop) => stop.id === stopId)) as WrittenStop;
}

/** Returns the stop at its next version, as Claude's revision writes it, with the replaced version among its earlier versions. */
function revised(stop: WrittenStop, headline: string, passages: StopPassage[] = stop.content.passages): WrittenStop {
   const { title, kind, content } = stop;
   return { ...stop, version: stop.version + 1, earlier: [...stop.earlier, { title, kind, content, replacedAt: updatedAt }], status: 'revised', content: { ...content, headline, passages } };
}

/** Stores a revision through the open stop's hold rule, at a later revision of the tour, as an event or a load brings it. */
function revise({ record, open }: Open, stop: WrittenStop, revision = at + 1, kind: PlanSource['kind'] = 'event'): void {
   open.applyPlan(() => record.putStop(stop, revision), updatedAt, { kind, revision });
}

const moved = (passage: StopPassage, by: number, commit = newHead): StopPassage => ({ ...passage, startLine: passage.startLine + by, endLine: passage.endLine + by, commit });
const headlineOf = (stop: Stop | null) => contentOf(stop)?.headline;

describe('the open stop', () => {
   it('shows a pending stop with no passages, focus, version or stale marker', () => {
      const { open } = opened('s5');

      expect(open.stop?.stage).toBe('pending');
      expect(open.viewedStopId).toBe('s5');
      expect(open.pinnedPassages).toEqual([]);
      expect(open.focusedPassage).toBeNull();
      expect(open.focusedIndex).toBe(-1);
      expect(open.shownVersion).toBeNull();
      expect(open.stale).toEqual({ kind: 'current' });
   });

   it('names no stop under view for a stop that the plan does not hold', () => {
      const { open } = opened('gone');

      expect(open.stopId).toBe('gone');
      expect(open.stop).toBeNull();
      expect(open.viewedStopId).toBeNull();
   });

   describe('across a revision while the reviewer reads it', () => {
      it('keeps the version the reviewer read, and names the newer one', () => {
         const state = opened('s2');
         const read = headlineOf(state.open.stop);

         revise(state, revised(stopOf(state.record, 's2'), 'The writer stores the tour, revised.'));

         expect(state.open.current).toMatchObject({ version: 3 });
         expect(state.open.shownVersion).toEqual({ number: 2, current: 3, reason: { kind: 'revised' } });
         expect(headlineOf(state.open.stop)).toBe(read);
         expect(state.record.earlierVersions.filter((version) => version.stopId === 's2').map((version) => version.number)).toEqual([1, 2]);
      });

      it('shows the current version once the reviewer moves on', () => {
         const state = opened('s2');
         revise(state, revised(stopOf(state.record, 's2'), 'The writer stores the tour, revised.'));

         state.open.moveOn();

         expect(state.open.shownVersion).toBeNull();
         expect(headlineOf(state.open.stop)).toBe('The writer stores the tour, revised.');
      });

      it('keeps the version that the reviewer opened, as opened, and shows no stale marker of an earlier version', () => {
         const state = opened('s4');
         revise(state, revised(stopOf(state.record, 's4'), 'Retry, revised.'));
         state.open.moveOn();

         state.open.readVersion(1);
         revise(state, revised(stopOf(state.record, 's4'), 'Retry, revised again.'), at + 2);

         expect(state.open.shownVersion).toEqual({ number: 1, current: 3, reason: { kind: 'opened' } });
         expect(state.open.stale).toEqual({ kind: 'current' });
      });

      it("shows a revision that the stop's own waiting tour action asked for", () => {
         const state = opened('s2', { stops: { s2: ['t3'] } });

         revise(state, revised(stopOf(state.record, 's2'), 'Explained.'));

         expect(state.open.shownVersion).toBeNull();
         expect(headlineOf(state.open.stop)).toBe('Explained.');
      });

      it('shows a revision while an action on the plan waits, which asks for revisions of every stop', () => {
         const state = opened('s2', { plan: ['t6'] });

         revise(state, revised(stopOf(state.record, 's2'), 'Replanned.'));

         expect(state.open.shownVersion).toBeNull();
      });

      it("keeps the version for a waiting iteration update that names the stop, beside a waiting action on the plan, and names the update's iteration", () => {
         const state = opened('s4', { plan: ['t6', updateThread.id], iteration: updateThread.id });
         state.record.putThread(updateThread, at);

         revise(state, revised(stopOf(state.record, 's4'), 'Retry, for iteration 2.'));

         expect(state.open.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'iterationUpdate', iteration: 2 } });
      });

      it("keeps the version for an update that waited before a load brought the revision with Claude's reply", () => {
         const state = opened('s4', { plan: [updateThread.id], iteration: updateThread.id });
         state.record.putThread(updateThread, at);

         state.record.putControls(controls({}, at + 1));
         revise(state, revised(stopOf(state.record, 's4'), 'Retry, for iteration 2.'), at + 1, 'load');

         expect(state.open.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'iterationUpdate', iteration: 2 } });
      });

      it("keeps the version for a revision that nobody asked for after Claude answered the stop's action without a revision", () => {
         const state = opened('s2', { stops: { s2: ['t3'] } });
         state.record.putControls(controls({}, at + 1));

         revise(state, revised(stopOf(state.record, 's2'), 'Revised later.'), at + 2);

         expect(state.open.shownVersion).toMatchObject({ number: 2, reason: { kind: 'revised' } });
      });

      it('keeps the version for a revision that nobody asked for after Claude answered an action on the plan without a revision', () => {
         const state = opened('s2', { plan: ['t6'] });
         state.record.putControls(controls({}, at + 1));

         revise(state, revised(stopOf(state.record, 's2'), 'Revised later.'), at + 2);

         expect(state.open.shownVersion).toMatchObject({ number: 2, reason: { kind: 'revised' } });
      });

      it("keeps the version for a waiting iteration update that names the stop after Claude answered the stop's own action", () => {
         const state = opened('s4', { stops: { s4: ['t3'] } });
         state.record.putControls(controls({}, at + 1));
         state.record.putThread(updateThread, at + 2);
         state.record.putControls(controls({ plan: [updateThread.id], iteration: updateThread.id }, at + 2));

         revise(state, revised(stopOf(state.record, 's4'), 'Retry, for iteration 2.'), at + 3);

         expect(state.open.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'iterationUpdate', iteration: 2 } });
      });

      it('shows a revision that a waiting action on the plan asked for after an iteration update that named the stop ended', () => {
         const state = opened(null, { plan: [updateThread.id], iteration: updateThread.id });
         state.record.putThread(updateThread, at);
         state.open.open('s4');
         state.record.putControls(controls({}, at + 1));
         state.record.putControls(controls({ plan: ['t6'] }, at + 2));

         revise(state, revised(stopOf(state.record, 's4'), 'Replanned.'), at + 3);

         expect(state.open.shownVersion).toBeNull();
      });

      it("shows a revision that the stop's own action asked for when a load brings it with Claude's reply", () => {
         const state = opened('s2', { stops: { s2: ['t3'] } });

         state.record.putControls(controls({}, at + 1));
         revise(state, revised(stopOf(state.record, 's2'), 'Explained.'), at + 1, 'load');

         expect(state.open.shownVersion).toBeNull();
      });

      it("shows a revision that the stop's own action asked for when a response brings the controls after Claude's reply before the revision's event", () => {
         const state = opened('s2', { stops: { s2: ['t3'] } });
         state.record.putControls(controls({}, at + 3));

         revise(state, revised(stopOf(state.record, 's2'), 'Explained.'), at + 1);

         expect(state.open.shownVersion).toBeNull();
      });

      it("shows a revision that the stop's own action asked for when a response brings the controls after Claude's reply before the load that brings the revision", () => {
         const state = opened('s2', { stops: { s2: ['t3'] } });
         state.record.putControls(controls({}, at + 3));

         revise(state, revised(stopOf(state.record, 's2'), 'Explained.'), at + 4, 'load');

         expect(state.open.shownVersion).toBeNull();
      });

      it("keeps the version for a revision that a load brings after the page heard the stop's action end on the stream that the last load joined", () => {
         const state = opened('s2', { stops: { s2: ['t3'] } });
         state.record.heardLoad(at, true);
         state.record.putControls(controls({}, at + 1));
         state.record.heardEvent(at + 1);
         state.record.streamOpened();

         revise(state, revised(stopOf(state.record, 's2'), 'Revised later.'), at + 3, 'load');

         expect(state.open.shownVersion).toEqual({ number: 2, current: 3, reason: { kind: 'revised' } });
      });

      it("shows a revision that a load brings when Claude's reply arrives first on a stream that no load joined yet", () => {
         const state = opened('s2', { stops: { s2: ['t3'] } });
         state.record.heardLoad(at, true);
         state.record.streamOpened();
         state.record.putControls(controls({}, at + 2));
         state.record.heardEvent(at + 2);

         revise(state, revised(stopOf(state.record, 's2'), 'Explained.'), at + 3, 'load');

         expect(state.open.shownVersion).toBeNull();
      });

      it("keeps the version for a revision that a load brings when the reviewer asks for one of the stop only in the load's own store", () => {
         const state = opened('s2');
         state.record.putControls(controls({ stops: { s2: ['t3'] } }, at + 1));

         revise(state, revised(stopOf(state.record, 's2'), 'Revised before the ask.'), at + 1, 'load');

         expect(state.open.shownVersion).toEqual({ number: 2, current: 3, reason: { kind: 'revised' } });
      });

      it("keeps the version for a revision that a load brings when the stop's action ended before the version on screen", () => {
         const state = opened('s2', { stops: { s2: ['t3'] } });
         state.record.putControls(controls({}, at + 1));
         revise(state, revised(stopOf(state.record, 's2'), 'Explained.'), at + 1);
         state.open.moveOn();

         revise(state, revised(stopOf(state.record, 's2'), 'Revised later.'), at + 3, 'load');

         expect(state.open.shownVersion).toMatchObject({ number: 3, reason: { kind: 'revised' } });
      });

      it('makes no hold for a revision of another stop, and drops the hold when another stop opens', () => {
         const state = opened('s2');
         revise(state, revised(stopOf(state.record, 's3'), 'The lock, revised.'));
         expect(state.open.shownVersion).toBeNull();

         revise(state, revised(stopOf(state.record, 's2'), 'The writer, revised.'), at + 2);
         state.open.open('s3');
         state.open.open('s2');

         expect(state.open.shownVersion).toBeNull();
      });
   });

   describe('across an iteration update', () => {
      /** The update's plan: s2's passages move by `by` lines to the new head, and s4 keeps its passage at the old head with a stale marker. */
      function updated(state: Open, by: number): void {
         const s2 = stopOf(state.record, 's2');
         const s4 = stopOf(state.record, 's4');
         const plan = structuredClone(state.record.plan!);
         plan.stops = plan.stops.map((stop) =>
            stop.id === 's2' ? { ...s2, content: { ...s2.content, passages: s2.content.passages.map((passage) => moved(passage, by)) } } : stop.id === 's4' ? { ...s4, staleOn: 2 } : stop,
         );
         state.commits.value = { base: pin.base, head: newHead };
         state.iteration.value = 2;
         state.open.applyPlan(() => state.record.putPlan(plan, at + 1), updatedAt, { kind: 'event', revision: at + 1 });
      }

      it('takes the moved passages at the new pin, and makes another stop stale with its passages at an earlier commit', () => {
         const state = opened('s2');

         updated(state, 2);

         expect(state.open.pinnedPassages.map((passage) => [passage.startLine, passage.commit])).toEqual([
            [3, newHead],
            [6, newHead],
         ]);
         expect(state.open.stale).toEqual({ kind: 'current' });
         state.open.open('s4');
         expect(state.open.pinnedPassages).toEqual([]);
         expect(state.open.stale).toEqual({ kind: 'stale', since: 2, target: 2, revising: false, earlierPassages: true });
      });

      it('says that Claude revises a stale stop while the update that names it waits', () => {
         const state = opened('s4', { iteration: updateThread.id });
         state.record.putThread(updateThread, at);

         updated(state, 0);

         expect(state.open.stale).toMatchObject({ kind: 'stale', since: 2, target: 2, revising: true });
      });

      it('names the update that waits as the target of a stale stop that Claude revises, whatever the pin holds', () => {
         const state = opened('s4', { iteration: updateThread.id });
         state.record.putThread(updateThread, at);

         updated(state, 0);
         state.iteration.value = 5;

         expect(state.open.stale).toMatchObject({ kind: 'stale', since: 2, target: 2, revising: true });
      });

      it('names the pin as the target of a stale stop that no update waits for, which a later update leaves past its stale marker', () => {
         const state = opened('s2');

         updated(state, 0);
         state.iteration.value = 3;
         state.open.open('s4');

         expect(state.open.stale).toMatchObject({ kind: 'stale', since: 2, target: 3, revising: false });
      });

      it('has no target while the page knows no iteration of the pin and no update waits', () => {
         const state = opened('s2');

         updated(state, 0);
         state.iteration.value = null;
         state.open.open('s4');

         expect(state.open.stale).toMatchObject({ kind: 'stale', since: 2, target: null });
      });

      it('marks no passage of the open stop while the page holds the new pin before the plan', () => {
         const state = opened('s2');

         state.commits.value = { base: pin.base, head: newHead };

         expect(state.open.pinnedPassages).toEqual([]);
         expect(state.open.focusedPassage).toBeNull();
         expect(state.open.stale).toEqual({ kind: 'current' });
      });

      it('keeps the focus on the passage that the update moved to other lines', () => {
         const state = opened('s2');
         state.open.focus(state.open.stepPassages[1]!);

         updated(state, 2);

         expect(state.open.focusedIndex).toBe(1);
         expect(state.open.focusedPassage).toMatchObject({ role: 'related', startLine: 6 });
      });

      it('follows the lines of a passage to their new place, and focuses other lines only at the pin that they were opened at', () => {
         const state = opened('s2');
         const passage = state.open.stepPassages[1]!;
         const target = { lines: { path: passage.path, side: passage.side, line: passage.startLine }, commit: pin.head, passage: state.open.placeOf(passage) };

         updated(state, 2);

         expect(state.open.lineFocus(target, null)).toEqual({ path: passage.path, side: 'head', line: 6 });
         expect(state.open.lineFocus({ ...target, passage: { ...target.passage!, index: 0 } }, null)).toEqual({ path: passage.path, side: 'head', line: 3 });
         expect(state.open.lineFocus({ ...target, passage: null }, null)).toBeNull();
      });
   });

   describe('across a revision that keeps the focused lines', () => {
      it("keeps the focus on the same occurrence of those lines, whatever the passage's label or role", () => {
         const state = opened('s2');
         state.open.focus(state.open.stepPassages[1]!);
         revise(
            state,
            revised(
               stopOf(state.record, 's2'),
               'Relabeled.',
               stopOf(state.record, 's2').content.passages.map((passage) => ({ ...passage, label: 'renamed' })),
            ),
         );
         state.open.moveOn();

         expect(state.open.focusedIndex).toBe(1);
         expect(state.open.focusedPassage).toMatchObject({ startLine: 4, label: 'renamed' });
      });
   });
});
