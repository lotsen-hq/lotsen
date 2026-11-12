// The flow tests' shared setup on real worlds: a backend per worker, a tour seeded through the real operations, and the app rendered on a world's transport.
import { cleanup, getConfig, render, screen, waitFor } from '@testing-library/svelte';
import { afterEach, aroundEach, beforeAll, expect, inject } from 'vitest';
import App from '../src/App.svelte';
import { LotsenApp } from '../src/lib/app.svelte';
import type { Schedule } from '../src/lib/eventStream';
import { SseParser } from '../src/lib/sse';
import type { Familiarity, StopKind, Tour, UpdatedIteration } from '../src/lib/types';
import { connectBackend, type Backend, type ClaudeSession, type EventName, type Transport, type World } from './backend/backend';
import { stubRangeLayout } from './rangeLayout';
import { runWithSignal, testSignal } from './testSignal.mjs';
import { openTourList, tourButton, tourSwitcherName } from './tourPicker';

/** The pull request URL that every world's platform recognizes, and its label in the tour list. */
export const prUrl = 'https://platform.test/shop/pull/42';
export const prLabel = 'shop #42';

let shared: Backend | undefined;

/** How long the build of one template may take. It can far outlast a test, and every test that uses the template would otherwise wait for it within its own timeout. */
const templateBuildTimeout = 120_000;

/**
 * Returns the worker's backend on the run's test host. Every world it started stops after each test, once the app is unmounted. A template that it returns is built in a `beforeAll` of the
 * suite that declares it.
 */
export function backend(): Backend {
   if (shared === undefined) {
      const connected = connectBackend(inject('lotsenHost'));
      shared = {
         ...connected,
         template(name, build) {
            const template = connected.template(name, build);
            beforeAll(() => connected.prepare(template), templateBuildTimeout);
            return template;
         },
      };
   }

   return shared;
}

/** One stop of a seeded plan: its title and kind, and the content that Claude writes for it, or none for a pending stop. */
export interface SeededStop {
   title: string;
   kind: StopKind;
   /** The `put_stop` input without the tour and the stop, or undefined for a stop that stays pending. */
   content?: Record<string, unknown>;
}

/** What {@link seedTour} builds: the files at the base and the head, and the plan by chapter. */
export interface TourSpec {
   /** The files of the pull request's target, by path. */
   base?: Record<string, string>;
   /** The files that the pull request's source changes, by path, with null for a deleted file. */
   head?: Record<string, string | null>;
   familiarity?: Familiarity;
   planFirst?: boolean;
   /**
    * The chapters of the plan, each with its stops, or undefined for a tour without a plan. A chapter's part is the title of the part that it belongs to, and the plan's parts are those titles in
    * the order of their first chapter.
    */
   chapters?: { title: string; part?: string; stops: SeededStop[] }[];
   /** Whether the session that creates the tour stays open and bound, which it does unless this is false. */
   bound?: boolean;
}

/** A seeded tour: its ID, the IDs of its stops in plan order, the commits, and the session that created it, which drops when the spec does not keep it bound. */
export interface SeededTour {
   tourId: string;
   stopIds: string[];
   base: string;
   head: string;
   claude: ClaudeSession | null;
}

/** The files of a small cart change, which most specs extend. */
export const cartBase: Record<string, string> = { 'src/cart.ts': 'export function total(lines: number[]): number {\n   return lines.reduce((sum, line) => sum + line, 0);\n}\n' };
export const cartHead: Record<string, string> = {
   'src/cart.ts': 'export function total(lines: number[]): number {\n   return Math.round(lines.reduce((sum, line) => sum + line, 0));\n}\n',
};

/** Returns put_stop content with a headline, a how and the cart change's primary passage. */
export function cartContent(headline: string, how = 'Each add checks the quantity.'): Record<string, unknown> {
   return { headline, how, passages: [{ side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 2, role: 'primary' }] };
}

