import { describe, expect, it } from 'vitest';
import { defaultThreadFilter, nowhere, type ReviewerPlace } from '../src/lib/threadFilters';
import { ThreadsView } from '../src/lib/threadsView.svelte';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { QuestionThread, Tour } from '../src/lib/types';
import { outputFixtures } from './contract/fixtures.generated';
import { Reactive } from './reactive.svelte';

const tour: Tour = outputFixtures.read_state;
const at = tour.revision;

function loaded(): TourRecord {
   const record = new TourRecord(tour.id);
   record.applyLoad(structuredClone(tour));
   return record;
}

function threadOf(record: TourRecord, id: string): QuestionThread {
   return structuredClone(record.thread(id)!);
}

const ids = (threads: readonly QuestionThread[]) => threads.map((thread) => thread.id);

describe('the threads view of a tour', () => {
   it("shows the record's open threads by default, with the counts of those shown and of all", () => {
      const view = new ThreadsView(loaded(), () => nowhere);

      expect(view.filter).toBe(defaultThreadFilter);
      expect(ids(view.shown)).toEqual(['t1', 't2', 't3', 't4', 't5', 't6']);
      expect(view.counts).toEqual({ shown: 6, total: 7 });
   });

   it("shows the threads of the reviewer's filter, and offers the stops in the plan's order and the paths", () => {
      const view = new ThreadsView(loaded(), () => nowhere);

      view.choose({ ...defaultThreadFilter, where: { kind: 'stop', stopId: 's2' } });

      expect(ids(view.shown)).toEqual(['t1', 't2']);
      expect(view.counts).toEqual({ shown: 2, total: 7 });
      expect(view.options).toEqual({ stopIds: ['s2', 's3'], paths: ['src/a.cs', 'src/old.cs'] });
   });

   it('follows the record, as when an event brings a thread or a write removes one', () => {
      const record = loaded();
      const view = new ThreadsView(record, () => nowhere);

      record.putThread({ ...threadOf(record, 't8'), id: 't9', state: 'open' }, at + 1);
      record.removeThread('t1', at + 2);

      expect(ids(view.shown)).toEqual(['t2', 't3', 't4', 't5', 't6', 't9']);
      expect(view.counts).toEqual({ shown: 6, total: 7 });
   });

   it('counts a chosen stop that no thread is about any more as every stop', () => {
      const record = loaded();
      const view = new ThreadsView(record, () => nowhere);
      view.choose({ ...defaultThreadFilter, where: { kind: 'stop', stopId: 's3' } });

      record.removeThread('t3', at + 1);

      expect(view.applied.where).toEqual({ kind: 'anywhere' });
      expect(view.counts.shown).toBe(5);
   });

   it('follows where the reviewer is with the current stop, and says when no stop is open', () => {
      const here = new Reactive<ReviewerPlace>(nowhere);
      const view = new ThreadsView(loaded(), () => here.value);
      view.choose({ ...defaultThreadFilter, where: { kind: 'currentStop' } });

      expect(view.shown).toEqual([]);
      expect(view.missing).toBe('stop');
      here.value = { stopId: 's3', file: null };

      expect(view.missing).toBeNull();
      expect(ids(view.shown)).toEqual(['t3']);
      here.value = { stopId: 's2', file: null };
      expect(ids(view.shown)).toEqual(['t1', 't2']);
   });

   it('widens the filter as little as it takes to reveal a hidden thread, and keeps it when the thread shows', () => {
      const view = new ThreadsView(loaded(), () => nowhere);

      view.reveal('t2');
      expect(view.filter).toBe(defaultThreadFilter);

      view.reveal('t8');
      expect(view.filter).toEqual({ ...defaultThreadFilter, states: ['open', 'outdated'] });
      expect(ids(view.shown)).toContain('t8');
   });

   describe('after a write', () => {
      it('reveals the thread that the write stored', () => {
         const record = loaded();
         const view = new ThreadsView(record, () => nowhere);
         const reveal = view.revealAfterWrite();
         const resolved = { ...threadOf(record, 't2'), state: 'resolved' as const };

         record.putThread(resolved, at + 1);
         reveal(resolved);

         expect(view.filter.states).toEqual(['open', 'resolved']);
      });

      it('leaves the filter that the reviewer chose while the write was in flight', () => {
         const record = loaded();
         const view = new ThreadsView(record, () => nowhere);
         const reveal = view.revealAfterWrite();
         const chosen = { ...defaultThreadFilter, actions: false };
         view.choose(chosen);
         const resolved = { ...threadOf(record, 't2'), state: 'resolved' as const };

         record.putThread(resolved, at + 1);
         reveal(resolved);

         expect(view.filter).toBe(chosen);
      });

      it('leaves the filter when the record holds a newer state of the thread', () => {
         const record = loaded();
         const view = new ThreadsView(record, () => nowhere);
         const reveal = view.revealAfterWrite();
         const resolved = { ...threadOf(record, 't2'), state: 'resolved' as const };

         record.putThread(threadOf(record, 't2'), at + 2);
         record.putThread(resolved, at + 1);
         reveal(resolved);

         expect(view.filter).toBe(defaultThreadFilter);
      });
   });

   describe('with a pick', () => {
      const lines = { kind: 'code' as const, side: 'head' as const, path: 'src/a.cs', startLine: 1, endLine: 2 };

      it('holds each pick as a new object, so the Threads panel takes it up again', () => {
         const view = new ThreadsView(loaded(), () => nowhere);

         view.pick(lines, null);
         const first = view.picked;
         view.pick(lines, null);

         expect(view.picked).toEqual({ anchor: lines, quote: null });
         expect(view.picked).not.toBe(first);
      });

      it('releases the pick that a write asked about, but not a newer pick', () => {
         const view = new ThreadsView(loaded(), () => nowhere);
         view.pick(lines, null);
         const asked = view.picked!;

         view.pick({ ...lines, startLine: 2 }, null);
         view.release(asked);
         expect(view.picked?.anchor.kind).toBe('code');

         view.release(view.picked!);
         expect(view.picked).toBeNull();
      });
   });

   it('opens a thread view with the filter widened to show the thread, and shows the thread list again on a pick, so the ask box that holds the pick shows', () => {
      const view = new ThreadsView(loaded(), () => nowhere);
      view.choose({ ...defaultThreadFilter, where: { kind: 'stop', stopId: 's2' } });

      view.open('t3');

      expect(view.opened?.id).toBe('t3');
      expect(ids(view.shown)).toContain('t3');
      view.pick({ kind: 'file', side: 'head', path: 'src/a.cs' }, null);
      expect(view.opened).toBeNull();
   });

   it('shows the thread list again once the thread of the thread view leaves the record', () => {
      const record = loaded();
      const view = new ThreadsView(record, () => nowhere);
      view.open('t1');

      record.removeThread('t1', at + 1);

      expect(view.opened).toBeNull();
   });
});
