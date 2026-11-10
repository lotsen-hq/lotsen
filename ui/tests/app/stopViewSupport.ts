// The tour, the editors' spies and the queries that the view-mode, step-through and stop-files flow tests share.
import { EditorView } from '@codemirror/view';
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, expect, vi } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { ClaudeSession } from '../backend/backend';
import { openTour, prLabel, type TourSpec } from '../harness';
import { filesPanel, planPanel, tab } from './appPanels';
import { centerFocusedLine, focusedLine, spyOnFocus, type FocusSpy } from './editorFocus';

// Forty numbered lines, of which one line differs between the sides, or none for a changedLine of 0.
const numbered = (name: string, side: string, changedLine: number) =>
   Array.from({ length: 40 }, (_, index) => (index + 1 === changedLine ? `${name} ${side} line ${index + 1}` : `${name} line ${index + 1}`)).join('\n') + '\n';

/** The cart and the pay file change one line each, at lines 20 and 21, and the catalog stays the same at both sides. */
export const stopFiles = {
   base: { 'src/cart.ts': numbered('cart', 'base', 20), 'src/pay.ts': numbered('pay', 'base', 21), 'src/catalog.ts': numbered('catalog', 'head', 0) },
   head: { 'src/cart.ts': numbered('cart', 'head', 20), 'src/pay.ts': numbered('pay', 'head', 21) },
};

export interface PassageInput {
   side: 'base' | 'head';
   path: string;
   startLine: number;
   endLine: number;
   role: 'primary' | 'related';
   label?: string;
}

/** Returns a put_stop passage, related when it has a label and primary otherwise. */
export function passage(side: 'base' | 'head', path: string, startLine: number, endLine: number, label?: string): PassageInput {
   return { side, path, startLine, endLine, role: label === undefined ? 'primary' : 'related', ...(label === undefined ? {} : { label }) };
}

/** The overview's passages, with the primary passage second, so the steps reorder it to the front. */
export const scattered = {
   headline: 'The cart checks its quantities.',
   passages: [
      passage('head', 'src/pay.ts', 30, 31, 'caller'),
      passage('head', 'src/cart.ts', 5, 7),
      passage('head', 'src/catalog.ts', 3, 4, 'model'),
      passage('base', 'src/cart.ts', 20, 20, 'before'),
   ],
};
export const totals = { headline: 'Totals round to cents.', why: 'Sums drifted by a cent.', passages: [passage('head', 'src/pay.ts', 20, 22)] };
export const rounding = { headline: 'Rounding can hide a cent.', scenario: 'Two discounts stack.', passages: [passage('head', 'src/cart.ts', 20, 20)] };
const ask = { headline: 'Is the old form gone?', checks: [{ question: 'Does anything post the form?' }], passages: [passage('head', 'src/cart.ts', 20, 20)] };

/** The four stops: the overview, an inspect of the pay file, a pending risk and a question on the cart. */
export const scatteredSpec: TourSpec = {
   ...stopFiles,
   chapters: [
      {
         title: 'Chapter',
         stops: [
            { title: 'The change at a glance', kind: 'overview', content: scattered },
            { title: 'Cart totals', kind: 'inspect', content: totals },
            { title: 'Rounding', kind: 'risk' },
            { title: 'The old form', kind: 'question', content: ask },
         ],
      },
   ],
};

/** The stop IDs of a seeded tour, which a template keeps. */
export interface SeededIds {
   tourId: string;
   stopIds: string[];
}

let focus: FocusSpy;

/** Spies on each editor surface's focus before each test of the file that calls it, which {@link scrolledTo} reads. */
export function spyOnEditorsEachTest(): void {
   beforeEach(() => {
      focus = spyOnFocus();
   });
   afterEach(() => vi.restoreAllMocks());
}

/** Waits for the editor of the file's side in the center to scroll to the line, and returns the text the editor shows at that line. */
export async function scrolledTo(view: 'Diff' | 'File', path: string, side: 'base' | 'head', line: number): Promise<string> {
   return centerFocusedLine(focus, view, path, side, line);
}

export const stackList = () => within(screen.getByRole('list', { name: 'Files in this stop' }));
export const stackItems = () => stackList().queryAllByRole('listitem');
export const stackItem = (path: string) => stackItems().find((candidate) => candidate.querySelector('header code')?.textContent === path);
export const stackHeaders = () => stackItems().map((candidate) => candidate.querySelector('header')!.textContent.replace(/\s+/g, ' ').trim());

