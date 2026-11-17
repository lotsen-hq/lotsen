import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
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
});
