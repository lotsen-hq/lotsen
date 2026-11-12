import { expect, test, type Locator, type Page } from '@playwright/test';
import { waitTimeout } from '../tests/app/waitTimeout.mjs';
import { center, checkoutTour, contrast, drawnColors, editorLine, inScrollerView, openDemoTour, openStop, over, showPanel, themeColor, type Rgba } from './demo';

const cartLineStop = 'A cart line needs at least one item';
const paymentStop = 'The new payment step';

/** The editors of the center's diff, base first side by side. */
const editors = (page: Page) => center(page).locator('.cm-editor');
const block = (page: Page) => center(page).locator('.lotsen-explanation');

/** Returns the top and the bottom of an element on the page, in pixels. */
async function edges(element: Locator): Promise<{ top: number; bottom: number }> {
   const box = (await element.boundingBox())!;
   return { top: box.y, bottom: box.y + box.height };
}

test('opening a stop scrolls its focused passage into view', async ({ page }) => {
   // A short window leaves the passage below the first screen of its file.
   await page.setViewportSize({ width: 1600, height: 560 });
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide' });

   await openStop(page, cartLineStop);

   const head = editors(page).nth(1);
   const first = head.locator('.cm-line.lotsen-passage-primary').first();
   await expect(first).toContainText('if (quantity < 1)');
   await expect.poll(() => inScrollerView(first)).toBe(true);
   await expect.poll(() => inScrollerView(editorLine(head, "import type { Product } from '../lib/catalog';"))).toBe(false);
});

test('opening a base passage that the inline diff shows as deleted scrolls its first line to the top of the view', async ({ page }) => {
   await page.setViewportSize({ width: 1600, height: 560 });
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'inline' });
   await openStop(page, paymentStop);

   await page
      .getByRole('region', { name: 'Stop', exact: true })
      .getByRole('button', { name: /shipping/ })
      .click();

   const head = editors(page);
   await expect(head).toHaveCount(1);
   const first = head.locator('.cm-deletedLine', { hasText: "'region-050'" });
   // The editor scrolls a line to `scrollMargin` (32 pixels, in diffEditor.ts) below the top of its scroller.
   const belowScrollerTop = async () => (await edges(first)).top - (await edges(head.locator('.cm-scroller'))).top;
   await expect.poll(belowScrollerTop).toBeCloseTo(32, -1);
});

test('a drag over deleted lines of the inline diff selects the line that the ask of its selection lies on', async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'inline' });
   await openStop(page, paymentStop);
   await page
      .getByRole('region', { name: 'Stop', exact: true })
      .getByRole('button', { name: /shipping/ })
      .click();
   const deleted = (text: string) => editors(page).locator('.cm-deletedLine', { hasText: text });
   await expect.poll(() => inScrollerView(deleted("'region-053'"))).toBe(true);
   const [start, end] = [(await deleted("'region-052'").boundingBox())!, (await deleted("'region-053'").boundingBox())!];
   const ask = center(page).getByRole('button', { name: /^Ask Claude about src\/checkout\/shipping\.ts, lines/ });

   await page.mouse.move(start.x + 40, start.y + start.height / 2);
   await page.mouse.down();
   await page.mouse.move(end.x + 60, end.y + end.height / 2, { steps: 3 });
   await expect(ask).toHaveAccessibleName('Ask Claude about src/checkout/shipping.ts, lines 54–55 at the base');
   const [button, next] = [(await ask.boundingBox())!, (await deleted("'region-054'").boundingBox())!];
   const y = next.y + next.height / 2;
   expect(y).toBeGreaterThan(button.y);
   expect(y).toBeLessThan(button.y + button.height);
   await page.mouse.move(button.x + button.width / 2, y);
   await page.mouse.up();

   await expect(ask).toHaveAccessibleName('Ask Claude about src/checkout/shipping.ts, lines 54–56 at the base');
});

test('an inline explanation sits below its primary passage, and follows a revision that writes the stop', async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'inline', layout: 'inline' });
   await openStop(page, cartLineStop);

   await expect(block(page)).toHaveCount(1);
   const passageEnd = block(page).locator('xpath=preceding-sibling::div[contains(@class, "cm-line")][1]');
   await expect(passageEnd).toHaveClass(/lotsen-passage-primary/);
   await expect(passageEnd).toHaveText('}');
   expect((await edges(block(page))).top).toBeCloseTo((await edges(passageEnd)).bottom, 0);
   await expect(block(page).getByRole('heading', { name: cartLineStop })).toBeVisible();

   const oldForm = 'Does anything still post the old payment form?';
   await openStop(page, oldForm);
   await expect(block(page)).toHaveCount(0);
   await page.getByRole('button', { name: `Write “${oldForm}”` }).click();

   await expect(block(page).getByRole('heading', { name: oldForm })).toBeVisible();
   const written = block(page).locator('xpath=preceding-sibling::div[contains(@class, "cm-line")][1]');
   await expect(written).toHaveClass(/lotsen-passage-primary/);
   expect((await edges(block(page))).top).toBeCloseTo((await edges(written)).bottom, 0);
   await expect(page.getByRole('region', { name: 'Stop', exact: true })).toContainText('The explanation shows in the code, below the primary passage.');
});

