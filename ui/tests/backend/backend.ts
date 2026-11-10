// The UI's tests and the demo on real lotsen servers: a client of the test host's control API (tests/Lotsen.TestHost), which starts one world, a real server with its own data
// directory, clock and test platform, per test. It holds no rule of the server's and fakes nothing but the network, so every decision that a test meets is the server's own.
import type { EventPayloads, OperationError, OperationName, Operations, TourControls } from '$lib/contract.generated';

/** How to reach a running test host: its control API's URL and the bearer token that each control request carries. */
export interface HostAddress {
   url: string;
   token: string;
}

/** A refusal of an operation, with the server's status and its error. */
export class Refusal extends Error {
   constructor(
      readonly operation: string,
      readonly status: number,
      readonly error: OperationError | null,
      reason: string,
   ) {
      super(`${operation} was refused with ${status}: ${reason}`);
      this.name = 'Refusal';
   }
}

/** An operation's caller: the reviewer through the UI credential, or Claude through a real agent session. */
export interface Client {
   /** Calls the operation, and returns its output, or throws a {@link Refusal}. */
   call<N extends OperationName>(name: N, input: Operations[N]['input']): Promise<Operations[N]['output']>;
}

/** A real agent session on a world's server, as an adapter holds it. */
export interface ClaudeSession {
   /** The session's ID on the server, which its requests and replies name. */
   readonly sessionId: string;
   /** Calls an agent operation as the session, and returns its output, or throws a {@link Refusal} with the server's reason. */
   call(name: string, input: Record<string, unknown>): Promise<unknown>;
   /** Returns the payload of the session's next event of the kind, such as `question`, and drops the events of other kinds before it. It throws when none comes in time. */
   next(kind: SessionEventKind, timeoutMs?: number): Promise<SessionEventPayload>;
   /** Drops the session's connection, as an adapter that exits does, and completes once the server settled its end. */
   drop(): Promise<void>;
}

/** The kinds of the session events. */
export type SessionEventKind = 'question' | 'answers' | 'probe' | 'tourTakenOver' | 'threadDeleted';

/** A session event's payload, as the server sends it. */
export type SessionEventPayload = Record<string, unknown> & { tourId: string };

/** A fault that the transport puts in place of the server's answer, as a network would. */
export type Fault = { status: 500 | 502 | 503 } | 'network';

/** One request that the page sent through the transport, with its operation and input for an operation call. */
export interface SentRequest {
   path: string;
   operation: string | null;
   input: Record<string, unknown> | null;
}

/**
 * The page's network to one world's server. Holds and faults live only here, so a test fakes the network and never the server: a specific refusal must come from a real cause.
 */
export interface Transport {
   /** The `fetch` that the page uses, with the paths that it names resolved against the world's server. */
   readonly fetch: typeof fetch;
   /** Every request that the page sent, in the order sent. */
   readonly requests: readonly SentRequest[];
   /**
    * Holds the next call of the operation: at `request`, before it reaches the server, or at `response`, after the server answered and before the page reads the answer. It returns the release,
    * and the held call continues once the release runs.
    */
   hold(operation: string, at?: 'request' | 'response'): () => void;
   /** Answers the next call of the operation with the fault, without the server. */
   fail(operation: string, fault: Fault): void;
   /** Hands the page the server's answer to the next call of the operation once the delay in milliseconds has passed, with no release for the test to run. */
   delay(operation: string, ms: number): void;
   /** Lets the body of the server's answer to the next call of the operation arrive the delay in milliseconds after its headers, as a slow network does. The page gets both once the body is read. */
   slowBody(operation: string, ms: number): void;
   /** Returns the number of the page's requests in flight, without the event streams and the requests that wait on a {@link Transport.hold}. */
   inFlight(): number;
   /** Completes once no request of the page is in flight, as {@link Transport.inFlight} counts them. */
   requestsDone(): Promise<void>;
   /** Returns a count that grows with each request that the page sends, an event stream's included, and with each event that an event stream passes to the page. */
   activity(): number;
   /**
    * Writes a comment line of a new text with `write`, and completes once the page has read every event that the server published before that line, on each event stream open at the call. A
    * stream that ends counts as read. `write` must write the line on every open event stream of the server, after every event that the server published before, as {@link World.quiet} does.
    *
    * The stream passes the page the bytes up to that line as one chunk. The page reads its next chunk only once it has applied the events of the one before (`readEventStream`), so its next
    * read shows that it applied every event before the comment.
    */
   streamsRead(write: (text: string) => Promise<void>): Promise<void>;
   /** Returns whether a stall of the event streams is in effect, from the call that sets it until its release runs. */
   streamsStalled(): boolean;
   /** Ends every open event stream, as a dropped connection does, so the page reconnects. */
   cutEventStreams(): void;
   /**
    * Stops passing the bytes of every open event stream, while each stays connected, as a network does that stops delivering. Streams opened later pass. It returns the release, which passes what
    * the stalled streams held.
    */
   stallEventStreams(): () => void;
   /**
    * Stalls every open event stream as {@link Transport.stallEventStreams} does, but each stream first passes its bytes up to the end of its next event of the type. It returns the release, which
    * passes what the stalled streams held, and a stream that has passed no such event by then does not stall.
    */
   stallEventStreamsAfter(type: string): () => void;
   /** Returns a promise that completes once a call of the operation has reached the server and answered, from now on. */
   answered(operation: string): Promise<void>;
}

