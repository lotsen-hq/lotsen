import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { backend, prUrl, seedTour, startApp } from '../harness';
import { hoverTooltip } from '../tooltip';
import { openTourList, tourButton } from '../tourPicker';
import { filesPanel, resetPageEachTest, worldOf } from './appPanels';

resetPageEachTest();

// Paneforge saves a layout 100 ms after it changes, so a page of one test could write its layout after the next test cleared the storage.
afterEach(() => new Promise((resolve) => setTimeout(resolve, 150)));

// Paneforge sizes each pane in percent of its group through the pane's flex-grow.
const sizes = (...ids: string[]) => ids.map((id) => Number(document.getElementById(id)!.style.flexGrow));
const columns = () => sizes('left-column', 'center-column', 'right-column');
const rightPanels = () => sizes('stop-panel', 'threads-panel');
const columnToggle = (name: 'Files and plan' | 'Stop and threads') => within(screen.getByRole('group', { name: 'Columns' })).getByRole('button', { name });
const closeButtons = () => screen.queryAllByRole('button', { name: /^Close/ });
/** Returns the names of the buttons in the header of the left column's tabs or of a right-column panel, in document order. */
function headerButtons(region: 'Tour' | 'Stop' | 'Threads'): string[] {
   const section = screen.getByRole('region', { name: region });
   const header = region === 'Tour' ? section.querySelector('[role="tablist"]')!.parentElement! : section.firstElementChild!;
   return within(header as HTMLElement)
      .getAllByRole('button')
      .map((button) => button.getAttribute('aria-label') ?? '');
}

// Two pinned tours of the same pull request, so either one has the cart's changed file.
const twoTours = backend().template('panelLayout-two-tours', async (world) => {
   await seedTour(world, { bound: false });
   const claude = await world.claude();
   await claude.call('create_tour', { prUrl, familiarity: 'some' });
   await world.idle();
   await claude.drop();
});

async function startShell(): Promise<{ world: World; app: LotsenApp }> {
   const world = await backend().world();
   const { app } = await startApp(world);
   return { world, app };
}

/** Starts the app again on the world, as the next load of the page does. */
async function reload(world: World, app: LotsenApp): Promise<LotsenApp> {
   cleanup();
   app.stop();
   return (await startApp(world)).app;
}

