import { fireEvent, screen, waitFor } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import { attachClaude, updateIteration } from '../harness';
import { resetPageEachTest } from './appPanels';
import {
   changedFiles,
   historyTemplate,
   historyWorld,
   load,
   modeSwitch,
   openFromFiles,
   openPinnedTour,
   planStop,
   reload,
   scrolledTo,
   shown,
   stopNavigation,
   stopPanel,
   traverse,
   unchangedFiles,
   watchScrollsEachTest,
} from './historySupport';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';

resetPageEachTest();
watchScrollsEachTest();

const tours = historyTemplate('history-stop-files-tours');

const stackKeys = (app: LotsenApp) => (app.stopFiles ?? []).map((file) => file.key);

describe('the browser history in the stop files mode', () => {
   beforeEach(() => {
      localStorage.setItem('lotsen.defaultViewMode', 'stopFiles');
      localStorage.setItem('lotsen.placement', 'side');
   });

   it('drops a restored file that the side does not have from the stack, and scrolls to the stop’s primary passage', async () => {
      const { world, totals, entry, stopEntry } = await historyWorld(tours);

      const app = load(world, world.transport(), `/${entry({ stop: totals, side: 'head', file: 'src/gone.ts' })}`);

      await waitFor(() => expect(location.hash).toBe(stopEntry));
      expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' });
      expect(stackKeys(app)).toEqual(['diff:src/pay.ts', 'diff:src/cart.ts']);
   });

   it('names no file for a stop that stacks none, so back and forward leave its stack empty', async () => {
      const { world, rounding, entry } = await historyWorld(tours);
      const app = await openPinnedTour(world, world.transport());
      fireEvent.click(planStop('Cart totals'));
      await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' }));

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await stopPanel().findByRole('heading', { name: 'Rounding' });

      expect(location.hash).toBe(entry({ stop: rounding }));
      await traverse(() => history.back());
      await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' }));
      await traverse(() => history.forward());
      await waitFor(() => expect(app.selectedStopId).toBe(rounding));
      expect(stackKeys(app)).toEqual([]);
   });

   it('closes the earlier stop’s file for a stop that stacks none, so the whole pull request shows what the address names', async () => {
      localStorage.setItem('lotsen.defaultViewMode', 'wholePr');
      const { world, seed, rounding, entry } = await historyWorld(tours);
      const app = await openPinnedTour(world, world.transport());
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      fireEvent.click(modeSwitch().getByRole('button', { name: 'Stop files' }));
      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await stopPanel().findByRole('heading', { name: 'Rounding' });

      fireEvent.click(modeSwitch().getByRole('button', { name: 'Whole PR' }));

      await waitFor(() => expect(screen.getByRole('main', { name: 'Diff' })).toBeTruthy());
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: rounding, file: null, line: null });
      expect(app.selectedDiff).toBeNull();
      expect(location.hash).toBe(entry({ stop: rounding }));
   });

   it('rebuilds the stack of an entry of the same stop, without the files opened after it', async () => {
      const { world, referenceEntry } = await historyWorld(tours);
      const app = await openPinnedTour(world, world.transport());
      fireEvent.click(planStop('Cart totals'));
      await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' }));

      fireEvent.click(stopPanel().getByRole('link', { name: 'the product type' }));
      await waitFor(() => expect(stackKeys(app)).toEqual(['diff:src/pay.ts', 'diff:src/cart.ts', 'text:head:src/catalog.ts']));
      expect(location.hash).toBe(referenceEntry);

      await traverse(() => history.back());

      await waitFor(() => expect(stackKeys(app)).toEqual(['diff:src/pay.ts', 'diff:src/cart.ts']));
      expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' });
   });

   describe('with files added from outside the stop', () => {
      const stopKeys = ['diff:src/pay.ts', 'diff:src/cart.ts'];
      const catalogKey = 'text:head:src/catalog.ts';
      const moneyKey = 'text:head:src/money.ts';

      async function openCartTotals(world: World, transport = world.transport()): Promise<LotsenApp> {
         const app = await openPinnedTour(world, transport);
         fireEvent.click(planStop('Cart totals'));
         await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' }));
         return app;
      }

      /** Opens the product type's `ref:` link, then src/money.ts from the whole-repository view. */
      async function addCatalogThenMoney(app: LotsenApp, moneyEntry: string): Promise<void> {
         fireEvent.click(stopPanel().getByRole('link', { name: 'the product type' }));
         await waitFor(() => expect(stackKeys(app)).toEqual([...stopKeys, catalogKey]));
         app.openRepositoryFile('head', 'src/money.ts');
         await waitFor(() => expect(stackKeys(app)).toEqual([...stopKeys, catalogKey, moneyKey]));
         expect(location.hash).toBe(moneyEntry);
      }

      async function moneyWorld() {
         const opened = await historyWorld(tours);
         return { ...opened, moneyEntry: opened.entry({ stop: opened.totals, side: 'head', file: 'src/money.ts' }) };
      }

      it('restores the files added in earlier entries of the stop on back, and the later ones on forward', async () => {
         const { world, moneyEntry } = await moneyWorld();
         const app = await openCartTotals(world);
         await addCatalogThenMoney(app, moneyEntry);

         await traverse(() => history.back());
         await waitFor(() => expect(app.stackTarget).toEqual({ key: catalogKey }));
         expect(stackKeys(app)).toEqual([...stopKeys, catalogKey]);
         await traverse(() => history.forward());
         await waitFor(() => expect(app.stackTarget).toEqual({ key: moneyKey }));
         expect(stackKeys(app)).toEqual([...stopKeys, catalogKey, moneyKey]);
         await traverse(() => history.go(-2));
         await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' }));
         expect(stackKeys(app)).toEqual(stopKeys);
      });

      it('restores the added files of an entry that back returns to from another stop', async () => {
         const { world, moneyEntry } = await moneyWorld();
         const app = await openCartTotals(world);
         await addCatalogThenMoney(app, moneyEntry);
         fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
         await stopPanel().findByRole('heading', { name: 'Rounding' });
         expect(stackKeys(app)).toEqual([]);

         await traverse(() => history.back());

         await waitFor(() => expect(app.stackTarget).toEqual({ key: moneyKey }));
         expect(stackKeys(app)).toEqual([...stopKeys, catalogKey, moneyKey]);
         expect(location.hash).toBe(moneyEntry);
      });

      it('keeps the added files on a reload, and waits for the changed files to stack them', async () => {
         const { world, totals, moneyEntry } = await moneyWorld();
         const transport = world.transport();
         await addCatalogThenMoney(await openCartTotals(world, transport), moneyEntry);
         const release = transport.hold('list_changed_files');

         const app = reload(world, transport);
         await waitFor(() => expect(app.selectedStopId).toBe(totals));
         expect(app.stopFiles).toBeNull();
         release();

         await waitFor(() => expect(app.stackTarget).toEqual({ key: moneyKey }));
         expect(stackKeys(app)).toEqual([...stopKeys, catalogKey, moneyKey]);
      });

      it('keeps the added files on a reload whose address carries a one-time code, as the demo’s does', async () => {
         const { world, moneyEntry } = await moneyWorld();
         const transport = world.transport();
         await addCatalogThenMoney(await openCartTotals(world, transport), moneyEntry);
         const state: unknown = history.state;
         const code = await world.uiCode();

         history.replaceState(state, '', `/#code=${code}&${moneyEntry.slice(1)}`);
         const app = reload(world, transport);

         await waitFor(() => expect(app.stackTarget).toEqual({ key: moneyKey }));
         expect(stackKeys(app)).toEqual([...stopKeys, catalogKey, moneyKey]);
         expect(location.hash).toBe(moneyEntry);
      });

      it('stacks only the entry’s own file beside the stop’s files when the entry keeps no added files, as in a new tab', async () => {
         const { world, moneyEntry } = await moneyWorld();
         const transport = world.transport();
         await addCatalogThenMoney(await openCartTotals(world, transport), moneyEntry);
         history.replaceState(null, '', location.href);

         const app = reload(world, transport);

         await waitFor(() => expect(app.stackTarget).toEqual({ key: moneyKey }));
         expect(stackKeys(app)).toEqual([...stopKeys, moneyKey]);
      });

      describe('while the center shows another view mode', () => {
         const switchTo = (name: 'Whole PR' | 'Stop files') => fireEvent.click(modeSwitch().getByRole('button', { name }));

         it('keeps the added files of the entries it passes through, so Stop files restores them later', async () => {
            const { world, moneyEntry } = await moneyWorld();
            const app = await openCartTotals(world);
            await addCatalogThenMoney(app, moneyEntry);
            await traverse(() => history.go(-2));
            switchTo('Whole PR');

            await traverse(() => history.go(2));
            await waitFor(() => expect(app.selectedFilePath ?? app.selectedText?.path).toBe('src/money.ts'));
            await traverse(() => history.go(-2));
            switchTo('Stop files');
            await traverse(() => history.go(2));

            await waitFor(() => expect(app.stackTarget).toEqual({ key: moneyKey }));
            expect(stackKeys(app)).toEqual([...stopKeys, catalogKey, moneyKey]);
         });

         it('restores the entry’s stack when the reviewer switches to Stop files after a reload into another default mode', async () => {
            const { world, moneyEntry } = await moneyWorld();
            const transport = world.transport();
            await addCatalogThenMoney(await openCartTotals(world, transport), moneyEntry);
            localStorage.setItem('lotsen.defaultViewMode', 'wholePr');

            const app = reload(world, transport);
            await waitFor(() => expect(app.selectedText?.path).toBe('src/money.ts'));
            switchTo('Stop files');

            await waitFor(() => expect(stackKeys(app)).toEqual([...stopKeys, catalogKey, moneyKey]));
            await traverse(() => history.back());
            await waitFor(() => expect(app.stackTarget).toEqual({ key: catalogKey }));
            expect(stackKeys(app)).toEqual([...stopKeys, catalogKey]);
         });

         it('adds no files to a new entry, and keeps the earlier entry’s files', async () => {
            const { world } = await moneyWorld();
            const app = await openCartTotals(world);
            fireEvent.click(stopPanel().getByRole('link', { name: 'the product type' }));
            await waitFor(() => expect(stackKeys(app)).toEqual([...stopKeys, catalogKey]));
            switchTo('Whole PR');

            app.openRepositoryFile('head', 'src/money.ts');
            await waitFor(() => expect(app.selectedText?.path).toBe('src/money.ts'));
            switchTo('Stop files');
            await waitFor(() => expect(stackKeys(app)).toEqual(stopKeys));
            await traverse(() => history.back());
            await waitFor(() => expect(app.stackTarget).toEqual({ key: catalogKey }));
            expect(stackKeys(app)).toEqual([...stopKeys, catalogKey]);
            await traverse(() => history.forward());

            await waitFor(() => expect(app.stackTarget).toEqual({ key: moneyKey }));
            expect(stackKeys(app)).toEqual([...stopKeys, moneyKey]);
         });

         it('adds no files to the entry of another stop, and keeps the earlier stop’s files', async () => {
            const { world, moneyEntry } = await moneyWorld();
            const app = await openCartTotals(world);
            await addCatalogThenMoney(app, moneyEntry);
            switchTo('Whole PR');

            fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
            await stopPanel().findByRole('heading', { name: 'Rounding' });
            switchTo('Stop files');
            expect(stackKeys(app)).toEqual([]);
            await traverse(() => history.back());

            await waitFor(() => expect(app.stackTarget).toEqual({ key: moneyKey }));
            expect(stackKeys(app)).toEqual([...stopKeys, catalogKey, moneyKey]);
         });
      });

      it('drops an added file that the side no longer has from the restored stack and from the entry', async () => {
         const { world, seed, referenceEntry } = await moneyWorld();
         const transport = world.transport();
         const first = await openCartTotals(world, transport);
         first.openRepositoryFile('head', 'src/money.ts');
         await waitFor(() => expect(stackKeys(first)).toEqual([...stopKeys, moneyKey]));
         fireEvent.click(stopPanel().getByRole('link', { name: 'the product type' }));
         await waitFor(() => expect(stackKeys(first)).toEqual([...stopKeys, moneyKey, catalogKey]));
         // The target drops src/money.ts and the source rebases onto it, which is a new iteration, and the reviewer updates the tour to it.
         const target = await world.repo.commit('main', 'A base without the money module', unchangedFiles);
         const head = await world.repo.commit('feature', 'The change, rebased', changedFiles, target);
         await world.pullRequest.set(head, target, { iteration: 2 });
         await world.reviewer.call('check_iteration', { tourId: seed.tourId });
         await world.idle();
         await attachClaude(world, seed.tourId);
         await updateIteration(world, seed.tourId);
         await world.idle();
         const app = reload(world, transport);

         await waitFor(() => expect(stackKeys(app)).toEqual([...stopKeys, catalogKey]));
         expect(app.stackTarget).toEqual({ key: catalogKey });
         expect(location.hash).toBe(referenceEntry);
         const moneyRequests = () => transport.requests.filter((request) => JSON.stringify(request.input ?? {}).includes('money')).length;
         const requested = moneyRequests();
         fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
         await stopPanel().findByRole('heading', { name: 'Rounding' });
         await traverse(() => history.back());
         await waitFor(() => expect(app.stackTarget).toEqual({ key: catalogKey }));
         expect(stackKeys(app)).toEqual([...stopKeys, catalogKey]);
         await new Promise((resolve) => setTimeout(resolve, 20));
         expect(moneyRequests()).toBe(requested);
      });
   });
});

it('restores a file opened from Files into the stop files stack, and back scrolls the stack to the passage before it', async () => {
   localStorage.setItem('lotsen.defaultViewMode', 'stopFiles');
   const { world, fileEntry } = await historyWorld(tours);
   const app = await openPinnedTour(world, world.transport());
   fireEvent.click(planStop('Cart totals'));
   await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' }));
   const length = history.length;

   await openFromFiles('cart.ts, Modified');
   await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/cart.ts' }));
   expect(location.hash).toBe(fileEntry);
   expect(history.length).toBe(length + 1);

   await traverse(() => history.back());
   await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/pay.ts' }));
   expect(app.focus).toMatchObject({ path: 'src/pay.ts', line: 20 });
   await traverse(() => history.forward());
   await waitFor(() => expect(app.stackTarget).toEqual({ key: 'diff:src/cart.ts' }));
   expect(app.showsStopFiles).toBe(true);
});
