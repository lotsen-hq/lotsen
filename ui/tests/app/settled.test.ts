import { configure, getConfig, waitFor } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, onTestFinished } from 'vitest';
import { backend, cartContent, clickTour, openStop, openTour, seedTour, settled, startApp } from '../harness';
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

describe("the harness's settled", () => {
   /** Shortens the wait of settled for the rest of the test, and returns a reader of the time of each wait that the timeout's message names. */
   function shortWait(timeoutMs: number): (message: string) => { requests: number; jobs: number; events: number } {
      const { asyncUtilTimeout } = getConfig();
      configure({ asyncUtilTimeout: timeoutMs });
      onTestFinished(() => configure({ asyncUtilTimeout }));
      return (message) => {
         const times = /It waited (\d+) ms for its requests, (\d+) ms for the server's background jobs and (\d+) ms for its events\.$/.exec(message);
         expect(times, message).not.toBeNull();
         return { requests: Number(times![1]), jobs: Number(times![2]), events: Number(times![3]) };
      };
   }

   const failureOf = (settling: Promise<void>): Promise<Error> =>
      settling.then(
         () => expect.fail('settled returned before the wait that the test keeps open ended.'),
         (thrown: Error) => thrown,
      );

   it('names a request that outlasts its wait, with the time that each wait took', async () => {
      const { world } = await worldOf(cart);
      const { app, transport } = await startApp(world);
      transport.delay('list_changed_files', 10_000);
      await clickTour();
      await waitFor(() => expect(app.selectedPlan).toBeDefined());
      const timesOf = shortWait(1000);

      const error = await failureOf(settled(app));

      expect(error.message).toMatch(/^The page did not settle within 1000 ms: 1 of its requests are still in flight\. /);
      const times = timesOf(error.message);
      expect(times.requests).toBeGreaterThanOrEqual(900);
      expect(times).toMatchObject({ jobs: 0, events: 0 });
   });

   it("names the host's late answer on the jobs, with the time that each wait took", async () => {
      const { world } = await worldOf(cart);
      const { app } = await startApp(world);
      await openTour(app);
      let answer!: () => void;
      const answered = new Promise<void>((resolve) => (answer = resolve));
      const quiet = world.quiet.bind(world);
      (world as { quiet: typeof world.quiet }).quiet = async (comment) => {
         await answered;
         await quiet(comment);
      };
      onTestFinished(answer);
      const timesOf = shortWait(1000);

      const error = await failureOf(settled(app));

      expect(error.message).toMatch(/^The page did not settle within 1000 ms: the test host has not reported that the background jobs of the server ended\. /);
      const times = timesOf(error.message);
      expect(times.requests + times.jobs).toBeGreaterThanOrEqual(900);
      expect(times.events).toBe(0);
   });
});
