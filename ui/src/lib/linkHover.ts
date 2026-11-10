import type { CodeReference } from './types';

/** What makes a `ref:` link hovered: the pointer over it, or visible keyboard focus on it. */
export type HoverSource = 'pointer' | 'focus';

interface Entered {
   link: object;
   reference: CodeReference | null;
}

/**
 * Keeps the page's hovered reference, which every block of prose reports the links that the pointer and the keyboard focus enter and leave to: the stop's, inline and in the Stop panel, and the replies'.
 *
 * The page has one, so the pointer's link wins over the focused one wherever each is. A link leaves only the source it entered, so one block cannot clear what another block's link holds.
 */
export class LinkHover {
   private readonly entered: Record<HoverSource, Entered | null> = { pointer: null, focus: null };
   private readonly onchange: (reference: CodeReference | null) => void;
   private reported: Entered | null = null;

   /** @param onchange Receives the hovered reference each time it changes, or null when no link is hovered. */
   constructor(onchange: (reference: CodeReference | null) => void) {
      this.onchange = onchange;
   }

   /** Records that a source entered a link, with the link's code reference, or null when its attributes are malformed. */
   enter(source: HoverSource, link: object, reference: CodeReference | null): void {
      this.entered[source] = { link, reference };
      this.report();
   }

   /** Records that a source left a link. It does nothing when the source is on another link. */
   leave(source: HoverSource, link: object): void {
      if (this.entered[source]?.link === link) {
         this.entered[source] = null;
         this.report();
      }
   }

   private report(): void {
      const hovered = this.entered.pointer ?? this.entered.focus;
      if (hovered !== this.reported) {
         this.reported = hovered;
         this.onchange(hovered?.reference ?? null);
      }
   }
}
