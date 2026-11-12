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
