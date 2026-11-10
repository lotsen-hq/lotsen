import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { AnchorInput } from '../../src/lib/types';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, checkIteration, openStop, openTour, pushIteration, readTour, seedTour, startApp, updateIteration } from '../harness';
import { toggleShown } from './threadFilter';
import { openThreadView, shownIds, threadRow, threadViewElement } from './threadView';

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
const baseAdds = '   return [...cart, price];';
const headAdds = '   return [...cart, Math.max(0, price)];';
const headTotals = '   return Math.round(lines.reduce((sum, line) => sum + line, 0));';
const baseCart = cartLines(baseAdds, '   return lines.reduce((sum, line) => sum + line, 0);');
const headCart = cartLines(headAdds, headTotals);
// Iteration 2 changes line 2 alone, so the remap makes stale what points at the head's line 2 and moves what points at line 6.
const nextCart = cartLines('   return [...cart, Math.max(1, price)];', headTotals);

/** Returns a `ref:` link to one line of the cart at the side, as Claude writes it in prose. */
const ref = (label: string, side: 'base' | 'head', line: number) => `[${label} ${side} line](ref:${side}/${cartPath}#L${line}-${line})`;
const links = (label: string, line: number, baseLine = line) => `${ref(label, 'head', line)} and ${ref(label, 'base', baseLine)}`;
const passage = (side: 'base' | 'head', line: number) => ({ side, path: cartPath, startLine: line, endLine: line, role: 'primary' });

function inspect(headline: string, side: 'base' | 'head', line: number, extra: Record<string, unknown> = {}): Record<string, unknown> {
   return { headline, how: 'The change guards the value.', passages: [passage(side, line)], ...extra };
}

function risk(scenario: string, side: 'base' | 'head', line: number): Record<string, unknown> {
   return { headline: 'A risk.', scenario, passages: [passage(side, line)] };
}

const stops = {
   staleHead: { title: 'Cart adds', kind: 'inspect', content: inspect(links('a', 2), 'head', 2) },
   staleBase: { title: 'Base adds', kind: 'inspect', content: inspect(links('b', 2), 'base', 2) },
   fresh: { title: 'Line totals', kind: 'inspect', content: inspect(links('c', 6), 'head', 6) },
   revisedBefore: { title: 'Totals before', kind: 'inspect', content: inspect(ref('d', 'head', 6), 'head', 6) },
   revisedAfter: { title: 'Totals after', kind: 'inspect', content: inspect(ref('e', 'head', 6), 'head', 6) },
   revisedFromBase: { title: 'Totals at base', kind: 'inspect', content: inspect(links('f', 6), 'base', 6) },
   staleRisk: { title: 'Stale risk', kind: 'risk', content: risk(links('g', 2), 'head', 2) },
   baseRisk: { title: 'Base risk', kind: 'risk', content: risk(links('h', 2), 'base', 2) },
   freshRisk: { title: 'Fresh risk', kind: 'risk', content: risk(links('i', 6), 'head', 6) },
   staleCheck: { title: 'Stale check', kind: 'inspect', content: inspect('A check.', 'head', 2, { checks: [{ question: links('j', 2) }] }) },
   checkBefore: { title: 'Check before', kind: 'inspect', content: inspect('A check.', 'head', 6, { checks: [{ question: links('k', 6) }] }) },
   checkAfter: { title: 'Check after', kind: 'inspect', content: inspect('A check.', 'head', 6, { checks: [{ question: links('l', 6) }] }) },
} as const;

type StopName = keyof typeof stops;

interface Seed {
   tourId: string;
   stopIds: Record<StopName, string>;
   base: string;
   head: string;
}

