import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import { codeMirrorSurfaces } from '../../src/lib/editor/codeMirrorSurface';
import { CodeMirrorSurface } from '../../src/lib/editor/codeMirrorSurface';
import type { DiffLayout } from '../../src/lib/diffLayout';
import { backend, startApp } from '../harness';
import type { Template, Transport } from '../backend/backend';
import { planPanel, resetPageEachTest, worldOf } from './appPanels';
import { glance, numbered, openPinnedTour, passage, planStop, rounding, scrolledTo, seedStops, spyOnScrolls, stopNavigation, stopPanel, type PlannedStop, type StopsSeed } from './stopProseWorld';

resetPageEachTest();
const spies = spyOnScrolls();

const headline = 'Totals round to [the sum](ref:head/src/pay.ts#L21-21), as [the cart](ref:head/src/cart.ts#L20-20) asks.';
const short: PlannedStop = { title: 'Cart totals', kind: 'inspect', content: { headline, why: 'Sums drifted by a cent.', passages: [passage('head', 'src/pay.ts', 20, 22)] } };
// The payment's head grows past the server's 1 MiB limit by a line after its forty, so its diff loads only when the reviewer asks.
const largePay = numbered('pay', 'head', 21) + 'x'.repeat(1_100_000) + '\n';

const placement = backend().template('explanationPlacement-plan', (world) => seedStops(world, [glance, short, rounding]));
const largePlacement = backend().template('explanationPlacement-large', (world) => seedStops(world, [glance, short, rounding], { payHead: largePay }));

const diff = () => screen.getByRole('main', { name: /Diff|Stop files/ });
const blocks = () => [...diff().querySelectorAll<HTMLElement>('.lotsen-explanation')];
const block = () => within(blocks()[0]!);
const note = 'The explanation shows in the code, below the primary passage.';
const fallbackNote = 'Shows in the code once its file shows the primary passage.';

/** The text of the line that the block follows in its editor. */
function lineAboveBlock(): string | undefined {
   return blocks()[0]!.previousElementSibling?.textContent ?? undefined;
}

/** Opens the tour of the template in the layout and the placement, with `arrange` given the page's network first, and returns the world and the app. */
async function openPlacedTour(template: Template<StopsSeed>, { layout = 'inline', mode = 'mixed' }: { layout?: DiffLayout; mode?: string } = {}) {
   localStorage.setItem('lotsen.diffLayout', layout);
   localStorage.setItem('lotsen.placement', mode);
   const { world, seed } = await worldOf(template);
   const { app, transport } = await startApp(world);
   await openPinnedTour(app);
   return { world, app, transport, ...seed };
}

async function openPlacedStop(title: string, options: { layout?: DiffLayout; mode?: string; arrange?: (transport: Transport) => void } = {}) {
   const opened = await openPlacedTour(placement, options);
   options.arrange?.(opened.transport);
   fireEvent.click(planStop(title));
   await stopPanel().findByRole('heading', { name: title });
   return opened;
}

const diffRequested = (transport: Transport) => transport.requests.some((request) => request.operation === 'diff_file' && request.input?.path === 'src/pay.ts');

