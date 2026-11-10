import { inFile, type FilePaths } from './pin';
import type { CodeReference, FileSide, PassageRole, StopPassage } from './types';

/** A passage of the open file, as its editors decorate it, with 1-based inclusive lines as git counts them. */
export interface DecoratedPassage {
   side: FileSide;
   startLine: number;
   endLine: number;
   role: PassageRole;
   label?: string;
   /** True on the passage that step through shows now, which gets a mark of its own. */
   focused?: true;
}

/** Lines of one file at one side, 1-based and inclusive as git counts them. */
export interface LinesAtSide {
   side: FileSide;
   startLine: number;
   endLine: number;
}

/**
 * What the editors of one file decorate: its passages, whether the lines outside every passage are dimmed, whether the passages' lines take their role's colors, and whether the diff collapses the regions
 * away from them.
 */
export interface PassageDecorations {
   passages: readonly DecoratedPassage[];
   dim: boolean;
   /** Whether the lines of each passage, and of the focused passage, take their marks. The labels show either way. */
   highlight: boolean;
   collapse: boolean;
   /** The lines of the hovered reference, which get a mark of their own and never dim, or undefined for none. Only the passages decide what collapses. */
   hovered?: LinesAtSide;
   /** The threads asked about lines of the file, each marked on its first line, or undefined for none. They neither dim nor collapse anything. */
   threads?: readonly ThreadSpot[];
}

/** A thread asked about lines of one file at one side, as its editors mark it. */
export interface ThreadSpot extends LinesAtSide {
   /** The thread's ID. */
   id: string;
   /** What the marker says of the thread, as plain text. */
   label: string;
}

/** A thread asked about lines of a file, by the file's path at the thread's side. */
export interface CodeThread extends ThreadSpot {
   path: string;
}

export const noPassageDecorations: PassageDecorations = { passages: [], dim: false, highlight: true, collapse: false };

/** The reviewer's switches for the passage decorations, which apply to every file. */
export interface DecorationSwitches {
   /** Whether the lines outside the passages dim. */
   dim: boolean;
   /** Whether the passages' lines take their role's colors. */
   highlight: boolean;
}

/**
 * Returns the decorations of one file from a stop's passages: the passages in the file, with the rest of the file dimmed when it has any and the switches allow it, and collapsed when it has any and
 * `collapse` asks for it.
 *
 * @param options `focused` marks every passage in the file as focused.
 */
export function filePassageDecorations(passages: readonly StopPassage[], paths: FilePaths, switches: DecorationSwitches, options: { collapse?: boolean; focused?: boolean } = {}): PassageDecorations {
   const found = passages
      .filter((passage) => inFile(passage, paths))
      .map(({ side, startLine, endLine, role, label }): DecoratedPassage => ({
         side,
         startLine,
         endLine,
         role,
         ...(label === undefined ? {} : { label }),
         ...(options.focused ? { focused: true } : {}),
      }));
   return { passages: found, dim: switches.dim && found.length > 0, highlight: switches.highlight, collapse: options.collapse === true && found.length > 0 };
}

/**
 * Returns a file's decorations with the lines of the hovered reference.
 *
 * @returns New decorations when the reference points into the file at its side, or else the decorations given, so a file that the hover does not touch is not decorated again.
 */
export function withHoveredReference(decorations: PassageDecorations, paths: FilePaths, reference: CodeReference | null): PassageDecorations {
   if (reference === null || !inFile(reference, paths)) {
      return decorations;
   }

   const { side, startLine, endLine } = reference;
   return { ...decorations, hovered: { side, startLine, endLine } };
}

/**
 * Returns a file's decorations with the threads asked about its lines at their side.
 *
 * @returns New decorations when a thread is in the file, or else the decorations given, so a file without one is not decorated again.
 */
export function withThreads(decorations: PassageDecorations, paths: FilePaths, threads: readonly CodeThread[]): PassageDecorations {
   const found = threads.filter((thread) => inFile(thread, paths)).map(({ id, side, startLine, endLine, label }): ThreadSpot => ({ id, side, startLine, endLine, label }));
   return found.length === 0 ? decorations : { ...decorations, threads: found };
}

/** Returns a file's decorations with every passage, the hovered lines and the threads at one side, for an editor of a file that holds the same text at both sides. */
export function atSide(decorations: PassageDecorations, side: FileSide): PassageDecorations {
   const { passages, hovered, threads } = decorations;
   return {
      ...decorations,
      passages: passages.map((passage) => ({ ...passage, side })),
      ...(hovered === undefined ? {} : { hovered: { ...hovered, side } }),
      ...(threads === undefined ? {} : { threads: threads.map((thread) => ({ ...thread, side })) }),
   };
}