// Each stop's links carry their own label, so a page that shows several stops, as the summary page does, names each link once.
const linked = backend().template('staleStopLinks-links', async (world): Promise<Seed> => {
   const names = Object.keys(stops) as StopName[];
   const seeded = await seedTour(world, { base: { [cartPath]: baseCart }, head: { [cartPath]: headCart }, chapters: [{ title: 'Cart', stops: names.map((name) => ({ ...stops[name] })) }] });
   const stopIds = Object.fromEntries(names.map((name, index) => [name, seeded.stopIds[index]!])) as Record<StopName, string>;
   const tourId = seeded.tourId;
   const tour = await readTour(world, tourId);
   const checkOf = (name: StopName) => {
      const stop = tour.plan!.stops.find((candidate) => candidate.id === stopIds[name]);
      return stop?.stage === 'written' ? stop.content.checks![0]!.id : '';
   };
   for (const name of ['staleCheck', 'checkBefore', 'checkAfter'] as const) {
      await world.reviewer.call('answer_check', { tourId, stopId: stopIds[name], checkId: checkOf(name), text: 'Yes.' });
   }

   await world.reviewer.call('send_answers', { tourId });
   const claude = seeded.claude!;
   await claude.call('put_stop', { tourId, stopId: stopIds.revisedBefore, ...inspect('Totals before, revised.', 'head', 6) });
   await claude.call('put_stop', { tourId, stopId: stopIds.revisedFromBase, ...inspect('Totals at base, revised.', 'head', 6) });
   await claude.call('put_stop', { tourId, stopId: stopIds.checkBefore, ...inspect('No check now.', 'head', 6) });
   await claude.drop();
   return { tourId, stopIds, base: seeded.base, head: seeded.head };
});

/**
 * Returns a copy of the linked tour updated to iteration 2 through the reviewer's real update, with the session that the update asks to revise the stale stops, which has not replied, so
 * the update waits.
 */
async function updatedWorld(): Promise<{ world: World; seed: Seed; claude: ClaudeSession }> {
   const world = await backend().world({ template: linked });
   const seed = world.seed as Seed;
   const claude = await attachClaude(world, seed.tourId);
   await pushIteration(world, { base: seed.base, parent: seed.head }, { [cartPath]: nextCart }, 2);
   await world.clock.advance(1000);
   await checkIteration(world, seed.tourId);
   await updateIteration(world, seed.tourId);
   await world.idle();
   return { world, seed, claude };
}

const link = (name: string) => screen.getByRole('link', { name });

async function openUpdated(stop: StopName): Promise<{ world: World; seed: Seed; claude: ClaudeSession; app: Awaited<ReturnType<typeof startApp>>['app'] }> {
   const { world, seed, claude } = await updatedWorld();
   const { app } = await startApp(world);
   await openTour(app);
   await openStop(app, seed.stopIds[stop]);
   return { world, seed, claude, app };
}