/** Creates a tour on the world through the real operations: commits, the pull request, a real agent session's create_tour and pin, its plan and its stops' content. */
export async function seedTour(world: World, spec: TourSpec = {}): Promise<SeededTour> {
   const base = await world.repo.commit('main', 'The base', spec.base ?? cartBase);
   const head = await world.repo.commit('feature', 'The change', spec.head ?? cartHead, base);
   await world.pullRequest.set(head, base);
   const claude = await world.claude();
   const tour = (await claude.call('create_tour', { prUrl, familiarity: spec.familiarity ?? 'some', ...(spec.planFirst ? { planFirst: true } : {}) })) as Tour;
   await world.idle();
   const stopIds: string[] = [];
   if (spec.chapters !== undefined) {
      const partTitles = [...new Set(spec.chapters.flatMap((chapter) => (chapter.part === undefined ? [] : [chapter.part])))];
      const parts = partTitles.map((title, index) => ({ id: `p${index + 1}`, title }));
      const chapters = spec.chapters.map((chapter, index) => ({
         id: `c${index + 1}`,
         title: chapter.title,
         ...(chapter.part === undefined ? {} : { part: parts[partTitles.indexOf(chapter.part)]!.id }),
      }));
      const planned = (await claude.call('set_plan', {
         tourId: tour.id,
         ...(parts.length === 0 ? {} : { parts }),
         chapters,
         stops: spec.chapters.flatMap((chapter, index) => chapter.stops.map((stop) => ({ title: stop.title, kind: stop.kind, chapter: chapters[index]!.id }))),
      })) as Tour;
      stopIds.push(...planned.plan!.stops.map((stop) => stop.id));
      const stops = spec.chapters.flatMap((chapter) => chapter.stops);
      for (const [index, stop] of stops.entries()) {
         if (stop.content !== undefined) {
            await claude.call('put_stop', { tourId: tour.id, stopId: stopIds[index], ...stop.content });
         }
      }
   }

   if (spec.bound === false) {
      await claude.drop();
      return { tourId: tour.id, stopIds, base, head, claude: null };
   }

   return { tourId: tour.id, stopIds, base, head, claude };
}

/** Opens a session that attaches the tour, as Claude does in a world copied from a template, whose sessions did not survive the seal. */
export async function attachClaude(world: World, tourId: string, options: { confirm?: boolean } = {}): Promise<ClaudeSession> {
   const claude = await world.claude(options);
   await claude.call('attach_tour', { tourId });
   return claude;
}

let started: { app: LotsenApp; world: World; transport: Transport } | undefined;
let restoreRanges: (() => void) | undefined;

// A hook that throws skips the hooks after it, so the worlds stop last and a stop that fails still leaves the next test an empty page.
afterEach(async () => {
   started?.app.stop();
   started = undefined;
   cleanup();
   restoreRanges?.();
   restoreRanges = undefined;
   await shared?.disposeAll();
});

// A test that times out keeps running after vitest moves on, and vitest aborts only that test's signal, so its code finds its own signal.
aroundEach(async (runTest, context) => {
   await runWithSignal(context.signal, runTest);
});

/** Renders the page of the app, and throws instead when the calling test timed out, so the late code of that test mounts nothing in the next test's page. */
export function renderApp(app: LotsenApp): void {
   testSignal()?.throwIfAborted();
   render(App, { props: { app } });
}

/** Returns the app on the world's transport, rendered and live, with the UI credential stored as a page that exchanged its code holds it. */
export async function startApp(world: World, options: { transport?: Transport; retryDelaysMs?: number[]; silenceTimer?: Schedule } = {}): Promise<{ app: LotsenApp; transport: Transport }> {
   const transport = options.transport ?? world.transport();
   // A real tour's diff mounts CodeMirror, which measures text through ranges.
   restoreRanges ??= stubRangeLayout();
   sessionStorage.setItem('lotsen.uiCredential', world.uiCredential);
   const app = new LotsenApp({ environment: { fetch: transport.fetch, location, history, storage: sessionStorage }, retryDelaysMs: options.retryDelaysMs ?? [5], silenceTimer: options.silenceTimer });
   renderApp(app);
   started = { app, world, transport };
   await screen.findByRole('button', { name: tourSwitcherName });
   // A full run starts many worlds at once, so the page's first connection may take longer than a test's default wait.
   await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
   await waitFor(() => expect(app.toursLoaded).toBe(true), { timeout: 10_000 });
   return { app, transport };
}

/** Opens the tour of the label from the tour list, and waits for its plan and then for the page to settle. */
export async function openTour(app: LotsenApp, label = prLabel): Promise<void> {
   await clickTour(label);
   await waitFor(() => expect(app.selectedPlan).toBeDefined());
   await settled(app);
}

