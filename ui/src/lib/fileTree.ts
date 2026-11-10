import { preference } from './preference';
import { changedFilesAt } from './repositoryTree';
import type { ChangedFile, CodeReference, FileChangeKind, FileMark } from './types';

/** A changed file in the tree, named by the last segment of its path, or in the flat list, named by its whole path. */
export interface FileTreeFile {
   type: 'file';
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

/** A folder in the tree. A chain of folders that each hold only one folder is one node, named by the chain's segments joined with slashes, with the path of the last folder in the chain. */
export interface FileTreeFolder {
   type: 'folder';
   /** The folder's path with a trailing slash, so that it differs from a deleted file at the same path. */
   id: string;
   name: string;
   path: string;
   /** Folders first, then files, each in name order. */
   children: FileTreeNode[];
}

export type FileTreeNode = FileTreeFile | FileTreeFolder;

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

/** The changed-file layout the reviewer last chose in this browser, the tree by default. */
export const fileLayoutPreference = preference<FileLayout>('lotsen.fileLayout', ['tree', 'flat'], 'tree');
/** Whether the reviewer last chose in this browser to list the current stop's files first, off by default. */
export const groupByStopPreference = preference<'true' | 'false'>('lotsen.groupFilesByStop', ['true', 'false'], 'false');
/** Whether the reviewer last chose in this browser to dim the changed files outside the current stop, on by default. */
export const dimOtherFilesPreference = preference<'true' | 'false'>('lotsen.dimOtherFiles', ['true', 'false'], 'true');
/** Whether the reviewer last chose in this browser to show a marked file's reason under its name, off by default. */
export const showMarkReasonsPreference = preference<'true' | 'false'>('lotsen.showMarkReasons', ['true', 'false'], 'false');

interface DraftFolder {
   path: string;
   folders: Map<string, DraftFolder>;
   files: FileTreeFile[];
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
export function buildFileList(files: readonly ChangedFile[], marks: ReadonlyMap<string, FileMark> = new Map()): FileTreeFile[] {
   return files.map((file) => fileNode(file, file.path, marks)).sort((a, b) => compareRepositoryPaths(a.path, b.path));
}

/** Returns the top-level nodes of the tree that the changed files make, with each file's mark from the marks by path. */
export function buildFileTree(files: readonly ChangedFile[], marks: ReadonlyMap<string, FileMark> = new Map()): FileTreeNode[] {
   const root: DraftFolder = { path: '', folders: new Map(), files: [] };
   for (const file of files) {
      const segments = file.path.split('/');
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

      folder.files.push(fileNode(file, name, marks));
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

/** The changed files split into the ones the current stop points at and the rest, each in the order of the list. */
export interface StopGroups {
   stop: ChangedFile[];
   rest: ChangedFile[];
}

/**
 * Returns the changed files that the passages point at, and the rest.
 *
 * A passage finds a file by its path at the passage's side, so a base-side passage finds a renamed file by its old path. A passage in an unchanged file finds none.
 *
 * @returns The groups, or null when the passages find no changed file.
 */
export function groupByStop(files: readonly ChangedFile[], passages: readonly CodeReference[]): StopGroups | null {
   const sides = { base: changedFilesAt(files, 'base'), head: changedFilesAt(files, 'head') };
   const found = new Set(passages.map((passage) => sides[passage.side].get(passage.path)).filter((file) => file !== undefined));
   if (found.size === 0) {
      return null;
   }

   return { stop: files.filter((file) => found.has(file)), rest: files.filter((file) => !found.has(file)) };
}

function fileNode(file: ChangedFile, name: string, marks: ReadonlyMap<string, FileMark>): FileTreeFile {
   const oldPath = file.oldPath ?? null;
   return { type: 'file', id: file.path, name, path: file.path, kind: file.kind, oldPath, lineEndingsOnly: file.lineEndingsOnly === true, mark: marks.get(file.path) ?? null };
}

function childrenOf(draft: DraftFolder): FileTreeNode[] {
   const folders = [...draft.folders].map(([name, child]) => finishFolder(name, child)).sort(byName);
   return [...folders, ...draft.files.sort(byName)];
}

function finishFolder(name: string, draft: DraftFolder): FileTreeFolder {
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
