import { describe, expect, it } from 'vitest';
import { divesByStop, divesOf, firstDives, flatDives, groupFamilies, parentOf, readingStateOf, subtreeOf, type DiveBranch } from '../src/lib/tourFamily';
import { archiveLabel } from '../src/lib/tourList';
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

describe("a dive's parent", () => {
   const tours = [tour('root', 0), tour('dive', 1, { parent: 'root', root: 'root' }), tour('deeper', 2, { parent: 'dive', root: 'root' })];

   it('is the tour that the dive started from', () => {
      expect(parentOf(tours[2]!, tours)?.id).toBe('dive');
      expect(parentOf(tours[1]!, tours)?.id).toBe('root');
   });

   it('is null for a tour that is not a dive', () => {
      expect(parentOf(tours[0]!, tours)).toBeNull();
   });

   it('is the root when the list lacks the parent', () => {
      expect(parentOf(tours[2]!, [tours[0]!, tours[2]!])?.id).toBe('root');
   });

   it('is null when the list lacks the parent and the root', () => {
      expect(parentOf(tours[2]!, [tours[2]!])).toBeNull();
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

// Each branch as its ID, followed by its dives in brackets.
const shape = (branches: readonly DiveBranch[]): string[] => branches.map((branch) => (branch.dives.length === 0 ? branch.tour.id : `${branch.tour.id}[${shape(branch.dives).join(' ')}]`));

describe("a tour's subtree", () => {
   const tours = [
      tour('root', 0),
      tour('later', 3, { parent: 'root', root: 'root', stop: 's2' }),
      tour('whole', 1, { parent: 'root', root: 'root' }),
      tour('deeper', 2, { parent: 'whole', root: 'root' }),
      tour('deepest', 5, { parent: 'deeper', root: 'root' }),
      tour('other', 4),
   ];

   it('holds every dive below the tour, each with its own dives, oldest first', () => {
      expect(shape(subtreeOf('root', tours))).toEqual(['whole[deeper[deepest]]', 'later']);
      expect(flatDives(subtreeOf('root', tours)).map((dive) => dive.id)).toEqual(['whole', 'deeper', 'deepest', 'later']);
   });

   it("holds a dive's own dives alone, and nothing for a tour without dives", () => {
      expect(shape(subtreeOf('whole', tours))).toEqual(['deeper[deepest]']);
      expect(subtreeOf('other', tours)).toEqual([]);
   });

   it('shows each tour of a cycle of parents once, as only a damaged store holds it', () => {
      const cycle = [tour('a', 0, { parent: 'b', root: 'b' }), tour('b', 1, { parent: 'a', root: 'a' }), tour('c', 2, { parent: 'b', root: 'a' })];

      expect(shape(subtreeOf('a', cycle))).toEqual(['b[c]']);
   });

   it('cuts to its first dives in the order that the flat list gives them', () => {
      const subtree = subtreeOf('root', tours);

      expect(shape(firstDives(subtree, 2))).toEqual(['whole[deeper]']);
      expect(shape(firstDives(subtree, 3))).toEqual(['whole[deeper[deepest]]']);
      expect(shape(firstDives(subtree, 10))).toEqual(['whole[deeper[deepest]]', 'later']);
   });
});

describe("the archive button's name", () => {
   const root = tour('root', 0);
   const dive = tour('dive', 1, { parent: 'root', root: 'root' });
   const deeper = tour('deeper', 2, { parent: 'dive', root: 'root' });
   const archived = { state: 'archived' } as const;

   it('names the dives below the tour that the archive takes, without those already archived', () => {
      expect(archiveLabel(root, [root])).toBe('Archive this tour');
      expect(archiveLabel(root, [root, dive])).toBe('Archive this tour and the dive below it');
      expect(archiveLabel(root, [root, dive, deeper])).toBe('Archive this tour and the 2 dives below it');
      expect(archiveLabel(root, [root, { ...dive, ...archived }, deeper])).toBe('Archive this tour and the dive below it');
      expect(archiveLabel(root, [root, { ...dive, ...archived }])).toBe('Archive this tour');
   });

   it('speaks of the dives archived with the tour while any dive below it is archived', () => {
      const archivedRoot = { ...root, ...archived };

      expect(archiveLabel(archivedRoot, [archivedRoot, { ...dive, ...archived }])).toBe('Unarchive this tour and the dives archived with it');
      expect(archiveLabel(archivedRoot, [archivedRoot, dive])).toBe('Unarchive this tour');
      expect(archiveLabel(archivedRoot, [archivedRoot])).toBe('Unarchive this tour');
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
