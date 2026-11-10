import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import FileTree from '../src/components/FileTree.svelte';
import { buildFileList, buildFileTree, compareRepositoryPaths, fileTreeKeyAction, groupByStop, nodesOutside, visibleRows, type FileTreeNode, type TreeNode } from '../src/lib/fileTree';
import type { ChangedFile, CodeReference, FileMark } from '../src/lib/types';
import { currentRows, rowParts } from './treeRows';

const modified = (path: string): ChangedFile => ({ path, kind: 'modified' });

// Each node as "name" for a file with its kind, or [name, children] for a folder.
type Outline = string | [string, Outline[]];
function outline(nodes: FileTreeNode[]): Outline[] {
   return nodes.map((node) => (node.type === 'file' ? `${node.name} (${node.kind})` : [node.name, outline(node.children)]));
}

describe('buildFileTree', () => {
   it('nests files in their folders, folders before files, each level in name order, with each change kind', () => {
      const tree = buildFileTree([
         { path: 'README.md', kind: 'modified' },
         { path: 'src/app/main.ts', kind: 'added' },
         { path: 'src/app/legacy.ts', kind: 'deleted' },
         { path: 'src/index.ts', kind: 'modified' },
         { path: 'docs/guide.md', kind: 'added' },
         { path: 'docs/api/rest.md', kind: 'modified' },
      ]);

      expect(outline(tree)).toEqual([
         ['docs', [['api', ['rest.md (modified)']], 'guide.md (added)']],
         ['src', [['app', ['legacy.ts (deleted)', 'main.ts (added)']], 'index.ts (modified)']],
         'README.md (modified)',
      ]);
   });

   it('gives each node its repository path', () => {
      const [docs] = buildFileTree([modified('docs/a.md'), modified('docs/b.md')]);

      expect(docs).toMatchObject({ type: 'folder', path: 'docs' });
      expect(docs?.type === 'folder' && docs.children.map((child) => child.path)).toEqual(['docs/a.md', 'docs/b.md']);
   });

   it('sorts names without regard to letter case', () => {
      const tree = buildFileTree([modified('b.txt'), modified('Z.txt'), modified('a.txt'), modified('C.txt')]);

      expect(outline(tree)).toEqual(['a.txt (modified)', 'b.txt (modified)', 'C.txt (modified)', 'Z.txt (modified)']);
   });

   it('collapses a chain of folders that each hold one folder into one row, at the root and further down', () => {
      const tree = buildFileTree([
         modified('src/Lotsen/Api/Routes.cs'),
         modified('src/Lotsen/Api/Errors.cs'),
         modified('tests/Lotsen.Tests/Git/Fixtures/one.txt'),
         modified('tests/Lotsen.Tests/Program.cs'),
      ]);

      expect(outline(tree)).toEqual([
         ['src/Lotsen/Api', ['Errors.cs (modified)', 'Routes.cs (modified)']],
         ['tests/Lotsen.Tests', [['Git/Fixtures', ['one.txt (modified)']], 'Program.cs (modified)']],
      ]);
      expect(tree[0]).toMatchObject({ path: 'src/Lotsen/Api' });
   });

   it('keeps a folder whose only entry is a file', () => {
      expect(outline(buildFileTree([modified('docs/guide.md')]))).toEqual([['docs', ['guide.md (modified)']]]);
   });

   it('keeps a deleted file apart from an added folder of the same path', () => {
      const tree = buildFileTree([
         { path: 'config', kind: 'deleted' },
         { path: 'config/app.json', kind: 'added' },
      ]);

      expect(outline(tree)).toEqual([['config', ['app.json (added)']], 'config (deleted)']);
      expect(new Set(tree.map((node) => node.id)).size).toBe(2);
   });

   it('puts a renamed file at its new path and keeps its old path', () => {
      const tree = buildFileTree([{ path: 'lib/new.ts', kind: 'renamed', oldPath: 'src/old.ts' }]);

      expect(outline(tree)).toEqual([['lib', ['new.ts (renamed)']]]);
      expect(tree[0]?.type === 'folder' && tree[0].children[0]).toMatchObject({ id: 'lib/new.ts', path: 'lib/new.ts', oldPath: 'src/old.ts' });
   });

   it('keeps the mark of a file whose only change is its line endings', () => {
      const tree = buildFileTree([{ path: 'crlf.ts', kind: 'modified', lineEndingsOnly: true }, modified('other.ts')]);

      expect(tree).toMatchObject([
         { path: 'crlf.ts', lineEndingsOnly: true },
         { path: 'other.ts', lineEndingsOnly: false },
      ]);
   });

   it('gives each file the mark on its path, and a renamed file the mark on its path at the head', () => {
      const generated: FileMark = { path: 'lib/new.g.ts', mark: 'generated', reason: 'A tool writes it.' };
      const atOldPath: FileMark = { path: 'src/old.ts', mark: 'mechanical', reason: 'Only moved.' };
      const tree = buildFileTree(
         [{ path: 'lib/new.g.ts', kind: 'renamed', oldPath: 'src/old.ts' }, modified('other.ts')],
         new Map([
            [generated.path, generated],
            [atOldPath.path, atOldPath],
         ]),
      );

      expect(tree).toMatchObject([
         { type: 'folder', children: [{ path: 'lib/new.g.ts', mark: generated }] },
         { path: 'other.ts', mark: null },
      ]);
   });

   it('builds nothing from no files', () => {
      expect(buildFileTree([])).toEqual([]);
   });
});

