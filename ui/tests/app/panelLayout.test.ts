import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, onTestFinished, vi } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { arrangementRecord } from '../../src/lib/arrangements';
import { layoutVersion } from '../../src/lib/layoutRecord';
import { backend, seedTour, settled, startApp } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { filesPanel, resetPageEachTest, worldOf } from './appPanels';
import { closeMenu, inMenu, itemName, openMenu } from './threadFilter';

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

const savedKey = 'lotsen.workspaceLayout';
const titles = { files: 'Files', plan: 'Plan', diff: 'Code', stop: 'Stop', threads: 'Threads' };

/** Waits until the server's preferences hold the layout that the page saved last, as they do once its write lands. */
async function layoutStored(world: World): Promise<void> {
   await waitFor(async () => expect((await world.reviewer.call('read_preferences', {})).preferences[savedKey]).toBe(localStorage.getItem(savedKey) ?? undefined));
}

/** Starts the app again on the world, as the next load of the page does, once the server holds the saved layout. */
async function reload(world: World, app: LotsenApp): Promise<LotsenApp> {
   await layoutStored(world);
   cleanup();
   app.stop();
   return (await startApp(world)).app;
}

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
      await layoutStored(world);
      const newer = JSON.stringify({ ...JSON.parse(localStorage.getItem(savedKey)!), version: 2 });
      await world.reviewer.call('write_preference', { key: savedKey, value: newer });
      await waitFor(() => expect(localStorage.getItem(savedKey)).toBe(newer));

      const loaded = await reload(world, app);

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
      await settled(loaded);
      expect((await world.reviewer.call('read_preferences', {})).preferences[savedKey]).toBe(newer);
   });

   it('saves no layout on a load without a saved one until the reviewer changes the layout, though a tour opens on Files and a file opens', async () => {
      const { world } = await worldOf(twoTours);
      const { app, transport } = await startApp(world);
      const layoutWrites = () => transport.requests.filter((request) => request.operation === 'write_preference' && request.input?.key === savedKey);
      const list = await openTourList();
      fireEvent.click((await waitFor(() => list.getAllByRole('button', tourButton)))[0]!);
      await waitFor(() => expect(groups()).toEqual(pinnedGroups));
      fireEvent.click(await (await filesPanel()).findByText('cart.ts'));
      await waitFor(() => expect(app.selectedFilePath).toBe('src/cart.ts'));
      await settled(app);

      expect(layoutWrites()).toEqual([]);
      expect(localStorage.getItem(savedKey)).toBeNull();
      fireEvent.click(screen.getByRole('button', { name: 'Hide the stop' }));
      await layoutStored(world);
      expect(JSON.parse(localStorage.getItem(savedKey)!).hidden).toEqual([{ id: 'stop', home: null }]);
   });

   it('loads the saved layout of the server without a write, over another one that browser storage holds', async () => {
      const world = await backend().world();
      const columns = JSON.stringify(arrangementRecord('columns', titles));
      await world.reviewer.call('write_preference', { key: savedKey, value: columns });
      localStorage.setItem(savedKey, JSON.stringify(arrangementRecord('explanationAbove', titles)));

      const { app, transport } = await startApp(world);

      await waitFor(() => expect(groups()).toEqual([['Files*'], ['Plan*'], ['Code*'], ['Stop*'], ['Threads*']]));
      await settled(app);
      expect(transport.requests.filter((request) => request.operation === 'write_preference')).toEqual([]);
      expect((await world.reviewer.call('read_preferences', {})).preferences[savedKey]).toBe(columns);
   });

   it('gives the server the layout that browser storage holds when the server holds none', async () => {
      const world = await backend().world();
      const columns = JSON.stringify(arrangementRecord('columns', titles));
      localStorage.setItem(savedKey, columns);

      await startApp(world);

      await waitFor(() => expect(groups()).toEqual([['Files*'], ['Plan*'], ['Code*'], ['Stop*'], ['Threads*']]));
      await waitFor(async () => expect((await world.reviewer.call('read_preferences', {})).preferences[savedKey]).toBe(columns));
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

/** Applies an arrangement, or Reset layout, from the Panels menu. */
async function arrange(name: string): Promise<void> {
   const menu = await openMenu(panelsMenu());
   await fireEvent.click(within(menu).getByRole('menuitem', { ...inMenu, name }));
}

describe('the arrangements', () => {
   it.each([
      { name: 'Columns', expected: [['Files*'], ['Plan*'], ['Code*'], ['Stop*'], ['Threads*']] },
      { name: 'Explanation above the diff', expected: [['Files', 'Plan*'], ['Stop*'], ['Code*'], ['Threads*']] },
      { name: 'Default', expected: defaultGroups },
   ])('lays out the panels as $name', async ({ name, expected }) => {
      await startShell();

      await arrange(name);

      await waitFor(() => expect(groups()).toEqual(expected));
   });

   it('shows every hidden panel and restores a maximized group on Reset layout', async () => {
      await startShell();
      await arrange('Columns');
      await togglePanel('Plan');
      fireEvent.click(screen.getByRole('button', { name: 'Maximize the code' }));
      await waitFor(() => expect(groups()).toEqual([['Code*']]));

      await arrange('Reset layout');

      await waitFor(() => expect(groups()).toEqual(defaultGroups));
      expect((await panelItems()).every((item) => item.checked)).toBe(true);
   });

   it('keeps an applied arrangement on the next load', async () => {
      const { world, app } = await startShell();
      await arrange('Explanation above the diff');
      await waitFor(() => expect(groups()).toHaveLength(4));

      await reload(world, app);

      await waitFor(() => expect(groups()).toEqual([['Files', 'Plan*'], ['Stop*'], ['Code*'], ['Threads*']]));
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

const columnsRecord = arrangementRecord('columns', titles);

/** Returns the stored value of a named layout. */
const layoutValue = (name: unknown, record: unknown) => JSON.stringify({ name, record });

/** Returns the server's named layouts, each key's value parsed. */
async function storedLayouts(world: World): Promise<Record<string, { name: string; record: unknown }>> {
   const { preferences } = await world.reviewer.call('read_preferences', {});
   return Object.fromEntries(
      Object.entries(preferences)
         .filter(([key]) => key.startsWith('workspaceLayouts/'))
         .map(([key, text]) => [key, JSON.parse(text!)]),
   );
}

/** Opens the Panels menu and returns the items of its My layouts group, by name. */
async function myLayouts(): Promise<{ name: string; disabled: boolean }[]> {
   const menu = await openMenu(panelsMenu());
   const group = within(menu).getByRole('group', { ...inMenu, name: 'My layouts' });
   const items = within(group)
      .getAllByRole('menuitem', inMenu)
      .map((item) => ({ name: itemName(item), disabled: item.getAttribute('aria-disabled') === 'true' }));
   await closeMenu(menu);
   return items;
}

const menuEnd = [
   { name: 'Save layout as…', disabled: false },
   { name: 'Manage layouts…', disabled: false },
];

/** Opens a dialog from the Panels menu's item, and returns the dialog. */
async function openFromMenu(item: string, dialog: string): Promise<ReturnType<typeof within>> {
   await arrange(item);
   return within(await screen.findByRole('dialog', { name: dialog }));
}

/** Enters the name in the Save layout dialog and saves. */
async function saveAs(name: string): Promise<ReturnType<typeof within>> {
   const dialog = await openFromMenu('Save layout as…', 'Save layout');
   await fireEvent.input(dialog.getByRole('textbox', { name: 'Name' }), { target: { value: name } });
   await fireEvent.click(dialog.getByRole('button', { name: 'Save' }));
   return dialog;
}

/** Returns the element that each panel renders in, opening each panel's tab in turn. */
async function panelElements(): Promise<Record<string, Element | null>> {
   const elements: Record<string, Element | null> = {};
   for (const name of Object.values(titles)) {
      await fireEvent.click(screen.getByRole('tab', { name }));
      elements[name] = await waitFor(() => {
         const element = screen.getByRole('region', { name }).querySelector('[role="tabpanel"]')?.firstElementChild ?? null;
         expect(element, name).not.toBeNull();
         return element;
      });
   }

   return elements;
}

describe('the named layouts', () => {
   it('lists a saved layout in the menu and applies it, with every panel keeping its state', async () => {
      const { world } = await startShell();
      await arrange('Columns');
      await togglePanel('Plan');
      await waitFor(() => expect(groups()).toEqual([['Files*'], ['Code*'], ['Stop*'], ['Threads*']]));
      await saveAs('Reading');
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Save layout' })).toBeNull());
      expect(document.activeElement).toBe(panelsMenu());
      expect(Object.values(await storedLayouts(world))).toEqual([{ name: 'Reading', record: expect.objectContaining({ hidden: [{ id: 'plan', home: null }] }) }]);
      await arrange('Reset layout');
      await waitFor(() => expect(groups()).toEqual(defaultGroups));
      const before = await panelElements();
      expect(new Set(Object.values(before)).size).toBe(5);

      await arrange('Reading');

      await waitFor(() => expect(groups()).toEqual([['Files*'], ['Code*'], ['Stop*'], ['Threads*']]));
      expect((await panelItems()).find((item) => item.name === 'Plan')?.checked).toBe(false);
      await waitFor(async () => expect(JSON.parse((await world.reviewer.call('read_preferences', {})).preferences[savedKey]!).hidden).toEqual([{ id: 'plan', home: null }]));
      await togglePanel('Plan');
      const after = await panelElements();
      for (const name of Object.values(titles)) {
         expect(after[name], name).toBe(before[name]);
      }
   });

   it('asks before it replaces the layout that has the name, in any letter case', async () => {
      const world = await backend().world();
      await world.reviewer.call('write_preference', { key: 'workspaceLayouts/a', value: layoutValue('Reading', columnsRecord) });
      await startApp(world);
      fireEvent.click(screen.getByRole('button', { name: 'Hide the stop' }));
      await waitFor(() => expect(groups()).toEqual([['Files', 'Plan*'], ['Code*'], ['Threads*']]));

      const dialog = await saveAs('reading');

      expect(await dialog.findByRole('heading', { name: 'Replace the layout Reading?' })).toBeTruthy();
      await fireEvent.click(dialog.getByRole('button', { name: 'Cancel' }));
      expect(dialog.getByRole('textbox', { name: 'Name' })).toBeTruthy();
      expect(Object.values(await storedLayouts(world))).toEqual([{ name: 'Reading', record: columnsRecord }]);
      await fireEvent.click(dialog.getByRole('button', { name: 'Save' }));
      await fireEvent.click(await dialog.findByRole('button', { name: 'Replace' }));
      // The dialog takes the name of its replace step, so the wait names none.
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      const stored = await storedLayouts(world);
      expect(Object.keys(stored)).toEqual(['workspaceLayouts/a']);
      expect(stored['workspaceLayouts/a']).toEqual({ name: 'reading', record: expect.objectContaining({ hidden: [{ id: 'stop', home: null }] }) });
   });

   it('renames and deletes a layout, which changes the menu', async () => {
      const world = await backend().world();
      await world.reviewer.call('write_preference', { key: 'workspaceLayouts/a', value: layoutValue('Reading', columnsRecord) });
      await world.reviewer.call('write_preference', { key: 'workspaceLayouts/b', value: layoutValue('Wide', columnsRecord) });
      await startApp(world);
      expect(await myLayouts()).toEqual([{ name: 'Reading', disabled: false }, { name: 'Wide', disabled: false }, ...menuEnd]);
      const dialog = await openFromMenu('Manage layouts…', 'Manage layouts');

      await fireEvent.click(dialog.getByRole('button', { name: 'Rename Reading' }));
      const field = dialog.getByRole('textbox', { name: 'New name of Reading' });
      await fireEvent.input(field, { target: { value: 'wide' } });
      expect(dialog.getByText('A layout named Wide already exists.')).toBeTruthy();
      expect(dialog.getByRole('button', { name: 'Save name' }).hasAttribute('disabled')).toBe(true);
      await fireEvent.input(field, { target: { value: 'Focus' } });
      await fireEvent.click(dialog.getByRole('button', { name: 'Save name' }));
      await dialog.findByRole('button', { name: 'Rename Focus' });
      await fireEvent.click(dialog.getByRole('button', { name: 'Delete Wide' }));
      await fireEvent.click(dialog.getByRole('button', { name: 'Delete' }));
      await waitFor(() => expect(dialog.queryByText(/Wide/)).toBeNull());
      await fireEvent.click(dialog.getByRole('button', { name: 'Close' }));
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Manage layouts' })).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(panelsMenu()));

      expect(await myLayouts()).toEqual([{ name: 'Focus', disabled: false }, ...menuEnd]);
      expect(await storedLayouts(world)).toEqual({ 'workspaceLayouts/a': { name: 'Focus', record: columnsRecord } });
   });

   it('shows a layout of another version disabled, and still renames and deletes it', async () => {
      const world = await backend().world();
      const newer = { ...columnsRecord, version: layoutVersion + 1 };
      await world.reviewer.call('write_preference', { key: 'workspaceLayouts/a', value: layoutValue('Wide', newer) });
      await startApp(world);

      expect(await myLayouts()).toEqual([{ name: 'Wide Saved by another version of lotsen', disabled: true }, ...menuEnd]);
      const dialog = await openFromMenu('Manage layouts…', 'Manage layouts');
      expect(dialog.getByText('Saved by another version of lotsen')).toBeTruthy();
      await fireEvent.click(dialog.getByRole('button', { name: 'Rename Wide' }));
      await fireEvent.input(dialog.getByRole('textbox', { name: 'New name of Wide' }), { target: { value: 'Old' } });
      await fireEvent.click(dialog.getByRole('button', { name: 'Save name' }));
      await dialog.findByRole('button', { name: 'Rename Old' });
      expect(await storedLayouts(world)).toEqual({ 'workspaceLayouts/a': { name: 'Old', record: newer } });
      await fireEvent.click(dialog.getByRole('button', { name: 'Delete Old' }));
      await fireEvent.click(dialog.getByRole('button', { name: 'Delete' }));

      await dialog.findByText('No layouts are saved.');
      expect(await storedLayouts(world)).toEqual({});
   });

   it('lists a layout that another page saves, and leaves this page’s workspace as it is', async () => {
      const world = await backend().world();
      const { app } = await startApp(world);
      await waitFor(() => expect(groups()).toEqual(defaultGroups));

      await world.reviewer.call('write_preference', { key: 'workspaceLayouts/a', value: layoutValue('Reading', columnsRecord) });

      await waitFor(async () => expect(await myLayouts()).toEqual([{ name: 'Reading', disabled: false }, ...menuEnd]));
      await settled(app);
      expect(groups()).toEqual(defaultGroups);
   });
});
