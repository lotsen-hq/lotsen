import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { contentOf, staleOnOf, versionOf } from '../../src/lib/stops';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, checkIteration, openTour, prLabel, pushIteration, seedTour, startApp, updateIteration, withEventHolds, withHolds } from '../harness';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

const cartPath = 'src/cart.ts';
const baseAdds = '   return [...cart, price];';
const headAdds = '   return [...cart, Math.max(0, price)];';
const nextAdds = '   return [...cart, Math.max(1, price)];';
const headTotals = '   return Math.round(lines.reduce((sum, line) => sum + line, 0));';
const cartLines = (adds: string, totals: string, between: string[] = []): string =>
   ['export function add(cart: number[], price: number): number[] {', adds, '}', ...between, '', 'export function total(lines: number[]): number {', totals, '}', ''].join('\n');

/**
 * The cart file of iteration 2, as the server's remap meets it: `adds` changes line 2, which makes the stop on it stale; `moved` also inserts four lines above the totals, which move from line 6
 * to line 10; `restored` sets line 2 back to the base, so a passage on the base's line 2 no longer touches the diff.
 */
function iterationFiles(change: 'adds' | 'moved' | 'restored'): Record<string, string> {
   const inserted = ['', 'export function count(cart: number[]): number {', '   return cart.length;', '}'];
   return { [cartPath]: change === 'restored' ? cartLines(baseAdds, headTotals) : cartLines(nextAdds, headTotals, change === 'moved' ? inserted : []) };
}

function content(headline: string, line: number, side: 'base' | 'head' = 'head'): Record<string, unknown> {
   return { headline, how: 'The change guards the value.', passages: [{ side, path: cartPath, startLine: line, endLine: line, role: 'primary' }] };
}

interface Seed {
   tourId: string;
   stopIds: string[];
   base: string;
   head: string;
}

async function seedCart(world: World): Promise<Seed> {
   const seeded = await seedTour(world, {
      base: { [cartPath]: cartLines(baseAdds, '   return lines.reduce((sum, line) => sum + line, 0);') },
      head: { [cartPath]: cartLines(headAdds, headTotals) },
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'Cart adds', kind: 'inspect', content: content('Adds guard the price.', 2) },
               { title: 'Line totals', kind: 'inspect', content: content('Totals round.', 6) },
            ],
         },
      ],
      bound: false,
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds, base: seeded.base, head: seeded.head };
}

/** The seeded tour with iteration 2 pushed and found by the server's check, after the clock moves on so its writes are newer than the tour's, by the change that iteration 2 makes. */
const pushed = new Map(
   (['adds', 'moved', 'restored'] as const).map((change) => [
      change,
      backend().template(`staleStops-pushed-${change}`, async (world): Promise<Seed> => {
         const seed = await seedCart(world);
         await pushIteration(world, { base: seed.base, parent: seed.head }, iterationFiles(change), 2);
         await world.clock.advance(1000);
         await checkIteration(world, seed.tourId);
         return seed;
      }),
   ]),
);

interface Ready {
   world: World;
   seed: Seed;
   claude: ClaudeSession;
   s1: string;
   s2: string;
}

/** A copy of the pushed tour of the change with a bound session. */
async function pushedWorld(change: Parameters<typeof iterationFiles>[0] = 'adds'): Promise<Ready> {
   // The template's clock moved on with its writes, so the copy's clock starts after that.
   const world = await backend().world({ template: pushed.get(change)!, clockStart: '2026-01-01T13:00:00Z' });
   const seed = world.seed as Seed;
   const claude = await attachClaude(world, seed.tourId);
   return { world, seed, claude, s1: seed.stopIds[0]!, s2: seed.stopIds[1]! };
}

/** The pushed world once the reviewer updated the tour, with the update's question that Claude received, which waits for Claude's reply. */
async function updatedWorld(options: { change?: Parameters<typeof iterationFiles>[0]; before?: (ready: Ready) => Promise<void> } = {}): Promise<Ready & { update: string }> {
   const ready = await pushedWorld(options.change);
   await options.before?.(ready);
   await updateIteration(ready.world, ready.seed.tourId);
   const question = await ready.claude.next('question');
   return { ...ready, update: question.questionId as string };
}

