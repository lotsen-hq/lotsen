// The tour that the flow tests of a stop's prose, its ref: links and the placement of its explanation share: two changed files of forty numbered lines and an unchanged one.
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, expect, vi, type MockInstance } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { FileMark, StopKind, Tour } from '../../src/lib/types';
import type { ClaudeSession, World } from '../backend/backend';
import { openTour, prUrl } from '../harness';
import { planPanel, tab } from './appPanels';
import { CodeMirrorSurface } from '../../src/lib/editor/codeMirrorSurface';
import { centerFocusedLine, spyOnFocus, type FocusSpy } from './editorFocus';

/** Returns forty numbered lines, of which one line differs between the sides. */
export function numbered(name: string, side: string, changedLine: number): string {
   return Array.from({ length: 40 }, (_, index) => (index + 1 === changedLine ? `${name} ${side} line ${index + 1}` : `${name} line ${index + 1}`)).join('\n') + '\n';
}

/** Returns a `put_stop` passage: primary without a label, related with one. */
export function passage(side: 'base' | 'head', path: string, startLine: number, endLine: number, label?: string): Record<string, unknown> {
   return { side, path, startLine, endLine, role: label === undefined ? 'primary' : 'related', ...(label === undefined ? {} : { label }) };
}

/** One stop of the plan, with the `put_stop` input without the tour and the stop, or none for a pending stop. */
export interface PlannedStop {
   title: string;
   kind: StopKind;
   content?: Record<string, unknown>;
}

/** What {@link seedStops} built: the tour, its stops in plan order and each stop's check IDs. */
export interface StopsSeed {
   tourId: string;
   stopIds: string[];
   checkIds: string[][];
}

/** The overview of the plans in these tests, on the cart with related passages in the payment, the catalog and the base. */
export const glance: PlannedStop = {
   title: 'The change at a glance',
   kind: 'overview',
   content: {
      headline: 'The cart checks its quantities.',
      how: 'Each add checks the quantity.',
      why: 'Carts held negative lines.',
      designChoices: 'A RangeError over a result.',
      pinnedBy: 'cart.test.ts',
      passages: [
         passage('head', 'src/cart.ts', 5, 7),
         passage('head', 'src/pay.ts', 30, 31, 'caller'),
         passage('head', 'src/catalog.ts', 3, 4, 'model'),
         passage('base', 'src/cart.ts', 20, 20, 'before'),
      ],
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
   },
};

export const totals: PlannedStop = {
   title: 'Cart totals',
   kind: 'inspect',
   content: { headline: 'Totals round to cents.', why: 'Sums drifted by a cent.', passages: [passage('head', 'src/pay.ts', 20, 22)] },
};

export const rounding: PlannedStop = {
   title: 'Rounding',
   kind: 'risk',
   content: { headline: 'Rounding can hide a cent.', scenario: 'Two discounts stack.', passages: [passage('head', 'src/cart.ts', 20, 20)] },
};

/** Creates the tour through the real operations: the cart and the payment change line 20 and line 21, the catalog stays, and Claude plans and writes the stops. */
export async function seedStops(world: World, stops: PlannedStop[], options: { fileMarks?: FileMark[]; payHead?: string; draftAnswer?: string } = {}): Promise<StopsSeed> {
   const catalog = numbered('catalog', 'head', 0);
   const base = await world.repo.commit('main', 'The base', { 'src/cart.ts': numbered('cart', 'base', 20), 'src/pay.ts': numbered('pay', 'base', 21), 'src/catalog.ts': catalog });
   const head = await world.repo.commit('feature', 'The change', { 'src/cart.ts': numbered('cart', 'head', 20), 'src/pay.ts': options.payHead ?? numbered('pay', 'head', 21) }, base);
   await world.pullRequest.set(head, base);
   const claude = await world.claude();
   const tourId = ((await claude.call('create_tour', { prUrl, familiarity: 'some' })) as Tour).id;
   await world.idle();
   const planned = (await claude.call('set_plan', {
      tourId,
      chapters: [{ id: 'only', title: 'Chapter' }],
      stops: stops.map((stop) => ({ title: stop.title, kind: stop.kind, chapter: 'only' })),
      ...(options.fileMarks === undefined ? {} : { fileMarks: options.fileMarks }),
   })) as Tour;
   const stopIds = planned.plan!.stops.map((stop) => stop.id);
   for (const [index, stop] of stops.entries()) {
      if (stop.content !== undefined) {
         await claude.call('put_stop', { tourId, stopId: stopIds[index], ...stop.content });
      }
   }

   const tour = await world.reviewer.call('read_state', { tourId });
   const checkIds = tour.plan!.stops.map((stop) => (stop.stage === 'written' ? (stop.content.checks ?? []).map((check) => check.id) : []));
   if (options.draftAnswer !== undefined) {
      const at = checkIds.findIndex((ids) => ids.length > 0);
      await world.reviewer.call('answer_check', { tourId, stopId: stopIds[at]!, checkId: checkIds[at]![0]!, text: options.draftAnswer });
   }

   await claude.drop();
   return { tourId, stopIds, checkIds };
}

/** Opens the tour from the tour list, waits for its changed files and turns to the Plan tab. */
export async function openPinnedTour(app: LotsenApp): Promise<void> {
   await openTour(app);
   await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
   fireEvent.click(tab('Plan'));
   await screen.findByRole('tabpanel', { name: 'Plan' });
}

/** Has Claude write a revision of the stop under view, then shows it from the banner, as the reviewer does. */
export async function reviseViewedStop(claude: ClaudeSession, tourId: string, stopId: string, content: Record<string, unknown>): Promise<void> {
   await claude.call('put_stop', { tourId, stopId, ...content });
   fireEvent.click(await stopPanel().findByRole('button', { name: 'Show the new version' }));
}

export const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
export const stopNavigation = () => within(screen.getByRole('navigation', { name: 'Stops' }));
export const planStop = (title: string) => planPanel().getByRole('button', { name: new RegExp(title) });

/** Spies on the editor surfaces' focus and decorations, for {@link scrolledTo}, during each test of the file that calls it. */
export function spyOnScrolls(): { focus: () => FocusSpy; showPassages: () => MockInstance<CodeMirrorSurface['showPassages']> } {
   let focus: FocusSpy;
   let showPassages: MockInstance<CodeMirrorSurface['showPassages']>;
   beforeEach(() => {
      focus = spyOnFocus();
      showPassages = vi.spyOn(CodeMirrorSurface.prototype, 'showPassages');
   });
   afterEach(() => vi.restoreAllMocks());
   return { focus: () => focus, showPassages: () => showPassages };
}

/** Waits for the editor of the file's side to scroll to the line, and returns the text the editor shows at that line. */
export async function scrolledTo(spies: ReturnType<typeof spyOnScrolls>, view: 'Diff' | 'File', path: string, side: 'base' | 'head', line: number): Promise<string> {
   return centerFocusedLine(spies.focus(), view, path, side, line);
}
