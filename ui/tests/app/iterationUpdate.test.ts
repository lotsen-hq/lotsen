import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { contentOf, staleOnOf } from '../../src/lib/stops';
import { decisionSummary } from '../../src/lib/tourActions';
import type { World } from '../backend/backend';
import { attachClaude, backend, checkIteration, openTour, pushIteration, seedTour, startApp, updateIteration, withEventHolds, withHolds } from '../harness';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

const cartPath = 'src/cart.ts';
const cartLines = (adds: string, totals: string): string =>
   ['export function add(cart: number[], price: number): number[] {', adds, '}', '', 'export function total(lines: number[]): number {', totals, '}', ''].join('\n');
const headAdds = '   return [...cart, Math.max(0, price)];';
const headTotals = '   return Math.round(lines.reduce((sum, line) => sum + line, 0));';
const nextAdds = '   return [...cart, Math.max(1, price)];';

/** The cart files of a later iteration that changes the lines of the stops that it names, so the server's remap makes those stops stale. A later iteration that names none adds a file. */
function iterationFiles(stale: ('adds' | 'totals')[]): Record<string, string> {
   const cart = cartLines(stale.includes('adds') ? nextAdds : headAdds, stale.includes('totals') ? '   return Math.ceil(lines.reduce((sum, line) => sum + line, 0));' : headTotals);
   return stale.length === 0 ? { 'src/notes.ts': 'export const note = 1;\n' } : { [cartPath]: cart };
}

function stopContent(headline: string, line: number): Record<string, unknown> {
   return { headline, how: 'The change guards the value.', passages: [{ side: 'head', path: cartPath, startLine: line, endLine: line, role: 'primary' }] };
}

interface Seed {
   tourId: string;
   stopIds: string[];
   base: string;
   head: string;
}

async function seedCart(world: World): Promise<Seed> {
   const seeded = await seedTour(world, {
      base: { [cartPath]: cartLines('   return [...cart, price];', '   return lines.reduce((sum, line) => sum + line, 0);') },
      head: { [cartPath]: cartLines(headAdds, headTotals) },
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'Cart adds', kind: 'inspect', content: stopContent('Adds guard the price.', 2) },
               { title: 'Line totals', kind: 'inspect', content: stopContent('Totals round.', 6) },
            ],
         },
      ],
      bound: false,
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds, base: seeded.base, head: seeded.head };
}

const cart = backend().template('iterationUpdate-cart', seedCart);

async function cartWorld(): Promise<{ world: World; seed: Seed }> {
   const world = await backend().world({ template: cart });
   return { world, seed: world.seed as Seed };
}

/** Pushes iteration `iteration` onto `parent` and lets the server's check find it, after the clock moves on so its event is newer than what the page holds. */
async function pushChecked(world: World, seed: Seed, stale: ('adds' | 'totals')[], iteration = 2, parent = seed.head): Promise<string> {
   const head = await pushIteration(world, { base: seed.base, parent }, iterationFiles(stale), iteration);
   await world.clock.advance(1000);
   await checkIteration(world, seed.tourId);
   return head;
}

type Pushed = Seed & { pushed: string };

/** The cart tour with iteration 2 pushed onto its head and found by the server's check, by the stops that iteration 2 makes stale. */
const pushedTemplates = new Map(
   ([['adds'], []] as ('adds' | 'totals')[][]).map((stale) => [
      stale.join(','),
      backend().template(`iterationUpdate-pushed-${stale.join('-') || 'none'}`, async (world): Promise<Pushed> => {
         const seed = await seedCart(world);
         return { ...seed, pushed: await pushChecked(world, seed, stale) };
      }),
   ]),
);

/** A copy of the cart tour with iteration 2 pushed and checked, with no session attached, and the head of iteration 2. */
async function pushedWorld(stale: ('adds' | 'totals')[]): Promise<{ world: World; seed: Seed; pushed: string }> {
   // The template's clock moved on with its writes, so the copy's clock starts after that.
   const world = await backend().world({ template: pushedTemplates.get(stale.join(','))!, clockStart: '2026-01-01T13:00:00Z' });
   const { pushed, ...seed } = world.seed as Pushed;
   return { world, seed, pushed };
}

const notice = () => screen.queryByRole('status', { name: 'New update' });
const noticeBar = () => within(document.querySelector<HTMLElement>('[data-iteration-notice]')!);
const updateButton = () => screen.getByRole('button', { name: /^Move to update \d+$/ }) as HTMLButtonElement;
const settle = () => new Promise((resolve) => setTimeout(resolve, 50));

