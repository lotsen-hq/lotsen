// The panels and the page state that the app flow tests split from the app's old test file share.
import { screen, within } from '@testing-library/svelte';
import { beforeEach } from 'vitest';
import type { Template, World } from '../backend/backend';
import { backend } from '../harness';

/** Clears the browser storage and the address before each test of the file that calls it, and gives elements a scrollIntoView, which jsdom lacks. */
export function resetPageEachTest(): void {
   beforeEach(() => {
      sessionStorage.clear();
      localStorage.clear();
      history.replaceState(null, '', '/');
      Element.prototype.scrollIntoView = () => {};
      return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
   });
}

/** Returns a new world copied from the template, with what the template built. */
export async function worldOf<T>(template: Template<T>): Promise<{ world: World; seed: T }> {
   const world = await backend().world({ template });
   return { world, seed: world.seed as T };
}

export function planPanel() {
   return within(screen.getByRole('tabpanel', { name: 'Plan' }));
}

/** Returns the Files tab's panel once it shows, because a click turns the tab only on the next update. */
export async function filesPanel() {
   return within(await screen.findByRole('tabpanel', { name: 'Files' }));
}

export function tab(name: 'Plan' | 'Files') {
   return screen.getByRole('tab', { name });
}
