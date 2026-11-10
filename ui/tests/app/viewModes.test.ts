import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { Tour } from '../../src/lib/types';
import type { Transport, World } from '../backend/backend';
import { attachClaude, backend, prLabel, seedTour, startApp } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { filesPanel, planPanel, resetPageEachTest, tab, worldOf } from './appPanels';
import {
   chooseMode,
   dimmedFiles,
   keepStops,
   lineMarks,
   openPinnedTour,
   openStopIn,
   passagePosition,
   planStop,
   pressed,
   scatteredSpec,
   scrolledTo,
   spyOnEditorsEachTest,
   stopNavigation,
   stopPanel,
   totals,
   type SeededIds,
} from './stopViewSupport';

resetPageEachTest();
spyOnEditorsEachTest();

const otherUrl = 'https://platform.test/shop/pull/7';
const otherLabel = 'shop #7';

const oneTour = backend().template('viewModes-tour', async (world): Promise<SeededIds> => {
   const { tourId, stopIds } = await seedTour(world, scatteredSpec);
   return { tourId, stopIds };
});

// A second tour on another pull request URL, which the platform resolves to the same commits.
const twoTours = backend().template('viewModes-twoTours', async (world): Promise<SeededIds> => {
   const { tourId, stopIds } = await seedTour(world, scatteredSpec);
   const claude = await world.claude();
   const other = (await claude.call('create_tour', { prUrl: otherUrl, familiarity: 'some' })) as Tour;
   await world.idle();
   const planned = (await claude.call('set_plan', { tourId: other.id, chapters: [{ id: 'c1', title: 'Chapter' }], stops: [{ title: 'Elsewhere', kind: 'inspect', chapter: 'c1' }] })) as Tour;
   await claude.call('put_stop', { tourId: other.id, stopId: planned.plan!.stops[0]!.id, ...totals });
   return { tourId, stopIds };
});

/** Picks the tour of the label from the tour list, without waiting for its plan. */
async function chooseTour(label: string): Promise<void> {
   const list = await openTourList();
   list
      .getAllByRole('button', tourButton)
      .find((candidate) => candidate.textContent?.includes(label))!
      .click();
}

async function started(template = oneTour): Promise<{ world: World; seed: SeededIds; app: LotsenApp; transport: Transport }> {
   const { world, seed } = await worldOf(template);
   const { app, transport } = await startApp(world);
   return { world, seed, app, transport };
}

/** Makes every read and write of localStorage throw, as a browser that blocks site data does, and leaves sessionStorage, which holds the UI credential, working. */
function blockLocalStorage(): void {
   const { getItem, setItem } = Storage.prototype;
   vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
      if (this === localStorage) {
         throw new DOMException('Blocked', 'SecurityError');
      }

      return getItem.call(this, key);
   });
   vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, key: string, value: string) {
      if (this === localStorage) {
         throw new DOMException('Blocked', 'SecurityError');
      }

      setItem.call(this, key, value);
   });
}

