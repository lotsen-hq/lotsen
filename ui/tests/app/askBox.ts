// The Threads panel's ask box, as the app flow tests drive it: its chip, the chip's menu and the quote of a pick.
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { expect } from 'vitest';

const threadsPanel = () => within(screen.getByRole('region', { name: 'Threads' }));

/** The chip that names what the ask box asks about. */
export const aboutChip = () => threadsPanel().getByRole('button', { name: /^Ask about / });

/** Returns what the chip says the ask box asks about. */
export const askedAbout = () => aboutChip().textContent?.trim();

/** Opens the chip's menu, and returns each entry's text with whether it holds the check, in order. */
export async function aboutChoices(): Promise<{ name: string; checked: boolean }[]> {
   const menu = await openAboutMenu();
   const choices = within(menu)
      .getAllByRole('menuitemradio')
      .map((item) => ({ name: item.textContent!.replace(/\s+/g, ' ').trim(), checked: item.getAttribute('aria-checked') === 'true' }));
   await fireEvent.keyDown(menu, { key: 'Escape' });
   await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());
   return choices;
}

/** Chooses the entry of the chip's menu whose text starts with `name`, and waits for the menu to close. */
export async function askAbout(name: string): Promise<void> {
   const menu = await openAboutMenu();
   const item = within(menu)
      .getAllByRole('menuitemradio')
      .find((candidate) => candidate.textContent!.trim().startsWith(name));
   expect(item, `no entry "${name}" in the menu`).toBeDefined();
   await fireEvent.click(item!);
   await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());
}

async function openAboutMenu(): Promise<HTMLElement> {
   await fireEvent.keyDown(aboutChip(), { key: 'Enter' });
   return screen.findByRole('menu');
}

/** The quote of the pick that the ask box asks about, or null when it shows none. */
export const quote = () => threadsPanel().queryByRole('group', { name: /^The (selected text|file)$/ });
