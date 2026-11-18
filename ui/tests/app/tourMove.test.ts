import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { attachClaude, backend, cartHead, openStop, openTour, readTour, seedDive, seedRepositoryTour, settled, startApp } from '../harness';
import { planPanel, resetPageEachTest, tab, worldOf } from './appPanels';
import { hoverOwnTooltip } from '../tooltip';

resetPageEachTest();

const rootTitle = 'How the cart adds up';
const diveTitle = 'The rounding in depth';
const deeperTitle = 'Rounding edge cases';
const cartPath = 'src/cart.ts';
const passage = (line: number) => ({ side: 'head', path: cartPath, startLine: line, endLine: line, role: 'primary' });
const written = (headline: string, line: number) => ({ headline, passages: [passage(line)] });
// The next commit of the default branch rounds up, which changes line 2 of the cart, so a stop on it goes stale and a stop on line 1 moves.
const roundedUp = { [cartPath]: cartHead[cartPath]!.replace('Math.round', 'Math.ceil') };

interface Family {
   rootId: string;
   rootStopIds: string[];
   diveId: string;
   deeperId: string;
   head: string;
   latest: string;
}

/** A root tour on the default branch with a stop on each of lines 1 and 2, a dive from it and a dive from that dive, each with a stop on line 2, then a newer commit of the branch. */
const family = backend().template('tourMove-family', async (world): Promise<Family> => {
   const root = await seedRepositoryTour(world, {
      title: rootTitle,
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'The total', kind: 'overview', content: written('The total sums the lines.', 1) },
               { title: 'Rounding', kind: 'context', content: written('Totals round once.', 2) },
            ],
         },
      ],
   });
   const claude = root.claude!;
   const dive = await seedDive(world, claude, {
      parentTourId: root.tourId,
      stopId: root.stopIds[1],
      title: diveTitle,
      chapters: [{ title: 'Rounding', stops: [{ title: 'Where it rounds', kind: 'inspect', content: written('It rounds at the end.', 2) }] }],
   });
   const deeper = await seedDive(world, claude, {
      parentTourId: dive.tourId,
      title: deeperTitle,
      chapters: [{ title: 'Edges', stops: [{ title: 'Half a cent', kind: 'inspect', content: written('Half a cent rounds up.', 2) }] }],
   });
   await claude.drop();
   const latest = await world.repo.commit('main', 'Round up', roundedUp, root.head);
   await world.repository.set(latest);
   return { rootId: root.tourId, rootStopIds: root.stopIds, diveId: dive.tourId, deeperId: deeper.tourId, head: root.head, latest };
});

async function openFamily(): Promise<{ app: LotsenApp; world: World; seed: Family }> {
   const { world, seed } = await worldOf(family);
   const { app } = await startApp(world);
   await openTour(app, rootTitle);
   return { app, world, seed };
}

/** Attaches a session to each tour of the family, so a move that leaves stale stops reaches Claude. */
async function attachFamily(world: World, seed: Family) {
   const claude = await attachClaude(world, seed.rootId);
   await claude.call('attach_tour', { tourId: seed.diveId });
   await claude.call('attach_tour', { tourId: seed.deeperId });
   return claude;
}

const short = (commit: string) => commit.slice(0, 8);
// The page's top bar is its first banner, before the header of an open file.
const topBar = () => screen.getAllByRole('banner')[0]!;
const banner = () => within(topBar());
const commitText = () => topBar().querySelector('[data-tour-commit]')?.textContent.trim();
const moveName = 'Move the tour to the latest commit…';
const moveButton = () => banner().getByRole('button', { name: moveName });
const dialog = () => within(screen.getByRole('dialog', { name: 'Move to the latest commit' }));
const dives = () => within(screen.getByRole('list', { name: 'Dives that move with it' }));
const checkbox = (name: string) => dives().getByRole('checkbox', { name }) as HTMLInputElement;
const commitsOf = () => [...screen.getByRole('dialog').querySelectorAll('[data-move-commits] dd')].map((value) => value.textContent.trim());

async function openMoveDialog(): Promise<void> {
   await fireEvent.click(await banner().findByRole('button', { name: moveName }));
   await screen.findByRole('dialog', { name: 'Move to the latest commit' });
}

