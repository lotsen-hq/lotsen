import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, onTestFinished, vi } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { backend, seedTour, startApp } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { filesPanel, resetPageEachTest, worldOf } from './appPanels';
import { inMenu, itemName, openMenu } from './threadFilter';

resetPageEachTest();

/** Returns the tabs of each group that shows, in document order, the active tab of each marked with a star. */
function groups(): string[][] {
   return screen.getAllByRole('tablist').map((list) =>
      within(list)
         .getAllByRole('tab')
         .map((tab) => `${tab.getAttribute('aria-label')}${tab.getAttribute('aria-selected') === 'true' ? '*' : ''}`),
   );
}

// Without a pinned tour, Plan is the active tab of its group.
const defaultGroups = [['Files', 'Plan*'], ['Code*'], ['Stop*'], ['Threads*']];
// A pinned tour opens on the Files tab.
const pinnedGroups = [['Files*', 'Plan'], ['Code*'], ['Stop*'], ['Threads*']];
const panelsMenu = () => screen.getByRole('button', { name: 'Panels' });

/** Opens the Panels menu and returns each item's name with whether it holds the check. */
async function panelItems(): Promise<{ name: string; checked: boolean }[]> {
   const menu = await openMenu(panelsMenu());
   return within(menu)
      .getAllByRole('menuitemcheckbox', inMenu)
      .map((item) => ({ name: itemName(item), checked: item.getAttribute('aria-checked') === 'true' }));
}

/** Toggles a panel's item in the Panels menu, which stays open. */
async function togglePanel(name: string): Promise<void> {
   const menu = await openMenu(panelsMenu());
   await fireEvent.click(within(menu).getByRole('menuitemcheckbox', { ...inMenu, name }));
}

// Two pinned tours of the same pull request, so either one has the cart's changed file.
const twoTours = backend().template('panelLayout-two-tours', async (world) => {
   await seedTour(world, { bound: false });
   const claude = await world.claude();
   await claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/42', familiarity: 'some' });
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

const savedKey = 'lotsen.workspaceLayout';

describe('the saved layout', () => {
   it('keeps a panel that shared its group hidden on the next load, and shows it back in that group', async () => {
      const { world, app } = await startShell();
      await togglePanel('Files');
      await waitFor(() => expect(groups()).toEqual([['Plan*'], ['Code*'], ['Stop*'], ['Threads*']]));

      await reload(world, app);

      await waitFor(() => expect(groups()).toEqual([['Plan*'], ['Code*'], ['Stop*'], ['Threads*']]));
      expect((await panelItems()).find((item) => item.name === 'Files')?.checked).toBe(false);
      await togglePanel('Files');
      await waitFor(() => expect(groups()).toEqual([['Files*', 'Plan'], ['Code*'], ['Stop*'], ['Threads*']]));
   });

   it('keeps a panel with a group of its own hidden on the next load, and shows it again', async () => {
      const { world, app } = await startShell();
      fireEvent.click(screen.getByRole('button', { name: 'Hide the stop' }));
      await waitFor(() => expect(groups()).toEqual([['Files', 'Plan*'], ['Code*'], ['Threads*']]));

      await reload(world, app);

      await waitFor(() => expect(groups()).toEqual([['Files', 'Plan*'], ['Code*'], ['Threads*']]));
      await togglePanel('Stop');
      await waitFor(() => expect(groups()).toEqual(defaultGroups));
   });

   it('loads every group in its place after a load while a group was maximized', async () => {
      const { world, app } = await startShell();
      fireEvent.click(screen.getByRole('button', { name: 'Maximize the threads' }));
      await waitFor(() => expect(groups()).toEqual([['Threads*']]));

      await reload(world, app);

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
   });

   it.each([
      { saved: 'not a layout', why: 'unreadable' },
      { saved: '{"version":1,"dockview":{"grid":{},"panels":{}},"hidden":[]}', why: 'without the workspace panels' },
   ])('gives the Default layout for a saved layout that is $why', async ({ saved }) => {
      localStorage.setItem(savedKey, saved);

      await startShell();

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
   });

   it('gives the Default layout for a saved layout of another version', async () => {
      const { world, app } = await startShell();
      fireEvent.click(screen.getByRole('button', { name: 'Hide the stop' }));
      await waitFor(() => expect(JSON.parse(localStorage.getItem(savedKey)!).hidden).toEqual([{ id: 'stop', home: null }]));
      localStorage.setItem(savedKey, JSON.stringify({ ...JSON.parse(localStorage.getItem(savedKey)!), version: 2 }));

      await reload(world, app);

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
   });

   it('gives the Default layout without a saved one, and keeps working while browser storage throws', async () => {
      // The page's credential stays in session storage, which keeps working.
      const { getItem, setItem } = Storage.prototype;
      const refuse = (storage: Storage) => {
         if (storage === localStorage) {
            throw new DOMException('The storage is not available.', 'SecurityError');
         }
      };
      const reads = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key) {
         refuse(this);
         return getItem.call(this, key);
      });
      const writes = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, key, value) {
         refuse(this);
         setItem.call(this, key, value);
      });
      onTestFinished(() => {
         reads.mockRestore();
         writes.mockRestore();
      });

      await startShell();

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
      fireEvent.click(screen.getByRole('button', { name: 'Hide the stop' }));
      await waitFor(() => expect(groups()).toEqual([['Files', 'Plan*'], ['Code*'], ['Threads*']]));
   });
});

