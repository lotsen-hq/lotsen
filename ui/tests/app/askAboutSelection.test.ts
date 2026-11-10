import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { DiffLayout } from '../../src/lib/diffLayout';
import type { AnchorInput, Tour } from '../../src/lib/types';
import type { ViewMode } from '../../src/lib/viewMode';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, openTour, prLabel, readTour, seedTour, startApp, withHolds, type HeldTransport } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { aboutChoices, askedAbout, quote } from './askBox';
import { openThreadView, threadViewElement } from './threadView';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   history.replaceState(null, '', '/');
   // jsdom has no layout, so it lacks the scroll that the Threads panel asks of an opened thread.
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

afterEach(() => {
   document.getSelection()?.removeAllRanges();
});

// A negative check needs the page to have handled what the server sent, which no condition on the page shows.
const settled = () => new Promise((resolve) => setTimeout(resolve, 50));

interface Seed {
   tourId: string;
   otherId: string;
   stopId: string;
   /** The ID that the server gave the stop's one check. */
   checkId: string;
}

const otherLabel = 'shop #43';

/** The put_stop input of the stop's content, without the tour and the stop, with the check's ID once the server gave it one. */
function cartAdds(how: string, checkId?: string): Record<string, unknown> {
   return {
      headline: 'The cart adds a line.',
      how,
      checks: [{ question: 'Is zero an error?', recommendation: 'Yes, refuse it.', suggestions: [{ text: 'Refuse zero.', type: 'decision' }], ...(checkId === undefined ? {} : { id: checkId }) }],
      passages: [{ side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 2, role: 'primary' }],
   };
}

// The head's line 2 replaces the base's lines 2 and 3. A second tour on the same pull request has a pending stop, and neither tour has a session.
const cart = backend().template('askAboutSelection-cart', async (world): Promise<Seed> => {
   const { tourId, stopIds } = await seedTour(world, {
      bound: false,
      base: { 'src/cart.ts': 'one\ntwo\nthree\nfour\n' },
      head: { 'src/cart.ts': 'one\nnew\nfour\n' },
      chapters: [{ title: 'Cart', stops: [{ title: 'Cart adds', kind: 'inspect', content: cartAdds('Each add checks the quantity.') }] }],
   });
   const stop = (await readTour(world, tourId)).plan!.stops[0]!;
   const second = await world.claude();
   const other = (await second.call('create_tour', { prUrl: 'https://platform.test/shop/pull/43', familiarity: 'some' })) as Tour;
   await world.idle();
   await second.call('set_plan', { tourId: other.id, chapters: [{ id: 'c1', title: 'Cart' }], stops: [{ title: 'Cart adds', kind: 'inspect', chapter: 'c1' }] });
   await second.drop();
   return { tourId, otherId: other.id, stopId: stopIds[0]!, checkId: stop.stage === 'written' ? stop.content.checks![0]!.id! : '' };
});

/** A question that the reviewer asked before the page opened, at an anchor that may name the seeded stop and check. */
type ThreadSpec = (seed: Seed) => { anchor: AnchorInput; text: string };

function thread(anchor: AnchorInput | ((seed: Seed) => AnchorInput), text: string): ThreadSpec {
   return (seed) => ({ anchor: typeof anchor === 'function' ? anchor(seed) : anchor, text });
}

const baseThread = thread({ kind: 'code', side: 'base', path: 'src/cart.ts', startLine: 3, endLine: 3 }, 'Where did three go?');
const headThread = thread({ kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 3 }, 'Why new?');
const fileThread = thread({ kind: 'file', side: 'head', path: 'src/cart.ts' }, 'What does the cart do?');
const stopTextThread = thread(({ stopId }) => ({ kind: 'stopText', stopId, field: 'how', text: 'checks the quantity', version: 1 }), 'Which quantity?');
const stopThread = thread(({ stopId }) => ({ kind: 'stop', stopId }), 'Is this stop done?');

let world: World;
let seed: Seed;
let app: LotsenApp;
let transport: HeldTransport;
let claude: ClaudeSession;

async function ask(tourId: string, spec: ThreadSpec): Promise<void> {
   await world.reviewer.call('ask_question', { tourId, ...spec(seed) });
}

