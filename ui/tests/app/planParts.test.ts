import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { attachClaude, backend, cartContent, openStop, openTour, seedTour, startApp, type SeededTour } from '../harness';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

type Seed = Pick<SeededTour, 'tourId' | 'stopIds'>;

// Two parts of one chapter each, with two written stops in each part.
const parted = backend().template('planParts-plan', async (world) => {
   const seeded = await seedTour(world, {
      chapters: [
         {
            title: 'Cart',
            part: 'The model',
            stops: [
               { title: 'Cart adds', kind: 'inspect', content: cartContent('Cart adds.') },
               { title: 'Line totals', kind: 'inspect', content: cartContent('Line totals.') },
            ],
         },
         {
            title: 'Checkout',
            part: 'The flow',
            stops: [
               { title: 'Gateway errors', kind: 'inspect', content: cartContent('Gateway errors.') },
               { title: 'Receipts', kind: 'inspect', content: cartContent('Receipts.') },
            ],
         },
      ],
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds } satisfies Seed;
});

// Opens the tour with the stop open and shows the Plan panel.
async function open(stopTitle: string): Promise<{ app: LotsenApp; world: World } & Seed> {
   const world = await backend().world({ template: parted });
   const seed = world.seed as Seed;
   const { app } = await startApp(world);
   await openTour(app);
   await openStop(app, seed.stopIds[['Cart adds', 'Line totals', 'Gateway errors', 'Receipts'].indexOf(stopTitle)]!);
   await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   await screen.findByRole('tabpanel', { name: 'Plan' });
   return { app, world, ...seed };
}

const planPanel = () => within(screen.getByRole('tabpanel', { name: 'Plan' }));
const partButton = (title: string) => planPanel().getByRole('button', { name: title });
const stopNavigation = () => within(screen.getByRole('navigation', { name: 'Stops' }));
const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));

async function fold(title: string): Promise<void> {
   await fireEvent.click(partButton(title));
   await waitFor(() => expect(partButton(title).getAttribute('aria-expanded')).toBe('false'));
}

describe('a folded part of the plan', () => {
   it('unfolds when Next opens a stop in it', async () => {
      await open('Line totals');
      await fold('The flow');

      await fireEvent.click(stopNavigation().getByRole('button', { name: 'Next stop' }));

      await stopPanel().findByRole('heading', { name: 'Gateway errors' });
      expect(partButton('The flow').getAttribute('aria-expanded')).toBe('true');
      expect(
         planPanel()
            .getByRole('button', { name: /^3 Inspect: Gateway errors/ })
            .getAttribute('aria-current'),
      ).toBe('step');
   });

   it('unfolds when Back opens a stop in it', async () => {
      await open('Gateway errors');
      await fold('The model');

      await fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));

      await stopPanel().findByRole('heading', { name: 'Line totals' });
      expect(partButton('The model').getAttribute('aria-expanded')).toBe('true');
   });

   it('unfolds when a link opens a stop in it, and leaves the other parts folded', async () => {
      const { app, stopIds } = await open('Cart adds');
      await fold('The model');
      await fold('The flow');

      app.openStop(stopIds[3]!);

      await stopPanel().findByRole('heading', { name: 'Receipts' });
      expect(partButton('The flow').getAttribute('aria-expanded')).toBe('true');
      expect(partButton('The model').getAttribute('aria-expanded')).toBe('false');
   });

   // Claude's replan with the seed's stops in the order Line totals, Cart adds, Gateway errors, Receipts, the first in part p1 and the others in part p2.
   async function moveCartAddsToTheFlow(world: World, tourId: string, stopIds: string[]): Promise<void> {
      const claude = await attachClaude(world, tourId);
      const [cartAdds, lineTotals, gateway, receipts] = stopIds;
      await claude.call('set_plan', {
         tourId,
         parts: [
            { id: 'p1', title: 'The model' },
            { id: 'p2', title: 'The flow' },
         ],
         chapters: [
            { id: 'c1', title: 'Cart', part: 'p1' },
            { id: 'c2', title: 'Checkout', part: 'p2' },
         ],
         stops: [
            { id: lineTotals, title: 'Line totals', kind: 'inspect', chapter: 'c1' },
            { id: cartAdds, title: 'Cart adds', kind: 'inspect', chapter: 'c2' },
            { id: gateway, title: 'Gateway errors', kind: 'inspect', chapter: 'c2' },
            { id: receipts, title: 'Receipts', kind: 'inspect', chapter: 'c2' },
         ],
      });
   }

   it('unfolds when a replan moves the open stop into it', async () => {
      const { world, tourId, stopIds } = await open('Cart adds');
      await fold('The flow');

      await moveCartAddsToTheFlow(world, tourId, stopIds);

      await waitFor(() => expect(partButton('The flow').getAttribute('aria-expanded')).toBe('true'));
      expect(
         planPanel()
            .getByRole('button', { name: /^2 Inspect: Cart adds/ })
            .getAttribute('aria-current'),
      ).toBe('step');
   });

   it('stays folded when a replan leaves the open stop in another part', async () => {
      const { world, tourId, stopIds } = await open('Line totals');
      await fold('The flow');

      await moveCartAddsToTheFlow(world, tourId, stopIds);

      await waitFor(() => expect(planPanel().queryByRole('button', { name: /^1 Inspect: Line totals/ })).not.toBeNull());
      expect(partButton('The flow').getAttribute('aria-expanded')).toBe('false');
   });
});
