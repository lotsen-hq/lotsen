import { changedFilePaths, type FilePaths } from './pin';
import { filePassageDecorations, noPassageDecorations, type DecorationSwitches, type PassageDecorations } from './passageMarks';
import { changedFilesAt } from './repositoryTree';
import type { ChangedFile, FileSide, StopPassage } from './types';
import { stepOrder } from './viewMode';

/** A file at one side, as a passage, a code reference or the whole-repository view names it. */
export interface FileReference {
   side: FileSide;
   path: string;
}

interface StackFileBase {
   /** What tells this file apart from the other files of the stack: `diff:` and a changed file's path, or `text:`, the side and the path. */
   key: string;
   paths: FilePaths;
   /** The file's passages of the open stop, which always collapse the file to them. */
   decorations: PassageDecorations;
   /** Whether the reviewer added the file from outside the stop. */
   outside: boolean;
}

/** One file of the stop-files view: a changed file's diff, or the text at one side of a file that the pull request does not change. */
export type StackFile = (StackFileBase & { kind: 'diff'; file: ChangedFile }) | (StackFileBase & { kind: 'text'; side: FileSide; path: string });

/** A stacked file's key and the viewport positions of its top and bottom edges. */
export interface StackFileExtent {
   key: string;
   top: number;
   bottom: number;
}

// A file of which no more than its bottom border still shows has scrolled away.
const scrolledAwayMargin = 8;

/**
 * Returns the key of the stacked file that the reviewer reads, or null when no file shows, as before layout.
 *
 * That is the opened file while any part of it shows in the viewport, since the last files of a stack cannot scroll to its top. Otherwise it is the last file when the viewport is at the stack's
 * end, for the same reason, and else the file at the top of the viewport: the first file whose bottom edge lies below the viewport's top.
 *
 * @param viewport The viewport positions of the top and bottom edges of the stack's scroll viewport, and whether it is at the stack's end, as {@link scrolledToEnd} decides.
 * @param opened The key of the file that the reviewer opened since they last scrolled, or null for none.
 */
export function shownStackFile(files: readonly StackFileExtent[], viewport: { top: number; bottom: number; atEnd: boolean }, opened: string | null): string | null {
   const held = files.find((file) => file.key === opened);
   if (held && held.bottom > viewport.top + scrolledAwayMargin && held.top < viewport.bottom) {
      return held.key;
   }

   if (viewport.atEnd) {
      return files.at(-1)?.key ?? null;
   }

   return files.find((file) => file.bottom > viewport.top + scrolledAwayMargin)?.key ?? null;
}

/**
 * Returns whether a scroll viewport that can scroll shows the end of its content.
 *
 * It allows a pixel, because `scrollTop` is fractional while `scrollHeight` and `clientHeight` are rounded (https://developer.mozilla.org/en-US/docs/Web/API/Element/scrollHeight). A viewport that
 * scrolls no further than that pixel cannot scroll.
 */
export function scrolledToEnd({ scrollTop, scrollHeight, clientHeight }: { scrollTop: number; scrollHeight: number; clientHeight: number }): boolean {
   const scrollable = scrollHeight - clientHeight;
   return scrollable > 1 && scrollable - scrollTop <= 1;
}

/**
 * Returns the key that a file at one side gets in the stack, as {@link StackFileBase.key} describes it.
 *
 * @param files The changed files, or null when they are not known, which makes every file a text.
 */
export function stackFileKey(files: readonly ChangedFile[] | null, reference: FileReference): string {
   return resolver(files)(reference, false).key;
}

/**
 * Returns the files of the stop-files view: the files of the stop's passages in step through's order, then the files that the reviewer added from outside the stop, in the order added.
 *
 * Each file appears once. A renamed file's passages at its old path and at its path are one file.
 *
 * @param files The changed files, or null when they are not known, which makes every file a text at its side.
 */
export function stopFileStack(passages: readonly StopPassage[], files: readonly ChangedFile[] | null, added: readonly FileReference[], switches: DecorationSwitches): StackFile[] {
   const resolve = resolver(files);
   const stack = new Map<string, StackFile>();
   for (const [references, outside] of [
      [stepOrder(passages), false],
      [added, true],
   ] as const) {
      for (const reference of references) {
         const file = resolve(reference, outside);
         if (!stack.has(file.key)) {
            stack.set(file.key, { ...file, decorations: filePassageDecorations(passages, file.paths, switches, { collapse: true }) });
         }
      }
   }

   return [...stack.values()];
}

function resolver(files: readonly ChangedFile[] | null): (reference: FileReference, outside: boolean) => StackFile {
   const sides = { base: changedFilesAt(files ?? [], 'base'), head: changedFilesAt(files ?? [], 'head') };
   return ({ side, path }, outside) => {
      const file = sides[side].get(path);
      if (file) {
         return { kind: 'diff', key: `diff:${file.path}`, file, paths: changedFilePaths(file), decorations: noPassageDecorations, outside };
      }

      return { kind: 'text', key: `text:${side}:${path}`, side, path, paths: { base: side === 'base' ? path : null, head: side === 'head' ? path : null }, decorations: noPassageDecorations, outside };
   };
}
