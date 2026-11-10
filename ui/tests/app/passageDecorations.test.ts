import { EditorView } from '@codemirror/view';
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CodeMirrorSurface } from '../../src/lib/editor/codeMirrorSurface';
import { backend, openTour, startApp } from '../harness';
import { filesPanel, planPanel, resetPageEachTest, tab, worldOf } from './appPanels';
import { buildTour, commitFiles, createTour, openPinnedTour, passage, scrolledTo, type EditorSpies } from './fourStopTour';
import { spyOnFocus } from './editorFocus';

resetPageEachTest();

const spies = {} as EditorSpies;
beforeEach(() => {
   spies.focus = spyOnFocus();
});
afterEach(() => vi.restoreAllMocks());

const four = backend().template('passageDecorations-four', (world) => buildTour(world));

// A context stop on the unchanged catalog with a passage at each side.
const bothSides = backend().template('passageDecorations-bothSides', (world) =>
   buildTour(world, [
      {
         title: 'Both sides',
         kind: 'context',
         content: { headline: 'The catalog keeps its model.', passages: [passage('head', 'src/catalog.ts', 3, 4), passage('base', 'src/catalog.ts', 10, 11, 'old')] },
      },
   ]),
);

// Two tours of the same change, of which the one on pull request 43 is the newer.
const twoTours = backend().template('passageDecorations-twoTours', async (world) => {
   await commitFiles(world);
   const older = await createTour(world, await world.claude());
   await world.clock.advance(1000);
   const newer = await createTour(world, await world.claude(), undefined, 'https://platform.test/shop/pull/43');
   return { older, newer };
});

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
const planStop = (title: string) => planPanel().getByRole('button', { name: new RegExp(title) });

async function openFour() {
   const { world } = await worldOf(four);
   const started = await startApp(world);
   await openPinnedTour(started.app);
   return started;
}