/** Starts the app with the settings on a copy of the tours, with a bound session that the threads' questions went to, and opens the first tour unless `open` is false. */
async function start(settings: { layout?: DiffLayout; viewMode?: ViewMode; placement?: 'side' | 'inline'; threads?: ThreadSpec[]; open?: boolean } = {}) {
   world = await backend().world({ template: cart });
   seed = world.seed as Seed;
   claude = await attachClaude(world, seed.tourId);
   for (const spec of settings.threads ?? []) {
      await ask(seed.tourId, spec);
   }

   localStorage.setItem('lotsen.diffLayout', settings.layout ?? 'sideBySide');
   localStorage.setItem('lotsen.defaultViewMode', settings.viewMode ?? 'wholePr');
   localStorage.setItem('lotsen.placement', settings.placement ?? 'side');
   transport = withHolds(world.transport());
   ({ app } = await startApp(world, { transport }));
   if (settings.open !== false) {
      await selectTour(prLabel, seed.tourId);
   }
}

async function selectTour(label: string, tourId: string) {
   await openTour(app, label);
   await waitFor(() => expect(app.selectedTourId).toBe(tourId));
   await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
}

async function openStop() {
   app.openStop(seed.stopId);
   await editorLines();
}

const threadsPanel = () => within(screen.getByRole('region', { name: 'Threads' }));
const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
const askBox = () => threadsPanel().getByRole('textbox', { name: 'Question for Claude' });

/** Waits for the code to show, and returns every line its editors draw, their own and the deleted lines of the inline view. */
async function editorLines(): Promise<Element[]> {
   return waitFor(() => {
      const lines = [...document.querySelectorAll('.cm-content > .cm-line, .cm-deletedLine')];
      expect(lines.length).toBeGreaterThan(0);
      return lines;
   });
}

/** The code of a line, without the labels and the thread markers that the editor draws in it. */
function code(line: Element): string {
   const clone = line.cloneNode(true) as Element;
   clone.querySelectorAll('.lotsen-passageLabels, .lotsen-threadMarkers').forEach((element) => element.remove());
   return clone.textContent;
}

/** Returns the line that shows a text, in the base's pane side by side, or among the deleted lines inline, when `base` asks for it. */
async function codeLine(text: string, base = false): Promise<Element> {
   return waitFor(() => {
      const found = [...document.querySelectorAll('.cm-content > .cm-line, .cm-deletedLine')].filter((line) => code(line) === text);
      const pick = base ? (found.find((line) => line.closest('.cm-merge-a') !== null || line.classList.contains('cm-deletedLine')) ?? found[0]) : found.at(-1);
      expect(pick).toBeDefined();
      return pick!;
   });
}

function select(from: Node, to: Node, toOffset = to.childNodes.length, fromOffset = 0): void {
   const range = document.createRange();
   range.setStart(from, fromOffset);
   range.setEnd(to, toOffset);
   const selection = document.getSelection()!;
   selection.removeAllRanges();
   selection.addRange(range);
   document.dispatchEvent(new Event('selectionchange'));
}

/** Selects a run of words inside an element's text. */
function selectWords(element: Element, words: string): void {
   const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
   for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const index = node.textContent!.indexOf(words);
      if (index >= 0) {
         select(node, node, index + words.length, index);
         return;
      }
   }

   throw new Error(`No text "${words}".`);
}

/** Sends a question from the ask box, with what is picked there, and returns the input that the page sent. */
async function send(text: string): Promise<Record<string, unknown>> {
   await fireEvent.input(askBox(), { target: { value: text } });
   await fireEvent.click(threadsPanel().getByRole('button', { name: 'Send' }));
   return waitFor(() => {
      const request = transport.requests.findLast((candidate) => candidate.operation === 'ask_question');
      expect(request?.input).toBeDefined();
      return request!.input!;
   });
}

/** Waits for the ask box to ask about the pick, with its quote of where the pick is and the first line of its text, or no line for a file, and with the focus in the box. */
async function pickedInBox(where: string, line: string | null) {
   await waitFor(() => expect(askedAbout()).toBe(line === null ? 'The file' : 'The selected text'));
   const lines = [...quote()!.querySelectorAll('p')].map((element) => element.textContent);
   expect(lines).toEqual(line === null ? [where] : [where, line]);
   await waitFor(() => expect(document.activeElement).toBe(askBox()));
}

