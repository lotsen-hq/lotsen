import { fireEvent, screen, waitFor } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { EventPayloads } from '../../src/lib/contract.generated';
import { readEventStream } from '../../src/lib/sse';
import type { Tour } from '../../src/lib/types';
import type { Transport, World } from '../backend/backend';
import { backend, cartContent, prLabel, prUrl, seedTour, startApp } from '../harness';
import { manualTimer } from '../manualTimer';
import { openTourList, tourButton } from '../tourPicker';
import { planPanel, resetPageEachTest } from './appPanels';

const credentialKey = 'lotsen.uiCredential';

resetPageEachTest();

/** A response that the page's network keeps back after the server answered, until the release. */
interface HeldAnswer {
   /** Completes once the server answered the held call. */
   answered: Promise<void>;
   release(): void;
   /** Completes once the page got the answer, after the release. */
   delivered: Promise<void>;
}

/**
 * The world's transport with three more faults of the network: a closed gate that keeps the page's reconnects of the event stream from the server, a hold that tells when the server
 * answered, which `Transport.hold` does not, and text that the test adds to the page's latest event stream.
 */
function pageNetwork(world: World) {
   const inner = world.transport();
   let gate: Promise<void> | null = null;
   let openGate = () => {};
   const holds: { operation: string; answered: () => void; released: Promise<void>; delivered: () => void }[] = [];
   let latest: { controller: ReadableStreamDefaultController<Uint8Array>; hello: Promise<void> } | null = null;
   const fetch: typeof window.fetch = async (input, init) => {
      const path = String(input);
      if (path === '/api/events') {
         if (gate !== null) {
            await gate;
         }

         const response = await inner.fetch(input, init);
         if (!response.ok || response.body === null) {
            return response;
         }

         const reader = response.body.getReader();
         let helloSent = () => {};
         const hello = new Promise<void>((resolve) => (helloSent = resolve));
         const body = new ReadableStream<Uint8Array>({
            start: (controller) => void (latest = { controller, hello }),
            async pull(controller) {
               const { done, value } = await reader.read();
               if (done) {
                  controller.close();
               } else {
                  controller.enqueue(value);
                  helloSent();
               }
            },
            cancel: (reason) => reader.cancel(reason),
         });
         return new Response(body, { status: response.status, headers: response.headers });
      }

      const operation = /^\/api\/ops\/([a-z_]+)$/.exec(path)?.[1];
      const index = holds.findIndex((hold) => hold.operation === operation);
      if (index < 0) {
         return inner.fetch(input, init);
      }

      const [hold] = holds.splice(index, 1);
      const response = await inner.fetch(input, init);
      const body = await response.arrayBuffer();
      hold!.answered();
      await hold!.released;
      setTimeout(hold!.delivered);
      return new Response(body, { status: response.status, headers: response.headers });
   };
   const transport: Transport = { ...inner, fetch };
   return {
      transport,
      closeStreams() {
         gate = new Promise((resolve) => (openGate = resolve));
      },
      openStreams() {
         gate = null;
         openGate();
      },
      /** Adds the text to the page's latest event stream, after the server's first chunk, which holds the hello. */
      async inject(text: string): Promise<void> {
         const stream = latest!;
         await stream.hello;
         stream.controller.enqueue(new TextEncoder().encode(text));
      },
      holdAnswer(operation: string): HeldAnswer {
         let answered!: () => void;
         let release!: () => void;
         let delivered!: () => void;
         const held: HeldAnswer = {
            answered: new Promise((resolve) => (answered = resolve)),
            release: () => release(),
            delivered: new Promise((resolve) => (delivered = resolve)),
         };
         holds.push({ operation, answered, released: new Promise((resolve) => (release = resolve)), delivered });
         return held;
      },
   };
}

