import { EditorView } from '@codemirror/view';
import { screen, waitFor } from '@testing-library/svelte';
import { expect, vi, type MockInstance } from 'vitest';
import { CodeMirrorSurface } from '../../src/lib/editor/codeMirrorSurface';

/** A spy on every editor surface's {@link CodeMirrorSurface.focus}. */
export type FocusSpy = MockInstance<CodeMirrorSurface['focus']>;

/** Spies on the focus of every editor surface until the test's mocks are restored. */
export function spyOnFocus(): FocusSpy {
   return vi.spyOn(CodeMirrorSurface.prototype, 'focus');
}

/**
 * Waits for the last focus of any editor surface to be the line at the side, in a surface mounted in the element that `scope` returns, and returns the text that the editor of that side shows at
 * the line.
 */
export async function focusedLine(focus: FocusSpy, scope: () => HTMLElement, side: 'base' | 'head', line: number): Promise<string> {
   return waitFor(() => {
      const last = focus.mock.calls.length - 1;
      expect(focus.mock.calls[last]?.[0]).toEqual({ side, line });
      expect(focus.mock.results[last]?.value).toBe(true);
      const surface = focus.mock.contexts[last] as CodeMirrorSurface;
      expect(scope().contains(surface.parent)).toBe(true);
      const editors = [...surface.parent.querySelectorAll<HTMLElement>('.cm-editor')];
      // An inline diff and a file have one editor, and side by side the head's is the second.
      const editor = EditorView.findFromDOM(editors[side === 'head' && editors.length === 2 ? 1 : 0]!)!;
      return editor.state.doc.line(line).text;
   });
}

/** Waits for the editor of the file's side in the center to take the focus at the line, and returns the text the editor shows at that line. */
export async function centerFocusedLine(focus: FocusSpy, view: 'Diff' | 'File', path: string, side: 'base' | 'head', line: number): Promise<string> {
   return focusedLine(
      focus,
      () => {
         const main = screen.getByRole('main', { name: view });
         // The toolbar's path is the first code element of a single file's view.
         expect(main.querySelector('code')?.textContent).toBe(path);
         return main;
      },
      side,
      line,
   );
}