describe("a repository tour's commit in the top bar", () => {
   it("shows the commit on the switcher's top line, and a Move button described by the commit for a branch, once the check of the ref answers", async () => {
      const { seed } = await openFamily();

      expect(commitText()).toBe(short(seed.head));
      const move = await waitFor(() => moveButton());
      expect(document.getElementById(move.getAttribute('aria-describedby')!)?.textContent).toBe(`The tour shows commit ${short(seed.head)}`);
      expect(await hoverOwnTooltip(move)).toBe(moveName);
   });

   it('opens the move dialog from the Move button', async () => {
      await openFamily();

      await fireEvent.click(await waitFor(() => moveButton()));

      expect(await screen.findByRole('dialog', { name: 'Move to the latest commit' })).toBeTruthy();
   });

   it('shows the commit without a Move button for a tour at a tag', async () => {
      const world = await backend().world();
      const tagged = await seedRepositoryTour(world, { title: 'At a tag', ref: 'v1.2.0', bound: false });
      const { app } = await startApp(world);
      await openTour(app, 'At a tag');
      await settled(app);

      expect(commitText()).toBe(short(tagged.head));
      expect(banner().queryByRole('button', { name: moveName })).toBeNull();
   });
});

describe('the move dialog', () => {
   it('shows the current and the latest commit, and the dives behind, ticked and nested as the family nests them', async () => {
      const { seed } = await openFamily();

      await openMoveDialog();

      await waitFor(() => expect(commitsOf()).toEqual([short(seed.head), short(seed.latest)]));
      expect(checkbox(diveTitle).checked).toBe(true);
      expect(checkbox(deeperTitle).checked).toBe(true);
      expect(checkbox(diveTitle).closest('li')!.contains(checkbox(deeperTitle))).toBe(true);
      await waitFor(() => expect(document.activeElement).toBe(dialog().getByRole('button', { name: 'Move' })));
   });

   it("unticks a dive's dives with it, and ticks a dive's parent with it", async () => {
      await openFamily();
      await openMoveDialog();
      await waitFor(() => checkbox(diveTitle));

      await fireEvent.click(checkbox(diveTitle));

      expect(checkbox(diveTitle).checked).toBe(false);
      expect(checkbox(deeperTitle).checked).toBe(false);

      await fireEvent.click(checkbox(deeperTitle));

      expect(checkbox(deeperTitle).checked).toBe(true);
      expect(checkbox(diveTitle).checked).toBe(true);
   });

   it('says that the tour is already at the latest commit when the ref has not moved', async () => {
      const world = await backend().world();
      const seeded = await seedRepositoryTour(world, { title: 'Up to date', bound: false });
      const { app } = await startApp(world);
      await openTour(app, 'Up to date');

      await openMoveDialog();

      await waitFor(() => expect(dialog().getByText(/The tour and its dives are already at the latest commit of the default branch/).textContent).toContain(short(seeded.head)));
      expect(dialog().queryByRole('button', { name: 'Move' })).toBeNull();
      await fireEvent.click(dialog().getByRole('button', { name: 'Close' }));
      await waitFor(() => expect(document.activeElement).toBe(moveButton()));
   });
});

