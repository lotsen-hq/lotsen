import { expect, test, type Locator, type Page } from '@playwright/test';
import { checkoutTour, openDemoTour, openStop } from './demo';

const header = (page: Page) => page.getByRole('banner');
const tourZone = (page: Page) => header(page).locator('[data-tour-zone]');
const updateCheck = (page: Page) => header(page).getByRole('button', { name: 'Check the PR for a new update' });
const sendAnswers = (page: Page) => header(page).getByRole('button', { name: 'Send answers' });

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
