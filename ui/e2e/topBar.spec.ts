import { expect, test, type Locator, type Page } from '@playwright/test';
import { checkoutTour, contrast, drawnColors, expectPalette, openDemoTour, openStop, over, paletteName, palettes } from './demo';

const header = (page: Page) => page.getByRole('banner');
const tourZone = (page: Page) => header(page).locator('[data-tour-zone]');
const updateCheck = (page: Page) => header(page).getByRole('button', { name: 'Check the PR for a new update' });
const sendAnswers = (page: Page) => header(page).getByRole('button', { name: 'Send answers' });
const statusChip = (page: Page) => header(page).locator('[data-status-chip]');

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
      const state = statusChip(page);
      await expect(state).toHaveAccessibleName('Claude session: Confirmed');
      const style = await styleOf(state, ['border-top-left-radius', 'border-top-color', 'border-top-width']);
      const height = (await state.boundingBox())!.height;

      expect(parseFloat(style['border-top-left-radius'])).toBeGreaterThanOrEqual(height / 2);
      expect(style['border-top-width']).not.toBe('0px');
      expect(transparent(style['border-top-color']), `the pill's edge, ${style['border-top-color']}`).toBe(false);
   });
});

test.describe("the top bar's status chip", () => {
   test.beforeEach(async ({ page }) => {
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await page.mouse.move(0, 500);
   });

   const popover = (page: Page) => page.getByRole('dialog', { name: 'Claude session and connection' });

   test('opens a popover on click with the session, the connection and what Claude is doing, and no tooltip over it', async ({ page }) => {
      await statusChip(page).click();

      await expect(popover(page).locator('dt')).toHaveText(['Session', 'Connection', 'Now']);
      await expect(popover(page).locator('[data-status-row="session"]')).toHaveText('Confirmed. Questions reach Claude.');
      await expect(popover(page).locator('[data-status-row="connection"]')).toHaveText('Live. Updates arrive as Claude writes.');
      await expect(popover(page).locator('[data-status-row="generation"]')).toHaveText(/^Claude is writing stop \d+ of \d+…$/);
      // The pointer stays on the chip past the tooltip's delay, then leaves it and returns while the popover shows.
      await page.waitForTimeout(800);
      await expect(page.locator('[data-tooltip-content]')).toHaveCount(0);
      await page.mouse.move(0, 500);
      await statusChip(page).hover();
      await page.waitForTimeout(800);
      await expect(popover(page)).toBeVisible();
      await expect(page.locator('[data-tooltip-content]')).toHaveCount(0);
   });

   test('closes the popover on Escape, returns the focus to the chip, and opens it again on Enter', async ({ page }) => {
      await statusChip(page).click();
      await expect(popover(page)).toBeVisible();

      await page.keyboard.press('Escape');
      await expect(popover(page)).toHaveCount(0);
      await expect(statusChip(page)).toBeFocused();

      await page.keyboard.press('Enter');
      await expect(popover(page).locator('dt')).toHaveText(['Session', 'Connection', 'Now']);
   });

   test('opened from the keyboard, shows no tooltip over the popover past its delay, and closes the popover on one Escape', async ({ page }) => {
      await page.keyboard.press('Shift');
      await statusChip(page).focus();
      await expect(page.locator('[data-tooltip-content]')).toBeVisible();
      await page.keyboard.press('Enter');
      await expect(popover(page)).toBeVisible();

      await page.waitForTimeout(800);
      await expect(page.locator('[data-tooltip-content]')).toHaveCount(0);
      await page.keyboard.press('Escape');
      await expect(popover(page)).toHaveCount(0);
      await expect(statusChip(page)).toBeFocused();
   });
});

test.describe("the top bar's Panels menu", () => {
   test.beforeEach(async ({ page }) => {
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await page.mouse.move(0, 500);
   });

   const panels = (page: Page) => header(page).getByRole('button', { name: 'Panels', exact: true });
   const menu = (page: Page) => page.getByRole('menu');

   // The pointer stays on Panels past the tooltip's delay, as a slow page keeps it there between the click and the next step.
   test('opened with a click, shows no tooltip over the menu past its delay, and closes the menu on one Escape', async ({ page }) => {
      await panels(page).click();
      await expect(menu(page)).toBeVisible();

      await page.waitForTimeout(800);
      await expect(page.locator('[data-tooltip-content]')).toHaveCount(0);
      await page.keyboard.press('Escape');
      await expect(menu(page)).toHaveCount(0);
      await expect(panels(page)).toBeFocused();
   });

   test('opened from the keyboard, shows no tooltip over the menu past its delay, and closes the menu on one Escape', async ({ page }) => {
      await page.keyboard.press('Shift');
      await panels(page).focus();
      await expect(page.locator('[data-tooltip-content]')).toBeVisible();
      await page.keyboard.press('Enter');
      await expect(menu(page)).toBeVisible();

      await page.waitForTimeout(800);
      await expect(page.locator('[data-tooltip-content]')).toHaveCount(0);
      await page.keyboard.press('Escape');
      await expect(menu(page)).toHaveCount(0);
      await expect(panels(page)).toBeFocused();
   });
});

