import { expect, test, type Page } from '@playwright/test';
import { center, checkoutTour, dragTab, openDemoTour, openStop, showPanel } from './demo';

const region = (page: Page, name: string) => page.getByRole('region', { name, exact: true });

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