describe('asking about selected code', () => {
   it.each<[DiffLayout, ViewMode]>([
      ['sideBySide', 'wholePr'],
      ['sideBySide', 'stepThrough'],
      ['sideBySide', 'stopFiles'],
      ['inline', 'wholePr'],
      ['inline', 'stepThrough'],
      ['inline', 'stopFiles'],
   ])('asks with the base lines of a deleted line, %s in %s', async (layout, viewMode) => {
      await start({ layout, viewMode });
      await openStop();

      const three = await codeLine('three', true);
      select(three, three);
      await fireEvent.click(await screen.findByRole('button', { name: 'Ask Claude about src/cart.ts, line 3 at the base' }));
      await pickedInBox('src/cart.ts, line 3 at the base', 'three');

      const body = await send('Where did three go?');

      expect(body).toEqual({ tourId: seed.tourId, text: 'Where did three go?', anchor: { kind: 'code', side: 'base', path: 'src/cart.ts', startLine: 3, endLine: 3 }, stopId: seed.stopId });
      await waitFor(() => expect(quote()).toBeNull());
      expect(askedAbout()).toBe('Stop 1');
   });

   it.each<DiffLayout>(['sideBySide', 'inline'])('asks with the head lines selected, %s', async (layout) => {
      await start({ layout });
      await openStop();

      select(await codeLine('new'), await codeLine('four'));
      await fireEvent.click(await screen.findByRole('button', { name: 'Ask Claude about src/cart.ts, lines 2–3' }));
      await pickedInBox('src/cart.ts, lines 2–3', 'new');

      expect((await send('Why new?')).anchor).toEqual({ kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 3 });
   });

   it("lists the selected text in the chip's menu only while it waits, and the quote's remove button returns the box to the open stop", async () => {
      await start();
      await openStop();
      expect(await aboutChoices()).toEqual([
         { name: 'The tour', checked: false },
         { name: 'Stop 1 · Cart adds', checked: true },
      ]);
      const one = await codeLine('one');
      select(one, one);
      await fireEvent.click(await screen.findByRole('button', { name: 'Ask Claude about src/cart.ts, line 1' }));
      await pickedInBox('src/cart.ts, line 1', 'one');
      expect(await aboutChoices()).toEqual([
         { name: 'The tour', checked: false },
         { name: 'Stop 1 · Cart adds', checked: false },
         { name: 'The selected text · src/cart.ts, line 1', checked: true },
      ]);

      await fireEvent.click(within(quote()!).getByRole('button', { name: 'Remove the selection and ask about stop 1' }));

      await waitFor(() => expect(quote()).toBeNull());
      expect(document.activeElement).toBe(askBox());
      expect(app.pickedAnchor).toBeNull();
      expect(askedAbout()).toBe('Stop 1');
      expect(await aboutChoices()).toEqual([
         { name: 'The tour', checked: false },
         { name: 'Stop 1 · Cart adds', checked: true },
      ]);
   });

   it('keeps a pick while the tour list and the tour load again after the stream reconnects', async () => {
      await start();
      await openStop();
      const one = await codeLine('one');
      select(one, one);
      await fireEvent.click(await screen.findByRole('button', { name: 'Ask Claude about src/cart.ts, line 1' }));
      await pickedInBox('src/cart.ts, line 1', 'one');

      transport.cutEventStreams();
      await waitFor(() => expect(transport.requests.filter((request) => request.path === '/api/events').length).toBeGreaterThanOrEqual(2));
      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      await settled();

      expect(askedAbout()).toBe('The selected text');
      expect(quote()?.textContent).toContain('src/cart.ts, line 1');
   });

   it('drops a pick when another tour opens, so it is never asked in that tour', async () => {
      await start();
      await openStop();
      const one = await codeLine('one');
      select(one, one);
      await fireEvent.click(await screen.findByRole('button', { name: 'Ask Claude about src/cart.ts, line 1' }));

      await selectTour(otherLabel, seed.otherId);

      await waitFor(() => expect(quote()).toBeNull());
      expect(askedAbout()).toBe('The tour');
      expect(app.pickedAnchor).toBeNull();
   });
});

