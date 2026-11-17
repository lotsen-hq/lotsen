import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import TourMove from '../src/components/TourMove.svelte';
import type { MoveCheckOutcome } from '../src/lib/tourMoves';

const current = 'a'.repeat(40);
const older = 'b'.repeat(40);
const newer = 'c'.repeat(40);
const outcomeAt = (latest: string): MoveCheckOutcome => ({ checked: { tourId: 't', move: 'available', current, latest } });

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
      const move = screen.getByRole('button', { name: 'Move…' });
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
      await fireEvent.click(screen.getByRole('button', { name: 'Move…' }));
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