/** Clicks the tour of the label in the tour list, without waiting for its plan or for the page to settle. */
export async function clickTour(label = prLabel): Promise<void> {
   const list = await openTourList();
   const button = await waitFor(() => {
      const found = list.getAllByRole('button', tourButton).find((candidate) => candidate.textContent?.includes(label));
      if (found === undefined) {
         throw new Error(`The tour list has no tour labeled ${label}.`);
      }

      return found;
   });
   button.click();
}

/** Opens the stop, and waits until the page shows it and then for the page to settle. */
export async function openStop(app: LotsenApp, stopId: string): Promise<void> {
   app.openStop(stopId);
   await waitFor(() => expect(app.selectedStop?.id).toBe(stopId));
   await settled(app);
}

/**
 * Waits until the page and its world are quiet: no request of the page is in flight, no background job of the server runs, and the page has read every event that the server published.
 *
 * It repeats the waits until no new request and no new event come after the page's requests end, through the other waits and a macrotask after them, because the page can chain a request
 * on an answer only after the answer leaves the count. One call of the host waits for the jobs and then writes the comment that the event wait reads. No wait outlasts a test's hold: a request
 * held at its answer does not count, the job wait ends once a job waits on the world's pull request (`World.quiet`), and the event wait is skipped while the test stalls the event streams.
 *
 * @throws An `Error` that names the wait that did not end, once the waits take longer than the library's `asyncUtilTimeout`.
 */
export async function settled(app: LotsenApp): Promise<void> {
   if (started?.app !== app) {
      throw new Error('settled waits only for the app that startApp started.');
   }

   const { world, transport } = started;
   const timeoutMs = getConfig().asyncUtilTimeout;
   const deadline = Date.now() + timeoutMs;
   const within = async (wait: Promise<void>, unfinished: () => string): Promise<void> => {
      let timer: ReturnType<typeof setTimeout> | undefined;
      const late = new Promise<never>((_, reject) => {
         timer = setTimeout(() => reject(new Error(`The page did not settle within ${timeoutMs} ms: ${unfinished()}`)), Math.max(0, deadline - Date.now()));
      });
      try {
         await Promise.race([wait, late]);
      } finally {
         clearTimeout(timer);
      }
   };

   for (;;) {
      await within(transport.requestsDone(), () => `${transport.inFlight()} of its requests are still in flight.`);
      const seen = transport.activity();
      let quiet = false;
      const quieted = async (comment?: string) => {
         await world.quiet(comment);
         quiet = true;
      };
      await within(transport.streamsStalled() ? quieted() : transport.streamsRead(quieted), () =>
         quiet ? 'it has not read every event that the server published.' : 'a background job of the server still runs.',
      );
      await new Promise((resolve) => setTimeout(resolve, 0));
      if (transport.activity() === seen && transport.inFlight() === 0) {
         return;
      }

      if (Date.now() >= deadline) {
         throw new Error(`The page did not settle within ${timeoutMs} ms: it kept sending requests or hearing events.`);
      }
   }
}

/** Returns the tour as the reviewer reads it now. */
export async function readTour(world: World, tourId: string): Promise<Tour> {
   return world.reviewer.call('read_state', { tourId });
}

/** A held call of an operation, from {@link withHolds}. */
export interface AnsweredHold {
   /** Completes once the server has answered the held call, so a test can write after it and know the answer predates the write. */
   answered: Promise<void>;
   /** Hands the server's answer to the page, and completes once the page's fetch has it. */
   release(): Promise<void>;
}

/** The page's network with the holds that a test needs to order its own writes around what the page has heard from the server. */
export interface HeldTransport extends Transport {
   /** Holds the page's next call of the operation after the server answered it. */
   holdAnswered(operation: string): AnsweredHold;
   /** Ends the open event streams and keeps the page's reconnects waiting, so the page hears of no write until the returned release runs and the page reconnects and reloads. */
   holdEvents(): () => void;
}