describe('asking about a file', () => {
   it('asks about the open file from its heading in the diff toolbar', async () => {
      await start();
      await openStop();

      await fireEvent.click(within(screen.getByRole('main', { name: 'Diff' })).getByRole('button', { name: 'Ask Claude about src/cart.ts' }));
      await pickedInBox('src/cart.ts', null);

      expect((await send('What is the cart?')).anchor).toEqual({ kind: 'file', side: 'head', path: 'src/cart.ts' });
   });

   it("returns the box to the tour when the quote's remove button removes a pick while no stop is open", async () => {
      await start();
      app.selectFile('src/cart.ts');
      await fireEvent.click(await within(screen.getByRole('main', { name: 'Diff' })).findByRole('button', { name: 'Ask Claude about src/cart.ts' }));
      await pickedInBox('src/cart.ts', null);

      await fireEvent.click(within(quote()!).getByRole('button', { name: 'Remove the file and ask about the tour' }));

      await waitFor(() => expect(quote()).toBeNull());
      expect(askedAbout()).toBe('The tour');
      expect(await aboutChoices()).toEqual([{ name: 'The tour', checked: true }]);
   });

   it('asks about a stacked file from its header in the Stop files stack', async () => {
      await start({ viewMode: 'stopFiles' });
      await openStop();

      const stack = within(screen.getByRole('list', { name: 'Files in this stop' }));
      await fireEvent.click(stack.getByRole('button', { name: 'Ask Claude about src/cart.ts' }));
      await pickedInBox('src/cart.ts', null);

      expect((await send('What is the cart?')).anchor).toEqual({ kind: 'file', side: 'head', path: 'src/cart.ts' });
   });
});

describe('asking about text in a stop', () => {
   it('asks with the field and the text selected in the Stop panel', async () => {
      await start();
      await openStop();

      selectWords(stopPanel().getByText('Each add checks the quantity.'), 'checks the quantity');
      await fireEvent.click(await screen.findByRole('button', { name: 'Ask Claude about “checks the quantity”' }));
      await pickedInBox('Stop 1 · Cart adds', 'checks the quantity');

      expect((await send('Which quantity?')).anchor).toEqual({ kind: 'stopText', stopId: seed.stopId, field: 'how', text: 'checks the quantity', version: 1 });
   });

   it("names a check's question by the check's ID, in the stop's version", async () => {
      await start();
      await openStop();

      selectWords(stopPanel().getByText('Is zero an error?'), 'zero');
      await fireEvent.click(await screen.findByRole('button', { name: 'Ask Claude about “zero”' }));

      expect((await send('Why zero?')).anchor).toEqual({ kind: 'stopText', stopId: seed.stopId, field: `checks[${seed.checkId}].question`, text: 'zero', version: 1 });
   });

   it("names a check's recommendation by the check's ID", async () => {
      await start();
      await openStop();

      selectWords(stopPanel().getByText('Yes, refuse it.'), 'refuse');
      await fireEvent.click(await screen.findByRole('button', { name: 'Ask Claude about “refuse”' }));
      expect((await send('Why refuse?')).anchor).toEqual({ kind: 'stopText', stopId: seed.stopId, field: `checks[${seed.checkId}].recommendation`, text: 'refuse', version: 1 });
   });

   it('offers no ask for a suggested answer, which is no prose field', async () => {
      await start();
      await openStop();

      selectWords(stopPanel().getByText('Refuse zero.'), 'Refuse');
      await settled();

      expect(screen.queryByRole('button', { name: /^Ask Claude about “/ })).toBeNull();
   });

   it('offers no ask for text across two sections, and says why', async () => {
      await start();
      await openStop();

      const headline = stopPanel().getByText('The cart adds a line.');
      const how = stopPanel().getByText('Each add checks the quantity.');
      select(headline.firstChild!, how.firstChild!, 4);

      expect(await screen.findByText('Select text of one section to ask about it.')).toBeTruthy();
      expect(screen.queryByRole('button', { name: /^Ask Claude about “/ })).toBeNull();
   });

   it('asks with the text selected in the inline explanation', async () => {
      await start({ layout: 'inline', placement: 'inline' });
      await openStop();

      const block = await waitFor(() => {
         const found = document.querySelector('.editor section[aria-label="Explanation of Cart adds"]');
         expect(found).not.toBeNull();
         return found!;
      });
      selectWords(within(block as HTMLElement).getByText('Each add checks the quantity.'), 'Each add');
      await fireEvent.click(await within(block as HTMLElement).findByRole('button', { name: 'Ask Claude about “Each add”' }));
      await pickedInBox('Stop 1 · Cart adds', 'Each add');

      expect((await send('Each?')).anchor).toEqual({ kind: 'stopText', stopId: seed.stopId, field: 'how', text: 'Each add', version: 1 });
   });

   it('keeps a revised stop as it was in the inline explanation, with a banner and no ask, until the reviewer shows the new version there', async () => {
      await start({ layout: 'inline', placement: 'inline' });
      await openStop();
      const block = () => {
         const found = document.querySelector<HTMLElement>('.editor section[aria-label="Explanation of Cart adds"]');
         expect(found).not.toBeNull();
         return within(found!);
      };
      await waitFor(() => block());

      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopId, ...cartAdds('Each add checks the quantity first.', seed.checkId) });

      const banner = await waitFor(() => within(block().getByRole('status', { name: 'Stop version' })));
      expect(screen.getAllByRole('status', { name: 'Stop version' })).toHaveLength(1);
      selectWords(block().getByText('Each add checks the quantity.'), 'Each add');
      await settled();
      expect(screen.queryByRole('button', { name: /^Ask Claude about “/ })).toBeNull();

      await fireEvent.click(banner.getByRole('button', { name: 'Show the new version' }));

      await waitFor(() => expect(block().getByText('Each add checks the quantity first.')).toBeDefined());
      expect(block().queryByRole('status', { name: 'Stop version' })).toBeNull();
      selectWords(block().getByText('Each add checks the quantity first.'), 'Each add');
      expect(await block().findByRole('button', { name: 'Ask Claude about “Each add”' })).toBeDefined();
   });
});