const streamsOpened = (transport: Transport) => transport.requests.filter((request) => request.path === '/api/events').length;
const listLoads = (transport: Transport) => transport.requests.filter((request) => request.operation === 'list_tours').length;
const settle = () => new Promise((resolve) => setTimeout(resolve, 20));

async function createTour(world: World, prUrl: string): Promise<{ tour: Tour; claude: Awaited<ReturnType<World['claude']>> }> {
   const claude = await world.claude();
   return { tour: (await claude.call('create_tour', { prUrl, familiarity: 'some' })) as Tour, claude };
}

/** Opens the tour of the label from the tour list, and turns to its Plan tab, which a pinned tour does not open on. */
async function openPlanOf(label: string | RegExp = prLabel): Promise<void> {
   const list = await openTourList();
   (await list.findByRole('button', typeof label === 'string' ? { name: (name) => name.includes(label) } : { name: label })).click();
   await waitFor(() => expect(screen.queryByRole('navigation', { name: 'Tours' })).toBeNull());
   await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
}

/** Closes the tour list, which jsdom leaves unpositioned and hidden when an event changes it while it is open. */
async function closeTourList(): Promise<void> {
   await fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
   await waitFor(() => expect(screen.queryByRole('navigation', { name: 'Tours' })).toBeNull());
}

