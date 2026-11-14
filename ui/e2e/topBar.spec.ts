import { expect, test, type Locator, type Page } from '@playwright/test';
import { checkoutTour, openDemoTour, openStop } from './demo';

const header = (page: Page) => page.getByRole('banner');

/** The icon-only actions of the bar, by their accessible names. */
const iconActions = ['Check the PR for a new update', 'Settings', 'Choose another tour'];

/** Returns the element's computed style properties. */
const styleOf = <P extends string>(element: Locator, properties: readonly P[]) =>
   element.evaluate((node, names) => Object.fromEntries(names.map((name) => [name, getComputedStyle(node).getPropertyValue(name)])), properties) as Promise<Record<P, string>>;

/** Returns whether a computed color is fully transparent. */
const transparent = (color: string) => color === 'transparent' || /^rgba\(.*,\s*0\)$/.test(color) || /\/\s*0\)$/.test(color);

test.describe("the top bar's controls", () => {
   test.beforeEach(async ({ page }) => {
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await openStop(page, 'The new payment step');
      // The pointer and the focus leave the bar, so no control shows its hover or focus style.
      await page.mouse.move(0, 500);
      await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
   });

   test('share one height, twice the root font size', async ({ page }) => {
      const rootSize = parseFloat(await page.locator('html').evaluate((node) => getComputedStyle(node).fontSize));
      const controls = await header(page)
         .locator('button, a')
         .evaluateAll((nodes) => nodes.map((node) => ({ name: node.getAttribute('aria-label') ?? node.textContent.trim(), height: node.getBoundingClientRect().height })));

      expect(controls.length).toBeGreaterThanOrEqual(8);
      for (const { name, height } of controls) {
         expect.soft(height, `${name}'s height`).toBeCloseTo(2 * rootSize, 1);
      }
   });

   test('show each icon-only action as a ghost button with a name and a tooltip', async ({ page }) => {
      for (const name of iconActions) {
         const button = header(page).getByRole('button', { name, exact: true });
         const style = await styleOf(button, ['border-top-color', 'border-top-width', 'box-shadow']);
         expect.soft(transparent(style['border-top-color']) || style['border-top-width'] === '0px', `${name}'s border, ${style['border-top-color']}`).toBe(true);
         expect.soft(style['box-shadow'], `${name}'s shadow`).toBe('none');

         await button.hover();
         await expect(button).toHaveAttribute('aria-describedby', /.+/);
         const tooltip = page.locator(`[id="${await button.getAttribute('aria-describedby')}"]`);
         await expect.soft(tooltip, `${name}'s tooltip`).toHaveText(name);
         await page.mouse.move(0, 500);
         await expect(tooltip).toHaveCount(0);
      }
   });

   test('underline the pull request link without hover, and mark it as external', async ({ page }) => {
      const link = header(page).getByRole('link', { name: checkoutTour, exact: true });
      const style = await styleOf(link, ['text-decoration-line', 'text-decoration-color']);

      expect(style['text-decoration-line']).toBe('underline');
      expect(transparent(style['text-decoration-color']), `the underline's color, ${style['text-decoration-color']}`).toBe(false);
      await expect(link.locator('svg.lucide-git-pull-request')).toBeVisible();
      await expect(link.locator('svg.lucide-external-link')).toBeVisible();
   });

   test('show the Claude session state as a pill with an edge', async ({ page }) => {
      const state = header(page).getByRole('button', { name: /^Claude session:/ });
      const style = await styleOf(state, ['border-top-left-radius', 'border-top-color', 'border-top-width']);
      const height = (await state.boundingBox())!.height;

      expect(parseFloat(style['border-top-left-radius'])).toBeGreaterThanOrEqual(height / 2);
      expect(style['border-top-width']).not.toBe('0px');
      expect(transparent(style['border-top-color']), `the pill's edge, ${style['border-top-color']}`).toBe(false);
   });
});
