import { createContext, untrack } from 'svelte';

/** The document and window of the main page or of a panel, which are what a component reads selections, focus and frames from and listens on. */
export type DocumentPlace = Pick<PanelDocument, 'document' | 'window'>;

/** The panel document of one workspace panel, with the portal target that the panel's menus, tooltips and dialogs render into. */
export class PanelDocument {
   /** The element at the end of the panel document's body that the panel's menus, tooltips and dialogs render into. */
   readonly portalTarget: HTMLElement;

   private readonly element: HTMLElement;
   private current = $state.raw(document);

   /** @param element - The panel's element, which Dockview moves between the main page and a popout window. */
   constructor(element: HTMLElement) {
      this.element = element;
      // bits-ui's Portal refuses a target that fails `instanceof Element` in a development build (portal.svelte in bits-ui 2.20.2), and an element that a popout window's document
      // creates fails it, so the main page's document creates the target.
      this.portalTarget = document.createElement('div');
      this.follow();
   }

   /** The document that holds the panel's element. */
   get document(): Document {
      return this.current;
   }

   /**
    * The window of the document that holds the panel's element, or the main page's window once that document's window has closed.
    *
    * @remarks The panel's ResizeObservers come from this window, because one from the main page's window misses resizes in a popout window (observeGridviewSize in dockview-core
    * 8.9.0's popoutWindowService.ts).
    */
   get window(): Window & typeof globalThis {
      return this.current.defaultView ?? window;
   }

   /** Moves the portal target into the body of the document that holds the panel's element now. Call it after Dockview moves the panel into a popout window or back. */
   follow(): void {
      const owner = this.element.ownerDocument;
      this.current = owner;
      if (this.portalTarget.parentNode !== owner.body) {
         owner.body.append(this.portalTarget);
      }
   }

   /** Removes the portal target from its document. */
   dispose(): void {
      this.portalTarget.remove();
   }
}

const [panelContext, providePanel, inPanel] = createContext<PanelDocument>();

const mainPage: DocumentPlace = {
   get document() {
      return document;
   },
   get window() {
      return window;
   },
};

/** Gives the components below the panel document of the panel that they render in. Call it while the component initializes. */
export const providePanelDocument: (panel: PanelDocument) => void = providePanel;

/** Returns the panel document of the panel that the calling component renders in, or the main page's document and window outside a panel. Call it while the component initializes. */
export function panelDocument(): DocumentPlace {
   return inPanel() ? panelContext() : mainPage;
}

/** Returns whether an overlay that shows in the place may lock the page's scroll, which bits-ui's lock does on the main page's body wherever the overlay shows (body-scroll-lock.svelte.js in bits-ui 2.20.2). */
export function locksScroll(place: DocumentPlace): boolean {
   return place.document === document;
}

/**
 * Calls `close` each time the calling component's panel moves to another document, such as when its popout window closes. An open overlay's listeners stay on the document that it opened in,
 * so its owner closes it then. Call it while the component initializes.
 */
export function closeOnMove(close: () => void): void {
   const place = panelDocument();
   let shown = untrack(() => place.document);
   $effect(() => {
      const current = place.document;
      if (current !== shown) {
         shown = current;
         untrack(close);
      }
   });
}
