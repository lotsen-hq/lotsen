// The world and the page of the browser history's flow tests: two pinned tours of one change, and a page that loads and reloads at an address as the browser does.
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, expect, vi } from 'vitest';
import { LotsenApp } from '../../src/lib/app.svelte';
import type { Tour } from '../../src/lib/types';
import type { Template, Transport, World } from '../backend/backend';
import { backend, prUrl, renderApp } from '../harness';
import { stubRangeLayout } from '../rangeLayout';
import { openTourList, tourButton, tourSwitcherName } from '../tourPicker';
import { filesPanel, planPanel, tab, worldOf } from './appPanels';
import { centerFocusedLine, spyOnFocus, type FocusSpy } from './editorFocus';

/** What the template built: the tour of `shop #42` and its stops, the tour of `shop #7`, and the commits. */
export interface HistorySeed {
   tourId: string;
   otherId: string;
   /** The stops of the tour of `shop #42`: the overview, Cart totals and the pending Rounding. */
   stopIds: string[];
   base: string;
   head: string;
}

export const otherLabel = 'shop #7';

// Forty numbered lines, of which one line differs between the sides.
const numbered = (name: string, side: string, changedLine: number) =>
   Array.from({ length: 40 }, (_, index) => (index + 1 === changedLine ? `${name} ${side} line ${index + 1}` : `${name} line ${index + 1}`)).join('\n') + '\n';

/** The files that the pull request leaves as they are, but src/money.ts, which only the base commit adds. */
export const unchangedFiles: Record<string, string> = {
   'src/cart.ts': numbered('cart', 'base', 20),
   'src/pay.ts': numbered('pay', 'base', 21),
   'src/catalog.ts': numbered('catalog', 'head', 0),
};
/** The files that the pull request changes. */
export const changedFiles: Record<string, string> = { 'src/cart.ts': numbered('cart', 'head', 20), 'src/pay.ts': numbered('pay', 'head', 21) };

const passage = (side: 'base' | 'head', path: string, startLine: number, endLine: number, label?: string) => ({
   side,
   path,
   startLine,
   endLine,
   role: label === undefined ? 'primary' : 'related',
   ...(label === undefined ? {} : { label }),
});

const glance = {
   headline: 'The cart checks its quantities.',
   how: 'Each add checks the quantity.',
   passages: [
      passage('head', 'src/cart.ts', 5, 7),
      passage('head', 'src/pay.ts', 30, 31, 'caller'),
      passage('head', 'src/catalog.ts', 3, 4, 'model'),
      passage('base', 'src/cart.ts', 20, 20, 'before'),
   ],
};
const referring = {
   headline: 'Totals round to cents.',
   why: '[the caller](ref:head/src/pay.ts#L30-31) and [the product type](ref:head/src/catalog.ts#L3-4)',
   passages: [passage('head', 'src/pay.ts', 20, 22), passage('head', 'src/cart.ts', 5, 7, 'caller')],
};

/**
 * Returns the template of both tours under the name. Each test file names its own, because two workers that build one template at once each seal it, and the later seal replaces the tours
 * that the earlier worker's seed names.
 */
export function historyTemplate(name: string): Template<HistorySeed> {
   return backend().template(name, buildTours);
}

async function buildTours(world: World): Promise<HistorySeed> {
   const base = await world.repo.commit('main', 'The base', { ...unchangedFiles, 'src/money.ts': 'money\n' });
   const head = await world.repo.commit('feature', 'The change', changedFiles, base);
   await world.pullRequest.set(head, base);
   const claude = await world.claude();
   const tours: { id: string; stopIds: string[] }[] = [];
   for (const url of ['https://platform.test/shop/pull/7', prUrl]) {
      const tour = (await claude.call('create_tour', { prUrl: url, familiarity: 'some' })) as Tour;
      await world.idle();
      const planned = (await claude.call('set_plan', {
         tourId: tour.id,
         chapters: [{ id: 'only', title: 'Chapter' }],
         stops: [
            { title: 'The change at a glance', kind: 'overview', chapter: 'only' },
            { title: 'Cart totals', kind: 'inspect', chapter: 'only' },
            { title: 'Rounding', kind: 'risk', chapter: 'only' },
         ],
      })) as Tour;
      const stopIds = planned.plan!.stops.map((stop) => stop.id);
      await claude.call('put_stop', { tourId: tour.id, stopId: stopIds[0], ...glance });
      await claude.call('put_stop', { tourId: tour.id, stopId: stopIds[1], ...referring });
      tours.push({ id: tour.id, stopIds });
   }

   return { tourId: tours[1]!.id, otherId: tours[0]!.id, stopIds: tours[1]!.stopIds, base, head };
}