/** Claude's revision of a stop, at the head of iteration 2 when the stop's passage is on line 2 or 6, after the clock moves on. */
async function revise(ready: Ready, stopId: string, headline: string, line: number, side: 'base' | 'head' = 'head'): Promise<void> {
   await ready.world.clock.advance(1000);
   await ready.claude.call('put_stop', { tourId: ready.seed.tourId, stopId, ...content(headline, line, side) });
}

const replan = async (ready: Ready): Promise<void> => void (await ready.world.reviewer.call('request_tour_action', { tourId: ready.seed.tourId, action: { kind: 'replan' }, text: 'Split the cart.' }));
const reply = (ready: Ready & { update: string }) => ready.claude.call('reply', { tourId: ready.seed.tourId, questionId: ready.update, resolution: 'done', text: 'Revised.' });

async function open(app: LotsenApp): Promise<void> {
   await openTour(app);
   await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
}

async function openStop(app: LotsenApp, stopId: string): Promise<void> {
   app.openStop(stopId);
   await waitFor(() => expect(app.selectedStop?.id).toBe(stopId));
}

const staleNotice = () => screen.queryByRole('status', { name: 'Stale stop' });
const banner = () => screen.queryByRole('status', { name: 'Stop version' });
const planRow = (name: RegExp) => screen.getByRole('button', { name });
const settle = () => new Promise((resolve) => setTimeout(resolve, 50));
const at = (line: number) => ({ path: cartPath, side: 'head', line });