test('the side-by-side diff keeps both panes aligned past an inline explanation', async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'inline', layout: 'sideBySide' });
   await openStop(page, cartLineStop);

   const [base, head] = [editors(page).nth(0), editors(page).nth(1)];
   const shown = head.locator('.lotsen-explanation');
   const spacer = base.locator('.lotsen-explanationSpacer');
   await expect(shown).toHaveCount(1);
   await expect(spacer).toHaveCount(1);
   await expect(base.locator('.lotsen-explanation')).toHaveCount(0);

   await expect.poll(async () => Math.round((await edges(spacer)).bottom - (await edges(spacer)).top)).toBe(Math.round((await edges(shown)).bottom - (await edges(shown)).top));
   expect((await edges(spacer)).top).toBeCloseTo((await edges(shown)).top, 0);
   // A pane draws the lines below its viewport as one gap, so the element after each widget is a line or a gap.
   const after = (element: Locator) => element.locator('xpath=following-sibling::*[1]');
   expect((await edges(after(spacer))).top).toBeCloseTo((await edges(after(shown))).top, 0);
});

test("the stop kind label is 70% of its title's size, never below 10.5 pixels, and leaves a one-line Plan row as tall as before", async ({ page }) => {
   // A wider window widens the Plan panel, so the title of this row fits on one line beside the row's skip toggle and done checkbox.
   await page.setViewportSize({ width: 1920, height: 1000 });
   await openDemoTour(page, checkoutTour, { placement: 'inline', layout: 'inline' });
   const fontSize = async (element: Locator) => parseFloat(await element.evaluate((node) => getComputedStyle(node).fontSize));
   const height = async (element: Locator) => (await element.boundingBox())!.height;
   const row = page.getByRole('tabpanel', { name: 'Plan' }).locator('button.stop-row', { hasText: 'Where totals come from' });

   const inPlan = row.locator('.kind-pill');
   expect(await fontSize(inPlan)).toBe(10.5);
   expect(await height(inPlan)).toBeGreaterThanOrEqual(16);
   expect(await height(inPlan)).toBeLessThanOrEqual(17.5);
   // A one-line row is this tall in Chromium with a kind of 10.5-pixel text and no vertical padding as well.
   expect(await height(row)).toBe(28.5);

   await openStop(page, cartLineStop);
   const header = page.getByRole('region', { name: 'Stop', exact: true }).locator('header').first();
   expect(await fontSize(header.locator('.kind-pill'))).toBeCloseTo(0.7 * (await fontSize(header.locator('h3'))), 1);
   await expect(block(page)).toHaveCount(1);
   expect(await fontSize(block(page).locator('.kind-pill'))).toBe(10.5);
});

test("a stop's done checkbox shows while the pointer is in its row, and stays once the stop is done", async ({ page }) => {
   await openDemoTour(page, checkoutTour);
   // The open demo panel lies over the plan's rows.
   await page.locator('details', { hasText: 'Demo' }).first().locator('summary').click();
   const item = page.getByRole('tabpanel', { name: 'Plan' }).locator('.stop-item', { hasText: 'Where totals come from' });
   const checkbox = item.getByRole('checkbox', { name: /^Stop 2 done/ });
   await page.mouse.move(0, 0);
   await expect(checkbox).toHaveCSS('opacity', '0');

   await item.locator('.stop-row').hover();
   await expect(checkbox).toHaveCSS('opacity', '1');
   await checkbox.click();
   await expect(checkbox).toHaveAttribute('aria-checked', 'true');
   await page.mouse.move(0, 0);
   await expect(checkbox).toHaveCSS('opacity', '1');
});

/** Returns a color's hue in degrees on the HSL wheel. */
function hue([r, g, b]: Rgba): number {
   const [max, min] = [Math.max(r, g, b), Math.min(r, g, b)];
   const sector = max === r ? (g - b) / (max - min) : max === g ? (b - r) / (max - min) + 2 : (r - g) / (max - min) + 4;
   return (sector * 60 + 360) % 360;
}

for (const theme of ['light', 'dark'] as const) {
   for (const messageStyle of ['bubbles', 'fullWidth'] as const) {
      test(`the message colors set Claude's violet messages apart from the reviewer's blue ones, in the ${theme} theme with ${messageStyle}`, async ({ page }) => {
         await openDemoTour(page, checkoutTour, { theme, messageStyle });
         await expect(page.locator('html')).toHaveAttribute('data-color-scheme', theme);
         const threads = page.getByRole('region', { name: 'Threads', exact: true });
         await threads.getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' }).getByRole('button').first().click();
         const view = threads.getByRole('article');
         await expect(view.locator('[data-messages]')).toHaveAttribute('data-messages', messageStyle);
         const reviewer = view.locator('[data-writer="You"] [data-bubble]').first();
         const claude = view.locator('[data-writer="Claude"] [data-bubble]').first();

         const [mine, theirs] = await Promise.all([drawnColors(reviewer, ['background-color']), drawnColors(claude, ['background-color'])]);
         const reviewerFill = over(mine.own['background-color'], mine.behind);
         const claudeFill = over(theirs.own['background-color'], theirs.behind);
         const apart = Math.abs(hue(reviewerFill) - hue(claudeFill));
         expect(Math.min(apart, 360 - apart), `hues ${hue(reviewerFill)} and ${hue(claudeFill)}`).toBeGreaterThanOrEqual(20);
         expect(contrast(reviewerFill, mine.behind), "the reviewer's fill against the card").toBeGreaterThanOrEqual(1.2);

         expect(await claude.evaluate((node) => getComputedStyle(node).borderLeftWidth)).toBe('3px');
         const violet = theme === 'light' ? 'rgb(130, 80, 223)' : 'rgb(188, 140, 255)';
         expect(await themeColor(claude, '--claude-marker')).toBe(violet);
         expect(await claude.evaluate((node) => getComputedStyle(node).borderLeftColor)).toBe(violet);
      });
   }
}

