import { screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { pinningTour } from '../../demo/sampleTours';
import { seedPinningTour } from '../../demo/seed';
import { readTour, startApp } from '../harness';
import { resetPageEachTest } from './appPanels';
import { alerts, sampleWorld, select, showTab, specs, threadsPanel } from './sampleWorld';
import { toggleShown } from './threadFilter';
import { openThreadView } from './threadView';

resetPageEachTest();

describe('the sample tours in the UI', () => {
   it.each([
      ['src/api/gateway.generated.ts', 'generated'],
      ['src/checkout/receipt.ts', 'mechanical'],
   ])("show the checkout tour's file %s collapsed as %s with its reason in its row's label, and expand it", async (path, mark) => {
      const { world, tour } = await sampleWorld();
      const reason = (await readTour(world, tour('checkout-rework').tourId)).plan!.fileMarks.find((candidate) => candidate.path === path)!.reason;
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      const files = await showTab('Files');
      const name = path.split('/').at(-1)!;
      const row = await files.findByRole('treeitem', { name: new RegExp(`^${name.replaceAll('.', '\\.')}, .*${mark}: `) });
      expect(row.getAttribute('aria-label')).toContain(reason);

      app.selectFile(path);

      const main = screen.getByRole('main', { name: 'Diff' });
      const show = await within(main).findByRole('button', { name: 'Show the diff' });
      expect(main.textContent).toContain(reason);
      show.click();
      await waitFor(() => expect(main.querySelector('.cm-editor')).not.toBeNull());
      expect(alerts()).toEqual([]);
   });

   it.each([...specs.map((spec) => [spec.key, typeof spec.pin === 'object' ? 'failed' : 'pinned'] as const), [pinningTour.key, 'pinning'] as const])(
      'show %s, whose pin is %s, without an error beyond a failed pin',
      async (key, pinState) => {
         const { world } = await sampleWorld();
         if (pinState === 'pinning') {
            await seedPinningTour(world, await world.claude());
         }

         const { app } = await startApp(world);
         await select(app, key);
         expect(app.selectedTourError).toBeNull();
         expect(alerts()).toEqual([]);

         await showTab('Files');

         expect(alerts()).toEqual(pinState === 'failed' ? [expect.stringContaining('could not be pinned')] : []);
         await world.pullRequest.release();
      },
   );

   it("show every thread of the checkout tour, open and resolved, with the answer's ref: link opening its code", async () => {
      const { world } = await sampleWorld();
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      const open = app.selectedThreads.filter((thread) => thread.state === 'open').length;

      await waitFor(() => expect(threadsPanel().getAllByRole('listitem')).toHaveLength(open));
      expect(open).toBeLessThan(app.selectedThreads.length);
      await toggleShown('Resolved');
      await toggleShown('Outdated');
      await waitFor(() => expect(threadsPanel().getAllByRole('listitem')).toHaveLength(app.selectedThreads.length));
      const linked = app.selectedThreads.find((thread) => thread.kind !== 'note' && thread.messages.some((message) => message.kind !== 'question' && message.html?.includes('>total<')));
      (await (await openThreadView(linked!.id)).findAllByRole('link', { name: 'total' }))[0]!.click();

      await waitFor(() => expect(app.selectedFile?.path).toBe('src/pricing/totals.ts'));
      expect(alerts()).toEqual([]);
   });

   it('open the summary page of every sample tour without an error', async () => {
      const { world } = await sampleWorld();
      await seedPinningTour(world, await world.claude());
      const { app } = await startApp(world);

      for (const key of [...specs.map((spec) => spec.key), pinningTour.key]) {
         await select(app, key);
         app.openSummary();
         const page = within(await screen.findByRole('main', { name: 'Summary' }));
         expect(await page.findByRole('region', { name: /^Decisions/ }), key).toBeTruthy();
         expect(page.queryAllByRole('alert'), key).toEqual([]);
      }

      await world.pullRequest.release();
   });

   it("fill every group of the checkout tour's summary, and its stale stops once the tour updates to the new iteration", async () => {
      const { world } = await sampleWorld();
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      app.openSummary();
      const page = within(await screen.findByRole('main', { name: 'Summary' }));
      await page.findByRole('region', { name: /^Decisions/ });
      const groupItems = (name: RegExp) => page.getByRole('region', { name }).querySelectorAll(':scope li').length;

      for (const group of [/^Decisions/, /^Concerns/, /^Open questions/, /^Outdated answers/, /^Open threads/, /^Resolved threads/, /^Outdated threads/, /^Skipped stops/]) {
         expect(groupItems(group), String(group)).toBeGreaterThan(0);
      }

      expect(page.getByRole('region', { name: /^Concerns/ }).querySelector('.kind-pill[data-kind="risk"]')).not.toBeNull();
      expect(groupItems(/^Stale stops/)).toBe(0);
      expect(await app.updateIteration()).toBeNull();
      await waitFor(() => expect(groupItems(/^Stale stops/)).toBeGreaterThan(0));
   });
});
