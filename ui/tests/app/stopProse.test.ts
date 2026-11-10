import { fireEvent, screen, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { attachClaude, backend, startApp } from '../harness';
import { resetPageEachTest, worldOf } from './appPanels';
import { openPinnedTour, passage, planStop, reviseViewedStop, scrolledTo, seedStops, spyOnScrolls, stopPanel, type PlannedStop, type StopsSeed } from './stopProseWorld';

resetPageEachTest();
const spies = spyOnScrolls();

const linkedContent = {
   headline: 'Totals **round** to `cents`.',
   how: 'Each add checks:\n\n- the quantity\n- the product',
   why:
      'See [the add](ref:head/src/cart.ts#L20-20), [the caller](ref:head/src/pay.ts#L30-31), [the product type](ref:head/src/catalog.ts#L3-4), ' +
      '[the old line](ref:base/src/cart.ts#L20-20) and [the docs](https://gateway.example/docs).',
   passages: [passage('head', 'src/pay.ts', 20, 22)],
};
const pendingRounding: PlannedStop = { title: 'Rounding', kind: 'risk' };

const linked = backend().template('stopProse-linked', (world) => seedStops(world, [{ title: 'Cart totals', kind: 'inspect', content: linkedContent }, pendingRounding]));
// The stop's primary passage is in the cart, so its link into the generated payment file opens a file that starts collapsed.
const generated = backend().template('stopProse-generated', (world) =>
   seedStops(world, [{ title: 'Cart totals', kind: 'inspect', content: { ...linkedContent, passages: [passage('head', 'src/cart.ts', 20, 20)] } }, pendingRounding], {
      fileMarks: [{ path: 'src/pay.ts', mark: 'generated', reason: 'The gateway SDK writes it.' }],
   }),
);

async function openLinked() {
   const { world, seed } = await worldOf<StopsSeed>(linked);
   const { app } = await startApp(world);
   await openPinnedTour(app);
   fireEvent.click(planStop('Cart totals'));
   await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 20);
   return { world, app, ...seed };
}

const link = (name: string) => stopPanel().getByRole('link', { name });

describe('the prose of a stop', () => {
   it('shows the headline and each section as the server rendered them', async () => {
      await openLinked();

      expect(stopPanel().getByText('round').tagName).toBe('STRONG');
      expect(stopPanel().getByText('cents').tagName).toBe('CODE');
      expect(stopPanel().getByText('the quantity').closest('ul')).not.toBeNull();
      expect(stopPanel().getByText('the quantity').closest('.markdown')).not.toBeNull();
   });

   it('opens a ref: link into a changed file in its diff', async () => {
      await openLinked();

      fireEvent.click(link('the caller'));

      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
   });

   it('opens a ref: link into a collapsed generated file expanded at the passage', async () => {
      const { world } = await worldOf<StopsSeed>(generated);
      const { app } = await startApp(world);
      await openPinnedTour(app);
      fireEvent.click(planStop('Cart totals'));
      await scrolledTo(spies, 'Diff', 'src/cart.ts', 'head', 20);

      fireEvent.click(link('the caller'));

      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
      expect(within(screen.getByRole('main', { name: 'Diff' })).queryByRole('button', { name: 'Show the diff' })).toBeNull();
   });

   it('opens a ref: link into an unchanged file at its side', async () => {
      await openLinked();

      fireEvent.click(link('the product type'));

      expect(await scrolledTo(spies, 'File', 'src/catalog.ts', 'head', 3)).toBe('catalog line 3');
   });

   it('opens a ref: link at the base side', async () => {
      await openLinked();

      fireEvent.click(link('the old line'));

      expect(await scrolledTo(spies, 'Diff', 'src/cart.ts', 'base', 20)).toBe('cart base line 20');
   });

   it('opens a ref: link that a stopPut brings, from a click on its label', async () => {
      const { world, tourId, stopIds } = await openLinked();
      const claude = await attachClaude(world, tourId);
      await reviseViewedStop(claude, tourId, stopIds[0]!, { ...linkedContent, why: '[`pay`](ref:head/src/pay.ts#L30-31)' });

      const pay = (await stopPanel().findByText('pay')).closest('a')!;
      expect(pay.getAttribute('tabindex')).toBe('0');
      fireEvent.click(stopPanel().getByText('pay'));

      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
   });

   it('lets the keyboard reach a ref: link and open it with Enter', async () => {
      const { app } = await openLinked();
      const caller = link('the caller');
      expect(caller.getAttribute('tabindex')).toBe('0');

      fireEvent.keyDown(caller, { key: ' ' });
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(app.focus?.path).toBe('src/pay.ts');
      expect(app.focus?.line).toBe(20);

      fireEvent.keyDown(caller, { key: 'Enter' });

      expect(await scrolledTo(spies, 'Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
   });

   it('never navigates the page on a click of a ref: link', async () => {
      const { tourId, stopIds } = await openLinked();
      const before = location.pathname + location.search;

      const notPrevented = await fireEvent.click(link('the add'));

      expect(notPrevented).toBe(false);
      expect(location.pathname + location.search).toBe(before);
      // Only the history entry that the link adds changes the address.
      const fragment = new URLSearchParams({ tour: tourId, stop: stopIds[0]!, side: 'head', file: 'src/cart.ts', lines: '20-20' });
      expect(location.hash).toBe(`#${fragment.toString()}`);
   });

   it('leaves an https: link to open in a new tab with no opener', async () => {
      const { app } = await openLinked();
      const docs = link('the docs');
      expect(docs.getAttribute('href')).toBe('https://gateway.example/docs');
      expect(docs.getAttribute('target')).toBe('_blank');
      expect(docs.getAttribute('rel')).toBe('noopener noreferrer');
      let prevented: boolean | null = null;
      // The listener on the document runs after the page's own, then cancels the click so the test's page stays where it is.
      document.addEventListener('click', (event) => ((prevented = event.defaultPrevented), event.preventDefault()), { once: true });

      fireEvent.click(docs);

      expect(prevented).toBe(false);
      expect(app.focus?.path).toBe('src/pay.ts');
      expect(app.focus?.line).toBe(20);
   });

   it('shows the HTML of a stopPut in place of the HTML it replaces', async () => {
      const { world, tourId, stopIds } = await openLinked();
      const claude = await attachClaude(world, tourId);

      await reviseViewedStop(claude, tourId, stopIds[0]!, { headline: 'Rounding *can* hide a cent.', passages: [passage('head', 'src/pay.ts', 20, 22)] });

      expect((await stopPanel().findByText('can')).tagName).toBe('EM');
      expect(stopPanel().queryByText('round')).toBeNull();
      expect(stopPanel().queryByRole('link', { hidden: true })).toBeNull();
   });

   it('shows no prose for a pending stop, and the prose once its stopPut arrives', async () => {
      const { world, tourId, stopIds } = await openLinked();
      const claude = await attachClaude(world, tourId);
      fireEvent.click(planStop('Rounding'));
      await stopPanel().findByText('Claude has not written this stop yet.');

      expect(screen.getByRole('region', { name: 'Stop' }).querySelector('.markdown')).toBeNull();

      await claude.call('put_stop', { tourId, stopId: stopIds[1], headline: 'Rounding *can* hide a cent.', scenario: 'Two discounts stack.', passages: [passage('head', 'src/cart.ts', 20, 20)] });
      expect((await stopPanel().findByText('can')).tagName).toBe('EM');
   });
});