describe('thread markers', () => {
   /** Waits for the Threads panel to show and focus the thread view of the thread with the question. */
   async function opened(question: string) {
      await waitFor(() => {
         const view = threadViewElement();
         expect(view).not.toBeNull();
         expect(document.activeElement).toBe(view);
         expect(view!.textContent).toContain(question);
      });
   }

   const marker = (scope: Element | Document, question: string) =>
      [...scope.querySelectorAll<HTMLButtonElement>('button[data-thread-id]')].find((button) => button.getAttribute('aria-label') === `Open the thread: ${question}`);

   it.each<DiffLayout>(['sideBySide', 'inline'])('marks code threads at their lines and opens the thread view of each in the Threads panel, %s', async (layout) => {
      await start({ layout, threads: [baseThread, headThread] });
      await openStop();

      const three = await codeLine('three', true);
      await waitFor(() => expect(marker(three, 'Where did three go?')).toBeDefined());
      expect(marker(await codeLine('new'), 'Why new?')).toBeDefined();

      await fireEvent.click(marker(three, 'Where did three go?')!);

      await opened('Where did three go?');
   });

   it('marks a file thread at the file heading, and the stop text and stop threads at the stop', async () => {
      await start({ threads: [fileThread, stopTextThread, stopThread] });
      await openStop();

      const diff = screen.getByRole('main', { name: 'Diff' });
      await waitFor(() => expect(marker(diff, 'What does the cart do?')).toBeDefined());
      const stop = screen.getByRole('region', { name: 'Stop' });
      expect(marker(stop, 'Which quantity?')).toBeDefined();
      expect(marker(stop, 'Is this stop done?')).toBeDefined();

      await fireEvent.click(marker(diff, 'What does the cart do?')!);
      await opened('What does the cart do?');
      await fireEvent.click(marker(stop, 'Which quantity?')!);
      await opened('Which quantity?');
   });

   it("marks the threads on a check's question and recommendation at that text, and on a closed Checks section at its heading", async () => {
      const onQuestion = thread(({ stopId, checkId }) => ({ kind: 'stopText', stopId, field: `checks[${checkId}].question`, text: 'zero', version: 1 }), 'Why zero?');
      const onRecommendation = thread(({ stopId, checkId }) => ({ kind: 'stopText', stopId, field: `checks[${checkId}].recommendation`, text: 'refuse', version: 1 }), 'Why refuse?');
      await start({ threads: [onQuestion, onRecommendation] });
      await openStop();

      const question = stopPanel().getByText('Is zero an error?').closest('[data-stop-field]')!.parentElement!;
      const recommendation = stopPanel().getByText('Yes, refuse it.').closest('[data-stop-field]')!.parentElement!;
      expect(marker(question, 'Why zero?')).toBeDefined();
      expect(marker(recommendation, 'Why refuse?')).toBeDefined();
      expect(marker(recommendation, 'Why zero?')).toBeUndefined();

      const heading = stopPanel().getByRole('button', { name: 'Checks' });
      await fireEvent.click(heading);
      await waitFor(() => expect(marker(heading.parentElement!, 'Why refuse?')).toBeDefined());
      expect(marker(heading.parentElement!, 'Why zero?')).toBeDefined();
      await fireEvent.click(marker(heading.parentElement!, 'Why refuse?')!);
      await opened('Why refuse?');
   });

   it('marks a file thread at the header of the stacked file', async () => {
      await start({ viewMode: 'stopFiles', threads: [fileThread] });
      await openStop();

      const stack = screen.getByRole('list', { name: 'Files in this stop' });
      await waitFor(() => expect(marker(stack, 'What does the cart do?')).toBeDefined());
   });

   it('marks a thread that an event brings while the tour loads, and no thread of another tour', async () => {
      await start({ open: false });
      await attachClaude(world, seed.otherId);
      const load = transport.holdAnswered('read_state');
      const list = await openTourList();
      list
         .getAllByRole('button', tourButton)
         .find((candidate) => candidate.textContent?.includes(prLabel))!
         .click();
      await load.answered;
      await ask(seed.tourId, headThread);
      await ask(seed.otherId, baseThread);
      await load.release();
      await waitFor(() => expect(app.selectedPlan).toBeDefined());
      await openStop();

      await waitFor(() => expect(marker(document, 'Why new?')).toBeDefined());
      await settled();
      expect(marker(document, 'Where did three go?')).toBeUndefined();
   });

   it('marks a thread that an event brings while the file’s diff loads, once the diff shows', async () => {
      await start();
      const diff = transport.holdAnswered('diff_file');
      app.openStop(seed.stopId);
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loading'));
      await diff.answered;

      await ask(seed.tourId, baseThread);
      await diff.release();

      await waitFor(() => expect(marker(document, 'Where did three go?')).toBeDefined());
      expect(code(marker(document, 'Where did three go?')!.closest('.cm-line, .cm-deletedLine')!)).toBe('three');
   });
});

