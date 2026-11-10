// The tour that the stop flow tests read: two changed files and an unchanged catalog of forty numbered lines each, and a plan of four stops in one chapter, of which the third is pending.
import { fireEvent, screen, waitFor } from '@testing-library/svelte';
import { expect } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { Tour } from '../../src/lib/types';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { openTour, prUrl } from '../harness';
import { tab } from './appPanels';
import { centerFocusedLine, type FocusSpy } from './editorFocus';

/** Forty numbered lines, of which only the changed line names the side. */
export const numbered = (name: string, side: string, changedLine: number) =>
   Array.from({ length: 40 }, (_, index) => (index + 1 === changedLine ? `${name} ${side} line ${index + 1}` : `${name} line ${index + 1}`)).join('\n') + '\n';

export const catalog = numbered('catalog', 'head', 0);

/** The files at the base, where the catalog stays as it is, and the head's changes to the cart's line 20 and the pay's line 21. */
export const baseFiles: Record<string, string> = { 'src/cart.ts': numbered('cart', 'base', 20), 'src/pay.ts': numbered('pay', 'base', 21), 'src/catalog.ts': catalog };
export const headFiles: Record<string, string> = { 'src/cart.ts': numbered('cart', 'head', 20), 'src/pay.ts': numbered('pay', 'head', 21) };

/** Returns a put_stop passage: primary without a label, related with one. */
export function passage(side: 'base' | 'head', path: string, startLine: number, endLine: number, label?: string): Record<string, unknown> {
   return { side, path, startLine, endLine, role: label === undefined ? 'primary' : 'related', ...(label === undefined ? {} : { label }) };
}

export const glance = {
   headline: 'The cart checks its quantities.',
   passages: [
      passage('head', 'src/cart.ts', 5, 7),
      passage('head', 'src/pay.ts', 30, 31, 'caller'),
      passage('head', 'src/catalog.ts', 3, 4, 'model'),
      passage('base', 'src/cart.ts', 20, 20, 'before'),
   ],
   how: 'Each add checks the quantity.',
   why: 'Carts held negative lines.',
   designChoices: 'A RangeError over a result.',
   pinnedBy: 'cart.test.ts',
   checks: [
      {
         question: 'Is a zero quantity an error?',
         recommendation: 'Yes: a zero line is noise.',
         suggestions: [
            { text: 'Refuse a zero quantity.', type: 'decision' },
            { text: 'Zero may mean a removal.', type: 'concern' },
            { text: 'What does the shop expect?', type: 'openQuestion' },
         ],
      },
      { question: 'Should remove check too?' },
   ],
};
export const totals = { headline: 'Totals round to cents.', passages: [passage('head', 'src/pay.ts', 20, 22)], why: 'Sums drifted by a cent.' };
export const rounding = { headline: 'Rounding can hide a cent.', passages: [passage('head', 'src/cart.ts', 20, 20)], scenario: 'Two discounts stack.' };
export const ask = { headline: 'Is the old form gone?', passages: [passage('head', 'src/cart.ts', 20, 20)], checks: [{ question: 'Does anything post the form?' }] };

/** A stop of a plan, with the put_stop content that Claude writes for it, or null for a stop that stays pending. */
export interface PlannedStop {
   title: string;
   kind: 'overview' | 'context' | 'inspect' | 'risk' | 'question';
   content: Record<string, unknown> | null;
}

export const fourStops: PlannedStop[] = [
   { title: 'The change at a glance', kind: 'overview', content: glance },
   { title: 'Cart totals', kind: 'inspect', content: totals },
   { title: 'Rounding', kind: 'risk', content: null },
   { title: 'The old form', kind: 'question', content: ask },
];

/** The IDs that a template of these tests hands its worlds. */
export interface SeededStops {
   tourId: string;
   stopIds: string[];
}

/** Commits the base and the head, and points the world's pull request at them. */
export async function commitFiles(world: World): Promise<void> {
   const base = await world.repo.commit('main', 'The base', baseFiles);
   const head = await world.repo.commit('feature', 'The change', headFiles, base);
   await world.pullRequest.set(head, base);
}

