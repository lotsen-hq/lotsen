// The ⋯ menu of the Plan panel and its Re-plan form, as the app flow tests drive them.
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { expect } from 'vitest';
import { planPanel } from './appPanels';

// Floating UI hides an open menu once it finds that jsdom lays out nothing, so the queries inside a menu include hidden elements.
const inMenu = { hidden: true } as const;

/** The section with the familiarity line, its menu, its reasons, its waiting status and its forms. */
export const planActions = () => within(planPanel().getByRole('region', { name: 'Plan actions' }));

/** The ⋯ button, or null while the line has no menu. */
export const planMenuButton = () => planActions().queryByRole('button', { name: 'Plan actions' });

/** Opens the ⋯ menu once the line shows, and returns the menu. */
export async function openPlanMenu(): Promise<HTMLElement> {
   const button = await waitFor(() => planActions().getByRole('button', { name: 'Plan actions' }));
   await fireEvent.keyDown(button, { key: 'Enter' });
   return screen.findByRole('menu', inMenu);
}

/** Closes the ⋯ menu that shows. */
export async function closePlanMenu(): Promise<void> {
   await fireEvent.keyDown(screen.getByRole('menu', inMenu), { key: 'Escape' });
   await waitFor(() => expect(screen.queryByRole('menu', inMenu)).toBeNull());
}

/** The names of the items of the ⋯ menu that shows. */
export const planMenuNames = (menu: HTMLElement) =>
   within(menu)
      .getAllByRole('menuitem', inMenu)
      .map((item) => item.textContent);

/** The item of the ⋯ menu that shows, by its name. */
export const planMenuItem = (menu: HTMLElement, name: string) => within(menu).getByRole('menuitem', { name, hidden: true });

/** Chooses an item of the ⋯ menu, by its name, and waits for the menu to close. */
export async function chooseFromPlanMenu(name: string): Promise<void> {
   const menu = await openPlanMenu();
   await fireEvent.click(planMenuItem(menu, name));
   await waitFor(() => expect(screen.queryByRole('menu', inMenu)).toBeNull());
}

/** Returns whether the item is disabled. */
export const isItemDisabled = (item: HTMLElement) => item.hasAttribute('data-disabled');

/** The text that describes why the control is disabled. */
export function reasonOf(control: HTMLElement): string | null {
   return document.getElementById(control.getAttribute('aria-describedby') ?? '')?.textContent ?? null;
}

/** The form's radio option of a familiarity level, by the start of its name. */
export function levelOption(name: string): HTMLButtonElement {
   const option = planActions()
      .getAllByRole('radio')
      .find((radio) => (radio.closest('div')?.textContent ?? '').trim().replace(/\s+/g, ' ').startsWith(name));
   expect(option, `no radio option "${name}"`).toBeDefined();
   return option as HTMLButtonElement;
}

/** Whether the radio option is selected. */
export const isSelected = (option: HTMLElement) => option.getAttribute('aria-checked') === 'true';

/** The form's Send to Claude button. */
export const sendButton = () => planActions().getByRole('button', { name: 'Send to Claude' }) as HTMLButtonElement;

/** The form's words box, by its label. */
export const wordsBox = (name: string | RegExp) => planActions().getByRole('textbox', { name }) as HTMLTextAreaElement;

/** Writes the words into the form's box of the label. */
export async function writeWords(name: string | RegExp, value: string): Promise<void> {
   await fireEvent.input(wordsBox(name), { target: { value } });
}

/** Changes the level through the Re-plan form: picks the level by its full name, with the words if any, and sends. */
export async function sendLevelChange(level: string, words?: string): Promise<void> {
   await chooseFromPlanMenu('Re-plan…');
   await fireEvent.click(levelOption(level));
   if (words !== undefined) {
      await writeWords(/^Anything else Claude should change\?/, words);
   }

   await fireEvent.click(sendButton());
}
