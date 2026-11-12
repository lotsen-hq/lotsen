import { expect, test, type Page } from '@playwright/test';
import { arrange, center, checkoutTour, dragTab, groups, nextWorld, openDemoTour, openSecondPage, openStop, showPanel } from './demo';

const region = (page: Page, name: string) => page.getByRole('region', { name, exact: true });

test.describe('the arrangements', () => {
   for (const { name, expected } of [
      { name: 'Columns', expected: [['Files'], ['Plan'], ['Code'], ['Stop'], ['Threads']] },
      { name: 'Explanation above the diff', expected: [['Files', 'Plan'], ['Stop'], ['Code'], ['Threads']] },
      { name: 'Default', expected: [['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']] },
   ]) {
      test(`lays out every panel in its group as ${name}`, async ({ page }) => {
         await openDemoTour(page, checkoutTour);
         await dragTab(page, 'Threads', center(page));

         await arrange(page, name);

         await expect.poll(() => groups(page)).toEqual(expected);
      });
   }

   test('gives the Default layout on Reset layout after a drag', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await dragTab(page, 'Stop', center(page));
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code', 'Stop'], ['Threads']]);

      await arrange(page, 'Reset layout');

      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
   });

   test('keeps a section of the stop closed through an arrangement', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await openStop(page, 'A cart line needs at least one item');
      const why = region(page, 'Stop').getByRole('button', { name: 'Why' });
      await why.click();
      await expect(why).toHaveAttribute('aria-expanded', 'false');

      await arrange(page, 'Explanation above the diff');

      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Stop'], ['Code'], ['Threads']]);
      await expect(why).toHaveAttribute('aria-expanded', 'false');
   });
});

test.describe('the workspace', () => {
   test('maximizes a panel over the others and restores them', async ({ page }) => {
      await openDemoTour(page, checkoutTour);

      await page.getByRole('button', { name: 'Maximize the stop' }).click();

      await expect(region(page, 'Stop')).toBeVisible();
      for (const other of ['Plan', 'Code', 'Threads']) {
         await expect(region(page, other)).toBeHidden();
      }

      await page.getByRole('button', { name: 'Restore the stop' }).click();

      for (const shown of ['Plan', 'Code', 'Stop', 'Threads']) {
         await expect(region(page, shown)).toBeVisible();
      }
   });

   test('hides a panel from the Panels menu and shows it again with the sections the reviewer closed', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await openStop(page, 'A cart line needs at least one item');
      const why = region(page, 'Stop').getByRole('button', { name: 'Why' });
      await why.click();
      await expect(why).toHaveAttribute('aria-expanded', 'false');

      await page.getByRole('button', { name: 'Panels' }).click();
      await page.getByRole('menuitemcheckbox', { name: 'Stop' }).click();
      await expect(page.getByRole('menuitemcheckbox', { name: 'Stop' })).toHaveAttribute('aria-checked', 'false');
      await page.keyboard.press('Escape');

      await expect(region(page, 'Stop')).toBeHidden();

      await showPanel(page, 'Stop');

      await expect(why).toHaveAttribute('aria-expanded', 'false');
   });

   test('keeps the size of every group while a panel that shares its group hides and shows again', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await dragTab(page, 'Threads', center(page));
      await page.getByRole('tab', { name: 'Code' }).click();
      const widths = () => page.getByRole('tablist').evaluateAll((lists) => lists.map((list) => Math.round(list.closest('[role="region"]')!.getBoundingClientRect().width)));
      const before = await widths();
      expect(before).toHaveLength(3);

      await page.getByRole('button', { name: 'Hide the code' }).click();

      await expect(page.getByRole('tab', { name: 'Code' })).toBeHidden();
      expect(await widths()).toEqual(before);

      await showPanel(page, 'Code');

      await expect(region(page, 'Code').getByRole('tab', { name: 'Threads' })).toBeVisible();
      expect(await widths()).toEqual(before);
   });

   test('saves no layout until the reviewer changes it, through a tour that opens and a resize', async ({ page }) => {
      const saved = () => page.evaluate(() => localStorage.getItem('lotsen.workspaceLayout'));
      await page.goto('/');
      await page.getByRole('button', { name: /^(Choose a tour|Tour: .+, choose another)$/ }).click();
      await page
         .getByRole('navigation', { name: 'Tours' })
         .getByRole('button', { name: new RegExp(checkoutTour) })
         .first()
         .click();
      // The tour opens on the Files tab, which the page selects on its own.
      await expect(page.getByRole('tab', { name: 'Files' })).toHaveAttribute('aria-selected', 'true');
      await page.setViewportSize({ width: 1100, height: 700 });
      await page.evaluate(() => new Promise((resolve) => setTimeout(resolve, 300)));
      expect(await saved()).toBeNull();

      await dragTab(page, 'Threads', center(page));

      await expect.poll(saved).not.toBeNull();
   });

   test('keeps a dragged panel in its group and a hidden panel hidden after a reload', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await dragTab(page, 'Threads', center(page));
      await expect(region(page, 'Threads').getByRole('tab', { name: 'Code' })).toBeVisible();
      await page.getByRole('button', { name: 'Hide the stop' }).click();
      await expect(region(page, 'Stop')).toBeHidden();

      await page.reload();

      const codeGroup = page.getByRole('tablist').filter({ has: page.getByRole('tab', { name: 'Code' }) });
      await expect(codeGroup.getByRole('tab')).toHaveText(['Code', 'Threads']);
      await expect(page.getByRole('tab', { name: 'Stop' })).toBeHidden();
      await page.getByRole('button', { name: 'Panels' }).click();
      await expect(page.getByRole('menuitemcheckbox', { name: 'Stop' })).toHaveAttribute('aria-checked', 'false');
   });

   test('opens a thread menu and a tooltip inside a panel dragged to another group', async ({ page }) => {
      await openDemoTour(page, checkoutTour);

      await dragTab(page, 'Threads', center(page));

      const threads = region(page, 'Threads');
      await expect(threads).toBeVisible();
      await expect(threads.getByRole('tab', { name: 'Code' })).toBeVisible();
      const row = threads.getByRole('list', { name: 'Threads' }).getByRole('listitem').filter({ hasText: 'Is the discount applied before tax?' });
      await row.click({ button: 'right' });
      const menu = page.getByRole('menu');
      await expect(menu).toHaveCount(1);
      await expect(menu.getByRole('menuitem', { name: 'Delete thread…' })).toBeInViewport();
      await page.keyboard.press('Escape');

      await threads.getByRole('textbox', { name: 'Question for Claude' }).fill('A note to read later.');
      const save = threads.getByRole('button', { name: 'Save as note' });
      await save.hover();
      await expect(save).toHaveAttribute('aria-describedby', /.+/);
      const tooltip = page.locator(`[id="${await save.getAttribute('aria-describedby')}"]`);
      await expect(tooltip).toHaveText('Save as note');
      const [anchor, tip] = [(await save.boundingBox())!, (await tooltip.boundingBox())!];
      expect(Math.abs(tip.x + tip.width / 2 - (anchor.x + anchor.width / 2))).toBeLessThan(anchor.width);
   });
});