describe('compareRepositoryPaths', () => {
   const sorted = (paths: string[]) => [...paths].sort(compareRepositoryPaths);

   it("compares segment by segment, so a folder's files stay together ahead of a name that extends the folder's", () => {
      expect(sorted(['a.txt', 'a-b/c.ts', 'a/z.ts', 'ab.ts', 'a/b/c.ts'])).toEqual(['a/b/c.ts', 'a/z.ts', 'a-b/c.ts', 'a.txt', 'ab.ts']);
   });

   it('sorts without regard to letter case, with lowercase first between names that differ only in case', () => {
      expect(sorted(['src/B.ts', 'README.md', 'src/a.ts', 'docs/x.md', 'readme.md'])).toEqual(['docs/x.md', 'readme.md', 'README.md', 'src/a.ts', 'src/B.ts']);
   });

   it('puts a path before the paths inside it', () => {
      expect(sorted(['config/app.json', 'config'])).toEqual(['config', 'config/app.json']);
   });
});

describe('buildFileList', () => {
   it('lists every file by its full path, in path order, with each change kind', () => {
      const list = buildFileList([
         { path: 'src/index.ts', kind: 'modified' },
         { path: 'README.md', kind: 'modified' },
         { path: 'src/app/main.ts', kind: 'added' },
         { path: 'docs/guide.md', kind: 'deleted' },
      ]);

      expect(outline(list)).toEqual(['docs/guide.md (deleted)', 'README.md (modified)', 'src/app/main.ts (added)', 'src/index.ts (modified)']);
      expect(list.map((node) => node.path)).toEqual(['docs/guide.md', 'README.md', 'src/app/main.ts', 'src/index.ts']);
   });

   it("keeps the marks, the line-endings flag and a renamed file's old path", () => {
      const mark: FileMark = { path: 'lib/new.g.ts', mark: 'generated', reason: 'A tool writes it.' };
      const list = buildFileList(
         [
            { path: 'lib/new.g.ts', kind: 'renamed', oldPath: 'src/old.ts' },
            { path: 'crlf.ts', kind: 'modified', lineEndingsOnly: true },
         ],
         new Map([[mark.path, mark]]),
      );

      expect(list).toMatchObject([
         { id: 'crlf.ts', lineEndingsOnly: true, mark: null, oldPath: null },
         { id: 'lib/new.g.ts', lineEndingsOnly: false, mark, oldPath: 'src/old.ts' },
      ]);
   });
});

