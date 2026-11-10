import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { attachClaude, backend, readTour, startApp } from '../harness';
import { filesPanel, planPanel, resetPageEachTest, tab, worldOf } from './appPanels';
import { buildTour, createTour, openPinnedTour, rounding, scrolledTo, type EditorSpies } from './fourStopTour';
import { spyOnFocus } from './editorFocus';

resetPageEachTest();

const spies = {} as EditorSpies;
beforeEach(() => {
   spies.focus = spyOnFocus();
});
afterEach(() => vi.restoreAllMocks());

const four = backend().template('stopReading-four', (world) => buildTour(world));

// Two tours of the same change, of which the one on pull request 43 is the newer.
const twoTours = backend().template('stopReading-twoTours', async (world) => {
   const older = await buildTour(world);
   await world.clock.advance(1000);
   const newer = await createTour(world, await world.claude(), undefined, 'https://platform.test/shop/pull/43');
   return { older, newer };
});

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
const stopNavigation = () => within(screen.getByRole('navigation', { name: 'Stops' }));
const planStop = (title: string) => planPanel().getByRole('button', { name: new RegExp(title) });
/** The status that the plan shows after a stop's title, or null when it shows none, as for a ready stop. */
const planStatus = (title: string) => planStop(title).querySelector('.status')?.textContent.trim() ?? null;
const readOperations = new Set(['list_tours', 'read_state', 'read_summary', 'list_changed_files', 'read_file_text', 'diff_file', 'list_directory']);

async function openFour() {
   const { world, seed } = await worldOf(four);
   const started = await startApp(world);
   await openPinnedTour(started.app);
   return { ...started, world, seed };
}

