// The Threads panel's thread list and thread view, as the app flow tests drive them: a thread's row, the thread view that shows, and the moves between them.
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { expect } from 'vitest';

const panel = () => screen.getByRole('region', { name: 'Threads' });

/** The row of the thread in the thread list, or null while the list does not show it. */
export const threadRow = (threadId: string) => panel().querySelector<HTMLElement>(`li[data-thread-id="${threadId}"]`);

/** The IDs of the threads that the thread list shows, in its order, or none while a thread view shows. */
export const shownIds = () => [...panel().querySelectorAll<HTMLElement>('li[data-thread-id]')].map((row) => row.dataset.threadId!);

/** The thread view that shows, or null while the thread list shows. */
export const threadViewElement = () => panel().querySelector<HTMLElement>('article[data-thread-id]');

/** The thread view that shows. It fails while the thread list shows. */
export function threadView() {
   const shown = threadViewElement();
   expect(shown, 'no thread view shows').not.toBeNull();
   return within(shown!);
}

/**
 * Opens a thread's thread view from its row, the newest thread's when no ID is given, and returns the view once it shows. A thread view of that thread that shows already stays, and one of
 * another thread goes back to the list first.
 */
export async function openThreadView(threadId?: string) {
   const shown = threadViewElement();
   if (shown !== null && (threadId === undefined || shown.dataset.threadId === threadId)) {
      return within(shown);
   }

   if (shown !== null) {
      await backToList();
   }

   const row = await waitFor(() => {
      const found = threadId === undefined ? panel().querySelector<HTMLElement>('li[data-thread-id]') : threadRow(threadId);
      expect(found, `no row of thread ${threadId ?? '(any)'} in the thread list`).not.toBeNull();
      return found!;
   });
   const wanted = row.dataset.threadId;
   await fireEvent.click(within(row).getAllByRole('button')[0]!);
   return within(
      await waitFor(() => {
         const opened = threadViewElement();
         expect(opened?.dataset.threadId).toBe(wanted);
         return opened!;
      }),
   );
}

/** Goes back from the thread view to the thread list with the back arrow, and waits for the list. */
export async function backToList(): Promise<void> {
   await fireEvent.click(threadView().getByRole('button', { name: 'Back to all threads' }));
   await waitFor(() => expect(threadViewElement()).toBeNull());
}

/** Chooses an item of the thread view's menu, by its name, and waits for the menu to close. */
export async function chooseFromThreadMenu(name: string): Promise<void> {
   await fireEvent.keyDown(threadView().getByRole('button', { name: 'More thread actions' }), { key: 'Enter' });
   const menu = await screen.findByRole('menu', { hidden: true });
   await fireEvent.click(within(menu).getByRole('menuitem', { name, hidden: true }));
   await waitFor(() => expect(screen.queryByRole('menu', { hidden: true })).toBeNull());
}
