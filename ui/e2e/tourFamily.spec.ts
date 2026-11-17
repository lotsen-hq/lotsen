import { expect, test, type Page } from '@playwright/test';
import type { QuestionThread, Tour } from '../src/lib/contract.generated';
import { joinWorld } from '../tests/backend/backend';
import { nextWorld, openDemoTour, openStop } from './demo';

const orientTour = 'Find your way around the storefront';
const diveTitle = 'How the cart merges its lines';

const stopPanel = (page: Page) => page.getByRole('region', { name: 'Stop', exact: true });
const switcher = (page: Page) => page.getByRole('button', { name: 'Choose another tour' });
const breadcrumb = (page: Page) => page.getByRole('banner').first().getByRole('navigation', { name: 'Breadcrumb' });

test("a dive from a stop shows in the parent's Dives row and Plan panel, and its top bar leads back to the parent", async ({ page, baseURL }) => {
   const demo = nextWorld(page);
   await openDemoTour(page, orientTour);
   const { world: worldId, uiCredential, seed } = await demo;
   // The demo adds the host's token to the control requests of a client on a loopback name, so the test acts as Claude on the page's world.
   const world = joinWorld({ url: baseURL!, token: '' }, { world: worldId, url: baseURL!, uiCredential }, seed);
   const claude = await world.claude();
   await claude.call('attach_tour', { tourId: seed.tours['storefront-orient']!.tourId });
   await openStop(page, 'The cart');

   await stopPanel(page).getByRole('button', { name: 'Ask Claude' }).click();
   await page.getByRole('menuitem', { name: 'Dive…' }).click();
   await stopPanel(page).getByRole('button', { name: 'Send to Claude' }).click();

   const question = await claude.next('question', 30_000);
   const questionId = String(question.questionId);
   const { thread } = (await claude.call('fetch_question', { tourId: question.tourId, questionId })) as { thread: QuestionThread };
   expect(thread.kind === 'asked' && thread.action?.kind).toBe('dive');
   const action = thread.kind === 'asked' && thread.action?.kind === 'dive' ? thread.action : null;
   const stopId = thread.anchor.kind === 'stop' ? thread.anchor.stopId : undefined;
   const dive = (await claude.call('create_tour', {
      diveOrigin: { parentTourId: question.tourId, purpose: action!.purpose, title: diveTitle, stopId },
      familiarity: action!.familiarity,
   })) as Tour;
   await world.idle();
   const planned = (await claude.call('set_plan', { tourId: dive.id, chapters: [{ id: 'c1', title: 'Lines' }], stops: [{ title: 'Merging lines', kind: 'context', chapter: 'c1' }] })) as Tour;
   await claude.call('put_stop', {
      tourId: dive.id,
      stopId: planned.plan!.stops[0]!.id,
      headline: 'An add of a product already in the cart raises its quantity.',
      passages: [{ side: 'head', path: 'src/checkout/cart.ts', startLine: 12, endLine: 14, role: 'primary' }],
   });
   await claude.call('reply', { tourId: question.tourId, questionId, resolution: 'done', text: `I created the dive "${diveTitle}".` });

   const dives = stopPanel(page).getByRole('region', { name: 'Dives' });
   const link = dives.getByRole('button', { name: diveTitle });
   await expect(link).toBeVisible();
   await expect(link).toHaveAccessibleDescription('Not started');
   await expect(page.getByRole('tabpanel', { name: 'Plan' }).getByRole('button', { name: 'Dives from stop 2: 1' })).toBeVisible();

   await link.focus();
   await page.keyboard.press('Enter');
   await expect(switcher(page)).toBeFocused();

   await expect(breadcrumb(page).getByRole('button', { name: orientTour })).toBeVisible();
   await expect(breadcrumb(page).locator('[aria-current="page"]')).toHaveText(diveTitle);

   await breadcrumb(page).getByRole('button', { name: orientTour }).focus();
   await page.keyboard.press('Enter');
   await expect(switcher(page)).toBeFocused();

   await expect(breadcrumb(page)).toHaveCount(0);
   await expect(page.getByRole('banner').first().locator('[data-heading-part="title"]')).toHaveText(orientTour);
});

test("a dive's breadcrumb widens the tour zone, and a narrow top bar keeps the zone at its usual width", async ({ page }) => {
   await openDemoTour(page, 'How a payment reaches the gateway');
   const zone = page.getByRole('banner').first().locator('[data-tour-zone]');
   await expect(breadcrumb(page).getByRole('button', { name: orientTour })).toBeVisible();
   const zoneLimit = 18 * (await page.locator('html').evaluate((node) => parseFloat(getComputedStyle(node).fontSize)));

   expect((await zone.boundingBox())!.width).toBeGreaterThan(zoneLimit);

   await page.setViewportSize({ width: 900, height: 800 });
   await expect.poll(async () => (await zone.boundingBox())!.width).toBeLessThanOrEqual(zoneLimit + 0.5);
   await expect(breadcrumb(page).getByRole('button', { name: orientTour })).toBeVisible();
});

test("a dive marker's menu opens the dive from the keyboard and leaves the focus on the tour switcher", async ({ page }) => {
   await openDemoTour(page, orientTour);
   const marker = page.getByRole('tabpanel', { name: 'Plan' }).getByRole('button', { name: 'Dives from stop 3: 1' });
   await marker.focus();
   await page.keyboard.press('Enter');
   await page.getByRole('menuitem', { name: 'How a payment reaches the gateway, In progress' }).focus();

   await page.keyboard.press('Enter');

   await expect(breadcrumb(page).getByRole('button', { name: orientTour })).toBeVisible();
   await expect(switcher(page)).toBeFocused();
});

test('deleting a parent names its dive in the confirmation, and both leave the tour list', async ({ page }) => {
   const diveName = 'How a payment reaches the gateway';
   await openDemoTour(page, orientTour);
   await switcher(page).click();
   const list = page.getByRole('navigation', { name: 'Tours' });
   const row = list.getByRole('listitem').filter({ has: page.locator('[id^="tour-label-"]', { hasText: orientTour }) });
   await row.getByRole('button', { name: 'Delete this tour' }).click();

   const dialog = page.getByRole('alertdialog', { name: 'Delete this tour?' });
   await expect(dialog).toContainText(`The tour “${orientTour}” of`);
   await expect(dialog).toContainText('and the dive below it are deleted for good');
   await expect(dialog.getByRole('list', { name: 'Dives deleted with it' }).getByRole('listitem')).toHaveText([diveName]);
   await dialog.getByRole('button', { name: 'Delete tour' }).click();

   await expect(dialog).toHaveCount(0);
   await expect(page.getByRole('button', { name: 'Choose a tour' })).toBeFocused();
   await page.getByRole('button', { name: 'Choose a tour' }).click();
   await expect(list.locator('[id^="tour-label-"]', { hasText: orientTour })).toHaveCount(0);
   await expect(list.locator('[id^="tour-label-"]', { hasText: diveName })).toHaveCount(0);
   await expect(list.getByRole('listitem').first()).toBeVisible();
});
