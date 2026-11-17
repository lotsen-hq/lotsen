import { preference } from './preference';
import { changedFilesAt } from './repositoryTree';
import type { ChangedFile, CodeReference, FileChangeKind, FileMark } from './types';

/** A changed file's row in the tree, named by the last segment of its path, or in the flat list, named by its whole path. */
export interface ChangedFileRow {
   type: 'file';
   row: 'changed';
   /** The file's path, which is unique among the files. */
   id: string;
   name: string;
   path: string;
   kind: FileChangeKind;
   /** The path at the merge base of a renamed file, or null for every other kind. */
   oldPath: string | null;
   /** Whether the file's only change is its line endings. */
   lineEndingsOnly: boolean;
   /** Claude's mark on the file, or null when the plan has none for its path. */
   mark: FileMark | null;
}

/** A row of a file that the stops of a repository tour visit, named as a changed file's row is, with the tour marker's count. */
export interface VisitedFileRow {
   type: 'file';
   row: 'visited';
   /** The file's path, which is unique among the files. */
   id: string;
   name: string;
   path: string;
   /** The number of stops that visit the file. */
   visits: number;
}

/** A file's row: a changed file's, with its change and its mark, or a visited file's, with its stop count. */
export type FileTreeFile = ChangedFileRow | VisitedFileRow;

/** A folder in the tree. A chain of folders that each hold only one folder is one node, named by the chain's segments joined with slashes, with the path of the last folder in the chain. */
export interface FileTreeFolder<F extends FileTreeFile = FileTreeFile> {
   type: 'folder';
   /** The folder's path with a trailing slash, so that it differs from a deleted file at the same path. */
   id: string;
   name: string;
   path: string;
   /** Folders first, then files, each in name order. */
   children: FileTreeNode<F>[];
}

export type FileTreeNode<F extends FileTreeFile = FileTreeFile> = F | FileTreeFolder<F>;

/** What a file tree lists, which decides the kind of its file rows: changed files, whose rows can carry review checkboxes, or visited files. */
export type FileTreeListing =
   | {
        kind: 'changed';
        nodes: FileTreeNode<ChangedFileRow>[];
        /** Whether a marked file's reason shows under its name. The row's tooltip and label hold it either way. */
        showReasons: boolean;
        /** The paths of the files whose checkbox is checked. */
        reviewedPaths?: ReadonlySet<string>;
        /** Sets or clears a file's review mark. Without it the file rows have no checkbox. */
        onreview?: (path: string, reviewed: boolean) => void;
     }
   | { kind: 'visited'; nodes: FileTreeNode<VisitedFileRow>[] };

/** What the keyboard handling of a tree needs of a node. */
export interface TreeNode {
   type: 'file' | 'folder';
   /** Unique among the tree's nodes. */
   id: string;
}

/** A node that a tree shows, at its nesting depth from 0, with its parent folder or null at the top. */
export interface TreeRow<N extends TreeNode> {
   node: N;
   depth: number;
   parent: N | null;
   /** Whether the node is a folder that shows what it holds, which can be nothing while its entries load. */
   expanded: boolean;
}

export type FileTreeRow = TreeRow<FileTreeNode>;

/** What a key press on a tree row asks for. `activate` selects a file or toggles a folder, and `check` toggles a file's review mark. */
export type FileTreeAction = { type: 'focus'; id: string } | { type: 'expand'; id: string } | { type: 'collapse'; id: string } | { type: 'activate'; id: string } | { type: 'check'; id: string };

/** How the Files panel shows the changed files: as a directory tree, or as one list of full paths. */
export type FileLayout = 'tree' | 'flat';

/** The changed-file layout the reviewer last chose, the tree by default. */
export const fileLayoutPreference = preference<FileLayout>('lotsen.fileLayout', ['tree', 'flat'], 'tree');
/** Whether the reviewer last chose to list the current stop's files first, off by default. */
export const groupByStopPreference = preference<'true' | 'false'>('lotsen.groupFilesByStop', ['true', 'false'], 'false');
/** Whether the reviewer last chose to dim the changed files outside the current stop, on by default. */
export const dimOtherFilesPreference = preference<'true' | 'false'>('lotsen.dimOtherFiles', ['true', 'false'], 'true');
/** Whether the reviewer last chose to show a marked file's reason under its name, off by default. */
export const showMarkReasonsPreference = preference<'true' | 'false'>('lotsen.showMarkReasons', ['true', 'false'], 'false');

interface DraftFolder<F extends FileTreeFile> {
   path: string;
   folders: Map<string, DraftFolder<F>>;
   files: F[];
}

