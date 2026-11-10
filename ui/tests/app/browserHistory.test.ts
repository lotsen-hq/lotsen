import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { tourSwitcherName } from '../tourPicker';
import { planPanel, resetPageEachTest, tab } from './appPanels';
import {
   chooseTour,
   historyTemplate,
   historyWorld,
   load,
   loadShell,
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
   watchScrollsEachTest,
} from './historySupport';

resetPageEachTest();
watchScrollsEachTest();

const tours = historyTemplate('history-tours');

describe('the browser history', () => {
   it('adds one entry for each move, a tour, a stop, a ref: link and a file from Files, and back and forward restore each side of it', async () => {
      const { world, seed, totals, tourEntry, stopEntry, referenceEntry, fileEntry } = await historyWorld(tours);
      const transport = world.transport();
      const app = await loadShell(world, transport);
      const first = history.length;

      await chooseTour(/#42/);
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      expect(location.hash).toBe(tourEntry);
      fireEvent.click(tab('Plan'));
      fireEvent.click(await planPanel().findByRole('button', { name: /Cart totals/ }));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      expect(location.hash).toBe(stopEntry);
      fireEvent.click(stopPanel().getByRole('link', { name: 'the product type' }));
      await scrolledTo('File', 'src/catalog.ts', 'head', 3);
      expect(location.hash).toBe(referenceEntry);
      await openFromFiles('cart.ts, Modified');
      await waitFor(() => expect(app.selectedFilePath).toBe('src/cart.ts'));
      expect(location.hash).toBe(fileEntry);
      expect(history.length).toBe(first + 4);

      await traverse(() => history.back());
      expect(await scrolledTo('File', 'src/catalog.ts', 'head', 3)).toBe('catalog line 3');
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'head:src/catalog.ts', line: 3 });
      await traverse(() => history.back());
      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/pay.ts', line: 20 });
      await traverse(() => history.back());
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: null, file: null, line: null });
      await traverse(() => history.back());
      expect(shown(app)).toEqual({ tour: null, stop: null, file: null, line: null });
      expect(location.hash).toBe('');

      await traverse(() => history.forward());
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: null, file: null, line: null });
      await traverse(() => history.go(3));
      await waitFor(() => expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/cart.ts', line: null }));
      expect(location.hash).toBe(fileEntry);
      expect(history.length).toBe(first + 4);
   });

   it('adds no entry for a step through the passages, a passage opened from the stop or a change of the view mode, and keeps the entry on the open passage', async () => {
      const { world, overview, entry } = await historyWorld(tours);
      await openPinnedTour(world, world.transport());
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo('Diff', 'src/cart.ts', 'head', 5);
      const length = history.length;

      fireEvent.click(modeSwitch().getByRole('button', { name: 'Step through' }));
      fireEvent.click(within(screen.getByRole('navigation', { name: 'Passages' })).getByRole('button', { name: 'Next passage' }));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 30);
      await waitFor(() => expect(location.hash).toBe(entry({ stop: overview, side: 'head', file: 'src/pay.ts', lines: '30-31' })));
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /src\/catalog\.ts/ }));
      await scrolledTo('File', 'src/catalog.ts', 'head', 3);
      await waitFor(() => expect(location.hash).toBe(entry({ stop: overview, side: 'head', file: 'src/catalog.ts', lines: '3-4' })));

      expect(history.length).toBe(length);
   });

   it('restores the tour, the stop and the file at its lines from the fragment on a reload, with the passage they name focused', async () => {
      const { world, totals, entry } = await historyWorld(tours);
      const transport = world.transport();
      await openPinnedTour(world, transport);
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /src\/cart\.ts/ }));
      const cartEntry = entry({ stop: totals, side: 'head', file: 'src/cart.ts', lines: '5-7' });
      await waitFor(() => expect(location.hash).toBe(cartEntry));
      const length = history.length;

      const app = reload(world, transport);

      expect(await scrolledTo('Diff', 'src/cart.ts', 'head', 5)).toBe('cart line 5');
      expect(await stopPanel().findByRole('heading', { name: 'Cart totals' })).toBeTruthy();
      expect(app.focusedPassage).toMatchObject({ path: 'src/cart.ts', startLine: 5, role: 'related' });
      expect(location.hash).toBe(cartEntry);
      expect(history.length).toBe(length);
   });

   it('restores a file opened from Files on a reload, and waits for the changed files to tell whether it opens a diff', async () => {
      const { world, seed, totals, fileEntry } = await historyWorld(tours);
      const transport = world.transport();
      const release = transport.hold('list_changed_files');
      const app = load(world, transport, `/${fileEntry}`);
      await screen.findByRole('button', { name: tourSwitcherName });
      await waitFor(() => expect(app.selectedStopId).toBe(totals));
      expect(app.selectedText).toBeNull();

      release();

      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/cart.ts', line: null });
      expect(location.hash).toBe(fileEntry);
   });

   it('signs in from a link with a code and restores the navigation beside it, which stays in the address without the code', async () => {
      const { world, seed, totals, stopEntry } = await historyWorld(tours);
      const code = await world.uiCode();

      const app = load(world, world.transport(), `/#code=${code}&${stopEntry.slice(1)}`, null, { signedIn: false });

      expect(location.hash).toBe(stopEntry);
      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(sessionStorage.getItem('lotsen.uiCredential')).toBe(world.uiCredential);
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/pay.ts', line: 20 });
      expect(location.href).not.toContain(code);
   });

   it('leaves an address with a code to the page load that exchanges it, and adds no navigation to it', async () => {
      const { world, seed, totals } = await historyWorld(tours);
      const app = await openPinnedTour(world, world.transport());
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);

      history.pushState(null, '', '/#code=pasted-code');
      window.dispatchEvent(new PopStateEvent('popstate'));
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(location.hash).toBe('#code=pasted-code');
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/pay.ts', line: 20 });
   });

   it('shows the tour list for a fragment that names a tour the list lacks, and keeps no trace of it in the address', async () => {
      const { world, totals } = await historyWorld(tours);
      const transport = world.transport();

      const app = load(world, transport, `/#tour=gone&stop=${totals}`);
      await screen.findByRole('button', { name: tourSwitcherName });

      await waitFor(() => expect(location.hash).toBe(''));
      expect(app.selectedTourId).toBeNull();
      expect(transport.requests.some((request) => request.input?.tourId === 'gone')).toBe(false);
      expect(screen.getByText('Choose a tour to start.')).toBeTruthy();
   });

   it('opens no stop for a fragment that names a stop the plan lacks, and still opens the file', async () => {
      const { world, seed, entry } = await historyWorld(tours);

      const app = load(world, world.transport(), `/${entry({ stop: 'gone', side: 'head', file: 'src/cart.ts' })}`);

      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: null, file: 'src/cart.ts', line: null });
      await waitFor(() => expect(location.hash).toBe(entry({ side: 'head', file: 'src/cart.ts' })));
   });

   it('opens the stop’s primary passage in place of a file that the side does not have', async () => {
      const { world, totals, entry, stopEntry } = await historyWorld(tours);

      load(world, world.transport(), `/${entry({ stop: totals, side: 'head', file: 'src/gone.ts' })}`);

      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      await waitFor(() => expect(location.hash).toBe(stopEntry));
   });

   it('opens a tour that back returns to in the default view mode, and keeps the mode within one tour', async () => {
      localStorage.setItem('lotsen.defaultViewMode', 'stepThrough');
      const pressed = (name: string) => modeSwitch().getByRole('button', { name }).getAttribute('aria-pressed') === 'true';
      const { world, seed, totals } = await historyWorld(tours);
      const app = await openPinnedTour(world, world.transport());
      fireEvent.click(planStop('Cart totals'));
      await stopPanel().findByRole('heading', { name: 'Cart totals' });
      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await stopPanel().findByRole('heading', { name: 'Rounding' });
      fireEvent.click(modeSwitch().getByRole('button', { name: 'Whole PR' }));

      await traverse(() => history.back());
      expect(shown(app).stop).toBe(totals);
      expect(pressed('Whole PR')).toBe(true);

      await chooseTour(/#7/);
      await waitFor(() => expect(app.selectedTourId).toBe(seed.otherId));
      fireEvent.click(modeSwitch().getByRole('button', { name: 'Stop files' }));
      await traverse(() => history.back());

      await waitFor(() => expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/pay.ts', line: 20 }));
      expect(pressed('Step through')).toBe(true);
   });

   it('shows every region again when back returns to another tour', async () => {
      const { world, seed, totals } = await historyWorld(tours);
      const app = await openPinnedTour(world, world.transport());
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      await chooseTour(/#7/);
      await waitFor(() => expect(app.selectedTourId).toBe(seed.otherId));
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      fireEvent.click(tab('Plan'));
      fireEvent.click(await planPanel().findByRole('button', { name: /Cart totals/ }));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      const diff = screen.getByRole('main', { name: 'Diff' });
      fireEvent.click(within(diff).getByRole('button', { name: 'Collapse to passages' }));
      await waitFor(() => expect(diff.querySelectorAll('.lotsen-collapsed').length).toBeGreaterThan(0));

      await traverse(() => history.back());
      await traverse(() => history.back());

      await waitFor(() => expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/pay.ts', line: 20 }));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      expect(diff.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);
      expect(within(diff).getByRole('button', { name: 'Collapse to passages' }).getAttribute('aria-pressed')).toBe('false');
   });

   it('restores the tour that back returns to while another tour’s plan and changed files load', async () => {
      const { world, seed, totals, stopEntry } = await historyWorld(tours);
      const transport = world.transport();
      const app = await openPinnedTour(world, transport);
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      // The other tour's loads reach the server, and their answers wait until the page returns to the first tour.
      const releasePlan = transport.hold('read_state');
      const releaseFiles = transport.hold('list_changed_files');
      const planAnswered = transport.answered('read_state');
      const filesAnswered = transport.answered('list_changed_files');
      await chooseTour(/#7/);
      await waitFor(() => expect(app.selectedTourId).toBe(seed.otherId));
      await waitFor(() =>
         expect(transport.requests.filter((request) => request.input?.tourId === seed.otherId).map((request) => request.operation)).toEqual(
            expect.arrayContaining(['read_state', 'list_changed_files']),
         ),
      );

      await traverse(() => history.back());
      releasePlan();
      releaseFiles();
      await Promise.all([planAnswered, filesAnswered]);

      await waitFor(() => expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/pay.ts', line: 20 }));
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(app.selectedTourId).toBe(seed.tourId);
      expect(location.hash).toBe(stopEntry);
   });

   it('loads the plan and the changed files of a restored tour once', async () => {
      const { world, seed, stopEntry } = await historyWorld(tours);
      const transport = world.transport();
      const calls = (operation: string) => transport.requests.filter((request) => request.operation === operation && request.input?.tourId === seed.tourId);

      load(world, transport, `/${stopEntry}`);

      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(calls('read_state')).toHaveLength(1);
      expect(calls('list_changed_files')).toHaveLength(1);
   });

   it('keeps a waiting restore and adds no entry when the reviewer picks the tour that it opens', async () => {
      const { world, seed, totals, stopEntry } = await historyWorld(tours);
      const transport = world.transport();
      const release = transport.hold('read_state');
      const app = load(world, transport, `/${stopEntry}`);
      await waitFor(() => expect(app.selectedTourId).toBe(seed.tourId));
      const length = history.length;

      await chooseTour(/#42/);
      release();

      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(shown(app)).toEqual({ tour: seed.tourId, stop: totals, file: 'src/pay.ts', line: 20 });
      expect(location.hash).toBe(stopEntry);
      expect(history.length).toBe(length);
   });

   it('adds no entry when the reviewer opens the open stop again, and rewrites the entry to its primary passage', async () => {
      const { world, totals, entry, stopEntry } = await historyWorld(tours);
      await openPinnedTour(world, world.transport());
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo('Diff', 'src/pay.ts', 'head', 20);
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /src\/cart\.ts/ }));
      await waitFor(() => expect(location.hash).toBe(entry({ stop: totals, side: 'head', file: 'src/cart.ts', lines: '5-7' })));
      const length = history.length;

      fireEvent.click(planStop('Cart totals'));

      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      await waitFor(() => expect(location.hash).toBe(stopEntry));
      expect(history.length).toBe(length);
   });
});

it('closes the open stop when another tour is selected', async () => {
   const { world, seed } = await historyWorld(tours);
   const app = await openPinnedTour(world, world.transport());
   fireEvent.click(planStop('Cart totals'));
   await stopPanel().findByText('Totals round to cents.');

   await chooseTour(/#7/);

   await waitFor(() => expect(app.selectedTourId).toBe(seed.otherId));
   expect(stopPanel().queryByRole('heading', { name: 'Cart totals' })).toBeNull();
   expect(stopPanel().getByText(/Open a stop from the plan/)).toBeTruthy();
});
