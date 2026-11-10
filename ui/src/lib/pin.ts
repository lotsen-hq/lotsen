import type { ChangedFile, CodeReference, FileSide, PinnedCommits, StopContent, TourPin } from './types';

/** The repository paths of one file at each side, or null for a side where it does not exist. */
export interface FilePaths {
   base: string | null;
   head: string | null;
}

/** The commits that a pinned tour shows at each side: its merge base at the base and its head at the head. */
export type PinCommits = Readonly<Record<FileSide, string>>;

/** Returns the commits that a tour's pin shows at each side, or null while the tour is not pinned. */
export function pinCommits(pin: TourPin | undefined): PinCommits | null {
   return pin?.state === 'pinned' ? { base: pin.mergeBase, head: pin.head } : null;
}

/** Returns the number of the iteration that a tour is pinned to, or null while the tour is not pinned. */
export function pinIteration(pin: TourPin | undefined): number | null {
   return pin?.state === 'pinned' ? pin.iteration : null;
}

/**
 * Returns whether lines read at a commit are at the pin: the commit is the pin's commit of their side. Without a pin every commit counts, because the page then shows no code.
 *
 * Lines at another commit, as a stale stop's passages or an anchor that an iteration update could not move, may stand at other line numbers at the pin, so the page neither marks nor opens them there.
 */
export function atPin(lines: { side: FileSide; commit: string }, commits: PinCommits | null): boolean {
   return commits === null || lines.commit === commits[lines.side];
}

/**
 * Returns a test of whether a `ref:` link in a stop's prose is at the pin, for the stop at the version the page shows or for one of its earlier versions.
 *
 * A link carries no commit, so it reads at the commits that the stop's content was written against. An iteration update moves the links of a current stop that it leaves fresh, so those are at the pin.
 * The links of a stale stop and of an earlier version stay where they were written, so only a side where a passage of that content is at the pin is known to have kept its commit.
 *
 * @param current - Whether the page shows the stop's current version rather than an earlier one.
 */
export function referenceAtPin(stop: { content: StopContent | null; staleOn?: number | undefined }, current: boolean, commits: PinCommits | null): (reference: CodeReference) => boolean {
   if (commits === null || (current && stop.staleOn === undefined)) {
      return () => true;
   }

   const kept = new Set((stop.content?.passages ?? []).filter((passage) => atPin(passage, commits)).map((passage) => passage.side));
   return (reference) => kept.has(reference.side);
}

/** Returns a test of whether a `ref:` link in prose that was checked at `pin`, as Claude's synthesis was, is at the tour's pin: whether the commit of the link's side is the tour's commit of that side. */
export function pinnedReferenceAtPin(pin: Pick<PinnedCommits, 'mergeBase' | 'head'>, commits: PinCommits | null): (reference: CodeReference) => boolean {
   return (reference) => commits === null || (reference.side === 'base' ? pin.mergeBase : pin.head) === commits[reference.side];
}

/** Returns a changed file's paths at each side: a renamed file's old path at the base. */
export function changedFilePaths(file: ChangedFile): FilePaths {
   return {
      base: file.kind === 'added' ? null : (file.oldPath ?? file.path),
      head: file.kind === 'deleted' ? null : file.path,
   };
}

/** Returns whether a code reference names a file at its side: the file's path at that side, a renamed file's old path at the base. */
export function inFile(reference: Pick<CodeReference, 'side' | 'path'>, paths: FilePaths | null): boolean {
   return paths !== null && paths[reference.side] === reference.path;
}

/**
 * Returns whether a code reference points into a passage: its side and path are the passage's, and its lines overlap the passage's.
 *
 * A base reference names a renamed file by its old path, as a base passage does.
 */
export function pointsInto(reference: CodeReference, passage: CodeReference): boolean {
   return reference.side === passage.side && reference.path === passage.path && reference.startLine <= passage.endLine && reference.endLine >= passage.startLine;
}
