import { expect, test, type Locator, type Page } from '@playwright/test';
import { arrange, center, checkoutTour, editorLine, groups, inScrollerView, openDemoTour, openStop, showPanel } from './demo';

const region = (page: Page, name: string) => page.getByRole('region', { name, exact: true });

/** Returns the number of drop targets that the page shows: Dockview draws one in the group, or anchored over it, and keeps an anchored one in the page between drags. */
const shownDropTargets = (page: Page) =>
   page.evaluate(
      () =>
         [...document.querySelectorAll('.dv-drop-target-dropzone, .dv-drop-target-anchor')].filter((target) => {
            const style = getComputedStyle(target);
            return target.getBoundingClientRect().width > 0 && style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0';
         }).length,
   );

/** Dispatches an HTML5 drag event at a point of the element, given as shares of its box. Playwright drags only within one page, so a test of a drag between windows dispatches them itself. */
const dispatchDrag = (target: Locator, type: string, x = 0.5, y = 0.5) =>
   target.evaluate(
      (element, [type, x, y]) => {
         const view = element.ownerDocument.defaultView!;
         const box = element.getBoundingClientRect();
         const init = { bubbles: true, cancelable: true, clientX: box.x + box.width * x, clientY: box.y + box.height * y, dataTransfer: new view.DataTransfer(), view };
         element.dispatchEvent(new view.DragEvent(type, init));
      },
      [type, x, y] as const,
   );

// An unhandled error in the page, such as one that Svelte throws, leaves a test that reads only the page passing, so each test fails on one, in the main page or a popout window.
let pageErrors: string[] = [];
test.beforeEach(({ page }) => {
   pageErrors = [];
   const record = (source: Page) => source.on('pageerror', (error) => pageErrors.push(`${error.name}: ${error.message}`));
   record(page);
   page.on('popup', record);
});
test.afterEach(() => expect(pageErrors).toEqual([]));

/**
 * Pops the panel out from its group header, and returns its popout window once the panel shows there.
 *
 * The main page's pointer then leaves the header, as a person's leaves the main window. Otherwise the button that moves under it opens its tooltip, whose escape layer bits-ui puts above
 * the popout's overlays, so an Escape in the popout window would close nothing (isResponsibleEscapeLayer in bits-ui 2.20.2's use-escape-layer.svelte.js).
 */
async function popOut(page: Page, name: string, button: string): Promise<Page> {
   const opened = page.waitForEvent('popup');
   await page.getByRole('button', { name: button }).click();
   const popout = await opened;
   await expect(region(popout, name)).toBeVisible();
   await page.mouse.move(0, 0);
   return popout;
}

/** Returns the pointer-events value of the page's body, which a modal menu or dialog sets to none while it locks the page. */
const bodyPointer = (page: Page) => page.locator('body').evaluate((body) => getComputedStyle(body).pointerEvents);

/** Returns the row of the demo's thread on the discount in the Threads panel of the page. */
const discountRow = (page: Page) => region(page, 'Threads').getByRole('list', { name: 'Threads' }).getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' });

/** Opens the payment stop's passage in the shipping rates, a file longer than the window, which the Stop panel of the page offers. */
async function openShippingRates(page: Page): Promise<void> {
   await openStop(page, 'The new payment step');
   await region(page, 'Stop')
      .getByRole('button', { name: /shipping/ })
      .click();
}

/** Selects the lines of an editor from the line that holds one text to the line that holds another, with a mouse drag in the editor's window. */
async function dragOver(page: Page, from: Locator, to: Locator): Promise<void> {
   const [start, end] = [(await from.boundingBox())!, (await to.boundingBox())!];
   await page.mouse.move(start.x + 40, start.y + start.height / 2);
   await page.mouse.down();
   await page.mouse.move(end.x + 60, end.y + end.height / 2, { steps: 5 });
   await page.mouse.up();
}

/**
 * Turns the mouse wheel over an editor until the element that scrolls its lines reaches the end that the wheel's direction points to.
 *
 * Firefox 157 scrolls less than the scroller's height for one wheel event, however large its delta, so one event does not reach the end of a long diff.
 */