/** Returns the transport with the holds of {@link HeldTransport}. */
export function withHolds(inner: Transport): HeldTransport {
   const holds: { operation: string; reached: () => void; released: Promise<void>; delivered: () => void }[] = [];
   let events: Promise<void> | null = null;
   const fetchFn: typeof fetch = async (input, init) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
      if (path === '/api/events' && events !== null) {
         await events;
      }

      const index = holds.findIndex((hold) => path === `/api/ops/${hold.operation}`);
      if (index < 0) {
         return inner.fetch(input, init);
      }

      const [hold] = holds.splice(index, 1);
      const response = await inner.fetch(input, init);
      const body = await response.arrayBuffer();
      hold!.reached();
      await hold!.released;
      hold!.delivered();
      return new Response(body, { status: response.status, headers: response.headers });
   };
   return {
      ...inner,
      fetch: fetchFn,
      holdAnswered(operation) {
         let reached!: () => void;
         let release!: () => void;
         let delivered!: () => void;
         const answered = new Promise<void>((resolve) => (reached = resolve));
         const released = new Promise<void>((resolve) => (release = resolve));
         const handed = new Promise<void>((resolve) => (delivered = resolve));
         holds.push({ operation, reached, released, delivered });
         return {
            answered,
            release: async () => {
               release();
               await handed;
            },
         };
      },
      holdEvents() {
         let release!: () => void;
         events = new Promise<void>((resolve) => (release = resolve));
         inner.cutEventStreams();
         return () => {
            events = null;
            release();
         };
      },
   };
}

/**
 * Commits the files onto `parent` as the head of the pull request's next iteration, and returns the new head. The server learns of it only from a check, such as {@link checkIteration}, or
 * from an update.
 */
export async function pushIteration(world: World, commits: { base: string; parent: string }, files: Record<string, string | null>, iteration: number): Promise<string> {
   const head = await world.repo.commit('feature', `Iteration ${iteration}`, files, commits.parent);
   await world.pullRequest.set(head, commits.base, { iteration });
   return head;
}

/** Checks the tour's pull request as the reviewer, so the server stores and publishes what it finds, and returns once no background job runs. */
export async function checkIteration(world: World, tourId: string): Promise<void> {
   await world.reviewer.call('check_iteration', { tourId });
   await world.idle();
}

/** Updates the tour as the reviewer to the available iteration that a read of the tour returns, as the page's notice shows it, and returns the update's output. */
export async function updateIteration(world: World, tourId: string): Promise<UpdatedIteration> {
   const { availableIteration } = await readTour(world, tourId);
   if (availableIteration === null || availableIteration === undefined) {
      throw new Error(`Tour ${tourId} has no available iteration to update to.`);
   }

   return world.reviewer.call('update_iteration', { tourId, pin: availableIteration.pin });
}

/** Returns the transport with event streams that can hold back the events of chosen kinds while the others pass. */
export function withEventHolds<T extends Transport>(inner: T): T & { holdKinds(...kinds: EventName[]): () => void } {
   const heldKinds = new Set<string>();
   const held: { controller: ReadableStreamDefaultController<Uint8Array>; chunk: Uint8Array }[] = [];
   const encoder = new TextEncoder();

   function filtered(response: Response): Response {
      const reader = response.body!.getReader();
      const decoder = new TextDecoder();
      const parser = new SseParser();
      const body = new ReadableStream<Uint8Array>(
         {
            async pull(controller) {
               for (;;) {
                  const { done, value } = await reader.read();
                  if (done) {
                     controller.close();
                     return;
                  }

                  let passed = false;
                  for (const event of parser.push(decoder.decode(value, { stream: true }))) {
                     const chunk = encoder.encode(
                        `${event.type === 'message' ? '' : `event: ${event.type}\n`}${event.data
                           .split('\n')
                           .map((line) => `data: ${line}\n`)
                           .join('')}\n`,
                     );
                     if (heldKinds.has(event.type)) {
                        held.push({ controller, chunk });
                     } else {
                        controller.enqueue(chunk);
                        passed = true;
                     }
                  }

                  // A pull ends only once an event passed, so the page's pending read always gets data.
                  if (passed) {
                     return;
                  }
               }
            },
            cancel: (reason) => reader.cancel(reason),
         },
         // Each read of the inner stream then follows a read of the page, which keeps Transport.streamsRead true for a page behind this filter.
         { highWaterMark: 0 },
      );
      return new Response(body, { status: response.status, headers: response.headers });
   }

   const fetchFn: typeof fetch = async (input, init) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
      const response = await inner.fetch(input, init);
      return path === '/api/events' && response.ok && response.body !== null ? filtered(response) : response;
   };
   return {
      ...inner,
      fetch: fetchFn,
      holdKinds(...kinds) {
         for (const kind of kinds) {
            heldKinds.add(kind);
         }

         return () => {
            for (const kind of kinds) {
               heldKinds.delete(kind);
            }

            for (const { controller, chunk } of held.splice(0)) {
               try {
                  controller.enqueue(chunk);
               } catch {
                  // The stream closed while its event was held, and the page's reload after it reconnects brings what the event held.
               }
            }
         };
      },
   };
}