describe('the view modes', () => {
   it('shows the whole pull request by default, with the files outside the stop and the lines outside its passages dimmed, and nothing collapsed', async () => {
      const { app } = await started();
      await openPinnedTour(app);
      expect(pressed('Whole PR')).toBe(true);
      expect(pressed('Step through')).toBe(false);

      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);

      await waitFor(() => expect([19, 20, 22, 23].map((line) => lineMarks('Diff', 1).get(line))).toEqual(['dimmed', 'primary', 'primary', 'dimmed']));
      expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.lotsen-collapsed, .lotsen-passage-focused')).toHaveLength(0);
      expect(screen.queryByRole('navigation', { name: 'Passages' })).toBeNull();
      fireEvent.click(tab('Files'));
      expect(await dimmedFiles()).toEqual(['src/cart.ts']);

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));
      await waitFor(async () => expect(await dimmedFiles()).toEqual([]));
   });

   it('dims the files outside the stop in Files in every view mode while its toggle is on, and none with it off', async () => {
      const { app } = await started();
      await openPinnedTour(app);
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      fireEvent.click(tab('Files'));
      const dimOthers = async () => (await filesPanel()).getByRole('button', { name: 'Dim other files' });
      expect((await dimOthers()).getAttribute('aria-pressed')).toBe('true');

      for (const [dimming, expected] of [
         [true, ['src/cart.ts']],
         [false, []],
      ] as const) {
         if (!dimming) {
            fireEvent.click(await dimOthers());
         }

         for (const mode of ['Whole PR', 'Stop files', 'Step through'] as const) {
            chooseMode(mode);
            await waitFor(() => expect(pressed(mode)).toBe(true));
            await waitFor(async () => expect(await dimmedFiles()).toEqual(expected));
         }
      }

      expect(localStorage.getItem('lotsen.dimOtherFiles')).toBe('false');
   });

   it('keeps the Files toggle and the diff’s dimming toggle apart', async () => {
      const diffDim = () => within(screen.getByRole('main', { name: 'Diff' })).getByRole('button', { name: 'Dim outside the passages' });
      const { app } = await started();
      await openPinnedTour(app);
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      await waitFor(() => expect([19, 20].map((line) => lineMarks('Diff', 1).get(line))).toEqual(['dimmed', 'primary']));
      fireEvent.click(tab('Files'));
      const dimOthers = (await filesPanel()).getByRole('button', { name: 'Dim other files' });

      fireEvent.click(diffDim());

      await waitFor(() => expect([19, 20].map((line) => lineMarks('Diff', 1).get(line))).toEqual(['', 'primary']));
      expect(await dimmedFiles()).toEqual(['src/cart.ts']);
      expect(dimOthers.getAttribute('aria-pressed')).toBe('true');

      fireEvent.click(diffDim());
      await waitFor(() => expect([19, 20].map((line) => lineMarks('Diff', 1).get(line))).toEqual(['dimmed', 'primary']));
      fireEvent.click(dimOthers);

      await waitFor(async () => expect(await dimmedFiles()).toEqual([]));
      expect([19, 20].map((line) => lineMarks('Diff', 1).get(line))).toEqual(['dimmed', 'primary']);
      expect(diffDim().getAttribute('aria-pressed')).toBe('true');
      expect(localStorage.getItem('lotsen.dimming')).toBe('true');
   });

   it('opens the passage that the reviewer last opened from the stop when switching to step through, and shows every passage again in the whole pull request', async () => {
      const { app } = await started();
      await openPinnedTour(app);
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo('Diff', 'src/cart.ts', 'head', 5);
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /caller/ }));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 30);
      fireEvent.click(tab('Files'));
      fireEvent.click((await filesPanel()).getByText('cart.ts'));
      await waitFor(() => expect(screen.getByRole('main', { name: 'Diff' }).querySelector('code')?.textContent).toBe('src/cart.ts'));

      chooseMode('Step through');

      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
      expect(passagePosition()).toBe('2/4 lines 30–31');

      chooseMode('Whole PR');

      await waitFor(() => expect(lineMarks('Diff', 1).get(30)).toBe('related'));
      expect(screen.getByRole('main', { name: 'Diff' }).querySelector('code')?.textContent).toBe('src/pay.ts');
      expect(screen.queryByRole('navigation', { name: 'Passages' })).toBeNull();
   });

   it('shows every region again when the mode changes', async () => {
      const { app } = await started();
      await openStopIn(app, 'Step through');
      const diff = screen.getByRole('main', { name: 'Diff' });
      fireEvent.click(await within(diff).findByRole('button', { name: 'Collapse to passages' }));
      await waitFor(() => expect(diff.querySelectorAll('.lotsen-collapsed').length).toBeGreaterThan(0));

      chooseMode('Whole PR');

      await waitFor(() => expect(diff.querySelectorAll('.lotsen-collapsed')).toHaveLength(0));
      expect(within(diff).getByRole('button', { name: 'Collapse to passages' }).getAttribute('aria-pressed')).toBe('false');
   });

   it('keeps the mode chosen in the bar across the stops of the tour, without storing it, and opens the tour in the default after a reload', async () => {
      const { world, app } = await started();
      await openStopIn(app, 'Step through');

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(passagePosition()).toBe('1/1 lines 20–22');
      expect(localStorage.getItem('lotsen.viewMode')).toBeNull();

      app.stop();
      cleanup();
      history.replaceState(null, '', '/');
      const { app: reloaded } = await startApp(world);
      await openPinnedTour(reloaded);
      fireEvent.click(planStop('Cart totals'));

      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(pressed('Whole PR')).toBe(true);
   });

   it('opens each tour in the default mode from the settings, and leaves the current mode alone when the default changes', async () => {
      localStorage.setItem('lotsen.defaultViewMode', 'stepThrough');
      const { app } = await started(twoTours);
      await openPinnedTour(app);
      expect(pressed('Step through')).toBe(true);
      chooseMode('Stop files');

      fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
      const settings = within(await screen.findByRole('dialog', { name: 'Settings' }));
      const group = settings.getByRole('radiogroup', { name: 'Default view mode' });
      expect(document.getElementById(group.getAttribute('aria-describedby') ?? '')?.textContent).toBe(
         'Each tour opens in this mode. A mode you choose in the diff toolbar lasts until another tour opens.',
      );
      const defaults = within(group);
      expect(defaults.getByRole('radio', { name: 'Step through' }).getAttribute('aria-checked')).toBe('true');
      fireEvent.click(defaults.getByRole('radio', { name: 'Whole PR' }));
      await waitFor(() => expect(localStorage.getItem('lotsen.defaultViewMode')).toBe('wholePr'));
      expect(pressed('Stop files')).toBe(true);
      fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Settings' })).toBeNull());

      await chooseTour(otherLabel);
      await waitFor(() => expect(pressed('Whole PR')).toBe(true));
      chooseMode('Step through');
      await chooseTour(prLabel);
      await waitFor(() => expect(pressed('Whole PR')).toBe(true));
   });

   it('keeps the mode when the reviewer chooses the open tour again', async () => {
      const { app } = await started();
      await openStopIn(app, 'Step through');

      await chooseTour(prLabel);

      await waitFor(() => expect(screen.queryByRole('navigation', { name: 'Tours' })).toBeNull());
      expect(pressed('Step through')).toBe(true);
      expect(stopPanel().getByRole('heading', { name: 'The change at a glance' })).toBeTruthy();
   });

   it('opens a tour in the default before its plan and changed files load, and keeps a mode chosen while they load', async () => {
      localStorage.setItem('lotsen.defaultViewMode', 'stopFiles');
      const { app, transport } = await started(twoTours);
      await openPinnedTour(app);
      chooseMode('Step through');
      const releasePlan = transport.hold('read_state');
      const releaseFiles = transport.hold('list_changed_files');

      await chooseTour(otherLabel);
      await waitFor(() => expect(pressed('Stop files')).toBe(true));
      chooseMode('Step through');
      releasePlan();
      releaseFiles();

      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
      expect(pressed('Step through')).toBe(true);
   });

   it('starts in the whole pull request when browser storage throws', async () => {
      blockLocalStorage();
      const { app } = await started();
      await openPinnedTour(app);
      expect(pressed('Whole PR')).toBe(true);

      chooseMode('Step through');

      await waitFor(() => expect(pressed('Step through')).toBe(true));
   });

   it('opens the next tour in the default that the reviewer chose while browser storage throws', async () => {
      blockLocalStorage();
      const { app } = await started(twoTours);
      await openPinnedTour(app);

      fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
      const settings = within(await screen.findByRole('dialog', { name: 'Settings' }));
      fireEvent.click(within(settings.getByRole('radiogroup', { name: 'Default view mode' })).getByRole('radio', { name: 'Step through' }));
      fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Settings' })).toBeNull());
      expect(pressed('Whole PR')).toBe(true);

      await chooseTour(otherLabel);

      await waitFor(() => expect(pressed('Step through')).toBe(true));
   });
});

