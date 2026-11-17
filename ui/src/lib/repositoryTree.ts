import type { TreeRow } from './fileTree';
import { visitedCode } from './pin';
import type { ChangedFile, FileSide, RepositoryEntry, RepositoryEntryType, Stop } from './types';

/** An entry of the whole-repository tree. A directory is a folder, and every other entry is a file row. */
export interface RepositoryTreeNode {
   type: 'file' | 'folder';
   /** The entry's path, with a trailing slash for a folder. */
   id: string;
   name: string;
   /** The repository path. */
   path: string;
   entry: RepositoryEntryType;
}

/** What the tree knows of one directory: a listing in progress, its entries as nodes, or a failure with a message and the server's reason when it gave one. */
export type DirectoryState = { state: 'loading' } | { state: 'loaded'; nodes: RepositoryTreeNode[] } | { state: 'failed'; message: string; reason: string | null };

const collator = new Intl.Collator('en');

/** Returns the nodes of a directory's entries: folders first, then the other entries, each in name order. */
export function repositoryNodes(entries: readonly RepositoryEntry[]): RepositoryTreeNode[] {
   const nodes = entries.map((entry): RepositoryTreeNode => {
      const folder = entry.type === 'directory';
      return { type: folder ? 'folder' : 'file', id: folder ? `${entry.path}/` : entry.path, name: entry.path.slice(entry.path.lastIndexOf('/') + 1), path: entry.path, entry: entry.type };
   });
   return nodes.sort((a, b) => (a.type === b.type ? collator.compare(a.name, b.name) : a.type === 'folder' ? -1 : 1));
}

/**
 * Returns the changed files by their path at one side.
 *
 * A renamed file is at its old path at the base. An added file is not at the base, and a deleted file is not at the head.
 */
export function changedFilesAt(files: readonly ChangedFile[], side: FileSide): Map<string, ChangedFile> {
   const found = new Map<string, ChangedFile>();
   for (const file of files) {
      if (side === 'head' && file.kind !== 'deleted') {
         found.set(file.path, file);
      } else if (side === 'base' && file.kind !== 'added') {
         found.set(file.oldPath ?? file.path, file);
      }
   }

   return found;
}

/**
 * Returns how many stops visit each row of a repository tour's tree that a tour marker marks, by the row's node ID: each file that {@link visitedCode} of a stop points into, and each folder that holds one.
 *
 * Only code at the snapshot's head counts, because the tree shows that commit alone. The folders come from the code's paths, so a folder carries its count before its listing loads.
 *
 * @param head The snapshot's head commit.
 */
export function stopVisits(stops: readonly Stop[], head: string): Map<string, number> {
   const visitors = new Map<string, Set<string>>();
   const visit = (id: string, stopId: string) => {
      const found = visitors.get(id);
      if (found === undefined) {
         visitors.set(id, new Set([stopId]));
      } else {
         found.add(stopId);
      }
   };

   // A fresh stop's links count at either side, so the side check excludes its base links, which a snapshot does not have.
   const commits = { base: '', head };
   for (const stop of stops) {
      for (const { side, path } of visitedCode(stop, commits)) {
         if (side === 'head') {
            visit(path, stop.id);
            for (let end = path.indexOf('/'); end !== -1; end = path.indexOf('/', end + 1)) {
               visit(path.slice(0, end + 1), stop.id);
            }
         }
      }
   }

   return new Map([...visitors].map(([id, stopIds]) => [id, stopIds.size]));
}

/**
 * Returns the rows the tree shows, in display order: the root directory's nodes, and the nodes of each expanded folder whose listing has loaded.
 *
 * @param listings The state of each directory the tree has asked for, by repository path, with an empty path for the root directory.
 * @param expanded The IDs of the expanded folders.
 */
export function repositoryRows(listings: ReadonlyMap<string, DirectoryState>, expanded: ReadonlySet<string>): TreeRow<RepositoryTreeNode>[] {
   const rows: TreeRow<RepositoryTreeNode>[] = [];
   const visit = (path: string, depth: number, parent: RepositoryTreeNode | null) => {
      const listing = listings.get(path);
      for (const node of listing?.state === 'loaded' ? listing.nodes : []) {
         const isExpanded = node.type === 'folder' && expanded.has(node.id);
         rows.push({ node, depth, parent, expanded: isExpanded });
         if (isExpanded) {
            visit(node.path, depth + 1, node);
         }
      }
   };
   visit('', 0, null);
   return rows;
}