/** Options of a new world. */
export interface WorldOptions {
   /** The template whose data directory the world starts from, or none for an empty one. */
   template?: Template;
   /** The time the world's clock starts at, as ISO 8601, or none for the host's start, 2026-01-01T12:00:00Z. */
   clockStart?: string;
}

/** A sealed world's data directory, which later worlds copy, built once per host by its `build`. */
export interface Template<T = unknown> {
   readonly name: string;
   readonly build: (world: World) => Promise<T>;
}

/** One real server and what it stands on. Only what no operation can do goes through here: commits, the pull request, the clock, restarts. */
export interface World {
   readonly id: string;
   /** What the world's template built, or undefined for a world without one. */
   readonly seed: unknown;
   /** The server's base URL, which a restart changes. */
   readonly url: string;
   /** The UI credential of the server's current run, which a restart changes. */
   readonly uiCredential: string;
   readonly repo: { commit(branch: string, message: string, files: Record<string, string | null>, parent?: string): Promise<string> };
   /** The pull request that the world's platform reports for every pull request URL. */
   readonly pullRequest: {
      set(source: string, target: string, options?: { iteration?: number; state?: 'active' | 'completed' | 'abandoned' }): Promise<void>;
      fail(reason: string): Promise<void>;
      hold(): Promise<void>;
      release(): Promise<void>;
   };
   /** The world's manual clock, whose advance completes once each timer that came due has fired and the server's background jobs are idle again after it. */
   readonly clock: { advance(ms: number): Promise<void> };
   /** The reviewer through the UI credential, for setup and assertions. */
   readonly reviewer: Client;
   /** Completes once no background job of the server runs, such as a pin or an iteration check. */
   idle(): Promise<void>;
   /**
    * Completes as {@link World.idle} does, or once a resolve of the pull request waits on the test: on a hold, or on a pull request that the test has not set. It then writes the comment line of
    * the text, if any, on every open event stream of the server, after every event that the server published before.
    */
   quiet(comment?: string): Promise<void>;
   /** Stops the server and starts it again on the same data directory, with new credentials. */
   restart(): Promise<void>;
   /** Stops the world and removes its data directory. */
   dispose(): Promise<void>;
   /** Opens a real agent session, which acks each probe at once unless `confirm` is false. */
   claude(options?: { confirm?: boolean }): Promise<ClaudeSession>;
   /** Returns the page's network to the world's server, one per call. */
   transport(): Transport;
   /** Returns a one-time code for the UI. */
   uiCode(): Promise<string>;
}

/** The test host as a test sees it: new worlds, and templates. */
export interface Backend {
   world(options?: WorldOptions): Promise<World>;
   template<T>(name: string, build: (world: World) => Promise<T>): Template<T>;
   /** Builds the template, or waits for the build that this backend or another worker's started. */
   prepare(template: Template): Promise<void>;
   /** Disposes every world that `world` returned and no one has disposed. A template's build keeps its own world, and stops it when the build seals or fails. */
   disposeAll(): Promise<void>;
}

/** The server's event names, for the tests that wait for one on the page's stream. */
export type EventName = keyof EventPayloads;

