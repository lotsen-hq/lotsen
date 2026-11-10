import { expect, test } from '@playwright/test';
import { checkoutTour, openDemoTour } from './demo';

test('the file trees log no Svelte warning while the reviewer moves through them', async ({ page }) => {
   const warnings: string[] = [];
   page.on('console', (message) => {
      if (message.text().includes('[svelte]')) {
         warnings.push(message.text());
      }
   });
   await openDemoTour(page, checkoutTour);
   await page.getByRole('tab', { name: 'Files' }).click();

   const changed = page.getByRole('tree', { name: 'Changed files' });
   await changed.getByRole('treeitem').first().locator('.row').first().click();
   await page.keyboard.press('ArrowDown');
   await page.keyboard.press('ArrowDown');
   await expect(changed.getByRole('treeitem').nth(2)).toBeFocused();

   await page.getByRole('button', { name: 'Repository' }).click();
   const repository = page.getByRole('tree', { name: 'Repository files' });
   await repository.getByRole('treeitem').first().locator('.row').first().click();
   // The folder's rows arrive after it expands, and an arrow key before then moves the focus past the folder.
   await expect(repository.getByRole('treeitem').nth(1)).toHaveAttribute('data-id', 'assets/banner.png');
   await page.keyboard.press('ArrowDown');
   await expect(repository.getByRole('treeitem').nth(1)).toBeFocused();

   expect(warnings).toEqual([]);
});