test.describe("the top bar's zones", () => {
   const gatewayStop = 'Gateway errors reach the customer as they are';

   test('give the tour zone its content width at 1600px, so the stop line shows the start of the stop title', async ({ page }) => {
      await page.setViewportSize({ width: 1600, height: 1000 });
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await openStop(page, gatewayStop);

      const { zone, content } = await tourZone(page).evaluate((node) => {
         const boxes = [...node.querySelectorAll('*')].map((child) => child.getBoundingClientRect()).filter((box) => box.width > 0);
         return { zone: node.getBoundingClientRect().width, content: Math.max(...boxes.map((box) => box.right)) - Math.min(...boxes.map((box) => box.left)) };
      });
      expect.soft(zone, `the tour zone's width beside its content's ${content}px`).toBeLessThanOrEqual(content + 1);

      const line = header(page).getByRole('navigation', { name: 'Stops' }).locator('p');
      const { lineRight, wordRight } = await line.evaluate((node, title) => {
         const text = [...node.querySelectorAll('span')].find((span) => span.textContent === title)!.firstChild!;
         const word = document.createRange();
         word.setStart(text, 0);
         word.setEnd(text, title.indexOf(' '));
         return { lineRight: node.getBoundingClientRect().right, wordRight: word.getBoundingClientRect().right };
      }, gatewayStop);
      expect(wordRight, "the right edge of the title's first word beside the line's").toBeLessThanOrEqual(lineRight);
   });

   test('keep the update check at one x on tours whose bars show different items', async ({ page }) => {
      const edges: number[] = [];
      for (const tour of [checkoutTour, 'storefront #428', 'storefront #433']) {
         await openDemoTour(page, tour, { fontSize: 'medium' });
         await expect(header(page).getByRole('link', { name: tour, exact: true })).toBeVisible();
         edges.push((await updateCheck(page).boundingBox())!.x);
      }

      for (const edge of edges.slice(1)) {
         expect(Math.abs(edge - edges[0]!), `the update check's left edges, ${edges.join(', ')}`).toBeLessThanOrEqual(1);
      }
   });

   test('separate the tour, the stop navigation, and Claude and the workspace with two dividers that take no focus', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      const dividers = header(page).locator('[data-zone-divider]');
      await expect(dividers).toHaveCount(2);

      const box = async (element: Locator) => (await element.boundingBox())!;
      const zone = await box(tourZone(page));
      const first = await box(dividers.nth(0));
      const stops = await box(header(page).getByRole('navigation', { name: 'Stops' }));
      const second = await box(dividers.nth(1));
      // The checkout tour has unsent answers and a confirmed session, so Send answers opens the third zone.
      const answers = await box(sendAnswers(page));
      expect(first.x).toBeGreaterThanOrEqual(zone.x + zone.width);
      expect(first.x + first.width).toBeLessThanOrEqual(stops.x);
      expect(second.x).toBeGreaterThanOrEqual(stops.x + stops.width);
      expect(second.x + second.width).toBeLessThanOrEqual(answers.x);

      for (const divider of await dividers.all()) {
         await expect(divider).toHaveAttribute('aria-hidden', 'true');
         expect(await divider.evaluate((node: HTMLElement) => (node.focus(), document.activeElement === node))).toBe(false);
      }
   });

   test('show no dividers while no tour is open', async ({ page }) => {
      await page.goto('/');
      await expect(header(page).getByRole('button', { name: 'Choose a tour', exact: true })).toBeVisible();
      await expect(header(page).locator('[data-zone-divider]')).toHaveCount(0);
   });
});