describe('the event stream and the loads', () => {
   it('reconnects after the stream drops and reloads the list and the selected plan', async () => {
      const world = await backend().world();
      const { tourId, claude } = await seedTour(world);
      const network = pageNetwork(world);
      const { app } = await startApp(world, { transport: network.transport });
      await openPlanOf();
      await planPanel().findByText(/has not written the plan/);

      // Changes made while the stream is down reach the page only through the reload.
      network.closeStreams();
      network.transport.cutEventStreams();
      await waitFor(() => expect(app.connection).toBe('reconnecting'));
      await createTour(world, 'https://platform.test/missed/pull/2');
      await claude!.call('set_plan', { tourId, chapters: [{ id: 'c', title: 'Chapter' }], stops: [{ title: 'Written while away', kind: 'inspect', chapter: 'c' }] });
      expect(streamsOpened(network.transport)).toBe(1);
      network.openStreams();

      await waitFor(() => expect(streamsOpened(network.transport)).toBe(2));
      expect(await planPanel().findByText('Written while away')).toBeTruthy();
      await waitFor(() => expect(screen.getAllByRole('status').map((status) => status.textContent)).toContain('Live'));
      const list = await openTourList();
      await waitFor(() => expect(list.getAllByRole('button', tourButton)).toHaveLength(2));
      await closeTourList();

      await createTour(world, 'https://platform.test/after/pull/3');
      await waitFor(() => expect(app.tours).toHaveLength(3));
      expect((await openTourList()).getAllByRole('button', tourButton)).toHaveLength(3);
   });

   it('clears a failed plan load once a reload after a reconnect loads the plan', async () => {
      const world = await backend().world();
      await seedTour(world, { chapters: [{ title: 'Chapter', stops: [{ title: 'Loaded at last', kind: 'inspect' }] }] });
      const { transport } = await startApp(world);
      transport.fail('read_state', { status: 500 });
      await openPlanOf();
      await planPanel().findByText('The tour could not be loaded.');

      transport.cutEventStreams();

      expect(await planPanel().findByText('Loaded at last')).toBeTruthy();
      expect(planPanel().queryByText('The tour could not be loaded.')).toBeNull();
   });

   it('clears a failed plan load when a planSet event brings the plan', async () => {
      const world = await backend().world();
      const { tourId, claude } = await seedTour(world);
      const { transport } = await startApp(world);
      transport.fail('read_state', { status: 500 });
      await openPlanOf();
      await planPanel().findByText('The tour could not be loaded.');

      await claude!.call('set_plan', { tourId, chapters: [{ id: 'a', title: 'Chapter' }], stops: [{ title: 'Sent live', kind: 'inspect', chapter: 'a' }] });

      expect(await planPanel().findByText('Sent live')).toBeTruthy();
      expect(planPanel().queryByText('The tour could not be loaded.')).toBeNull();
   });

   it('keeps the error of the selected tour when a late response for another tour arrives', async () => {
      const world = await backend().world();
      const left = await seedTour(world, { chapters: [{ title: 'Chapter', stops: [{ title: 'Left behind', kind: 'inspect' }] }] });
      await world.clock.advance(1000);
      await createTour(world, 'https://platform.test/chosen/pull/2');
      await world.idle();
      const { app, transport } = await startApp(world);
      const release = transport.hold('read_state');
      (await (await openTourList()).findByRole('button', { name: /shop #42/ })).click();
      await waitFor(() => expect(transport.requests.some((request) => request.operation === 'read_state' && request.input?.tourId === left.tourId)).toBe(true));
      transport.fail('read_state', { status: 500 });
      await openPlanOf('chosen #2');
      await planPanel().findByText('The tour could not be loaded.');

      release();
      await waitFor(() => expect(app.tourRecord(left.tourId)?.plan).toBeDefined());

      expect(planPanel().getByText('The tour could not be loaded.')).toBeTruthy();
   });

   it('keeps the reload’s newer value in a tour row over a write response that was in flight across the reconnect', async () => {
      const world = await backend().world();
      const stops = [
         { title: 'Cart adds', kind: 'inspect' as const, content: cartContent('Cart adds.') },
         { title: 'Line totals', kind: 'inspect' as const, content: cartContent('Line totals.') },
      ];
      const { tourId, claude } = await seedTour(world, { chapters: [{ title: 'Cart', stops }] });
      const network = pageNetwork(world);
      const { app } = await startApp(world, { transport: network.transport });
      await openPlanOf();
      network.closeStreams();
      network.transport.cutEventStreams();
      await waitFor(() => expect(app.connection).toBe('reconnecting'));

      // Another page changes the level after the server stored this page's change, so the reload's list holds the newer level.
      const held = network.holdAnswer('change_familiarity');
      const changing = app.changeFamiliarity('new');
      await held.answered;
      const question = await claude!.next('question');
      await claude!.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Rewritten.' });
      await world.reviewer.call('change_familiarity', { tourId, familiarity: 'familiar' });
      network.openStreams();
      await waitFor(() => expect(app.tours[0]?.familiarity).toBe('familiar'));
      held.release();
      expect(await changing).toBeNull();
      await held.delivered;
      await settle();

      expect(app.tours[0]?.familiarity).toBe('familiar');
   });

   it('keeps a planSet over a slower response from earlier in the same millisecond', async () => {
      const world = await backend().world();
      const { tourId, claude } = await seedTour(world, { chapters: [{ title: 'Chapter', stops: [{ title: 'Old stop', kind: 'inspect' }] }] });
      const network = pageNetwork(world);
      await startApp(world, { transport: network.transport });
      const held = network.holdAnswer('read_state');
      await openPlanOf();
      await held.answered;

      // Eight ticks put the plan's write time later than the held answer's tour, within the same millisecond.
      await world.clock.advance(0.0008);
      await claude!.call('set_plan', { tourId, chapters: [{ id: 'a', title: 'Chapter' }], stops: [{ title: 'New stop', kind: 'inspect', chapter: 'a' }] });
      await planPanel().findByText('New stop');
      held.release();
      await held.delivered;
      await settle();

      expect(planPanel().getByText('New stop')).toBeTruthy();
      expect(planPanel().queryByText('Old stop')).toBeNull();
   });
});

describe('an event that the page fails to apply', () => {
   // A tourDeleted event without a payload makes its handler throw.
   const broken = 'event: tourDeleted\ndata: null\n\n';

   afterEach(() => {
      vi.restoreAllMocks();
   });

   it('logs each failure, keeps the stream, reloads twice for a chunk of failures and again for a failure after those reloads', async () => {
      const logged = vi.spyOn(console, 'error').mockImplementation(() => {});
      const world = await backend().world();
      const network = pageNetwork(world);
      const { app } = await startApp(world, { transport: network.transport });
      const loads = listLoads(network.transport);
      const first = network.transport.answered('list_tours');

      // The first failure starts a reload, so the chunk's other two fail during its list load, which its replay logs again and a second reload follows.
      await network.inject(broken.repeat(3));
      await first;
      const second = network.transport.answered('list_tours');
      await second;
      await settle();
      expect(logged).toHaveBeenCalledTimes(5);
      expect(listLoads(network.transport)).toBe(loads + 2);
      await network.inject(broken);

      await waitFor(() => expect(listLoads(network.transport)).toBe(loads + 3));
      expect(logged).toHaveBeenCalledTimes(6);
      expect(streamsOpened(network.transport)).toBe(1);
      await createTour(world, prUrl);
      await waitFor(() => expect(app.tours).toHaveLength(1));
   });

   it('reloads once more after a list load in flight lands, for a failure during that load, and still shows the list', async () => {
      const logged = vi.spyOn(console, 'error').mockImplementation(() => {});
      const world = await backend().world();
      await seedTour(world);
      const network = pageNetwork(world);
      const { app } = await startApp(world, { transport: network.transport });
      const loads = listLoads(network.transport);
      const held = network.holdAnswer('list_tours');

      await network.inject(broken);
      await held.answered;
      await network.inject(broken);
      await waitFor(() => expect(logged).toHaveBeenCalledTimes(2));
      expect(listLoads(network.transport)).toBe(loads + 1);
      held.release();
      await held.delivered;

      await waitFor(() => expect(listLoads(network.transport)).toBe(loads + 2));
      await settle();
      expect(listLoads(network.transport)).toBe(loads + 2);
      expect(logged).toHaveBeenCalledTimes(3);
      expect(app.toursError).toBeNull();
      expect((await openTourList()).getAllByRole('button', tourButton)).toHaveLength(1);
   });

   it('starts no reload for a failure that only the replay of a list load meets', async () => {
      const logged = vi.spyOn(console, 'error').mockImplementation(() => {});
      const world = await backend().world();
      const network = pageNetwork(world);
      const { app } = await startApp(world, { transport: network.transport });
      // The stream's handler is the replay's too, so only a fault put on the page fails the replay of an event that applied live.
      const page = app as unknown as { applyEvent(event: { name: string; payload: { tourId?: string } }): void };
      const apply = page.applyEvent.bind(app);
      let applied = 0;
      vi.spyOn(page, 'applyEvent').mockImplementation((event) => {
         if (event.payload.tourId === 'replayed' && applied++ > 0) {
            throw new Error('The replay failed.');
         }

         apply(event);
      });
      const loads = listLoads(network.transport);
      const held = network.holdAnswer('list_tours');
      network.transport.cutEventStreams();
      await held.answered;

      await network.inject('event: tourDeleted\ndata: {"tourId":"replayed"}\n\n');
      await waitFor(() => expect(applied).toBe(1));
      held.release();
      await held.delivered;
      await settle();

      expect(logged).toHaveBeenCalledTimes(1);
      expect(listLoads(network.transport)).toBe(loads + 1);
      expect(streamsOpened(network.transport)).toBe(2);
   });
});

/** Returns the hello that the world's server opens each event stream with, read from a stream of the test's own. */
async function helloOf(world: World): Promise<EventPayloads['hello']> {
   const reading = new AbortController();
   const response = await fetch(`${world.url}/api/events`, { headers: { Authorization: `Bearer ${world.uiCredential}` }, signal: reading.signal });
   let hello: EventPayloads['hello'] | undefined;
   await readEventStream(
      response.body!,
      (event) => {
         expect(event.type).toBe('hello');
         hello = JSON.parse(event.data) as EventPayloads['hello'];
         reading.abort();
      },
      reading.signal,
   ).catch(() => {});
   return hello!;
}

describe('a stream that the network stalls', () => {
   it('reconnects once the stream stays silent for the limit that its hello announced, and reloads', async () => {
      const world = await backend().world();
      const { keepAliveMs, silenceFactor } = await helloOf(world);
      const limitMs = keepAliveMs * silenceFactor;
      const silence = manualTimer();
      const { app, transport } = await startApp(world, { silenceTimer: silence.schedule });
      await (await openTourList()).findByText(/No tours yet/);
      await closeTourList();
      await waitFor(() => expect(silence.armed()).toEqual([limitMs]));

      const release = transport.stallEventStreams();
      await createTour(world, prUrl);
      silence.advance(limitMs - 1);
      expect(silence.armed()).toEqual([1]);
      expect(app.tours).toHaveLength(0);
      silence.advance(1);

      await waitFor(() => expect(streamsOpened(transport)).toBe(2));
      await waitFor(() => expect(app.tours).toHaveLength(1));
      await waitFor(() => expect(app.connection).toBe('live'));
      await waitFor(() => expect(silence.armed()).toEqual([limitMs]));
      release();
   });
});

// A restart, by `lotsen restart` or on a configured port, puts a server with new credentials behind the page's address.
describe('a server that restarted', () => {
   it('stops and tells the user to run lotsen open when the reconnected stream is refused', async () => {
      const world = await backend().world();
      const { transport } = await startApp(world);
      await (await openTourList()).findByText(/No tours yet/);

      await world.restart();

      expect((await screen.findByRole('alert')).textContent).toContain('lotsen open');
      expect(sessionStorage.getItem(credentialKey)).toBeNull();
      const opened = streamsOpened(transport);
      await new Promise((resolve) => setTimeout(resolve, 30));
      expect(streamsOpened(transport)).toBe(opened);
   });

   it('shows the signed-out notice naming lotsen open once the event stream reconnects to the new server', async () => {
      const world = await backend().world();
      await seedTour(world);
      await startApp(world);
      await openPlanOf();
      await planPanel().findByText(/has not written the plan/);

      await world.restart();

      const alert = await screen.findByRole('alert');
      expect(alert.textContent).toContain('lotsen open');
      expect(screen.queryByRole('tabpanel', { name: 'Plan' })).toBeNull();
      expect(sessionStorage.getItem(credentialKey)).toBeNull();
   });

   it('shows the signed-out notice naming lotsen open when a request reaches the new server before the stream reconnects', async () => {
      const world = await backend().world();
      await seedTour(world);
      const { app, transport } = await startApp(world, { retryDelaysMs: [60_000] });
      const list = await openTourList();
      const button = await list.findByRole('button', tourButton);
      const opened = streamsOpened(transport);

      await world.restart();
      await waitFor(() => expect(app.connection).toBe('reconnecting'));
      button.click();

      expect((await screen.findByRole('alert')).textContent).toContain('lotsen open');
      expect(sessionStorage.getItem(credentialKey)).toBeNull();
      expect(streamsOpened(transport)).toBe(opened);
   });

   it('keeps the signed-out notice when a load that the earlier server answered arrives after the refusal', async () => {
      const world = await backend().world();
      await seedTour(world);
      const network = pageNetwork(world);
      const { app } = await startApp(world, { transport: network.transport });
      const held = network.holdAnswer('read_state');
      (await (await openTourList()).findByRole('button', tourButton)).click();
      await held.answered;

      await world.restart();
      await screen.findByRole('alert');
      held.release();
      await held.delivered;
      await settle();

      expect(screen.getByRole('alert').textContent).toContain('lotsen open');
      expect(app.phase).toBe('signedOut');
      expect(screen.queryByRole('tabpanel', { name: 'Plan' })).toBeNull();
   });
});