/** What the host's `world.start` returns for a world: its ID, its server's base URL and its UI credential. */
export interface WorldInfo {
   world: string;
   url: string;
   uiCredential: string;
}

/**
 * Returns a client of a world that another client of the host started, such as the demo page's client of the world that the demo's dev server started for the page. The world keeps running when
 * this client is dropped, and the client's `dispose` stops it.
 */
export function joinWorld(host: HostAddress, info: WorldInfo, seed: unknown, fetchFn: typeof fetch = globalThis.fetch.bind(globalThis)): World {
   return createWorld(info, seed, controlOf(host, fetchFn), fetchFn);
}

function controlOf(host: HostAddress, fetchFn: typeof fetch): Control {
   return async <T = Record<string, unknown>>(verb: string, body: Record<string, unknown> = {}): Promise<T> => {
      const response = await fetchFn(`${host.url}/control/${verb}`, {
         method: 'POST',
         headers: { Authorization: `Bearer ${host.token}`, 'Content-Type': 'application/json' },
         body: JSON.stringify(body),
      });
      const answer = await bodyOf(response);
      if (answer.json === null) {
         throw new Error(`${verb} answered ${response.status} with ${answer.unreadable}.`);
      }

      if (!response.ok) {
         throw new Error(String(answer.json.error ?? `${verb} answered ${response.status}`));
      }

      return answer.json as T;
   };
}

/** Returns the response's body as JSON, or null with a description of its text when the body is not JSON, such as the empty body of a bare 401 or of an unhandled exception's 500. */
async function bodyOf(response: Response): Promise<{ json: Record<string, unknown> } | { json: null; unreadable: string }> {
   const text = await response.text();
   try {
      return { json: JSON.parse(text) as Record<string, unknown> };
   } catch {
      return { json: null, unreadable: text === '' ? 'an empty body' : `a body that is not JSON: ${text.slice(0, 200)}` };
   }
}

/** Returns a backend on the running host. `fetchFn` defaults to the global `fetch`. */
export function connectBackend(host: HostAddress, fetchFn: typeof fetch = globalThis.fetch.bind(globalThis)): Backend {
   const control = controlOf(host, fetchFn);
   const built = new Map<string, Promise<unknown>>();
   const open = new Set<World>();

   async function ensure(template: Template): Promise<unknown> {
      let building = built.get(template.name);
      if (building === undefined) {
         building = (async () => {
            // The host gives one claim of a name the world that builds it, and the others wait for its seal. A build world that stops unsealed fails the claims that wait.
            const known = await control<{ has: true; data?: unknown } | { has: false; build: WorldInfo }>('template.claim', { name: template.name });
            if (known.has) {
               return known.data;
            }

            // The build's world stays out of `open`, so the teardown of a test that timed out during the build lets the build finish for the tests that wait for it. The seal or the
            // failure below stops the world.
            const world = createWorld(known.build, undefined, control, fetchFn);
            try {
               const data = await template.build(world);
               await control('world.seal', { world: world.id, name: template.name, data: data ?? null });
               return data;
            } catch (error) {
               await world.dispose().catch(() => undefined);
               throw error;
            }
         })();
         built.set(template.name, building);
      }

      return building;
   }

   async function start(options: WorldOptions = {}): Promise<World> {
      const seed = options.template === undefined ? undefined : await ensure(options.template);
      const info = await control<WorldInfo>('world.start', {
         ...(options.template === undefined ? {} : { template: options.template.name }),
         ...(options.clockStart === undefined ? {} : { clockStart: options.clockStart }),
      });
      const world = createWorld(info, seed, control, fetchFn);
      open.add(world);
      const dispose = world.dispose.bind(world);
      world.dispose = async () => {
         open.delete(world);
         await dispose();
      };
      return world;
   }

   return {
      world: start,
      template: <T>(name: string, build: (world: World) => Promise<T>): Template<T> => ({ name, build }),
      async prepare(template) {
         await ensure(template);
      },
      async disposeAll() {
         await Promise.all([...open].map((world) => world.dispose()));
      },
   };
}

type Control = <T = Record<string, unknown>>(verb: string, body?: Record<string, unknown>) => Promise<T>;

