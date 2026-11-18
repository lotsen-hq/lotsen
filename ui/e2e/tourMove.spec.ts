import { expect, test, type Page } from '@playwright/test';
import { sampleRepository, sampleRepositoryUrl } from '../demo/sampleTours';
import type { QuestionThread, Tour } from '../src/lib/contract.generated';
import { joinWorld } from '../tests/backend/backend';
import { checkoutTour, nextWorld, openDemoTour, tourSwitcherName } from './demo';

const tourTitle = 'Where the checkout starts';
const diveTitle = 'How a payment is charged';
const paymentPath = 'src/checkout/payment.ts';
// The default branch's next commit rounds the amount, which changes a line of every passage on the payment and none of the passage on the cart.
const roundedPayment = sampleRepository.push[paymentPath]!.replace('const amount = total(cart.items, taxRate);', 'const amount = Math.round(total(cart.items, taxRate) * 100) / 100;');

const header = (page: Page) => page.getByRole('banner').first();
const switcher = (page: Page) => page.getByRole('button', { name: tourSwitcherName });
const moveButton = (page: Page) => header(page).getByRole('button', { name: 'Move the tour to the latest commit…' });
const planPanel = (page: Page) => page.getByRole('tabpanel', { name: 'Plan' });

async function openTour(page: Page, title: string): Promise<void> {
   await switcher(page).click();
   await page
      .getByRole('navigation', { name: 'Tours' })
      .getByRole('button', { name: new RegExp(title) })
      .first()
      .click();
}

test('a move takes a repository tour and its ticked dive to the latest commit of the default branch, and marks the stale stops of each', async ({ page, baseURL }) => {
   const demo = nextWorld(page);
   // A pull request's tour, so the page checks no repository tour for a move before the test sets the branch.
   await openDemoTour(page, checkoutTour);
   const { world: worldId, uiCredential, seed } = await demo;
   // The demo adds the host's token to the control requests of a client on a loopback name, so the test acts as Claude on the page's world.
   const world = joinWorld({ url: baseURL!, token: '' }, { world: worldId, url: baseURL!, uiCredential }, seed);
   const pushed = seed.heads[1];
   const tags = { 'v2.4.0': pushed };
   await world.repository.set(pushed, { tags });
   const claude = await world.claude();
   const tour = (await claude.call('create_tour', { repository: { url: sampleRepositoryUrl, purpose: 'orient', title: tourTitle }, familiarity: 'some' })) as Tour;
   await world.idle();
   const planned = (await claude.call('set_plan', {
      tourId: tour.id,
      chapters: [{ id: 'c1', title: 'Checkout' }],
      stops: [
         { title: 'The cart', kind: 'context', chapter: 'c1' },
         { title: 'Paying', kind: 'inspect', chapter: 'c1' },
      ],
   })) as Tour;
   const [cartStop, paymentStop] = planned.plan!.stops.map((stop) => stop.id);
   await claude.call('put_stop', {
      tourId: tour.id,
      stopId: cartStop,
      headline: 'The cart checks each quantity.',
      passages: [{ side: 'head', path: 'src/checkout/cart.ts', startLine: 12, endLine: 14, role: 'primary' }],
   });
   await claude.call('put_stop', {
      tourId: tour.id,
      stopId: paymentStop,
      headline: '`pay` charges the total.',
      passages: [{ side: 'head', path: paymentPath, startLine: 11, endLine: 23, role: 'primary' }],
   });
   const dive = (await claude.call('create_tour', { diveOrigin: { parentTourId: tour.id, stopId: paymentStop, purpose: 'orient', title: diveTitle }, familiarity: 'some' })) as Tour;
   await world.idle();
   const divePlan = (await claude.call('set_plan', { tourId: dive.id, chapters: [{ id: 'c1', title: 'Charging' }], stops: [{ title: 'The charge', kind: 'inspect', chapter: 'c1' }] })) as Tour;
   await claude.call('put_stop', {
      tourId: dive.id,
      stopId: divePlan.plan!.stops[0]!.id,
      headline: 'The gateway charges the amount.',
      passages: [{ side: 'head', path: paymentPath, startLine: 11, endLine: 23, role: 'primary' }],
   });
   const latest = await world.repo.commit('main', 'Round the amount', { [paymentPath]: roundedPayment }, pushed);
   await world.repository.set(latest, { tags });

   await openTour(page, tourTitle);
   await expect(header(page).locator('[data-tour-commit]')).toHaveText(pushed.slice(0, 8));
   await moveButton(page).click();
   const dialog = page.getByRole('dialog', { name: 'Move to the latest commit' });
   await expect(dialog.locator('[data-move-commits] dd')).toHaveText([pushed.slice(0, 8), latest.slice(0, 8)]);
   await expect(dialog.getByRole('list', { name: 'Dives that move with it' }).getByRole('checkbox', { name: diveTitle })).toBeChecked();
   await dialog.getByRole('button', { name: 'Move' }).click();

   await expect(dialog).toHaveCount(0);
   await expect(moveButton(page)).toBeFocused();
   await expect(header(page).locator('[data-tour-commit]')).toHaveText(latest.slice(0, 8));
   await page.getByRole('tab', { name: 'Plan' }).click();
   await expect(planPanel(page).getByRole('button', { name: /^2 Inspect: Paying/ })).toContainText('Stale since move 1');
   await expect(planPanel(page).getByRole('button', { name: /^1 Context: The cart/ })).not.toContainText('Stale');
   const question = await claude.next('question', 30_000);
   const { thread } = (await claude.call('fetch_question', { tourId: question.tourId, questionId: String(question.questionId) })) as { thread: QuestionThread };
   expect(thread.kind === 'decided' && thread.decision).toMatchObject({ kind: 'tourMoved', move: 1, commit: latest, stopIds: [paymentStop] });

   await openTour(page, diveTitle);
   await expect(header(page).locator('[data-tour-commit]')).toHaveText(latest.slice(0, 8));
   await page.getByRole('tab', { name: 'Plan' }).click();
   await expect(planPanel(page).getByRole('button', { name: /^1 Inspect: The charge/ })).toContainText('Stale since move 1');
});