describe('a stale stop', () => {
   it('shows its marker in the Plan and Stop panels, saying that Claude is revising it while the update waits', async () => {
      const { world, s1 } = await updatedWorld();
      const { app } = await startApp(world);
      await open(app);

      app.openStop(s1);

      await waitFor(() => expect(staleNotice()).not.toBeNull());
      expect(staleNotice()!.textContent).toMatch(/Stale since update 2: Claude is revising this stop for update 2\. Its passages are lines from before update 2, /);
      await fireEvent.click(screen.getByRole('tab', { name: 'Plan' }));
      expect(planRow(/^1 Inspect: Cart adds/).getAttribute('aria-label')).toBe('1 Inspect: Cart adds, stale since update 2');
      expect(within(planRow(/^1 Inspect: Cart adds/)).getByText('Stale since update 2')).toBeTruthy();
      expect(planRow(/^2 Inspect: Line totals/).getAttribute('aria-label')).toBe('2 Inspect: Line totals');
   });

   it('says that it waits for a revision once no update waits', async () => {
      const ready = await updatedWorld();
      await reply(ready);
      const { app } = await startApp(ready.world);
      await open(app);

      app.openStop(ready.s1);

      await waitFor(() => expect(staleNotice()).not.toBeNull());
      expect(staleNotice()!.textContent).toMatch(/Stale since update 2: this stop waits for Claude to revise it for update 2\./);
   });

   it("names the later update while a stop that stayed stale waits for it, and the pin's update once no update waits", async () => {
      const ready = await updatedWorld();
      await reply(ready);
      const head = (await ready.world.reviewer.call('read_state', { tourId: ready.seed.tourId })) as { pin: { head: string } };
      await pushIteration(ready.world, { base: ready.seed.base, parent: head.pin.head }, { 'src/later.ts': 'export const later = 1;\n' }, 3);
      await ready.world.clock.advance(1000);
      await checkIteration(ready.world, ready.seed.tourId);
      await updateIteration(ready.world, ready.seed.tourId);
      const question = await ready.claude.next('question');
      const { app } = await startApp(ready.world);
      await open(app);

      app.openStop(ready.s1);

      await waitFor(() => expect(staleNotice()?.textContent).toMatch(/Stale since update 2: Claude is revising this stop for update 3\./));
      await ready.claude.call('reply', { tourId: ready.seed.tourId, questionId: question.questionId as string, resolution: 'done', text: 'Revised.' });
      await waitFor(() => expect(staleNotice()?.textContent).toMatch(/Stale since update 2: this stop waits for Claude to revise it for update 3\./));
   });

   it('opens no file and focuses no lines, gets no decoration in the diff at the new pin, and its passage does not open', async () => {
      const { world, s1 } = await updatedWorld();
      const { app } = await startApp(world);
      await open(app);

      app.openStop(s1);
      await waitFor(() => expect(staleNotice()).not.toBeNull());
      expect(app.selectedFilePath).toBeNull();
      expect(app.focus).toBeNull();
      expect(app.stepPassages).toEqual([]);

      app.selectFile(cartPath);
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      expect(app.fileDecorations.passages).toEqual([]);

      const row = screen.getByRole('button', { name: /^primary passage, src\/cart\.ts, line 2, at an earlier commit$/ });
      expect(row.getAttribute('aria-disabled')).toBe('true');
      row.click();
      expect(app.focus).toBeNull();
   });

   it('leaves a fresh stop of the same tour decorated, focused and opening at the new pin', async () => {
      const { world, s2 } = await updatedWorld();
      const { app } = await startApp(world);
      await open(app);

      app.openStop(s2);

      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      expect(app.focus).toEqual(at(6));
      expect(app.fileDecorations.passages).toMatchObject([{ side: 'head', startLine: 6, endLine: 6, role: 'primary' }]);
      expect(staleNotice()).toBeNull();
      expect(screen.getByRole('button', { name: /^primary passage, src\/cart\.ts, line 6$/ }).getAttribute('aria-disabled')).toBeNull();
   });

   // The stop's one passage is on the base's line 2, which iteration 2 no longer changes, so the stop goes stale with its passage at the merge base that both pins share.
   it('keeps a passage decorated whose side kept its commit, and says nothing of passages when none is at an earlier commit', async () => {
      const { world, s1 } = await updatedWorld({ change: 'restored', before: (ready) => revise(ready, ready.s1, 'Adds guard the price.', 2, 'base') });
      const { app } = await startApp(world);
      await open(app);

      app.openStop(s1);

      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      expect(app.fileDecorations.passages).toMatchObject([{ side: 'base', startLine: 2 }]);
      expect(staleNotice()!.textContent).not.toMatch(/Its passages/);
   });

   it('shows from an update at the new pin, with no decoration for its passages there', async () => {
      const { world, s1, s2 } = await pushedWorld();
      const { app } = await startApp(world);
      await open(app);
      app.openStop(s1);
      await waitFor(() => expect(app.fileDecorations.passages).toHaveLength(1));
      expect(app.focus).toEqual(at(2));
      const pins = app.filesPin;

      expect(await app.updateIteration()).toBeNull();

      await waitFor(() => expect(staleNotice()).not.toBeNull());
      await waitFor(() => expect([null, pins]).not.toContain(app.filesPin));
      expect(app.fileDecorations.passages).toEqual([]);
      expect(app.focus).toBeNull();
      app.openStop(s2);
      await waitFor(() => expect(app.fileDecorations.passages).toHaveLength(1));
      expect(app.focus).toEqual(at(6));
   });

   it('keeps the focus of a fresh stop under view through an update that makes another stop stale', async () => {
      const { world, s2 } = await pushedWorld();
      const { app } = await startApp(world);
      await open(app);
      app.openStop(s2);
      await waitFor(() => expect(app.fileDecorations.passages).toHaveLength(1));
      const pins = app.filesPin;

      expect(await app.updateIteration()).toBeNull();

      await waitFor(() => expect([null, pins]).not.toContain(app.filesPin));
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      expect(app.focus).toEqual(at(6));
      expect(app.fileDecorations.passages).toHaveLength(1);
   });

   describe('the focus of the stop under view across an update', () => {
      async function openBeforeUpdate(change: Parameters<typeof iterationFiles>[0], stop: 1 | 2) {
         const ready = await pushedWorld(change);
         const transport = withEventHolds(withHolds(ready.world.transport()));
         const { app } = await startApp(ready.world, { transport });
         await open(app);
         app.openStop(stop === 1 ? ready.s1 : ready.s2);
         await waitFor(() => expect(app.focus).toEqual(at(stop === 1 ? 2 : 6)));
         return { ...ready, app, transport };
      }

      it.each([
         ['a fresh stop keeps its lines', 'adds', 2, 6],
         ['a moved passage focuses its new lines', 'moved', 2, 10],
         ['a stale stop focuses nothing', 'adds', 1, null],
      ] as const)('with the files at the new pin before the plan, %s', async (_, change, stop, line) => {
         const { app, transport } = await openBeforeUpdate(change, stop);
         const pins = app.filesPin;
         const release = transport.holdKinds('planSet');

         expect(await app.updateIteration()).toBeNull();
         await waitFor(() => expect([null, pins]).not.toContain(app.filesPin));
         expect(app.focus).toBeNull();
         release();

         await waitFor(() => expect(staleOnOf(app.selectedPlan?.plan?.stops[0])).toBe(2));
         await waitFor(() => expect(app.focus).toEqual(line === null ? null : at(line)));
      });

      it.each([
         ['a fresh stop keeps its lines', 'adds', 2, 6],
         ['a moved passage focuses its new lines', 'moved', 2, 10],
         ['a stale stop focuses nothing', 'adds', 1, null],
      ] as const)('with the plan before the files at the new pin, %s', async (_, change, stop, line) => {
         const { app, transport } = await openBeforeUpdate(change, stop);
         const pins = app.filesPin;
         const release = transport.holdKinds('planSet');
         const files = transport.holdAnswered('list_changed_files');

         expect(await app.updateIteration()).toBeNull();
         await files.answered;
         release();
         await waitFor(() => expect(staleOnOf(app.selectedPlan?.plan?.stops[0])).toBe(2));
         expect(app.focus).toEqual(line === null ? null : at(line));
         await files.release();

         await waitFor(() => expect([null, pins]).not.toContain(app.filesPin));
         expect(app.focus).toEqual(line === null ? null : at(line));
      });

      it('keeps step through on a related passage that the update moves', async () => {
         const ready = await pushedWorld('moved');
         await ready.claude.call('put_stop', {
            tourId: ready.seed.tourId,
            stopId: ready.s2,
            headline: 'Totals round.',
            how: 'The change guards the value.',
            passages: [
               { side: 'head', path: cartPath, startLine: 6, endLine: 6, role: 'primary' },
               { side: 'head', path: cartPath, startLine: 5, endLine: 5, role: 'related' },
            ],
         });
         const transport = withEventHolds(ready.world.transport());
         const { app } = await startApp(ready.world, { transport });
         await open(app);
         app.setViewMode('stepThrough');
         app.openStop(ready.s2);
         app.openNextPassage();
         await waitFor(() => expect(app.focus).toEqual(at(5)));
         const release = transport.holdKinds('planSet');

         expect(await app.updateIteration()).toBeNull();
         release();

         await waitFor(() => expect(app.focus).toEqual(at(9)));
         expect(app.focusedPassageIndex).toBe(1);
         expect(app.focusedPassage).toMatchObject({ role: 'related', startLine: 9 });
      });

      // The update reaches the page only through the reload after it reconnects, which brings the moved passage.
      it('follows a passage that the reload after a reconnect brings at other lines, and scrolls again only then', async () => {
         const { world, seed, app, transport } = await openBeforeUpdate('moved', 2);
         const before = app.focus;
         transport.cutEventStreams();
         await waitFor(() => expect(app.connection).not.toBe('live'));
         await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
         expect(app.focus).toBe(before);

         const release = transport.holdEvents();
         await updateIteration(world, seed.tourId);
         release();

         await waitFor(() => expect(app.focus).toEqual(at(10)));
      });
   });

   it.each([
      ['a stale stop', 1, 2, null],
      ['a fresh stop', 2, 6, at(6)],
   ] as const)('restores a history entry with the lines of a passage of %s, focusing them only at the pin', async (_, stop, line, focus) => {
      const { world, seed, s1, s2 } = await updatedWorld();
      const stopId = stop === 1 ? s1 : s2;
      history.replaceState(null, '', `/#tour=${seed.tourId}&stop=${stopId}&side=head&file=${cartPath}&lines=${line}-${line}`);

      const { app } = await startApp(world);

      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      expect(app.selectedStopId).toBe(stopId);
      expect(app.focus).toEqual(focus);
   });
});