function createWorld(info: WorldInfo, seed: unknown, control: Control, fetchFn: typeof fetch): World & { dispose(): Promise<void> } {
   let { url, uiCredential } = info;
   const id = info.world;
   const transports: TransportState[] = [];
   let restarting = false;
   const reviewer: Client = {
      async call(name, input) {
         return (await operation(fetchFn, url, uiCredential, name, input as Record<string, unknown>)) as never;
      },
   };

   const world: World & { dispose(): Promise<void> } = {
      id,
      seed,
      get url() {
         return url;
      },
      get uiCredential() {
         return uiCredential;
      },
      repo: {
         async commit(branch, message, files, parent) {
            return (await control<{ commit: string }>('repo.commit', { world: id, branch, message, files, ...(parent === undefined ? {} : { parent }) })).commit;
         },
      },
      pullRequest: {
         set: async (source, target, options = {}) => void (await control('pr.set', { world: id, source, target, ...options })),
         fail: async (reason) => void (await control('pr.fail', { world: id, reason })),
         hold: async () => void (await control('pr.hold', { world: id })),
         release: async () => void (await control('pr.release', { world: id })),
      },
      clock: { advance: async (ms) => void (await control('clock.advance', { world: id, ms })) },
      reviewer,
      idle: async () => void (await control('jobs.idle', { world: id })),
      quiet: async (comment) => void (await control('jobs.quiet', { world: id, ...(comment === undefined ? {} : { comment }) })),
      async restart() {
         // On Windows a connection to the stopping server can stay unanswered, so the page's requests are refused until the new server listens.
         restarting = true;
         try {
            for (const transport of transports) {
               transport.cutEventStreams();
            }

            ({ url, uiCredential } = await control<{ url: string; uiCredential: string }>('world.restart', { world: id }));
         } finally {
            restarting = false;
         }
      },
      async dispose() {
         for (const transport of transports) {
            transport.cutEventStreams();
         }

         await control('world.stop', { world: id });
      },
      async claude(options = {}) {
         const opened = await control<{ session: string; sessionId: string }>('claude.open', { world: id, confirm: options.confirm ?? true });
         return {
            sessionId: opened.sessionId,
            async call(name, input) {
               const answer = await control<{ output?: unknown; refusal?: string }>('claude.call', { world: id, session: opened.session, operation: name, input });
               if (answer.refusal !== undefined) {
                  throw new Refusal(name, 0, null, answer.refusal);
               }

               return answer.output;
            },
            async next(kind, timeoutMs = 10_000) {
               const event = await control<{ type?: string; data?: SessionEventPayload }>('claude.next', { world: id, session: opened.session, kind, timeoutMs });
               if (event.data === undefined) {
                  throw new Error(`Session ${opened.sessionId} got no ${kind} event within ${timeoutMs} ms.`);
               }

               return event.data;
            },
            drop: async () => void (await control('claude.drop', { world: id, session: opened.session })),
         };
      },
      transport() {
         const transport = createTransport(
            () => url,
            () => restarting,
            fetchFn,
         );
         transports.push(transport);
         return transport;
      },
      uiCode: async () => (await control<{ code: string }>('ui.code', { world: id })).code,
   };
   return world;
}

