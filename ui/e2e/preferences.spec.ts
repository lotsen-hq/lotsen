import { expect, test } from '@playwright/test';
import { checkoutTour, nextWorld, openDemoTour, openSecondPage } from './demo';

test('a theme that one page changes changes in another page of the same server without a reload', async ({ context, browser, baseURL }) => {
   test.setTimeout(120_000);
   const first = await context.newPage();
   const world = nextWorld(first);
   await openDemoTour(first, checkoutTour, { theme: 'light' });

   const { page: second, close } = await openSecondPage(browser, baseURL, await world);
   try {
      await expect(second.locator('html')).not.toHaveClass(/\bdark\b/);

      await first.getByRole('button', { name: 'Settings' }).click();
      await first.getByRole('dialog', { name: 'Settings' }).getByRole('radiogroup', { name: 'Theme' }).getByRole('radio', { name: 'Dark' }).click();

      await expect(first.locator('html')).toHaveClass(/\bdark\b/);
      await expect(second.locator('html')).toHaveClass(/\bdark\b/);
   } finally {
      await close();
   }
});
