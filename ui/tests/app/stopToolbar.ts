// The stop toolbar of the Stop panel, as the app flow tests drive it: the Ask Claude menu and the ⋯ menu.
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { expect } from 'vitest';
import { isItemDisabled } from './planMenu';
import { inMenu } from './threadFilter';

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));

/** The names of the stop toolbar's menu buttons. */
export type StopMenu = 'Ask Claude' | 'More stop actions';

/** The stop toolbar's menu button of the name, or null while the stop toolbar has none. */
export const stopMenuButton = (name: StopMenu) => stopPanel().queryByRole('button', { name });

/** Opens the stop toolbar's menu of the name once its button shows, and returns the menu. */
export async function openStopMenu(name: StopMenu): Promise<HTMLElement> {
   const button = await waitFor(() => stopPanel().getByRole('button', { name }));
   await fireEvent.keyDown(button, { key: 'Enter' });
   return screen.findByRole('menu', inMenu);
}

/** Closes the menu that shows. */
export async function closeStopMenu(): Promise<void> {
   await fireEvent.keyDown(screen.getByRole('menu', inMenu), { key: 'Escape' });
   await waitFor(() => expect(screen.queryByRole('menu', inMenu)).toBeNull());
}

/** The item of the menu that shows, by its name, as a plain item or a version's radio item. */
const stopMenuItem = (menu: HTMLElement, name: string, role: 'menuitem' | 'menuitemradio' = 'menuitem') => within(menu).getByRole(role, { name, hidden: true });

/** Chooses an item of the stop toolbar's menu of the name, by the item's name, and waits for the menu to close. */
export async function chooseFromStopMenu(name: StopMenu, item: string, role: 'menuitem' | 'menuitemradio' = 'menuitem'): Promise<void> {
   const menu = await openStopMenu(name);
   await fireEvent.click(await within(menu).findByRole(role, { name: item, hidden: true }));
   await waitFor(() => expect(screen.queryByRole('menu', inMenu)).toBeNull());
}

/** Opens the stop toolbar's menu of the name, waits until it holds the item, and closes it again. */
export async function expectStopMenuItem(name: StopMenu, item: string): Promise<void> {
   const menu = await openStopMenu(name);
   await within(menu).findByRole('menuitem', { name: item, hidden: true });
   await closeStopMenu();
}

/** Opens the stop toolbar's menu of the name, checks that it lacks the item, and closes it again. */
export async function expectNoStopMenuItem(name: StopMenu, item: string): Promise<void> {
   const menu = await openStopMenu(name);
   expect(within(menu).queryByRole('menuitem', { name: item, hidden: true })).toBeNull();
   await closeStopMenu();
}

/** Opens the ⋯ menu, and returns the names of its version items and the name of the checked one, then closes it again. */
export async function stopVersionItems(): Promise<{ names: (string | undefined)[]; checked: string | undefined }> {
   const menu = await openStopMenu('More stop actions');
   const items = within(menu).queryAllByRole('menuitemradio', inMenu);
   const shown = { names: items.map((item) => item.textContent?.trim()), checked: items.find((item) => item.getAttribute('aria-checked') === 'true')?.textContent?.trim() };
   await closeStopMenu();
   return shown;
}

/** Waits until the Ask Claude item of the action is enabled, then chooses it. */
export async function chooseStopAction(action: 'Explain deeper' | 'Revise'): Promise<void> {
   await waitFor(() => expect(stopMenuButton('Ask Claude')?.hasAttribute('disabled')).toBe(false));
   const menu = await openStopMenu('Ask Claude');
   const item = stopMenuItem(menu, `${action}…`);
   if (isItemDisabled(item)) {
      await closeStopMenu();
      throw new Error(`The Ask Claude item ${action} is disabled.`);
   }

   await fireEvent.click(item);
   await waitFor(() => expect(screen.queryByRole('menu', inMenu)).toBeNull());
}
