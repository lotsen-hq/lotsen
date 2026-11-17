import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { attachClaude, backend, openStop, openTour, seedDive, seedRepositoryTour, settled, startApp, withEventHolds } from '../harness';
import { openTourList } from '../tourPicker';
import { planPanel, resetPageEachTest, tab, worldOf } from './appPanels';

resetPageEachTest();

const rootTitle = 'How the cart adds up';
const otherTitle = 'Where the totals live';
const stopDiveTitle = 'The rounding in depth';
const wholeDiveTitle = 'Everything about totals';
const deeperTitle = 'Rounding edge cases';
const passage = { side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 2, role: 'primary' };
const written = (headline: string) => ({ headline, passages: [passage] });

interface Family {
   rootId: string;
   rootStopIds: string[];
   stopDiveId: string;
   wholeDiveId: string;
   deeperId: string;
}

/**
 * A root with an overview and a context stop, then another tour, then three dives a second apart: one from the context stop that the reviewer reads, one from the whole tour without a plan,
 * and one from the first dive that the reviewer read to its summary page.
 */
const family = backend().template('tour-family', async (world): Promise<Family> => {
   const root = await seedRepositoryTour(world, {
      title: rootTitle,
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'The cart at a glance', kind: 'overview', content: written('One cart, one total.') },
               { title: 'Rounding', kind: 'context', content: written('Totals round once.') },
            ],
         },
      ],
   });
   await world.clock.advance(1000);
   await seedRepositoryTour(world, { title: otherTitle, bound: false });
   await world.clock.advance(1000);
   const claude = root.claude!;
   const stopDive = await seedDive(world, claude, {
      parentTourId: root.tourId,
      stopId: root.stopIds[1],
      purpose: 'prepare',
      title: stopDiveTitle,
      chapters: [{ title: 'Rounding', stops: [{ title: 'Where it rounds', kind: 'inspect', content: written('It rounds at the end.') }] }],
      read: 0,
   });
   await world.clock.advance(1000);
   const wholeDive = await seedDive(world, claude, { parentTourId: root.tourId, purpose: 'evaluate', title: wholeDiveTitle });
   await world.clock.advance(1000);
   const deeper = await seedDive(world, claude, {
      parentTourId: stopDive.tourId,
      title: deeperTitle,
      chapters: [{ title: 'Edges', stops: [{ title: 'Half a cent', kind: 'inspect', content: written('Half a cent rounds up.') }] }],
      read: 'summary',
   });
   await claude.drop();
   return { rootId: root.tourId, rootStopIds: root.stopIds, stopDiveId: stopDive.tourId, wholeDiveId: wholeDive.tourId, deeperId: deeper.tourId };
});

async function openFamily(): Promise<{ app: LotsenApp; world: World; seed: Family }> {
   const { world, seed } = await worldOf(family);
   const { app } = await startApp(world);
   return { app, world, seed };
}

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
const switcher = () => screen.getByRole('button', { name: 'Choose another tour' });

/** Focuses the control and clicks it, as the keyboard's Enter does. */
async function press(control: HTMLElement): Promise<void> {
   control.focus();
   await fireEvent.click(control);
}

const descriptionOf = (element: HTMLElement) =>
   document
      .getElementById(element.getAttribute('aria-describedby') ?? '')
      ?.textContent.replace(/\s+/g, ' ')
      .trim();

describe('the tour list', () => {
   it('groups the family under its root, one level deep and oldest first, with the roots newest first', async () => {
      await openFamily();
      const list = await openTourList();
      const items = list.getAllByRole('listitem');

      expect(items.map((item) => [item.querySelector('[id^="tour-label-"]')!.textContent, item.dataset.depth])).toEqual([
         [otherTitle, '0'],
         [rootTitle, '0'],
         [stopDiveTitle, '1'],
         [wholeDiveTitle, '1'],
         [deeperTitle, '1'],
      ]);
      expect(items[4]!.querySelector('[data-dive-parent]')?.textContent.trim()).toBe(`Dive from ${stopDiveTitle}`);
   });
});

describe("a dive's top bar", () => {
   it('shows the breadcrumb back to the root, and a step opens its tour', async () => {
      const { app, seed } = await openFamily();
      await openTour(app, deeperTitle);
      const breadcrumb = within(screen.getByRole('navigation', { name: 'Breadcrumb' }));

      expect(breadcrumb.getAllByRole('button').map((step) => step.textContent.trim())).toEqual([rootTitle, stopDiveTitle]);
      expect(breadcrumb.getByText(deeperTitle).closest('[aria-current="page"]')).not.toBeNull();

      await press(breadcrumb.getByRole('button', { name: rootTitle }));

      await waitFor(() => expect(app.selectedTourId).toBe(seed.rootId));
      expect(screen.queryByRole('navigation', { name: 'Breadcrumb' })).toBeNull();
      await waitFor(() => expect(document.activeElement).toBe(switcher()));
   });
});