/** Expects a warning notice's bar and fill to be the theme's warning colors, and its text and mark to read against the fill. */
async function expectWarningStyle(notice: Locator, name: string): Promise<void> {
   await expect(notice, `${name}: the bar`).toHaveCSS('border-left-color', await themeColor(notice, '--warning'));
   await expect(notice, `${name}: the fill`).toHaveCSS('background-color', await themeColor(notice, '--warning-surface'));

   const mark = notice.locator('svg');
   await expect(mark, `${name}: the mark`).toHaveCSS('color', await themeColor(notice, '--warning-mark'));

   const { own, behind } = await drawnColors(notice, ['color', 'background-color']);
   const fill = over(own['background-color'], behind);
   expect(contrast(over(own.color, fill), fill), `${name}: the text against the fill`).toBeGreaterThanOrEqual(4.5);
   const markColor = (await drawnColors(mark, ['color'])).own.color;
   expect(contrast(over(markColor, fill), fill), `${name}: the mark against the fill`).toBeGreaterThanOrEqual(3);
}

for (const theme of ['light', 'dark'] as const) {
   test(`warning notices are amber while the Stop version notice stays blue, in the ${theme} theme`, async ({ page }) => {
      await openDemoTour(page, checkoutTour, { theme, placement: 'side' });
      await expect(page.locator('html')).toHaveAttribute('data-color-scheme', theme);

      // The keyboard opens the row, as in `openStop`.
      const summaryRow = page
         .getByRole('tabpanel', { name: 'Plan' })
         .locator('button.stop-row')
         .filter({ has: page.locator('.title', { hasText: /^Summary$/ }) });
      await summaryRow.focus();
      await page.keyboard.press('Enter');
      await expectWarningStyle(center(page).getByRole('note').filter({ hasText: 'predates changes to the summary' }), 'the outdated synthesis');

      await openStop(page, paymentStop);
      await page.getByRole('combobox', { name: 'Stop version' }).selectOption({ label: 'Version 1' });
      const version = page.getByRole('status', { name: 'Stop version' });
      await expect(version).toHaveCSS('border-left-color', await themeColor(version, '--primary'));
      await version.getByRole('button', { name: 'Show the current version' }).click();

      await page
         .locator('[data-iteration-notice]')
         .getByRole('button', { name: /update/i })
         .click();
      await expectWarningStyle(page.getByRole('status', { name: 'Stale stop' }), 'the stale stop');
   });
}

test('collapsed rows expand and keep the passage in view', async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide' });
   await openStop(page, paymentStop);
   await page
      .getByRole('region', { name: 'Stop', exact: true })
      .getByRole('button', { name: /shipping/ })
      .click();
   const base = editors(page).nth(0);
   const passage = editorLine(base, "'region-050'");
   await expect.poll(() => inScrollerView(passage)).toBe(true);

   await center(page).getByRole('button', { name: 'Collapse to passages' }).click();
   const markers = base.locator('.lotsen-collapsed');
   await expect(markers).toHaveCount(2);
   await expect(editorLine(base, "'region-010'")).toHaveCount(0);
   await expect.poll(() => inScrollerView(passage)).toBe(true);

   await markers.last().getByRole('button', { name: 'Show all' }).click();

   await expect(markers).toHaveCount(1);
   await expect(editorLine(base, "'region-080'")).toHaveCount(1);
   await expect.poll(() => inScrollerView(passage)).toBe(true);
});

test('Show all on the collapsed marker above a passage keeps the passage in view', async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide' });
   await openStop(page, paymentStop);
   await page
      .getByRole('region', { name: 'Stop', exact: true })
      .getByRole('button', { name: /shipping/ })
      .click();
   const base = editors(page).nth(0);
   const passage = editorLine(base, "'region-050'");
   await center(page).getByRole('button', { name: 'Collapse to passages' }).click();
   const markers = base.locator('.lotsen-collapsed');
   await expect(markers).toHaveCount(2);
   await expect.poll(() => inScrollerView(passage)).toBe(true);
   const showAll = markers.first().getByRole('button', { name: 'Show all' });

   await showAll.click();

   await expect(markers).toHaveCount(1);
   await expect(editorLine(base, "'region-046'")).toHaveCount(1);
   await expect.poll(() => inScrollerView(passage)).toBe(true);
});

