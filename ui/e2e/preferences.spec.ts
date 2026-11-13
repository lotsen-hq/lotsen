import { expect, test, type Locator } from '@playwright/test';
import { center, checkoutTour, editorLine, nextWorld, openDemoTour, openSecondPage, openStop } from './demo';

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

test('the font size scales the page and the diff editors, and Medium keeps their sizes', async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide' });
   await openStop(page, 'A cart line needs at least one item');
   const fontSize = async (element: Locator) => parseFloat(await element.evaluate((node) => getComputedStyle(node).fontSize));
   const html = page.locator('html');
   const line = editorLine(center(page), 'if (quantity < 1)');
   await expect(line).toBeVisible();
   const sizes = page.getByRole('dialog', { name: 'Settings' }).getByRole('radiogroup', { name: 'Font size' });

   expect(await fontSize(html)).toBe(15);
   expect(await fontSize(line)).toBe(13);

   await page.getByRole('button', { name: 'Settings' }).click();
   await sizes.getByRole('radio', { name: 'Large' }).click();
   await expect(html).toHaveAttribute('data-font-size', 'large');
   expect(await fontSize(html)).toBeCloseTo(17, 2);
   expect(await fontSize(line)).toBeCloseTo((13 * 17) / 15, 2);

   await sizes.getByRole('radio', { name: 'Small' }).click();
   await expect(html).toHaveAttribute('data-font-size', 'small');
   expect(await fontSize(html)).toBeCloseTo(13, 2);
   expect(await fontSize(line)).toBeCloseTo((13 * 13) / 15, 2);
});
