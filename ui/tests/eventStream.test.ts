import { afterEach, describe, expect, it, vi } from 'vitest';
import { connectEventStream, helloLimitMs, type EventStreamConnection } from '../src/lib/eventStream';
import type { SseEvent } from '../src/lib/sse';
import { manualTimer } from './manualTimer';
import { eventFixtures } from './contract/fixtures.generated';

const hello = 'event: hello\ndata: {"keepAliveMs":50,"silenceFactor":3}\n\n';
const limitMs = 150;

/** One fetch of the event stream that the test feeds, with whether the page aborted it. */
interface FedStream {
   send(text: string): void;
   readonly aborted: boolean;
}

/** Returns a `fetch` that answers each request for the event stream with a stream that the test feeds. */
function fedStreams(): { fetch: typeof fetch; opened: FedStream[] } {
   const encoder = new TextEncoder();
   const opened: FedStream[] = [];
   const fetchFn: typeof fetch = async (_input, init) => {
      let controller!: ReadableStreamDefaultController<Uint8Array>;
      let aborted = false;
      const body = new ReadableStream<Uint8Array>({ start: (started) => void (controller = started) });
      init?.signal?.addEventListener('abort', () => {
         aborted = true;
         try {
            controller.error(new DOMException('The fetch was aborted.', 'AbortError'));
         } catch {
            // The page canceled the body before it aborted the fetch.
         }
      });
      opened.push({
         send: (text) => controller.enqueue(encoder.encode(text)),
         get aborted() {
            return aborted;
         },
      });
      return new Response(body, { headers: { 'Content-Type': 'text/event-stream' } });
   };
   return { fetch: fetchFn, opened };
}

let connection: EventStreamConnection | undefined;

afterEach(() => {
   connection?.close();
   connection = undefined;
});

/** Connects with an `onEvent` that keeps each event, after it runs `apply` on the event. */
function connect(apply: (event: SseEvent) => void = () => {}) {
   const network = fedStreams();
   const silence = manualTimer();
   const events: SseEvent[] = [];
   let opens = 0;
   let failures = 0;
   connection = connectEventStream({
      fetch: network.fetch,
      credential: 'credential',
      onOpen: () => opens++,
      onEvent: (event) => {
         apply(event);
         events.push(event);
      },
      onEventFailed: () => failures++,
      onUnauthorized: () => {},
      retryDelaysMs: [0],
      silenceTimer: silence.schedule,
   });
   return { network, silence, events, opens: () => opens, failures: () => failures };
}

async function opened(network: { opened: FedStream[] }, count: number): Promise<FedStream> {
   await vi.waitFor(() => expect(network.opened).toHaveLength(count));
   return network.opened[count - 1]!;
}

describe('connectEventStream', () => {
   it('hands every event after the hello to onEvent, and the hello to none', async () => {
      const { network, events } = connect();
      (await opened(network, 1)).send(`${hello}event: planSet\ndata: {}\n\n`);

      await vi.waitFor(() => expect(events).toEqual([{ type: 'planSet', data: '{}' }]));
   });

   it('reconnects and opens again once a stream stays silent for the limit that its hello announced', async () => {
      const { network, silence, opens } = connect();
      const first = await opened(network, 1);
      first.send(hello);
      await vi.waitFor(() => expect(silence.armed()).toEqual([limitMs]));

      silence.advance(limitMs - 1);
      expect(silence.armed()).toEqual([1]);
      silence.advance(1);

      await opened(network, 2);
      expect(first.aborted).toBe(true);
      await vi.waitFor(() => expect(opens()).toBe(2));
   });

   it('keeps a stream connected while keep-alive comments arrive within the limit, and times the limit from the last one', async () => {
      const { network, silence } = connect();
      const first = await opened(network, 1);
      first.send(hello);
      await vi.waitFor(() => expect(silence.armed()).toEqual([limitMs]));
      silence.advance(100);

      first.send(': keep-alive\n\n');
      await vi.waitFor(() => expect(silence.armed()).toEqual([limitMs]));
      silence.advance(limitMs - 1);
      expect(first.aborted).toBe(false);
      silence.advance(1);

      await opened(network, 2);
      expect(first.aborted).toBe(true);
   });

   it('reconnects once a stream sends no hello within the hello limit after its headers', async () => {
      const { network, silence } = connect();
      const first = await opened(network, 1);
      await vi.waitFor(() => expect(silence.armed()).toEqual([helloLimitMs]));

      silence.advance(helloLimitMs - 1);
      expect(first.aborted).toBe(false);
      silence.advance(1);

      await opened(network, 2);
      expect(first.aborted).toBe(true);
   });

   it('treats a stream whose first event is no hello as broken, and reconnects', async () => {
      const { network, events } = connect();
      const first = await opened(network, 1);
      first.send('event: planSet\ndata: {}\n\n');

      await opened(network, 2);
      expect(first.aborted).toBe(true);
      expect(events).toEqual([]);
   });

   it.each([
      ['a keep-alive of 0', '{"keepAliveMs":0,"silenceFactor":3}'],
      ['a factor of 0', '{"keepAliveMs":50,"silenceFactor":0}'],
      ['no factor', '{"keepAliveMs":50}'],
      ['a limit past a browser timer', '{"keepAliveMs":2147483647,"silenceFactor":2}'],
      ['no JSON', 'hello'],
   ])('treats a stream whose hello announces %s as broken', async (_case, data) => {
      const { network } = connect();
      const first = await opened(network, 1);
      first.send(`event: hello\ndata: ${data}\n\n`);

      await opened(network, 2);
      expect(first.aborted).toBe(true);
   });
});

describe('an onEvent that throws', () => {
   const fault = new Error('The handler failed.');
   const throwOn = (type: string) => (event: SseEvent) => {
      if (event.type === type) {
         throw fault;
      }
   };

   afterEach(() => {
      vi.restoreAllMocks();
   });

   it('logs the error, keeps the stream connected and hands the next event of the same chunk to onEvent', async () => {
      const logged = vi.spyOn(console, 'error').mockImplementation(() => {});
      const { network, events, opens, failures } = connect(throwOn('broken'));
      const first = await opened(network, 1);
      first.send(`${hello}event: broken\ndata: {}\n\nevent: planSet\ndata: {}\n\n`);

      await vi.waitFor(() => expect(events).toEqual([{ type: 'planSet', data: '{}' }]));
      expect(logged).toHaveBeenCalledWith(expect.stringContaining('broken'), fault);
      expect(failures()).toBe(1);
      expect(first.aborted).toBe(false);
      expect(network.opened).toHaveLength(1);
      expect(opens()).toBe(1);
   });

   it('asks for a reload on each event that onEvent throws on, and keeps the stream connected', async () => {
      const logged = vi.spyOn(console, 'error').mockImplementation(() => {});
      const { network, opens, failures } = connect(throwOn('broken'));
      const first = await opened(network, 1);
      first.send(`${hello}${'event: broken\ndata: {}\n\n'.repeat(3)}`);
      first.send('event: broken\ndata: {}\n\n'.repeat(2));

      await vi.waitFor(() => expect(logged).toHaveBeenCalledTimes(5));
      expect(failures()).toBe(5);
      expect(first.aborted).toBe(false);
      expect(network.opened).toHaveLength(1);
      expect(opens()).toBe(1);
   });
});

describe('the hello limit', () => {
   // The golden hello fixture is the server's default liveness, so this fails when the C# default changes and the UI's constant does not.
   it("is the silence limit of the server's default liveness", () => {
      expect(helloLimitMs).toBe(eventFixtures.hello.keepAliveMs * eventFixtures.hello.silenceFactor);
   });
});
