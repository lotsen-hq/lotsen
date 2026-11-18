import { expect, test, type Locator, type Page } from '@playwright/test';
import { openDemoTour } from './demo';

const orientTour = 'Find your way around the storefront';

const plan = (page: Page) => page.getByRole('tabpanel', { name: 'Plan' });
const item = (page: Page, number: number) =>
   plan(page)
      .locator('.stop-item')
      .nth(number - 1);

/** Opens the orientation tour, whose third stop has a dive, with the demo panel closed, because it lies over the plan's rows. */
async function openPlan(page: Page): Promise<void> {
   await openDemoTour(page, orientTour);
   await page.locator('details', { hasText: 'Demo' }).first().locator('summary').click();
   await expect(item(page, 3).getByRole('button', { name: /^Dives from stop 3/ })).toBeVisible();
}

/** Moves the pointer off the plan and the focus off the row, so the row is at rest. */
async function rest(page: Page): Promise<void> {
   await page.mouse.move(0, 0);
   await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
}

/** Points at the start of the row's title, away from its controls and their tooltips. */
async function hover(row: Locator): Promise<void> {
   await row.locator('.stop-row').hover({ position: { x: 8, y: 8 } });
}

async function markDone(page: Page, row: Locator, number: number): Promise<void> {
   await hover(row);
   const checkbox = row.getByRole('checkbox', { name: new RegExp(`^Stop ${number} done`) });
   await checkbox.click();
   await expect(checkbox).toHaveAttribute('aria-checked', 'true');
   await rest(page);
}

/** Returns the left edge of each control in the row that shows, by its class. */
async function shownLefts(row: Locator): Promise<Record<string, number>> {
   return row
      .locator('.stop-actions')
      .evaluate((actions) =>
         Object.fromEntries(
            [...actions.querySelectorAll<HTMLElement>('.dive-marker, .skip-toggle, .done-toggle')]
               .filter((control) => control.checkVisibility({ visibilityProperty: true }))
               .map((control) => [control.classList.value.match(/dive-marker|skip-toggle|done-toggle/)![0], control.getBoundingClientRect().left]),
         ),
      );
}

/** Expects hovering the row to show its other controls and to leave every control that showed at rest where it was. */
async function expectStillUnderPointer(page: Page, row: Locator, atRest: readonly string[], state: string): Promise<void> {
   const hovered = ['dive-marker', 'done-toggle', 'skip-toggle'].filter((name) => name !== 'dive-marker' || atRest.includes(name));
   await rest(page);
   await expect.poll(async () => Object.keys(await shownLefts(row)).sort(), { message: `the controls at rest on ${state}` }).toEqual([...atRest].sort());
   const before = await shownLefts(row);

   await hover(row);
   await expect.poll(async () => Object.keys(await shownLefts(row)).sort(), { message: `the controls hovered on ${state}` }).toEqual(hovered);
   const after = await shownLefts(row);
   for (const [control, left] of Object.entries(before)) {
      expect(after[control], `the ${control} on ${state}`).toBe(left);
   }
}

test('hovering a stop row shows its other controls and moves none of those that showed', async ({ page }) => {
   await openPlan(page);
   const first = item(page, 1);
   const third = item(page, 3);
   await markDone(page, first, 1);

   await expectStillUnderPointer(page, first, ['done-toggle'], 'a done stop');
   await expectStillUnderPointer(page, third, ['dive-marker'], 'a stop with dives that is not done');
   await markDone(page, third, 3);
   await expectStillUnderPointer(page, third, ['dive-marker', 'done-toggle'], 'a done stop with dives');
});

test('the done checkboxes of rows in different skip and dive states share one horizontal position', async ({ page }) => {
   await openPlan(page);
   const rows = [item(page, 1), item(page, 2), item(page, 3)];
   for (const [index, row] of rows.entries()) {
      await markDone(page, row, index + 1);
   }
   await hover(rows[1]!);
   await rows[1]!.getByRole('button', { name: 'Skip stop 2' }).click();
   await expect(rows[1]!.getByRole('button', { name: 'Skip stop 2' })).toHaveAttribute('aria-pressed', 'true');
   await rest(page);

   const lefts = await Promise.all(rows.map(async (row) => (await row.locator('.done-toggle').boundingBox())!.x));
   expect(lefts[1], 'a skipped stop against a stop with no skip or dive').toBe(lefts[0]);
   expect(lefts[2], 'a stop with dives against a stop with no skip or dive').toBe(lefts[0]);
});

test('a hidden control is out of the tab order and the accessibility tree until the focus enters its row', async ({ page }) => {
   await openPlan(page);
   const cart = item(page, 2);
   await rest(page);
   await expect(cart.getByRole('checkbox', { name: /^Stop 2 done/ })).toHaveCount(0);
   await expect(cart.getByRole('button', { name: 'Skip stop 2' })).toHaveCount(0);

   await item(page, 3).locator('.stop-row').focus();
   await page.keyboard.press('Shift+Tab');
   await expect(cart.locator('.stop-row')).toBeFocused();
   await expect(cart.getByRole('button', { name: 'Skip stop 2' })).toBeVisible();

   await page.keyboard.press('Tab');
   await expect(cart.getByRole('button', { name: 'Skip stop 2' })).toBeFocused();
});