describe('groupByStop', () => {
   const files: ChangedFile[] = [
      modified('src/cart.ts'),
      modified('src/pay.ts'),
      { path: 'lib/total.ts', kind: 'renamed', oldPath: 'src/total.ts' },
      { path: 'src/gone.ts', kind: 'deleted' },
      { path: 'src/new.ts', kind: 'added' },
   ];
   const at = (side: CodeReference['side'], path: string): CodeReference => ({ side, path, startLine: 1, endLine: 2 });
   const paths = (group: readonly ChangedFile[]) => group.map((file) => file.path);

   it("puts the files of the stop's passages in the stop's group and the rest after, each in list order", () => {
      const groups = groupByStop(files, [at('head', 'src/pay.ts'), at('head', 'src/new.ts'), at('head', 'src/pay.ts')]);

      expect(groups && [paths(groups.stop), paths(groups.rest)]).toEqual([
         ['src/pay.ts', 'src/new.ts'],
         ['src/cart.ts', 'lib/total.ts', 'src/gone.ts'],
      ]);
   });

   it('finds a renamed file by its old path at the base, and a deleted file at the base', () => {
      const groups = groupByStop(files, [at('base', 'src/total.ts'), at('base', 'src/gone.ts')]);

      expect(groups && paths(groups.stop)).toEqual(['lib/total.ts', 'src/gone.ts']);
   });

   it('does not find a renamed file by its new path at the base, or by its old path at the head', () => {
      expect(groupByStop(files, [at('base', 'lib/total.ts'), at('head', 'src/total.ts')])).toBeNull();
   });

   it('leaves out a passage in a file that the pull request does not change', () => {
      const groups = groupByStop(files, [at('head', 'src/catalog.ts'), at('head', 'src/cart.ts')]);

      expect(groups && paths(groups.stop)).toEqual(['src/cart.ts']);
   });

   it('does not group for a stop without passages, or with passages only in unchanged files', () => {
      expect(groupByStop(files, [])).toBeNull();
      expect(groupByStop(files, [at('head', 'src/catalog.ts')])).toBeNull();
   });

   it('leaves the rest empty when the stop points at every file', () => {
      const groups = groupByStop([modified('a.ts')], [at('head', 'a.ts')]);

      expect(groups && [paths(groups.stop), paths(groups.rest)]).toEqual([['a.ts'], []]);
   });
});

describe('visibleRows', () => {
   const tree = buildFileTree([modified('a/one.ts'), modified('a/b/two.ts'), modified('three.ts')]);
   const [a] = tree;
   const b = a?.type === 'folder' ? a.children[0] : undefined;

   it('lists every node in display order with its depth and parent when nothing is collapsed', () => {
      const rows = visibleRows(tree, new Set());

      expect(rows.map((row) => [row.node.name, row.depth, row.parent?.name ?? null, row.expanded])).toEqual([
         ['a', 0, null, true],
         ['b', 1, 'a', true],
         ['two.ts', 2, 'b', false],
         ['one.ts', 1, 'a', false],
         ['three.ts', 0, null, false],
      ]);
   });

   it('leaves out what a collapsed folder holds', () => {
      const rows = visibleRows(tree, new Set([b!.id]));

      expect(rows.map((row) => row.node.name)).toEqual(['a', 'b', 'one.ts', 'three.ts']);
      expect(visibleRows(tree, new Set([a!.id])).map((row) => row.node.name)).toEqual(['a', 'three.ts']);
   });
});