for (const [which, step] of [
   ['first', 'Show last 20'],
   ['last', 'Show first 20'],
] as const) {
   test(`${step} by keyboard on the ${which} collapsed marker keeps the focused control in view in a short window`, async ({ page }) => {
      await page.setViewportSize({ width: 1600, height: 560 });
      await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'inline' });
      await openStop(page, paymentStop);
      await page
         .getByRole('region', { name: 'Stop', exact: true })
         .getByRole('button', { name: /shipping/ })
         .click();
      await center(page).getByRole('button', { name: 'Collapse to passages' }).click();
      const markers = center(page).locator('.lotsen-collapsed');
      await expect(markers).toHaveCount(2);
      const control = markers[which]().getByRole('button', { name: step });
      const hidden = async () => Number((await markers[which]().locator('.lotsen-collapsedCount').textContent())!.split(' ')[0]);
      const before = await hidden();

      await control.focus();
      await page.keyboard.press('Enter');

      await expect.poll(hidden).toBe(before - 20);
      const focused = page.locator(':focus');
      await expect(focused).toHaveClass(/lotsen-expand/);
      await expect.poll(() => inScrollerView(focused)).toBe(true);
   });
}

test('the splitter follows a drag across the side-by-side row until the release, and goes back to half on a double-click', async ({ page }) => {
   await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide' });
   await openStop(page, cartLineStop);
   const splitter = center(page).getByRole('separator');
   const row = center(page).locator('.cm-mergeViewEditors');
   const panes = center(page).locator('.cm-mergeViewEditor');
   const widths = async () => Promise.all([0, 1].map(async (index) => (await panes.nth(index).boundingBox())!.width));
   const rowBox = (await row.boundingBox())!;
   const handle = (await splitter.boundingBox())!;
   // The separator is too narrow for the mouse to reach it at another share of the row, so the moves outside a drag go to it as events, as a pointer capture would send them.
   const moveOverSplitter = (share: number) => splitter.dispatchEvent('pointermove', { clientX: rowBox.x + rowBox.width * share, pointerId: 1, isPrimary: true });

   await moveOverSplitter(0.7);
   await expect(splitter).toHaveAttribute('aria-valuenow', '50');
   await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2);
   await page.mouse.down();
   await page.mouse.move(rowBox.x + rowBox.width * 0.3, handle.y + handle.height / 2, { steps: 5 });
   await page.mouse.up();

   await expect(splitter).toHaveAttribute('aria-valuenow', '30');
   const [narrow, wide] = await widths();
   expect(narrow! / (narrow! + wide!)).toBeCloseTo(0.3, 1);

   await moveOverSplitter(0.7);
   await expect(splitter).toHaveAttribute('aria-valuenow', '30');
   await splitter.dblclick();
   await expect(splitter).toHaveAttribute('aria-valuenow', '50');
});

test('the stop bar shows once the full header has scrolled away, moves nothing, holds the stale notice on one line and leaves room for an opened check', async ({ page }) => {
   await page.setViewportSize({ width: 1280, height: 800 });
   await openDemoTour(page, checkoutTour, { placement: 'side' });
   const panel = page.getByRole('region', { name: 'Stop', exact: true });
   const scroller = panel.locator('.scroll-box');
   const bar = panel.locator('[data-stop-bar]');
   const scrollTo = (top: number) => scroller.evaluate((element, value) => void (element.scrollTop = value), top);
   const lineHeight = (element: Locator) => element.evaluate((node) => parseFloat(getComputedStyle(node).lineHeight));
   const height = async (element: Locator) => (await element.boundingBox())!.height;

   const gatewayStop = 'Gateway errors reach the customer as they are';
   await openStop(page, gatewayStop);
   await expect(bar).toBeHidden();
   expect(await scroller.evaluate((element) => element.scrollHeight > element.clientHeight)).toBe(true);

   // The first read is in the same task as the scroll, before the observer can show the bar.
   const headerEnd = (await edges(panel.locator('header').first())).bottom - (await edges(scroller)).top;
   const headlineBeforeBar = await scroller.evaluate((element, top) => {
      element.scrollTop = top;
      return element.querySelector('.headline')!.getBoundingClientRect().top;
   }, headerEnd + 2);
   await expect(bar).toBeVisible();
   expect((await edges(panel.locator('.headline'))).top).toBeCloseTo(headlineBeforeBar, 0);

   await scrollTo(1_000_000);
   await expect(bar).toBeVisible();
   expect((await edges(bar)).top).toBeCloseTo((await edges(scroller)).top, 0);
   await expect(bar.locator('h3')).toHaveText(gatewayStop);
   expect(await height(bar.locator('h3'))).toBeLessThanOrEqual((await lineHeight(bar.locator('h3'))) + 1);

   await expect(bar).toHaveCSS('user-select', 'none');

   // Returns how far below the bar the focused element sits, or null once focus left the panel or the bar hides at the top.
   const focusClearance = () =>
      scroller.evaluate((element) => {
         const barElement = element.querySelector('[data-stop-bar]')!;
         const focused = document.activeElement!;
         return element.contains(focused) && getComputedStyle(barElement).visibility === 'visible' ? focused.getBoundingClientRect().top - barElement.getBoundingClientRect().bottom : null;
      });
   await scroller.evaluate((element) => [...element.querySelectorAll<HTMLElement>('a[href], button:not([disabled]), select, textarea, input')].at(-1)!.focus());
   let focusStops = 0;
   for (let clearance = await focusClearance(); clearance !== null; clearance = await focusClearance()) {
      expect(clearance).toBeGreaterThanOrEqual(0);
      focusStops++;
      await page.keyboard.press('Shift+Tab');
   }
   expect(focusStops).toBeGreaterThan(5);

   const summaryRow = page
      .getByRole('tabpanel', { name: 'Plan' })
      .locator('button.stop-row')
      .filter({ has: page.locator('.title', { hasText: /^Summary$/ }) });
   await summaryRow.focus();
   await page.keyboard.press('Enter');
   // In this window the check and the bar do not fit in the panel together, so the bar covers the top of a check that the scroll places without a margin.
   await page.setViewportSize({ width: 1280, height: 440 });
   await center(page).getByRole('button', { name: 'Go to the check in version 1' }).click();
   const openedCheck = panel.locator('[data-check-id]:focus');
   await expect(openedCheck).toBeVisible();
   await expect(bar).toBeVisible();
   await expect.poll(async () => (await edges(openedCheck)).top - (await edges(bar)).bottom).toBeGreaterThan(0);
   await page.setViewportSize({ width: 1280, height: 800 });

   await openStop(page, paymentStop);
   await page
      .locator('[data-iteration-notice]')
      .getByRole('button', { name: /update/i })
      .click();
   await expect(panel.getByRole('status', { name: 'Stale stop' })).toBeVisible();
   await scrollTo(0);
   await expect(bar).toBeHidden();
   await scrollTo(1_000_000);
   await expect(bar).toBeVisible();
   const staleLine = bar.locator('[title^="Stale since update"]');
   await expect(staleLine).toBeVisible();
   await expect(staleLine.locator('svg')).toBeVisible();
   expect(await height(staleLine.locator('p'))).toBeLessThanOrEqual((await lineHeight(staleLine.locator('p'))) + 1);
   await expect(panel.getByRole('status', { name: 'Stale stop' })).toHaveText((await staleLine.getAttribute('title'))!);
});