describe('the stops of a tour', () => {
   it('opens a stop from the plan with its number, kind, title and headline, opens its primary passage, and marks it current in the plan', async () => {
      await openFour();

      fireEvent.click(planStop('Cart totals'));

      expect(await stopPanel().findByRole('heading', { name: 'Cart totals' })).toBeTruthy();
      expect(stopNavigation().getByText('Stop 2 of 4')).toBeTruthy();
      expect(stopPanel().getByText('Inspect', { ignore: '[data-stop-bar] *' })).toBeTruthy();
      expect(stopPanel().getByText('Totals round to cents.')).toBeTruthy();
      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(planStop('Cart totals').getAttribute('aria-current')).toBe('step');
      expect(planStop('The change at a glance').getAttribute('aria-current')).toBeNull();
   });

   it('moves to the next and the previous stop, each showing its headline and opening its primary passage, and sends the server only the reading progress', async () => {
      const { app, transport, world, seed } = await openFour();
      fireEvent.click(planStop('The change at a glance'));
      expect(await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5)).toBe('cart line 5');

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      expect(await stopPanel().findByText('Totals round to cents.')).toBeTruthy();
      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      fireEvent.click(await stopNavigation().findByRole('button', { name: 'Next stop' }));
      expect(await stopPanel().findByText('Is the old form gone?')).toBeTruthy();
      expect(await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20)).toBe('cart head line 20');
      expect(planStop('The old form').getAttribute('aria-current')).toBe('step');

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));
      fireEvent.click(await stopNavigation().findByRole('button', { name: 'Previous stop' }));
      expect(await stopPanel().findByText('Totals round to cents.')).toBeTruthy();
      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');

      const writes = transport.requests.filter((request) => request.operation !== null && !readOperations.has(request.operation));
      expect(writes.map((request) => `${request.operation} ${String(request.input?.tourId)}`)).toEqual(writes.map(() => `record_reading_progress ${seed.tourId}`));
      await waitFor(async () => expect((await readTour(world, seed.tourId)).progress?.lastStop).toBe(app.selectedStopId));
   });

   it("lists the open stop's files first in the Files tab when grouped by stop, and follows the stop as the reviewer moves on", async () => {
      await openFour();
      fireEvent.click(tab('Files'));
      const files = await filesPanel();
      fireEvent.click(files.getByRole('button', { name: 'Group by stop' }));
      fireEvent.click(files.getByRole('button', { name: 'Flat' }));
      const rows = (name: string) =>
         within(files.getByRole('tree', { name }))
            .getAllByRole('treeitem')
            .map((item) => item.dataset.id);
      expect(await files.findByRole('tree', { name: 'Changed files' })).toBeTruthy();

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await waitFor(() => expect(rows('Changed files in this stop')).toEqual(['src/cart.ts', 'src/pay.ts']));
      expect(files.queryByRole('tree', { name: 'Other changed files' })).toBeNull();

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await waitFor(() => expect(rows('Changed files in this stop')).toEqual(['src/pay.ts']));
      expect(rows('Other changed files')).toEqual(['src/cart.ts']);
   });

   it.each([false, true])('scrolls to the open passage in the layout that the toolbar switches to, with the diff loaded before the change: %s', async (loadedFirst) => {
      const { transport } = await openFour();
      const release = transport.hold('diff_file', 'response');
      fireEvent.click(planStop('Cart totals'));
      if (loadedFirst) {
         release();
         expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      }

      fireEvent.click(within(screen.getByRole('main', { name: 'Diff' })).getByRole('button', { name: 'Side by side' }));
      release();

      await waitFor(() => expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.cm-editor')).toHaveLength(1));
      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(transport.requests.filter((request) => request.operation === 'diff_file').map((request) => request.input?.path)).toEqual(['src/pay.ts']);
   });

   it('names the open stop in the top bar with its number, chapter and title', async () => {
      await openFour();
      expect(stopNavigation().getByText('4 stops')).toBeTruthy();

      fireEvent.click(planStop('Cart totals'));

      await waitFor(() => expect(screen.getByRole('navigation', { name: 'Stops' }).textContent?.replace(/\s+/g, ' ').trim()).toBe('Stop 2 of 4 · Chapter · Cart totals'));
   });

   it('opens the first stop from the top bar when no stop is open', async () => {
      await openFour();
      expect(stopNavigation().getByRole('button', { name: 'Previous stop' }).hasAttribute('disabled')).toBe(true);

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));

      expect(await stopNavigation().findByText('Stop 1 of 4')).toBeTruthy();
      expect(await stopPanel().findByRole('heading', { name: 'The change at a glance' })).toBeTruthy();
      expect(await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5)).toBe('cart line 5');
   });

   it('offers no previous stop at the first stop, and the summary page after the last', async () => {
      await openFour();

      fireEvent.click(planStop('The change at a glance'));
      const back = await stopNavigation().findByRole('button', { name: 'Previous stop' });
      expect(back.hasAttribute('disabled')).toBe(true);
      expect(stopNavigation().getByRole('button', { name: 'Next stop' }).hasAttribute('disabled')).toBe(false);
      fireEvent.click(back);
      expect(stopNavigation().getByText('Stop 1 of 4')).toBeTruthy();

      fireEvent.click(planStop('The old form'));
      expect(await stopNavigation().findByText('Stop 4 of 4')).toBeTruthy();
      const next = stopNavigation().getByRole('button', { name: 'Summary' });
      expect(next.hasAttribute('disabled')).toBe(false);
      expect(stopNavigation().getByRole('button', { name: 'Previous stop' }).hasAttribute('disabled')).toBe(false);
      fireEvent.click(next);
      expect(await stopNavigation().findByText('After the last stop')).toBeTruthy();
      expect(stopNavigation().getByRole('button', { name: 'Next stop' }).hasAttribute('disabled')).toBe(true);
   });

   it('shows each section the stop has open until the reviewer closes it, and leaves out the empty ones', async () => {
      const { world, seed } = await worldOf(four);
      const claude = await attachClaude(world, seed.tourId);
      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds[2], ...rounding });
      const { app } = await startApp(world);
      await openPinnedTour(app);
      fireEvent.click(planStop('The change at a glance'));
      await stopPanel().findByText('The cart checks its quantities.');
      // The tour actions open their words with buttons of their own, which are no sections.
      const outsideActions = (buttons: HTMLElement[]) => buttons.filter((button) => button.closest('[aria-label="Tour actions"]') === null);
      const sections = () =>
         outsideActions(
            stopPanel()
               .queryAllByRole('button', { expanded: false })
               .concat(stopPanel().queryAllByRole('button', { expanded: true })),
         );
      expect(
         sections()
            .map((button) => button.textContent?.trim())
            .sort(),
      ).toEqual(['Checks', 'Design choices', 'How', 'Pinned by', 'Why']);
      expect(outsideActions(stopPanel().queryAllByRole('button', { expanded: false }))).toEqual([]);

      const how = stopPanel().getByRole('button', { name: 'How' });
      const section = document.getElementById(how.getAttribute('aria-controls')!)!;
      expect(section.hidden).toBe(false);
      expect(section.textContent).toContain('Each add checks the quantity.');
      fireEvent.click(how);
      await waitFor(() => expect(section.hidden).toBe(true));
      expect(how.getAttribute('aria-expanded')).toBe('false');
      fireEvent.click(how);
      await waitFor(() => expect(section.hidden).toBe(false));

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await stopPanel().findByText('Totals round to cents.');
      expect(sections().map((button) => button.textContent?.trim())).toEqual(['Why']);
      expect(stopPanel().getByRole('button', { name: 'Why' }).getAttribute('aria-expanded')).toBe('true');

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await stopPanel().findByText('Rounding can hide a cent.');
      expect(sections().map((button) => `${button.textContent?.trim()} ${button.getAttribute('aria-expanded')}`)).toEqual(['Scenario true']);
   });

   it("shows each check numbered with its question, recommendation and suggested answers, without the answers' types", async () => {
      await openFour();
      fireEvent.click(planStop('The change at a glance'));
      await stopPanel().findByText('The cart checks its quantities.');

      const checks = stopPanel().getByRole('button', { name: 'Checks' });
      const section = document.getElementById(checks.getAttribute('aria-controls')!)!;
      const items = within(section)
         .getAllByRole('listitem')
         .filter((item) => item.parentElement?.tagName === 'OL');
      expect(items).toHaveLength(2);
      const [first, second] = items as [HTMLElement, HTMLElement];
      expect(within(first).getByText('Is a zero quantity an error?')).toBeTruthy();
      expect(within(first).getByText('Recommendation')).toBeTruthy();
      expect(within(first).getByText('Yes: a zero line is noise.')).toBeTruthy();
      const answers = within(first).getByRole('list', { name: 'Suggested answers' });
      expect(
         within(answers)
            .getAllByRole('listitem')
            .map((item) => item.textContent),
      ).toEqual(['Refuse a zero quantity.', 'Zero may mean a removal.', 'What does the shop expect?']);
      expect(section.textContent).not.toMatch(/decision|concern|openQuestion|open question/i);
      expect(within(second).getByText('Should remove check too?')).toBeTruthy();
      expect(within(second).queryByText('Recommendation')).toBeNull();
      expect(within(second).queryByRole('list', { name: 'Suggested answers' })).toBeNull();
   });

   it('shows a pending stop as being written and a ready one without a status, and turns a pending stop ready when its stopPut event arrives', async () => {
      const { world, seed } = await worldOf(four);
      const claude = await attachClaude(world, seed.tourId);
      const { app } = await startApp(world);
      await openPinnedTour(app);
      expect(planStatus('Rounding')).toBe('Writing');
      expect(planStatus('Cart totals')).toBeNull();
      fireEvent.click(planStop('Rounding'));
      expect(await stopPanel().findByText('Claude has not written this stop yet.')).toBeTruthy();
      expect(stopPanel().getByText('Risk', { ignore: '[data-stop-bar] *' })).toBeTruthy();

      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds[2], ...rounding });

      expect(await stopPanel().findByText('Rounding can hide a cent.')).toBeTruthy();
      expect(stopPanel().queryByText('Claude has not written this stop yet.')).toBeNull();
      expect(planStatus('Rounding')).toBeNull();
      expect(await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20)).toBe('cart head line 20');
   });

   it('ignores a stopPut for a stop of another tour', async () => {
      const { world, seed } = await worldOf(twoTours);
      const claude = await attachClaude(world, seed.older.tourId);
      const { app } = await startApp(world);
      await openPinnedTour(app, 'shop #43');
      fireEvent.click(planStop('Rounding'));
      await stopPanel().findByText('Claude has not written this stop yet.');
      const before = app.tours.find((tour) => tour.id === seed.older.tourId)!.updatedAt;

      await world.clock.advance(1000);
      await claude.call('put_stop', { tourId: seed.older.tourId, stopId: seed.older.stopIds[2], ...rounding });
      await waitFor(() => expect(app.tours.find((tour) => tour.id === seed.older.tourId)?.updatedAt).not.toBe(before));

      expect(stopPanel().getByText('Claude has not written this stop yet.')).toBeTruthy();
      expect(planStatus('Rounding')).toBe('Not written yet');
   });

   it('lists the passages with their labels, and opens a related passage in a changed file, in an unchanged file and at the base', async () => {
      await openFour();
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      const passages = within(stopPanel().getByRole('list', { name: 'Code' }));
      expect(passages.getAllByRole('button').map((button) => button.getAttribute('aria-label'))).toEqual([
         'primary passage, src/cart.ts, lines 5–7',
         'caller, related passage, src/pay.ts, lines 30–31',
         'model, related passage, src/catalog.ts, lines 3–4',
         'before, related passage, src/cart.ts, line 20 at the base',
      ]);

      fireEvent.click(passages.getByRole('button', { name: /caller/ }));
      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');

      fireEvent.click(passages.getByRole('button', { name: /model/ }));
      expect(await scrolledTo(spies, 'File', 'src/catalog.ts', 'head', 3)).toBe('catalog line 3');

      fireEvent.click(passages.getByRole('button', { name: /before/ }));
      expect(await scrolledTo(spies, 'Diff', 'src/cart.ts', 'base', 20)).toBe('cart base line 20');
   });
});