describe('fileTreeKeyAction', () => {
   const tree = buildFileTree([modified('a/one.ts'), modified('a/two.ts'), modified('three.ts')]);
   const a = tree[0]!;
   const rows = visibleRows(tree, new Set());
   const id = (name: string) => rows.find((row) => row.node.name === name)!.node.id;
   const act = (name: string, key: string, collapsed: ReadonlySet<string> = new Set()) => fileTreeKeyAction(visibleRows(tree, collapsed), id(name), key);

   it('moves to the next and the previous row, and stays put at either end', () => {
      expect(act('a', 'ArrowDown')).toEqual({ type: 'focus', id: id('one.ts') });
      expect(act('three.ts', 'ArrowUp')).toEqual({ type: 'focus', id: id('two.ts') });
      expect(act('three.ts', 'ArrowDown')).toEqual({ type: 'focus', id: id('three.ts') });
      expect(act('a', 'ArrowUp')).toEqual({ type: 'focus', id: id('a') });
   });

   it('skips what a collapsed folder holds', () => {
      expect(act('a', 'ArrowDown', new Set([a.id]))).toEqual({ type: 'focus', id: id('three.ts') });
   });

   it('moves to the first and the last row on Home and End', () => {
      expect(act('two.ts', 'Home')).toEqual({ type: 'focus', id: id('a') });
      expect(act('one.ts', 'End')).toEqual({ type: 'focus', id: id('three.ts') });
   });

   it('expands a collapsed folder on the right arrow, and moves into an expanded one', () => {
      expect(act('a', 'ArrowRight', new Set([a.id]))).toEqual({ type: 'expand', id: a.id });
      expect(act('a', 'ArrowRight')).toEqual({ type: 'focus', id: id('one.ts') });
      expect(act('one.ts', 'ArrowRight')).toEqual({ type: 'focus', id: id('one.ts') });
   });

   it('collapses an expanded folder on the left arrow, and otherwise moves to the parent folder', () => {
      expect(act('a', 'ArrowLeft')).toEqual({ type: 'collapse', id: a.id });
      expect(act('two.ts', 'ArrowLeft')).toEqual({ type: 'focus', id: a.id });
      expect(act('a', 'ArrowLeft', new Set([a.id]))).toEqual({ type: 'focus', id: a.id });
      expect(act('three.ts', 'ArrowLeft')).toEqual({ type: 'focus', id: id('three.ts') });
   });

   it('collapses an expanded folder that shows no rows on the left arrow', () => {
      const empty: TreeNode = { type: 'folder', id: 'empty/' };
      const file: TreeNode = { type: 'file', id: 'file.ts' };
      const shown = [
         { node: empty, depth: 0, parent: null, expanded: true },
         { node: file, depth: 0, parent: null, expanded: false },
      ];

      expect(fileTreeKeyAction(shown, empty.id, 'ArrowLeft')).toEqual({ type: 'collapse', id: empty.id });
      expect(fileTreeKeyAction(shown, empty.id, 'ArrowRight')).toEqual({ type: 'focus', id: empty.id });
   });

   it('activates the row on Enter and Space', () => {
      expect(act('one.ts', 'Enter')).toEqual({ type: 'activate', id: id('one.ts') });
      expect(act('a', ' ')).toEqual({ type: 'activate', id: a.id });
   });

   it('checks a file on Space in a tree whose files are checkable, and still activates a folder', () => {
      const checkable = (name: string, key: string) => fileTreeKeyAction(rows, id(name), key, { checkable: true });

      expect(checkable('one.ts', ' ')).toEqual({ type: 'check', id: id('one.ts') });
      expect(checkable('one.ts', 'Enter')).toEqual({ type: 'activate', id: id('one.ts') });
      expect(checkable('a', ' ')).toEqual({ type: 'activate', id: a.id });
      expect(act('one.ts', ' ')).toEqual({ type: 'activate', id: id('one.ts') });
   });

   it('ignores other keys and a row that is not shown', () => {
      expect(act('one.ts', 'x')).toBeNull();
      expect(fileTreeKeyAction(rows, 'missing', 'ArrowDown')).toBeNull();
   });
});

