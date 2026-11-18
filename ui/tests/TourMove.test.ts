import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import TourMove from '../src/components/TourMove.svelte';
import type { MoveCheckOutcome, MoveOffer } from '../src/lib/tourMoves';
import { hoverOwnTooltip } from './tooltip';

const current = 'a'.repeat(40);
const older = 'b'.repeat(40);
const newer = 'c'.repeat(40);
const outcomeAt = (latest: string): MoveCheckOutcome => ({ checked: { tourId: 't', move: 'available', current, latest } });
const moveName = 'Move the tour to the latest commit…';

function renderMove(offer: MoveOffer | undefined) {
   return render(TourMove, { props: { commit: current, offer, refName: 'main', oncheck: async () => outcomeAt(newer), onmove: async () => null, onfocuslost: () => {} } });
}

describe('the Move button', () => {
   it('shows as an icon button for a movable ref, described by the commit, and opens the move dialog', async () => {
      renderMove('movable');
      const move = screen.getByRole('button', { name: moveName });

      expect(move.textContent.trim()).toBe('');
      expect(document.getElementById(move.getAttribute('aria-describedby')!)?.textContent).toBe('The tour shows commit aaaaaaaa');
      expect(await hoverOwnTooltip(move)).toBe(moveName);
      await fireEvent.click(move);

      expect(await screen.findByRole('dialog', { name: 'Move to the latest commit' })).toBeTruthy();
   });

   it.each([undefined, 'checking', 'fixed'] as const)('does not show while the offer is %s', (offer) => {
      renderMove(offer);

      expect(screen.queryByRole('button', { name: moveName })).toBeNull();
   });
});

describe('the move dialog', () => {
   it('keeps the answer of the latest check when an earlier check answers after it, as after a close and a reopen', async () => {
      const answers: ((outcome: MoveCheckOutcome) => void)[] = [];
      render(TourMove, {
         props: {
            commit: current,
            offer: 'movable',
            refName: null,
            oncheck: () => new Promise<MoveCheckOutcome>((resolve) => answers.push(resolve)),
            onmove: async () => null,
            onfocuslost: () => {},
         },
      });
      const move = screen.getByRole('button', { name: moveName });
      await fireEvent.click(move);
      await fireEvent.keyDown(await screen.findByRole('dialog'), { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await fireEvent.click(move);
      const dialog = within(await screen.findByRole('dialog'));

      answers[1]!(outcomeAt(newer));
      await waitFor(() => expect(dialog.getByText(newer.slice(0, 8))).toBeTruthy());
      answers[0]!(outcomeAt(older));
      await new Promise((resolve) => setTimeout(resolve, 0));

      expect(dialog.queryByText(older.slice(0, 8))).toBeNull();
      expect(dialog.getByText(newer.slice(0, 8))).toBeTruthy();
      expect(dialog.queryByRole('status')).toBeNull();
   });

   it('keeps Move unavailable, with its reason, while the tour is at the latest commit and every dive is unticked', async () => {
      const onmove = vi.fn(async () => null);
      const dive = { tourId: 'd1', parentId: 't', title: 'The rounding', state: 'ready' as const, pin: { state: 'snapshot' as const, head: current, remoteUrl: 'https://code.example/repo.git' } };
      render(TourMove, {
         props: {
            commit: newer,
            offer: 'movable',
            refName: null,
            oncheck: async () => ({ checked: { tourId: 't', move: 'available', current: newer, latest: newer, dives: [dive] } }),
            onmove,
            onfocuslost: () => {},
         },
      });
      await fireEvent.click(screen.getByRole('button', { name: moveName }));
      const dialog = within(await screen.findByRole('dialog'));
      const move = await dialog.findByRole('button', { name: 'Move' });
      expect(move.getAttribute('aria-disabled')).toBe('false');

      await fireEvent.click(dialog.getByRole('checkbox', { name: 'The rounding' }));
      await fireEvent.click(move);

      expect(move.getAttribute('aria-disabled')).toBe('true');
      expect(document.getElementById(move.getAttribute('aria-describedby')!)?.textContent).toBe('Tick a dive to move. The tour is at the latest commit already.');
      expect(onmove).not.toHaveBeenCalled();
   });
});
