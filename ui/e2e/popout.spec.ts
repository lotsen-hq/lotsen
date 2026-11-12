import { expect, test, type Page } from '@playwright/test';
import { checkoutTour, openDemoTour, openStop } from './demo';

const region = (page: Page, name: string) => page.getByRole('region', { name, exact: true });

/** Returns the tabs of each group that shows, in document order. */
const groups = (page: Page) => page.getByRole('tablist').evaluateAll((lists) => lists.map((list) => [...list.querySelectorAll('[role="tab"]')].map((tab) => tab.getAttribute('aria-label'))));

/** Pops the panel out from its group header, and returns its popout window once the panel shows there. */
async function popOut(page: Page, name: string, button: string): Promise<Page> {
   const opened = page.waitForEvent('popup');
   await page.getByRole('button', { name: button }).click();
   const popout = await opened;
   await expect(region(popout, name)).toBeVisible();
   return popout;
}

/** Returns the pointer-events value of the page's body, which a modal menu or dialog sets to none while it locks the page. */
const bodyPointer = (page: Page) => page.locator('body').evaluate((body) => getComputedStyle(body).pointerEvents);

/** Returns the row of the demo's thread on the discount in the Threads panel of the page. */
const discountRow = (page: Page) => region(page, 'Threads').getByRole('list', { name: 'Threads' }).getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' });