describe('the Update button of the new-iteration notice', () => {
   it('is disabled with the server’s reason while no session is attached, and sends nothing', async () => {
      const { world } = await pushedWorld(['adds']);
      const { app, transport } = await startApp(world);
      await openTour(app);

      await waitFor(() => expect(notice()).not.toBeNull());

      expect(updateButton().disabled).toBe(true);
      expect(noticeBar().getByText(/is attached to no Claude Code session/)).toBeTruthy();
      await fireEvent.click(updateButton());
      expect(transport.requests.filter((request) => request.operation === 'update_iteration')).toHaveLength(0);
   });

   it('is not sent while the page shows no available iteration', async () => {
      const { world } = await cartWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);

      expect(await app.updateIteration()).toBe('The tour has no newer update to move to.');

      expect(notice()).toBeNull();
      expect(transport.requests.filter((request) => request.operation === 'update_iteration')).toHaveLength(0);
   });

   it('updates the tour, keeps the open stop, and loads the changed files and the open diff again at the new pin', async () => {
      const { world, seed, pushed: newHead } = await pushedWorld(['adds']);
      const claude = await attachClaude(world, seed.tourId);
      const { app, transport } = await startApp(world);
      await openTour(app);
      app.openStop(seed.stopIds[0]!);
      app.selectFile(cartPath);
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      const pin = app.selectedTour!.pin;
      const fileLoads = () => transport.requests.filter((request) => request.operation === 'list_changed_files').length;
      const loadsBefore = fileLoads();
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      expect(updateButton().textContent?.trim()).toBe('Move to update 2');

      await fireEvent.click(updateButton());

      await waitFor(() => expect(app.selectedDiff?.state === 'loaded' && app.selectedDiff.diff.content === 'text' ? app.selectedDiff.diff.headText : null).toBe(cartLines(nextAdds, headTotals)));
      expect(app.selectedTour?.pin).toEqual({ ...pin, head: newHead, iteration: 2 });
      expect(fileLoads()).toBe(loadsBefore + 1);
      expect(app.selectedStopId).toBe(seed.stopIds[0]);
      expect(notice()).toBeNull();
      await waitFor(() => expect(app.selectedPlan?.plan?.stops.map((candidate) => staleOnOf(candidate))).toEqual([2, undefined]));
      expect(contentOf(app.selectedPlan?.plan?.stops[1])?.passages[0]?.commit).toBe(newHead);
      const update = app.selectedThreads.find((thread) => thread.kind === 'decided');
      expect(update).toMatchObject({ kind: 'decided', messages: [{ kind: 'question', text: '' }], decision: { kind: 'iterationUpdated', iteration: 2, stopIds: [seed.stopIds[0]] } });
      expect(await claude.next('question')).toMatchObject({ tourId: seed.tourId });
      expect(decisionSummary({ kind: 'iterationUpdated', iteration: 2, stopIds: [seed.stopIds[0]!] })).toBe('Move to update 2');
   });

   it('loads the open diff again at the new pin when the load of the changed files there fails for a moment', async () => {
      const { world, seed } = await pushedWorld(['adds']);
      await attachClaude(world, seed.tourId);
      const { app, transport } = await startApp(world);
      await openTour(app);
      app.selectFile(cartPath);
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      transport.fail('list_changed_files', { status: 503 });

      expect(await app.updateIteration()).toBeNull();

      await waitFor(() => expect(app.selectedDiff?.state === 'loaded' && app.selectedDiff.diff.content === 'text' ? app.selectedDiff.diff.headText : null).toBe(cartLines(nextAdds, headTotals)));
      expect(app.selectedFiles?.state).toBe('failed');
   });

   // The event carries the revision of the response, so the page drops it, as `supersedes` says.
   it('loads the changed files once at the new pin when the update’s tourPinned event arrives after its response', async () => {
      const { world, seed } = await pushedWorld(['adds']);
      await attachClaude(world, seed.tourId);
      const transport = withEventHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      const fileLoads = () => transport.requests.filter((request) => request.operation === 'list_changed_files').length;
      const loadsBefore = fileLoads();
      const release = transport.holdKinds('tourPinned');

      expect(await app.updateIteration()).toBeNull();
      await waitFor(() => expect(app.selectedTour?.pin).toMatchObject({ iteration: 2 }));
      release();
      await waitFor(() => expect(app.selectedFiles?.state === 'loaded' ? app.selectedFiles.list.head : null).toBe(app.selectedTour?.pin.state === 'pinned' ? app.selectedTour.pin.head : ''));
      await settle();

      expect(fileLoads()).toBe(loadsBefore + 1);
   });

   // Claude's session drops once the notice shows, while the page hears no event, so the server refuses the update and the notice and its button stay while the answer is held.
   it('is busy while the update runs, and sends one update for two presses', async () => {
      const { world, seed } = await pushedWorld(['adds']);
      const claude = await attachClaude(world, seed.tourId);
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      transport.holdEvents();
      await claude.drop();
      const update = transport.holdAnswered('update_iteration');

      await fireEvent.click(updateButton());
      await waitFor(() => expect(updateButton().getAttribute('aria-busy')).toBe('true'));
      await fireEvent.click(updateButton());
      await update.answered;
      await update.release();

      await screen.findByRole('alert');
      expect(updateButton().getAttribute('aria-busy')).toBe('false');
      expect(transport.requests.filter((request) => request.operation === 'update_iteration')).toHaveLength(1);
   });

   it('is disabled with the server’s reason while the last update waits for Claude', async () => {
      const { world, seed, pushed: second } = await pushedWorld(['adds']);
      await attachClaude(world, seed.tourId);
      const { app } = await startApp(world);
      await openTour(app);
      await waitFor(() => expect(updateButton().disabled).toBe(false));

      await fireEvent.click(updateButton());
      await waitFor(() => expect(notice()).toBeNull());
      await pushChecked(world, seed, ['adds', 'totals'], 3, second);

      await waitFor(() => expect(notice()).not.toBeNull());
      await waitFor(() => expect(updateButton().disabled).toBe(true));
      const reason = app.selectedControls?.offers.updateIteration;
      expect(reason?.state).toBe('closed');
      expect(noticeBar().getByText(reason?.state === 'closed' ? reason.reason.slice(1) : '', { exact: false })).toBeTruthy();
   });

   // Claude's session drops while the page hears no event, so the page still offers the update that the server then refuses.
   it('shows the server’s reason when it refuses the update, and keeps the notice and the pin', async () => {
      const { world, seed } = await pushedWorld(['adds']);
      const claude = await attachClaude(world, seed.tourId);
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      const pin = app.selectedTour!.pin;
      transport.holdEvents();
      await claude.drop();

      await fireEvent.click(updateButton());

      expect((await screen.findByRole('alert')).textContent).toContain('the move to the new update');
      expect(notice()).not.toBeNull();
      expect(app.selectedTour?.pin).toEqual(pin);
   });

   // A check stores iteration 3 while the page's events are held, so the notice still shows iteration 2 when the reviewer clicks Update.
   it('refuses the update when the server found a newer iteration than the notice shows, and keeps the pin', async () => {
      const { world, seed, pushed: second } = await pushedWorld(['adds']);
      await attachClaude(world, seed.tourId);
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await waitFor(() => expect(notice()?.textContent).toContain('Update 2'));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      const pin = app.selectedTour!.pin;
      const release = transport.holdEvents();
      await pushChecked(world, seed, ['adds', 'totals'], 3, second);

      await fireEvent.click(updateButton());

      expect((await screen.findByRole('alert')).textContent).toContain('update 3');
      expect(app.selectedTour?.pin).toEqual(pin);
      release();
      await waitFor(() => expect(notice()?.textContent).toContain('Update 3'));
      expect(app.selectedTour?.pin).toEqual(pin);
   });

   // The platform holds every resolve until the update stored, so the server learns of iteration 3 only in its check after the update, which the update does not ask the platform for.
   it('updates to the shown iteration and then shows the newer one when the head moved again since the notice', async () => {
      const { world, seed, pushed: second } = await pushedWorld(['adds']);
      await attachClaude(world, seed.tourId);
      const { app } = await startApp(world);
      await openTour(app);
      await waitFor(() => expect(notice()?.textContent).toContain('Update 2'));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      await world.clock.advance(1000);
      await world.pullRequest.hold();
      await pushIteration(world, { base: seed.base, parent: second }, iterationFiles(['adds', 'totals']), 3);

      await fireEvent.click(updateButton());

      await waitFor(() => expect(app.selectedTour?.pin).toMatchObject({ head: second, iteration: 2 }));
      await world.pullRequest.release();
      await waitFor(() => expect(notice()?.textContent).toContain('Update 3 is available: 2 stops affected'));
   });

   // Neither iteration makes a stop stale, so no update waits for Claude and the server takes the reviewer's own update to iteration 3 while the page's answer is held.
   it('keeps a newer pin from an event when the update’s response arrives after it', async () => {
      const { world, seed, pushed: second } = await pushedWorld([]);
      await attachClaude(world, seed.tourId);
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      const update = transport.holdAnswered('update_iteration');
      await fireEvent.click(updateButton());
      await update.answered;
      await waitFor(() => expect(app.selectedTour?.pin).toMatchObject({ iteration: 2 }));

      const third = await pushIteration(world, { base: seed.base, parent: second }, { 'src/notes.ts': 'export const note = 2;\n' }, 3);
      await world.clock.advance(1000);
      await checkIteration(world, seed.tourId);
      await updateIteration(world, seed.tourId);
      await waitFor(() => expect(app.selectedTour?.pin).toMatchObject({ head: third, iteration: 3 }));
      await update.release();
      await settle();

      expect(app.selectedTour?.pin).toMatchObject({ head: third, iteration: 3 });
   });

   it('keeps the update’s plan and pin when a tour load that started before the update answers late', async () => {
      const { world, seed, pushed: newHead } = await pushedWorld(['adds']);
      await attachClaude(world, seed.tourId);
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      app.openStop(seed.stopIds[0]!);
      app.selectFile(cartPath);
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      const load = transport.holdAnswered('read_state');
      app.selectTour(seed.tourId, 'wholePr');
      await load.answered;
      await world.clock.advance(1000);

      await fireEvent.click(updateButton());
      await waitFor(() => expect(staleOnOf(app.selectedPlan?.plan?.stops[0])).toBe(2));
      await load.release();
      await settle();

      expect(staleOnOf(app.selectedPlan?.plan?.stops[0])).toBe(2);
      expect(app.selectedTour?.pin).toMatchObject({ head: newHead, iteration: 2 });
      expect(notice()).toBeNull();
      expect(app.selectedStopId).toBe(seed.stopIds[0]);
   });

   // The stop's passage is on the base's line 2, at the merge base that both pins share, so the stack keeps its file through the update.
   it('loads a file that the stop-files stack keeps through the update again at the new pin', async () => {
      const { world, seed } = await cartWorld();
      const claude = await attachClaude(world, seed.tourId);
      const passages = [{ side: 'base', path: cartPath, startLine: 2, endLine: 2, role: 'primary' }];
      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds[0], headline: 'Adds guard the price.', how: 'The change guards the value.', passages });
      await pushChecked(world, seed, ['adds']);
      const { app, transport } = await startApp(world);
      await openTour(app);
      app.setViewMode('stopFiles');
      app.openStop(seed.stopIds[0]!);
      const diffLoads = () => transport.requests.filter((request) => request.operation === 'diff_file' && request.input?.path === cartPath).length;
      await waitFor(() => expect(diffLoads()).toBeGreaterThan(0));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      await settle();
      const loadsBefore = diffLoads();

      expect(await app.updateIteration()).toBeNull();

      await waitFor(() => expect(diffLoads()).toBe(loadsBefore + 1));
      await settle();
      expect(diffLoads()).toBe(loadsBefore + 1);
      expect(app.pinnedPassages).toHaveLength(1);
   });

   it('sends no action and adds no thread when no stop is stale', async () => {
      const { world, seed } = await pushedWorld([]);
      await attachClaude(world, seed.tourId);
      const { app } = await startApp(world);
      await openTour(app);
      await waitFor(() => expect(notice()?.textContent).toContain('0 stops affected'));
      await waitFor(() => expect(updateButton().disabled).toBe(false));

      await fireEvent.click(updateButton());

      await waitFor(() => expect(app.selectedTour?.pin).toMatchObject({ iteration: 2 }));
      expect(app.selectedThreads).toEqual([]);
      await waitFor(() => expect(app.selectedPlan?.plan?.stops.map((candidate) => staleOnOf(candidate))).toEqual([undefined, undefined]));
      expect((await world.reviewer.call('read_state', { tourId: seed.tourId })).threads).toEqual([]);
   });
});