describe('a ref: link in a stop at an earlier commit', () => {
   it('of a stale stop reads as at an earlier commit, and a click, Enter or the pointer opens and highlights nothing', async () => {
      const { app } = await openUpdated('staleHead');
      const hash = location.hash;

      const head = await screen.findByRole('link', { name: 'a head line, at an earlier commit' });
      expect(head.getAttribute('aria-disabled')).toBe('true');
      expect(link('a base line, at an earlier commit').getAttribute('aria-disabled')).toBe('true');
      await fireEvent.click(head);
      await fireEvent.keyDown(head, { key: 'Enter' });
      await fireEvent.mouseOver(head);

      expect(app.selectedFilePath).toBeNull();
      expect(app.focus).toBeNull();
      expect(app.hoveredReference).toBeNull();
      expect(location.hash).toBe(hash);
   });

   it('of a stale stop opens at a side where a passage of the stop is at the pin', async () => {
      const { app } = await openUpdated('staleBase');

      expect((await screen.findByRole('link', { name: 'b head line, at an earlier commit' })).getAttribute('aria-disabled')).toBe('true');
      const base = link('b base line');
      expect(base.getAttribute('aria-disabled')).toBeNull();
      await fireEvent.click(base);

      await waitFor(() => expect(app.focus).toEqual({ path: cartPath, side: 'base', line: 2 }));
   });

   it('of a fresh stop opens at the pin', async () => {
      const { app } = await openUpdated('fresh');
      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      app.selectFile(cartPath);

      await fireEvent.click(await screen.findByRole('link', { name: 'c head line' }));

      await waitFor(() => expect(app.focus).toEqual({ path: cartPath, side: 'head', line: 6 }));
   });

   it('of an earlier stop version from before the update reads as at an earlier commit', async () => {
      const { app } = await openUpdated('revisedBefore');

      app.openVersion(1);

      expect((await screen.findByRole('link', { name: 'd head line, at an earlier commit' })).getAttribute('aria-disabled')).toBe('true');
   });

   // Claude revises the stop once the update moved its first version to the new pin, so that version stays on the pin.
   it('of an earlier stop version at the pin opens', async () => {
      const { seed, claude, app } = await openUpdated('revisedAfter');
      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds.revisedAfter, ...inspect('Totals after, revised.', 'head', 6) });
      await waitFor(() => expect(app.selectedStopVersions.map((known) => known.number)).toEqual([1]));

      app.openVersion(1);

      expect((await screen.findByRole('link', { name: 'e head line' })).getAttribute('aria-disabled')).toBeNull();
   });

   it('of a stale stop with a passage at the pin stays at an earlier commit behind the banner once Claude revises the stop', async () => {
      const { world, seed, claude, app } = await openUpdated('staleBase');
      await screen.findByRole('link', { name: 'b head line, at an earlier commit' });
      await world.clock.advance(1000);

      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds.staleBase, ...inspect(`Base adds, revised: ${links('b2', 2)}.`, 'head', 2) });

      await waitFor(() => expect(app.shownVersion?.reason.kind).toBe('iterationUpdate'));
      const head = link('b head line, at an earlier commit');
      expect(head.getAttribute('aria-disabled')).toBe('true');
      const [focus, hash] = [app.focus, location.hash];
      await fireEvent.click(head);
      expect(app.focus).toBe(focus);
      expect(location.hash).toBe(hash);
      expect(link('b base line').getAttribute('aria-disabled')).toBeNull();
   });

   it('of an earlier stop version with only a passage at the pin at the base reads as at an earlier commit at the head', async () => {
      const { app } = await openUpdated('revisedFromBase');

      app.openVersion(1);

      expect((await screen.findByRole('link', { name: 'f head line, at an earlier commit' })).getAttribute('aria-disabled')).toBe('true');
      expect(link('f base line').getAttribute('aria-disabled')).toBeNull();
   });

   describe('on the summary page', () => {
      async function openSummary(): Promise<{ seed: Seed; claude: ClaudeSession; app: Awaited<ReturnType<typeof startApp>>['app'] }> {
         const { world, seed, claude } = await updatedWorld();
         const { app } = await startApp(world);
         await openTour(app);
         app.openSummary();
         await screen.findByRole('main', { name: 'Summary' });
         return { seed, claude, app };
      }

      it("reads as at an earlier commit in a stale risk stop's scenario, and a click opens nothing", async () => {
         const { app } = await openSummary();

         const head = await screen.findByRole('link', { name: 'g head line, at an earlier commit' });
         expect(head.getAttribute('aria-disabled')).toBe('true');
         expect(link('g base line, at an earlier commit').getAttribute('aria-disabled')).toBe('true');
         await fireEvent.click(head);
         expect(app.focus).toBeNull();
      });

      it('opens at a side where a passage of a stale stop is at the pin', async () => {
         await openSummary();

         expect((await screen.findByRole('link', { name: 'h head line, at an earlier commit' })).getAttribute('aria-disabled')).toBe('true');
         expect(link('h base line').getAttribute('aria-disabled')).toBeNull();
      });

      it("opens in a fresh stop's scenario", async () => {
         await openSummary();

         expect((await screen.findByRole('link', { name: 'i head line' })).getAttribute('aria-disabled')).toBeNull();
         expect(link('i base line').getAttribute('aria-disabled')).toBeNull();
      });

      it("reads as at an earlier commit in a stale stop's check question", async () => {
         await openSummary();

         expect((await screen.findByRole('link', { name: 'j head line, at an earlier commit' })).getAttribute('aria-disabled')).toBe('true');
      });

      it("reads as at an earlier commit in an outdated answer's question of an earlier stop version from before the update", async () => {
         await openSummary();

         expect((await screen.findByRole('link', { name: 'k head line, at an earlier commit' })).getAttribute('aria-disabled')).toBe('true');
      });

      // Claude drops the check once the update moved the stop's first version to the new pin, so the answer's question stays on the pin.
      it("opens in an outdated answer's question of an earlier stop version at the pin", async () => {
         const { seed, claude } = await openSummary();

         await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds.checkAfter, ...inspect('No check now.', 'head', 6) });

         expect((await screen.findByRole('link', { name: 'l head line' })).getAttribute('aria-disabled')).toBeNull();
      });
   });
});