const collator = new Intl.Collator('en');
const byName = (a: { name: string }, b: { name: string }) => collator.compare(a.name, b.name);

/**
 * Compares two repository paths segment by segment, each segment in name order without regard to letter case.
 *
 * Comparing whole paths would sort a slash among the other punctuation, which splits a folder's files around a sibling such as `a-b` or `a.txt`.
 */
export function compareRepositoryPaths(a: string, b: string): number {
   const left = a.split('/');
   const right = b.split('/');
   for (let index = 0; index < Math.min(left.length, right.length); index++) {
      const order = collator.compare(left[index]!, right[index]!);
      if (order !== 0) {
         return order;
      }
   }

   return left.length - right.length;
}

/** Returns the changed files as top-level file nodes, each named by its whole path, in the order of {@link compareRepositoryPaths}, with each file's mark from the marks by path. */
export function buildFileList(files: readonly ChangedFile[], marks: ReadonlyMap<string, FileMark> = new Map()): ChangedFileRow[] {
   return flatNodes(files.map((file) => changedRow(file, marks)));
}

/** Returns the top-level nodes of the tree that the changed files make, with each file's mark from the marks by path. */
export function buildFileTree(files: readonly ChangedFile[], marks: ReadonlyMap<string, FileMark> = new Map()): FileTreeNode<ChangedFileRow>[] {
   return treeNodes(files.map((file) => changedRow(file, marks)));
}

/**
 * Returns the rows of the files that the stops visit, each named by its whole path, in the order of {@link compareRepositoryPaths}.
 *
 * @param visits The counts that `stopVisits` in `repositoryTree.ts` returns, whose folder IDs end in a slash and give no row.
 */
export function visitedFiles(visits: ReadonlyMap<string, number>): VisitedFileRow[] {
   const rows = [...visits].filter(([id]) => !id.endsWith('/')).map(([path, count]): VisitedFileRow => ({ type: 'file', row: 'visited', id: path, name: path, path, visits: count }));
   return flatNodes(rows);
}

/** Returns the file rows as top-level nodes, each named by its whole path, in the order of {@link compareRepositoryPaths}. */
export function flatNodes<F extends FileTreeFile>(rows: readonly F[]): F[] {
   return rows.map((row) => ({ ...row, name: row.path })).sort((a, b) => compareRepositoryPaths(a.path, b.path));
}

/** Returns the top-level nodes of the tree that the file rows make, each file named by the last segment of its path. */
export function treeNodes<F extends FileTreeFile>(rows: readonly F[]): FileTreeNode<F>[] {
   const root: DraftFolder<F> = { path: '', folders: new Map(), files: [] };
   for (const row of rows) {
      const segments = row.path.split('/');
      const name = segments.pop()!;
      let folder = root;
      for (const segment of segments) {
         let child = folder.folders.get(segment);
         if (!child) {
            child = { path: folder.path === '' ? segment : `${folder.path}/${segment}`, folders: new Map(), files: [] };
            folder.folders.set(segment, child);
         }

         folder = child;
      }

      folder.files.push({ ...row, name });
   }

   return childrenOf(root);
}

/** Returns the IDs of the nodes outside the kept paths: each file whose path is not kept, and each folder that holds only such files. */
export function nodesOutside(nodes: readonly FileTreeNode[], kept: ReadonlySet<string>): Set<string> {
   const outside = new Set<string>();
   const visit = (node: FileTreeNode): boolean => {
      // Every child is visited, so a kept file does not end the walk before its siblings.
      const isOutside = node.type === 'file' ? !kept.has(node.path) : node.children.map(visit).every(Boolean);
      if (isOutside) {
         outside.add(node.id);
      }

      return isOutside;
   };

   nodes.forEach(visit);
   return outside;
}

/** The files split into the ones the current stop points at and the rest, each in the order of the list. */
export interface StopGroups<F = ChangedFile> {
   stop: F[];
   rest: F[];
}

/**
 * Returns the changed files that the stop's code points at, and the rest.
 *
 * A passage or a code reference finds a file by its path at its side, so a base-side one finds a renamed file by its old path. One in an unchanged file finds none.
 *
 * @param code The code that the stop visits, as `visitedCode` in `pin.ts` returns it.
 * @returns The groups, or null when the code finds no changed file.
 */
export function groupByStop(files: readonly ChangedFile[], code: readonly CodeReference[]): StopGroups | null {
   const sides = { base: changedFilesAt(files, 'base'), head: changedFilesAt(files, 'head') };
   const found = new Set(code.map((reference) => sides[reference.side].get(reference.path)).filter((file) => file !== undefined));
   if (found.size === 0) {
      return null;
   }

   return { stop: files.filter((file) => found.has(file)), rest: files.filter((file) => !found.has(file)) };
}

