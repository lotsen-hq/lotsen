import { EditorView } from '@codemirror/view';
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CodeMirrorSurface } from '../../src/lib/editor/codeMirrorSurface';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { DiffLayout } from '../../src/lib/diffLayout';
import type { Template, Transport, World } from '../backend/backend';
import { attachClaude, backend, openTour, seedTour, startApp } from '../harness';
import { currentRows } from '../treeRows';
import type { AnchorInput, UpdatedThread } from '../../src/lib/types';
import { chooseWhere, whereOnButton } from './threadFilter';
import { backToList, shownIds, threadViewElement } from './threadView';
import { filesPanel, planPanel, resetPageEachTest, tab, worldOf } from './appPanels';
import {
   chooseMode,
   keepStops,
   openPinnedTour,
   openStopIn,
   passage,
   planStop,
   pressed,
   reviseViewedStop,
   scattered,
   scatteredSpec,
   scrolledTo,
   spyOnEditorsEachTest,
   stackHeaders,
   stackItem,
   stackItems,
   stackScrolledTo,
   stopFiles,
   stopNavigation,
   stopPanel,
   totals,
   type SeededIds,
} from './stopViewSupport';

resetPageEachTest();
spyOnEditorsEachTest();

let scrolledItems: Element[];

beforeEach(() => {
   scrolledItems = [];
   Element.prototype.scrollIntoView = function (this: Element) {
      scrolledItems.push(this);
   };
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

const seeded = (name: string, spec: Parameters<typeof seedTour>[1]) =>
   backend().template(name, async (world): Promise<SeededIds> => {
      const { tourId, stopIds } = await seedTour(world, spec);
      return { tourId, stopIds };
   });

const scatteredTour = seeded('stopFilesMode-scattered', scatteredSpec);
// The headline links to a file outside the stop and to a line of the stop's own file.
const linkedTour = seeded('stopFilesMode-linked', {
   ...stopFiles,
   chapters: [
      {
         title: 'Chapter',
         stops: [
            {
               title: 'Cart totals',
               kind: 'inspect',
               content: { headline: 'Totals use [the add](ref:head/src/cart.ts#L20-20) and [the caller](ref:head/src/pay.ts#L30-31).', passages: [passage('head', 'src/pay.ts', 20, 22)] },
            },
            { title: 'Totals again', kind: 'inspect', content: totals },
         ],
      },
   ],
});

/** The scattered tour's IDs, with a note about the cart's passage, one about the pay file's and one about the tour. */
interface NotedIds extends SeededIds {
   cartNote: string;
   payNote: string;
   tourNote: string;
}

const notedTour = backend().template('stopFilesMode-noted', async (world): Promise<NotedIds> => {
   const { tourId, stopIds } = await seedTour(world, scatteredSpec);
   const note = async (text: string, anchor: AnchorInput) => ((await world.reviewer.call('save_note', { tourId, text, anchor })) as UpdatedThread).thread.id;
   return {
      tourId,
      stopIds,
      cartNote: await note('About the cart.', { kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 5, endLine: 7 }),
      payNote: await note('About the pay file.', { kind: 'code', side: 'head', path: 'src/pay.ts', startLine: 30, endLine: 31 }),
      tourNote: await note('About the tour.', { kind: 'tour' }),
   };
});

async function started(template: Template<SeededIds> = scatteredTour): Promise<{ world: World; seed: SeededIds; app: LotsenApp; transport: Transport }> {
   const { world, seed } = await worldOf(template);
   const { app, transport } = await startApp(world);
   return { world, seed, app, transport };
}

/** The diff loads of the file in its default form: not across a rename, without whitespace ignored and without a large file's texts. */
const diffRequests = (transport: Transport, path: string) =>
   transport.requests.filter((request) => request.operation === 'diff_file' && request.input?.path === path && Object.keys(request.input).length === 2);

/** The line numbers that an editor of a stacked file draws, with each collapsed marker as 0. */
function drawnLines(path: string, index: number): number[] {
   const editor = EditorView.findFromDOM(stackItem(path)!.querySelectorAll<HTMLElement>('.cm-editor')[index]!)!;
   return [...editor.contentDOM.children]
      .filter((element) => element.classList.contains('cm-line') || element.classList.contains('lotsen-collapsed'))
      .map((element) => (element.classList.contains('cm-line') ? editor.state.doc.lineAt(editor.posAtDOM(element)).number : 0));
}

const link = (name: string) => stopPanel().getByRole('link', { name, hidden: true });

/** Opens the tour from the tour list while the transport holds its changed files, then opens the stop in stop files from the plan. */
async function openWhileFilesLoad(app: LotsenApp, transport: Transport, title: string): Promise<() => void> {
   const release = transport.hold('list_changed_files');
   await openTour(app);
   fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   chooseMode('Stop files');
   fireEvent.click(await planPanel().findByRole('button', { name: new RegExp(title) }));
   await stopPanel().findByRole('heading', { name: title });
   return release;
}

describe('the stop-files mode', () => {
   it.each<[DiffLayout, number]>([
      ['sideBySide', 1],
      ['inline', 0],
   ])('stacks exactly the stop’s files in passage order, each collapsed to its passages, %s', async (layout, headEditor) => {
      localStorage.setItem('lotsen.diffLayout', layout);
      const { app } = await started();
      await openStopIn(app, 'Stop files');

      expect(pressed('Stop files')).toBe(true);
      expect(stackHeaders()).toEqual(['src/cart.ts', 'src/pay.ts', 'src/catalog.ts head']);
      expect(await stackScrolledTo('src/cart.ts', 'head', 5)).toBe('cart line 5');
      // The cart's lines 5 to 7 at the head and its line 20 at the base stay with three lines of context, as do the pay file's lines 30 and 31 and the catalog's lines 3 and 4.
      // Inline, the base's line 20 is a deleted row above the head's line 20, so the context after it ends a line sooner.
      const cart = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0, 17, 18, 19, 20, 21, 22, ...(layout === 'inline' ? [] : [23]), 0];
      await waitFor(() => expect(drawnLines('src/cart.ts', headEditor)).toEqual(cart));
      await waitFor(() => expect(drawnLines('src/pay.ts', headEditor)).toEqual([0, 27, 28, 29, 30, 31, 32, 33, 34, 0]));
      await waitFor(() => expect(drawnLines('src/catalog.ts', 0)).toEqual([1, 2, 3, 4, 5, 6, 7, 0]));
      expect(stackItem('src/pay.ts')!.querySelector('.cm-line.lotsen-passage-related')?.textContent).toContain('pay line 30');
      expect(screen.queryByRole('main', { name: 'Diff' })).toBeNull();
   });

   it('loads the stacked diffs again with whitespace ignored from the toolbar, which offers no collapse toggle', async () => {
      const { app, transport } = await started();
      await openStopIn(app, 'Stop files');
      await waitFor(() => expect(diffRequests(transport, 'src/pay.ts')).toHaveLength(1));
      const toolbar = within(screen.getByRole('main', { name: 'Stop files' }));
      expect(toolbar.queryByRole('button', { name: 'Collapse to passages' })).toBeNull();
      const toggle = toolbar.getByRole('button', { name: 'Ignore whitespace' });
      expect(toggle.getAttribute('aria-pressed')).toBe('false');

      fireEvent.click(toggle);

      const ignored = (path: string) => transport.requests.some((request) => request.operation === 'diff_file' && request.input?.path === path && request.input.whitespace === 'ignore');
      await waitFor(() => expect(['src/cart.ts', 'src/pay.ts'].map(ignored)).toEqual([true, true]));
      expect(toggle.getAttribute('aria-pressed')).toBe('true');
   });

   it('shows the stacked diffs in the layout that the toolbar switches to, keeping the stack', async () => {
      const { app } = await started();
      await openStopIn(app, 'Stop files');
      await waitFor(() => expect(stackItem('src/pay.ts')!.querySelectorAll('.cm-editor')).toHaveLength(2));
      const toggle = within(screen.getByRole('main', { name: 'Stop files' })).getByRole('button', { name: 'Side by side' });
      expect(toggle.getAttribute('aria-pressed')).toBe('true');

      fireEvent.click(toggle);

      await waitFor(() => expect(['src/cart.ts', 'src/pay.ts'].map((path) => stackItem(path)!.querySelectorAll('.cm-editor').length)).toEqual([1, 1]));
      expect(stackItems().map((candidate) => candidate.querySelector('header code')?.textContent)).toEqual(['src/cart.ts', 'src/pay.ts', 'src/catalog.ts']);
      expect(toggle.getAttribute('aria-pressed')).toBe('false');
      expect(localStorage.getItem('lotsen.diffLayout')).toBe('inline');
   });

   it('dims no stacked file once the reviewer switches dimming off from the toolbar, and keeps the passages highlighted', async () => {
      const { app } = await started();
      await openStopIn(app, 'Stop files');
      await waitFor(() => expect(stackItem('src/cart.ts')!.querySelectorAll('.cm-line.lotsen-dimmed').length).toBeGreaterThan(0));

      fireEvent.click(within(screen.getByRole('main', { name: 'Stop files' })).getByRole('button', { name: 'Dim outside the passages' }));

      await waitFor(() => expect(screen.getByRole('list', { name: 'Files in this stop' }).querySelectorAll('.lotsen-dimmed')).toHaveLength(0));
      await waitFor(() => expect(stackItem('src/pay.ts')!.querySelector('.cm-line.lotsen-passage-related')?.textContent).toContain('pay line 30'));
      expect(pressed('Stop files')).toBe(true);
   });

   it('highlights no stacked passage once the reviewer switches the highlight off from the toolbar, and keeps the collapse', async () => {
      const { app } = await started();
      await openStopIn(app, 'Stop files');
      await waitFor(() => expect(stackItem('src/pay.ts')!.querySelector('.cm-line.lotsen-passage-related')).not.toBeNull());

      fireEvent.click(within(screen.getByRole('main', { name: 'Stop files' })).getByRole('button', { name: 'Highlight the passages' }));

      await waitFor(() => expect(screen.getByRole('list', { name: 'Files in this stop' }).querySelectorAll('.lotsen-passage')).toHaveLength(0));
      expect(stackItem('src/pay.ts')!.querySelectorAll('.lotsen-collapsed').length).toBeGreaterThan(0);
      expect(pressed('Stop files')).toBe(true);
   });

   it('stacks only the files of the open stop, and follows the next stop', async () => {
      const { app } = await started();
      await openStopIn(app, 'Stop files');

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));

      await waitFor(() => expect(stackHeaders()).toEqual(['src/pay.ts']));
      expect(await stackScrolledTo('src/pay.ts', 'head', 20)).toBe('pay line 20');
   });

   it('adds the file of a ref: link outside the stop at the bottom, marked, scrolls to it and keeps the mode, and drops it when the stop changes', async () => {
      const { app } = await started(linkedTour);
      await openStopIn(app, 'Stop files', 'Cart totals');
      await stackScrolledTo('src/pay.ts', 'head', 20);

      fireEvent.click(link('the add'));

      await waitFor(() => expect(stackHeaders()).toEqual(['src/pay.ts', 'src/cart.ts Outside the stop']));
      expect(await stackScrolledTo('src/cart.ts', 'head', 20)).toBe('cart head line 20');
      expect(scrolledItems.at(-1)).toBe(stackItem('src/cart.ts'));
      expect(pressed('Stop files')).toBe(true);

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));

      await waitFor(() => expect(stackHeaders()).toEqual(['src/pay.ts']));
      fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));
      await waitFor(() => expect(stackHeaders()).toEqual(['src/pay.ts']));
   });

   it('scrolls to a stacked file and its line for a ref: link into it, without adding the file again', async () => {
      const { app } = await started(linkedTour);
      await openStopIn(app, 'Stop files', 'Cart totals');
      await stackScrolledTo('src/pay.ts', 'head', 20);

      fireEvent.click(link('the caller'));

      expect(await stackScrolledTo('src/pay.ts', 'head', 30)).toBe('pay line 30');
      expect(stackHeaders()).toEqual(['src/pay.ts']);
      expect(scrolledItems.at(-1)).toBe(stackItem('src/pay.ts'));
   });

   it('scrolls to a file that the reviewer opens from Files when the stack holds it, and adds it at the bottom when it does not', async () => {
      const { app } = await started();
      await openStopIn(app, 'Stop files', 'Cart totals');
      fireEvent.click(tab('Files'));
      fireEvent.click((await filesPanel()).getByText('pay.ts'));
      await waitFor(() => expect(scrolledItems.at(-1)).toBe(stackItem('src/pay.ts')));
      expect(stackHeaders()).toEqual(['src/pay.ts']);

      fireEvent.click((await filesPanel()).getByText('cart.ts'));

      await waitFor(() => expect(stackHeaders()).toEqual(['src/pay.ts', 'src/cart.ts Outside the stop']));
      await waitFor(() => expect(scrolledItems.at(-1)).toBe(stackItem('src/cart.ts')));
      expect(pressed('Stop files')).toBe(true);
   });

   it('follows a revision that reorders, drops and adds the stop’s files without remounting the files it keeps', async () => {
      const { world, seed, app, transport } = await started();
      const claude = await attachClaude(world, seed.tourId);
      await openStopIn(app, 'Stop files');
      await waitFor(() => expect(stackItem('src/pay.ts')?.querySelectorAll('.cm-editor')).toHaveLength(2));
      const payEditor = stackItem('src/pay.ts')!.querySelector('.cm-editor');
      const destroy = vi.spyOn(CodeMirrorSurface.prototype, 'destroy');
      const [caller, primary] = scattered.passages;

      await reviseViewedStop(claude, seed.tourId, seed.stopIds[0]!, {
         ...scattered,
         passages: [
            { ...caller!, role: 'primary' },
            { ...primary!, role: 'related' },
         ],
      });

      await waitFor(() => expect(stackHeaders()).toEqual(['src/pay.ts', 'src/cart.ts']));
      expect(stackItem('src/pay.ts')!.querySelector('.cm-editor')).toBe(payEditor);
      // Only the catalog's editor goes.
      expect(destroy).toHaveBeenCalledTimes(1);
      expect(diffRequests(transport, 'src/pay.ts')).toHaveLength(1);

      await reviseViewedStop(claude, seed.tourId, seed.stopIds[0]!, { ...scattered, passages: [primary, passage('head', 'src/catalog.ts', 3, 4, 'model')] });

      await waitFor(() => expect(stackHeaders()).toEqual(['src/cart.ts', 'src/catalog.ts head']));
      await waitFor(() => expect(drawnLines('src/catalog.ts', 0)).toEqual([1, 2, 3, 4, 5, 6, 7, 0]));
   });

   it('shows the next stop’s files when the stop changes while a file of the first one loads', async () => {
      const { app, transport } = await started();
      await openPinnedTour(app);
      chooseMode('Stop files');
      const release = transport.hold('diff_file');
      fireEvent.click(planStop('The change at a glance'));
      // The stack loads its files in its order, so the held load is the cart's.
      await waitFor(() => expect(transport.requests.find((request) => request.operation === 'diff_file')?.input?.path).toBe('src/cart.ts'));

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await waitFor(() => expect(stackHeaders()).toEqual(['src/pay.ts']));
      const answered = transport.answered('diff_file');
      release();
      await answered;

      expect(await stackScrolledTo('src/pay.ts', 'head', 20)).toBe('pay line 20');
      expect(stackHeaders()).toEqual(['src/pay.ts']);
   });

   it('waits for the changed files before it stacks the stop’s files, and then scrolls to the primary passage', async () => {
      const { app, transport } = await started();
      const release = await openWhileFilesLoad(app, transport, 'The change at a glance');

      expect(screen.getByRole('main', { name: 'Stop files' }).textContent).toContain('Loading the changed files…');
      release();

      await waitFor(() => expect(stackHeaders()).toEqual(['src/cart.ts', 'src/pay.ts', 'src/catalog.ts head']));
      expect(await stackScrolledTo('src/cart.ts', 'head', 5)).toBe('cart line 5');
      expect(transport.requests.filter((request) => request.operation === 'read_file_text' && request.input?.path === 'src/cart.ts')).toEqual([]);
   });

   it('opens the passage that the reviewer opened last in the stack when the mode changes, and loads no single diff before then', async () => {
      const { app, transport } = await started();
      await openStopIn(app, 'Stop files');
      await stackScrolledTo('src/cart.ts', 'head', 5);
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /caller/ }));
      expect(await stackScrolledTo('src/pay.ts', 'head', 30)).toBe('pay line 30');
      expect(diffRequests(transport, 'src/pay.ts')).toHaveLength(1);

      chooseMode('Whole PR');

      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
      expect(diffRequests(transport, 'src/cart.ts')).toHaveLength(1);
   });

   it('opens the file that the reviewer opened last in the stack when a planSet drops the open stop', async () => {
      const { world, seed, app } = await started();
      const claude = await attachClaude(world, seed.tourId);
      await openStopIn(app, 'Stop files');
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /model/ }));
      await stackScrolledTo('src/catalog.ts', 'head', 3);

      await keepStops(claude, seed.tourId, [{ id: seed.stopIds[1]!, title: 'Cart totals', kind: 'inspect' }]);

      expect(await scrolledTo('File', 'src/catalog.ts', 'head', 3)).toBe('catalog line 3');
      expect(screen.queryByRole('list', { name: 'Files in this stop' })).toBeNull();
   });

   it('scrolls to a file opened outside the stop before the changed files arrive, once they arrive, and selects it in Files', async () => {
      const { app, transport } = await started();
      const release = await openWhileFilesLoad(app, transport, 'The old form');

      app.openRepositoryFile('head', 'src/pay.ts');
      release();

      await waitFor(() => expect(stackHeaders()).toEqual(['src/cart.ts', 'src/pay.ts Outside the stop']));
      await waitFor(() => expect(scrolledItems.at(-1)).toBe(stackItem('src/pay.ts')));
      expect(app.stackFilePath).toBe('src/pay.ts');
   });

   it('marks in Files the stacked changed file at the top of the viewport, and no changed file while an unchanged one is there', async () => {
      const { app } = await started();
      await openStopIn(app, 'Stop files');
      fireEvent.click(tab('Files'));
      const panel = await screen.findByRole('tabpanel', { name: 'Files' });

      app.setStackTop('diff:src/pay.ts');
      await waitFor(() => expect(currentRows(panel)).toEqual(['pay.ts, Modified']));
      app.setStackTop('text:head:src/catalog.ts');
      await waitFor(() => expect(currentRows(panel)).toEqual([]));
   });

   it('makes the stacked file of a thread that the reviewer opens the current file, so the current-file filter keeps it', async () => {
      const { world, seed } = await worldOf(notedTour);
      const { app } = await startApp(world);
      await openStopIn(app, 'Stop files');
      app.setStackTop('diff:src/cart.ts');
      await chooseWhere('Current file');
      await waitFor(() => expect(shownIds()).toEqual([seed.cartNote]));

      app.openThread(seed.payNote);

      await waitFor(() => expect(threadViewElement()?.dataset.threadId).toBe(seed.payNote));
      expect(app.stackFilePath).toBe('src/pay.ts');
      await backToList();
      expect(whereOnButton()).toBe('Current file');
      expect(shownIds()).toEqual([seed.payNote]);
   });

   it('keeps the stacked file current and widens the current-file filter for a thread that the reviewer opens about no stacked file', async () => {
      const { world, seed } = await worldOf(notedTour);
      const { app } = await startApp(world);
      await openStopIn(app, 'Stop files');
      app.setStackTop('diff:src/cart.ts');
      await chooseWhere('Current file');
      await waitFor(() => expect(shownIds()).toEqual([seed.cartNote]));

      app.openThread(seed.tourNote);

      await waitFor(() => expect(threadViewElement()?.dataset.threadId).toBe(seed.tourNote));
      expect(app.stackFilePath).toBe('src/cart.ts');
      await backToList();
      expect(whereOnButton()).toBe('Anywhere');
   });

   it('adds a file opened outside the stop once, however often the reviewer opens it', async () => {
      const { app } = await started();
      await openStopIn(app, 'Stop files', 'Cart totals');
      fireEvent.click(tab('Files'));

      fireEvent.click((await filesPanel()).getByText('cart.ts'));
      fireEvent.click((await filesPanel()).getByText('pay.ts'));
      fireEvent.click((await filesPanel()).getByText('cart.ts'));

      await waitFor(() => expect(scrolledItems.at(-1)).toBe(stackItem('src/cart.ts')));
      expect(stackHeaders()).toEqual(['src/pay.ts', 'src/cart.ts Outside the stop']);
   });

   it('shows the single file of the Files tab while no stop is open', async () => {
      const { app } = await started();
      await openPinnedTour(app);
      chooseMode('Stop files');
      fireEvent.click(tab('Files'));

      fireEvent.click((await filesPanel()).getByText('cart.ts'));

      await waitFor(() => expect(screen.getByRole('main', { name: 'Diff' }).querySelector('code')?.textContent).toBe('src/cart.ts'));
      expect(screen.queryByRole('list', { name: 'Files in this stop' })).toBeNull();
   });

   it('opens a tour in stop files when the settings name it the default', async () => {
      localStorage.setItem('lotsen.defaultViewMode', 'stopFiles');
      const { app } = await started();
      await openPinnedTour(app);
      fireEvent.click(planStop('Cart totals'));

      await waitFor(() => expect(stackHeaders()).toEqual(['src/pay.ts']));
      expect(pressed('Stop files')).toBe(true);
   });
});
