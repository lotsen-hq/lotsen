// The Threads panel's thread filter, as the app flow tests drive it: the Show and the Where buttons and their menus.
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { expect } from 'vitest';

const panel = () => within(screen.getByRole('region', { name: 'Threads' }));

export const showButton = () => panel().getByRole('button', { name: /^Show:/ });
export const whereButton = () => panel().getByRole('button', { name: /^Where:/ });
/** What the Show button names as shown, such as "Open, tour actions". */
export const shownOnButton = () =>
   showButton()
      .textContent?.replace(/\s+/g, ' ')
      .trim()
      .replace(/^Show: /, '');
/** Where the Where button says the threads are about, such as "Anywhere". */
export const whereOnButton = () =>
   whereButton()
      .textContent?.replace(/\s+/g, ' ')
      .trim()
      .replace(/^Where: /, '');

// Floating UI hides an open menu once it finds that jsdom lays out nothing, so the queries inside a menu include hidden elements.
export const inMenu = { hidden: true } as const;

/** Opens the menu of a filter button with the keyboard, and returns the menu. */
export async function openMenu(button: HTMLElement): Promise<HTMLElement> {
   await fireEvent.keyDown(button, { key: 'Enter' });
   return screen.findByRole('menu', inMenu);
}

export async function closeMenu(menu: HTMLElement) {
   await fireEvent.keyDown(menu, { key: 'Escape' });
   await waitFor(() => expect(screen.queryByRole('menu', inMenu)).toBeNull());
}

export const itemName = (item: HTMLElement) => item.textContent!.replace(/\s+/g, ' ').trim();

/** Returns the item of an open Show menu whose text starts with `name`. */
export function showItem(menu: HTMLElement, name: string): HTMLElement {
   const item = within(menu)
      .getAllByRole('menuitemcheckbox', inMenu)
      .find((candidate) => itemName(candidate).startsWith(name));
   expect(item, `no item "${name}" in the Show menu`).toBeDefined();
   return item!;
}

/** Toggles the item of the Show menu whose text starts with `name`, then closes the menu. */
export async function toggleShown(name: string) {
   const menu = await openMenu(showButton());
   await fireEvent.click(showItem(menu, name));
   await closeMenu(menu);
}

/** Opens the Where menu, and returns each choice's text, its count included, with whether it holds the check, in order. */
export async function whereChoices(): Promise<{ name: string; checked: boolean }[]> {
   const menu = await openMenu(whereButton());
   const choices = within(menu)
      .getAllByRole('menuitemradio', inMenu)
      .map((item) => ({ name: itemName(item), checked: item.getAttribute('aria-checked') === 'true' }));
   await closeMenu(menu);
   return choices;
}

/** Chooses the place of the Where menu whose text starts with `name`, and waits for the menu to close. */
export async function chooseWhere(name: string) {
   const menu = await openMenu(whereButton());
   const item = within(menu)
      .getAllByRole('menuitemradio', inMenu)
      .find((candidate) => itemName(candidate).startsWith(name));
   expect(item, `no place "${name}" in the Where menu`).toBeDefined();
   await fireEvent.click(item!);
   await waitFor(() => expect(screen.queryByRole('menu', inMenu)).toBeNull());
}