test.describe("the refusal of the answers' send", () => {
   test('shows in a strip right under the bar, above the update notice, until Dismiss, which returns the focus to Send answers', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await page.route('**/api/ops/send_answers', (route) => route.fulfill({ status: 500, body: 'The store failed.' }));
      await sendAnswers(page).click();

      const strip = page.locator('[data-answer-refusal]');
      await expect(strip.getByRole('alert')).toHaveText('The answers could not be sent.');
      const bar = (await header(page).boundingBox())!;
      const shown = (await strip.boundingBox())!;
      expect(shown.y, "the strip's top beside the bar's bottom").toBeCloseTo(bar.y + bar.height, 0);
      expect(shown.width, "the strip's width").toBeCloseTo(bar.width, 0);
      expect((await page.locator('[data-iteration-notice]').boundingBox())!.y, "the update notice's top").toBeGreaterThanOrEqual(shown.y + shown.height - 0.5);

      await strip.getByRole('button', { name: 'Dismiss' }).click();
      await expect(strip).toHaveCount(0);
      await expect(sendAnswers(page)).toBeFocused();
      await expect(sendAnswers(page).locator('[data-unsent-count]')).toBeVisible();
   });
});

test.describe('the stop line', () => {
   // At 2400px the bar has room for the whole stop line, so a cap on the line alone sets the line's room.
   test.use({ viewport: { width: 2400, height: 1000 } });

   const line = (page: Page) => header(page).getByRole('navigation', { name: 'Stops' }).locator('p');

   /** Caps the width of the live region that holds the stop line, or lifts the cap given null. */
   const capLine = (page: Page, width: number | null) =>
      line(page).evaluate((node, cap) => {
         node.parentElement!.style.maxWidth = cap === null ? '' : `${cap}px`;
      }, width);

   /** A part of the stop line: its rendered width, the width of its whole text, and its edges. */
   type Part = { width: number; full: number; left: number; right: number };

   /** Measures the stop line: the long position, the title's separator, the title's first word, and each element that `texts` names by its text. */
   const measure = <N extends string>(page: Page, texts: Record<N, string>) =>
      line(page)
         .evaluate(
            (node, wanted) => {
               const parts = [...node.children] as HTMLElement[];
               // A range over the part's contents measures its text whole, also where the part clips it.
               const fullWidth = (part: HTMLElement) => {
                  const contents = document.createRange();
                  contents.selectNodeContents(part);
                  return contents.getBoundingClientRect().width;
               };
               const box = (part: HTMLElement): Part => {
                  const { width, left, right } = part.getBoundingClientRect();
                  return { width, full: fullWidth(part), left, right };
               };
               const elements = [...node.querySelectorAll('*')] as HTMLElement[];
               const named = Object.fromEntries(Object.entries(wanted).map(([name, text]) => [name, box(elements.find((element) => element.textContent.replace(/\s+/g, ' ').trim() === text)!)]));
               const separator = parts.at(-1)!.firstElementChild as HTMLElement;
               const walker = document.createTreeWalker(separator, NodeFilter.SHOW_TEXT);
               while (!walker.nextNode()!.textContent!.includes('·'));
               const dot = document.createRange();
               dot.setStart(walker.currentNode, walker.currentNode.textContent!.indexOf('·'));
               dot.setEnd(walker.currentNode, walker.currentNode.textContent!.indexOf('·') + 1);
               const title = parts.at(-1)!.lastElementChild!.firstChild!;
               const word = document.createRange();
               word.setStart(title, 0);
               word.setEnd(title, title.textContent!.indexOf(' '));
               return {
                  line: { width: node.getBoundingClientRect().width, right: node.getBoundingClientRect().right },
                  full: parts.reduce((sum, part) => sum + fullWidth(part), 0),
                  position: box(node.querySelector('[data-stop-position="long"]')!),
                  separator: box(separator),
                  dot: { left: dot.getBoundingClientRect().left, right: dot.getBoundingClientRect().right },
                  named,
                  wordLeft: word.getBoundingClientRect().left,
                  wordRight: word.getBoundingClientRect().right,
                  live: (node.closest('[aria-live="polite"]') as HTMLElement).innerText.replace(/\s+/g, ' ').trim(),
               };
            },
            texts as Record<string, string>,
         )
         .then(({ named, ...rest }) => ({ ...rest, ...(named as Record<N, Part>) }));

   /** Returns the distance from the stop line's left edge to the end of the first text that holds `text`, at the line's current width. */
   const textEnd = (page: Page, text: string) =>
      line(page).evaluate((node, wanted) => {
         const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
         while (!walker.nextNode()!.textContent!.includes(wanted));
         const range = document.createRange();
         range.setStart(walker.currentNode, walker.currentNode.textContent!.indexOf(wanted));
         range.setEnd(walker.currentNode, walker.currentNode.textContent!.indexOf(wanted) + wanted.length);
         return range.getBoundingClientRect().right - node.getBoundingClientRect().left;
      }, text);

   /** Returns how the stop line ends: the parts that run past its right edge, the parts cut short without an ellipsis, and whether the last part with room ends in one. */
   const lineEnd = (page: Page) =>
      line(page).evaluate((node) => {
         const right = node.getBoundingClientRect().right;
         const shown = ([...node.children] as HTMLElement[]).filter((part) => part.getBoundingClientRect().width > 0);
         const last = shown.at(-1)!;
         return {
            spills: shown.filter((part) => part.getBoundingClientRect().right > right + 0.5).map((part) => part.textContent.replace(/\s+/g, ' ').trim()),
            bare: shown.filter((part) => part.scrollWidth > part.clientWidth && getComputedStyle(part).textOverflow !== 'ellipsis').map((part) => part.textContent.replace(/\s+/g, ' ').trim()),
            last: last.textContent.replace(/\s+/g, ' ').trim(),
            ellipsis: getComputedStyle(last).textOverflow === 'ellipsis' && last.scrollWidth > last.clientWidth,
         };
      });

   test('ends the line in an ellipsis where it lacks the room for the position and Skipped', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await openStop(page, 'The product data file');
      const positionEnd = await textEnd(page, 'Stop 7 of 7');
      const skippedEnd = await textEnd(page, 'Skipped');

      for (const [cap, where] of [
         [skippedEnd - 20, 'inside Skipped'],
         [positionEnd - 20, 'inside the position'],
         [positionEnd / 2, 'at half the position'],
      ] as const) {
         await capLine(page, cap);
         const end = await lineEnd(page);
         expect.soft(end.spills, `the parts past the line's right edge with a ${cap}px line, ${where}`).toEqual([]);
         expect.soft(end.ellipsis, `whether "${end.last}" ends in an ellipsis with a ${cap}px line, ${where}`).toBe(true);
      }
   });

   test('gives way in order at every width, and ends a cut line in an ellipsis', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      for (const [title, chapter] of [
         ['Gateway errors reach the customer as they are', 'Paying'],
         ['The product data file', 'Before you approve'],
      ] as const) {
         await openStop(page, title);
         const parts = { chapter: `· ${chapter}`, title: `· ${title}` };
         await capLine(page, null);
         const natural = await measure(page, parts);

         const faults: string[] = [];
         for (let cap = Math.ceil(natural.full); cap >= 10; cap -= 4) {
            await capLine(page, cap);
            const at = await measure(page, parts);
            const end = await lineEnd(page);
            if (at.chapter.width > 0 && at.title.width < at.title.full - 0.5) faults.push(`${cap}px: the title is cut while the chapter shows`);
            if (end.spills.length > 0) faults.push(`${cap}px: ${end.spills.join(', ')} past the line's right edge`);
            if (end.bare.length > 0) faults.push(`${cap}px: ${end.bare.join(', ')} cut short without an ellipsis`);
         }
         expect(faults, title).toEqual([]);
      }
   });

   test('lets the chapter give way before the title truncates, and keeps the full line in the live region', async ({ page }) => {
      const title = 'Gateway errors reach the customer as they are';
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await openStop(page, title);
      const parts = { chapter: '· Paying', title: `· ${title}` };
      const natural = await measure(page, parts);

      await capLine(page, natural.full);
      const wide = await measure(page, parts);
      expect(wide.line.width, "the line's width at its cap").toBeCloseTo(natural.full, 0);
      expect(wide.chapter.width, "the chapter's width").toBeCloseTo(wide.chapter.full, 0);
      expect(wide.title.width, "the title's width").toBeCloseTo(wide.title.full, 0);

      await capLine(page, natural.full - natural.chapter.full / 2);
      const partly = await measure(page, parts);
      expect(partly.chapter.width, "the chapter's width with half its room").toBeGreaterThan(0);
      expect(partly.chapter.width).toBeLessThan(partly.chapter.full);
      expect(partly.title.width, "the title's width while the chapter gives way").toBeCloseTo(partly.title.full, 0);

      await capLine(page, natural.full - natural.chapter.full - natural.title.full / 2);
      const narrow = await measure(page, parts);
      expect(narrow.chapter.width, "the chapter's width with no room for the whole line").toBe(0);
      expect(narrow.separator.left, "the title's separator beside the position's right edge").toBeCloseTo(narrow.position.right, 1);
      expect(narrow.dot.left - narrow.position.right, 'the space before the dot').toBeGreaterThan(2);
      expect(narrow.wordLeft - narrow.dot.right, 'the space after the dot').toBeGreaterThan(2);
      expect(narrow.title.width).toBeGreaterThan(0);
      expect(narrow.wordRight, "the right edge of the title's first word beside the line's").toBeLessThanOrEqual(narrow.line.right);
      expect(narrow.live).toBe(`Stop 4 of 7 · Paying · ${title}`);
      await expect(line(page).locator('[data-stop-position="long"]')).toBeVisible();
      await expect(line(page).locator('[data-stop-position="short"]')).toBeHidden();
   });

   test('keeps Skipped whole once the chapter has given way', async ({ page }) => {
      const title = 'The product data file';
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await openStop(page, title);
      const parts = { skipped: '· Skipped', chapter: '· Before you approve', title: `· ${title}` };
      const natural = await measure(page, parts);

      await capLine(page, natural.full - natural.chapter.full - natural.title.full / 2);
      const narrow = await measure(page, parts);
      expect(narrow.chapter.width, "the chapter's width").toBe(0);
      expect(narrow.separator.left, "the title's separator beside Skipped's right edge").toBeCloseTo(narrow.skipped.right, 1);
      expect(narrow.skipped.right, "Skipped's right edge beside the line's").toBeLessThanOrEqual(narrow.line.right);
      expect(narrow.wordRight, "the right edge of the title's first word beside the line's").toBeLessThanOrEqual(narrow.line.right);
      expect(narrow.live).toBe(`Stop 7 of 7 · Skipped · Before you approve · ${title}`);
   });

   test('keeps the summary page on one line, cut short where it lacks the room', async ({ page }) => {
      await openDemoTour(page, 'storefront #394', { fontSize: 'medium' });
      await expect(line(page)).toHaveText(/^Summary\s*·\s*After the last stop$/);
      const height = (await line(page).boundingBox())!.height;

      await capLine(page, 80);
      const text = line(page).locator('> span');
      expect((await line(page).boundingBox())!.height, "the line's height").toBe(height);
      expect(await text.evaluate((node) => node.scrollWidth > node.clientWidth), 'whether the text overflows its box').toBe(true);
      expect(await text.evaluate((node) => getComputedStyle(node).textOverflow)).toBe('ellipsis');
   });
});

