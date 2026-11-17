import { expect, test, type Locator, type Page } from '@playwright/test';
import { contrast, drawnColors, expectPalette, openDemoTour, over, paletteName, palettes } from './demo';

const orientTour = 'Find your way around the storefront';

/** Returns the contrasts of a tour marker's count and its pin against the surface behind them, as the page draws them. */
async function markerContrasts(marker: Locator): Promise<{ count: number; pin: number }> {
   const [count, pin] = await Promise.all([drawnColors(marker, ['color']), drawnColors(marker.locator('svg'), ['color'])]);
   return { count: contrast(over(count.own.color, count.behind), count.behind), pin: contrast(over(pin.own.color, pin.behind), pin.behind) };
}

/** Expects the marker's count to reach text contrast and its pin the contrast of a graphic. */
async function expectContrasts(marker: Locator, state: string): Promise<void> {
   const { count, pin } = await markerContrasts(marker);
   expect(count, `the count ${state}`).toBeGreaterThanOrEqual(4.5);
   expect(pin, `the pin ${state}`).toBeGreaterThanOrEqual(3);
}

const files = (page: Page) => page.getByRole('tabpanel', { name: 'Files' }).getByRole('tree', { name: 'Repository files' });

for (const palette of palettes) {
   test(`a tour marker's count and pin stand off their row at rest, hovered and on the open file, in ${paletteName(palette)}`, async ({ page }) => {
      await openDemoTour(page, orientTour, palette);
      await expectPalette(page, palette);
      // The open demo panel lies over the tree's lower rows.
      await page.locator('details', { hasText: 'Demo' }).first().locator('summary').click();
      await page.getByRole('tab', { name: 'Files' }).click();
      await page.getByRole('tabpanel', { name: 'Files' }).getByRole('button', { name: 'Repository' }).click();
      const src = files(page).getByRole('treeitem', { name: 'src, Visited by 3 stops' });
      const marker = src.locator(':scope > .row > .visits');
      await expect(marker).toHaveText('3');
      await page.mouse.move(0, 0);

      await expectContrasts(marker, 'at rest');
      await src.locator(':scope > .row').hover();
      await expectContrasts(marker, 'hovered');

      // The keyboard opens the folders, because a hover's tooltip covers the rows below the one it describes.
      await src.focus();
      await page.keyboard.press('ArrowRight');
      const checkout = files(page).getByRole('treeitem', { name: 'checkout, Visited by 2 stops' });
      await checkout.focus();
      await page.keyboard.press('ArrowRight');
      const cart = files(page).getByRole('treeitem', { name: 'cart.ts, Visited by 1 stop' });
      await cart.focus();
      await page.keyboard.press('Enter');
      await expect(cart).toHaveAttribute('aria-current', 'true');
      await page.mouse.move(0, 0);
      await expectContrasts(cart.locator(':scope > .row > .visits'), 'on the open file');

      await page.getByRole('tabpanel', { name: 'Files' }).getByRole('button', { name: 'Visited' }).click();
      const visitedCart = page.getByRole('tree', { name: 'Visited files' }).getByRole('treeitem', { name: 'cart.ts, Visited by 1 stop' });
      await expect(visitedCart).toHaveAttribute('aria-current', 'true');
      await page.mouse.move(0, 0);
      await expectContrasts(visitedCart.locator(':scope > .row > .visits'), 'on the open file in the Visited view');
   });
}
