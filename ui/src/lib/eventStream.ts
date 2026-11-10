import type { EventName, EventPayloads } from './contract.generated';
import { readEventStream, type SseEvent } from './sse';

/** The state of the connection to the event stream. */
export type ConnectionState = 'connecting' | 'live' | 'reconnecting';

/** The default waits before each reconnect attempt, in milliseconds. The last one repeats. */
export const defaultRetryDelaysMs: readonly number[] = [1000, 2000, 5000, 10000, 30000];

/** The event that opens every stream, which the event stream client reads itself and hands to no `onEvent`. */
export const helloEvent = 'hello' satisfies EventName;

/** The silence limit before a stream's hello, in milliseconds: that of a server whose liveness is the default, 20 s × 3, as `StreamLiveness.Default` sets it. */
export const helloLimitMs = 60_000;

/** Runs `callback` once after `ms` milliseconds, and returns the function that cancels it. */
export type Schedule = (callback: () => void, ms: number) => () => void;

/** The browser's timer. */
export const browserSchedule: Schedule = (callback, ms) => {
   const timer = setTimeout(callback, ms);
   return () => clearTimeout(timer);
};

export interface EventStreamOptions {
   fetch: typeof fetch;
   credential: string;
   /** Runs each time the stream opens, first and after every reconnect, once the server has subscribed this client. */
   onOpen: () => void;
   /** Runs for each event after the stream's hello. The stream logs an event that it throws on, and continues with the next event. */
   onEvent: (event: SseEvent) => void;
   /** Runs each time `onEvent` throws on an event. The caller reloads what it shows, because the event may be half applied. */
   onEventFailed: () => void;
   /** Runs once when the server refuses the UI credential. The stream then stops for good. */
   onUnauthorized: () => void;
   onStateChange?: (state: ConnectionState) => void;
   retryDelaysMs?: readonly number[];
   /** The timer of the silence limit, or none for the browser's. */
   silenceTimer?: Schedule;
}

/** An open event stream. */
export interface EventStreamConnection {
   /** Stops the stream and any reconnect. */
   close(): void;
}

/**
 * Reads `GET /api/events` with the UI credential, and reconnects with backoff whenever the stream ends, fails or stays silent too long.
 *
 * It uses `fetch` because `EventSource` cannot send an `Authorization` header. The server does not replay events, so `onOpen` is where the caller reloads what it shows.
 *
 * Each stream opens with a hello that announces the server's keep-alive interval and silence factor. Their product is the stream's silence limit, which every chunk, a keep-alive comment included,
 * starts again. Until the hello arrives, the limit is `helloLimitMs`. A stream that stays silent past its limit lost its connection without a close, so the client aborts its fetch and reconnects.
 * A stream whose first event is no valid hello is broken, and the client reconnects too.
 */
export function connectEventStream(options: EventStreamOptions): EventStreamConnection {
   const controller = new AbortController();
   const { signal } = controller;
   const delays = options.retryDelaysMs ?? defaultRetryDelaysMs;
   const schedule = options.silenceTimer ?? browserSchedule;

   async function run(): Promise<void> {
      let attempt = 0;
      options.onStateChange?.('connecting');
      while (!signal.aborted) {
         const connection = new AbortController();
         const close = () => connection.abort();
         signal.addEventListener('abort', close, { once: true });
         let cancelSilence = () => {};
         try {
            const response = await options.fetch('/api/events', {
               headers: { Authorization: `Bearer ${options.credential}`, Accept: 'text/event-stream' },
               cache: 'no-store',
               signal: connection.signal,
            });
            if (response.status === 401) {
               options.onUnauthorized();
               return;
            }

            if (!response.ok || !response.body) {
               throw new Error(`The event stream answered ${response.status}.`);
            }

            // The server subscribes before it sends the headers, so no event is lost between here and the reload.
            attempt = 0;
            options.onStateChange?.('live');
            options.onOpen();
            let limitMs: number | null = null;
            const watch = () => {
               cancelSilence();
               cancelSilence = schedule(close, limitMs ?? helloLimitMs);
            };
            const onEvent = (event: SseEvent) => {
               if (limitMs !== null) {
                  try {
                     options.onEvent(event);
                  } catch (error) {
                     logEventFailure(event, error);
                     options.onEventFailed();
                  }

                  return;
               }

               limitMs = silenceLimitOf(event);
               watch();
            };
            watch();
            await readEventStream(response.body, onEvent, connection.signal, watch);
         } catch {
            // A refused, broken or silent stream is retried like one that ended.
         } finally {
            cancelSilence();
            signal.removeEventListener('abort', close);
            connection.abort();
         }

         if (signal.aborted) {
            return;
         }

         options.onStateChange?.('reconnecting');
         await wait(delays[Math.min(attempt, delays.length - 1)] ?? 0, signal);
         attempt++;
      }
   }

   void run();
   return { close: () => controller.abort() };
}

/** Logs an error that a handler threw on an event to the browser's console. */
export function logEventFailure(event: SseEvent, error: unknown): void {
   console.error(`The page could not apply the ${event.type} event.`, error);
}

/**
 * Returns the silence limit that a stream's first event announces, in milliseconds.
 *
 * @throws An `Error` when the event is no hello with a keep-alive interval and a silence factor from 1, whose product fits a browser timer.
 */
function silenceLimitOf(first: SseEvent): number {
   let hello: Partial<EventPayloads['hello']> | null = null;
   try {
      hello = first.type === helloEvent ? (JSON.parse(first.data) as Partial<EventPayloads['hello']>) : null;
   } catch {
      // A hello that is not JSON announces nothing.
   }

   const { keepAliveMs, silenceFactor } = hello ?? {};
   // A browser timer waits at most 2147483647 ms (https://developer.mozilla.org/en-US/docs/Web/API/Window/setTimeout#maximum_delay_value).
   if (typeof keepAliveMs !== 'number' || typeof silenceFactor !== 'number' || !(keepAliveMs >= 1 && silenceFactor >= 1 && keepAliveMs * silenceFactor <= 0x7fffffff)) {
      throw new Error(`The event stream sent ${first.type} before a valid hello.`);
   }

   return keepAliveMs * silenceFactor;
}

function wait(ms: number, signal: AbortSignal): Promise<void> {
   return new Promise((resolve) => {
      const timer = setTimeout(done, ms);
      signal.addEventListener('abort', done, { once: true });
      function done() {
         clearTimeout(timer);
         signal.removeEventListener('abort', done);
         resolve();
      }
   });
}
