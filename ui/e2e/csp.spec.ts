import { expect, test, type Page } from '@playwright/test';
import { arrange, center, checkoutTour, dragTab, groups, openDemoTour, openStop } from './demo';

declare global {
   interface Window {
      violations: string[];
   }
}

// This file runs in the csp project, against the built demo that Vite's preview serves with the server's policy.
test('records no securitypolicyviolation while the page loads, a panel is dragged to another group and each arrangement applies', async ({ page }) => {
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
   for (const { name, expected } of [
      { name: 'Columns', expected: [['Files'], ['Plan'], ['Code'], ['Stop'], ['Threads']] },
      { name: 'Explanation above the diff', expected: [['Files', 'Plan'], ['Stop'], ['Code'], ['Threads']] },
      { name: 'Reset layout', expected: [['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']] },
   ]) {
      await arrange(page, name);
      await expect.poll(() => groups(page)).toEqual(expected);
   }

   expect(await policy).toContain("style-src 'self' 'nonce-");
   expect(await page.evaluate(() => window.violations)).toEqual([]);
});

test('applies app.css and each style element that a popout window copies from the main page', async ({ page }) => {
   await openDemoTour(page, checkoutTour);
   // The diff's editor adds a <style> element to the main page, which the popout window's page copies.
   await openStop(page, 'A cart line needs at least one item');
   await expect(center(page).locator('.cm-editor').first()).toBeVisible();

   const popout = await popOutThreads(page);

   await expect(threadList(popout)).toBeVisible();
   expect(await rowDisplay(popout)).toBe('flex');
   // The policy leaves a <style> element without the page's nonce with no style sheet.
   const styles = await popout.evaluate(() => [...document.querySelectorAll('style')].map((style) => style.sheet !== null));
   expect(styles.length).toBeGreaterThan(0);
   expect(styles.every(Boolean)).toBe(true);
});

// Routing turns off the browser's HTTP cache, so the popout window's request for app.css reaches the route (https://playwright.dev/docs/api/class-browsercontext#browser-context-route).
test('shows a popped-out panel in its popout window only once the stylesheets that the window loads apply', async ({ page }) => {
   await openDemoTour(page, checkoutTour);
   let caught!: () => void;
   const requested = new Promise<void>((resolve) => (caught = resolve));
   let release!: () => void;
   const released = new Promise<void>((resolve) => (release = resolve));
   await page.context().route('**/app.css', async (route) => {
      caught();
      await released;
      await route.continue();
   });

   const popout = await popOutThreads(page);

   await requested;
   await expect(threadList(popout)).toBeAttached();
   await expect(threadList(popout)).toBeHidden();
   release();
   await expect(threadList(popout)).toBeVisible();
   expect(await rowDisplay(popout)).toBe('flex');
});

test('shows a popped-out panel in its popout window when a stylesheet that the window loads fails to load', async ({ page }) => {
   await openDemoTour(page, checkoutTour);
   await page.context().route('**/app.css', (route) => route.abort());

   const popout = await popOutThreads(page);

   await expect(threadList(popout)).toBeVisible();
});

/** Pops the Threads panel out, and returns its popout window. */
async function popOutThreads(page: Page): Promise<Page> {
   const opened = page.waitForEvent('popup');
   await page.getByRole('button', { name: 'Pop out the threads' }).click();
   return opened;
}

// `includeHidden` finds the list also while the popout window's page is hidden.
const threadList = (target: Page) => target.getByRole('region', { name: 'Threads', exact: true, includeHidden: true }).getByRole('list', { name: 'Threads', includeHidden: true });

/** Returns the display of the thread list's first row button, which app.css makes a flex box. */
const rowDisplay = (target: Page) =>
   threadList(target)
      .getByRole('button')
      .first()
      .evaluate((button) => getComputedStyle(button).display);

/** Records each securitypolicyviolation of the page and of the popout windows that it opens, in the window's `violations`. */
async function recordViolations(page: Page): Promise<void> {
   await page.context().addInitScript(() => {
      window.violations = [];
      document.addEventListener('securitypolicyviolation', (event) => window.violations.push(`${event.violatedDirective} ${event.sourceFile}:${event.lineNumber} ${event.sample}`));
   });
}

/** Returns whether each style element of the page has a style sheet, which the policy leaves out for an element without the page's nonce. */
const sheets = (target: Page) => target.evaluate(() => [...document.querySelectorAll('style')].map((style) => style.sheet !== null));

/** Returns the display of the diff's first scroller, which CodeMirror's base theme makes a flex box. */
const scrollerDisplay = (target: Page) =>
   center(target)
      .locator('.cm-scroller')
      .first()
      .evaluate((scroller) => getComputedStyle(scroller).display);

/** Pops the Code panel out, and returns its popout window once the panel shows there. */
async function popOutCode(page: Page): Promise<Page> {
   const opened = page.waitForEvent('popup');
   await page.getByRole('button', { name: 'Pop out the code' }).click();
   const popout = await opened;
   await expect(center(popout)).toBeVisible();
   return popout;
}

test('applies the styles of a diff that mounts in a popout window, and of the diff once its panel returns, with no securitypolicyviolation in either window', async ({ page }) => {
   await recordViolations(page);
   await openDemoTour(page, checkoutTour);
   const popout = await popOutCode(page);

   await openStop(page, 'A cart line needs at least one item');

   await expect(center(popout).locator('.cm-editor').first()).toBeVisible();
   expect(await scrollerDisplay(popout)).toBe('flex');
   expect((await sheets(popout)).every(Boolean)).toBe(true);
   expect(await popout.evaluate(() => window.violations)).toEqual([]);

   await popout.close({ runBeforeUnload: true });
   await expect(center(page).locator('.cm-editor').first()).toBeVisible();
   await openStop(page, 'The new payment step');
   await expect(center(page).locator('.cm-editor').first()).toBeVisible();
   expect(await scrollerDisplay(page)).toBe('flex');
   expect((await sheets(page)).every(Boolean)).toBe(true);
   expect(await page.evaluate(() => window.violations)).toEqual([]);
});

test('applies the styles of a diff that moves from the main page into a popout window, whose page has a nonce of its own, with no securitypolicyviolation there', async ({ page }) => {
   await recordViolations(page);
   await openDemoTour(page, checkoutTour);
   await openStop(page, 'A cart line needs at least one item');
   await expect(center(page).locator('.cm-editor').first()).toBeVisible();

   const popout = await popOutCode(page);

   const pageNonce = (target: Page) => target.locator('meta[name="csp-nonce"]').getAttribute('content');
   expect(await pageNonce(popout)).not.toBe(await pageNonce(page));
   await expect(center(popout).locator('.cm-editor').first()).toBeVisible();
   expect(await scrollerDisplay(popout)).toBe('flex');
   expect((await sheets(popout)).every(Boolean)).toBe(true);
   expect(await popout.evaluate(() => window.violations)).toEqual([]);
});