describe("a parent's dives", () => {
   it('mark the stop each started from in the Plan panel, a whole-tour dive the overview, and the marker opens a dive', async () => {
      const { app, seed } = await openFamily();
      await openTour(app, rootTitle);
      await fireEvent.click(tab('Plan'));

      expect(
         planPanel()
            .getByRole('button', { name: /^1 Overview: The cart at a glance/ })
            .getAttribute('aria-label'),
      ).toMatch(/, 1 dive$/);
      const marker = planPanel().getByRole('button', { name: 'Dives from stop 2: 1' });
      marker.focus();
      await fireEvent.keyDown(marker, { key: 'Enter' });
      const menu = within(await screen.findByRole('menu', { hidden: true }));
      const item = menu.getByRole('menuitem', { name: `${stopDiveTitle}, In progress`, hidden: true });
      item.focus();
      await fireEvent.keyDown(item, { key: 'Enter' });

      await waitFor(() => expect(app.selectedTourId).toBe(seed.stopDiveId));
      await waitFor(() => expect(screen.queryByRole('menu', { hidden: true })).toBeNull());
      await settled(app);
      expect(document.activeElement).toBe(switcher());
   });

   it('show in a Dives row on the stop each started from, and on the overview for a whole-tour dive', async () => {
      const { app, seed } = await openFamily();
      await openTour(app, rootTitle);
      await openStop(app, seed.rootStopIds[1]!);

      const row = within(stopPanel().getByRole('region', { name: 'Dives' }));
      expect(row.getAllByRole('button').map((link) => [link.textContent.trim(), descriptionOf(link)])).toEqual([[stopDiveTitle, 'In progress']]);

      await openStop(app, seed.rootStopIds[0]!);
      const overviewRow = within(stopPanel().getByRole('region', { name: 'Dives' }));
      expect(overviewRow.getAllByRole('button').map((link) => [link.textContent.trim(), descriptionOf(link)])).toEqual([[wholeDiveTitle, 'Not started']]);

      await press(overviewRow.getByRole('button', { name: wholeDiveTitle }));
      await waitFor(() => expect(app.selectedTourId).toBe(seed.wholeDiveId));
      await waitFor(() => expect(document.activeElement).toBe(switcher()));
   });

   it('list in a Dives section on the summary page with their purpose and reading state, without the dives of a dive', async () => {
      const { app, seed } = await openFamily();
      await openTour(app, rootTitle);
      app.openSummary();
      const section = within(await screen.findByRole('region', { name: 'Dives 2' }));

      expect(section.getAllByRole('button').map((link) => [link.textContent.trim(), descriptionOf(link)])).toEqual([
         [stopDiveTitle, 'Prepare, In progress'],
         [wholeDiveTitle, 'Evaluate, Not started'],
      ]);

      await press(section.getByRole('button', { name: stopDiveTitle }));
      await waitFor(() => expect(app.selectedTourId).toBe(seed.stopDiveId));
      await waitFor(() => expect(document.activeElement).toBe(switcher()));
   });

   it('show a new dive and its reading state without a reload', async () => {
      const { app, world, seed } = await openFamily();
      await openTour(app, rootTitle);
      await openStop(app, seed.rootStopIds[1]!);
      const claude = await attachClaude(world, seed.rootId);
      // The template made its tours a second apart on the world's clock, so the new dive needs a later time to be the newest.
      await world.clock.advance(60_000);

      const dive = await seedDive(world, claude, {
         parentTourId: seed.rootId,
         stopId: seed.rootStopIds[1],
         title: 'Rounding, once more',
         chapters: [{ title: 'Again', stops: [{ title: 'Once more', kind: 'context', content: written('It still rounds once.') }] }],
      });
      await settled(app);
      const row = () => within(stopPanel().getByRole('region', { name: 'Dives' }));
      await waitFor(() =>
         expect(
            row()
               .getAllByRole('button')
               .map((link) => descriptionOf(link)),
         ).toEqual(['In progress', 'Not started']),
      );

      await world.reviewer.call('record_reading_progress', { tourId: dive.tourId, stopId: dive.stopIds[0]! });
      await settled(app);

      await waitFor(() => expect(row().getByRole('button', { name: 'Rounding, once more' }).getAttribute('aria-describedby')).toBeTruthy());
      expect(descriptionOf(row().getByRole('button', { name: 'Rounding, once more' }))).toBe('In progress');
   });
});

describe('deleting a dive', () => {
   it('drops the dive and the dives below it once the server answers, before their tourDeleted events arrive', async () => {
      const { world, seed } = await worldOf(family);
      const transport = withEventHolds(world.transport());
      const { app } = await startApp(world, { transport });
      transport.holdKinds('tourDeleted');

      expect(await app.deleteTour(seed.stopDiveId)).toBeNull();

      const ids = app.tours.map((tour) => tour.id);
      expect(ids).toEqual(expect.arrayContaining([seed.rootId, seed.wholeDiveId]));
      expect(ids).not.toContain(seed.stopDiveId);
      expect(ids).not.toContain(seed.deeperId);
      expect(ids).toHaveLength(3);
   });
});