/** Saves the workspace layout under the name from the Panels menu. */
async function saveLayoutAs(page: Page, name: string): Promise<void> {
   await page.getByRole('button', { name: 'Panels' }).click();
   await page.getByRole('menuitem', { name: 'Save layout as…' }).click();
   const dialog = page.getByRole('dialog', { name: 'Save layout' });
   await dialog.getByRole('textbox', { name: 'Name' }).fill(name);
   await dialog.getByRole('button', { name: 'Save', exact: true }).click();
   await expect(dialog).toBeHidden();
}

test.describe('the named layouts', () => {
   test('lays out a dragged panel in its group again from a saved layout after Default', async ({ page }) => {
      await openDemoTour(page, checkoutTour);
      await dragTab(page, 'Threads', center(page));
      await expect(region(page, 'Threads').getByRole('tab', { name: 'Code' })).toBeVisible();
      const dragged = await groups(page);
      await saveLayoutAs(page, 'Side by side');

      await arrange(page, 'Default');
      await expect.poll(() => groups(page)).toEqual([['Files', 'Plan'], ['Code'], ['Stop'], ['Threads']]);
      await arrange(page, 'Side by side');

      await expect.poll(() => groups(page)).toEqual(dragged);
      await expect(region(page, 'Threads').getByRole('tab', { name: 'Code' })).toBeVisible();
   });

   test('lists a layout that one page saves in another page’s menu, and leaves that page’s workspace as it is', async ({ context, browser, baseURL }) => {
      test.setTimeout(120_000);
      const first = await context.newPage();
      const world = nextWorld(first);
      await openDemoTour(first, checkoutTour);

      const { page: second, close } = await openSecondPage(browser, baseURL, await world);
      try {
         const before = await groups(second);

         await dragTab(first, 'Threads', center(first));
         await saveLayoutAs(first, 'Side by side');

         await second.getByRole('button', { name: 'Panels' }).click();
         await expect(second.getByRole('group', { name: 'My layouts' }).getByRole('menuitem', { name: 'Side by side' })).toBeVisible();
         expect(await groups(second)).toEqual(before);
      } finally {
         await close();
      }
   });
});
