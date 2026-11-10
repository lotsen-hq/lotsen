import { expect, test } from '@playwright/test';
import { worldPath, type DemoWorld } from '../demo/pageWorld';

test('three pages that load together each get a world of their own, and each binds its sample tours to its sessions', async ({ context }) => {
   test.setTimeout(120_000);
   const pages = await Promise.all([0, 1, 2].map(() => context.newPage()));
   const worlds = pages.map(async (page) => {
      const answer = await page.waitForResponse((response) => new URL(response.url()).pathname === worldPath && response.request().method() === 'POST');
      return ((await answer.json()) as DemoWorld).world;
   });

   await Promise.all(pages.map((page) => page.goto('/')));

   expect(new Set(await Promise.all(worlds)).size).toBe(3);
   // The page shows the tour picker only after `attach_tour` bound every sample tour that has a session, and the three pages wait on that work at once.
   await Promise.all(pages.map((page) => expect(page.getByRole('button', { name: /^(Choose a tour|Tour: .+, choose another)$/ })).toBeVisible({ timeout: 90_000 })));
});