/** Creates a pinned tour of the pull request with the stops in one chapter titled Chapter, as Claude does, and writes each stop that has content. */
export async function createTour(world: World, claude: ClaudeSession, stops: PlannedStop[] = fourStops, url = prUrl): Promise<SeededStops> {
   const tour = (await claude.call('create_tour', { prUrl: url, familiarity: 'some' })) as Tour;
   await world.idle();
   const planned = (await claude.call('set_plan', {
      tourId: tour.id,
      chapters: [{ id: 'only', title: 'Chapter' }],
      stops: stops.map(({ title, kind }) => ({ title, kind, chapter: 'only' })),
   })) as Tour;
   const stopIds = planned.plan!.stops.map((stop) => stop.id);
   for (const [index, stop] of stops.entries()) {
      if (stop.content !== null) {
         await claude.call('put_stop', { tourId: tour.id, stopId: stopIds[index], ...stop.content });
      }
   }

   return { tourId: tour.id, stopIds };
}

/** Builds a world with the files and one tour of the stops. */
export async function buildTour(world: World, stops: PlannedStop[] = fourStops): Promise<SeededStops> {
   await commitFiles(world);
   return createTour(world, await world.claude(), stops);
}

/** Opens the tour of the label, waits for its changed files and turns to the Plan tab. */
export async function openPinnedTour(app: LotsenApp, label?: string): Promise<void> {
   await openTour(app, label);
   await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
   fireEvent.click(tab('Plan'));
   await screen.findByRole('tabpanel', { name: 'Plan' });
}

/** Returns the read_state calls for the tour that the page sent. */
export function tourLoads(transport: Transport, tourId: string): number {
   return transport.requests.filter((request) => request.operation === 'read_state' && request.input?.tourId === tourId).length;
}

/** The spy on the editor surfaces' focus that {@link scrolledTo} reads. */
export interface EditorSpies {
   focus: FocusSpy;
}

/** Waits for the editor of the file's side to scroll to the line, and returns the text the editor shows at that line. */
export async function scrolledTo(spies: EditorSpies, view: 'Diff' | 'File', path: string, side: 'base' | 'head', line: number): Promise<string> {
   return centerFocusedLine(spies.focus, view, path, side, line);
}

/** A held answer of {@link gated}: `answered` completes once the server has answered, and the page reads the answer once `release` runs. */
export interface HeldAnswer {
   answered: Promise<void>;
   release(): void;
}

/**
 * The world's transport with holds that report when the server answered, and holds before the request that leave the transport's own faults in place, so a delayed fault or a stream that
 * reconnects only when the test says can be set up. The transport's own holds release the answer only after the server answered, without saying when it did.
 */
export interface GatedTransport {
   transport: Transport;
   /** Holds the next call of the operation after the server answered it. */
   holdAnswer(operation: string): HeldAnswer;
   /** Holds the next call of the operation, or the next open of the event stream for `events`, before the transport sends it, and returns the release. */
   holdRequest(operation: string): () => void;
}

export function gated(base: Transport): GatedTransport {
   const answers: { operation: string; answered: () => void; released: Promise<void> }[] = [];
   const requests: { operation: string; released: Promise<void> }[] = [];
   const take = <T extends { operation: string }>(list: T[], name: string): T | undefined => {
      const index = list.findIndex((entry) => entry.operation === name);
      return index < 0 ? undefined : list.splice(index, 1)[0];
   };

   const fetch: typeof globalThis.fetch = async (input, init) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
      const name = path === '/api/events' ? 'events' : (/^\/api\/ops\/([a-z_]+)$/.exec(path)?.[1] ?? '');
      const before = take(requests, name);
      if (before !== undefined) {
         await before.released;
      }

      const held = take(answers, name);
      const response = await base.fetch(input, init);
      if (held === undefined) {
         return response;
      }

      const body = await response.arrayBuffer();
      held.answered();
      await held.released;
      return new Response(body, { status: response.status, headers: response.headers });
   };

   return {
      transport: { ...base, fetch },
      holdAnswer(operation) {
         let answered!: () => void;
         let release!: () => void;
         const answer = new Promise<void>((resolve) => (answered = resolve));
         const released = new Promise<void>((resolve) => (release = resolve));
         answers.push({ operation, answered, released });
         return { answered: answer, release };
      },
      holdRequest(operation) {
         let release!: () => void;
         requests.push({ operation, released: new Promise<void>((resolve) => (release = resolve)) });
         return release;
      },
   };
}
