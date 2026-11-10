import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { expect } from 'vitest';

/** The accessible name of the tour switcher in the top bar, with a tour selected or without one. */
export const tourSwitcherName = /^(Choose a tour|Tour: .+, choose another)$/;

/** Opens the tour switcher in the top bar unless it is open, and returns its list. */
export async function openTourList() {
   const picker = await screen.findByRole('button', { name: tourSwitcherName });
   // A choice in the list closes the picker, so wait until it is open or its list is gone.
   await waitFor(() => expect(picker.getAttribute('aria-expanded') === 'true' || screen.queryByRole('navigation', { name: 'Tours' }) === null).toBe(true));
   if (picker.getAttribute('aria-expanded') !== 'true') {
      await fireEvent.click(picker);
   }

   return within(await screen.findByRole('navigation', { name: 'Tours' }));
}

/** Matches a tour's button in the tour list, and not the archive and delete buttons beside it. */
export const tourButton = { name: (name: string) => !/^(Archive|Unarchive|Delete) this tour$/.test(name) };
