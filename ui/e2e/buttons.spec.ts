import { expect, test, type Locator, type Page } from '@playwright/test';
import { checkoutTour, contrast, drawnColors, openDemoTour, openStop, over, themeColor, type Rgba } from './demo';

/** The colors that draw a button: its border, its own fill and the opaque surface behind it. */
interface ButtonColors {
   border: Rgba;
   fill: Rgba;
   behind: Rgba;
}

const edgeFloor = 2;
const noticeEdgeFloor = 1.6;
const hoverFloor = 1.2;

const stopPanel = (page: Page) => page.getByRole('region', { name: 'Stop', exact: true });
const threadsPanel = (page: Page) => page.getByRole('region', { name: 'Threads', exact: true });
// The Plan panel's icon toggles are named "Skip stop N", so the exact name finds the Stop panel's button alone.
const skipStop = (page: Page) => stopPanel(page).getByRole('button', { name: 'Skip stop', exact: true });
const askClaude = (page: Page) => stopPanel(page).getByRole('button', { name: 'Ask Claude', exact: true });

/** Returns the button's colors as the page draws them, in sRGB. */
async function colorsOf(button: Locator): Promise<ButtonColors> {
   const { own, behind } = await drawnColors(button, ['border-top-color', 'background-color']);
   return { border: own['border-top-color'], fill: own['background-color'], behind };
}

/** Returns the contrast of the button's border against the surface behind it. */
async function edgeContrast(button: Locator): Promise<number> {
   const { border, behind } = await colorsOf(button);
   return contrast(over(border, behind), behind);
}

/** Returns the button's fill as it shows over the surface behind it. */
async function shownFill(button: Locator): Promise<Rgba> {
   const { fill, behind } = await colorsOf(button);
   return over(fill, behind);
}

/** Expects the button's fill to change by the hover floor while the pointer is over it. */
async function expectHoverChange(page: Page, button: Locator, name: string): Promise<void> {
   await page.mouse.move(0, 0);
   const rest = await shownFill(button);
   await button.hover();
   // The button's colors transition, so the hovered fill settles after a moment.
   await expect.poll(async () => contrast(rest, await shownFill(button)), `${name}'s hovered fill against its resting fill`).toBeGreaterThanOrEqual(hoverFloor);
   // The pointer leaves, so the button's tooltip does not cover the next one.
   await page.mouse.move(0, 0);
}

/** Opens the outdated thread, which the default filter hides, in the Threads panel. */
async function openOutdatedThread(page: Page): Promise<void> {
   await threadsPanel(page)
      .getByRole('button', { name: /^Show:/ })
      .click();
   await page.getByRole('menuitemcheckbox', { name: /^Outdated/ }).click();
   await page.keyboard.press('Escape');
   await threadsPanel(page)
      .getByRole('button', { name: /no test of a refund/ })
      .click();
}

for (const theme of ['light', 'dark'] as const) {
   test.describe(`in the ${theme} theme`, () => {
      test.beforeEach(async ({ page }) => {
         await openDemoTour(page, checkoutTour, { theme, placement: 'mixed' });
         await expect(page.locator('html')).toHaveAttribute('data-color-scheme', theme);
      });

      test('gives every text-label button an edge that stands off the surface behind it', async ({ page }) => {
         const update = page.locator('[data-iteration-notice]').getByRole('button', { name: /^Move to update \d+$/ });
         expect.soft(await edgeContrast(update), 'Move to update against the notice').toBeGreaterThanOrEqual(noticeEdgeFloor);

         await openStop(page, 'The new payment step');
         await expect(askClaude(page)).toBeEnabled();
         expect.soft(await edgeContrast(askClaude(page)), 'Ask Claude against the card').toBeGreaterThanOrEqual(edgeFloor);

         await threadsPanel(page)
            .getByRole('button', { name: /coupons apply before or after rounding/ })
            .click();
         const edit = threadsPanel(page).getByRole('group', { name: 'Note' }).getByRole('button', { name: 'Edit' });
         expect.soft(await edgeContrast(edit), "a note's Edit against the card").toBeGreaterThanOrEqual(edgeFloor);

         await openStop(page, 'Where totals come from');
         await stopPanel(page)
            .getByRole('button', { name: /Open the thread: .*round to cents/ })
            .click();
         const resolvedReopen = threadsPanel(page).getByRole('button', { name: 'Reopen' });
         expect.soft(await edgeContrast(resolvedReopen), "a resolved thread's Reopen against the card").toBeGreaterThanOrEqual(edgeFloor);

         await threadsPanel(page).getByRole('button', { name: 'Back to all threads' }).click();
         await openOutdatedThread(page);
         const outdatedReopen = threadsPanel(page).getByRole('button', { name: 'Reopen' });
         expect.soft(await edgeContrast(outdatedReopen), "an outdated thread's Reopen against the card").toBeGreaterThanOrEqual(edgeFloor);
      });

      test('changes the fill clearly on hover, of a text-label button and of an icon-only ghost button', async ({ page }) => {
         await openStop(page, 'The new payment step');
         await expect(askClaude(page)).toBeEnabled();

         await expectHoverChange(page, askClaude(page), 'Ask Claude');
         await expectHoverChange(page, skipStop(page), 'Skip stop');
         await expectHoverChange(page, page.getByRole('button', { name: 'Maximize the stop' }), 'Maximize');
      });

      test('marks a pressed Skip stop with the secondary fill, as the Plan panel marks a skipped stop', async ({ page }) => {
         await openStop(page, 'The new payment step');
         await expect(skipStop(page)).toHaveAttribute('aria-pressed', 'false');
         const unpressed = await shownFill(skipStop(page));

         await openStop(page, 'The product data file');
         await expect(skipStop(page)).toHaveAttribute('aria-pressed', 'true');
         await page.mouse.move(0, 0);
         const [fill, secondary] = await Promise.all([skipStop(page).evaluate((node) => getComputedStyle(node).backgroundColor), themeColor(skipStop(page), '--secondary')]);
         expect(fill).toBe(secondary);
         expect(await shownFill(skipStop(page))).not.toEqual(unpressed);
      });
   });
}