/** Waits for the editor of a stacked file's side to scroll to the line, and returns the text the editor shows at that line. */
export async function stackScrolledTo(path: string, side: 'base' | 'head', line: number): Promise<string> {
   return focusedLine(focus, () => stackItem(path)!, side, line);
}

/** The mark of each line that an editor of the open file draws, by line number: the passage role and whether it is focused, or dimmed. */
export function lineMarks(view: 'Diff' | 'File', index = 0): Map<number, string> {
   const editor = EditorView.findFromDOM(screen.getByRole('main', { name: view }).querySelectorAll<HTMLElement>('.cm-editor')[index]!)!;
   return new Map(
      [...editor.contentDOM.querySelectorAll(':scope > .cm-line')].map((line) => {
         const role = ['primary', 'related'].find((name) => line.classList.contains(`lotsen-passage-${name}`));
         const mark = role === undefined ? (line.classList.contains('lotsen-dimmed') ? 'dimmed' : '') : line.classList.contains('lotsen-passage-focused') ? `${role} focused` : role;
         return [editor.state.doc.lineAt(editor.posAtDOM(line)).number, mark];
      }),
   );
}

export const modeSwitch = () => within(screen.getByRole('group', { name: 'View mode' }));
export const pressed = (name: string) => modeSwitch().getByRole('button', { name }).getAttribute('aria-pressed') === 'true';
export const chooseMode = (name: 'Whole PR' | 'Stop files' | 'Step through') => fireEvent.click(modeSwitch().getByRole('button', { name }));
export const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
export const stopNavigation = () => within(screen.getByRole('navigation', { name: 'Stops' }));
export const planStop = (title: string) => planPanel().getByRole('button', { name: new RegExp(title) });
/** The status that the plan shows after a stop's title, or null when it shows none, as for a ready stop. */
export const planStatus = (title: string) => planStop(title).querySelector('.status')?.textContent.trim() ?? null;
export const passageNavigation = () => within(screen.getByRole('navigation', { name: 'Passages' }));
export const nextPassage = () => fireEvent.click(passageNavigation().getByRole('button', { name: 'Next passage' }));
export const previousPassage = () => fireEvent.click(passageNavigation().getByRole('button', { name: 'Previous passage' }));

/** The passage navigation's visible text, without its status, which only a screen reader reads. */
export function passagePosition(): string {
   const shown = screen.getByRole('navigation', { name: 'Passages' }).cloneNode(true) as HTMLElement;
   shown.querySelector('[role="status"]')?.remove();
   return shown.textContent.replace(/\s+/g, ' ').trim();
}

export async function dimmedFiles(): Promise<(string | undefined)[]> {
   return (await filesPanel())
      .getAllByRole('treeitem')
      .filter((item) => item.querySelector(':scope > .row')?.classList.contains('dimmed'))
      .map((item) => item.dataset.id);
}

/** Opens the tour from the tour list, waits for its changed files, and turns to the Plan tab. */
export async function openPinnedTour(app: LotsenApp, label = prLabel): Promise<void> {
   await openTour(app, label);
   await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
   fireEvent.click(tab('Plan'));
   await screen.findByRole('tabpanel', { name: 'Plan' });
}

/** Opens the tour in the mode at the stop, and waits for the Stop panel to show it. */
export async function openStopIn(app: LotsenApp, mode: 'Stop files' | 'Step through', title = 'The change at a glance'): Promise<void> {
   await openPinnedTour(app);
   chooseMode(mode);
   fireEvent.click(planStop(title));
   await stopPanel().findByRole('heading', { name: title });
}

/** Writes a revision of the stop under view as Claude, then shows it from the banner, as the reviewer does. */
export async function reviseViewedStop(claude: ClaudeSession, tourId: string, stopId: string, content: Record<string, unknown>): Promise<void> {
   await claude.call('put_stop', { tourId, stopId, ...content });
   fireEvent.click(await stopPanel().findByRole('button', { name: 'Show the new version' }));
}

/** Sets the plan again as Claude with the stops of the IDs, in that order, each keeping its title, kind and content. */
export async function keepStops(claude: ClaudeSession, tourId: string, stops: { id: string; title: string; kind: string }[], fileMarks?: unknown[]): Promise<void> {
   await claude.call('set_plan', {
      tourId,
      chapters: [{ id: 'c1', title: 'Chapter' }],
      stops: stops.map((stop) => ({ ...stop, chapter: 'c1' })),
      ...(fileMarks === undefined ? {} : { fileMarks }),
   });
}
