import { EditorView } from '@codemirror/view';
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import { CodeMirrorSurface } from '../../src/lib/editor/codeMirrorSurface';
import type { DiffLayout } from '../../src/lib/diffLayout';
import { attachClaude, backend, startApp } from '../harness';
import type { World } from '../backend/backend';
import { resetPageEachTest, worldOf } from './appPanels';
import { openPinnedTour, passage, planStop, reviseViewedStop, scrolledTo, seedStops, spyOnScrolls, stopNavigation, stopPanel, totals, type StopsSeed } from './stopProseWorld';
import { openThreadView } from './threadView';

resetPageEachTest();
const spies = spyOnScrolls();

// A context stop, as its primary passage at the cart's lines 5-7 does not touch the diff.
const linkedContent = {
   headline:
      'See [the check](ref:head/src/cart.ts#L6-6), [the whole add](ref:head/src/cart.ts#L1-10), [the caller](ref:head/src/pay.ts#L31-35), [the old line](ref:base/src/cart.ts#L20-20), ' +
      '[the new line](ref:head/src/cart.ts#L20-20) and [the type](ref:head/src/catalog.ts#L3-4).',
   // The scenario is a block of its own that starts open.
   scenario: '[the guard](ref:head/src/cart.ts#L22-22)',
   passages: [passage('head', 'src/cart.ts', 5, 7), passage('head', 'src/pay.ts', 30, 31, 'caller'), passage('base', 'src/cart.ts', 20, 20, 'before')],
};

const linked = backend().template('refLinks-linked', (world) => seedStops(world, [{ title: 'Cart checks', kind: 'context', content: linkedContent }, totals]));

const link = (name: string) => stopPanel().getByRole('link', { name, hidden: true });
const highlighted = () =>
   stopPanel()
      .queryAllByRole('link', { hidden: true })
      .filter((candidate) => candidate.classList.contains('lotsen-ref-focused'))
      .map((candidate) => candidate.textContent);
const modeSwitch = () => within(screen.getByRole('group', { name: 'View mode' }));
const hover = (name: string) => fireEvent.mouseOver(link(name));
const leave = (name: string) => fireEvent.mouseOut(link(name), { relatedTarget: screen.getByRole('region', { name: 'Stop' }) });

/** The lines of an editor that the hovered reference highlights: line numbers of the document, and the text of each deleted line without its labels. */
function hoveredLines(editorElement: Element): (number | string)[] {
   const editor = EditorView.findFromDOM(editorElement as HTMLElement)!;
   return [...editor.contentDOM.querySelectorAll('.lotsen-reference-hovered')].map((line) =>
      line.classList.contains('cm-deletedLine') ? (line.firstChild?.textContent ?? '') : editor.state.doc.lineAt(editor.posAtDOM(line)).number,
   );
}

