import type { AnchorInput, DirectoryListing, FileDiff, FileSide, FileText, TourPin } from './types';

/** What the reviewer picks to ask about in the page: lines of code at one side, a file at one side, or text in a stop's prose. */
export type PickedAnchor = Extract<AnchorInput, { kind: 'code' | 'file' | 'stopText' }>;

/** Hands a pick to the Threads panel's ask, with the first line of the text that the pick covers, which the ask box quotes, or null when it covers no text, as a whole file does. */
export type PickHandler = (anchor: PickedAnchor, quote: string | null) => void;

/** What a question is about: the open stop, the whole tour, or what the reviewer picked. */
export type AskAbout = 'stop' | 'tour' | PickedAnchor;

/** How a prune of the clones ended: the number of clones it removed, or the reason it failed. */
export type PruneOutcome = { removed: number } | { failure: string };

/**
 * What the page knows of the selected file's diff: a load in progress, the diff, the pin that the server gave when it refused the diff because the tour is not pinned, or a failure with a message and
 * the server's reason when it gave one. Each state names the path of the file it belongs to. A loaded diff of a binary file, or of a large file that the user has not asked for, has no texts.
 */
export type KnownDiff =
   | { state: 'loading'; path: string }
   | { state: 'loaded'; path: string; diff: FileDiff }
   | { state: 'notPinned'; path: string; pin: TourPin }
   | { state: 'failed'; path: string; message: string; reason: string | null };

/**
 * What the page knows of the unchanged file that the reviewer opened from the whole-repository view: a load in progress, its text at one side, the pin that the server gave
 * when it refused the text because the tour is not pinned, or a failure with a message and the server's reason when it gave one. Each state names the side and the path of its file. A loaded
 * binary file, or a large file that the user has not asked for, has no text.
 */
export type KnownText =
   | { state: 'loading'; side: FileSide; path: string }
   | { state: 'loaded'; side: FileSide; path: string; text: FileText }
   | { state: 'notPinned'; side: FileSide; path: string; pin: TourPin }
   | { state: 'failed'; side: FileSide; path: string; message: string; reason: string | null };

/** The result of listing one directory: the listing, or a failure with a message and the server's reason when it gave one. */
export type KnownDirectory = { state: 'loaded'; listing: DirectoryListing } | { state: 'failed'; message: string; reason: string | null };

/** The reviewer's request from the summary page to open a check of a stop, which only the first explanation that shows the check carries out. */
export class CheckOpening {
   readonly stopId: string;
   readonly checkId: string;
   private taken = false;

   constructor(stopId: string, checkId: string) {
      this.stopId = stopId;
      this.checkId = checkId;
   }

   /** Returns true on the first call and false after, so an explanation that mounts later, such as the editor's redraw of an inline block, leaves the sections, the scroll and the focus alone. */
   take(): boolean {
      const first = !this.taken;
      this.taken = true;
      return first;
   }
}