describe('the passage decorations', () => {
   /** The mark of each line that an editor of the open file draws, by line number: the passage role, or dimmed, and the labels. */
   function marks(view: 'Diff' | 'File', index = 0): Map<number, string> {
      const editor = EditorView.findFromDOM(screen.getByRole('main', { name: view }).querySelectorAll<HTMLElement>('.cm-editor')[index]!)!;
      return new Map(
         [...editor.contentDOM.querySelectorAll(':scope > .cm-line')].map((line) => {
            const role = ['primary', 'related', 'dimmed'].find((name) => line.classList.contains(name === 'dimmed' ? 'lotsen-dimmed' : `lotsen-passage-${name}`)) ?? '';
            const labels = [...line.querySelectorAll('.lotsen-passageLabel')].map((label) => ` ${label.textContent}`).join('');
            return [editor.state.doc.lineAt(editor.posAtDOM(line)).number, role + labels];
         }),
      );
   }

   it('dims the open file outside the open stop’s passages and highlights them at each side, and follows the next stop in the same editors', async () => {
      const destroy = vi.spyOn(CodeMirrorSurface.prototype, 'destroy');
      await openFour();
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);

      await waitFor(() => expect([4, 5, 6, 7, 8].map((line) => marks('Diff', 1).get(line))).toEqual(['dimmed', 'primary', 'primary', 'primary', 'dimmed']));
      expect([19, 20, 21].map((line) => marks('Diff', 0).get(line))).toEqual(['dimmed', 'related before', 'dimmed']);

      fireEvent.click(planStop('The old form'));

      await waitFor(() => expect([5, 20].map((line) => marks('Diff', 1).get(line))).toEqual(['dimmed', 'primary']));
      expect(marks('Diff', 0).get(20)).toBe('dimmed');
      expect(destroy).not.toHaveBeenCalled();
   });

   it('leaves a file without a passage of the open stop undimmed, and decorates a passage in an unchanged file', async () => {
      await openFour();
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
      await waitFor(() => expect([19, 20].map((line) => marks('Diff', 1).get(line))).toEqual(['dimmed', 'primary']));

      fireEvent.click(tab('Files'));
      fireEvent.click((await filesPanel()).getByText('cart.ts'));
      await waitFor(() => expect(screen.getByRole('main', { name: 'Diff' }).querySelector('code')?.textContent).toBe('src/cart.ts'));
      await waitFor(() => expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.cm-line').length).toBeGreaterThan(0));
      expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.lotsen-dimmed, .lotsen-passage')).toHaveLength(0);

      fireEvent.click(tab('Plan'));
      fireEvent.click(planStop('The change at a glance'));
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /model/ }));
      await scrolledTo(spies, 'File', 'src/catalog.ts', 'head', 3);

      await waitFor(() => expect([2, 3, 4, 5].map((line) => marks('File').get(line))).toEqual(['dimmed', 'related model', 'related', 'dimmed']));
   });

   it('dims nothing once the reviewer switches dimming off, in the diff and in a file view, and keeps the passages, the view mode and the choice', async () => {
      const dimToggle = (view: 'Diff' | 'File') => within(screen.getByRole('main', { name: view })).getByRole('button', { name: 'Dim outside the passages' });
      const wholePr = () => within(screen.getByRole('group', { name: 'View mode' })).getByRole('button', { name: 'Whole PR' });
      await openFour();
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      await waitFor(() => expect([4, 5].map((line) => marks('Diff', 1).get(line))).toEqual(['dimmed', 'primary']));
      expect(dimToggle('Diff').getAttribute('aria-pressed')).toBe('true');
      expect(wholePr().getAttribute('aria-pressed')).toBe('true');

      fireEvent.click(dimToggle('Diff'));

      await waitFor(() => expect([4, 5, 6, 7, 8].map((line) => marks('Diff', 1).get(line))).toEqual(['', 'primary', 'primary', 'primary', '']));
      expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.lotsen-dimmed')).toHaveLength(0);
      expect(marks('Diff', 0).get(20)).toBe('related before');
      expect(dimToggle('Diff').getAttribute('aria-pressed')).toBe('false');
      expect(wholePr().getAttribute('aria-pressed')).toBe('true');
      expect(localStorage.getItem('lotsen.dimming')).toBe('false');

      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /model/ }));
      await scrolledTo(spies, 'File', 'src/catalog.ts', 'head', 3);
      await waitFor(() => expect([2, 3, 4, 5].map((line) => marks('File').get(line))).toEqual(['', 'related model', 'related', '']));
      expect(dimToggle('File').getAttribute('aria-pressed')).toBe('false');

      fireEvent.click(dimToggle('File'));

      await waitFor(() => expect([2, 3, 4, 5].map((line) => marks('File').get(line))).toEqual(['dimmed', 'related model', 'related', 'dimmed']));
      expect(localStorage.getItem('lotsen.dimming')).toBe('true');
   });

   it('drops the passages’ highlight once the reviewer switches it off, keeps their labels, the dimming and the view mode, and remembers the choice', async () => {
      const toggle = () => within(screen.getByRole('main', { name: 'Diff' })).getByRole('button', { name: 'Highlight the passages' });
      await openFour();
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      await waitFor(() => expect([4, 5].map((line) => marks('Diff', 1).get(line))).toEqual(['dimmed', 'primary']));
      expect(toggle().getAttribute('aria-pressed')).toBe('true');

      fireEvent.click(toggle());

      await waitFor(() => expect([4, 5, 6, 7, 8].map((line) => marks('Diff', 1).get(line))).toEqual(['dimmed', '', '', '', 'dimmed']));
      expect(marks('Diff', 0).get(20)).toBe(' before');
      expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.lotsen-passage')).toHaveLength(0);
      expect(toggle().getAttribute('aria-pressed')).toBe('false');
      expect(
         within(screen.getByRole('group', { name: 'View mode' }))
            .getByRole('button', { name: 'Whole PR' })
            .getAttribute('aria-pressed'),
      ).toBe('true');
      expect(localStorage.getItem('lotsen.highlight')).toBe('false');

      fireEvent.click(toggle());

      await waitFor(() => expect(marks('Diff', 1).get(5)).toBe('primary'));
      expect(localStorage.getItem('lotsen.highlight')).toBe('true');
   });

   it('starts without the passages’ highlight when the reviewer switched it off before', async () => {
      localStorage.setItem('lotsen.highlight', 'false');
      await openFour();
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);

      await waitFor(() => expect([4, 5].map((line) => marks('Diff', 1).get(line))).toEqual(['dimmed', '']));
      expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.lotsen-passage')).toHaveLength(0);
   });

   describe('of an unchanged file with passages at both sides', () => {
      const catalogMarks = () => [2, 3, 4, 5, 9, 10, 11, 12].map((line) => marks('File').get(line));
      const bothDecorated = ['dimmed', 'primary', 'primary', 'dimmed', 'dimmed', 'related old', 'related', 'dimmed'];

      it('decorates the passages of both sides with their roles at either side', async () => {
         const { world } = await worldOf(bothSides);
         const { app } = await startApp(world);
         await openPinnedTour(app);
         fireEvent.click(planStop('Both sides'));
         await scrolledTo(spies, 'File', 'src/catalog.ts', 'head', 3);
         await waitFor(() => expect(catalogMarks()).toEqual(bothDecorated));

         fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /old/ }));
         await scrolledTo(spies, 'File', 'src/catalog.ts', 'base', 10);
         await waitFor(() => expect(catalogMarks()).toEqual(bothDecorated));
      });

      it('decorates only the open side’s passages while the changed files load, and both sides’ once they show the file unchanged', async () => {
         const { world } = await worldOf(bothSides);
         const { app, transport } = await startApp(world);
         const release = transport.hold('list_changed_files', 'response');
         await openTour(app);
         fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
         fireEvent.click(await planPanel().findByRole('button', { name: /Both sides/ }));
         await stopPanel().findByRole('heading', { name: 'Both sides' });
         app.openRepositoryFile('head', 'src/catalog.ts');
         await waitFor(() => expect(catalogMarks()).toEqual(['dimmed', 'primary', 'primary', 'dimmed', 'dimmed', 'dimmed', 'dimmed', 'dimmed']));
         expect(app.selectedFiles?.state).toBe('loading');

         release();

         await waitFor(() => expect(catalogMarks()).toEqual(bothDecorated));
      });
   });

   it('starts without dimming when the reviewer switched it off before', async () => {
      localStorage.setItem('lotsen.dimming', 'false');
      await openFour();
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);

      await waitFor(() => expect([4, 5].map((line) => marks('Diff', 1).get(line))).toEqual(['', 'primary']));
      expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.lotsen-dimmed')).toHaveLength(0);
   });
});