interface ThreadSeed {
   tourId: string;
   base: string;
   head: string;
   /** The server's IDs of the threads, by the role that each plays across the update. */
   threads: Record<'left' | 'moved' | 'base' | 'leftFile' | 'file', string>;
   /** The head of iteration 2. */
   next: string;
}

// The head adds src/old.ts, which iteration 2 deletes, so a file thread on it goes stale with the code thread on the head's line 2. The server's check found iteration 2, after the clock moved on.
const threaded = backend().template('staleStopLinks-threads', async (world): Promise<ThreadSeed> => {
   const seeded = await seedTour(world, {
      base: { [cartPath]: baseCart },
      head: { [cartPath]: headCart, 'src/old.ts': 'export const old = 1;\n' },
      chapters: [{ title: 'Cart', stops: [{ title: 'Line totals', kind: 'inspect', content: inspect('Totals round.', 'head', 6) }] }],
   });
   const ask = async (text: string, anchor: AnchorInput) => (await world.reviewer.call('ask_question', { tourId: seeded.tourId, text, anchor })).id;
   const threads = {
      left: await ask('About the add?', { kind: 'code', side: 'head', path: cartPath, startLine: 2, endLine: 2 }),
      moved: await ask('About the total?', { kind: 'code', side: 'head', path: cartPath, startLine: 6, endLine: 6 }),
      base: await ask('About the old add?', { kind: 'code', side: 'base', path: cartPath, startLine: 2, endLine: 2 }),
      leftFile: await ask('About the old file?', { kind: 'file', side: 'head', path: 'src/old.ts' }),
      file: await ask('About the cart?', { kind: 'file', side: 'head', path: cartPath }),
   };
   await seeded.claude!.drop();
   const next = await pushIteration(world, { base: seeded.base, parent: seeded.head }, { [cartPath]: nextCart, 'src/old.ts': null }, 2);
   await world.clock.advance(1000);
   await checkIteration(world, seeded.tourId);
   return { tourId: seeded.tourId, base: seeded.base, head: seeded.head, threads, next };
});

/** Returns a copy of the threaded tour with a bound session and iteration 2 available, and updates it unless `update` is false. */
async function threadWorld(update = true): Promise<{ world: World; seed: ThreadSeed; next: string }> {
   // The template's clock moved on with its writes, so the copy's clock starts after that.
   const world = await backend().world({ template: threaded, clockStart: '2026-01-01T13:00:00Z' });
   const seed = world.seed as ThreadSeed;
   const next = seed.next;
   await attachClaude(world, seed.tourId);
   if (update) {
      await updateIteration(world, seed.tourId);
      await world.idle();
   }

   return { world, seed, next };
}

