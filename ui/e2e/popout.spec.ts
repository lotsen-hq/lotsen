import { expect, test, type Page } from '@playwright/test';
import { checkoutTour, openDemoTour } from './demo';

const region = (page: Page, name: string) => page.getByRole('region', { name, exact: true });

/** Returns the tabs of each group that shows, in document order. */
const groups = (page: Page) => page.getByRole('tablist').evaluateAll((lists) => lists.map((list) => [...list.querySelectorAll('[role="tab"]')].map((tab) => tab.getAttribute('aria-label'))));

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
});