// The page's stream misses the update's tourPinned event, so the update's new pin reaches the page first through the reload after the reconnect.
describe('the open diff across an update that the page’s stream misses', () => {
   async function openDiffOffline() {
      const { world, seed } = await pushedWorld(['adds']);
      await attachClaude(world, seed.tourId);
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      app.selectFile(cartPath);
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      const release = transport.holdEvents();
      await waitFor(() => expect(app.connection).not.toBe('live'));
      return { world, seed, app, transport, release };
   }

   const shownHead = (app: LotsenApp) => (app.selectedDiff?.state === 'loaded' && app.selectedDiff.diff.content === 'text' ? app.selectedDiff.diff.headText : null);

   it('loads again at the new pin of an update from elsewhere', async () => {
      const { world, seed, app, release } = await openDiffOffline();

      await updateIteration(world, seed.tourId);
      release();

      await waitFor(() => expect(shownHead(app)).toBe(cartLines(nextAdds, headTotals)));
   });

   it('loads again at the new pin of the reviewer’s own update, whose response arrives after the reload', async () => {
      const { app, transport, release } = await openDiffOffline();
      const held = transport.holdAnswered('update_iteration');

      const update = app.updateIteration();
      await held.answered;
      release();
      await waitFor(() => expect(app.selectedTour?.pin).toMatchObject({ iteration: 2 }));
      await held.release();
      expect(await update).toBeNull();

      await waitFor(() => expect(shownHead(app)).toBe(cartLines(nextAdds, headTotals)));
   });
});