async function wheelToEnd(page: Page, editor: Locator, deltaY: number): Promise<void> {
   await editor.hover();
   await expect(async () => {
      await page.mouse.wheel(0, deltaY);
      const atEnd = await editor.evaluate((node, down) => {
         let scroller = node.querySelector<HTMLElement>('.cm-scroller');
         while (scroller !== null && !(scroller.scrollHeight > scroller.clientHeight && ['auto', 'scroll'].includes(getComputedStyle(scroller).overflowY))) {
            scroller = scroller.parentElement;
         }

         return scroller !== null && (down ? scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 1 : scroller.scrollTop <= 0);
      }, deltaY > 0);
      expect(atEnd).toBe(true);
   }).toPass({ timeout: 10_000 });
}

/** Returns the right and the bottom edge of an element in its window, in pixels. */
async function farEdges(element: Locator): Promise<{ right: number; bottom: number }> {
   const box = (await element.boundingBox())!;
   return { right: Math.round(box.x + box.width), bottom: Math.round(box.y + box.height) };
}

/** A panel of the workspace to pop out, with the controls of its region that a test uses in the popout window. */
interface PoppedPanel {
   /** The name of the panel's region and of its tab. */
   name: string;
   /** The name of the Pop out button in the panel's group header. */
   button: string;
   /** Shows what the panel needs in the main window, and makes it the active panel of its group. */
   open?: (page: Page) => Promise<void>;
   /** A click in the panel, and the check of its effect, which holds in any window that shows the panel. */
   click: { run: (panel: Locator) => Promise<void>; shown: (panel: Locator) => Promise<void> };
   /** The button that opens a menu of the panel, and an item of the menu, or null when the panel offers no menu. */
   menu: { trigger: (panel: Locator) => Locator; item: string } | null;
   /** Returns the element that shows a tooltip on hover, and the tooltip's text. */
   tooltip: { trigger: (panel: Locator) => Promise<Locator>; text: string | RegExp };
}