describe('FileTree', () => {
   const nodes = buildFileTree([
      { path: 'src/app/main.ts', kind: 'modified' },
      { path: 'src/app/util.ts', kind: 'added' },
      { path: 'src/index.ts', kind: 'modified' },
      { path: 'README.md', kind: 'deleted' },
   ]);

   function renderTree(selectedPath: string | null = null) {
      const onselect = vi.fn();
      render(FileTree, { props: { nodes: nodes, selectedPath, onselect, showReasons: true } });
      return onselect;
   }

   const item = (name: string) => screen.getByRole('treeitem', { name });
   const focused = () => document.activeElement?.getAttribute('aria-label');

   async function press(key: string) {
      await fireEvent.keyDown(document.activeElement!, { key });
   }

   it('has one tab stop, on the first row when no file is selected', () => {
      renderTree();

      const stops = screen.getAllByRole('treeitem').filter((row) => row.tabIndex === 0);
      expect(stops.map((row) => row.getAttribute('aria-label'))).toEqual(['src']);
   });

   it('puts the tab stop on the selected file and marks it selected', () => {
      renderTree('src/index.ts');

      expect(item('index.ts, Modified').tabIndex).toBe(0);
      expect(item('index.ts, Modified').getAttribute('aria-selected')).toBe('true');
      expect(item('src').tabIndex).toBe(-1);
   });

   it('moves the focus down and up through the shown rows, and to either end on Home and End', async () => {
      renderTree();
      item('src').focus();

      await press('ArrowDown');
      expect(focused()).toBe('app');
      await press('ArrowDown');
      expect(focused()).toBe('main.ts, Modified');
      await press('ArrowUp');
      expect(focused()).toBe('app');
      expect(item('app').tabIndex).toBe(0);
      await press('End');
      expect(focused()).toBe('README.md, Deleted');
      await press('Home');
      expect(focused()).toBe('src');
   });

   it('collapses a folder on the left arrow, skips its rows, and expands it again on the right arrow', async () => {
      renderTree();
      item('app').focus();

      await press('ArrowLeft');
      expect(item('app').getAttribute('aria-expanded')).toBe('false');
      expect(screen.queryByRole('treeitem', { name: 'main.ts, Modified' })).toBeNull();
      await press('ArrowDown');
      expect(focused()).toBe('index.ts, Modified');

      await press('ArrowUp');
      await press('ArrowRight');
      expect(item('app').getAttribute('aria-expanded')).toBe('true');
      expect(focused()).toBe('app');
      await press('ArrowRight');
      expect(focused()).toBe('main.ts, Modified');
   });

   it('moves from a row to its folder on the left arrow', async () => {
      renderTree();
      item('util.ts, Added').focus();

      await press('ArrowLeft');

      expect(focused()).toBe('app');
   });

   it('selects a file on Enter and on Space, and toggles a folder', async () => {
      const onselect = renderTree();
      item('util.ts, Added').focus();

      await press('Enter');
      expect(onselect).toHaveBeenLastCalledWith('src/app/util.ts');
      await press('ArrowDown');
      await press(' ');
      expect(onselect).toHaveBeenLastCalledWith('src/index.ts');

      item('app').focus();
      await press('Enter');
      expect(item('app').getAttribute('aria-expanded')).toBe('false');
      expect(onselect).toHaveBeenCalledTimes(2);
   });

   it('toggles a folder and selects a file on a click', async () => {
      const onselect = renderTree();

      await fireEvent.click(screen.getByText('main.ts'));
      expect(onselect).toHaveBeenCalledWith('src/app/main.ts');
      expect(focused()).toBe('main.ts, Modified');

      await fireEvent.click(screen.getByText('app'));
      expect(item('app').getAttribute('aria-expanded')).toBe('false');
   });

   it('moves the focus to the folder when a click collapses the folder around the focused row', async () => {
      renderTree();
      item('main.ts, Modified').focus();

      await fireEvent.click(screen.getByText('src'));

      expect(item('src').tabIndex).toBe(0);
   });

   it('shows a renamed file at its new path with its old path, and selects it by its new path', async () => {
      const onselect = vi.fn();
      render(FileTree, { props: { nodes: buildFileTree([{ path: 'lib/new.ts', kind: 'renamed', oldPath: 'src/old.ts' }]), selectedPath: null, onselect, showReasons: true } });

      const row = item('new.ts, Renamed from src/old.ts');
      expect(row.textContent).toMatch(/src\/old\.ts\s*→\s*new\.ts/);
      await fireEvent.click(screen.getByText('new.ts'));
      expect(onselect).toHaveBeenCalledWith('lib/new.ts');
   });

   it('tags a file whose only change is its line endings', () => {
      render(FileTree, { props: { nodes: buildFileTree([{ path: 'crlf.ts', kind: 'modified', lineEndingsOnly: true }]), selectedPath: null, onselect: vi.fn(), showReasons: true } });

      expect(item('crlf.ts, Modified, line endings only').textContent).toContain('line endings only');
   });

   it('tags a marked file with its mark and shows its reason', () => {
      const mark: FileMark = { path: 'client.g.ts', mark: 'generated', reason: 'A tool writes it.' };
      render(FileTree, { props: { nodes: buildFileTree([modified('client.g.ts')], new Map([[mark.path, mark]])), selectedPath: null, onselect: vi.fn(), showReasons: true } });

      const row = item('client.g.ts, Modified, generated: A tool writes it.');
      expect(row.textContent).toContain('generated');
      expect(row.textContent).toContain('A tool writes it.');
   });

   const parts = (name: string) => rowParts(item(name));

   it('puts a file’s tags before its name, its reason under the name and its change marker after it', () => {
      const mark: FileMark = { path: 'src/client.g.ts', mark: 'generated', reason: 'A tool writes it.' };
      const files: ChangedFile[] = [
         { path: 'src/client.g.ts', kind: 'modified', lineEndingsOnly: true },
         { path: 'lib/new.ts', kind: 'renamed', oldPath: 'src/old.ts' },
      ];
      render(FileTree, { props: { nodes: buildFileList(files, new Map([[mark.path, mark]])), selectedPath: null, onselect: vi.fn(), showReasons: true } });

      expect(parts('src/client.g.ts, Modified, line endings only, generated: A tool writes it.')).toEqual([
         'seti typescript',
         'line endings only',
         'generated',
         'src/client.g.ts',
         'A tool writes it.',
         'M',
      ]);
      expect(parts('lib/new.ts, Renamed from src/old.ts')).toEqual(['seti typescript', 'src/old.ts → lib/new.ts', 'R']);
   });

   // Each part of a row's path with the direction it truncates from: 'rtl' loses its start.
   const pathParts = (name: string) => [...item(name).querySelectorAll('.old-path, .directory, .name')].map((part) => `${part.textContent} ${part.getAttribute('dir') ?? 'ltr'}`);

   it('splits a flat path into its directory and its file name, each losing its start, and leaves a tree name whole', () => {
      const files: ChangedFile[] = [modified('src/app/main.ts'), modified('README.md'), { path: 'lib/new.ts', kind: 'renamed', oldPath: 'src/old.ts' }];
      const flat = render(FileTree, { props: { nodes: buildFileList(files), selectedPath: null, onselect: vi.fn(), showReasons: true } });

      expect(pathParts('src/app/main.ts, Modified')).toEqual(['src/app/ rtl', 'main.ts rtl']);
      expect(pathParts('README.md, Modified')).toEqual(['README.md ltr']);
      expect(pathParts('lib/new.ts, Renamed from src/old.ts')).toEqual(['src/old.ts rtl', 'lib/ rtl', 'new.ts rtl']);

      flat.unmount();
      render(FileTree, { props: { nodes: buildFileTree(files), selectedPath: null, onselect: vi.fn(), showReasons: true } });

      expect(pathParts('main.ts, Modified')).toEqual(['main.ts ltr']);
   });

   describe('with review marks', () => {
      function renderReviewed(reviewed: string[]) {
         const onselect = vi.fn();
         const onreview = vi.fn();
         render(FileTree, { props: { nodes, selectedPath: null, onselect, reviewedPaths: new Set(reviewed), onreview, showReasons: true } });
         return { onselect, onreview };
      }

      it('puts a checkbox in each file row’s first column, checked for a reviewed file, and none in a folder row', () => {
         renderReviewed(['src/index.ts']);

         expect(parts('index.ts, Modified')).toEqual(['square-check', 'seti typescript', 'index.ts', 'M']);
         expect(parts('README.md, Deleted')).toEqual(['square', 'seti info', 'README.md', 'D']);
         expect(parts('src')).toEqual(['chevron-right down', 'folder-open', 'src']);
         expect(item('index.ts, Modified').getAttribute('aria-checked')).toBe('true');
         expect(item('README.md, Deleted').getAttribute('aria-checked')).toBe('false');
         expect(item('src').hasAttribute('aria-checked')).toBe(false);
      });

      it('toggles a file’s mark on a click on its checkbox without opening the file, and focuses its row', async () => {
         const { onselect, onreview } = renderReviewed(['src/index.ts']);

         await fireEvent.click(item('main.ts, Modified').querySelector('.check')!);
         await fireEvent.click(item('index.ts, Modified').querySelector('.check')!);

         expect(onreview.mock.calls).toEqual([
            ['src/app/main.ts', true],
            ['src/index.ts', false],
         ]);
         expect(onselect).not.toHaveBeenCalled();
         await waitFor(() => expect(focused()).toBe('index.ts, Modified'));
      });

      it('toggles the focused file’s mark on Space and opens it on Enter, and toggles a folder on Space', async () => {
         const { onselect, onreview } = renderReviewed([]);
         item('README.md, Deleted').focus();

         await press(' ');
         await press('Enter');
         item('app').focus();
         await press(' ');

         expect(onreview).toHaveBeenCalledExactlyOnceWith('README.md', true);
         expect(onselect).toHaveBeenCalledExactlyOnceWith('README.md');
         expect(item('app').getAttribute('aria-expanded')).toBe('false');
      });
   });

   it('leaves a file’s first column empty beside folders, so its name lines up with theirs', () => {
      renderTree();

      expect(parts('src')).toEqual(['chevron-right down', 'folder-open', 'src']);
      expect(parts('index.ts, Modified')).toEqual(['', 'seti typescript', 'index.ts', 'M']);
      expect(parts('README.md, Deleted')).toEqual(['', 'seti info', 'README.md', 'D']);
   });

   it('marks the open file’s row as current, and only its row, and moves the mark with the open file', async () => {
      const onselect = vi.fn();
      const view = render(FileTree, { props: { nodes, selectedPath: 'src/app/main.ts', onselect, showReasons: true } });

      expect(currentRows(document.body)).toEqual(['main.ts, Modified']);
      await view.rerender({ nodes, selectedPath: 'README.md', onselect });
      expect(currentRows(document.body)).toEqual(['README.md, Deleted']);
      await view.rerender({ nodes, selectedPath: null, onselect });
      expect(currentRows(document.body)).toEqual([]);
   });

   it('marks the collapsed folder that holds the open file, and the file again once its folders expand', async () => {
      renderTree('src/app/main.ts');

      await fireEvent.click(screen.getByText('app'));
      expect(currentRows(document.body)).toEqual(['app']);
      await fireEvent.click(screen.getByText('src'));
      expect(currentRows(document.body)).toEqual(['src']);

      await fireEvent.click(screen.getByText('src'));
      expect(currentRows(document.body)).toEqual(['app']);
      await fireEvent.click(screen.getByText('app'));
      expect(currentRows(document.body)).toEqual(['main.ts, Modified']);
   });

   it('marks no folder whose name only starts like the open file’s folder', async () => {
      render(FileTree, { props: { nodes: buildFileTree([modified('src/a.ts'), modified('src2/b.ts')]), selectedPath: 'src2/b.ts', onselect: vi.fn(), showReasons: true } });

      await fireEvent.click(screen.getByText('src'));
      await fireEvent.click(screen.getByText('src2'));

      expect(currentRows(document.body)).toEqual(['src2']);
   });

   it('turns a folder’s chevron and closes its icon when the folder collapses', async () => {
      renderTree();

      await fireEvent.click(screen.getByText('app'));

      expect(parts('app')).toEqual(['chevron-right', 'folder', 'app']);
      expect(parts('src')).toEqual(['chevron-right down', 'folder-open', 'src']);
   });

   it('ignores a click in the indent of a nested folder', async () => {
      const onselect = renderTree();
      const groups = screen.getAllByRole('group');

      for (const group of groups) {
         await fireEvent.click(group);
      }

      expect(groups).toHaveLength(2);
      expect(item('src').getAttribute('aria-expanded')).toBe('true');
      expect(item('app').getAttribute('aria-expanded')).toBe('true');
      expect(onselect).not.toHaveBeenCalled();
   });
});

describe('nodesOutside', () => {
   const tree = buildFileTree([modified('src/app/main.ts'), modified('src/pay.ts'), modified('docs/guide.md'), modified('README.md')]);

   it('returns each file outside the kept paths and each folder whose files all are', () => {
      expect([...nodesOutside(tree, new Set(['src/pay.ts']))].sort()).toEqual(['README.md', 'docs/', 'docs/guide.md', 'src/app/', 'src/app/main.ts']);
   });

   it('returns the files of a flat list outside the kept paths', () => {
      expect([...nodesOutside(buildFileList([modified('a.ts'), modified('b.ts')]), new Set(['b.ts']))]).toEqual(['a.ts']);
   });
});