test.describe("the top bar's Review the plan button", () => {
   /** The demo's plan-first tour, whose plan waits for the reviewer's approval. */
   const waitingTour = 'storefront #433';
   const reviewPlan = (page: Page) => header(page).getByRole('button', { name: 'Review the plan' });
   const planTab = (page: Page) => page.getByRole('tab', { name: 'Plan' });
   const approve = (page: Page) => page.getByRole('tabpanel', { name: 'Plan' }).getByRole('button', { name: 'Approve plan' });

   /** Expects the Plan panel to show as the active tab of its group, with the focus on Approve plan. */
   async function expectPlanFocused(page: Page): Promise<void> {
      await expect(planTab(page)).toHaveAttribute('aria-selected', 'true');
      await expect(page.getByRole('tabpanel', { name: 'Plan' })).toBeVisible();
      await expect(approve(page)).toBeFocused();
   }

   test("stands where the generation line was, at the height of the bar's other controls, with the full line in its description", async ({ page }) => {
      await openDemoTour(page, waitingTour, { fontSize: 'medium' });
      const rootSize = parseFloat(await page.locator('html').evaluate((node) => getComputedStyle(node).fontSize));

      await expect(reviewPlan(page)).toBeVisible();
      await expect(reviewPlan(page)).toHaveAccessibleDescription('The plan waits for your approval');
      await expect(header(page).locator('[data-generation-status]')).toHaveText('The plan waits for your approval');
      expect((await reviewPlan(page).boundingBox())!.height).toBeCloseTo(2 * rootSize, 1);
   });

   test('shows the Plan panel that the Panels menu hid, and moves the focus to Approve plan', async ({ page }) => {
      await openDemoTour(page, waitingTour, { fontSize: 'medium' });
      await page.getByRole('button', { name: 'Panels' }).click();
      await page.getByRole('menuitemcheckbox', { name: 'Plan' }).click();
      await page.keyboard.press('Escape');
      await expect(planTab(page)).toBeHidden();

      await reviewPlan(page).click();

      await expectPlanFocused(page);
   });

   test('restores another maximized group, opens the Plan tab behind Files, and moves the focus to Approve plan', async ({ page }) => {
      await openDemoTour(page, waitingTour, { fontSize: 'medium' });
      await page.getByRole('tab', { name: 'Files' }).click();
      await expect(planTab(page)).toHaveAttribute('aria-selected', 'false');
      await page.getByRole('button', { name: 'Maximize the stop' }).click();
      await expect(planTab(page)).toBeHidden();

      await reviewPlan(page).click();

      await expect(page.getByRole('button', { name: 'Maximize the stop' })).toBeVisible();
      await expectPlanFocused(page);
   });
});

