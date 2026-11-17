import { describe, expect, it } from 'vitest';
import { breadcrumb, divesByStop, divesOf, groupFamilies, readingStateOf } from '../src/lib/tourFamily';
import type { Plan, Stop, StopKind, TourSummary } from '../src/lib/types';
import { familySummary } from './family';

const tour = familySummary;

// The list's order, newest first.
const newestFirst = (tours: TourSummary[]) => [...tours].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));

const rows = (tours: TourSummary[]) => groupFamilies(newestFirst(tours)).map(({ tour, depth }) => `${tour.id}:${depth}`);

function plan(...stops: [id: string, kind: StopKind][]): Plan {
   return {
      chapters: [{ id: 'c1', title: 'Chapter' }],
      stops: stops.map(([id, kind]): Stop => ({ stage: 'pending', status: 'pending', anchorHint: null, id, title: id, kind, chapter: 'c1' }) as Stop),
      fileMarks: [],
   };
}

describe('the family grouping', () => {
   it('lists each family under its root, one level deep and oldest first, with the roots in the list order', () => {
      const tours = [
         tour('root', 0),
         tour('dive', 1, { parent: 'root', root: 'root' }),
         tour('other', 2),
         tour('deeper', 3, { parent: 'dive', root: 'root' }),
         tour('second', 4, { parent: 'root', root: 'root' }),
      ];

      expect(rows(tours)).toEqual(['other:0', 'root:0', 'dive:1', 'deeper:1', 'second:1']);
   });

   it('keeps a dive whose root the list hides at its own place', () => {
      const tours = [tour('dive', 1, { parent: 'root', root: 'root' }), tour('other', 2), tour('deeper', 3, { parent: 'dive', root: 'root' })];

      expect(rows(tours)).toEqual(['deeper:0', 'other:0', 'dive:0']);
   });

   it('keeps a tour that names itself as its root at its own place', () => {
      expect(rows([tour('self', 1, { parent: 'self', root: 'self' }), tour('other', 2)])).toEqual(['other:0', 'self:0']);
   });

   it('keeps every tour of a cycle of roots at its own place', () => {
      const tours = [tour('a', 1, { parent: 'b', root: 'b' }), tour('b', 2, { parent: 'a', root: 'a' }), tour('c', 3, { parent: 'a', root: 'a' })];

      expect(rows(tours)).toEqual(['c:0', 'b:0', 'a:0']);
   });
});

describe('the breadcrumb', () => {
   const tours = [tour('root', 0), tour('dive', 1, { parent: 'root', root: 'root' }), tour('deeper', 2, { parent: 'dive', root: 'root' })];

   it('runs from the root down to the parent', () => {
      expect(breadcrumb(tours[2]!, tours).map((step) => step.id)).toEqual(['root', 'dive']);
      expect(breadcrumb(tours[1]!, tours).map((step) => step.id)).toEqual(['root']);
   });

   it('is empty for a tour that is not a dive', () => {
      expect(breadcrumb(tours[0]!, tours)).toEqual([]);
   });

   it('keeps the root first when the list lacks a tour between', () => {
      expect(breadcrumb(tours[2]!, [tours[0]!, tours[2]!]).map((step) => step.id)).toEqual(['root']);
   });
});

describe("a parent's dives", () => {
   const tours = [
      tour('root', 0),
      tour('later', 3, { parent: 'root', root: 'root', stop: 's2' }),
      tour('whole', 1, { parent: 'root', root: 'root' }),
      tour('deeper', 2, { parent: 'whole', root: 'root' }),
      tour('gone', 4, { parent: 'root', root: 'root', stop: 's9' }),
   ];

   it('are the direct dives, oldest first', () => {
      expect(divesOf('root', tours).map((dive) => dive.id)).toEqual(['whole', 'later', 'gone']);
   });

   it('show on their stop, and a whole-tour dive or a dive from a stop the plan lacks on the first overview', () => {
      const byStop = divesByStop(divesOf('root', tours), plan(['s1', 'context'], ['s2', 'inspect'], ['s3', 'overview']));

      expect(Object.fromEntries([...byStop].map(([stopId, dives]) => [stopId, dives.map((dive) => dive.id)]))).toEqual({ s3: ['whole', 'gone'], s2: ['later'] });
   });

   it('show on the first stop when no stop is an overview, and nowhere without a plan', () => {
      expect([...divesByStop(divesOf('root', tours), plan(['s1', 'context'], ['s2', 'inspect'])).keys()]).toEqual(['s1', 's2']);
      expect(divesByStop(divesOf('root', tours), null).size).toBe(0);
   });
});

describe('the reading state', () => {
   it('is not started until the reviewer opens the tour, whatever Claude writes', () => {
      expect(['generating', 'ready', 'inProgress', 'finished', 'archived'].map((state) => readingStateOf(state as TourSummary['state']))).toEqual([
         'notStarted',
         'notStarted',
         'inProgress',
         'read',
         'archived',
      ]);
   });
});