/**
 * Returns the visited files that the stop's code at the head points at, and the rest.
 *
 * @param code The code that the stop visits, as `visitedCode` in `pin.ts` returns it. Its base side names no file of a snapshot.
 * @returns The groups, or null when the code finds no visited file.
 */
export function groupVisitedByStop(files: readonly VisitedFileRow[], code: readonly CodeReference[]): StopGroups<VisitedFileRow> | null {
   const paths = new Set(code.filter((reference) => reference.side === 'head').map((reference) => reference.path));
   const stop = files.filter((file) => paths.has(file.path));
   return stop.length === 0 ? null : { stop, rest: files.filter((file) => !paths.has(file.path)) };
}

function changedRow(file: ChangedFile, marks: ReadonlyMap<string, FileMark>): ChangedFileRow {
   const oldPath = file.oldPath ?? null;
   return {
      type: 'file',
      row: 'changed',
      id: file.path,
      name: file.path,
      path: file.path,
      kind: file.kind,
      oldPath,
      lineEndingsOnly: file.lineEndingsOnly === true,
      mark: marks.get(file.path) ?? null,
   };
}

function childrenOf<F extends FileTreeFile>(draft: DraftFolder<F>): FileTreeNode<F>[] {
   const folders = [...draft.folders].map(([name, child]) => finishFolder(name, child)).sort(byName);
   return [...folders, ...draft.files.sort(byName)];
}

function finishFolder<F extends FileTreeFile>(name: string, draft: DraftFolder<F>): FileTreeFolder<F> {
   const [only] = draft.folders;
   if (only && draft.folders.size === 1 && draft.files.length === 0) {
      return finishFolder(`${name}/${only[0]}`, only[1]);
   }

   return { type: 'folder', id: `${draft.path}/`, name, path: draft.path, children: childrenOf(draft) };
}

/** Returns the rows the tree shows, in display order, leaving out what the collapsed folders hold. */
export function visibleRows(tree: readonly FileTreeNode[], collapsed: ReadonlySet<string>): FileTreeRow[] {
   const rows: FileTreeRow[] = [];
   const visit = (nodes: readonly FileTreeNode[], depth: number, parent: FileTreeFolder | null) => {
      for (const node of nodes) {
         const expanded = node.type === 'folder' && !collapsed.has(node.id);
         rows.push({ node, depth, parent, expanded });
         if (node.type === 'folder' && expanded) {
            visit(node.children, depth + 1, node);
         }
      }
   };
   visit(tree, 0, null);
   return rows;
}

/**
 * Returns what a key press on the focused row asks for, as the WAI-ARIA tree view pattern defines the keys.
 *
 * In a tree whose files are checkable, Space checks a file, as it toggles a checkbox, and Enter still selects it.
 *
 * @returns The action, or null when the tree does not handle the key. An arrow key with nothing to do returns a focus on the same row, so the caller still stops the panel from scrolling.
 */
export function fileTreeKeyAction<N extends TreeNode>(rows: readonly TreeRow<N>[], focusedId: string, key: string, { checkable = false } = {}): FileTreeAction | null {
   const index = rows.findIndex((row) => row.node.id === focusedId);
   const row = rows[index];
   if (!row) {
      return null;
   }

   const focus = (at: number): FileTreeAction => ({ type: 'focus', id: rows[at]!.node.id });
   const { node, expanded } = row;
   switch (key) {
      case 'ArrowDown':
         return focus(Math.min(index + 1, rows.length - 1));
      case 'ArrowUp':
         return focus(Math.max(index - 1, 0));
      case 'Home':
         return focus(0);
      case 'End':
         return focus(rows.length - 1);
      case 'ArrowRight':
         if (node.type === 'file') {
            return focus(index);
         }

         if (!expanded) {
            return { type: 'expand', id: node.id };
         }

         return rows[index + 1]?.parent === node ? focus(index + 1) : focus(index);
      case 'ArrowLeft':
         if (expanded) {
            return { type: 'collapse', id: node.id };
         }

         return row.parent ? { type: 'focus', id: row.parent.id } : focus(index);
      case ' ':
         return { type: checkable && node.type === 'file' ? 'check' : 'activate', id: node.id };
      case 'Enter':
         return { type: 'activate', id: node.id };
      default:
         return null;
   }
}

/** Whether a folder holds the file at a repository path, at any depth, which a folder whose name only starts the same way does not. */
export function holds(folderPath: string, path: string | null): boolean {
   return path !== null && path.startsWith(`${folderPath}/`);
}