describe('the workspace', () => {
   it('opens with Files and Plan as tabs of one group, and the code, the stop and the threads in a group each', async () => {
      await startShell();

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
      expect(screen.getAllByRole('region').map((region) => region.getAttribute('aria-label'))).toEqual(expect.arrayContaining(['Plan', 'Code', 'Stop', 'Threads']));
   });

   it('opens a tab on a click, as a screen reader sends it without a pointer press', async () => {
      await startShell();

      fireEvent.click(screen.getByRole('tab', { name: 'Files' }));

      await waitFor(() => expect(groups()[0]).toEqual(['Files*', 'Plan']));
   });

   it('keeps a panel when Delete or Backspace is pressed on its tab', async () => {
      await startShell();
      const tab = screen.getByRole('tab', { name: 'Files' });

      fireEvent.keyDown(tab, { key: 'Delete' });
      fireEvent.keyDown(tab, { key: 'Backspace' });

      expect(groups()).toEqual(defaultGroups);
   });

   it('lists each panel in the Panels menu, checked while it shows', async () => {
      await startShell();

      expect(await panelItems()).toEqual(['Files', 'Plan', 'Code', 'Stop', 'Threads'].map((name) => ({ name, checked: true })));
   });

   it('hides a panel from the Panels menu and shows it again with the same content', async () => {
      await startShell();
      const stop = screen.getByRole('region', { name: 'Stop' }).querySelector('[role="tabpanel"] > *');

      await togglePanel('Stop');

      await waitFor(() => expect(groups()).toEqual([['Files', 'Plan*'], ['Code*'], ['Threads*']]));
      expect(screen.queryByRole('region', { name: 'Stop' })).toBeNull();
      expect((await panelItems()).find((item) => item.name === 'Stop')?.checked).toBe(false);

      await togglePanel('Stop');

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
      expect(screen.getByRole('region', { name: 'Stop' }).querySelector('[role="tabpanel"] > *')).toBe(stop);
   });

   it('shows a hidden panel back as a tab of the group it shared', async () => {
      await startShell();

      await togglePanel('Files');
      await waitFor(() => expect(groups()).toEqual([['Plan*'], ['Code*'], ['Stop*'], ['Threads*']]));

      await togglePanel('Files');

      await waitFor(() => expect(groups()[0]).toEqual(['Files*', 'Plan']));
      expect(groups()).toHaveLength(4);
   });

   it('hides the active panel of a group with its Hide button, and moves focus to the Panels menu', async () => {
      await startShell();

      fireEvent.click(screen.getByRole('button', { name: 'Hide the threads' }));

      await waitFor(() => expect(groups()).toEqual([['Files', 'Plan*'], ['Code*'], ['Stop*']]));
      expect(document.activeElement).toBe(panelsMenu());
   });

   it('maximizes a group over the others, offers only Restore there, and restores them', async () => {
      await startShell();

      fireEvent.click(screen.getByRole('button', { name: 'Maximize the threads' }));

      await waitFor(() => expect(groups()).toEqual([['Threads*']]));
      expect(screen.queryByRole('button', { name: 'Hide the threads' })).toBeNull();

      fireEvent.click(screen.getByRole('button', { name: 'Restore the threads' }));

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
      expect(screen.getByRole('button', { name: 'Hide the threads' })).toBeTruthy();
   });

   it('names the active panel of a group in its buttons', async () => {
      await startShell();
      expect(screen.getByRole('button', { name: 'Maximize the plan' })).toBeTruthy();

      fireEvent.click(screen.getByRole('tab', { name: 'Files' }));

      expect(await screen.findByRole('button', { name: 'Maximize the files' })).toBeTruthy();
      expect(screen.queryByRole('button', { name: 'Maximize the plan' })).toBeNull();
   });

   it('restores a maximized group to hide a panel from the Panels menu', async () => {
      await startShell();
      fireEvent.click(screen.getByRole('button', { name: 'Maximize the code' }));
      await waitFor(() => expect(groups()).toEqual([['Code*']]));

      await togglePanel('Threads');

      await waitFor(() => expect(groups()).toEqual([['Files', 'Plan*'], ['Code*'], ['Stop*']]));
   });

   it('shows the hidden Threads panel when the reviewer asks about a file, and restores another maximized group', async () => {
      const { world } = await worldOf(twoTours);
      const { app } = await startApp(world);
      const list = await openTourList();
      fireEvent.click((await waitFor(() => list.getAllByRole('button', tourButton)))[0]!);
      await waitFor(() => expect(app.selectedTourId).not.toBeNull());
      fireEvent.click(await (await filesPanel()).findByText('cart.ts'));
      await waitFor(() => expect(app.selectedFilePath).toBe('src/cart.ts'));
      await togglePanel('Threads');
      await waitFor(() => expect(screen.queryByRole('region', { name: 'Threads' })).toBeNull());
      fireEvent.click(screen.getByRole('button', { name: 'Maximize the code' }));
      await waitFor(() => expect(groups()).toEqual([['Code*']]));

      fireEvent.click(screen.getByRole('button', { name: 'Ask Claude about src/cart.ts' }));

      await waitFor(() => expect(groups()).toEqual(pinnedGroups));
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
      await waitFor(() => expect(groups()).toEqual([['Code*']]));

      const other = (await tourButtons()).find((button) => button.getAttribute('aria-current') !== 'true')!;
      fireEvent.click(other);

      await waitFor(() => expect(app.selectedTourId).not.toBe(first));
      await waitFor(() => expect(app.selectedFilePath).toBeNull());
      fireEvent.click(await screen.findByRole('button', { name: 'Restore the code' }));
      await waitFor(() => expect(groups()).toEqual(pinnedGroups));
   });
});