describe('the open stop across plans', () => {
   it('opens a passage into a collapsed generated file expanded at the passage, and collapses the file again when the reviewer opens it from the list', async () => {
      const { world, seed, app } = await started();
      const claude = await attachClaude(world, seed.tourId);
      const titles = ['The change at a glance', 'Cart totals', 'Rounding', 'The old form'];
      const kinds = ['overview', 'inspect', 'risk', 'question'];
      await keepStops(
         claude,
         seed.tourId,
         seed.stopIds.map((id, index) => ({ id, title: titles[index]!, kind: kinds[index]! })),
         [{ path: 'src/pay.ts', mark: 'generated', reason: 'The gateway SDK writes it.' }],
      );
      await openPinnedTour(app);
      fireEvent.click(tab('Files'));
      fireEvent.click((await filesPanel()).getByText('pay.ts'));
      expect(await within(screen.getByRole('main', { name: 'Diff' })).findByRole('button', { name: 'Show the diff' })).toBeTruthy();
      fireEvent.click(tab('Plan'));

      fireEvent.click(planStop('Cart totals'));

      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(within(screen.getByRole('main', { name: 'Diff' })).queryByRole('button', { name: 'Show the diff' })).toBeNull();

      fireEvent.click(tab('Files'));
      fireEvent.click((await filesPanel()).getByText('cart.ts'));
      fireEvent.click((await filesPanel()).getByText('pay.ts'));

      expect(await within(screen.getByRole('main', { name: 'Diff' })).findByRole('button', { name: 'Show the diff' })).toBeTruthy();
   });

   it('keeps the open stop through a planSet that keeps it, with its new title, closes it when a planSet drops it, and keeps it closed when a new stop of its title comes', async () => {
      const { world, seed, app } = await started();
      const claude = await attachClaude(world, seed.tourId);
      const [glanceId, totalsId] = seed.stopIds;
      await openPinnedTour(app);
      fireEvent.click(planStop('Cart totals'));
      await stopPanel().findByText('Totals round to cents.');

      await keepStops(claude, seed.tourId, [
         { id: glanceId!, title: 'The change at a glance', kind: 'overview' },
         { id: totalsId!, title: 'How totals round', kind: 'inspect' },
      ]);

      expect(await stopPanel().findByRole('heading', { name: 'How totals round' })).toBeTruthy();
      expect(stopNavigation().getByText('Stop 2 of 2')).toBeTruthy();
      expect(stopPanel().getByText('Totals round to cents.')).toBeTruthy();
      expect(planStop('How totals round').getAttribute('aria-current')).toBe('step');

      await keepStops(claude, seed.tourId, [{ id: glanceId!, title: 'The change at a glance', kind: 'overview' }]);

      await waitFor(() => expect(stopPanel().queryByText(/Open a stop from the plan/)).not.toBeNull());

      await claude.call('set_plan', {
         tourId: seed.tourId,
         chapters: [{ id: 'c1', title: 'Chapter' }],
         stops: [
            { id: glanceId, title: 'The change at a glance', kind: 'overview', chapter: 'c1' },
            { title: 'Cart totals', kind: 'inspect', chapter: 'c1' },
         ],
      });
      await planPanel().findByRole('button', { name: /Cart totals/ });
      expect(stopPanel().queryByText(/Open a stop from the plan/)).not.toBeNull();
   });
});