// Files and Code offer no menu of their own, and the Stop panel's two menus have a test of their own below.
const poppedPanels: PoppedPanel[] = [
   {
      name: 'Files',
      button: 'Pop out the files',
      open: (page) => page.getByRole('tab', { name: 'Files' }).click(),
      click: {
         run: (panel) => panel.getByRole('treeitem', { name: 'checkout', exact: true }).locator('.row').first().click(),
         shown: (panel) => expect(panel.getByRole('treeitem', { name: 'checkout', exact: true })).toHaveAttribute('aria-expanded', 'false'),
      },
      menu: null,
      tooltip: { trigger: async (panel) => panel.getByRole('treeitem', { name: /^payment\.ts,/ }).locator('.row'), text: /^src\/checkout\/payment\.ts/ },
   },
   {
      name: 'Plan',
      button: 'Pop out the plan',
      click: {
         run: (panel) => panel.getByRole('button', { name: /The new payment step/ }).click(),
         shown: (panel) => expect(panel.getByRole('button', { name: /The new payment step/ })).toHaveAttribute('aria-current', 'step'),
      },
      menu: { trigger: (panel) => panel.getByRole('button', { name: 'Plan actions' }), item: 'Re-plan…' },
      tooltip: {
         // A skip toggle shows only while the pointer is in its row.
         trigger: async (panel) => {
            await panel.getByRole('button', { name: /^1 Overview: / }).hover();
            return panel.getByRole('button', { name: 'Skip stop 1', exact: true });
         },
         text: /^Next and Back pass over this stop/,
      },
   },
   {
      name: 'Code',
      button: 'Pop out the code',
      open: openShippingRates,
      click: {
         run: (panel) => panel.getByRole('button', { name: 'Side by side' }).click(),
         shown: (panel) => expect(panel.locator('.cm-mergeView')).toBeVisible(),
      },
      menu: null,
      tooltip: { trigger: async (panel) => panel.getByRole('button', { name: 'Highlight the passages' }), text: 'Highlight the passages' },
   },
   {
      name: 'Stop',
      button: 'Pop out the stop',
      open: (page) => openStop(page, 'The new payment step'),
      click: {
         run: (panel) => panel.getByRole('button', { name: 'Skip stop' }).click(),
         shown: (panel) => expect(panel.getByRole('button', { name: 'Skip stop' })).toHaveAttribute('aria-pressed', 'true'),
      },
      menu: null,
      tooltip: { trigger: async (panel) => panel.getByRole('list', { name: 'Code' }).locator('.file').first(), text: /^src\// },
   },
   {
      name: 'Threads',
      button: 'Pop out the threads',
      click: {
         run: (panel) => discountRow(panel.page()).getByRole('button').first().click(),
         shown: (panel) => expect(panel.getByRole('button', { name: 'Back to all threads' })).toBeVisible(),
      },
      menu: { trigger: (panel) => discountRow(panel.page()).getByRole('button', { name: 'Thread actions' }), item: 'Delete thread…' },
      tooltip: {
         trigger: async (panel) => {
            await panel.getByRole('textbox', { name: 'Question for Claude' }).fill('A note to read later.');
            return panel.getByRole('button', { name: 'Save as note' });
         },
         text: 'Save as note',
      },
   },
];

test.describe('a popout window', () => {
   test('runs a click in the Threads panel that it holds, and returns the panel to its group once the window closes', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { theme: 'dark' });
      const opened = page.waitForEvent('popup');

      await page.getByRole('button', { name: 'Pop out the threads' }).click();

      const popout = await opened;
      const threads = region(popout, 'Threads');
      await expect(threads).toBeVisible();
      await expect(region(page, 'Threads')).toBeHidden();
      await expect(popout.locator('html')).toHaveClass(/\bdark\b/);
      await expect(threads.getByRole('button', { name: /^Pop out/ })).toHaveCount(0);

      const row = threads.getByRole('list', { name: 'Threads' }).getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' });
      await row.getByRole('button').first().click();
      await expect(threads.getByRole('button', { name: 'Back to all threads' })).toBeVisible();

      await popout.close({ runBeforeUnload: true });

      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
      await expect(region(page, 'Threads').getByRole('button', { name: 'Back to all threads' })).toBeVisible();
   });

   for (const panel of poppedPanels) {
      test(`runs a click${panel.menu === null ? '' : ', a menu'} and a tooltip in the ${panel.name} panel that it holds, and returns it to its group once the window closes`, async ({ page }) => {
         await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'inline' });
         await panel.open?.(page);
         const popout = await popOut(page, panel.name, panel.button);
         const held = region(popout, panel.name);

         if (panel.menu !== null) {
            await panel.menu.trigger(held).click();
            await expect(popout.getByRole('menu')).toHaveCount(1);
            await expect(popout.getByRole('menuitem', { name: panel.menu.item })).toBeInViewport();
            await expect(page.getByRole('menu')).toHaveCount(0);
            await popout.keyboard.press('Escape');
            await expect(popout.getByRole('menu')).toHaveCount(0);
         }

         const trigger = await panel.tooltip.trigger(held);
         await trigger.hover();
         await expect(trigger).toHaveAttribute('aria-describedby', /.+/);
         const id = (await trigger.getAttribute('aria-describedby'))!;
         await expect(popout.locator(`[id="${id}"]`)).toHaveText(panel.tooltip.text);
         await expect(page.locator(`[id="${id}"]`)).toHaveCount(0);
         await popout.keyboard.press('Escape');
         await expect(popout.locator(`[id="${id}"]`)).toBeHidden();

         await panel.click.run(held);
         await panel.click.shown(held);

         await popout.close({ runBeforeUnload: true });
         await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
         await page.getByRole('tab', { name: panel.name }).click();
         await panel.click.shown(region(page, panel.name));
      });
   }

   test('keeps the panel on Delete or Backspace on its tab, in the popout window and back in the main window', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const opened = page.waitForEvent('popup');
      await page.getByRole('button', { name: 'Pop out the threads' }).click();
      const popout = await opened;
      const tab = popout.getByRole('tab', { name: 'Threads' });
      await expect(tab).toBeVisible();

      for (const key of ['Delete', 'Backspace']) {
         await tab.press(key);
      }

      await expect(region(popout, 'Threads')).toBeVisible();
      await popout.close({ runBeforeUnload: true });
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);

      for (const key of ['Delete', 'Backspace']) {
         await page.getByRole('tab', { name: 'Threads' }).press(key);
      }

      expect(await groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
   });

   test("shows a thread's menu in its own window, closes it there with Escape and an outside click, and shows it in the main window once the panel returns", async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      const actions = discountRow(popout).getByRole('button', { name: 'Thread actions' });

      await actions.click();

      await expect(popout.getByRole('menu')).toHaveCount(1);
      await expect(popout.getByRole('menuitem', { name: 'Delete thread…' })).toBeInViewport();
      await expect(popout.getByRole('menu')).toBeFocused();
      await expect(page.getByRole('menu')).toHaveCount(0);
      expect(await bodyPointer(page)).not.toBe('none');
      await popout.keyboard.press('ArrowDown');
      await expect(popout.getByRole('menuitem').first()).toBeFocused();
      await popout.keyboard.press('Escape');
      await expect(popout.getByRole('menu')).toHaveCount(0);
      await expect(actions).toBeFocused();

      await actions.click();
      await expect(popout.getByRole('menu')).toHaveCount(1);
      await popout.getByRole('tab', { name: 'Threads' }).click();
      await expect(popout.getByRole('menu')).toHaveCount(0);

      await discountRow(popout).click({ button: 'right' });
      await expect(popout.getByRole('menu')).toHaveAttribute('data-context-menu-content');
      await popout.keyboard.press('Escape');
      await expect(popout.getByRole('menu')).toHaveCount(0);

      await popout.close({ runBeforeUnload: true });
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
      await discountRow(page).getByRole('button', { name: 'Thread actions' }).click();
      await expect(page.getByRole('menu')).toHaveCount(1);
      await expect(page.getByRole('menuitem', { name: 'Delete thread…' })).toBeInViewport();
      await expect.poll(() => bodyPointer(page)).toBe('none');
   });

   test('shows a tooltip in its own window beside its trigger, and closes it there with Escape', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      const threads = region(popout, 'Threads');
      await threads.getByRole('textbox', { name: 'Question for Claude' }).fill('A note to read later.');
      const save = threads.getByRole('button', { name: 'Save as note' });

      await save.hover();

      await expect(save).toHaveAttribute('aria-describedby', /.+/);
      const id = (await save.getAttribute('aria-describedby'))!;
      const tooltip = popout.locator(`[id="${id}"]`);
      await expect(tooltip).toHaveText('Save as note');
      await expect(page.locator(`[id="${id}"]`)).toHaveCount(0);
      const [anchor, tip] = [(await save.boundingBox())!, (await tooltip.boundingBox())!];
      expect(Math.abs(tip.x + tip.width / 2 - (anchor.x + anchor.width / 2))).toBeLessThan(anchor.width);
      await popout.keyboard.press('Escape');
      await expect(tooltip).toBeHidden();
   });

   test('shows the dialog that a thread menu opens in its own window, keeps the focus in it, and closes it there with Escape', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      const actions = discountRow(popout).getByRole('button', { name: 'Thread actions' });
      await actions.click();

      await popout.getByRole('menuitem', { name: 'Delete thread…' }).click();

      const dialog = popout.getByRole('alertdialog', { name: 'Delete this thread?' });
      await expect(dialog).toBeVisible();
      await expect(page.getByRole('alertdialog')).toHaveCount(0);
      expect(await bodyPointer(page)).not.toBe('none');
      await expect(dialog).toBeFocused();
      await popout.keyboard.press('Tab');
      await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
      await popout.keyboard.press('Tab');
      await expect(dialog.getByRole('button', { name: 'Delete thread' })).toBeFocused();
      await popout.keyboard.press('Tab');
      await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
      await popout.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
      await expect(actions).toBeFocused();
      await expect(discountRow(popout)).toHaveCount(1);
   });

   test('offers the ask of a selection of the stop text in the Stop panel that it holds', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await openStop(page, 'The new payment step');
      const popout = await popOut(page, 'Stop', 'Pop out the stop');

      await region(popout, 'Stop').locator('[data-stop-field]').first().selectText();

      await expect(region(popout, 'Stop').getByRole('button', { name: /^Ask Claude about/ })).toBeVisible();
   });

   test('shows the Ask Claude and ⋯ menus of the Stop panel that it holds in its own window', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { placement: 'mixed' });
      await openStop(page, 'The new payment step');
      const popout = await popOut(page, 'Stop', 'Pop out the stop');
      const held = region(popout, 'Stop');

      for (const [trigger, item] of [
         ['Ask Claude', 'Explain deeper…'],
         ['More stop actions', 'Show inline'],
      ] as const) {
         await held.getByRole('button', { name: trigger, exact: true }).click();
         await expect(popout.getByRole('menuitem', { name: item })).toBeInViewport();
         await expect(page.getByRole('menu')).toHaveCount(0);
         await popout.keyboard.press('Escape');
         await expect(popout.getByRole('menu')).toHaveCount(0);
      }

      await held.getByRole('button', { name: 'More stop actions' }).click();
      await expect(popout.getByRole('menuitemradio', { name: 'Version 2, current' })).toHaveAttribute('aria-checked', 'true');
      await popout.keyboard.press('Escape');
   });

   test('lays out, scrolls and selects the diff of the Code panel that it holds, asks about the selected lines, and does the same once the panel returns', async ({ page }) => {
      await page.addInitScript(() => localStorage.setItem('lotsen.syntaxColors', 'vscode'));
      await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'sideBySide' });
      await openShippingRates(page);
      await expect(editorLine(center(page), "'region-052'")).toBeVisible();
      const popout = await popOut(page, 'Code', 'Pop out the code');
      const code = center(popout);
      const base = code.locator('.cm-editor').first();

      await expect(popout.locator('html')).toHaveAttribute('data-syntax-colors', 'vscode');
      for (const size of [
         { width: 900, height: 500 },
         { width: 1200, height: 800 },
      ]) {
         await popout.setViewportSize(size);
         await expect.poll(() => farEdges(code)).toEqual({ right: size.width, bottom: size.height });
         await expect.poll(() => farEdges(code.locator('.cm-mergeView'))).toEqual({ right: size.width, bottom: size.height });
      }

      await popout.setViewportSize({ width: 900, height: 500 });
      await wheelToEnd(popout, base, 10_000);
      await expect.poll(() => inScrollerView(editorLine(base, "'region-130'"))).toBe(true);

      await dragOver(popout, editorLine(base, "'region-120'"), editorLine(base, "'region-124'"));

      const ask = code.getByRole('button', { name: 'Ask Claude about src/checkout/shipping.ts, lines 122–126 at the base' });
      await expect(ask).toBeVisible();
      await ask.click();
      await expect(region(page, 'Threads').getByRole('textbox', { name: 'Question for Claude' })).toHaveAttribute('placeholder', 'Ask Claude about these lines');

      await popout.close({ runBeforeUnload: true });
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
      const returned = center(page).locator('.cm-editor').first();
      await wheelToEnd(page, returned, -10_000);
      await expect.poll(() => inScrollerView(editorLine(returned, "'region-003'"))).toBe(true);
      await dragOver(page, editorLine(returned, "'region-003'"), editorLine(returned, "'region-005'"));
      await expect(center(page).getByRole('button', { name: 'Ask Claude about src/checkout/shipping.ts, lines 5–7 at the base' })).toBeVisible();
   });

   for (const layout of ['inline', 'sideBySide'] as const) {
      test(`mounts the ${layout} diff of a file that opens while the Code panel is popped out, selects its deleted lines and scrolls it there`, async ({ page }) => {
         await openDemoTour(page, checkoutTour, { placement: 'side', layout });
         const popout = await popOut(page, 'Code', 'Pop out the code');
         await popout.setViewportSize({ width: 900, height: 500 });

         await openShippingRates(page);

         // The inline diff draws the deleted lines in its one editor, and the side-by-side diff in its base editor, which comes first.
         const editor = center(popout).locator('.cm-editor').first();
         const deleted = (text: string) => (layout === 'inline' ? editor.locator('.cm-deletedLine', { hasText: text }) : editorLine(editor, text));
         await expect.poll(() => inScrollerView(deleted("'region-052'"))).toBe(true);
         await dragOver(popout, deleted("'region-052'"), deleted("'region-054'"));
         await expect(center(popout).getByRole('button', { name: 'Ask Claude about src/checkout/shipping.ts, lines 54–56 at the base' })).toBeVisible();
         await wheelToEnd(popout, editor, 10_000);
         await expect.poll(() => inScrollerView(editorLine(editor, "'region-130'"))).toBe(true);
      });
   }

   test("selects the deleted lines of the inline diff in the Code panel that it holds while the lines' elements belong to the popout window's realm", async ({ page }) => {
      await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'inline' });
      const popout = await popOut(page, 'Code', 'Pop out the code');
      await popout.setViewportSize({ width: 900, height: 500 });
      await openShippingRates(page);
      const editor = center(popout).locator('.cm-editor').first();
      const deleted = (text: string) => editor.locator('.cm-deletedLine', { hasText: text });
      await expect.poll(() => inScrollerView(deleted("'region-052'"))).toBe(true);

      // Firefox can show a popout window's deleted lines with elements of that window's realm, so the test gives them such elements in every browser.
      const mainRealm = await deleted("'region-052'")
         .locator('xpath=..')
         .evaluate((chunk) => {
            for (const line of chunk.querySelectorAll('.cm-deletedLine')) {
               line.replaceChildren(...[...line.childNodes].map((node) => chunk.ownerDocument.importNode(node, true)));
            }

            return chunk.querySelector('.cm-deletedLine > *') instanceof (window.opener as typeof globalThis).Element;
         });
      expect(mainRealm).toBe(false);

      await dragOver(popout, deleted("'region-052'"), deleted("'region-054'"));
      await expect(center(popout).getByRole('button', { name: 'Ask Claude about src/checkout/shipping.ts, lines 54–56 at the base' })).toBeVisible();
   });

   test('keeps the focus on the control of a collapsed marker that expands it by keyboard in the Code panel that it holds', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { placement: 'side', layout: 'inline' });
      await openShippingRates(page);
      const popout = await popOut(page, 'Code', 'Pop out the code');
      await popout.setViewportSize({ width: 900, height: 500 });
      await center(popout).getByRole('button', { name: 'Collapse to passages' }).click();
      const marker = center(popout).locator('.lotsen-collapsed').last();
      const hidden = async () => Number((await marker.locator('.lotsen-collapsedCount').textContent())!.split(' ')[0]);
      const before = await hidden();

      await marker.getByRole('button', { name: 'Show first 20' }).focus();
      await popout.keyboard.press('Enter');

      await expect.poll(hidden).toBe(before - 20);
      const focused = popout.locator(':focus');
      await expect(focused).toHaveClass(/lotsen-expand/);
      await expect.poll(() => inScrollerView(focused)).toBe(true);
   });

   test('keeps the spacer of an inline explanation as high as the explanation, once the popout window narrows and the explanation grows', async ({ page }) => {
      await openDemoTour(page, checkoutTour, { placement: 'inline', layout: 'sideBySide' });
      await openStop(page, 'A cart line needs at least one item');
      await expect(center(page).locator('.lotsen-explanation')).toHaveCount(1);
      const popout = await popOut(page, 'Code', 'Pop out the code');
      const [base, head] = [center(popout).locator('.cm-editor').nth(0), center(popout).locator('.cm-editor').nth(1)];
      const shown = head.locator('.lotsen-explanation');
      const spacer = base.locator('.lotsen-explanationSpacer');
      const height = async (element: Locator) => Math.round((await element.boundingBox())!.height);
      await popout.setViewportSize({ width: 1400, height: 800 });
      await expect.poll(async () => (await height(spacer)) - (await height(shown))).toBe(0);
      const wide = await height(shown);

      await popout.setViewportSize({ width: 700, height: 800 });

      await expect.poll(() => height(shown)).toBeGreaterThan(wide);
      await expect.poll(async () => (await height(spacer)) - (await height(shown))).toBe(0);
   });

   for (const overlay of ['menu', 'alertdialog'] as const) {
      test(`closes the ${overlay} that is open as its window closes, and leaves the main window working`, async ({ page }) => {
         await openDemoTour(page, checkoutTour);
         let popout = await popOut(page, 'Threads', 'Pop out the threads');
         await discountRow(popout).getByRole('button', { name: 'Thread actions' }).click();
         if (overlay === 'alertdialog') {
            await popout.getByRole('menuitem', { name: 'Delete thread…' }).click();
         }

         await expect(popout.getByRole(overlay)).toBeVisible();

         await popout.close({ runBeforeUnload: true });

         await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
         await expect(page.getByRole('menu')).toHaveCount(0);
         await expect(page.getByRole('alertdialog')).toHaveCount(0);
         await expect.poll(() => bodyPointer(page)).not.toBe('none');
         await page.getByRole('tab', { name: 'Files' }).click({ timeout: 5_000 });
         await expect(page.getByRole('tab', { name: 'Files' })).toHaveAttribute('aria-selected', 'true');

         popout = await popOut(page, 'Threads', 'Pop out the threads');
         await discountRow(popout).getByRole('button', { name: 'Thread actions' }).click();
         await expect(popout.getByRole('menu')).toHaveCount(1);
         await popout.keyboard.press('Escape');
         await expect(popout.getByRole('menu')).toHaveCount(0);
      });
   }

   test('shows the reason of a thread delete that the server refuses after its window closed during the delete', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      let answer: () => Promise<void> = async () => {};
      const held = new Promise<void>((resolve) => {
         void page.route('**/api/ops/remove_thread', (route) => {
            answer = () => route.fulfill({ status: 500, body: 'The store failed.' });
            resolve();
         });
      });
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      await discountRow(popout).getByRole('button', { name: 'Thread actions' }).click();
      await popout.getByRole('menuitem', { name: 'Delete thread…' }).click();
      await popout.getByRole('alertdialog').getByRole('button', { name: 'Delete thread' }).click();
      await held;

      await popout.close({ runBeforeUnload: true });
      await expect(page.getByRole('alertdialog')).toHaveCount(0);
      await answer();

      const dialog = page.getByRole('alertdialog', { name: 'Delete this thread?' });
      await expect(dialog.getByRole('alert')).toBeVisible();
      await expect(discountRow(page)).toHaveCount(1);
      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
   });
});