describe('collapsing to the passages', () => {
   /** The line numbers that an editor of the diff draws, with each marker as 0. */
   function drawnLines(index: number): number[] {
      const editor = EditorView.findFromDOM(screen.getByRole('main', { name: 'Diff' }).querySelectorAll<HTMLElement>('.cm-editor')[index]!)!;
      return [...editor.contentDOM.children]
         .filter((element) => element.classList.contains('cm-line') || element.classList.contains('lotsen-collapsed'))
         .map((element) => (element.classList.contains('cm-line') ? editor.state.doc.lineAt(editor.posAtDOM(element)).number : 0));
   }

   it('starts with every region shown, collapses the diff to the open stop’s passages when the reviewer asks, and collapses the next stop’s passages in the same editors', async () => {
      const destroy = vi.spyOn(CodeMirrorSurface.prototype, 'destroy');
      await openFour();
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      const diff = screen.getByRole('main', { name: 'Diff' });
      await waitFor(() => expect(drawnLines(1).slice(0, 3)).toEqual([1, 2, 3]));
      expect(diff.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);

      fireEvent.click(within(diff).getByRole('button', { name: 'Collapse to passages' }));

      // The head's lines 5 to 7 and the base's line 20 stay with three lines of context in both panes.
      const glance = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0, 17, 18, 19, 20, 21, 22, 23, 0];
      await waitFor(() => expect(drawnLines(1)).toEqual(glance));
      expect(drawnLines(0)).toEqual(glance);

      fireEvent.click(planStop('The old form'));

      const old = [0, 17, 18, 19, 20, 21, 22, 23, 0];
      await waitFor(() => expect(drawnLines(1)).toEqual(old));
      expect(drawnLines(0)).toEqual(old);
      expect(destroy).not.toHaveBeenCalled();

      fireEvent.click(within(diff).getByRole('button', { name: 'Collapse to passages' }));

      await waitFor(() => expect(diff.querySelectorAll('.lotsen-collapsed')).toHaveLength(0));
      expect(drawnLines(1).slice(0, 3)).toEqual([1, 2, 3]);
   });

   it('shows every region again when another tour opens in the same view mode', async () => {
      const { world } = await worldOf(twoTours);
      const { app } = await startApp(world);
      await openPinnedTour(app, 'shop #43');
      fireEvent.click(planStop('The change at a glance'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      const diff = screen.getByRole('main', { name: 'Diff' });
      fireEvent.click(within(diff).getByRole('button', { name: 'Collapse to passages' }));
      await waitFor(() => expect(diff.querySelectorAll('.lotsen-collapsed').length).toBeGreaterThan(0));

      await openPinnedTour(app, 'shop #42');
      fireEvent.click(await planPanel().findByRole('button', { name: /The change at a glance/ }));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);

      expect(app.viewMode).toBe('wholePr');
      expect(diff.querySelectorAll('.lotsen-collapsed')).toHaveLength(0);
      expect(within(diff).getByRole('button', { name: 'Collapse to passages' }).getAttribute('aria-pressed')).toBe('false');
   });
});
