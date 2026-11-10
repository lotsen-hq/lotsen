import { beforeEach, describe, expect, it } from 'vitest';
import { backend, cartContent, openStop, openTour, seedTour, startApp } from '../harness';
import { worldOf } from './appPanels';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   history.replaceState(null, '', '/');
});

const cart = backend().template('settled-cart', async (world) => {
   const seeded = await seedTour(world, { chapters: [{ title: 'Cart', stops: [{ title: 'Cart totals', kind: 'inspect', content: cartContent('Totals round.') }] }], bound: false });
   return seeded.stopIds;
});

describe("the harness's openTour and openStop", () => {
   it('returns from openTour once a late list of the changed files has loaded', async () => {
      const { world } = await worldOf(cart);
      const { app, transport } = await startApp(world);
      transport.delay('list_changed_files', 300);

      await openTour(app);

      expect(app.selectedFiles?.state).toBe('loaded');
   });

   it('returns from openTour once the slow body of the changed files has loaded', async () => {
      const { world } = await worldOf(cart);
      const { app, transport } = await startApp(world);
      transport.slowBody('list_changed_files', 300);

      await openTour(app);

      expect(app.selectedFiles?.state).toBe('loaded');
   });

   it("returns from openStop once the server answered the stop's late progress write", async () => {
      const { world, seed: stopIds } = await worldOf(cart);
      const { app, transport } = await startApp(world);
      await openTour(app);
      let answered = false;
      void transport.answered('record_reading_progress').then(() => (answered = true));
      transport.delay('record_reading_progress', 300);

      await openStop(app, stopIds[0]!);

      expect(answered).toBe(true);
      expect(app.selectedRecord?.progress?.lastStop).toBe(stopIds[0]);
   });

   it.each(['request', 'response'] as const)('returns from openTour while the test holds the list of the changed files at the %s', async (at) => {
      const { world } = await worldOf(cart);
      const { app, transport } = await startApp(world);
      const release = transport.hold('list_changed_files', at);

      await openTour(app);

      expect(app.selectedFiles?.state).toBe('loading');
      release();
   });
});