describe('the placement of the explanation', () => {
   it('shows a short stop on one file inline below its primary passage, with its headline and sections, and a note in the Stop panel, in mixed placement with an inline diff', async () => {
      await openPlacedStop('Cart totals');
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);

      await waitFor(() => expect(blocks()).toHaveLength(1));
      expect(lineAboveBlock()).toBe('pay line 22');
      expect(block().getByRole('heading', { name: 'Cart totals' })).toBeTruthy();
      expect(blocks()[0]!.textContent).toContain('Totals round to the sum, as the cart asks.');
      const why = block().getByRole('button', { name: 'Why' });
      expect(why.getAttribute('aria-expanded')).toBe('true');
      expect(document.getElementById(why.getAttribute('aria-controls')!)!.hidden).toBe(false);
      expect(stopPanel().getByText(note)).toBeTruthy();
      expect(stopPanel().queryByText(/Totals round/)).toBeNull();
      expect(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /src\/pay\.ts/ })).toBeTruthy();
   });

   it('opens the code reference of a ref: link in the block, highlights the links into the focused passage, and reports a hovered link', async () => {
      const { app } = await openPlacedStop('Cart totals');
      await waitFor(() => expect(blocks()).toHaveLength(1));
      const link = (name: string) => block().getByRole('link', { name });

      await waitFor(() => expect(link('the sum').classList.contains('lotsen-ref-focused')).toBe(true));
      expect(link('the cart').classList.contains('lotsen-ref-focused')).toBe(false);

      fireEvent.mouseOver(link('the sum'));
      expect(app.hoveredReference).toEqual({ side: 'head', path: 'src/pay.ts', startLine: 21, endLine: 21 });
      await waitFor(() => expect(diff().querySelector('.cm-line.lotsen-reference-hovered')?.textContent).toBe('pay head line 21'));
      fireEvent.mouseOut(link('the sum'), { relatedTarget: blocks()[0] });
      expect(app.hoveredReference).toBeNull();

      fireEvent.click(link('the cart'));

      expect(await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20)).toBe('cart head line 20');
      await waitFor(() => expect(blocks()).toHaveLength(0));
      // The open file lacks the primary passage, so the Stop panel shows the explanation meanwhile.
      expect(await stopPanel().findByText(/Totals round to/)).toBeTruthy();
      expect(stopPanel().queryByText(note)).toBeNull();
      expect(stopPanel().getByText(fallbackNote)).toBeTruthy();
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /src\/pay\.ts/ }));
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
      await waitFor(() => expect(lineAboveBlock()).toBe('pay line 22'));
      expect(await stopPanel().findByText(note)).toBeTruthy();
      fireEvent.click(stopPanel().getByRole('button', { name: 'Go to the explanation' }));
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
   });

   it('shows the explanation in the Stop panel while the primary file’s diff loads, and below the passage once the diff shows', async () => {
      let release!: () => void;
      await openPlacedStop('Cart totals', { mode: 'inline', arrange: (transport) => (release = transport.hold('diff_file')) });

      expect(await stopPanel().findByText(/Totals round to/)).toBeTruthy();
      expect(stopPanel().queryByText(note)).toBeNull();
      expect(blocks()).toEqual([]);
      release();

      await waitFor(() => expect(lineAboveBlock()).toBe('pay line 22'));
      expect(await stopPanel().findByText(note)).toBeTruthy();
      expect(stopPanel().queryByText(/Totals round to/)).toBeNull();
   });

   it('keeps the explanation in the Stop panel when the diff fails to load, in inline placement', async () => {
      const { transport } = await openPlacedStop('Cart totals', { mode: 'inline', arrange: (network) => network.fail('diff_file', { status: 500 }) });

      await waitFor(() => expect(diffRequested(transport)).toBe(true));
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(stopPanel().getByText(/Totals round to/)).toBeTruthy();
      expect(stopPanel().queryByText(note)).toBeNull();
      expect(blocks()).toEqual([]);
   });

   it('keeps the explanation in the Stop panel while a large diff is not loaded, and moves it below the passage once the reviewer loads it', async () => {
      await openPlacedTour(largePlacement, { mode: 'inline' });

      fireEvent.click(planStop('Cart totals'));

      const load = await within(diff()).findByRole('button', { name: 'Load the diff' });
      expect(stopPanel().getByText(/Totals round to/)).toBeTruthy();
      expect(blocks()).toEqual([]);
      fireEvent.click(load);
      await waitFor(() => expect(lineAboveBlock()).toBe('pay line 22'), { timeout: 10_000 });
      expect(await stopPanel().findByText(note)).toBeTruthy();
   });

   it('keeps the explanation in the Stop panel when the diff cannot be shown', async () => {
      await openPlacedTour(placement, { mode: 'inline' });
      vi.spyOn(codeMirrorSurfaces, 'diff').mockRejectedValue(new Error('No editor.'));

      fireEvent.click(planStop('Cart totals'));
      await stopPanel().findByRole('heading', { name: 'Cart totals' });

      expect(await within(diff()).findByText('The diff could not be shown.')).toBeTruthy();
      expect(stopPanel().getByText(/Totals round to/)).toBeTruthy();
      expect(stopPanel().queryByText(note)).toBeNull();
   });

   describe('of a check that the reviewer goes to from the summary page', () => {
      const checked = (passages: Record<string, unknown>[], hint?: 'inline' | 'side'): PlannedStop => ({
         title: 'Cart totals',
         kind: 'inspect',
         content: { headline: 'Totals round to the sum.', passages, checks: [{ question: 'Is the cent right?' }], ...(hint === undefined ? {} : { placement: hint }) },
      });
      const onPay = [passage('head', 'src/pay.ts', 20, 22)];
      const checkTour = (name: string, stop: PlannedStop, payHead?: string) =>
         backend().template(`explanationPlacement-check-${name}`, (world) => seedStops(world, [stop], { draftAnswer: 'Yes.', ...(payHead === undefined ? {} : { payHead }) }));
      const plain = checkTour('plain', checked(onPay));
      const inlineHint = checkTour('inline', checked(onPay, 'inline'));
      const sideHint = checkTour('side', checked(onPay, 'side'));
      // Exactly one passage of a stop with passages is primary, so a stop without a primary passage is an overview without passages.
      const unplaced = checkTour('none', { ...checked([]), kind: 'overview' });
      const large = checkTour('large', checked(onPay), largePay);
      const focusedCheck = () => document.activeElement?.closest<HTMLElement>('[data-check-id]') ?? null;

      /**
       * Opens the stop's check from the summary page while the primary file's diff is held, and returns the release of the diff and the check's ID. A failure of the diff answers once
       * the release runs, which a gate in front of the transport's fault keeps.
       */
      async function goToCheck(template: Template<StopsSeed>, mode: string, fails = false): Promise<{ release: () => void; checkId: string }> {
         localStorage.setItem('lotsen.diffLayout', 'inline');
         localStorage.setItem('lotsen.placement', mode);
         const { world, seed } = await worldOf(template);
         const network = world.transport();
         let release = () => {};
         const gate = new Promise<void>((resolve) => (release = resolve));
         let gated = false;
         const transport: Transport = {
            ...network,
            requests: network.requests,
            fetch: async (input, init) => {
               if (fails && !gated && String(input).endsWith('/api/ops/diff_file')) {
                  gated = true;
                  await gate;
               }

               return network.fetch(input, init);
            },
         };
         const { app } = await startApp(world, { transport });
         await openPinnedTour(app);
         fireEvent.click(planPanel().getByRole('button', { name: 'Summary' }));
         const goTo = await screen.findByRole('button', { name: 'Go to the check' });
         if (fails) {
            network.fail('diff_file', { status: 500 });
         } else {
            release = network.hold('diff_file');
         }

         fireEvent.click(goTo);
         await stopPanel().findByRole('heading', { name: 'Cart totals' });
         return { release, checkId: seed.checkIds[0]![0]! };
      }

      it.each([
         ['in inline placement', plain, 'inline'],
         ['in mixed placement with an inline hint', inlineHint, 'mixed'],
      ] as const)("focuses it in the inline block once that replaces the Stop panel's stand-in, %s", async (_, template, mode) => {
         const { release, checkId } = await goToCheck(template, mode);
         expect(await stopPanel().findByText(fallbackNote)).toBeTruthy();
         await new Promise((resolve) => setTimeout(resolve, 20));
         expect(focusedCheck()).toBeNull();

         release();

         await waitFor(() => expect(blocks()).toHaveLength(1));
         await waitFor(() => expect(focusedCheck()?.closest('.lotsen-explanation')).toBe(blocks()[0]));
         expect(focusedCheck()?.dataset.checkId).toBe(checkId);
      });

      it.each([
         ['in inline placement for a stop without a primary passage', unplaced, 'inline'],
         ['in mixed placement with a side hint', sideHint, 'mixed'],
      ] as const)('focuses it in the Stop panel %s', async (_, template, mode) => {
         const { release, checkId } = await goToCheck(template, mode);
         release();

         await waitFor(() => expect(focusedCheck()?.dataset.checkId).toBe(checkId));
         expect(stopPanel().getByText('Is the cent right?').closest('[data-check-id]')).toBe(focusedCheck());
         expect(blocks()).toEqual([]);
      });

      it.each([
         ['fails to load', plain, true],
         ['is large and waits for "Load the diff"', large, false],
      ] as const)("focuses it in the Stop panel's stand-in in inline placement when the primary file's diff %s", async (_, template, fails) => {
         const { release, checkId } = await goToCheck(template, 'inline', fails);
         expect(await stopPanel().findByText(fallbackNote)).toBeTruthy();
         await new Promise((resolve) => setTimeout(resolve, 20));
         expect(focusedCheck()).toBeNull();

         release();

         await waitFor(() => expect(focusedCheck()?.dataset.checkId).toBe(checkId));
         expect(stopPanel().getByText('Is the cent right?').closest('[data-check-id]')).toBe(focusedCheck());
         expect(blocks()).toEqual([]);
      });

      it('focuses it in the Stop panel when the reviewer keeps the stand-in there before the block shows', async () => {
         const { release, checkId } = await goToCheck(inlineHint, 'mixed');
         expect(await stopPanel().findByText(fallbackNote)).toBeTruthy();

         fireEvent.click(stopPanel().getByRole('button', { name: 'Keep here' }));

         await waitFor(() => expect(focusedCheck()?.dataset.checkId).toBe(checkId));
         const check = focusedCheck();
         release();
         await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
         expect(blocks()).toEqual([]);
         expect(focusedCheck()).toBe(check);
      });
   });

   it('shows an overview, a risk and a stop on two files in the Stop panel in mixed placement', async () => {
      await openPlacedStop('The change at a glance');
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);

      expect(stopPanel().getByText('The cart checks its quantities.')).toBeTruthy();
      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
      await waitFor(() => expect(blocks()).toHaveLength(1));
      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));

      expect(await stopPanel().findByText('Rounding can hide a cent.')).toBeTruthy();
      await waitFor(() => expect(blocks()).toHaveLength(0));
   });

   it('shows the stop in the Stop panel when its body is over the mixed threshold', async () => {
      localStorage.setItem('lotsen.mixedThreshold', String('Sums drifted by a cent.'.length - 1));

      await openPlacedStop('Cart totals');
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);

      expect(stopPanel().getByText(/Totals round to/)).toBeTruthy();
      expect(blocks()).toEqual([]);
   });

   it('shows the stop in the Stop panel when the diff is side by side in mixed placement', async () => {
      await openPlacedStop('Cart totals', { layout: 'sideBySide' });
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);

      expect(stopPanel().getByText(/Totals round to/)).toBeTruthy();
      expect(blocks()).toEqual([]);
   });

   it('shows a stop whose primary passage is in a file that the pull request does not change inline, as that file shows in one pane, side by side too', async () => {
      const catalog = backend().template('explanationPlacement-catalog', (world) =>
         seedStops(world, [{ title: 'The product type', kind: 'context', content: { headline: 'Products keep their type.', passages: [passage('head', 'src/catalog.ts', 3, 4)] } }]),
      );
      await openPlacedTour(catalog, { layout: 'sideBySide' });

      fireEvent.click(planStop('The product type'));

      expect(await scrolledTo(spies, 'File', 'src/catalog.ts', 'head', 3)).toBe('catalog line 3');
      await waitFor(() => expect(screen.getByRole('main', { name: 'File' }).querySelector('.lotsen-explanation')?.previousElementSibling?.textContent).toBe('catalog line 4'));
      expect(await stopPanel().findByText(note)).toBeTruthy();
   });

   it('moves a stop to where the reviewer asks and keeps it there for the stop, over Claude’s hint and the rule', async () => {
      await openPlacedStop('Cart totals');
      await waitFor(() => expect(blocks()).toHaveLength(1));

      fireEvent.click(block().getByRole('button', { name: 'Show in the Stop panel' }));

      expect(await stopPanel().findByText(/Totals round to/)).toBeTruthy();
      await waitFor(() => expect(blocks()).toHaveLength(0));
      fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));
      await stopPanel().findByRole('heading', { name: 'The change at a glance' });

      fireEvent.click(stopPanel().getByRole('button', { name: 'Show inline' }));

      await waitFor(() => expect(lineAboveBlock()).toBe('cart line 7'));
      expect(stopPanel().getByText(note)).toBeTruthy();
      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      expect(await stopPanel().findByText(/Totals round to/)).toBeTruthy();
      fireEvent.click(stopPanel().getByRole('button', { name: 'Show inline' }));
      await waitFor(() => expect(lineAboveBlock()).toBe('pay line 22'));
   });

   it('opens the primary passage when the reviewer moves a stop inline from another file, so the block shows with a way back', async () => {
      await openPlacedStop('The change at a glance');
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /caller/ }));
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 30);

      fireEvent.click(stopPanel().getByRole('button', { name: 'Show inline' }));

      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 5);
      await waitFor(() => expect(lineAboveBlock()).toBe('cart line 7'));
      expect(await stopPanel().findByText(note)).toBeTruthy();
      fireEvent.click(stopPanel().getByRole('button', { name: 'Show here' }));
      await waitFor(() => expect(blocks()).toHaveLength(0));
      expect(stopPanel().getByRole('button', { name: 'Show inline' })).toBeTruthy();
   });

   it('lets the reviewer keep an inline stop in the Stop panel while its diff cannot show the block, and move it back', async () => {
      const { app, transport } = await openPlacedStop('Cart totals', { arrange: (network) => network.fail('diff_file', { status: 500 }) });
      await waitFor(() => expect(diffRequested(transport)).toBe(true));

      fireEvent.click(await stopPanel().findByRole('button', { name: 'Keep here' }));

      expect(await stopPanel().findByRole('button', { name: 'Show inline' })).toBeTruthy();
      expect(app.placementOverride).toBe('side');
      expect(stopPanel().getByText(/Totals round to/)).toBeTruthy();
      expect(stopPanel().queryByText(fallbackNote)).toBeNull();
      fireEvent.click(stopPanel().getByRole('button', { name: 'Show inline' }));
      expect(await stopPanel().findByRole('button', { name: 'Keep here' })).toBeTruthy();
      expect(app.placementOverride).toBe('inline');
   });

   it('names the move button Keep here while the Stop panel shows an inline stop whose primary passage no editor shows, and keeps the stop there for good', async () => {
      const { app } = await openPlacedStop('Cart totals');
      await waitFor(() => expect(blocks()).toHaveLength(1));
      expect(stopPanel().getByRole('button', { name: 'Show here' })).toBeTruthy();
      fireEvent.click(block().getByRole('link', { name: 'the cart' }));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20);
      await waitFor(() => expect(blocks()).toHaveLength(0));

      expect(await stopPanel().findByText(/Totals round to/)).toBeTruthy();
      expect(stopPanel().getByText(fallbackNote)).toBeTruthy();
      expect(stopPanel().queryByRole('button', { name: 'Show here' })).toBeNull();
      fireEvent.click(stopPanel().getByRole('button', { name: 'Go to the explanation' }));
      await waitFor(() => expect(lineAboveBlock()).toBe('pay line 22'));
      expect(stopPanel().getByRole('button', { name: 'Show here' })).toBeTruthy();
      expect(stopPanel().queryByText(fallbackNote)).toBeNull();

      fireEvent.click(block().getByRole('link', { name: 'the cart' }));
      await waitFor(() => expect(blocks()).toHaveLength(0));
      fireEvent.click(await stopPanel().findByRole('button', { name: 'Keep here' }));

      expect(await stopPanel().findByRole('button', { name: 'Show inline' })).toBeTruthy();
      expect(app.placementOverride).toBe('side');
      expect(stopPanel().queryByText(fallbackNote)).toBeNull();
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /src\/pay\.ts/ }));
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(blocks()).toEqual([]);
      expect(stopPanel().getByText(/Totals round to/)).toBeTruthy();
   });

   it('shows every stop with a primary passage inline in inline placement, even side by side, with a spacer in the base pane and no move buttons', async () => {
      await openPlacedStop('Rounding', { layout: 'sideBySide', mode: 'inline' });
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20);

      await waitFor(() => expect(blocks()).toHaveLength(1));
      const [baseEditor, headEditor] = diff().querySelectorAll('.cm-editor');
      expect(headEditor!.contains(blocks()[0]!)).toBe(true);
      expect(lineAboveBlock()).toBe('cart head line 20');
      expect(baseEditor!.querySelector('.lotsen-explanationSpacer')?.previousElementSibling?.textContent).toBe('cart base line 20');
      expect(block().getByRole('button', { name: 'Scenario' })).toBeTruthy();
      expect(block().queryByRole('button', { name: 'Show in the Stop panel' })).toBeNull();
      expect(stopPanel().queryByRole('button', { name: /Show/ })).toBeNull();
   });

   it('shows every stop in the Stop panel in side placement', async () => {
      await openPlacedStop('Cart totals', { mode: 'side' });
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);

      expect(stopPanel().getByText(/Totals round to/)).toBeTruthy();
      expect(blocks()).toEqual([]);
      expect(stopPanel().queryByRole('button', { name: /Show/ })).toBeNull();
   });

   it('shows a stop without passages in the Stop panel in inline placement', async () => {
      const bare = backend().template('explanationPlacement-bare', (world) => seedStops(world, [{ title: 'The change at a glance', kind: 'overview', content: { headline: 'Nothing to point at.' } }]));
      await openPlacedTour(bare, { mode: 'inline' });

      fireEvent.click(planStop('The change at a glance'));

      expect(await stopPanel().findByText('Nothing to point at.')).toBeTruthy();
   });

   it('shows the block below the primary passage in its stacked file in the stop-files mode, and only there in step through', async () => {
      const spread = backend().template('explanationPlacement-spread', (world) =>
         seedStops(world, [{ title: 'Cart totals', kind: 'inspect', content: { headline, passages: [passage('head', 'src/pay.ts', 20, 22), passage('head', 'src/pay.ts', 30, 31, 'caller')] } }]),
      );
      await openPlacedTour(spread, { mode: 'mixed' });
      const modeSwitch = within(screen.getByRole('group', { name: 'View mode' }));

      fireEvent.click(modeSwitch.getByRole('button', { name: 'Stop files' }));
      fireEvent.click(planStop('Cart totals'));

      await waitFor(() => expect(lineAboveBlock()).toBe('pay line 22'));
      expect(screen.getByRole('list', { name: 'Files in this stop' }).querySelectorAll('.lotsen-explanation')).toHaveLength(1);

      fireEvent.click(modeSwitch.getByRole('button', { name: 'Step through' }));
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
      await waitFor(() => expect(lineAboveBlock()).toBe('pay line 22'));
      fireEvent.click(within(screen.getByRole('navigation', { name: 'Passages' })).getByRole('button', { name: 'Next passage' }));
      await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 30);
      expect(blocks()).toHaveLength(1);
      expect(lineAboveBlock()).toBe('pay line 22');
   });

   it('keeps the sections that the reviewer closed in the block while the stop stays under view, through another view mode, and opens them again for the next stop', async () => {
      await openPlacedStop('Cart totals');
      await waitFor(() => expect(blocks()).toHaveLength(1));
      const why = () => block().getByRole('button', { name: 'Why' });
      fireEvent.click(why());
      await waitFor(() => expect(why().getAttribute('aria-expanded')).toBe('false'));
      const shownFirst = blocks()[0]!;

      fireEvent.click(within(screen.getByRole('group', { name: 'View mode' })).getByRole('button', { name: 'Stop files' }));

      await waitFor(() => expect(screen.getByRole('list', { name: 'Files in this stop' }).querySelectorAll('.lotsen-explanation')).toHaveLength(1));
      expect(blocks()[0]).not.toBe(shownFirst);
      expect(why().getAttribute('aria-expanded')).toBe('false');

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));
      await stopPanel().findByRole('heading', { name: rounding.title });
      fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));

      await stopPanel().findByRole('heading', { name: 'Cart totals' });
      await waitFor(() => expect(why().getAttribute('aria-expanded')).toBe('true'));
   });

   it('removes the block’s content when the diff goes and when the stop changes', async () => {
      const removed = (element: HTMLElement) => !element.isConnected && element.childElementCount === 0;
      await openPlacedStop('Cart totals');
      await waitFor(() => expect(blocks()).toHaveLength(1));
      const shownFirst = blocks()[0]!;
      const destroy = vi.spyOn(CodeMirrorSurface.prototype, 'destroy');

      fireEvent.click(within(shownFirst).getByRole('link', { name: 'the cart' }));

      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20);
      expect(destroy).toHaveBeenCalled();
      expect(removed(shownFirst)).toBe(true);
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /src\/pay\.ts/ }));
      await waitFor(() => expect(blocks()).toHaveLength(1));
      const shownAgain = blocks()[0]!;
      expect(shownAgain).not.toBe(shownFirst);

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));

      await waitFor(() => expect(blocks()).toHaveLength(0));
      expect(removed(shownAgain)).toBe(true);
   });
});
