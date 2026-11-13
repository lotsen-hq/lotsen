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

const gatewayStop = 'Gateway errors reach the customer as they are';

test("at Large, the code toolbar shows the whole file name and truncates a thread marker's label beside it", async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide', fontSize: 'large' });
   await openStop(page, gatewayStop);
   const toolbar = center(page).getByRole('group', { name: 'View mode' }).locator('xpath=..');
   const name = toolbar.locator('code > span', { hasText: /^payment\.ts$/ });
   const label = toolbar.locator('.thread-marker > span', { hasText: 'Should this file have tests of its own?' });
   await expect(label).toBeVisible();
   const overflow = (element: Locator) => element.evaluate((node) => node.scrollWidth - node.clientWidth);

   expect(await overflow(name)).toBeLessThanOrEqual(0);
   expect(await overflow(label)).toBeGreaterThan(0);
   expect((await label.boundingBox())!.width).toBeGreaterThan(0);
});

test('at Large, the top bar shows the count of unsent answers on one line', async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide', fontSize: 'large' });
   await openStop(page, gatewayStop);
   const pill = page.locator('[data-unsent-answers]');
   await expect(pill).toHaveText(/unsent answers?$/);
   const lineHeight = parseFloat(await pill.evaluate((node) => getComputedStyle(node).lineHeight));

   expect((await pill.boundingBox())!.height).toBeCloseTo(lineHeight, 0);
});

for (const fontSize of ['small', 'medium', 'large'] as const) {
   test(`at ${fontSize}, a line with thread markers or a passage label is as tall as a plain line, with its code at the same place`, async ({ page }) => {
      await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide', fontSize });
      await openStop(page, gatewayStop);
      const head = center(page).locator('.cm-editor').nth(1);
      const withMarkers = head.locator('.cm-line:has(.lotsen-threadMarkers)').first();
      const withLabel = head.locator('.cm-line:has(.lotsen-passageLabels)').first();
      const plain = head.locator('.cm-line:not(:has(.lotsen-threadMarkers, .lotsen-passageLabels))', { hasText: /\S/ }).first();
      await expect(withMarkers).toBeVisible();
      await expect(withLabel).toBeVisible();
      // The height of the line, and how far the first text of its code sits below the line's top.
      const geometry = (line: Locator) =>
         line.evaluate((node) => {
            const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT, {
               acceptNode: (text) =>
                  (text.textContent ?? '').trim() && !text.parentElement!.closest('.lotsen-threadMarkers, .lotsen-passageLabels') ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP,
            });
            const range = document.createRange();
            range.selectNodeContents(walker.nextNode()!);
            const [lineBox, textBox] = [node.getBoundingClientRect(), range.getBoundingClientRect()];
            return { height: lineBox.height, textTop: textBox.top - lineBox.top };
         });
      const expected = await geometry(plain);

      for (const line of [withMarkers, withLabel]) {
         const actual = await geometry(line);
         expect(Math.abs(actual.height - expected.height)).toBeLessThanOrEqual(0.5);
         expect(Math.abs(actual.textTop - expected.textTop)).toBeLessThanOrEqual(0.5);
      }
   });
}
