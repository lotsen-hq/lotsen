import { ViewPlugin } from '@codemirror/view';
import { isElement } from '../dom';

/** What a press on a widget leaves to the widget's own controls: buttons, links, form fields and anything else that takes focus. */
const interactive = 'a, button, input, select, textarea, [role="link"], [role="button"], [tabindex]';

/** Returns the text position at a point of the document's window, or null where there is none. */
function caretAt(document: Document, x: number, y: number): { node: Node; offset: number } | null {
   if (typeof document.caretPositionFromPoint === 'function') {
      const position = document.caretPositionFromPoint(x, y);
      return position === null ? null : { node: position.offsetNode, offset: position.offset };
   }

   const range = typeof document.caretRangeFromPoint === 'function' ? document.caretRangeFromPoint(x, y) : null;
   return range === null ? null : { node: range.startContainer, offset: range.startOffset };
}

/**
 * Lets a mouse drag select the text of the inline view's deleted lines and of an inline explanation, so the reviewer can ask about it.
 *
 * Both are widgets, non-editable islands in the editable content, and Chromium keeps a drag that starts in one from extending the selection, so such a drag sets the selection itself.
 * A double or triple click keeps the browser's own selection.
 */
export const islandSelection = ViewPlugin.define((view) => {
   /** Ends the drag in progress, or is null when none is. */
   let endDrag: (() => void) | null = null;
   const press = (event: MouseEvent) => {
      const target = isElement(event.target) ? event.target : null;
      if (target === null) {
         return;
      }

      const island = target.closest('.cm-deletedChunk, .lotsen-explanation');
      const control = target.closest(interactive);
      if (event.button !== 0 || event.detail > 1 || !island || !view.contentDOM.contains(island) || (control && island.contains(control))) {
         return;
      }

      // The editor's document is a popout window's while its panel is popped out, and that window has its own selection and coordinates.
      const document = view.contentDOM.ownerDocument;
      const anchor = caretAt(document, event.clientX, event.clientY);
      if (anchor === null) {
         return;
      }

      event.preventDefault();
      endDrag?.();
      const selection = document.getSelection();
      selection?.collapse(anchor.node, anchor.offset);
      const move = (moved: MouseEvent) => {
         const focus = caretAt(document, moved.clientX, moved.clientY);
         if (focus !== null) {
            selection?.setBaseAndExtent(anchor.node, anchor.offset, focus.node, focus.offset);
         }
      };
      const release = () => {
         document.removeEventListener('mousemove', move);
         document.removeEventListener('mouseup', release);
         endDrag = null;
      };
      document.addEventListener('mousemove', move);
      document.addEventListener('mouseup', release);
      endDrag = release;
   };

   view.contentDOM.addEventListener('mousedown', press);
   return {
      destroy: () => {
         view.contentDOM.removeEventListener('mousedown', press);
         endDrag?.();
      },
   };
});