describe('a thread left on an earlier commit', () => {
   it('gets no marker in the diff or the file heading, while a thread that the update moved shows on its new lines', async () => {
      const { world, seed } = await threadWorld();
      const { app, transport } = await startApp(world);
      // The diff loads before the changed files here, and the open file's decorations wait for the list.
      const releaseFiles = transport.hold('list_changed_files', 'response');
      await openTour(app);
      const { left, moved, base, leftFile, file } = seed.threads;

      app.selectFile(cartPath);

      await waitFor(() => expect(app.selectedDiff?.state).toBe('loaded'));
      expect(app.selectedFiles?.state).toBe('loading');
      releaseFiles();
      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
      expect(app.fileDecorations.threads?.map((thread) => thread.id)).toEqual([moved, base]);
      expect(app.selectedThreadsAtPin.map((thread) => thread.id)).toEqual([moved, base, file]);
      expect(app.selectedThreads.filter((thread) => thread.state === 'outdated').map((thread) => thread.id)).toEqual([left, leftFile]);
   });

   it('loses its marker on an update that cannot move its anchor, while a thread that the update moves keeps its marker', async () => {
      const { world, seed } = await threadWorld(false);
      const { app } = await startApp(world);
      await openTour(app);
      const { left, moved, base } = seed.threads;
      app.selectFile(cartPath);
      await waitFor(() => expect(app.fileDecorations.threads?.map((thread) => thread.id)).toEqual([left, moved, base]));

      expect(await app.updateIteration()).toBeNull();

      await waitFor(() => expect(app.fileDecorations.threads?.map((thread) => thread.id)).toEqual([moved, base]));
      expect(app.selectedThreads.slice(0, 2).map((thread) => [thread.id, thread.state])).toEqual([
         [left, 'outdated'],
         [moved, 'open'],
      ]);
   });

   // Iteration 3 puts back the head's line 2 as the thread read it, so the remap from the thread's commit finds its lines untouched.
   it('moves to the new pin with a later update that can move it, and stays outdated', async () => {
      const { world, seed, next } = await threadWorld();
      const { app } = await startApp(world);
      await openTour(app);
      const { left, moved, base, file } = seed.threads;
      expect(app.selectedThreadsAtPin.map((thread) => thread.id)).toEqual([moved, base, file]);
      await pushIteration(world, { base: seed.base, parent: next }, { [cartPath]: headCart }, 3);
      await world.clock.advance(1000);
      await checkIteration(world, seed.tourId);
      await waitFor(() => expect(screen.queryByRole('status', { name: 'New update' })).not.toBeNull());

      expect(await app.updateIteration()).toBeNull();

      await waitFor(() => expect(app.selectedThreadsAtPin.map((thread) => thread.id)).toEqual([left, moved, base, file]));
      expect(app.selectedThreads.find((thread) => thread.id === left)?.state).toBe('outdated');
   });

   it('shows under the Outdated filter, hidden by default, with the commit it stays on and no link to its lines', async () => {
      const { world, seed } = await threadWorld();
      const { app } = await startApp(world);
      await openTour(app);
      const { left, moved, leftFile } = seed.threads;
      await waitFor(() => expect(shownIds()).toContain(moved));
      expect(shownIds()).not.toContain(left);
      expect(shownIds()).not.toContain(leftFile);

      await toggleShown('Outdated');

      await waitFor(() => expect(shownIds()).toContain(left));
      expect(shownIds()).toContain(leftFile);
      expect(within(threadRow(left)!).getByText('Outdated')).toBeTruthy();
      const view = await openThreadView(left);
      expect(view.getByText('Outdated')).toBeTruthy();
      expect(view.getByText((_, element) => element?.matches('[data-earlier-commit]') === true).textContent).toBe(`On commit ${seed.head.slice(0, 8)}, which the tour no longer shows.`);
      expect(view.queryByRole('button', { name: `${cartPath}, line 2` })).toBeNull();
      expect((await openThreadView(moved)).getByRole('button', { name: `${cartPath}, line 6` })).toBeTruthy();
      expect(threadViewElement()!.querySelector('[data-earlier-commit]')).toBeNull();
      expect(app.selectedThreads.find((thread) => thread.id === left)?.state).toBe('outdated');
   });
});