describe('a thread’s link to where it was asked, in its thread view', () => {
   const location = (label: string) => threadsPanel().getByRole('button', { name: label });
   /** Opens the thread view of the tour's thread whose anchor is of the kind. */
   const openThreadOf = (kind: string) => openThreadView(app.selectedThreads.find((each) => each.anchor.kind === kind)!.id);

   it('opens the file at the lines of a code thread, as a ref: link does, with a history entry', async () => {
      await start({ threads: [baseThread] });
      await openThreadOf('code');
      const entries = history.length;

      await fireEvent.click(location('src/cart.ts, line 3 at the base'));

      await waitFor(() => expect(app.focus).toEqual({ path: 'src/cart.ts', side: 'base', line: 3 }));
      expect(app.selectedFilePath).toBe('src/cart.ts');
      expect(history.length).toBe(entries + 1);
   });

   it('highlights the lines of a code thread while the pointer is on its link', async () => {
      await start({ threads: [headThread] });
      await openThreadOf('code');

      await fireEvent.mouseEnter(location('src/cart.ts, lines 2–3'));
      expect(app.hoveredReference).toEqual({ side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 3 });
      await fireEvent.mouseLeave(location('src/cart.ts, lines 2–3'));
      expect(app.hoveredReference).toBeNull();
   });

   it('opens the file of a file thread, and the stop of a stop text thread', async () => {
      await start({ threads: [fileThread, stopTextThread] });

      await openThreadOf('file');
      await fireEvent.click(location('src/cart.ts'));
      await waitFor(() => expect(app.selectedFilePath).toBe('src/cart.ts'));

      await openThreadOf('stopText');
      await fireEvent.click(location('Stop 1 · Cart adds · “checks the quantity”'));
      await waitFor(() => expect(app.selectedStopId).toBe(seed.stopId));
   });

   it('names no place for a tour thread', async () => {
      await start({ threads: [thread({ kind: 'tour' }, 'Overall?')] });

      const view = await openThreadOf('tour');
      expect(view.queryByRole('button', { name: 'The tour' })).toBeNull();
      expect(view.getByText('The tour')).toBeTruthy();
   });
});
