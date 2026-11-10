import { describe, expect, it } from 'vitest';
import { changedFilesAt, repositoryNodes, repositoryRows, type DirectoryState } from '../src/lib/repositoryTree';
import type { ChangedFile } from '../src/lib/types';

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
