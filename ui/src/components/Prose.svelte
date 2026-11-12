<!--
   The one place the page inserts HTML: prose that the server rendered. `eslint.config.js` allows `{@html}` in this component alone, and only of the `html` prop.
   A `ref:` link's anchor has no href, so the component makes it focusable and opens its code reference on a click or Enter. A link that is not at the pin does not open and is never hovered.
   It marks the links into the focused passage with a class, and reports the links that the pointer and visible keyboard focus enter and leave.
-->
<script lang="ts">
   import { onDestroy } from 'svelte';
   import type { ClassValue } from 'svelte/elements';
   import { isElement } from '../lib/dom';
   import type { HoverSource, LinkHover } from '../lib/linkHover';
   import { pointsInto } from '../lib/pin';
   import type { CodeReference } from '../lib/types';

   let {
      html,
      onopenreference,
      focusedPassage = null,
      hover = null,
      shown = true,
      atPin = null,
      field,
      class: className,
   }: {
      /** One prose field of `StopHtml`, or a reply's `html`, as the server rendered it. */
      html: string;
      onopenreference: (reference: CodeReference) => void;
      /** The focused passage, whose links get the `lotsen-ref-focused` class, or null for none. */
      focusedPassage?: CodeReference | null;
      /** What the links that the pointer or the keyboard focus enter and leave are reported to, or null for nothing. */
      hover?: LinkHover | null;
      /** Whether the prose is visible. A hidden link stops being hovered. */
      shown?: boolean;
      /** Returns whether a link's code reference is at the pin, or null when every link is. */
      atPin?: ((reference: CodeReference) => boolean) | null;
      /** The stop's prose field that the HTML renders, as a stop text anchor names it, such as `how` or `checks[<check ID>].question`, or undefined for a reply. */
      field?: string;
      class?: ClassValue;
   } = $props();

   const referenceSelector = 'a[data-ref-path]';
   const focusedClass = 'lotsen-ref-focused';

   let container: HTMLElement;
   /** The anchor that each source is on and that {@link hover} holds for this prose. */
   const held: Record<HoverSource, HTMLElement | null> = { pointer: null, focus: null };

   // The effect runs after Svelte has replaced the inserted nodes, so it marks the anchors of the current HTML.
   $effect(() => {
      void html;
      for (const anchor of container.querySelectorAll<HTMLElement>(referenceSelector)) {
         anchor.setAttribute('role', 'link');
         anchor.setAttribute('tabindex', '0');
         // aria-disabled keeps the link in the tab order, as a passage that does not open stays there.
         if (earlier(anchor)) {
            anchor.setAttribute('aria-disabled', 'true');
            anchor.setAttribute('aria-label', `${anchor.textContent}, at an earlier commit`);
         } else {
            anchor.removeAttribute('aria-disabled');
            anchor.removeAttribute('aria-label');
         }
      }

      // New HTML removes a held anchor without a mouseout or a focusout, and a link that leaves the pin keeps neither.
      for (const source of ['pointer', 'focus'] as const) {
         if (held[source] !== null && (!container.contains(held[source]) || earlier(held[source]))) {
            hold(source, null);
         }
      }
   });

   $effect(() => {
      void html;
      const focused = focusedPassage;
      for (const anchor of container.querySelectorAll<HTMLElement>(referenceSelector)) {
         const reference = anchorReference(anchor);
         anchor.classList.toggle(focusedClass, focused !== null && reference !== null && !earlier(anchor) && pointsInto(reference, focused));
      }
   });

   $effect(() => {
      if (!shown) {
         release();
      }
   });

   // The prose goes when its panel does, as when the stop changes.
   onDestroy(release);

   function release(): void {
      hold('pointer', null);
      hold('focus', null);
   }

   /** Moves a source to an anchor of this prose, or off every anchor with null, and reports the change to {@link hover}. */
   function hold(source: HoverSource, anchor: HTMLElement | null): void {
      const current = held[source];
      if (anchor === current) {
         return;
      }

      if (current !== null) {
         hover?.leave(source, current);
      }

      held[source] = anchor;
      if (anchor !== null) {
         hover?.enter(source, anchor, anchorReference(anchor));
      }
   }

   /** Returns the `ref:` anchor of this prose that holds a node, or null when the node is in none or in one that is not at the pin. */
   function anchorAt(target: EventTarget | null): HTMLElement | null {
      const anchor = isElement(target) ? target.closest<HTMLElement>(referenceSelector) : null;
      return anchor !== null && container.contains(anchor) && !earlier(anchor) ? anchor : null;
   }

   /** Returns whether the code reference of a `ref:` anchor is not at the pin. */
   function earlier(anchor: HTMLElement): boolean {
      const reference = anchorReference(anchor);
      return atPin !== null && reference !== null && !atPin(reference);
   }

   // A click focuses the anchor too, and only focus from the keyboard is visible, so a link that the pointer left stops being hovered.
   function focus(target: EventTarget | null): void {
      const anchor = anchorAt(target);
      hold('focus', anchor !== null && anchor.matches(':focus-visible') ? anchor : null);
   }

   /** Returns the code reference of the `ref:` anchor that holds the event's target, or null when the target is in none or its attributes are malformed. */
   function referenceOf(event: Event): CodeReference | null {
      const anchor = anchorAt(event.target);
      return anchor === null ? null : anchorReference(anchor);
   }

   /** Returns the code reference of a `ref:` anchor, or null when its attributes are malformed. */
   function anchorReference(anchor: HTMLElement): CodeReference | null {
      const { refSide, refPath, refStartLine, refEndLine } = anchor.dataset;
      const startLine = Number(refStartLine);
      const endLine = Number(refEndLine);
      if ((refSide !== 'base' && refSide !== 'head') || !refPath || !Number.isInteger(startLine) || !Number.isInteger(endLine) || startLine < 1 || endLine < startLine) {
         return null;
      }

      return { side: refSide, path: refPath, startLine, endLine };
   }

   function open(event: Event): void {
      const reference = referenceOf(event);
      if (reference !== null) {
         event.preventDefault();
         onopenreference(reference);
      }
   }
</script>

<!-- The anchors inside take the clicks and the keys: the container only delegates them. -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
   bind:this={container}
   class={['markdown', className]}
   data-stop-field={field}
   onclick={open}
   onkeydown={(event) => event.key === 'Enter' && open(event)}
   onmouseover={(event) => hold('pointer', anchorAt(event.target))}
   onmouseout={(event) => hold('pointer', anchorAt(event.relatedTarget))}
   onfocusin={(event) => focus(event.target)}
   onfocusout={(event) => focus(event.relatedTarget)}
>
   {@html html}
</div>