async function operation(fetchFn: typeof fetch, url: string, credential: string, name: string, input: Record<string, unknown>): Promise<unknown> {
   const response = await fetchFn(`${url}/api/ops/${name}`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${credential}`, 'Content-Type': 'application/json', 'Lotsen-Prose': 'html' },
      body: JSON.stringify(input),
   });
   const body = await bodyOf(response);
   if (body.json === null) {
      throw response.ok ? new Error(`${name} answered ${response.status} with ${body.unreadable}.`) : new Refusal(name, response.status, null, body.unreadable);
   }

   const answer = body.json as { output?: unknown; controls?: TourControls } & Partial<OperationError>;
   if (!response.ok) {
      throw new Refusal(name, response.status, answer as OperationError, answer.reason ?? '');
   }

   return answer.output;
}

interface TransportState extends Transport {
   cutEventStreams(): void;
}

function createTransport(baseUrl: () => string, serverDown: () => boolean, fetchFn: typeof fetch): TransportState {
   const requests: SentRequest[] = [];
   const holds: { operation: string; at: 'request' | 'response'; released: Promise<void> }[] = [];
   const faults: { operation: string; fault: Fault }[] = [];
   const delays: { operation: string; ms: number }[] = [];
   const slowBodies: { operation: string; ms: number }[] = [];
   let inFlight = 0;
   const drained: (() => void)[] = [];
   let stalls = 0;
   let eventsPassed = 0;
   const answeredWaiters: { operation: string; resolve: () => void }[] = [];
   const streams = new Set<AbortController>();
   const taps = new Set<StreamTap>();

   function take<T extends { operation: string }>(list: T[], name: string | null): T | undefined {
      const index = name === null ? -1 : list.findIndex((entry) => entry.operation === name);
      return index < 0 ? undefined : list.splice(index, 1)[0];
   }

   const transportFetch: typeof fetch = async (input, init) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
      const name = /^\/api\/ops\/([a-z_]+)$/.exec(path)?.[1] ?? null;
      requests.push({ path, operation: name, input: name === null || typeof init?.body !== 'string' ? null : (JSON.parse(init.body) as Record<string, unknown>) });

      if (serverDown()) {
         throw new TypeError('fetch failed');
      }

      const fault = take(faults, name);
      if (fault !== undefined) {
         if (fault.fault === 'network') {
            throw new TypeError('fetch failed');
         }

         return new Response(null, { status: fault.fault.status });
      }

      const hold = take(holds, name);
      const delay = take(delays, name);
      const slow = take(slowBodies, name);
      // A request held at the response stage never counts, and one held at the request stage counts from its release, so a test's hold never keeps the page from settling.
      const counted = path !== '/api/events' && hold?.at !== 'response';
      if (hold?.at === 'request') {
         await hold.released;
      }

      if (counted) {
         inFlight++;
      }

      // The page's own signal may come from another realm than the fetch, so the transport aborts through a controller of its own.
      const controller = new AbortController();
      const outer = init?.signal;
      outer?.addEventListener('abort', () => controller.abort(), { once: true });
      if (outer?.aborted) {
         controller.abort();
      }

      if (path === '/api/events') {
         streams.add(controller);
      }

      try {
         const fetched = await fetchFn(`${baseUrl()}${path}`, { ...init, signal: controller.signal });
         const response = slow === undefined ? fetched : slowed(fetched, slow.ms);
         if (path === '/api/events') {
            settle(name);
            return tapped(response, taps, () => eventsPassed++);
         }

         // The transport reads the whole body before the request leaves the count, so the page's own read of the body never outlasts the count. The read also fixes a held answer at the time the server sent it.
         const body = response.body === null ? null : await response.arrayBuffer();
         if (hold?.at === 'response') {
            await hold.released;
         }

         if (delay !== undefined) {
            await new Promise((resolve) => setTimeout(resolve, delay.ms));
         }

         settle(name);
         return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
      } finally {
         if (counted && --inFlight === 0) {
            for (const done of drained.splice(0)) {
               done();
            }
         }

         if (path !== '/api/events') {
            streams.delete(controller);
         }
      }
   };

   /** Returns the release of a stall of the taps, which opens their gates. {@link Transport.streamsStalled} counts the stall until the release runs. */
   function stall(stalled: readonly StreamTap[]): () => void {
      stalls++;
      let released = false;
      return () => {
         if (!released) {
            released = true;
            stalls--;
         }

         for (const tap of stalled) {
            tap.stallAfter = null;
            tap.gate = null;
            tap.open();
         }
      };
   }

   function settle(name: string | null): void {
      for (const waiter of answeredWaiters.filter((entry) => entry.operation === name)) {
         answeredWaiters.splice(answeredWaiters.indexOf(waiter), 1);
         waiter.resolve();
      }
   }

   return {
      fetch: transportFetch,
      requests,
      hold(operation, at = 'response') {
         let release!: () => void;
         const released = new Promise<void>((resolve) => (release = resolve));
         holds.push({ operation, at, released });
         return release;
      },
      fail(operation, fault) {
         faults.push({ operation, fault });
      },
      delay(operation, ms) {
         delays.push({ operation, ms });
      },
      slowBody(operation, ms) {
         slowBodies.push({ operation, ms });
      },
      inFlight: () => inFlight,
      requestsDone: () => (inFlight === 0 ? Promise.resolve() : new Promise<void>((resolve) => drained.push(resolve))),
      activity: () => requests.length + eventsPassed,
      async streamsRead(write) {
         const text = `lotsen-test-read ${++streamComments}`;
         const read = Promise.all([...taps].map((tap) => awaitComment(tap, text)));
         await write(text);
         await read;
      },
      streamsStalled: () => stalls > 0,
      cutEventStreams() {
         for (const stream of streams) {
            stream.abort();
         }

         streams.clear();
      },
      stallEventStreams() {
         const stalled = [...taps];
         for (const tap of stalled) {
            closeGate(tap);
         }

         return stall(stalled);
      },
      stallEventStreamsAfter(type) {
         if (`event: ${type}\n`.length > eventHeadLength) {
            throw new Error(`The event type ${type} is too long for the stream scan, which reads ${eventHeadLength} characters of an event.`);
         }

         const stalled = [...taps];
         for (const tap of stalled) {
            tap.stallAfter = type;
         }

         return stall(stalled);
      },
      answered(operation) {
         return new Promise<void>((resolve) => answeredWaiters.push({ operation, resolve }));
      },
   };
}

/** Returns the response with a body whose first chunk passes once the delay in milliseconds has passed. */
function slowed(response: Response, ms: number): Response {
   if (response.body === null) {
      return response;
   }

   const reader = response.body.getReader();
   let waited = false;
   const body = new ReadableStream<Uint8Array>({
      async pull(controller) {
         if (!waited) {
            waited = true;
            await new Promise((resolve) => setTimeout(resolve, ms));
         }

         const { done, value } = await reader.read();
         if (done) {
            controller.close();
         } else {
            controller.enqueue(value);
         }
      },
      cancel: (reason) => reader.cancel(reason),
   });
   return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
}

/** The gate of one event stream's body, closed while the stream is stalled, and the waits of {@link Transport.streamsRead} on the stream. */
interface StreamTap {
   gate: Promise<void> | null;
   open(): void;
   /** The type of the event after which the gate closes, or null when no stall waits for one. */
   stallAfter: string | null;
   scan: EventScan;
   /** The comment texts that the stream waits for, each with the end of its wait. */
   comments: Map<string, () => void>;
   /** The ends of the waits whose comment the stream passed in its last chunk, which run once the page asks for the next chunk. */
   read: (() => void)[];
   ended: boolean;
}

/** Closes the tap's gate, so the stream passes no more bytes until the gate opens. */
function closeGate(tap: StreamTap): void {
   tap.gate = new Promise((resolve) => (tap.open = resolve));
}

/** Returns a promise that completes once the page asks for the chunk after the one that ends with the comment line of the text, or at once when the stream has ended. */
function awaitComment(tap: StreamTap, text: string): Promise<void> {
   return tap.ended ? Promise.resolve() : new Promise<void>((resolve) => tap.comments.set(text, resolve));
}

/** Removes the tap from `taps`, and ends every wait on it, because a stream that ends counts as read. */
function endTap(tap: StreamTap, taps: Set<StreamTap>): void {
   taps.delete(tap);
   tap.ended = true;
   for (const done of [...tap.comments.values(), ...tap.read.splice(0)]) {
      done();
   }

   tap.comments.clear();
}

let streamComments = 0;

/**
 * Returns the response with a body that passes each chunk once the stream's gate is open, and adds the stream's tap to `taps` until the stream ends. It calls `onEvent` for each event that it
 * passes.
 */
function tapped(response: Response, taps: Set<StreamTap>, onEvent: () => void): Response {
   if (!response.ok || response.body === null) {
      return response;
   }

   const tap: StreamTap = { gate: null, open: () => {}, stallAfter: null, scan: { head: '', line: '', endsWithLineFeed: false }, comments: new Map(), read: [], ended: false };
   const reader = response.body.getReader();
   // The bytes of a chunk after the line that ended the chunk, which pass with the next pull.
   const rest: Uint8Array[] = [];
   const body = new ReadableStream<Uint8Array>(
      {
         async pull(controller) {
            for (const done of tap.read.splice(0)) {
               done();
            }

            const next = rest.shift();
            let read: ReadableStreamReadResult<Uint8Array>;
            try {
               read = next === undefined ? await reader.read() : { done: false, value: next };
            } catch (error) {
               // A cut or a dropped connection fails the read, and the stream has ended then.
               endTap(tap, taps);
               throw error;
            }

            const { done, value } = read;
            while (tap.gate !== null) {
               await tap.gate;
            }

            if (done) {
               endTap(tap, taps);
               controller.close();
               return;
            }

            const cut = cutOf(tap, value, onEvent);
            if (cut.stalls) {
               tap.stallAfter = null;
               closeGate(tap);
            }

            if (cut.comment !== null) {
               tap.read.push(tap.comments.get(cut.comment)!);
               tap.comments.delete(cut.comment);
            }

            if (cut.end < value.length) {
               rest.unshift(value.subarray(cut.end));
            }

            controller.enqueue(value.subarray(0, cut.end));
         },
         cancel(reason) {
            endTap(tap, taps);
            return reader.cancel(reason);
         },
      },
      // The stream pulls only for a read of the page, so a pull shows that the page has applied the chunk before it.
      { highWaterMark: 0 },
   );
   taps.add(tap);
   return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
}

/** Where a stream's scan stands in the line and the event that its last chunk left open. */
interface EventScan {
   /** The start of the open event's text, up to {@link eventHeadLength} characters. */
   head: string;
   /** The start of the open line's text, up to {@link eventHeadLength} characters. */
   line: string;
   endsWithLineFeed: boolean;
}

/** Where the stream ends a chunk: the offset just after the line that ends it, and whether that line ends an event that the tap stalls after, or is a comment that the tap waits for. */
interface ChunkCut {
   end: number;
   stalls: boolean;
   /** The text of the comment that ends the chunk, or null when no comment does. */
   comment: string | null;
}

const lineFeed = 0x0a;
const eventHeadLength = 64;

/**
 * Returns where the stream ends the chunk: just after the end of an event of the type that the tap stalls after, or after a comment line that the tap waits for, whichever comes first, or at
 * the chunk's end. It calls `onEvent` for each `event:` line before that offset, and the scan carries the open line and event to the next chunk.
 *
 * The server writes each event as an `event:` line and a `data:` line, ended by a blank line, and a comment as a `: ` line, ended by a blank line, with LF alone (`EventStreamWriter`). So an LF
 * after an LF ends an event.
 */
function cutOf(tap: StreamTap, chunk: Uint8Array, onEvent: () => void): ChunkCut {
   const { scan } = tap;
   const header = `event: ${tap.stallAfter}\n`;
   let lineStart = 0;
   let eventStart = 0;
   for (let i = chunk.indexOf(lineFeed); i >= 0; i = chunk.indexOf(lineFeed, i + 1)) {
      const line = scan.line + headText(chunk.subarray(lineStart, i));
      scan.line = '';
      lineStart = i + 1;
      if (line.startsWith('event:')) {
         onEvent();
      }

      if (line.startsWith(': ') && tap.comments.has(line.slice(2))) {
         scan.head = (scan.head + headText(chunk.subarray(eventStart, i + 1))).slice(0, eventHeadLength);
         scan.endsWithLineFeed = true;
         return { end: i + 1, stalls: false, comment: line.slice(2) };
      }

      if (!(i === 0 ? scan.endsWithLineFeed : chunk[i - 1] === lineFeed)) {
         continue;
      }

      const head = scan.head + headText(chunk.subarray(eventStart, i + 1));
      scan.head = '';
      eventStart = i + 1;
      if (tap.stallAfter !== null && head.startsWith(header)) {
         scan.endsWithLineFeed = false;
         return { end: i + 1, stalls: true, comment: null };
      }
   }

   scan.line = (scan.line + headText(chunk.subarray(lineStart))).slice(0, eventHeadLength);
   scan.head = (scan.head + headText(chunk.subarray(eventStart))).slice(0, eventHeadLength);
   scan.endsWithLineFeed = chunk.length > 0 ? chunk[chunk.length - 1] === lineFeed : scan.endsWithLineFeed;
   return { end: chunk.length, stalls: false, comment: null };
}

/** Returns the first {@link eventHeadLength} bytes as one character each, which reads the ASCII of an event's `event:` line exactly. */
function headText(bytes: Uint8Array): string {
   return String.fromCharCode(...bytes.subarray(0, eventHeadLength));
}