for (const palette of palettes) {
   test(`gives Review the plan text and an edge that stand off their surfaces, at rest, on hover and with the keyboard's focus, in ${paletteName(palette)}`, async ({ page }) => {
      await openDemoTour(page, 'storefront #433', palette);
      await expectPalette(page, palette);
      await page.mouse.move(0, 500);
      const button = header(page).getByRole('button', { name: 'Review the plan' });
      // The button's colors transition, so the transition goes and each state shows its end colors at once.
      await button.evaluate((node: HTMLElement) => (node.style.transition = 'none'));

      /** Returns the contrast of the label against the button's fill, and of the edge against the bar. */
      const measure = async () => {
         const { own, behind } = await drawnColors(button, ['color', 'border-top-color', 'background-color']);
         const fill = over(own['background-color'], behind);
         return { label: contrast(over(own.color, fill), fill), edge: contrast(over(own['border-top-color'], behind), behind) };
      };

      const rest = await measure();
      expect.soft(rest.label, 'the label against the fill').toBeGreaterThanOrEqual(4.5);
      expect.soft(rest.edge, 'the edge against the bar').toBeGreaterThanOrEqual(2);

      await button.hover();
      expect.soft((await measure()).label, 'the label against the hovered fill').toBeGreaterThanOrEqual(4.5);

      await page.mouse.move(0, 500);
      await page.keyboard.press('Shift');
      await button.focus();
      await expect(button).toBeFocused();
      expect(await button.evaluate((node) => node.matches(':focus-visible')), 'whether the focus shows').toBe(true);
      expect.soft((await measure()).label, 'the label against the fill with the focus').toBeGreaterThanOrEqual(4.5);
   });
}