test.describe('the stop-files stack', () => {
   /** The name of the file that the Files panel marks as the one the reviewer reads. */
   const current = (page: Page) => page.getByRole('tabpanel', { name: 'Files' }).locator('[aria-current="true"]').last();
   const stack = (page: Page) => page.getByRole('list', { name: 'Files in this stop' });

   test('reports the file at the top of its viewport as it scrolls, and each file that the reviewer opens', async ({ page }) => {
      // A short window makes the stack several screens long.
      await page.setViewportSize({ width: 1600, height: 560 });
      await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide', viewMode: 'stopFiles' });
      await openStop(page, paymentStop);
      await expect(stack(page).getByRole('listitem')).toHaveCount(4);
      await expect(stack(page).locator('.cm-editor')).toHaveCount(8);
      await page.getByRole('tab', { name: 'Files' }).click();
      await expect(current(page)).toContainText('payment.ts');

      // The reviewer's wheel scrolls the stack, and then the second file's top just past the top of the stack.
      const second = stack(page).getByRole('listitem').nth(1);
      await stack(page).hover();
      await page.mouse.wheel(0, 40);
      await second.evaluate((item) => {
         const viewport = item.closest('ul')!.parentElement!;
         viewport.scrollTop += item.getBoundingClientRect().top - viewport.getBoundingClientRect().top + 1;
      });
      await expect(current(page)).toContainText('cart.ts');

      // The last file cannot scroll to the top of the stack, and is still the one the reviewer opened.
      await page
         .getByRole('region', { name: 'Stop', exact: true })
         .getByRole('button', { name: /shipping/ })
         .click();
      await expect(current(page)).toContainText('shipping.ts');
      await page
         .getByRole('region', { name: 'Stop', exact: true })
         .getByRole('button', { name: /quantity check/ })
         .click();
      await expect(current(page)).toContainText('cart.ts');
   });
});

