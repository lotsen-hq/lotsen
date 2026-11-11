import { expect, test } from '@playwright/test';
import { center, checkoutTour, dragTab, openDemoTour, openStop } from './demo';

declare global {
   interface Window {
      violations: string[];
   }
}

// This file runs in the csp project, against the built demo that Vite's preview serves with the server's policy.
test('records no securitypolicyviolation while the page loads and a panel is dragged to another group', async ({ page }) => {
   await page.addInitScript(() => {
      window.violations = [];
      document.addEventListener('securitypolicyviolation', (event) => window.violations.push(`${event.violatedDirective} ${event.sourceFile}:${event.lineNumber} ${event.sample}`));
   });
   const policy = page.waitForResponse((response) => response.request().resourceType() === 'document').then((response) => response.headers()['content-security-policy']);

   await openDemoTour(page, checkoutTour);
   await openStop(page, 'A cart line needs at least one item');
   await expect(center(page).locator('.cm-editor').first()).toBeVisible();
   await dragTab(page, 'Threads', center(page));

   await expect(page.getByRole('region', { name: 'Threads', exact: true }).getByRole('tab', { name: 'Code' })).toBeVisible();
   expect(await policy).toContain("style-src 'self' 'nonce-");
   expect(await page.evaluate(() => window.violations)).toEqual([]);
});
