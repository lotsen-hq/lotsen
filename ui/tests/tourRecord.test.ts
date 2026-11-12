import { describe, expect, it } from 'vitest';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { CheckAnswer, PendingWork, QuestionThread, Stop, Tour, TourControls } from '../src/lib/types';
import { eventFixtures, outputFixtures } from './contract/fixtures.generated';

// The fixtures show one tour at one revision, so each test moves the revisions where its order needs them.
const tour: Tour = outputFixtures.read_state;
const at = tour.revision;

function loaded(change: (copy: Tour) => Partial<Tour> = () => ({}), revision = at): TourRecord {
   const record = new TourRecord(tour.id);
   record.applyLoad(load(change, revision));
   return record;
}

function load(change: (copy: Tour) => Partial<Tour> = () => ({}), revision = at): Tour {
   const copy = structuredClone(tour);
   return { ...copy, ...change(copy), revision };
}

function threadOf(id: string): QuestionThread {
   return structuredClone(tour.threads.find((thread) => thread.id === id)!);
}

const ids = (record: TourRecord) => record.threads.map((thread) => thread.id);
const created = eventFixtures.threadCreated.thread;

describe('a tour record', () => {
   it("shows the load's plan, threads, answers, skips, iteration and progress", () => {
      const record = loaded();

      expect(record.plan).toEqual(tour.plan);
      expect(record.threads).toEqual(tour.threads);
      expect(record.answers).toEqual(tour.answers);
      expect(record.skippedStops).toEqual(tour.plan!.stops.filter((stop) => stop.skipped === true).map((stop) => stop.id));
      expect(record.availableIteration).toEqual(tour.availableIteration ?? null);
      expect(record.progress).toEqual(tour.progress ?? null);
   });

   it('knows no plan, iteration or controls before a load or an event brings them', () => {
      const record = new TourRecord(tour.id);

      expect(record.plan).toBeUndefined();
      expect(record.knownPlan).toBeUndefined();
      expect(record.availableIteration).toBeUndefined();
      expect(record.controls).toBeUndefined();
      expect(record.threads).toEqual([]);
   });

   describe('with a write response that arrives before its event', () => {
      it("drops the response's own event and an earlier event of the same thread, and keeps an earlier event of another thread", () => {
         const record = loaded((copy) => ({ threads: copy.threads.filter((thread) => thread.id !== created.id) }), 10);
         const written = { ...threadOf('t1'), state: 'resolved' as const };
         const earlier = { ...threadOf('t1'), state: 'open' as const };

         expect(record.putThread(written, 12)).toBe(true);
         expect(record.putThread(written, 12)).toBe(false);
         expect(record.putThread(earlier, 11)).toBe(false);
         expect(record.putThread(created, 11)).toBe(true);

         expect(record.thread('t1')?.state).toBe('resolved');
         expect(record.thread(created.id)).toEqual(created);
      });

      it('keeps the answers of an earlier event while the response raised only the skips', () => {
         const record = loaded(() => ({ answers: [] }), 10);

         record.putSkips(['s1', 's2'], 12);
         record.putSkips(['s1'], 11);
         const answer: CheckAnswer = eventFixtures.answerUpdated.answer;
         record.putAnswer(answer, 11);

         expect(record.skippedStops).toEqual(['s1', 's2']);
         expect(record.answers).toEqual([answer]);
      });

      it('keeps the later of two skip writes at one update time, by its revision, whichever response arrives last', () => {
         const record = loaded(() => ({}), 10);

         record.putSkips(['s2', 's3'], 12);
         record.putSkips(['s2'], 11);

         expect(record.skippedStops).toEqual(['s2', 's3']);
      });
   });

   it('drops an event that arrives after a newer response of the same entity', () => {
      const record = loaded(() => ({}), 10);
      record.putIteration(null, 12);

      expect(record.putIteration(eventFixtures.availableIterationChanged.availableIteration, 11)).toBe(false);
      expect(record.availableIteration).toBeNull();
   });

   it('keeps a removal over an older load that still holds the thread', () => {
      const record = loaded(() => ({}), 8);
      record.removeThread('t1', 9);

      record.applyLoad(load(() => ({}), 8));

      expect(ids(record)).not.toContain('t1');
      expect(record.putThread(threadOf('t1'), 8)).toBe(false);
   });

   it('keeps a cleared answer cleared over an older load that still holds it', () => {
      const [answer] = tour.answers;
      const record = loaded(() => ({}), 8);
      record.clearAnswer(answer!.stopId, answer!.checkId, 9);

      record.applyLoad(load(() => ({}), 8));

      expect(record.answers).not.toContainEqual(answer);
   });

   describe('with a snapshot after a reconnect', () => {
      it('removes the threads that it lacks and that the page knew at a lower revision, and keeps those that a newer event brought', () => {
         const record = loaded(() => ({}), 5);
         record.putThread(created, 9);

         record.applyLoad(load((copy) => ({ threads: copy.threads.filter((thread) => thread.id !== 't1' && thread.id !== created.id) }), 8));

         expect(ids(record)).not.toContain('t1');
         expect(ids(record)).toContain(created.id);
         expect(ids(record).at(-1)).toBe(created.id);
      });

      it('removes a thread that a newer event brought once a newer snapshot lacks it', () => {
         const record = loaded(() => ({}), 5);
         record.putThread(created, 9);

         record.applyLoad(load((copy) => ({ threads: copy.threads.filter((thread) => thread.id !== created.id) }), 10));

         expect(ids(record)).not.toContain(created.id);
      });

      it("keeps a stop that an event wrote after the snapshot's revision, and takes the snapshot's other stops", () => {
         const record = loaded(() => ({}), 5);
         const written: Stop = { ...structuredClone(tour.plan!.stops[0]!), title: 'Written later' };
         record.putStop(written, 9);

         record.applyLoad(load((copy) => ({ plan: { ...copy.plan!, stops: copy.plan!.stops.map((stop) => ({ ...stop, title: `${stop.title} at 8` })) } }), 8));

         expect(record.plan?.stops.map((stop) => stop.title)).toEqual(tour.plan!.stops.map((stop, index) => (index === 0 ? 'Written later' : `${stop.title} at 8`)));
      });
   });

   describe('with a plan and its stops', () => {
      it("keeps a stop that an event wrote over a load from before the write, and keeps the rest of the load's plan", () => {
         const record = loaded(() => ({}), 5);
         const written: Stop = { ...structuredClone(tour.plan!.stops[1]!), title: 'Rewritten' };
         record.putStop(written, 11);

         record.applyLoad(load((copy) => ({ plan: { ...copy.plan!, fileMarks: [] } }), 10));

         expect(record.plan?.stops[1]?.title).toBe('Rewritten');
         expect(record.plan?.fileMarks).toEqual([]);
      });

      it('drops the stops that a newer plan leaves out, and an older stop event does not bring one back', () => {
         const record = loaded(() => ({}), 5);
         const kept = tour.plan!.stops.slice(0, 2);

         expect(record.putPlan({ ...structuredClone(tour.plan!), stops: structuredClone(kept) }, 9)).toBe(true);
         expect(record.putStop(structuredClone(tour.plan!.stops[3]!), 8)).toBe(false);

         expect(record.plan?.stops.map((stop) => stop.id)).toEqual(kept.map((stop) => stop.id));
      });

      it('drops an older plan, and holds a stop that the plan does not name yet until a plan names it', () => {
         const record = loaded(() => ({}), 5);
         const added: Stop = { ...structuredClone(tour.plan!.stops[0]!), id: 'added', title: 'Added' };

         expect(record.putPlan({ ...structuredClone(tour.plan!), stops: [] }, 4)).toBe(false);
         expect(record.putStop(added, 7)).toBe(true);
         expect(record.plan?.stops.map((stop) => stop.id)).not.toContain('added');
         record.putPlan({ ...structuredClone(tour.plan!), stops: [...structuredClone(tour.plan!.stops), structuredClone(added)] }, 6);

         expect(record.plan?.stops.at(-1)?.title).toBe('Added');
      });

      it('shows no plan while the load says the tour has none', () => {
         const record = loaded(() => ({ plan: null }));

         expect(record.plan).toBeNull();
         expect(record.knownPlan).toEqual({ plan: null });
      });
   });

   it('replaces its controls with newer ones from a controlsChanged event, and keeps them over older ones', () => {
      const record = loaded();
      const controls: TourControls = eventFixtures.controlsChanged;
      const older: TourControls = { ...structuredClone(controls), revision: controls.revision - 1 };
      const newer: TourControls = { ...structuredClone(controls), revision: controls.revision + 1 };

      expect(record.putControls(controls)).toBe(true);
      expect(record.putControls(older)).toBe(false);
      expect(record.putControls(structuredClone(controls))).toBe(false);
      expect(record.controls).toBe(controls);
      expect(record.putControls(newer)).toBe(true);
      expect(record.controls).toBe(newer);
   });

   describe('with the waiting work of its controls', () => {
      /** Controls at the revision, whose waiting work names one stop: `s` and the revision, unless `stopId` names another, or none for null. */
      const waiting = (revision: number, stopId: string | null = `s${revision}`): TourControls => {
         const controls = structuredClone(eventFixtures.controlsChanged);
         const stops = stopId === null ? {} : { [stopId]: ['t1'] };
         return { ...controls, revision, offers: { ...controls.offers, pending: { ...controls.offers.pending, stops } } };
      };
      const named = (pending: readonly PendingWork[]) => pending.map((work) => Object.keys(work.stops)[0]);

      it('gives the work that held for stores in a span of revisions, older work that arrived late included', () => {
         const record = loaded(() => ({}), 10);
         for (const revision of [9, 12, 11, 14]) {
            record.putControls(waiting(revision));
         }

         expect(named(record.waitingWithin(10, 15))).toEqual(['s9', 's11', 's12', 's14']);
         expect(named(record.waitingWithin(11, 12))).toEqual(['s11']);
         expect(named(record.waitingWithin(13, 14))).toEqual(['s12']);
      });

      it('drops the work before the last at or before the lowest revision of its stops', () => {
         const record = loaded(() => ({}), 10);
         for (const revision of [8, 9, 12]) {
            record.putControls(waiting(revision));
         }

         expect(named(record.waitingWithin(7, 10))).toEqual(['s9']);
      });

      it('keeps one entry for work that repeats, at its earliest revision', () => {
         const record = loaded(() => ({}), 10);
         record.putControls(waiting(13, 's2'));
         record.putControls(waiting(11, 's2'));
         record.putControls(waiting(12, 's2'));

         expect(record.waitingWithin(10, 20)).toHaveLength(1);
         expect(named(record.waitingWithin(11, 12))).toEqual(['s2']);
      });

      it.each<[string, number[]]>([
         ['a later arrival lands between two equal entries', [5, 9, 7]],
         ['the repeat arrives after the entry between', [5, 7, 9]],
         ['the entry between arrives first', [7, 9, 5]],
      ])('keeps the work of each revision when %s', (_, order) => {
         const record = loaded(() => ({}), 4);
         const work: Record<number, string | null> = { 5: 's1', 7: null, 9: 's1' };
         for (const revision of order) {
            record.putControls(waiting(revision, work[revision]));
         }

         expect(named(record.waitingWithin(7, 11))).toEqual([undefined, 's1']);
         expect(named(record.waitingWithin(4, 7))).toEqual(['s1']);
      });

      it('keeps the work of each revision when the entry between arrives after an earlier repeat moved', () => {
         const record = loaded(() => ({}), 10);
         record.putControls(waiting(13, 's2'));
         record.putControls(waiting(11, 's2'));
         record.putControls(waiting(12, null));

         expect(named(record.waitingWithin(12, 20))).toEqual([undefined, 's2']);
         expect(named(record.waitingWithin(10, 12))).toEqual(['s2']);
      });

      it('keeps the first work it heard of a revision', () => {
         const record = loaded(() => ({}), 4);
         record.putControls(waiting(5, 's1'));
         record.putControls(waiting(5, null));

         expect(named(record.waitingWithin(4, 6))).toEqual(['s1']);
         expect(named(record.waitingWithin(5, 6))).toEqual(['s1']);
      });

      it('keeps the first work it heard of a revision inside a run of one work', () => {
         const record = loaded(() => ({}), 4);
         for (const revision of [5, 6, 7]) {
            record.putControls(waiting(revision, 's1'));
         }
         record.putControls(waiting(6, null));

         expect(named(record.waitingWithin(4, 8))).toEqual(['s1']);
         expect(named(record.waitingWithin(6, 7))).toEqual(['s1']);
      });

      it('starts a run at an arrival of its work just before it', () => {
         const record = loaded(() => ({}), 4);
         record.putControls(waiting(5, null));
         record.putControls(waiting(8, 's1'));
         record.putControls(waiting(7, 's1'));

         expect(named(record.waitingWithin(7, 8))).toEqual(['s1']);
      });

      it('keeps every revision of a run that an arrival joined on both sides', () => {
         const record = loaded(() => ({}), 4);
         for (const revision of [5, 7, 6]) {
            record.putControls(waiting(revision, 's1'));
         }
         record.putControls(waiting(7, null));

         expect(named(record.waitingWithin(6, 8))).toEqual(['s1']);
      });

      it('gives the first work it hears of to a span before it', () => {
         const record = loaded(() => ({}), 4);
         record.putControls(waiting(6, 's1'));

         expect(named(record.waitingWithin(4, 10))).toEqual(['s1']);
      });

      it('keeps the work of each revision above the lowest revision of its stops after it dropped older work', () => {
         const record = loaded(() => ({}), 10);
         for (const revision of [6, 8, 12, 5, 11, 7]) {
            record.putControls(waiting(revision, revision === 11 ? null : `s${revision}`));
         }

         expect(named(record.waitingWithin(10, 13))).toEqual(['s8', undefined, 's12']);
         expect(named(record.waitingWithin(4, 9))).toEqual(['s8']);
      });

      it('drops the work before the last at or before the revision up to which the page heard every store', () => {
         const record = loaded(() => ({}), 4);
         record.heardLoad(10, true);
         for (const revision of [6, 8, 12]) {
            record.putControls(waiting(revision));
         }

         expect(named(record.waitingWithin(4, 20))).toEqual(['s8', 's12']);
      });

      it('keeps only the newest work while it holds no stops', () => {
         const record = new TourRecord(tour.id);
         for (const revision of [3, 5, 4]) {
            record.putControls(waiting(revision));
         }

         expect(named(record.waitingWithin(0, 10))).toEqual(['s5']);
      });
   });

   it('keeps the reading progress of a write response over an older load', () => {
      const record = loaded(() => ({ progress: undefined }), 5);
      record.putProgress({ lastStop: 's2', finished: false }, 9);

      record.applyLoad(load(() => ({ progress: undefined }), 8));

      expect(record.progress).toEqual({ lastStop: 's2', finished: false });
   });

   describe('with parts in its plan', () => {
      // The fixture's plan has the parts p1 and p2, with the first stop in p1 and the last in p2.
      const firstStop = tour.plan!.stops[0]!.id;
      const lastStop = tour.plan!.stops.at(-1)!.id;

      it('keeps the parts of the plan and of each chapter', () => {
         expect(loaded().plan!.parts).toEqual(tour.plan!.parts);
         expect(loaded().plan!.chapters).toEqual(tour.plan!.chapters);
         expect(tour.plan!.parts).toHaveLength(2);
      });

      it('starts with every part unfolded, and folds and unfolds a part', () => {
         const record = loaded();
         expect([...record.foldedParts]).toEqual([]);

         record.setPartFolded('p1', true);
         record.setPartFolded('p2', true);
         record.setPartFolded('p2', false);

         expect([...record.foldedParts]).toEqual(['p1']);
      });

      it('unfolds the part that holds a stop, and only that part', () => {
         const record = loaded();
         record.setPartFolded('p1', true);
         record.setPartFolded('p2', true);

         record.unfoldPartOf(lastStop);

         expect([...record.foldedParts]).toEqual(['p1']);
         record.unfoldPartOf(firstStop);
         expect([...record.foldedParts]).toEqual([]);
      });

      it('keeps the folds of the parts that a replan keeps', () => {
         const record = loaded();
         record.setPartFolded('p1', true);

         record.putPlan({ ...tour.plan!, stops: tour.plan!.stops.slice(0, -1) }, at + 1);

         expect([...record.foldedParts]).toEqual(['p1']);
      });

      it('forgets the fold of a part that a replan drops, so a later plan that brings the part back shows it unfolded', () => {
         const record = loaded();
         record.setPartFolded('p1', true);
         record.setPartFolded('p2', true);
         const plan = tour.plan!;

         record.putPlan({ ...plan, parts: [plan.parts![1]!], chapters: plan.chapters.map((chapter) => ({ ...chapter, part: 'p2' })) }, at + 1);
         expect([...record.foldedParts]).toEqual(['p2']);

         record.putPlan(plan, at + 2);
         expect([...record.foldedParts]).toEqual(['p2']);
      });

      it('forgets every fold when a replan removes the parts', () => {
         const record = loaded();
         record.setPartFolded('p1', true);
         const { chapters, stops, fileMarks } = tour.plan!;

         record.putPlan({ chapters: chapters.map((chapter) => ({ id: chapter.id, title: chapter.title })), stops, fileMarks }, at + 1);

         expect([...record.foldedParts]).toEqual([]);
      });

      it('keeps the folds when an older plan arrives, which the record does not store', () => {
         const record = loaded();
         record.setPartFolded('p1', true);

         record.putPlan({ ...tour.plan!, parts: [] }, at - 1);

         expect([...record.foldedParts]).toEqual(['p1']);
      });

      it('names the part that holds a stop', () => {
         const record = loaded();

         expect(record.partOf(firstStop)).toBe('p1');
         expect(record.partOf(lastStop)).toBe('p2');
         expect(record.partOf('no-such-stop')).toBeUndefined();
      });

      it('changes nothing for a stop that the plan lacks', () => {
         const record = loaded();
         record.setPartFolded('p1', true);

         record.unfoldPartOf('no-such-stop');

         expect([...record.foldedParts]).toEqual(['p1']);
      });
   });

   describe('with done marks', () => {
      const pending = tour.plan!.stops.find((stop) => stop.stage === 'pending')!.id;

      it('shows a mark on the current version as done, and counts it', () => {
         const record = loaded();

         record.putDoneVersion('s2', 2, at + 1);

         expect(record.doneState('s2')).toEqual({ state: 'done' });
         expect(record.doneCount).toBe(1);
      });

      it('shows a mark above the version that it holds as done, because the record has yet to hear of the revision', () => {
         const record = loaded();

         record.putDoneVersion('s2', 3, at + 1);

         expect(record.doneState('s2')).toEqual({ state: 'done' });
      });

      it('shows a mark on an earlier version as done on that version, and does not count it', () => {
         const record = loaded();

         expect(record.doneState('s2')).toEqual({ state: 'earlier', version: 1 });
         expect(record.doneStates.get('s2')).toEqual({ state: 'earlier', version: 1 });
         expect(record.doneCount).toBe(0);
      });

      it('shows a stop without a mark and a pending stop as not done', () => {
         const record = loaded();
         record.putDoneVersion(pending, 1, at + 1);

         expect(record.doneState('s3')).toEqual({ state: 'none' });
         expect(record.doneState(pending)).toEqual({ state: 'none' });
         expect(record.doneStates.has(pending)).toBe(false);
      });

      it("keeps a newer revision's mark over an older mark and an older load", () => {
         const record = loaded();
         record.putDoneVersion('s2', 2, at + 2);

         expect(record.putDoneVersion('s2', 1, at + 1)).toBe(false);
         record.applyLoad(load(() => ({}), at + 1));

         expect(record.doneState('s2')).toEqual({ state: 'done' });
      });

      it('keeps a cleared mark cleared over an older load that still holds it', () => {
         const record = loaded(() => ({}), 8);
         record.putDoneVersion('s2', null, 9);

         record.applyLoad(load(() => ({}), 8));

         expect(record.doneState('s2')).toEqual({ state: 'none' });
      });
   });

   it("keeps every earlier stop version it heard of, each stop's oldest first", () => {
      const record = loaded();
      const versions = record.earlierVersions;

      record.applyLoad(load((copy) => ({ plan: { ...copy.plan!, stops: copy.plan!.stops.map((stop) => (stop.stage === 'written' ? { ...stop, earlier: [] } : stop)) } }), at + 1));

      expect(record.earlierVersions).toEqual(versions);
      expect(versions.length).toBeGreaterThan(0);
   });
});