describe('a move', () => {
   it('moves the tour and the ticked dives to the latest commit, marks the stale stops with the move, and reports the move to Claude', async () => {
      const { app, world, seed } = await openFamily();
      const claude = await attachFamily(world, seed);
      await fireEvent.click(tab('Plan'));
      await openMoveDialog();
      await waitFor(() => checkbox(diveTitle));

      await fireEvent.click(dialog().getByRole('button', { name: 'Move' }));

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(commitText()).toBe(short(seed.latest)));
      await waitFor(() => expect(document.activeElement).toBe(moveButton()));
      const row = await waitFor(() => planPanel().getByRole('button', { name: /^2 Context: Rounding/ }));
      await waitFor(() => expect(row.getAttribute('aria-label')).toBe('2 Context: Rounding, stale since move 1, 1 dive'));
      expect(within(row).getByText('Stale since move 1')).toBeTruthy();
      expect(
         planPanel()
            .getByRole('button', { name: /^1 Overview: The total/ })
            .getAttribute('aria-label'),
      ).not.toMatch(/stale/);
      const moved = app.selectedThreads.find((thread) => thread.kind === 'decided');
      expect(moved).toMatchObject({ decision: { kind: 'tourMoved', move: 1, commit: seed.latest, stopIds: [seed.rootStopIds[1]] } });
      expect(await claude.next('question')).toMatchObject({ tourId: seed.rootId });
      for (const tourId of [seed.diveId, seed.deeperId]) {
         expect((await readTour(world, tourId)).pin).toMatchObject({ state: 'snapshot', head: seed.latest, move: 1 });
      }

      await openStop(app, seed.rootStopIds[1]!);
      await waitFor(() =>
         expect(screen.getByRole('status', { name: 'Stale stop' }).textContent).toMatch(
            /Stale since move 1: Claude is revising this stop for move 1\. Its passages are lines from before move 1, so the file view does not mark them and they do not open\./,
         ),
      );
      app.openSummary();
      await waitFor(() => expect(screen.getByText('Stale since move 1')).toBeTruthy());
   });

   it('loads the open file again at the new commit, where the stale stop opens no passage', async () => {
      const { app, world, seed } = await openFamily();
      await attachFamily(world, seed);
      await openStop(app, seed.rootStopIds[1]!);
      await waitFor(() => expect(app.selectedText).toMatchObject({ state: 'loaded', path: cartPath, text: { commit: seed.head } }));
      await openMoveDialog();
      await waitFor(() => checkbox(diveTitle));

      await fireEvent.click(dialog().getByRole('button', { name: 'Move' }));

      await waitFor(() => expect(app.selectedText).toMatchObject({ state: 'loaded', path: cartPath, text: { commit: seed.latest } }));
      await waitFor(() => expect(app.selectedOpenStop?.stale).toMatchObject({ kind: 'stale', step: 'move', since: 1 }));
      expect(app.selectedOpenStop?.pinnedPassages).toEqual([]);
   });

   it('moves only the tour when the reviewer unticks its dive, which leaves the dive and its dive at their commit', async () => {
      const { world, seed } = await openFamily();
      await attachFamily(world, seed);
      await openMoveDialog();
      await fireEvent.click(await waitFor(() => checkbox(diveTitle)));

      await fireEvent.click(dialog().getByRole('button', { name: 'Move' }));

      await waitFor(() => expect(commitText()).toBe(short(seed.latest)));
      for (const tourId of [seed.diveId, seed.deeperId]) {
         expect((await readTour(world, tourId)).pin).toMatchObject({ head: seed.head });
      }
   });

   it("shows the server's refusal in place, and checks again when the ref moved since the check", async () => {
      const { world, seed } = await openFamily();
      await openMoveDialog();
      await waitFor(() => expect(commitsOf()).toEqual([short(seed.head), short(seed.latest)]));
      const newer = await world.repo.commit('main', 'Round up again', { 'src/notes.ts': 'export const note = 1;\n' }, seed.latest);
      await world.repository.set(newer);

      await fireEvent.click(dialog().getByRole('button', { name: 'Move' }));

      await waitFor(() => expect(dialog().getByRole('alert').textContent).toMatch(/the ref moved on to commit/));
      await waitFor(() => expect(commitsOf()).toEqual([short(seed.head), short(newer)]));
      expect(commitText()).toBe(short(seed.head));
   });

   it('says that a dive changed when a ticked dive was deleted since the check, and checks again, keeping the unticks', async () => {
      const { world, seed } = await openFamily();
      const wholeTitle = 'Everything about totals';
      await seedDive(world, await attachClaude(world, seed.rootId), { parentTourId: seed.rootId, title: wholeTitle });
      await openMoveDialog();
      await fireEvent.click(await waitFor(() => checkbox(wholeTitle)));
      await world.reviewer.call('delete_tour', { tourId: seed.deeperId });

      await fireEvent.click(dialog().getByRole('button', { name: 'Move' }));

      await waitFor(() => expect(dialog().getByRole('alert').textContent).toBe('A dive changed since the check, so nothing moved. The list shows the dives as they are now.'));
      await waitFor(() => expect(dives().queryByRole('checkbox', { name: deeperTitle })).toBeNull());
      expect(checkbox(diveTitle).checked).toBe(true);
      expect(checkbox(wholeTitle).checked).toBe(false);
   });

   it('shows the refusal in place while a tour that the move leaves stale has no session', async () => {
      const { seed } = await openFamily();
      await openMoveDialog();
      await waitFor(() => checkbox(diveTitle));

      await fireEvent.click(dialog().getByRole('button', { name: 'Move' }));

      await waitFor(() => expect(dialog().getByRole('alert').textContent).toMatch(/session/));
      expect(screen.getByRole('dialog', { name: 'Move to the latest commit' })).toBeTruthy();
      expect(commitText()).toBe(short(seed.head));
   });
});
