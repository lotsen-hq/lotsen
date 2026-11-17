import { describe, expect, it } from 'vitest';
import { moveBranches, moveOfferOf, tickDive, tickedDives } from '../src/lib/tourMoves';
import type { MoveDive } from '../src/lib/types';

const pin = { state: 'snapshot', head: 'a'.repeat(40), remoteUrl: 'https://code.example/repo.git' } as const;
const dive = (tourId: string, parentId: string): MoveDive => ({ tourId, parentId, title: `Dive ${tourId}`, state: 'ready', pin });

// The checked tour t, with d1 and d3 from it, d2 from d1, and d4 from a dive of t at the latest commit, which the check does not list. Each tour comes before its dives, as the check lists them.
const dives = [dive('d1', 't'), dive('d2', 'd1'), dive('d3', 't'), dive('d4', 'atLatest')];

describe('the dives that a move offers', () => {
   it('nest under the offered dive that each dives from, and a dive from a tour that the check does not list starts a branch', () => {
      const branches = moveBranches(dives);

      expect(branches.map((branch) => [branch.dive.tourId, branch.dives.map((child) => child.dive.tourId)])).toEqual([
         ['d1', ['d2']],
         ['d3', []],
         ['d4', []],
      ]);
   });

   it('all move while the reviewer unticks none', () => {
      expect(tickedDives(dives, new Set())).toEqual(['d1', 'd2', 'd3', 'd4']);
   });

   it('leave out the offered dives below an unticked dive', () => {
      expect(tickedDives(dives, new Set(['d1']))).toEqual(['d3', 'd4']);
   });

   it('tick the offered dives above a dive that the reviewer ticks, and keep the other unticked dives', () => {
      const unticked = tickDive(dives, new Set(['d1', 'd2', 'd3']), 'd2');

      expect([...unticked]).toEqual(['d3']);
      expect(tickedDives(dives, unticked)).toEqual(['d1', 'd2', 'd4']);
   });
});

describe('what a check tells of the ref', () => {
   it('is movable for a branch and fixed for a tag or a commit ID', () => {
      const checked = { tourId: 't', current: 'a'.repeat(40), latest: 'b'.repeat(40) };

      expect(moveOfferOf({ ...checked, move: 'available' })).toBe('movable');
      expect(moveOfferOf({ ...checked, move: 'atLatest' })).toBe('movable');
      expect(moveOfferOf({ ...checked, move: 'tag' })).toBe('fixed');
      expect(moveOfferOf({ ...checked, move: 'commit' })).toBe('fixed');
   });
});
