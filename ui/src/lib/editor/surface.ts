import { getContext, setContext } from 'svelte';
import type { DiffLayout } from '../diffLayout';
import type { LinesAtSide, PassageDecorations } from '../passageMarks';
import type { FileSide, TextDiff } from '../types';
import { codeMirrorSurfaces } from './codeMirrorSurface';

/** A 1-based line of one side, as git counts lines. */
export interface SideLine {
   side: FileSide;
   line: number;
}

/** Content that an editor shows in a block below some lines. */
export interface BlockHost {
   /** What tells the block apart from another. An editor keeps a block whose key is the shown block's key as it is. */
   key: string;
   /** The lines below whose last line the block sits. */
   below: LinesAtSide;
   /** Mounts the content into an element of the editor's, and returns the function that removes the content again. */
   render: (host: HTMLElement) => () => void;
}

/** The elements of a side-by-side layout: the row that holds both panes and the element of each pane in it. */
export interface SideBySidePanes {
   row: HTMLElement;
   base: HTMLElement;
   head: HTMLElement;
}

/**
 * Provides what the page asks of the editors of one file: a diff in its layout, or one side's text.
 *
 * The page and its components reach the editors only through this interface, so a test can record the calls. `CodeMirrorSurface` is the implementation over CodeMirror.
 */
export interface EditorSurface {
   /** The row of a side-by-side layout and the element of each pane in it, or null for any other layout. */
   readonly panes: SideBySidePanes | null;
   /** Gives the editors the file's decorations, and collapses the regions away from the passages when the decorations ask for it. */
   showPassages(decorations: PassageDecorations): void;
   /**
    * Scrolls a line to the top of the view, and expands the collapsed region that hides it.
    *
    * @returns Whether an editor shows the line's side, which a file's text at the other side does not.
    */
   focus(line: SideLine): boolean;
   /**
    * Shows a block below the last of its lines, in place of the block shown before, or no block with null.
    *
    * Side by side, the other pane gets empty space as high as the block, so the lines after it stay aligned.
    *
    * @returns Whether an editor shows the block, which none does when no editor shows the side of the block's lines.
    */
   placeBlock(block: BlockHost | null): boolean;
   /** Returns the git lines at one side that a selection of the page covers in the editors, `bothSides` when it covers lines of both sides, or null when it covers none of their lines. */
   selection(range: Range): LinesAtSide | 'bothSides' | null;
   /** Returns the text of a git line at one side, without its line break, or null when the editors show no such line. */
   lineText(line: SideLine): string | null;
   /** Removes the editors from the page and releases them. */
   destroy(): void;
}

/** What every editor of a file mounts from. */
interface SurfaceOptions {
   parent: HTMLElement;
   /** The file's path, which picks the language that highlights it. */
   path: string;
   /** Cancels the mount while the language loads, or undefined for a mount that nothing cancels. */
   signal?: AbortSignal;
}

/** What a diff's editors mount from. */
export interface DiffSurfaceOptions extends SurfaceOptions {
   layout: DiffLayout;
   diff: TextDiff;
}

/** What the editor of one side's text mounts from. */
export interface FileSurfaceOptions extends SurfaceOptions {
   /** The file's text. The editor splits lines at CRLF, CR and LF. */
   text: string;
   /** The side that the text is at, which the file's passages name. */
   side: FileSide;
}

/** Mounts the editors of the files that the page shows. */
export interface EditorSurfaces {
   /**
    * Mounts a read-only view of a file's diff in its parent, once the file's language has loaded.
    *
    * @returns The surface, or null when the signal canceled the mount. Nothing was mounted then. The promise rejects only when the editors fail to build, because a language that fails to load leaves the file in plain text.
    */
   diff(options: DiffSurfaceOptions): Promise<EditorSurface | null>;
   /**
    * Mounts a read-only view of one side's text in its parent, once the file's language has loaded.
    *
    * @returns The surface, or null when the signal canceled the mount. Nothing was mounted then. The promise rejects only when the editors fail to build, because a language that fails to load leaves the file in plain text.
    */
   file(options: FileSurfaceOptions): Promise<EditorSurface | null>;
}

const surfacesKey = Symbol('editorSurfaces');

/** Makes the components below mount their editors through `surfaces`, as a test does to record the calls. */
export function provideEditorSurfaces(surfaces: EditorSurfaces): void {
   setContext(surfacesKey, surfaces);
}

/** Returns the editor surfaces that a component mounts its editors through: the ones a parent provided, or else CodeMirror's. Call it while the component initializes. */
export function editorSurfaces(): EditorSurfaces {
   return getContext<EditorSurfaces | undefined>(surfacesKey) ?? codeMirrorSurfaces;
}