describe('an earlier stop version from the picker', () => {
   // Claude revised the stop before the update, so its first version stays at the head of iteration 1 while the update moves the second to the new head.
   it('shows no stale marker, and its passages at an earlier commit get no decoration at the pin', async () => {
      const { world, s2 } = await updatedWorld({ before: (ready) => revise(ready, ready.s2, 'Totals round, revised.', 6) });
      const { app } = await startApp(world);
      await open(app);
      app.openStop(s2);
      await waitFor(() => expect(app.fileDecorations.passages).toHaveLength(1));

      app.openVersion(1);

      await waitFor(() => expect(app.shownVersion?.number).toBe(1));
      expect(app.fileDecorations.passages).toEqual([]);
      expect(staleNotice()).toBeNull();
   });

   it('of a stale stop shows no stale marker', async () => {
      const { world, s1 } = await updatedWorld({ before: (ready) => revise(ready, ready.s1, 'Adds guard the price, revised.', 2) });
      const { app } = await startApp(world);
      await open(app);
      app.openStop(s1);
      await waitFor(() => expect(staleNotice()).not.toBeNull());

      app.openVersion(1);

      await waitFor(() => expect(staleNotice()).toBeNull());
      expect(staleOnOf(app.selectedStop)).toBeUndefined();
   });
});