test.describe('the Threads panel', () => {
   const threads = (page: Page) => page.getByRole('region', { name: 'Threads', exact: true });
   const field = (page: Page) => threads(page).getByRole('textbox', { name: 'Question for Claude' });

   test('keeps the ask box in view at the bottom of the panel after the thread list scrolls to its end', async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 600 });
      await openDemoTour(page, checkoutTour);
      for (let index = 1; index <= 8; index++) {
         await field(page).fill(`Note ${index}, to read later.`);
         await threads(page).getByRole('button', { name: 'Save as note' }).click();
         await expect(field(page)).toHaveValue('');
      }

      const list = threads(page).getByRole('list', { name: 'Threads' });
      await expect(list.getByRole('listitem').filter({ hasText: 'Note 8, to read later.' })).toHaveCount(1);
      const scroller = list.locator('xpath=parent::*');
      expect(await scroller.evaluate((node) => node.scrollHeight > node.clientHeight)).toBe(true);

      await scroller.evaluate((node) => node.scrollTo(0, node.scrollHeight));

      await expect.poll(() => scroller.evaluate((node) => Math.ceil(node.scrollTop + node.clientHeight) >= node.scrollHeight)).toBe(true);
      const form = field(page).locator('xpath=ancestor::form[1]');
      const panel = await edges(threads(page));
      const box = await edges(form);
      expect(box.bottom).toBeCloseTo(panel.bottom, 0);
      expect(box.bottom).toBeLessThanOrEqual(600);
      expect((await edges(scroller)).bottom).toBeCloseTo(box.top, 0);
      await expect(list.getByRole('listitem').last()).toBeInViewport();
      await expect(field(page)).toBeInViewport();
   });

   test('keeps the follow-up box in view at the bottom of a thread view while a long thread scrolls, and the list where it was after the back arrow', async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 600 });
      await openDemoTour(page, checkoutTour);
      const list = threads(page).getByRole('list', { name: 'Threads' });
      const listScroller = list.locator('xpath=parent::*');
      // The keyboard opens the row, as in `openStop`, and the list scrolls after the focus moved the row into view.
      const row = list.getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' }).getByRole('button').first();
      await row.focus();
      await listScroller.evaluate((node) => node.scrollBy(0, 30));
      const listTop = await listScroller.evaluate((node) => node.scrollTop);
      expect(listTop).toBeGreaterThan(0);
      await page.keyboard.press('Enter');

      const view = threads(page).getByRole('article');
      const follow = view.getByRole('textbox', { name: 'Follow-up for Claude' });
      await expect(follow).toBeVisible();
      const scroller = view.locator('[data-messages]');
      expect(await scroller.evaluate((node) => node.scrollHeight > node.clientHeight)).toBe(true);
      await scroller.evaluate((node) => node.scrollTo(0, node.scrollHeight));

      await expect.poll(() => scroller.evaluate((node) => Math.ceil(node.scrollTop + node.clientHeight) >= node.scrollHeight)).toBe(true);
      const form = follow.locator('xpath=ancestor::form[1]');
      const panel = await edges(threads(page));
      const box = await edges(form);
      expect(box.bottom).toBeCloseTo(panel.bottom, 0);
      expect(box.bottom).toBeLessThanOrEqual(600);
      expect((await edges(scroller)).bottom).toBeCloseTo(box.top, 0);
      await expect(view.locator('[data-writer]').last()).toBeInViewport();
      await expect(follow).toBeInViewport();

      await view.getByRole('button', { name: 'Back to all threads' }).click();

      await expect(list).toBeVisible();
      await expect.poll(() => listScroller.evaluate((node) => node.scrollTop)).toBe(listTop);
   });

   test('keeps the list where it was after the back arrow when its thread view opened before the scroll event fired', async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 600 });
      await openDemoTour(page, checkoutTour);
      const list = threads(page).getByRole('list', { name: 'Threads' });
      const listScroller = list.locator('xpath=parent::*');
      const row = list.getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' }).getByRole('button').first();
      // The browser fires the scroll event at the next frame, after the thread view replaced the list.
      const listTop = await row.evaluate((button: HTMLElement) => {
         const scroller = button.closest('ul')!.parentElement!;
         button.focus();
         scroller.scrollBy(0, 30);
         button.click();
         return scroller.scrollTop;
      });
      expect(listTop).toBeGreaterThan(0);

      await threads(page).getByRole('button', { name: 'Back to all threads' }).click();

      await expect(list).toBeVisible();
      await expect.poll(() => listScroller.evaluate((node) => node.scrollTop)).toBe(listTop);
   });

   test('opens one thread menu from a row with Shift+F10, the context-menu key and a right-click', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const row = threads(page).getByRole('list', { name: 'Threads' }).getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' });
      const menus = page.getByRole('menu');

      for (const key of ['Shift+F10', 'ContextMenu']) {
         await row.getByRole('button').first().focus();
         await page.keyboard.press(key);
         await expect(menus).toHaveCount(1);
         await expect(menus.getByRole('menuitem', { name: 'Delete thread…' })).toBeVisible();
         await page.keyboard.press('Escape');
         await expect(menus).toHaveCount(0);
      }

      await row.click({ button: 'right' });
      await expect(menus).toHaveCount(1);
      await expect(menus.getByRole('menuitem', { name: 'Delete thread…' })).toBeInViewport();
   });

   test("opens the right-click menu on a right-click on a row while the menu of the row's ⋯ button shows", async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const row = threads(page).getByRole('list', { name: 'Threads' }).getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' });
      const menus = page.getByRole('menu');
      await row.getByRole('button', { name: 'Thread actions' }).click();
      await expect(menus).toHaveCount(1);

      await row.click({ button: 'right' });

      await expect(menus).toHaveCount(1);
      await expect(menus).toHaveAttribute('data-context-menu-content');
   });

   test('grows the one-line field with the words', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const before = (await field(page).boundingBox())!.height;

      await field(page).fill('One\nTwo\nThree');

      await expect.poll(async () => (await field(page).boundingBox())!.height).toBeGreaterThan(before * 2);
   });

   test('keeps the one-line field one line tall when the panel shows again after another tour opened while it was hidden', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const before = (await field(page).boundingBox())!.height;
      await page.getByRole('button', { name: 'Hide the threads' }).click();

      await page.getByRole('button', { name: 'Choose another tour' }).click();
      await page
         .getByRole('navigation', { name: 'Tours' })
         .getByRole('button', { name: /storefront #433/ })
         .first()
         .click();
      await showPanel(page, 'Threads');

      await expect(field(page)).toBeVisible();
      expect((await field(page).boundingBox())!.height).toBeCloseTo(before, 0);
   });

   test('keeps the height that the words gave the one-line field while the panel hides and shows again', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await field(page).fill('One\nTwo\nThree');
      const grown = (await field(page).boundingBox())!.height;

      await page.getByRole('button', { name: 'Hide the threads' }).click();
      await showPanel(page, 'Threads');

      await expect(field(page)).toBeVisible();
      expect((await field(page).boundingBox())!.height).toBeCloseTo(grown, 0);
   });
});

