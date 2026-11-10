import { describe, expect, it } from 'vitest';
import { atPin, changedFilePaths, inFile, pinCommits, pointsInto, referenceAtPin } from '../src/lib/pin';
import type { CodeReference, FileSide, StopPassage, WrittenStop } from '../src/lib/types';

function stopPassage(side: FileSide, path: string, startLine: number, endLine: number, role: StopPassage['role'] = 'related', label?: string): StopPassage {
   return { side, path, startLine, endLine, role, commit: 'c'.repeat(40), text: 'text', ...(label === undefined ? {} : { label }) };
}

function reference(side: FileSide, path: string, startLine: number, endLine: number): CodeReference {
   return { side, path, startLine, endLine };
}

describe('changedFilePaths', () => {
   it.each([
      [{ path: 'a.ts', kind: 'modified' } as const, { base: 'a.ts', head: 'a.ts' }],
      [{ path: 'a.ts', kind: 'added' } as const, { base: null, head: 'a.ts' }],
      [{ path: 'a.ts', kind: 'deleted' } as const, { base: 'a.ts', head: null }],
      [{ path: 'b.ts', kind: 'renamed', oldPath: 'a.ts' } as const, { base: 'a.ts', head: 'b.ts' }],
   ])('finds the paths of a changed file at each side, %o', (file, paths) => {
      expect(changedFilePaths(file)).toEqual(paths);
   });
});

describe('inFile', () => {
   const renamed = { base: 'src/old.ts', head: 'src/new.ts' };

   it.each([
      ['a head reference to the head path', reference('head', 'src/new.ts', 1, 1)],
      ['a base reference to the old path', reference('base', 'src/old.ts', 1, 1)],
   ])('holds for %s of a renamed file', (_, link) => {
      expect(inFile(link, renamed)).toBe(true);
   });

   it.each([
      ['the head path at the base', reference('base', 'src/new.ts', 1, 1)],
      ['the old path at the head', reference('head', 'src/old.ts', 1, 1)],
      ['another path', reference('head', 'src/other.ts', 1, 1)],
   ])('fails for a reference to %s', (_, link) => {
      expect(inFile(link, renamed)).toBe(false);
   });

   it('fails at a side where the file does not exist, and while no file is open', () => {
      expect(inFile(reference('base', 'src/new.ts', 1, 1), { base: null, head: 'src/new.ts' })).toBe(false);
      expect(inFile(reference('head', 'src/new.ts', 1, 1), null)).toBe(false);
   });
});

describe('pointsInto', () => {
   const into = stopPassage('head', 'src/pay.ts', 10, 14);

   it.each([
      ['inside it', 11, 12],
      ['around it', 1, 30],
      ['over its first line', 5, 10],
      ['over its last line', 14, 20],
      ['on the same lines', 10, 14],
   ])('holds for a link at the passage’s side and path whose lines are %s', (_, startLine, endLine) => {
      expect(pointsInto(reference('head', 'src/pay.ts', startLine, endLine), into)).toBe(true);
   });

   it.each([
      ['lines before it', reference('head', 'src/pay.ts', 1, 9)],
      ['lines after it', reference('head', 'src/pay.ts', 15, 15)],
      ['the other side', reference('base', 'src/pay.ts', 10, 14)],
      ['another path', reference('head', 'src/other.ts', 10, 14)],
   ])('fails for a link to %s', (_, link) => {
      expect(pointsInto(link, into)).toBe(false);
   });

   it('compares a renamed file’s base link and base passage by the old path', () => {
      const before = stopPassage('base', 'src/old.ts', 3, 4);

      expect(pointsInto(reference('base', 'src/old.ts', 4, 6), before)).toBe(true);
      expect(pointsInto(reference('head', 'src/new.ts', 4, 6), before)).toBe(false);
   });
});

describe('the lines at the pin', () => {
   const commits = pinCommits({ state: 'pinned', mergeBase: 'a'.repeat(40), head: 'b'.repeat(40), iteration: 2, remoteUrl: 'https://code.example/repo.git' });

   it('are lines read at the commit of their side', () => {
      expect(atPin({ side: 'head', commit: 'b'.repeat(40) }, commits)).toBe(true);
      expect(atPin({ side: 'base', commit: 'a'.repeat(40) }, commits)).toBe(true);
   });

   it('leave out lines read at another commit, or at the commit of the other side', () => {
      expect(atPin({ side: 'head', commit: 'c'.repeat(40) }, commits)).toBe(false);
      expect(atPin({ side: 'head', commit: 'a'.repeat(40) }, commits)).toBe(false);
   });

   it('take every commit while the tour is not pinned', () => {
      expect(pinCommits({ state: 'pinning' })).toBeNull();
      expect(atPin({ side: 'head', commit: 'c'.repeat(40) }, null)).toBe(true);
   });
});

describe('a ref: link at the pin', () => {
   const commits = { base: 'a'.repeat(40), head: 'b'.repeat(40) };
   const head: CodeReference = { side: 'head', path: 'a.ts', startLine: 1, endLine: 1 };
   const base: CodeReference = { ...head, side: 'base' };
   const passage = (side: FileSide, commit: string): StopPassage => ({ side, path: 'a.ts', startLine: 1, endLine: 1, role: 'primary', commit, text: '' });
   const stop = (passages: StopPassage[], staleOn?: number): WrittenStop => ({
      id: 's1',
      title: 'T',
      kind: 'inspect',
      chapter: 'c',
      stage: 'written',
      status: 'ready',
      version: 1,
      earlier: [],
      content: { headline: 'T.', passages, html: { headline: '<p>T.</p>\n' } },
      ...(staleOn === undefined ? {} : { staleOn }),
   });
   const sides = (atPin: (reference: CodeReference) => boolean) => [atPin(base), atPin(head)];

   it.each([
      ['the current version of a fresh stop', stop([passage('head', 'b'.repeat(40))]), true, [true, true]],
      ['the current version of a fresh stop without passages', stop([]), true, [true, true]],
      ['a stale stop without passages', stop([], 2), true, [false, false]],
      ['a stale stop with a passage at an earlier commit', stop([passage('head', 'c'.repeat(40))], 2), true, [false, false]],
      ['a stale stop with a base passage at the pin', stop([passage('base', 'a'.repeat(40))], 2), true, [true, false]],
      ['an earlier version with a head passage at an earlier commit and a base passage at the pin', stop([passage('head', 'c'.repeat(40)), passage('base', 'a'.repeat(40))]), false, [true, false]],
      ['an earlier version with every passage at the pin at the head', stop([passage('head', 'b'.repeat(40))]), false, [false, true]],
      ['an earlier version without passages', stop([]), false, [false, false]],
   ])('in %s is at the pin at the base and the head as given', (_, shown, current, expected) => {
      expect(sides(referenceAtPin(shown, current, commits))).toEqual(expected);
   });

   it('is at the pin in a stale stop or an earlier version while the tour is not pinned', () => {
      expect(sides(referenceAtPin(stop([], 2), true, null))).toEqual([true, true]);
      expect(sides(referenceAtPin(stop([]), false, null))).toEqual([true, true]);
   });
});
