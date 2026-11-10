import type { EditorView } from '@codemirror/view';

/**
 * Returns the nearest element around an editor's content that scrolls now: the editor's own scroller, or the merge view or the stack around an editor that grows to its content.
 *
 * @returns The element, or null when no element around the editor has more content than it shows.
 */
export function scrollParent(view: EditorView): HTMLElement | null {
   let element: HTMLElement | null = view.scrollDOM;
   while (element !== null && !(element.scrollHeight > element.clientHeight && ['auto', 'scroll'].includes(getComputedStyle(element).overflowY))) {
      element = element.parentElement;
   }

   return element;
}