/** Opens the tour list and returns the row of the tour with the title. */
async function rowOf(title: string): Promise<HTMLElement> {
   const list = await openTourList();
   return await waitFor(() => list.getAllByRole('listitem').find((item) => item.querySelector('[id^="tour-label-"]')?.textContent === title)!);
}

/** Clicks the button of the tour's row whose name starts with the prefix, such as its delete or archive button. */
async function clickRowButton(title: string, prefix: string): Promise<void> {
   const row = await rowOf(title);
   await fireEvent.click(within(row).getByRole('button', { hidden: true, name: (_, element) => element.getAttribute('aria-label')?.startsWith(prefix) === true }));
}

// Each item as its text, followed by its nested items in brackets.
const nested = (list: HTMLElement): string[] =>
   [...list.children].map((item) => {
      const inner = item.querySelector(':scope > ul');
      const text = [...item.childNodes]
         .filter((node) => node.nodeType === Node.TEXT_NODE)
         .map((node) => node.textContent)
         .join('')
         .trim();
      return inner === null ? text : `${text}[${nested(inner as HTMLElement).join(', ')}]`;
   });

const listedTitles = async () => (await openTourList()).getAllByRole('listitem').map((item) => item.querySelector('[id^="tour-label-"]')!.textContent);

describe('deleting a family', () => {
   it('names the dives in the confirmation, then drops the family from the list and closes the open dive', async () => {
      const { app, world, seed } = await openFamily();
      await openTour(app, deeperTitle);

      await clickRowButton(rootTitle, 'Delete this tour');
      const dialog = await screen.findByRole('alertdialog', { name: 'Delete this tour?' });
      expect(dialog.textContent).toContain('and the 3 dives below it are deleted for good');
      expect(nested(within(dialog).getByRole('list', { name: 'Dives deleted with it' }))).toEqual([`${stopDiveTitle}[${deeperTitle}]`, wholeDiveTitle]);
      await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete tour' }));

      await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
      expect(app.selectedTourId).toBeNull();
      expect(screen.queryByRole('navigation', { name: 'Breadcrumb' })).toBeNull();
      await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Choose a tour' })));
      await settled(app);
      expect(await listedTitles()).toEqual([otherTitle]);
      const stored = (await world.reviewer.call('list_tours', {})).tours.map((tour) => tour.id);
      expect(stored).toHaveLength(1);
      expect(stored).not.toContain(seed.rootId);
   });

   it("shows the server's reason in the dialog when a dive below the tour cannot be read, and keeps the family", async () => {
      const { app, world, seed } = await openFamily();
      await world.damageTour(seed.deeperId);

      await clickRowButton(rootTitle, 'Delete this tour');
      const dialog = await screen.findByRole('alertdialog', { name: 'Delete this tour?' });
      await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete tour' }));

      expect((await within(dialog).findByRole('alert')).textContent).toContain(`Tour ${seed.deeperId} could not be read`);
      expect(screen.queryByRole('alertdialog')).not.toBeNull();
      expect(app.tours).toHaveLength(5);
      expect(nested(within(dialog).getByRole('list', { name: 'Dives deleted with it' }))).toEqual([`${stopDiveTitle}[${deeperTitle}]`, wholeDiveTitle]);
   });
});

describe('archiving a family', () => {
   it("names the dives on the archive button, hides them with the root and marks them archived in the open parent's Dives row, and an unarchive restores them", async () => {
      const { app, seed } = await openFamily();
      await openTour(app, rootTitle);
      await openStop(app, seed.rootStopIds[1]!);
      const diveLink = () => within(stopPanel().getByRole('region', { name: 'Dives' })).getByRole('button', { name: stopDiveTitle });

      await clickRowButton(rootTitle, 'Archive this tour and the 3 dives below it');
      await settled(app);

      await waitFor(() => expect(descriptionOf(diveLink())).toBe('Archived'));
      expect(await listedTitles()).toEqual([otherTitle]);
      expect(app.tours.filter((tour) => tour.state === 'archived').map((tour) => tour.id)).toEqual(expect.arrayContaining([seed.rootId, seed.stopDiveId, seed.wholeDiveId, seed.deeperId]));

      app.setShowHiddenTours(true);
      await clickRowButton(rootTitle, 'Unarchive this tour and the dives archived with it');
      await settled(app);

      await waitFor(() => expect(descriptionOf(diveLink())).toBe('In progress'));
      app.setShowHiddenTours(false);
      await waitFor(async () => expect(await listedTitles()).toEqual([otherTitle, rootTitle, stopDiveTitle, wholeDiveTitle, deeperTitle]));
   });
});