const diffEditors = () => [...screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.cm-editor')];

/** Opens the linked stop in the layout and the view mode, on a new world whose tour has the stop first. */
async function openLinkedStop(
   layout: DiffLayout = 'sideBySide',
   mode: 'Whole PR' | 'Step through' | 'Stop files' = 'Whole PR',
   arrange: (world: World, seed: StopsSeed) => Promise<void> = async () => {},
) {
   localStorage.setItem('lotsen.diffLayout', layout);
   const { world, seed } = await worldOf<StopsSeed>(linked);
   await arrange(world, seed);
   const { app } = await startApp(world);
   await openPinnedTour(app);
   if (mode !== 'Whole PR') {
      fireEvent.click(modeSwitch().getByRole('button', { name: mode }));
   }

   fireEvent.click(planStop('Cart checks'));
   await stopPanel().findByRole('heading', { name: 'Cart checks' });
   return { world, app, ...seed };
}

/** Opens the linked stop, and returns the bound session that revises it. */
async function openRevisable() {
   const opened = await openLinkedStop();
   return { ...opened, claude: await attachClaude(opened.world, opened.tourId) };
}

describe('the ref: links and the passages', () => {
   it('highlights exactly the links that overlap the focused passage, and moves the highlight with a passage from the list, a ref: link or step through', async () => {
      await openLinkedStop();
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);

      expect(highlighted()).toEqual(['the check', 'the whole add']);

      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /caller/ }));
      await waitFor(() => expect(highlighted()).toEqual(['the caller']));

      fireEvent.click(link('the old line'));
      await waitFor(() => expect(highlighted()).toEqual(['the old line']));

      fireEvent.click(modeSwitch().getByRole('button', { name: 'Step through' }));
      fireEvent.click(within(screen.getByRole('navigation', { name: 'Passages' })).getByRole('button', { name: 'Previous passage' }));
      await waitFor(() => expect(highlighted()).toEqual(['the caller']));
   });

   it('drops the focused passage’s marker in step through with the highlight off, and keeps the links into it and the hovered lines highlighted', async () => {
      await openLinkedStop('sideBySide', 'Step through');
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      const head = diffEditors().at(-1)!;
      await waitFor(() => expect(head.querySelector('.cm-line.lotsen-passage-focused')).not.toBeNull());

      fireEvent.click(within(screen.getByRole('main', { name: 'Diff' })).getByRole('button', { name: 'Highlight the passages' }));

      await waitFor(() => expect(screen.getByRole('main', { name: 'Diff' }).querySelectorAll('.lotsen-passage, .lotsen-passage-focused')).toHaveLength(0));
      expect(highlighted()).toEqual(['the check', 'the whole add']);
      expect(head.querySelector('.cm-line.lotsen-dimmed')).not.toBeNull();
      expect(modeSwitch().getByRole('button', { name: 'Step through' }).getAttribute('aria-pressed')).toBe('true');
      hover('the new line');
      await waitFor(() => expect(hoveredLines(head)).toEqual([20]));
   });

   it('keeps the focus when a ref: link matches no passage’s lines, and highlights nothing in a stop without passages', async () => {
      const { claude, tourId, stopIds } = await openRevisable();
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);

      fireEvent.click(link('the new line'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20);
      expect(highlighted()).toEqual(['the check', 'the whole add']);

      // Only an overview may have no passages, and the stop is the plan's first, so the revision makes it one.
      await reviseViewedStop(claude, tourId, stopIds[0]!, { ...linkedContent, kind: 'overview', passages: [] });
      await waitFor(() => expect(highlighted()).toEqual([]));
   });

   it('highlights the links of the HTML that a stopPut brings', async () => {
      const { claude, tourId, stopIds } = await openRevisable();
      await waitFor(() => expect(highlighted()).toEqual(['the check', 'the whole add']));

      await reviseViewedStop(claude, tourId, stopIds[0]!, { headline: '[the start](ref:head/src/cart.ts#L4-5) and [the end](ref:head/src/cart.ts#L8-9)', passages: linkedContent.passages });

      await waitFor(() => expect(highlighted()).toEqual(['the start']));
   });

   it.each<DiffLayout>(['sideBySide', 'inline'])('highlights the lines of a hovered link in the open diff until the pointer leaves it, without remounting the editors, %s', async (layout) => {
      await openLinkedStop(layout);
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      const editors = diffEditors();
      const head = editors.at(-1)!;
      const destroy = vi.spyOn(CodeMirrorSurface.prototype, 'destroy');

      hover('the new line');

      await waitFor(() => expect(hoveredLines(head)).toEqual([20]));
      const line20 = head.querySelectorAll('.lotsen-reference-hovered')[0]!;
      expect(line20.classList.contains('lotsen-dimmed')).toBe(false);

      leave('the new line');
      hover('the old line');

      if (layout === 'inline') {
         await waitFor(() => expect(hoveredLines(head)).toEqual(['cart base line 20']));
      } else {
         await waitFor(() => expect(hoveredLines(head)).toEqual([]));
         expect(hoveredLines(editors[0]!)).toEqual([20]);
      }

      leave('the old line');

      await waitFor(() => expect(editors.flatMap(hoveredLines)).toEqual([]));
      expect(head.querySelector('.cm-line.lotsen-dimmed')).not.toBeNull();
      expect(diffEditors()).toEqual(editors);
      expect(destroy).not.toHaveBeenCalled();
   });

   it('highlights nothing for a hovered link into a file that the center does not show', async () => {
      const { app } = await openLinkedStop();
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      const decorated = spies.showPassages().mock.calls.length;

      hover('the type');

      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(app.hoveredReference?.path).toBe('src/catalog.ts');
      expect(diffEditors().flatMap(hoveredLines)).toEqual([]);
      expect(spies.showPassages().mock.calls.length).toBe(decorated);
   });

   it('highlights the lines of a link with keyboard focus until the focus leaves it', async () => {
      await openLinkedStop();
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      const head = diffEditors().at(-1)!;

      // jsdom shows focus as visible only after a key press, as browsers do.
      fireEvent.keyDown(document.body, { key: 'Tab' });
      link('the check').focus();

      await waitFor(() => expect(hoveredLines(head)).toEqual([6]));

      // The pointer over another link wins, and the focused link is hovered again once the pointer leaves.
      hover('the new line');
      await waitFor(() => expect(hoveredLines(head)).toEqual([20]));
      leave('the new line');
      await waitFor(() => expect(hoveredLines(head)).toEqual([6]));

      link('the check').blur();

      await waitFor(() => expect(hoveredLines(head)).toEqual([]));
   });

   describe('with the pointer and the keyboard focus in different blocks', () => {
      const keyboardFocus = (name: string) => {
         // jsdom shows focus as visible only after a key press, as browsers do.
         fireEvent.keyDown(document.body, { key: 'Tab' });
         link(name).focus();
      };

      async function openWithFocus(name: string, opened: Promise<unknown> = openLinkedStop()) {
         await opened;
         await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
         keyboardFocus(name);
         return diffEditors().at(-1)!;
      }

      it('shows the focused link in one block again when the pointer leaves a link in another', async () => {
         const head = await openWithFocus('the check');
         await waitFor(() => expect(hoveredLines(head)).toEqual([6]));

         hover('the guard');
         await waitFor(() => expect(hoveredLines(head)).toEqual([22]));
         leave('the guard');

         await waitFor(() => expect(hoveredLines(head)).toEqual([6]));
      });

      it('keeps the pointer’s link while the keyboard moves or leaves in another block, and shows the focused link once the pointer leaves', async () => {
         const head = await openWithFocus('the check');
         hover('the guard');
         await waitFor(() => expect(hoveredLines(head)).toEqual([22]));

         keyboardFocus('the new line');
         await new Promise((resolve) => setTimeout(resolve, 20));
         expect(hoveredLines(head)).toEqual([22]);

         leave('the guard');
         await waitFor(() => expect(hoveredLines(head)).toEqual([20]));

         hover('the guard');
         await waitFor(() => expect(hoveredLines(head)).toEqual([22]));
         link('the new line').blur();
         await new Promise((resolve) => setTimeout(resolve, 20));
         expect(hoveredLines(head)).toEqual([22]);
      });

      it('keeps the focused link of one block when a stopPut replaces the hovered link of another', async () => {
         const opened = openRevisable();
         const head = await openWithFocus('the check', opened);
         const { claude, tourId, stopIds } = await opened;
         hover('the guard');
         await waitFor(() => expect(hoveredLines(head)).toEqual([22]));

         await reviseViewedStop(claude, tourId, stopIds[0]!, { ...linkedContent, scenario: 'No links.' });

         await waitFor(() => expect(hoveredLines(head)).toEqual([6]));
      });

      it('shows the focused link of the stop again when the pointer leaves a link in a reply', async () => {
         // Claude's reply to a question about the tour links to a line of the cart.
         await openLinkedStop('sideBySide', 'Whole PR', async (world, { tourId }) => {
            const claude = await attachClaude(world, tourId);
            await world.reviewer.call('ask_question', { tourId, text: 'Where?', anchor: { kind: 'tour' } });
            const question = await claude.next('question');
            await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: '[the reply line](ref:head/src/cart.ts#L22-22)' });
         });
         await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
         const head = diffEditors().at(-1)!;
         await openThreadView();
         keyboardFocus('the check');
         await waitFor(() => expect(hoveredLines(head)).toEqual([6]));
         const threads = screen.getByRole('region', { name: 'Threads' });
         const reply = await within(threads).findByRole('link', { name: 'the reply line', hidden: true });

         fireEvent.mouseOver(reply);
         await waitFor(() => expect(hoveredLines(head)).toEqual([22]));
         fireEvent.mouseOut(reply, { relatedTarget: threads });

         await waitFor(() => expect(hoveredLines(head)).toEqual([6]));
      });

      it('stops highlighting a focused link whose section closes', async () => {
         const head = await openWithFocus('the guard');
         await waitFor(() => expect(hoveredLines(head)).toEqual([22]));

         // A click that does not move the focus, as in Safari, which does not focus a button on a click.
         fireEvent.click(stopPanel().getByRole('button', { name: 'Scenario' }));

         await waitFor(() => expect(hoveredLines(head)).toEqual([]));
         expect(document.activeElement).toBe(link('the guard'));
      });
   });

   it('stops highlighting a clicked link once the pointer leaves it, though the click focused it', async () => {
      await openLinkedStop();
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      const head = diffEditors().at(-1)!;

      hover('the new line');
      fireEvent.mouseDown(link('the new line'));
      link('the new line').focus();
      fireEvent.click(link('the new line'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20);
      expect(hoveredLines(head)).toEqual([20]);

      leave('the new line');

      await waitFor(() => expect(hoveredLines(head)).toEqual([]));
      expect(document.activeElement).toBe(link('the new line'));
   });

   it('forgets the hovered link when the stop changes or the reviewer shows a revision that replaces the link', async () => {
      const { app, claude, tourId, stopIds } = await openRevisable();
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      hover('the check');
      await waitFor(() => expect(app.hoveredReference?.startLine).toBe(6));

      await reviseViewedStop(claude, tourId, stopIds[0]!, { headline: 'No links.', passages: linkedContent.passages });

      await waitFor(() => expect(app.hoveredReference).toBeNull());

      await reviseViewedStop(claude, tourId, stopIds[0]!, linkedContent);
      hover((await stopPanel().findByRole('link', { name: 'the check', hidden: true })).textContent);
      await waitFor(() => expect(app.hoveredReference?.startLine).toBe(6));

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));

      await waitFor(() => expect(app.hoveredReference).toBeNull());
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
      expect(diffEditors().flatMap(hoveredLines)).toEqual([]);
   });

   describe('in the stop-files mode', () => {
      const stacked = (path: string) =>
         [...screen.getByRole('list', { name: 'Files in this stop' }).querySelectorAll(':scope > li')].find((item) => item.querySelector('header code')?.textContent === path)!;

      /** The line numbers that an editor of a stacked file draws, with each collapsed marker as 0. */
      function drawnLines(editorElement: Element): number[] {
         const editor = EditorView.findFromDOM(editorElement as HTMLElement)!;
         return [...editor.contentDOM.children]
            .filter((element) => element.classList.contains('cm-line') || element.classList.contains('lotsen-collapsed'))
            .map((element) => (element.classList.contains('cm-line') ? editor.state.doc.lineAt(editor.posAtDOM(element)).number : 0));
      }

      it.each<DiffLayout>(['sideBySide', 'inline'])('highlights the lines of a hovered link in the stacked file it points into, keeping the collapse and the editors, %s', async (layout) => {
         await openLinkedStop(layout, 'Stop files');
         for (const path of ['src/pay.ts', 'src/cart.ts']) {
            await waitFor(() => expect(stacked(path)?.querySelectorAll('.cm-editor')).toHaveLength(layout === 'inline' ? 1 : 2));
         }

         const pay = stacked('src/pay.ts').querySelectorAll('.cm-editor');
         const payHead = pay[pay.length - 1]!;
         const cartEditors = [...stacked('src/cart.ts').querySelectorAll('.cm-editor')];
         await waitFor(() => expect(drawnLines(payHead)).toEqual([0, 27, 28, 29, 30, 31, 32, 33, 34, 0]));
         const destroy = vi.spyOn(CodeMirrorSurface.prototype, 'destroy');

         hover('the caller');

         // Line 35 stays collapsed, because only the stop's passages decide what collapses.
         await waitFor(() => expect(hoveredLines(payHead)).toEqual([31, 32, 33, 34]));
         expect(drawnLines(payHead)).toEqual([0, 27, 28, 29, 30, 31, 32, 33, 34, 0]);
         expect(cartEditors.flatMap(hoveredLines)).toEqual([]);

         leave('the caller');
         hover('the check');

         await waitFor(() => expect(hoveredLines(cartEditors.at(-1)!)).toEqual([6]));
         expect(hoveredLines(payHead)).toEqual([]);

         leave('the check');

         await waitFor(() => expect(cartEditors.flatMap(hoveredLines)).toEqual([]));
         expect([...stacked('src/pay.ts').querySelectorAll('.cm-editor')]).toEqual([...pay]);
         expect(destroy).not.toHaveBeenCalled();
      });
   });
});