describe('the stop under view while an iteration update waits', () => {
   const heldForUpdate = { number: 1, current: 2, reason: { kind: 'iterationUpdate', iteration: 2 } };

   it('keeps the version the reviewer read behind the banner, which names the iteration, when Claude revises the stop', async () => {
      const ready = await updatedWorld();
      const { app } = await startApp(ready.world);
      await open(app);
      await openStop(app, ready.s1);

      await revise(ready, ready.s1, 'Adds guard the price, revised.', 2);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldForUpdate);
      expect(contentOf(app.selectedStop)?.headline).toBe('Adds guard the price.');
      expect(banner()!.textContent).toMatch(/Claude revised this stop for update 2 while you read it\. This is the version you read, version 1/);
      expect(staleNotice()).toBeNull();

      await fireEvent.click(within(banner()!).getByRole('button', { name: 'Show the new version' }));

      await waitFor(() => expect(contentOf(app.selectedStop)?.headline).toBe('Adds guard the price, revised.'));
      expect(staleOnOf(app.selectedStop)).toBeUndefined();
      expect(app.pinnedPassages).toHaveLength(1);
   });

   it('updates another stop silently', async () => {
      const ready = await updatedWorld();
      const { app } = await startApp(ready.world);
      await open(app);
      await openStop(app, ready.s1);

      await revise(ready, ready.s2, 'Totals round, revised.', 6);

      await waitFor(() => expect(versionOf(app.selectedPlan?.plan?.stops[1])).toBe(2));
      expect(app.shownVersion).toBeNull();
      await openStop(app, ready.s2);
      expect(app.shownVersion).toBeNull();
      expect(contentOf(app.selectedStop)?.headline).toBe('Totals round, revised.');
   });

   it('holds a revision of a stop under view that the update did not name, as any revision', async () => {
      const ready = await updatedWorld();
      const { app } = await startApp(ready.world);
      await open(app);
      await openStop(app, ready.s2);

      await revise(ready, ready.s2, 'Totals round, revised.', 6);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'revised' } });
   });

   // A replan or a revise that waits asks Claude for revisions, so the page does not hold them, unless the waiting update names the stop.
   it.each([
      ['a replan waits, and the stop', 'replanAfterReply', 1, null],
      ['a replan waits beside an update that names', 'replanFirst', 1, heldForUpdate],
      ['a replan waits beside an update that does not name', 'replanFirst', 2, null],
      ['the reviewer asked to revise, and the update names,', 'reviseFirst', 1, null],
   ] as const)('while %s the stop under view, a revision of it shows as the shown version says', async (_, setup, stop, shown) => {
      const ready = await updatedWorld({
         before: async (pushed) => {
            if (setup === 'replanFirst') {
               await replan(pushed);
            } else if (setup === 'reviseFirst') {
               await pushed.world.reviewer.call('request_tour_action', { tourId: pushed.seed.tourId, action: { kind: 'revise', target: { kind: 'stop', stopId: pushed.s1 } }, text: 'Say more.' });
            }
         },
      });
      if (setup === 'replanAfterReply') {
         await reply(ready);
         await replan(ready);
      }

      const [stopId, headline, line] = stop === 1 ? [ready.s1, 'Adds guard the price.', 2] : [ready.s2, 'Totals round.', 6];
      const { app } = await startApp(ready.world);
      await open(app);
      await openStop(app, stopId);

      await revise(ready, stopId, `${headline} Revised.`, line);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(shown);
      expect(contentOf(app.selectedStop)?.headline).toBe(shown === null ? `${headline} Revised.` : headline);
   });

   it('holds a stop that the update names beside a replan after the reviewer switches to another tour and back', async () => {
      const ready = await updatedWorld({ before: replan });
      await ready.claude.call('create_tour', { prUrl: 'https://platform.test/shop/pull/7', familiarity: 'some' });
      await ready.world.idle();
      const { app } = await startApp(ready.world);
      await open(app);
      await openStop(app, ready.s1);
      await openTour(app, 'shop #7');
      await openTour(app, prLabel);
      await waitFor(() => expect(app.selectedPlan?.plan).toBeTruthy());
      await openStop(app, ready.s1);

      await revise(ready, ready.s1, 'Adds guard the price, revised.', 2);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldForUpdate);
   });

   it.each([
      ["beside a replan when the reload after a reconnect brings the revision and Claude's reply to the update", true],
      ["when the reload after a reconnect brings the revision and Claude's reply to the update together", false],
   ])('holds a stop that the update names %s', async (_, withReplan) => {
      const ready = await updatedWorld(withReplan ? { before: replan } : {});
      const transport = withHolds(ready.world.transport());
      const { app } = await startApp(ready.world, { transport });
      await open(app);
      await openStop(app, ready.s1);

      const release = transport.holdEvents();
      await revise(ready, ready.s1, 'Adds guard the price, revised.', 2);
      await reply(ready);
      release();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldForUpdate);
   });

   it('keeps the hold when a load that the server answered before the revision arrives after it', async () => {
      const ready = await updatedWorld();
      const transport = withHolds(ready.world.transport());
      const { app } = await startApp(ready.world, { transport });
      await open(app);
      await openStop(app, ready.s1);

      const load = transport.holdAnswered('read_state');
      transport.cutEventStreams();
      await load.answered;
      await revise(ready, ready.s1, 'Adds guard the price, revised.', 2);
      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      await load.release();
      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      await settle();

      expect(versionOf(app.currentStop)).toBe(2);
      expect(app.shownVersion).toEqual(heldForUpdate);
   });

   it("holds the stop under view that the reviewer's own update made stale, once Claude revises it", async () => {
      const ready = await pushedWorld();
      const { app } = await startApp(ready.world);
      await open(app);
      await openStop(app, ready.s1);
      await waitFor(() => expect(app.focus).toEqual(at(2)));
      expect(await app.updateIteration()).toBeNull();
      await waitFor(() => expect(staleNotice()).not.toBeNull());

      await revise(ready, ready.s1, 'Adds guard the price, revised.', 2);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual(heldForUpdate);
      expect(app.focus).toBeNull();

      await fireEvent.click(within(banner()!).getByRole('button', { name: 'Show the new version' }));

      await waitFor(() => expect(contentOf(app.selectedStop)?.headline).toBe('Adds guard the price, revised.'));
      expect(app.focus).toBeNull();
   });
});
