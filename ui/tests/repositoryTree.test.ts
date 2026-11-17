import { describe, expect, it } from 'vitest';
import { changedFilesAt, repositoryNodes, repositoryRows, stopVisits, type DirectoryState } from '../src/lib/repositoryTree';
import type { ChangedFile, CodeReference, FileSide, Stop, StopPassage } from '../src/lib/types';
import { stopOf } from './stops';

describe('repositoryNodes', () => {
   it('puts folders before files, each in name order without regard to letter case, named by their last segment', () => {
      const nodes = repositoryNodes([
         { path: 'src/b.ts', type: 'file' },
         { path: 'src/Lib', type: 'directory' },
         { path: 'src/A.ts', type: 'file' },
         { path: 'src/app', type: 'directory' },
         { path: 'src/sub', type: 'submodule' },
      ]);

      expect(nodes.map((node) => [node.type, node.id, node.name, node.entry])).toEqual([
         ['folder', 'src/app/', 'app', 'directory'],
         ['folder', 'src/Lib/', 'Lib', 'directory'],
         ['file', 'src/A.ts', 'A.ts', 'file'],
         ['file', 'src/b.ts', 'b.ts', 'file'],
         ['file', 'src/sub', 'sub', 'submodule'],
      ]);
   });
});

describe('changedFilesAt', () => {
   const files: ChangedFile[] = [
      { path: 'added.ts', kind: 'added' },
      { path: 'deleted.ts', kind: 'deleted' },
      { path: 'modified.ts', kind: 'modified' },
      { path: 'new.ts', kind: 'renamed', oldPath: 'old.ts' },
   ];

   it('finds each changed file by its path at the head, without the deleted files', () => {
      expect([...changedFilesAt(files, 'head').keys()]).toEqual(['added.ts', 'modified.ts', 'new.ts']);
   });

   it('finds each changed file by its path at the base, a renamed file by its old path, without the added files', () => {
      const atBase = changedFilesAt(files, 'base');

      expect([...atBase.keys()]).toEqual(['deleted.ts', 'modified.ts', 'old.ts']);
      expect(atBase.get('old.ts')?.path).toBe('new.ts');
   });
});

describe('repositoryRows', () => {
   const loaded = (...paths: [string, 'file' | 'directory'][]): DirectoryState => ({ state: 'loaded', nodes: repositoryNodes(paths.map(([path, type]) => ({ path, type }))) });

   it('shows what an expanded folder holds once it is loaded, and marks which folders are expanded', () => {
      const listings = new Map<string, DirectoryState>([
         ['', loaded(['a', 'directory'], ['b', 'directory'], ['top.ts', 'file'])],
         ['a', loaded(['a/one.ts', 'file'])],
         ['b', { state: 'loading' }],
      ]);

      const rows = repositoryRows(listings, new Set(['a/', 'b/']));

      expect(rows.map((row) => [row.node.name, row.depth, row.parent?.name ?? null, row.expanded])).toEqual([
         ['a', 0, null, true],
         ['one.ts', 1, 'a', false],
         ['b', 0, null, true],
         ['top.ts', 0, null, false],
      ]);
      expect(repositoryRows(listings, new Set()).map((row) => row.node.name)).toEqual(['a', 'b', 'top.ts']);
   });

   it('has no rows before the root is loaded', () => {
      expect(repositoryRows(new Map([['', { state: 'loading' }]]), new Set())).toEqual([]);
   });
});

describe('stopVisits', () => {
   const head = 'h1';
   const passage = (path: string, side: FileSide = 'head', commit = head): StopPassage => ({ side, path, startLine: 1, endLine: 2, role: 'related', commit, text: 'x' });
   const stop = (id: string, ...passages: StopPassage[]): Stop =>
      stopOf({ id, title: id, kind: 'context', chapter: 'c', content: { headline: id, passages, html: { headline: `<p>${id}</p>\n` }, references: [] } });

   it('counts each stop once for each file its passages visit and for each folder that holds such a file, by the node IDs of the repository tree', () => {
      const visits = stopVisits([stop('s1', passage('src/cart/total.ts'), passage('src/cart/total.ts'), passage('README.md')), stop('s2', passage('src/cart/line.ts'))], head);

      expect(Object.fromEntries(visits)).toEqual({ 'src/': 2, 'src/cart/': 2, 'src/cart/total.ts': 1, 'src/cart/line.ts': 1, 'README.md': 1 });
   });

   it('leaves out pending stops and passages at the base or at another commit than the head', () => {
      const pending = stopOf({ id: 'p', title: 'p', kind: 'context', chapter: 'c', content: null });

      expect(stopVisits([pending, stop('s1', passage('base.ts', 'base'), passage('old.ts', 'head', 'h0'))], head).size).toBe(0);
   });

   describe('with ref: links', () => {
      const link = (path: string, side: FileSide = 'head'): CodeReference => ({ side, path, startLine: 3, endLine: 4 });
      const linking = (id: string, references: CodeReference[], passages: StopPassage[] = [], staleOn?: number): Stop =>
         stopOf({ id, title: id, kind: 'context', chapter: 'c', content: { headline: id, passages, html: { headline: `<p>${id}</p>\n` }, references }, ...(staleOn === undefined ? {} : { staleOn }) });

      it('counts a file and its folders that only a link of a stop reaches, once for the stop', () => {
         const visits = stopVisits([linking('s1', [link('src/server/endpoints.ts'), link('src/cart/total.ts')], [passage('src/cart/total.ts')])], head);

         expect(Object.fromEntries(visits)).toEqual({ 'src/': 1, 'src/cart/': 1, 'src/cart/total.ts': 1, 'src/server/': 1, 'src/server/endpoints.ts': 1 });
      });

      it('leaves out a link at the base, which a snapshot does not have', () => {
         expect(stopVisits([linking('s1', [link('base.ts', 'base')])], head).size).toBe(0);
      });

      it("counts a stale stop's link only on a side where one of the stop's passages is at the pin", () => {
         const kept = linking('kept', [link('kept.ts')], [passage('shown.ts')], 1);
         const lost = linking('lost', [link('lost.ts')], [passage('old.ts', 'head', 'h0')], 1);

         expect(Object.fromEntries(stopVisits([kept, lost], head))).toEqual({ 'shown.ts': 1, 'kept.ts': 1 });
      });
   });
});
