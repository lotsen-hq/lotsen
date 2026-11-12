import { expect, test } from '@playwright/test';
import { worldHeader, worldPath, type DemoWorld } from '../demo/pageWorld';
import { checkoutTour, openDemoTour } from './demo';

test('a theme that one page changes changes in another page of the same server without a reload', async ({ context, browser, baseURL }) => {
   test.setTimeout(120_000);
   const first = await context.newPage();
   const answered = first.waitForResponse((response) => new URL(response.url()).pathname === worldPath && response.request().method() === 'POST');
   await openDemoTour(first, checkoutTour, { theme: 'light' });
   const world = (await (await answered).json()) as DemoWorld;

   // The demo gives each load a world of its own. So the second page's app gets the first world's UI credential, and each request that carries it goes to the first world. The demo's own
   // setup of the second world keeps that world's credential and its world. The page has a context of its own, because in one context the long-lived requests of both pages hold every
   // connection that Chromium opens to the demo's host, and the first page's write then never leaves the browser.
   const other = await browser.newContext({ baseURL });
   try {
      const second = await other.newPage();
      await second.route('**/api/**', async (route) => {
         const headers = route.request().headers();
         if (new URL(route.request().url()).pathname === '/api/ui-credential') {
            await route.fulfill({ json: { credential: world.uiCredential } });
         } else if (headers.authorization === `Bearer ${world.uiCredential}`) {
            await route.continue({ headers: { ...headers, [worldHeader.toLowerCase()]: world.world } });
         } else {
            await route.continue();
         }
      });
      await second.goto('/');
      // The second page waits for a world of its own and binds its sample tours before it shows.
      await expect(second.getByRole('button', { name: /^(Choose a tour|Tour: .+, choose another)$/ })).toBeVisible({ timeout: 90_000 });
      await expect(second.locator('html')).not.toHaveClass(/\bdark\b/);

      await first.getByRole('button', { name: 'Settings' }).click();
      await first.getByRole('dialog', { name: 'Settings' }).getByRole('radiogroup', { name: 'Theme' }).getByRole('radio', { name: 'Dark' }).click();

      await expect(first.locator('html')).toHaveClass(/\bdark\b/);
      await expect(second.locator('html')).toHaveClass(/\bdark\b/);
   } finally {
      await other.close();
   }
});