describe('the panel layout', () => {
   it('restores a splitter position from the browser on the next load', async () => {
      const { world, app } = await startShell();
      await waitFor(() => expect(columns()).toEqual([22, 50, 28]));

      fireEvent.keyDown(screen.getByRole('separator', { name: 'Resize the right column' }), { key: 'ArrowLeft' });
      await waitFor(() => expect(columns()).toEqual([22, 40, 38]));
      await waitFor(() => expect(localStorage.getItem('paneforge:lotsen.columns')).toContain('[22,40,38]'));
      await reload(world, app);

      await waitFor(() => expect(columns()).toEqual([22, 40, 38]));
   });

   it('keeps a folded column folded on the next load', async () => {
      const { world, app } = await startShell();
      fireEvent.click(columnToggle('Files and plan'));
      await waitFor(() => expect(localStorage.getItem('paneforge:lotsen.columns')).toContain('[0,72,28]'));
      await reload(world, app);

      await waitFor(() => expect(columns()).toEqual([0, 72, 28]));
      await waitFor(() => expect(columnToggle('Files and plan').getAttribute('aria-pressed')).toBe('false'));
      fireEvent.click(columnToggle('Files and plan'));
      await waitFor(() => expect(columns()).toEqual([22, 50, 28]));
   });

   it('gives a collapsed panel space to its neighbor in the same column, and takes it back when expanded', async () => {
      await startShell();
      await waitFor(() => expect(rightPanels()).toEqual([60, 40]));

      fireEvent.click(screen.getByRole('button', { name: 'Collapse the stop' }));

      await waitFor(() => expect(rightPanels()).toEqual([0, 100]));
      expect(columns()).toEqual([22, 50, 28]);
      expect(screen.queryByRole('region', { name: 'Stop' })).toBeNull();
      expect(screen.getByRole('region', { name: 'Threads' })).toBeTruthy();

      fireEvent.click(await screen.findByRole('button', { name: 'Expand the stop' }));

      await waitFor(() => expect(rightPanels()).toEqual([60, 40]));
      expect(screen.getByRole('region', { name: 'Stop' })).toBeTruthy();
   });

   it('folds a side column from the top bar, which gives its space to the diff, and unfolds it', async () => {
      await startShell();
      await waitFor(() => expect(columns()).toEqual([22, 50, 28]));
      const toggle = columnToggle('Files and plan');
      expect(toggle.getAttribute('aria-pressed')).toBe('true');
      expect(toggle.querySelector('svg.lucide-panel-left')).toBeTruthy();

      fireEvent.click(toggle);

      await waitFor(() => expect(columns()).toEqual([0, 72, 28]));
      expect(toggle.getAttribute('aria-pressed')).toBe('false');
      await waitFor(() => expect(toggle.querySelector('svg.lucide-panel-left-dashed')).toBeTruthy());

      fireEvent.click(toggle);

      await waitFor(() => expect(columns()).toEqual([22, 50, 28]));
      expect(toggle.getAttribute('aria-pressed')).toBe('true');
   });

   it('folds the right column from the top bar and shows the folded state in its icon', async () => {
      await startShell();
      await waitFor(() => expect(columns()).toEqual([22, 50, 28]));
      const toggle = columnToggle('Stop and threads');
      expect(toggle.getAttribute('aria-pressed')).toBe('true');
      expect(toggle.querySelector('svg.lucide-panel-right')).toBeTruthy();

      fireEvent.click(toggle);

      await waitFor(() => expect(columns()).toEqual([22, 78, 0]));
      expect(toggle.getAttribute('aria-pressed')).toBe('false');
      await waitFor(() => expect(toggle.querySelector('svg.lucide-panel-right-dashed')).toBeTruthy());
   });

   it('follows a column folded and unfolded with Enter on its splitter, so the top-bar toggle still works', async () => {
      await startShell();
      await waitFor(() => expect(columns()).toEqual([22, 50, 28]));
      const toggle = columnToggle('Files and plan');
      const splitter = screen.getByRole('separator', { name: 'Resize the left column' });

      fireEvent.keyDown(splitter, { key: 'Enter' });

      await waitFor(() => expect(columns()).toEqual([0, 72, 28]));
      await waitFor(() => expect(toggle.getAttribute('aria-pressed')).toBe('false'));

      fireEvent.click(toggle);

      await waitFor(() => expect(columns()).toEqual([12, 60, 28]));
      expect(toggle.getAttribute('aria-pressed')).toBe('true');

      fireEvent.keyDown(splitter, { key: 'Enter' });
      await waitFor(() => expect(toggle.getAttribute('aria-pressed')).toBe('false'));
      fireEvent.keyDown(splitter, { key: 'Enter' });
      await waitFor(() => expect(toggle.getAttribute('aria-pressed')).toBe('true'));
   });

   it('shows the header of a panel collapsed with Enter on its splitter, from which it expands again', async () => {
      await startShell();
      await waitFor(() => expect(rightPanels()).toEqual([60, 40]));

      fireEvent.keyDown(screen.getByRole('separator', { name: 'Resize the stop and the threads' }), { key: 'Enter' });

      await waitFor(() => expect(rightPanels()).toEqual([0, 100]));
      expect(screen.queryByRole('region', { name: 'Stop' })).toBeNull();
      fireEvent.click(await screen.findByRole('button', { name: 'Expand the stop' }));

      // Paneforge records no size to return to when Enter folds a pane, so the pane expands to its minimum size.
      await waitFor(() => expect(rightPanels()).toEqual([15, 85]));
      expect(screen.getByRole('region', { name: 'Stop' })).toBeTruthy();
   });

   it('follows a panel collapsed by resizing it below its minimum size', async () => {
      await startShell();
      await waitFor(() => expect(rightPanels()).toEqual([60, 40]));
      const splitter = screen.getByRole('separator', { name: 'Resize the stop and the threads' });

      for (let step = 0; step < 6; step++) {
         fireEvent.keyDown(splitter, { key: 'ArrowUp' });
      }

      await waitFor(() => expect(rightPanels()).toEqual([0, 100]));
      expect(await screen.findByRole('button', { name: 'Expand the stop' })).toBeTruthy();
   });

   it('keeps the restore button of the maximized code when another tour closes the open file', async () => {
      const { world } = await worldOf(twoTours);
      const { app } = await startApp(world);
      const tourButtons = async () => {
         const list = await openTourList();
         return waitFor(() => {
            const found = list.getAllByRole('button', tourButton);
            expect(found).toHaveLength(2);
            return found;
         });
      };
      const buttons = await tourButtons();
      fireEvent.click(buttons[0]!);
      await waitFor(() => expect(app.selectedTourId).not.toBeNull());
      const first = app.selectedTourId;
      fireEvent.click(await (await filesPanel()).findByText('cart.ts'));
      await waitFor(() => expect(app.selectedFilePath).toBe('src/cart.ts'));
      fireEvent.click(await screen.findByRole('button', { name: 'Maximize the code' }));
      await waitFor(() => expect(screen.queryByRole('region', { name: 'Tour' })).toBeNull());

      const other = (await tourButtons()).find((button) => button.getAttribute('aria-current') !== 'true')!;
      fireEvent.click(other);

      await waitFor(() => expect(app.selectedTourId).not.toBe(first));
      await waitFor(() => expect(app.selectedFilePath).toBeNull());
      fireEvent.click(await screen.findByRole('button', { name: 'Restore the code' }));
      await waitFor(() => expect(screen.getByRole('region', { name: 'Tour' })).toBeTruthy());
   });

   it('ends the header of the left column and of the stop with a close button after Maximize, and gives the threads and the code none', async () => {
      await startShell();

      expect(headerButtons('Tour').slice(-2)).toEqual(['Maximize the files and the plan', 'Close the files and the plan']);
      expect(headerButtons('Stop').slice(-2)).toEqual(['Maximize the stop', 'Close the stop and the threads']);
      expect(headerButtons('Threads')).toEqual(['Collapse the threads', 'Maximize the threads']);
      expect(closeButtons().map((button) => button.getAttribute('aria-label'))).toEqual(['Close the files and the plan', 'Close the stop and the threads']);
      expect(await hoverTooltip(screen.getByRole('button', { name: 'Close the stop and the threads' }))).toBe('Close the stop and the threads');
   });

   it.each([
      { button: 'Close the files and the plan', toggle: 'Files and plan', closed: [0, 72, 28] },
      { button: 'Close the stop and the threads', toggle: 'Stop and threads', closed: [22, 78, 0] },
   ] as const)('hides its column with "$button", and moves focus to the top-bar toggle that shows it again', async ({ button, toggle, closed }) => {
      await startShell();
      await waitFor(() => expect(columns()).toEqual([22, 50, 28]));

      fireEvent.click(screen.getByRole('button', { name: button }));

      await waitFor(() => expect(columns()).toEqual(closed));
      await waitFor(() => expect(columnToggle(toggle).getAttribute('aria-pressed')).toBe('false'));
      expect(document.activeElement).toBe(columnToggle(toggle));

      fireEvent.click(columnToggle(toggle));

      await waitFor(() => expect(columns()).toEqual([22, 50, 28]));
   });

   it.each([
      { maximize: 'Maximize the files and the plan', region: 'Tour', restore: 'Restore the files and the plan' },
      { maximize: 'Maximize the stop', region: 'Stop', restore: 'Restore the stop' },
   ] as const)('offers only Restore in the header of a maximized panel, after "$maximize"', async ({ maximize, region, restore }) => {
      await startShell();

      fireEvent.click(screen.getByRole('button', { name: maximize }));

      await waitFor(() => expect(screen.queryByRole('main')).toBeNull());
      expect(headerButtons(region)).toEqual([restore]);
      expect(closeButtons()).toEqual([]);
   });

   it('offers to maximize the code before a file is open, and to restore it', async () => {
      await startShell();

      fireEvent.click(screen.getByRole('button', { name: 'Maximize the code' }));

      await waitFor(() => expect(screen.queryByRole('region', { name: 'Tour' })).toBeNull());
      fireEvent.click(screen.getByRole('button', { name: 'Restore the code' }));
      await waitFor(() => expect(screen.getByRole('region', { name: 'Tour' })).toBeTruthy());
   });

   it('maximizes a panel over the others and restores them', async () => {
      await startShell();
      const tour = screen.getByRole('region', { name: 'Tour' });

      fireEvent.click(screen.getByRole('button', { name: 'Maximize the threads' }));

      await waitFor(() => expect(screen.queryByRole('region', { name: 'Tour' })).toBeNull());
      expect(screen.queryByRole('region', { name: 'Stop' })).toBeNull();
      expect(screen.queryAllByRole('separator')).toEqual([]);
      expect(screen.getByRole('region', { name: 'Threads' })).toBeTruthy();

      fireEvent.click(screen.getByRole('button', { name: 'Restore the threads' }));

      await waitFor(() => expect(screen.getByRole('region', { name: 'Tour' })).toBe(tour));
      expect(screen.getByRole('region', { name: 'Stop' })).toBeTruthy();
      expect(screen.getAllByRole('separator')).toHaveLength(3);
   });
});