/** Returns a new world copied from the template, and the fragments of the entries that the tests visit on the tour of `shop #42`. */
export async function historyWorld(template: Template<HistorySeed>) {
   const { world, seed } = await worldOf(template);
   // A copied world's platform reports no pull request until the test sets one, and an iteration check waits for it.
   await world.pullRequest.set(seed.head, seed.base);
   const [overview, totals, rounding] = seed.stopIds as [string, string, string];
   const entry = (fields: Record<string, string>) => `#${new URLSearchParams({ tour: seed.tourId, ...fields }).toString()}`;
   return {
      world,
      seed,
      overview,
      totals,
      rounding,
      entry,
      tourEntry: entry({}),
      stopEntry: entry({ stop: totals, side: 'head', file: 'src/pay.ts', lines: '20-22' }),
      referenceEntry: entry({ stop: totals, side: 'head', file: 'src/catalog.ts', lines: '3-4' }),
      fileEntry: entry({ stop: totals, side: 'head', file: 'src/cart.ts' }),
   };
}

let focus: FocusSpy;

/** Watches the editor surfaces' focus in each test of the file that calls it, for {@link scrolledTo}. */
export function watchScrollsEachTest(): void {
   beforeEach(() => {
      focus = spyOnFocus();
      return () => vi.restoreAllMocks();
   });
}

/** Waits for the editor of the file's side to scroll to the line, and returns the text the editor shows at that line. */
export async function scrolledTo(view: 'Diff' | 'File', path: string, side: 'base' | 'head', line: number): Promise<string> {
   return centerFocusedLine(focus, view, path, side, line);
}

let page: LotsenApp | undefined;
let restoreRanges: (() => void) | undefined;

afterEach(() => {
   page?.stop();
   page = undefined;
   cleanup();
   restoreRanges?.();
   restoreRanges = undefined;
});

/**
 * Loads the page at the address and history state on the transport, as the browser does, without waiting for it. The UI credential is stored as an earlier load of the tab left it, unless
 * `signedIn` is false.
 */
export function load(world: World, transport: Transport, path = '/', state: unknown = null, options: { signedIn?: boolean } = {}): LotsenApp {
   history.replaceState(state, '', path);
   restoreRanges ??= stubRangeLayout();
   if (options.signedIn !== false) {
      sessionStorage.setItem('lotsen.uiCredential', world.uiCredential);
   }

   const loaded = new LotsenApp({ environment: { fetch: transport.fetch, location, history, storage: sessionStorage }, retryDelaysMs: [5] });
   renderApp(loaded);
   page = loaded;
   return loaded;
}

/** Loads the page at its current address and history entry again, as the browser's reload does. */
export function reload(world: World, transport: Transport): LotsenApp {
   page?.stop();
   cleanup();
   return load(world, transport, location.pathname + location.search + location.hash, history.state);
}

/** Loads the page at the root, and waits until it is live with the tour list loaded. */
export async function loadShell(world: World, transport: Transport): Promise<LotsenApp> {
   const app = load(world, transport);
   await screen.findByRole('button', { name: tourSwitcherName });
   await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
   await waitFor(() => expect(app.toursLoaded).toBe(true));
   return app;
}

/** Loads the page at the root, opens the tour of the label from the tour list once its plan and its changed files load, and turns to the Plan tab. */
export async function openPinnedTour(world: World, transport: Transport, label: RegExp = /#42/): Promise<LotsenApp> {
   const app = await loadShell(world, transport);
   await chooseTour(label);
   // The page loads the plan and the changed files with separate requests, so either one may arrive last.
   await waitFor(() => expect(app.selectedPlan).toBeDefined());
   await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
   fireEvent.click(tab('Plan'));
   await screen.findByRole('tabpanel', { name: 'Plan' });
   return app;
}

/** Clicks the tour of the label in the tour list, once the list shows it. */
export async function chooseTour(label: RegExp): Promise<void> {
   const list = await openTourList();
   const button = await waitFor(() => {
      const found = list.getAllByRole('button', tourButton).find((candidate) => label.test(candidate.textContent ?? ''));
      expect(found).toBeDefined();
      return found!;
   });
   button.click();
}

/** Moves through the history, and waits for the page to restore the entry. */
export async function traverse(step: () => void): Promise<void> {
   const popped = new Promise<void>((resolve) => window.addEventListener('popstate', () => resolve(), { once: true }));
   step();
   await popped;
}

/** What the page shows: the tour, the stop, the open file and the line it scrolls to. */
export function shown(app: LotsenApp) {
   return {
      tour: app.selectedTourId,
      stop: app.selectedStopId,
      file: app.selectedFilePath ?? (app.selectedText === null ? null : `${app.selectedText.side}:${app.selectedText.path}`),
      line: app.focus?.line ?? null,
   };
}

export const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
export const stopNavigation = () => within(screen.getByRole('navigation', { name: 'Stops' }));
export const modeSwitch = () => within(screen.getByRole('group', { name: 'View mode' }));
export const planStop = (title: string) => planPanel().getByRole('button', { name: new RegExp(title) });

/** Opens the file of the accessible name, such as `cart.ts, Modified`, from the Files tab. */
export async function openFromFiles(name: string): Promise<void> {
   fireEvent.click(tab('Files'));
   const item = await (await filesPanel()).findByRole('treeitem', { name });
   fireEvent.click(within(item).getByText(name.split(',')[0]!));
}
