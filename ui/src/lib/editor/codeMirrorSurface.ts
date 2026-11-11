import { gitLineText } from '../gitLines';
import type { LinesAtSide, PassageDecorations } from '../passageMarks';
import type { TextDiff } from '../types';
import { createDiffEditor, createFileEditor, linePosition, scrollToPosition, type DiffEditor } from './diffEditor';
import { loadLanguageFor } from './languages';
import type { BlockHost, EditorSurface, EditorSurfaces, PanelMoves, SideBySidePanes, SideLine } from './surface';

/** The editors of one file in CodeMirror, as the page asks for them through {@link EditorSurface}. */
export class CodeMirrorSurface implements EditorSurface {
   /** The element that the editors are mounted in. */
   readonly parent: HTMLElement;
   private readonly editor: DiffEditor;
   /** The texts that the editors show, as the server sent them: one side's text alone for a file's text. */
   private readonly texts: TextDiff;
   private readonly unfollow: () => void;

   /** @param moves - The moves of the panel that shows the editors, which rebind the editors to the panel's new document. */
   constructor(parent: HTMLElement, editor: DiffEditor, texts: TextDiff, moves: PanelMoves) {
      this.parent = parent;
      this.editor = editor;
      this.texts = texts;
      this.unfollow = moves.subscribe(() => editor.follow());
   }

   get panes(): SideBySidePanes | null {
      return this.editor.panes;
   }

   showPassages(decorations: PassageDecorations): void {
      this.editor.decorate(decorations);
   }

   focus({ side, line }: SideLine): boolean {
      const position = linePosition(this.editor, this.texts, side, line);
      if (position === null) {
         return false;
      }

      this.editor.reveal(position);
      scrollToPosition(position);
      return true;
   }

   placeBlock(block: BlockHost | null): boolean {
      return this.editor.explain(block);
   }

   selection(range: Range): LinesAtSide | 'bothSides' | null {
      return this.editor.selection(range);
   }

   lineText({ side, line }: SideLine): string | null {
      const text = side === 'base' ? this.texts.baseText : this.texts.headText;
      return text === null ? null : gitLineText(text, line);
   }

   destroy(): void {
      this.unfollow();
      this.editor.destroy();
   }
}

/** The editor surfaces of the page, which mount CodeMirror. */
export const codeMirrorSurfaces: EditorSurfaces = {
   diff: async ({ parent, path, layout, diff, signal, moves }) => {
      const language = await loadLanguageFor(path);
      return signal?.aborted ? null : new CodeMirrorSurface(parent, createDiffEditor({ parent, layout, diff, language }), diff, moves);
   },
   file: async ({ parent, path, text, side, signal, moves }) => {
      const language = await loadLanguageFor(path);
      if (signal?.aborted) {
         return null;
      }

      const texts: TextDiff = { content: 'text', baseText: side === 'base' ? text : null, headText: side === 'head' ? text : null, changes: [] };
      return new CodeMirrorSurface(parent, createFileEditor({ parent, text, side, language }), texts, moves);
   },
};