/** Returns the saved layout that the page holds in browser storage, as JSON. */
const savedLayout = (page: Page) => page.evaluate(() => JSON.parse(localStorage.getItem('lotsen.workspaceLayout') ?? 'null'));

test.describe('the workspace with a popout window', () => {
   test('closes the popout window of Threads on Default and lays out Threads in its Default group, at its size though its column hid as a whole', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      await page.getByRole('button', { name: 'Hide the stop' }).click();
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code']]);

      await arrange(page, 'Default');

      await expect.poll(() => popout.isClosed()).toBe(true);
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
      const [stop, threads] = [(await region(page, 'Stop').boundingBox())!, (await region(page, 'Threads').boundingBox())!];
      // The Default layout gives the column of Stop and Threads 0.28 of the workspace, which spans the window (arrangements.ts).
      expect(threads.width).toBeCloseTo(0.28 * page.viewportSize()!.width, -1);
      expect(threads.x).toBeCloseTo(stop.x, 0);
      expect(threads.y).toBeGreaterThan(stop.y);
      await discountRow(page).getByRole('button', { name: 'Thread actions' }).click();
      await expect(page.getByRole('menu')).toHaveCount(1);
   });

   test('saves the layout with a popped-out panel in its place, which a reload shows there without a popout window', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await popOut(page, 'Stop', 'Pop out the stop');
      await expect.poll(() => savedLayout(page)).not.toBeNull();
      const saved = await savedLayout(page);
      expect(saved.dockview.popoutGroups).toBeUndefined();
      let popups = 0;
      page.on('popup', () => popups++);

      await page.reload();

      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
      await expect(region(page, 'Stop')).toBeVisible();
      expect(popups).toBe(0);
   });

   test('keeps the panel hidden that stayed behind in a group a panel popped out of, once the window closes and after a reload', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Plan', 'Pop out the plan');
      await page.getByRole('button', { name: 'Hide the files' }).click();
      await expect.poll(() => groups(page)).toEqual([['Code'], ['Stop'], ['Threads']]);

      await popout.close({ runBeforeUnload: true });

      await expect.poll(() => groups(page)).toEqual([['Plan'], ['Code'], ['Stop'], ['Threads']]);
      await page.reload();
      await expect.poll(() => groups(page)).toEqual([['Plan'], ['Code'], ['Stop'], ['Threads']]);
      await showPanel(page, 'Files');
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
   });

   test('shows a popped-out panel as shown in the Panels menu, and hides it from there in the main window, closing its window', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      await page.getByRole('button', { name: 'Panels' }).click();
      const item = page.getByRole('menuitemcheckbox', { name: 'Threads' });
      await expect(item).toHaveAttribute('aria-checked', 'true');

      await item.click();

      await expect.poll(() => popout.isClosed()).toBe(true);
      await expect(item).toHaveAttribute('aria-checked', 'false');
      await page.keyboard.press('Escape');
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop']]);
      await showPanel(page, 'Threads');
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
   });

   test('keeps a maximized group maximized and tells the reviewer when the browser blocks the popout window', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await page.getByRole('button', { name: 'Maximize the threads' }).click();
      await expect.poll(() => groups(page)).toEqual([['Threads']]);
      await page.evaluate(() => (window.open = () => null));

      await page.getByRole('button', { name: 'Pop out the threads' }).click();

      await expect(page.getByRole('button', { name: 'Restore the threads' })).toBeVisible();
      await expect.poll(() => groups(page)).toEqual([['Threads']]);
      const notice = page.getByRole('alert').filter({ hasText: 'The browser blocked the popout window' });
      await expect(notice).toBeVisible();
      await page.getByRole('button', { name: 'Dismiss the popout notice' }).click();
      await expect(notice).toBeHidden();
   });

   test('shows no place to drop the tab of a popout window inside that window, which holds only that panel', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');

      const tab = popout.getByRole('tab', { name: 'Threads' });
      const content = popout.locator('.dv-content-container');

      await dispatchDrag(tab, 'dragstart');
      const overlays: number[] = [];
      // The center of the group and a point near each edge, where Dockview offers a split.
      for (const [x, y] of [
         [0.5, 0.5],
         [0.95, 0.5],
         [0.05, 0.5],
         [0.5, 0.95],
         [0.5, 0.05],
      ] as const) {
         await dispatchDrag(content, 'dragenter', x, y);
         await dispatchDrag(content, 'dragover', x, y);
         overlays.push(await shownDropTargets(popout));
      }
      await dispatchDrag(tab, 'dragend');

      expect(overlays).toEqual([0, 0, 0, 0, 0]);
      expect(await groups(popout)).toEqual([['Threads']]);
   });

   test('refuses a tab that is dragged from the main window into a popout window', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      const popout = await popOut(page, 'Threads', 'Pop out the threads');
      const stop = page.getByRole('tab', { name: 'Stop' });

      const content = popout.locator('.dv-content-container');

      await dispatchDrag(stop, 'dragstart');
      await dispatchDrag(content, 'dragenter');
      await dispatchDrag(content, 'dragover');
      const overlays = await shownDropTargets(popout);
      await dispatchDrag(content, 'drop');
      await dispatchDrag(stop, 'dragend');

      expect(overlays).toBe(0);
      expect(await groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop']]);
      expect(await groups(popout)).toEqual([['Threads']]);
   });
});