test.describe('reorder by drag', () => {
   const waitingTour = 'storefront #433';
   const rowsOf = (page: Page) => page.getByRole('list', { name: 'Proposed order' }).getByRole('listitem');
   const rowTitles = (page: Page) => rowsOf(page).locator('.title').allTextContents();
   const rowNumbers = (page: Page) => rowsOf(page).locator('.number').allTextContents();

   /** Collects the stop IDs of each plan that the page reads, by stop title. */
   function stopIdsByTitle(page: Page): Map<string, string> {
      const ids = new Map<string, string>();
      page.on('response', async (response) => {
         if (response.url().endsWith('/api/ops/read_state')) {
            const state = (await response.json()) as { output: { plan?: { stops: { id: string; title: string }[] } | null } };
            for (const stop of state.output.plan?.stops ?? []) {
               ids.set(stop.title, stop.id);
            }
         }
      });
      return ids;
   }

   /** Drags the row at `from` onto the top or the bottom edge of the row at `to`. */
   async function dragRow(page: Page, from: number, to: number, edge: 'top' | 'bottom'): Promise<void> {
      const target = rowsOf(page).nth(to);
      const box = (await target.boundingBox())!;
      await rowsOf(page)
         .nth(from)
         .dragTo(target, { targetPosition: { x: box.width / 2, y: edge === 'top' ? 3 : box.height - 3 } });
   }

   // The demo's own panel floats over the bottom left of the page, and a drag needs the rows clear of it.
   async function closeDemoPanel(page: Page): Promise<void> {
      await page.locator('details', { hasText: 'Demo' }).first().locator('summary').click();
   }

   test('reorders a plan that waits for approval, Approve sends the order, and keyboard focus shows the arrows of a row', async ({ page }) => {
      const ids = stopIdsByTitle(page);
      await openDemoTour(page, waitingTour);
      await closeDemoPanel(page);
      // The Plan tab shows before the plan arrives, and the drag below needs three rows.
      await expect(rowsOf(page).nth(2)).toBeVisible({ timeout: waitTimeout });
      const before = await rowTitles(page);
      await expect(page.getByRole('button', { name: 'Reorder stops' })).toHaveCount(0);

      await dragRow(page, 0, 2, 'bottom');
      await expect.poll(() => rowTitles(page)).toEqual([before[1]!, before[2]!, before[0]!, ...before.slice(3)]);
      expect(await rowNumbers(page)).toEqual(before.map((_, index) => String(index + 1)));

      await page.mouse.move(0, 0);
      const arrows = (row: number) => rowsOf(page).nth(row).locator('.move-buttons');
      await expect(arrows(0)).toHaveCSS('opacity', '0');
      await rowsOf(page).nth(0).getByRole('button', { name: /down$/ }).focus();
      await expect(arrows(0)).toHaveCSS('opacity', '1');
      await expect(arrows(1)).toHaveCSS('opacity', '0');
      await page.keyboard.press('Enter');
      const moved = [before[2]!, before[1]!, before[0]!, ...before.slice(3)];
      await expect.poll(() => rowTitles(page)).toEqual(moved);

      const approval = page.waitForRequest((request) => request.url().endsWith('/api/ops/approve_plan'));
      await page.getByRole('button', { name: 'Approve plan' }).click();

      expect(((await approval).postDataJSON() as { stopIds: string[] }).stopIds).toEqual(moved.map((title) => ids.get(title)));
   });

   test('reorders a plan that already exists with Reorder, and Send to Claude sends the order', async ({ page }) => {
      const ids = stopIdsByTitle(page);
      await openDemoTour(page, checkoutTour);
      await closeDemoPanel(page);
      await page.getByRole('button', { name: 'Plan actions' }).click();
      await page.getByRole('menuitem', { name: 'Reorder…' }).click();
      const before = await rowTitles(page);
      await expect(
         rowsOf(page)
            .first()
            .getByRole('button', { name: /^Skip stop/ }),
      ).toHaveCount(0);

      await dragRow(page, 2, 0, 'top');
      const moved = [before[2]!, before[0]!, before[1]!, ...before.slice(3)];
      await expect.poll(() => rowTitles(page)).toEqual(moved);
      expect(await rowNumbers(page)).toEqual(before.map((_, index) => String(index + 1)));

      const action = page.waitForRequest((request) => request.url().endsWith('/api/ops/request_tour_action'));
      await page.getByRole('button', { name: 'Send to Claude' }).click();

      expect(((await action).postDataJSON() as { action: { stopIds: string[] } }).action.stopIds).toEqual(moved.map((title) => ids.get(title)));
   });
});