// A pull request that renames `src/old.ts` to `lib/new.ts` and changes a line, with one stop on the renamed file, whose iteration 2 changes another line of it.
describe('a renamed file across an update whose load of the changed files fails for a moment', () => {
   const renamedLines = (second: string, fourth: string) => ['export const one = 1;', second, 'export const three = 3;', fourth, 'export const five = 5;', ''].join('\n');
   const renamed = backend().template('iterationUpdate-renamed', async (world): Promise<Seed> => {
      const seeded = await seedTour(world, {
         base: { 'src/old.ts': renamedLines('export const two = 2;', 'export const four = 4;') },
         head: { 'src/old.ts': null, 'lib/new.ts': renamedLines('export const two = 20;', 'export const four = 4;') },
         chapters: [
            {
               title: 'Numbers',
               stops: [
                  {
                     title: 'Two',
                     kind: 'inspect',
                     content: { headline: 'Two grows.', how: 'The change scales the value.', passages: [{ side: 'head', path: 'lib/new.ts', startLine: 2, endLine: 2, role: 'primary' }] },
                  },
               ],
            },
         ],
         bound: false,
      });
      return { tourId: seeded.tourId, stopIds: seeded.stopIds, base: seeded.base, head: seeded.head };
   });

   async function updatedWorld() {
      const world = await backend().world({ template: renamed });
      const seed = world.seed as Seed;
      await attachClaude(world, seed.tourId);
      await pushIteration(world, { base: seed.base, parent: seed.head }, { 'lib/new.ts': renamedLines('export const two = 20;', 'export const four = 40;') }, 2);
      await world.clock.advance(1000);
      await checkIteration(world, seed.tourId);
      return { world, seed };
   }

   const diffInputs = (transport: { requests: readonly { operation: string | null; input: Record<string, unknown> | null }[] }) =>
      transport.requests.filter((request) => request.operation === 'diff_file').map((request) => request.input);

   it('loads the open diff again across the rename', async () => {
      const { world, seed } = await updatedWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      app.selectFile('lib/new.ts');
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      transport.fail('list_changed_files', { status: 503 });

      expect(await app.updateIteration()).toBeNull();

      await waitFor(() => expect(app.selectedDiff?.state === 'loaded' && app.selectedDiff.diff.content === 'text' ? app.selectedDiff.diff.headText : null).toContain('four = 40'));
      expect(diffInputs(transport).at(-1)).toEqual({ tourId: seed.tourId, path: 'lib/new.ts', oldPath: 'src/old.ts' });
      expect(app.selectedDiff?.state === 'loaded' && app.selectedDiff.diff.content === 'text' ? app.selectedDiff.diff.baseText : null).toContain('two = 2;');
   });

   it("keeps the stop-files stack's diff of the file across the rename", async () => {
      const { world, seed } = await updatedWorld();
      const { app, transport } = await startApp(world);
      await openTour(app);
      app.setViewMode('stopFiles');
      app.openStop(seed.stopIds[0]!);
      await waitFor(() => expect(diffInputs(transport)).toContainEqual({ tourId: seed.tourId, path: 'lib/new.ts', oldPath: 'src/old.ts' }));
      await waitFor(() => expect(updateButton().disabled).toBe(false));
      await settle();
      const loadsBefore = diffInputs(transport).length;
      transport.fail('list_changed_files', { status: 503 });

      expect(await app.updateIteration()).toBeNull();

      await waitFor(() => expect(diffInputs(transport).length).toBe(loadsBefore + 1));
      expect(diffInputs(transport).at(-1)).toEqual({ tourId: seed.tourId, path: 'lib/new.ts', oldPath: 'src/old.ts' });
      expect(app.stopFiles).toEqual([expect.objectContaining({ kind: 'diff', file: expect.objectContaining({ kind: 'renamed', path: 'lib/new.ts', oldPath: 'src/old.ts' }) })]);
   });
});
