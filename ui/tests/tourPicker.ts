import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { expect } from 'vitest';

/** The accessible name of the tour list's trigger in the top bar, with a tour selected or without one. */
export const tourSwitcherName = /^Choose a tour$|, choose another tour$/;

/** Returns the top bar's tour switcher while it shows the tour with the title: a repository tour's title, or "Pull request" and the number of a pull request's tour. */
export function selectedTourSwitcher(title: string): HTMLElement {
   return within(screen.getByRole('banner')).getByRole('button', { name: `${title}, choose another tour` });
}

/** Returns the top bar's tour switcher while it shows the pull request's tour with the label, such as `shop #43`: the number on the bottom line and the repository on the top line. */
export function selectedPullRequestSwitcher(label: string): HTMLElement {
   const [, repository, number] = /^(.*) (#\d+)$/.exec(label)!;
   const switcher = selectedTourSwitcher(`Pull request ${number}`);
   expect(switcher.querySelector('[data-switcher-line="place"]')?.textContent.trim()).toBe(repository);
   return switcher;
}

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
export const tourButton = { name: (name: string) => !/^(Archive|Unarchive|Delete) this tour( and .+)?$/.test(name) };
