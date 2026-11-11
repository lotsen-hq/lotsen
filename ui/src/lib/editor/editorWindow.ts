import { EditorView, ViewPlugin } from '@codemirror/view';
import { cspNonce } from '../cspNonce';

/** Returns the window of the document that holds a node now: the main page's, or a popout window's while the node's panel is popped out. */
export function windowOf(node: Node): Window & typeof globalThis {
   return node.ownerDocument?.defaultView ?? window;
}

/** The documents that hold CodeMirror's style element with their own nonce. */
const styledDocuments = new WeakSet<Document>();

/**
 * Gives the main page's document and the document that holds an element the style element that CodeMirror writes every editor's styles into, each with its own style nonce. Call it before an
 * editor mounts in the element or moves there.
 *
 * The editors carry no nonce of their own, because a popout window's page has another nonce than the main page and an editor moves between them. CodeMirror creates the element with the nonce
 * of the first editor that mounts its styles in the document, and an editor without a nonce leaves the element's nonce as it is (StyleModule.mount in style-mod 4.1.4).
 */
export function prepareStyles(element: Element): void {
   for (const target of [document, element.ownerDocument]) {
      if (!styledDocuments.has(target)) {
         styledDocuments.add(target);
         const nonce = cspNonce(target);
         new EditorView({ root: target, extensions: nonce === undefined ? [] : EditorView.cspNonce.of(nonce) }).destroy();
      }
   }
}

/**
 * Watches the size of an editor's scroller, and of the elements that the editor's extensions register, with a ResizeObserver of the window that holds the editor.
 *
 * CodeMirror observes its scroller from the window that it mounted in, and `setRoot` keeps that observer, which misses resizes in another window (see `PanelDocument.window`). So a resize of
 * the scroller measures the editor again here.
 */
class EditorWindow {
   private readonly view: EditorView;
   /** The elements that the editor's extensions watch, each with the function that a resize calls. */
   private readonly watched = new Map<Element, () => void>();
   private window: Window & typeof globalThis;
   private resizes: ResizeObserver | null = null;

   constructor(view: EditorView) {
      this.view = view;
      this.window = windowOf(view.dom);
      this.watched.set(view.scrollDOM, () => view.requestMeasure());
      this.observe();
   }

   /** Calls `resized` after each resize of the element, until the returned function stops it. */
   watch(element: Element, resized: () => void): () => void {
      this.watched.set(element, resized);
      this.resizes?.observe(element);
      return () => {
         this.watched.delete(element);
         this.resizes?.unobserve(element);
      };
   }

   /** Watches from the window that holds the editor now. Call it after the editor's element moves to another document. */
   follow(): void {
      const current = windowOf(this.view.dom);
      if (current !== this.window) {
         this.resizes?.disconnect();
         this.window = current;
         this.observe();
      }
   }

   destroy(): void {
      this.resizes?.disconnect();
      this.resizes = null;
   }

   private observe(): void {
      const ResizeObserver = this.window.ResizeObserver;
      this.resizes = typeof ResizeObserver === 'function' ? new ResizeObserver((entries) => entries.forEach((entry) => this.watched.get(entry.target)?.())) : null;
      this.watched.forEach((_, element) => this.resizes?.observe(element));
   }
}

/** The plugin that watches an editor's elements from the window that holds the editor, which {@link followWindow} moves along. */
export const editorWindow = ViewPlugin.fromClass(EditorWindow);

/**
 * Moves an editor's root, its window listeners and its size watching to the document that holds its element now. Call it after the editor's element moves to another document, as into a
 * popout window and back.
 *
 * CodeMirror's other observers stay in the window that the editor mounted in.
 */
export function followWindow(view: EditorView): void {
   prepareStyles(view.dom);
   // CodeMirror reads the selection and the focus from its root, and listens for selection changes, resizes and scrolls on the root's window (setRoot in @codemirror/view 6.43.15).
   view.setRoot(view.dom.ownerDocument);
   view.plugin(editorWindow)?.follow();
}