test.describe('a popout window', () => {
   test('runs a click in the Threads panel that it holds, and returns the panel to its group once the window closes', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { theme: 'dark' });
      const opened = page.waitForEvent('popup');

      await page.getByRole('button', { name: 'Pop out the threads' }).click();

      const popout = await opened;
      const threads = region(popout, 'Threads');
      await expect(threads).toBeVisible();
      await expect(region(page, 'Threads')).toBeHidden();
      await expect(popout.locator('html')).toHaveClass(/\bdark\b/);
      await expect(threads.getByRole('button', { name: /^Pop out/ })).toHaveCount(0);

      const row = threads.getByRole('list', { name: 'Threads' }).getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' });
      await row.getByRole('button').first().click();
      await expect(threads.getByRole('button', { name: 'Back to all threads' })).toBeVisible();

      await popout.close({ runBeforeUnload: true });

      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
      await expect(region(page, 'Threads').getByRole('button', { name: 'Back to all threads' })).toBeVisible();
   });

   test('keeps the panel on Delete or Backspace on its tab, in the popout window and back in the main window', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const opened = page.waitForEvent('popup');
      await page.getByRole('button', { name: 'Pop out the threads' }).click();
      const popout = await opened;
      const tab = popout.getByRole('tab', { name: 'Threads' });
      await expect(tab).toBeVisible();

      for (const key of ['Delete', 'Backspace']) {
         await tab.press(key);
      }

      await expect(region(popout, 'Threads')).toBeVisible();
      await popout.close({ runBeforeUnload: true });
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);

      for (const key of ['Delete', 'Backspace']) {
         await page.getByRole('tab', { name: 'Threads' }).press(key);
      }

      expect(await groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
   });

   test("shows a thread's menu in its own window, closes it there with Escape and an outside click, and shows it in the main window once the panel returns", async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      const actions = discountRow(popout).getByRole('button', { name: 'Thread actions' });

      await actions.click();

      await expect(popout.getByRole('menu')).toHaveCount(1);
      await expect(popout.getByRole('menuitem', { name: 'Delete thread…' })).toBeInViewport();
      await expect(popout.getByRole('menu')).toBeFocused();
      await expect(page.getByRole('menu')).toHaveCount(0);
      expect(await bodyPointer(page)).not.toBe('none');
      await popout.keyboard.press('ArrowDown');
      await expect(popout.getByRole('menuitem').first()).toBeFocused();
      await popout.keyboard.press('Escape');
      await expect(popout.getByRole('menu')).toHaveCount(0);
      await expect(actions).toBeFocused();

      await actions.click();
      await expect(popout.getByRole('menu')).toHaveCount(1);
      await popout.getByRole('tab', { name: 'Threads' }).click();
      await expect(popout.getByRole('menu')).toHaveCount(0);

      await discountRow(popout).click({ button: 'right' });
      await expect(popout.getByRole('menu')).toHaveAttribute('data-context-menu-content');
      await popout.keyboard.press('Escape');
      await expect(popout.getByRole('menu')).toHaveCount(0);

      await popout.close({ runBeforeUnload: true });
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
      await discountRow(page).getByRole('button', { name: 'Thread actions' }).click();
      await expect(page.getByRole('menu')).toHaveCount(1);
      await expect(page.getByRole('menuitem', { name: 'Delete thread…' })).toBeInViewport();
      await expect.poll(() => bodyPointer(page)).toBe('none');
   });

   test('shows a tooltip in its own window beside its trigger, and closes it there with Escape', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      const threads = region(popout, 'Threads');
      await threads.getByRole('textbox', { name: 'Question for Claude' }).fill('A note to read later.');
      const save = threads.getByRole('button', { name: 'Save as note' });

      await save.hover();

      await expect(save).toHaveAttribute('aria-describedby', /.+/);
      const id = (await save.getAttribute('aria-describedby'))!;
      const tooltip = popout.locator(`[id="${id}"]`);
      await expect(tooltip).toHaveText('Save as note');
      await expect(page.locator(`[id="${id}"]`)).toHaveCount(0);
      const [anchor, tip] = [(await save.boundingBox())!, (await tooltip.boundingBox())!];
      expect(Math.abs(tip.x + tip.width / 2 - (anchor.x + anchor.width / 2))).toBeLessThan(anchor.width);
      await popout.keyboard.press('Escape');
      await expect(tooltip).toBeHidden();
   });

   test('shows the dialog that a thread menu opens in its own window, keeps the focus in it, and closes it there with Escape', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      const actions = discountRow(popout).getByRole('button', { name: 'Thread actions' });
      await actions.click();

      await popout.getByRole('menuitem', { name: 'Delete thread…' }).click();

      const dialog = popout.getByRole('alertdialog', { name: 'Delete this thread?' });
      await expect(dialog).toBeVisible();
      await expect(page.getByRole('alertdialog')).toHaveCount(0);
      expect(await bodyPointer(page)).not.toBe('none');
      await expect(dialog).toBeFocused();
      await popout.keyboard.press('Tab');
      await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
      await popout.keyboard.press('Tab');
      await expect(dialog.getByRole('button', { name: 'Delete thread' })).toBeFocused();
      await popout.keyboard.press('Tab');
      await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
      await popout.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
      await expect(actions).toBeFocused();
      await expect(discountRow(popout)).toHaveCount(1);
   });

   test('offers the ask of a selection of the stop text in the Stop panel that it holds', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await openStop(page, 'The new payment step');
      const popout = await popOut(page, 'Stop', 'Pop out the stop');

      await region(popout, 'Stop').locator('[data-stop-field]').first().selectText();

      await expect(region(popout, 'Stop').getByRole('button', { name: /^Ask Claude about/ })).toBeVisible();
   });

   for (const overlay of ['menu', 'alertdialog'] as const) {
      test(`closes the ${overlay} that is open as its window closes, and leaves the main window working`, async ({ page }) => {
         await openDemoTour(page, checkoutTour);
         let popout = await popOut(page, 'Threads', 'Pop out the threads');
         await discountRow(popout).getByRole('button', { name: 'Thread actions' }).click();
         if (overlay === 'alertdialog') {
            await popout.getByRole('menuitem', { name: 'Delete thread…' }).click();
         }

         await expect(popout.getByRole(overlay)).toBeVisible();

         await popout.close({ runBeforeUnload: true });

         await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
         await expect(page.getByRole('menu')).toHaveCount(0);
         await expect(page.getByRole('alertdialog')).toHaveCount(0);
         await expect.poll(() => bodyPointer(page)).not.toBe('none');
         await page.getByRole('tab', { name: 'Files' }).click({ timeout: 5_000 });
         await expect(page.getByRole('tab', { name: 'Files' })).toHaveAttribute('aria-selected', 'true');

         popout = await popOut(page, 'Threads', 'Pop out the threads');
         await discountRow(popout).getByRole('button', { name: 'Thread actions' }).click();
         await expect(popout.getByRole('menu')).toHaveCount(1);
         await popout.keyboard.press('Escape');
         await expect(popout.getByRole('menu')).toHaveCount(0);
      });
   }

   test('shows the reason of a thread delete that the server refuses after its window closed during the delete', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      let answer: () => Promise<void> = async () => {};
      const held = new Promise<void>((resolve) => {
         void page.route('**/api/ops/remove_thread', (route) => {
            answer = () => route.fulfill({ status: 500, body: 'The store failed.' });
            resolve();
         });
      });
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      await discountRow(popout).getByRole('button', { name: 'Thread actions' }).click();
      await popout.getByRole('menuitem', { name: 'Delete thread…' }).click();
      await popout.getByRole('alertdialog').getByRole('button', { name: 'Delete thread' }).click();
      await held;

      await popout.close({ runBeforeUnload: true });
      await expect(page.getByRole('alertdialog')).toHaveCount(0);
      await answer();

      const dialog = page.getByRole('alertdialog', { name: 'Delete this thread?' });
      await expect(dialog.getByRole('alert')).toBeVisible();
      await expect(discountRow(page)).toHaveCount(1);
      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
   });
});