test.describe('the top bar in a narrow window', () => {
   const gatewayStop = 'Gateway errors reach the customer as they are';

   /** The items that give way, in the order that they do, each by the element that leaves the view. */
   const steps = [
      { item: 'the status chip', selector: '[data-chip-label]' },
      { item: 'the generation line', selector: '[data-generation-status]' },
      { item: 'Panels', selector: '[data-panels-label]' },
      { item: 'Send answers', selector: '[data-send-label]' },
      { item: 'the repository', selector: '[data-label-part="repository"]' },
      { item: 'the long position', selector: '[data-stop-position="long"]' },
      { item: 'the wordmark', selector: 'svg[aria-label="Lotsen"]' },
   ] as const;

   /** Measures the bar at the window's width: what spills past the bar or the window, the stop line's width in rem, and which steps' items have left the view. */
   const measureBar = (page: Page) =>
      header(page).evaluate(
         (bar, selectors) => {
            const rem = parseFloat(getComputedStyle(document.documentElement).fontSize);
            // A label leaves the view as sr-only text, a clipped 1px box, or not at all.
            const gone = (element: Element) => {
               const style = getComputedStyle(element);
               return style.display === 'none' || (style.position === 'absolute' && element.getBoundingClientRect().width <= 1);
            };
            const outside = [...bar.querySelectorAll('button, a')]
               .filter((control) => control.getBoundingClientRect().left < -0.5 || control.getBoundingClientRect().right > window.innerWidth + 0.5)
               .map((control) => control.getAttribute('aria-label') ?? control.textContent.trim());
            return {
               barSpill: bar.scrollWidth - bar.clientWidth,
               pageSpill: document.documentElement.scrollWidth - window.innerWidth,
               outside,
               line: bar.querySelector('nav[aria-label="Stops"] p')!.getBoundingClientRect().width / rem,
               gone: selectors.map((selector) => gone(bar.querySelector(selector)!)),
            };
         },
         steps.map((step) => step.selector),
      );

   for (const fontSize of ['small', 'medium', 'large'] as const) {
      test(`fits the window from 2000px down to 720px at ${fontSize}, gives way in order, and keeps the stop line 12rem wide until "4/7"`, async ({ page }) => {
         await page.setViewportSize({ width: 2000, height: 1000 });
         await openDemoTour(page, checkoutTour, { fontSize });
         await openStop(page, gatewayStop);
         await expect(header(page).locator('[data-unsent-answers]')).toHaveText('3 unsent answers');
         await expect(header(page).locator('[data-generation-status]')).toHaveText(/^Claude is writing/);

         const faults: string[] = [];
         const firstGone: (number | null)[] = steps.map(() => null);
         for (let width = 2000; width >= 720; width -= 20) {
            await page.setViewportSize({ width, height: 1000 });
            const bar = await measureBar(page);
            if (bar.barSpill > 0) faults.push(`${width}px: the bar's content runs ${bar.barSpill}px past it`);
            if (bar.pageSpill > 0) faults.push(`${width}px: the page scrolls ${bar.pageSpill}px sideways`);
            if (bar.outside.length > 0) faults.push(`${width}px: ${bar.outside.join(', ')} outside the window`);
            const short = bar.gone[steps.findIndex((step) => step.item === 'the long position')];
            if (!short && bar.line < 12) faults.push(`${width}px: the stop line is ${bar.line.toFixed(2)}rem wide before "4/7"`);
            bar.gone.forEach((gone, index) => {
               if (gone && firstGone[index] === null) firstGone[index] = width;
               if (!gone && firstGone[index] !== null) faults.push(`${width}px: ${steps[index]!.item} is back after it left at ${firstGone[index]}px`);
            });
         }

         expect(faults).toEqual([]);
         // A step that comes below 720px at this font size leaves nothing in the sweep, and neither does any step after it.
         const order = steps.map((step, index) => `${step.item} at ${firstGone[index] ?? 'no width'}px`).join(', ');
         const left = firstGone.filter((width) => width !== null);
         expect(
            firstGone.slice(left.length).every((width) => width === null),
            order,
         ).toBe(true);
         expect(left.length, order).toBeGreaterThanOrEqual(steps.length - 1);
         expect(left[0], order).toBeLessThan(2000);
         expect(
            left.every((width, index) => index === 0 || width < left[index - 1]!),
            order,
         ).toBe(true);
      });
   }

   /** Returns the width at which each step's item leaves the view, found in 10px steps down from 2000px. */
   async function stepWidths(page: Page): Promise<Record<(typeof steps)[number]['item'], number>> {
      const widths: Partial<Record<(typeof steps)[number]['item'], number>> = {};
      for (let width = 2000; width >= 600 && Object.keys(widths).length < steps.length; width -= 10) {
         await page.setViewportSize({ width, height: 1000 });
         (await measureBar(page)).gone.forEach((gone, index) => {
            if (gone) widths[steps[index]!.item] ??= width;
         });
      }
      expect(Object.keys(widths).sort(), 'the steps that came before 600px').toEqual(steps.map((step) => step.item).sort());
      return widths as Record<(typeof steps)[number]['item'], number>;
   }

   /** Returns the text that a screen reader reads in the element: what neither `display: none` nor `aria-hidden` leaves out. */
   const spokenText = (element: Locator) =>
      element.evaluate((node) => {
         const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
         let text = '';
         while (walker.nextNode()) {
            const parent = walker.currentNode.parentElement!;
            if (parent.closest('[aria-hidden="true"]') === null && parent.checkVisibility()) text += walker.currentNode.textContent;
         }
         return text.replace(/\s+/g, ' ').trim();
      });

   /** Returns the text of the tooltip that opens over the element, then moves the pointer away until it closes. */
   async function tooltipOver(page: Page, element: Locator): Promise<string> {
      await element.hover();
      const tooltip = page.locator('[data-tooltip-content]');
      await expect(tooltip).toBeVisible();
      const text = await tooltip.innerText();
      await page.mouse.move(0, 500);
      await expect(tooltip).toHaveCount(0);
      return text;
   }

   test('keeps each collapsed control named and with a tooltip, the live regions whole and the pull request number whole', async ({ page }) => {
      await page.setViewportSize({ width: 2000, height: 1000 });
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });
      await openStop(page, gatewayStop);
      await page.mouse.move(0, 500);
      const live = header(page).getByRole('navigation', { name: 'Stops' }).locator('[aria-live="polite"]');
      const fullLine = `Stop 4 of 7 · Paying · ${gatewayStop}`;
      const at = await stepWidths(page);
      const shows = (element: Locator) => element.evaluate((node) => getComputedStyle(node).position !== 'absolute' && getComputedStyle(node).display !== 'none');

      const atChip = at['the status chip'];
      await page.setViewportSize({ width: atChip + 10, height: 1000 });
      expect(await shows(header(page).locator('[data-chip-label]')), 'the chip label just above its step').toBe(true);
      await page.setViewportSize({ width: atChip, height: 1000 });
      await expect(statusChip(page)).toHaveAccessibleName('Claude session: Confirmed');
      expect(await tooltipOver(page, statusChip(page))).toMatch(/^Claude session: Confirmed\n/);

      const atGeneration = at['the generation line'];
      await page.setViewportSize({ width: atGeneration + 10, height: 1000 });
      expect(await shows(header(page).locator('[data-generation-status]')), 'the generation line just above its step').toBe(true);
      await page.setViewportSize({ width: atGeneration, height: 1000 });
      await expect(
         header(page)
            .getByRole('status')
            .filter({ hasText: /^Claude is writing/ }),
      ).toHaveCount(1);
      expect(await tooltipOver(page, header(page).locator('[data-generation-icon]'))).toMatch(/^Claude is writing stop \d+ of \d+…$/);

      const panels = header(page).getByRole('button', { name: 'Panels', exact: true });
      const atPanels = at['Panels'];
      await page.setViewportSize({ width: atPanels + 10, height: 1000 });
      expect(await shows(header(page).locator('[data-panels-label]')), 'the Panels label just above its step').toBe(true);
      await page.setViewportSize({ width: atPanels, height: 1000 });
      await expect(panels).toBeVisible();
      expect(await tooltipOver(page, panels)).toBe('Panels');

      const atSend = at['Send answers'];
      await page.setViewportSize({ width: atSend + 10, height: 1000 });
      expect(await shows(header(page).locator('[data-send-label]')), 'the Send answers label just above its step').toBe(true);
      await page.setViewportSize({ width: atSend, height: 1000 });
      await expect(sendAnswers(page)).toBeVisible();
      await expect(sendAnswers(page).locator('svg.lucide-send')).toBeVisible();
      await expect(sendAnswers(page).locator('[data-unsent-count]')).toHaveText('3');
      expect(await tooltipOver(page, sendAnswers(page))).toBe('Send your answers to Claude');

      const link = header(page).getByRole('link', { name: checkoutTour, exact: true });
      const number = link.locator('[data-label-part="number"]');
      const atRepository = at['the repository'];
      await page.setViewportSize({ width: atRepository + 10, height: 1000 });
      expect(await shows(link.locator('[data-label-part="repository"]')), 'the repository just above its step').toBe(true);
      await page.setViewportSize({ width: atRepository, height: 1000 });
      await expect(number).toHaveText('#412');
      expect(await number.evaluate((node) => node.scrollWidth <= node.clientWidth), 'whether the number shows whole').toBe(true);
      expect(await tooltipOver(page, link)).toMatch(/^storefront #412\n/);

      const atPosition = at['the long position'];
      await page.setViewportSize({ width: atPosition + 10, height: 1000 });
      await expect(header(page).locator('[data-stop-position="short"]')).toBeHidden();
      await page.setViewportSize({ width: atPosition, height: 1000 });
      await expect(header(page).locator('[data-stop-position="short"]')).toHaveText('4/7');
      expect(await spokenText(live)).toBe(fullLine);

      const atWordmark = at['the wordmark'];
      await page.setViewportSize({ width: atWordmark + 10, height: 1000 });
      await expect(header(page).getByRole('img', { name: 'Lotsen' })).toBeVisible();
      await page.setViewportSize({ width: atWordmark, height: 1000 });
      await expect(header(page).getByRole('img', { name: 'Lotsen' })).toBeHidden();
      for (const name of ['Check the PR for a new update', 'Panels', 'Settings']) {
         await expect(header(page).getByRole('button', { name, exact: true }), name).toBeVisible();
      }
   });

   test('keeps the pull request number whole while the tour zone cuts a long repository short', async ({ page }) => {
      const label = 'contoso-enterprise/platform-services/storefront #412';
      await page.route('**/api/ops/list_tours', async (route) => {
         const response = await route.fetch();
         await route.fulfill({ response, body: (await response.text()).split(JSON.stringify(checkoutTour)).join(JSON.stringify(label)) });
      });
      await page.setViewportSize({ width: 1600, height: 1000 });
      await openDemoTour(page, checkoutTour, { fontSize: 'medium' });

      const link = header(page).getByRole('link', { name: label, exact: true });
      const repository = link.locator('[data-label-part="repository"]');
      const number = link.locator('[data-label-part="number"]');
      await expect(number).toHaveText('#412');
      expect(await repository.evaluate((node) => node.scrollWidth > node.clientWidth), 'whether the repository is cut short').toBe(true);
      expect(await number.evaluate((node) => node.scrollWidth <= node.clientWidth), 'whether the number shows whole').toBe(true);
      expect(await tooltipOver(page, link)).toMatch(new RegExp(`^${label}\n`));
   });

   test('collapses Review the plan to its hourglass with its name and tooltip', async ({ page }) => {
      await page.setViewportSize({ width: 720, height: 1000 });
      await openDemoTour(page, 'storefront #433', { fontSize: 'large' });
      await page.mouse.move(0, 500);

      const review = header(page).getByRole('button', { name: 'Review the plan' });
      await expect(review.locator('svg.lucide-hourglass')).toBeVisible();
      expect(await review.locator('[data-review-label]').evaluate((node) => getComputedStyle(node).position)).toBe('absolute');
      expect(await tooltipOver(page, review)).toBe('Review the plan\nThe plan waits for your approval');
      await expect(review).toHaveAccessibleDescription('The plan waits for your approval');
   });
});
