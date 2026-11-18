import { fireEvent, waitFor } from '@testing-library/svelte';
import { expect } from 'vitest';

/** Points at the element and returns the text of the tooltip that opens for it, after the tooltip's delay. */
export async function hoverTooltip(element: Element): Promise<string> {
   await fireEvent.pointerEnter(element, { pointerType: 'mouse' });
   return openTooltip(element);
}

/** Returns the text of the tooltip that describes the element, once one does. */
export async function openTooltip(element: Element): Promise<string> {
   const id = await waitFor(() => {
      const described = element.getAttribute('aria-describedby');
      expect(described).toBeTruthy();
      return described!;
   });
   return document.getElementById(id)?.textContent?.trim() ?? '';
}

/** Points at an element whose own description replaces the tooltip's, and returns the text of the tooltip that opens for it. */
export async function hoverOwnTooltip(element: Element): Promise<string> {
   await fireEvent.pointerEnter(element, { pointerType: 'mouse' });
   const content = await waitFor(() => {
      const shown = document.querySelector('[data-slot="tooltip-content"]');
      expect(shown).not.toBeNull();
      return shown!;
   });
   return content.textContent.trim();
}