test.describe('the Files tree', () => {
   const tree = (page: Page) => page.getByRole('tree', { name: 'Changed files' });
   /** The row of a tree item, without the rows of the item's subtree. */
   const row = (item: Locator) => item.locator(':scope > .row');

   for (const theme of ['light', 'dark'] as const) {
      test(`draws a 16-pixel review mark in the twisty's column, in the success color once checked, in a row as tall as a folder's, in the ${theme} theme`, async ({ page }) => {
         await openDemoTour(page, checkoutTour, { theme });
         await expect(page.locator('html')).toHaveAttribute('data-color-scheme', theme);
         await page.getByRole('tab', { name: 'Files' }).click();
         // Both rows sit at the top level of the tree, and only the file has a review mark.
         const file = tree(page).getByRole('treeitem', { name: /^package\.json/ });
         const folder = tree(page).getByRole('treeitem', { name: 'assets' });
         const mark = row(file).locator('.check');
         const color = () => mark.evaluate((node) => getComputedStyle(node).color);
         const height = async (item: Locator) => (await row(item).boundingBox())!.height;
         const left = async (item: Locator) => (await row(item).locator('.icon').boundingBox())!.x;

         const icon = (await mark.locator('svg').boundingBox())!;
         expect([icon.width, icon.height]).toEqual([16, 16]);
         expect(await left(file)).toBeCloseTo(await left(folder), 1);
         expect(await color()).toBe(await themeColor(mark, '--muted-foreground'));

         await file.focus();
         await page.keyboard.press('Space');

         await expect(file).toHaveAttribute('aria-checked', 'true');
         await expect.poll(color).toBe(await themeColor(mark, '--success'));
         expect(await height(file)).toBeCloseTo(await height(folder), 1);
      });
   }
});

test.describe('the page height', () => {
   const picker = (page: Page) => page.getByRole('button', { name: /^(Choose a tour|Choose another tour)$/ });
   const tourList = (page: Page) => page.getByRole('navigation', { name: 'Tours' });
   const plan = (page: Page) => page.getByRole('tabpanel', { name: 'Plan' });

   /** The app's own panels, which leave out the demo's panel. */
   const panels = (page: Page) =>
      page
         .getByRole('main')
         .or(page.getByRole('region', { name: 'Files', exact: true }))
         .or(page.getByRole('region', { name: 'Plan', exact: true }))
         .or(page.getByRole('region', { name: 'Stop', exact: true }))
         .or(page.getByRole('region', { name: 'Threads', exact: true }));

   /** Returns whether a scroll box inside the app's panels holds more than it shows. */
   const panelOverflows = (page: Page) =>
      panels(page).evaluateAll((roots) =>
         roots.some((root) => [root, ...root.querySelectorAll('*')].some((node) => ['auto', 'scroll'].includes(getComputedStyle(node).overflowY) && node.scrollHeight > node.clientHeight)),
      );

   async function expectWindowHeight(page: Page, view: string) {
      // The Files panel fills only when its list of changed files arrives, after the tab shows.
      await expect.poll(() => panelOverflows(page), { message: `${view}: a panel holds more than the window shows`, timeout: waitTimeout }).toBe(true);
      const [documentHeight, windowHeight] = await page.evaluate(() => [document.scrollingElement!.scrollHeight, window.innerHeight]);
      expect.soft(documentHeight, `${view}: the document is as tall as the window`).toBe(windowHeight);
   }

   test('stays at the window height in every demo tour, panel and stop, while panels hold more than the window shows', async ({ page }) => {
      test.setTimeout(120_000);
      await page.setViewportSize({ width: 1440, height: 600 });
      await page.goto('/');
      await picker(page).click();
      await page.getByRole('checkbox', { name: /^Show hidden tours/ }).check();
      const tours = (await tourList(page).getByRole('listitem').allInnerTexts()).map((text) => text.split('\n')[0]!);
      expect(tours.length).toBeGreaterThan(1);
      await page.keyboard.press('Escape');

      for (const tour of tours) {
         await picker(page).click();
         await tourList(page).getByRole('button', { name: tour, exact: false }).first().click();
         await expect(page.getByRole('banner').getByRole('link', { name: tour, exact: true })).toBeVisible();
         for (const region of ['Stop', 'Threads']) {
            await expect(page.getByRole('region', { name: region, exact: true })).toBeVisible();
         }

         for (const panel of ['Files', 'Plan']) {
            await page.getByRole('tab', { name: panel }).click();
            await expect(page.getByRole('tabpanel', { name: panel })).toBeVisible();
            await expectWindowHeight(page, `${tour}, ${panel}`);
         }

         // The keyboard opens each row, as in `openStop`.
         const rows = plan(page).locator('button.stop-row');
         for (let index = 0; index < (await rows.count()); index++) {
            const row = rows.nth(index);
            const title = (await row.locator('.title').textContent())!;
            await row.focus();
            await page.keyboard.press('Enter');
            await expect(row).toHaveAttribute('aria-current', 'step');
            const shown = title === 'Summary' ? center(page).getByRole('heading', { name: 'Summary' }) : page.getByRole('region', { name: 'Stop', exact: true }).getByRole('heading', { name: title });
            await expect(shown).toBeVisible();
            await expectWindowHeight(page, `${tour}, ${title}`);
         }
      }
   });
});
