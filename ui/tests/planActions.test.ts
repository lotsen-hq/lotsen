import { createEvent, fireEvent, render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import StopOrderEditor from '../src/components/StopOrderEditor.svelte';
import { droppedStop, isReordered, movedStop, reconciledOrder } from '../src/lib/stopOrder';
import type { Plan, Stop, StopContent } from '../src/lib/types';
import { dragOverRow, dragRow } from './stopOrderDrag';

function content(headline: string): StopContent {
   return { headline, passages: [], html: { headline: `<p>${headline}</p>\n` } };
}

function stop(id: string, title: string, chapter: string, kind: Stop['kind'] = 'inspect'): Stop {
   return { id, title, kind, chapter, stage: 'written', status: 'ready', version: 1, earlier: [], content: content(`${title}.`) };
}

const plan: Plan = {
   chapters: [
      { id: 'c', title: 'Cart' },
      { id: 'p', title: 'Payment' },
   ],
   stops: [stop('s1', 'Cart adds', 'c', 'overview'), stop('s2', 'Line totals', 'c'), stop('s3', 'Gateway errors', 'p', 'risk')],
   fileMarks: [],
};

describe('the proposed order of stops', () => {
   const stops = plan.stops;
   const dataTransfer = { setData: () => {}, effectAllowed: 'none', dropEffect: 'none' };

   it('moves a stop up or down by one place, and leaves the order as it is at either end', () => {
      expect(movedStop(['s1', 's2', 's3'], 2, -1)).toEqual(['s1', 's3', 's2']);
      expect(movedStop(['s1', 's2', 's3'], 0, 1)).toEqual(['s2', 's1', 's3']);
      expect(movedStop(['s1', 's2', 's3'], 0, -1)).toEqual(['s1', 's2', 's3']);
      expect(movedStop(['s1', 's2', 's3'], 2, 1)).toEqual(['s1', 's2', 's3']);
   });

   it('moves a dragged stop to before or after another stop', () => {
      expect(droppedStop(['s1', 's2', 's3', 's4'], 's1', 's3', 'before')).toEqual(['s2', 's1', 's3', 's4']);
      expect(droppedStop(['s1', 's2', 's3', 's4'], 's1', 's3', 'after')).toEqual(['s2', 's3', 's1', 's4']);
      expect(droppedStop(['s1', 's2', 's3', 's4'], 's4', 's2', 'before')).toEqual(['s1', 's4', 's2', 's3']);
      expect(droppedStop(['s1', 's2', 's3', 's4'], 's4', 's2', 'after')).toEqual(['s1', 's2', 's4', 's3']);
   });

   it('moves a dragged stop to the first and the last place', () => {
      expect(droppedStop(['s1', 's2', 's3'], 's3', 's1', 'before')).toEqual(['s3', 's1', 's2']);
      expect(droppedStop(['s1', 's2', 's3'], 's1', 's3', 'after')).toEqual(['s2', 's3', 's1']);
   });

   it('leaves the order as it is after a drop on the dragged stop itself, or with a stop that the order does not name', () => {
      expect(droppedStop(['s1', 's2', 's3'], 's2', 's2', 'before')).toEqual(['s1', 's2', 's3']);
      expect(droppedStop(['s1', 's2', 's3'], 's2', 's2', 'after')).toEqual(['s1', 's2', 's3']);
      expect(droppedStop(['s1', 's2', 's3'], 'sX', 's2', 'after')).toEqual(['s1', 's2', 's3']);
      expect(droppedStop(['s1', 's2', 's3'], 's2', 'sX', 'before')).toEqual(['s1', 's2', 's3']);
   });

   it("is reordered only when it differs from the plan's order", () => {
      expect(isReordered(['s1', 's2', 's3'], stops)).toBe(false);
      expect(isReordered(['s2', 's1', 's3'], stops)).toBe(true);
      expect(isReordered(['s1', 's2'], stops)).toBe(true);
   });

   it("stays as the reviewer made it while it names each stop of the plan once, and starts again from the plan's order otherwise", () => {
      expect(reconciledOrder(['s3', 's1', 's2'], stops)).toEqual(['s3', 's1', 's2']);
      expect(reconciledOrder(['s2', 's1'], stops)).toEqual(['s1', 's2', 's3']);
      expect(reconciledOrder(['s2', 's1', 's3', 's4'], stops)).toEqual(['s1', 's2', 's3']);
      expect(reconciledOrder(['s2', 's1', 's1'], stops)).toEqual(['s1', 's2', 's3']);
      expect(reconciledOrder(['s2', 's1', 'sX'], stops)).toEqual(['s1', 's2', 's3']);
   });

   it("is edited by the stop order editor on its own, with each stop's move buttons", async () => {
      let order = ['s1', 's2', 's3'];
      const { rerender } = render(StopOrderEditor, { props: { stops, chapters: plan.chapters, order, onchange: (next: string[]) => (order = next) } });

      await fireEvent.click(screen.getByRole('button', { name: 'Move “Gateway errors” up' }));
      await rerender({ order });

      expect(order).toEqual(['s1', 's3', 's2']);
      expect(screen.getAllByRole('listitem').map((item) => item.querySelector('.title')!.textContent)).toEqual(['Cart adds', 'Gateway errors', 'Line totals']);
      expect((screen.getByRole('button', { name: 'Move “Cart adds” up' }) as HTMLButtonElement).disabled).toBe(true);
      expect((screen.getByRole('button', { name: 'Move “Gateway errors” down' }) as HTMLButtonElement).disabled).toBe(false);
   });

   describe('in the stop order editor, by drag and drop', () => {
      const rows = () => screen.getAllByRole('listitem');
      const titles = () => rows().map((item) => item.querySelector('.title')!.textContent);

      function editor(initial: string[] = ['s1', 's2', 's3']) {
         const changes: string[][] = [];
         let order = initial;
         const view = render(StopOrderEditor, {
            props: {
               stops,
               chapters: plan.chapters,
               order,
               onchange: (next: string[]) => {
                  changes.push(next);
                  order = next;
               },
            },
         });
         return { changes, sync: () => view.rerender({ order }) };
      }

      it('moves a dropped row to the half of the target row under the pointer, and announces the new position', async () => {
         const { changes, sync } = editor();

         await dragRow(rows()[0]!, rows()[2]!, 'after');
         await sync();

         expect(changes).toEqual([['s2', 's3', 's1']]);
         expect(titles()).toEqual(['Line totals', 'Gateway errors', 'Cart adds']);
         expect(screen.getByRole('status').textContent).toBe('Cart adds is now 3 of 3.');

         await dragRow(rows()[1]!, rows()[0]!, 'before');
         await sync();

         expect(changes[1]).toEqual(['s3', 's2', 's1']);
         expect(screen.getByRole('status').textContent).toBe('Gateway errors is now 1 of 3.');
      });

      it('marks the place of the drop with a line on the row under the pointer, and removes the line after the drag', async () => {
         editor();

         await dragOverRow(rows()[0]!, rows()[1]!, 'before');
         expect(rows()[1]!.classList.contains('drop-before')).toBe(true);

         await dragOverRow(rows()[0]!, rows()[1]!, 'after');
         expect(rows()[1]!.classList.contains('drop-before')).toBe(false);
         expect(rows()[1]!.classList.contains('drop-after')).toBe(true);

         await fireEvent.dragEnd(rows()[0]!);
         expect(rows()[1]!.classList.contains('drop-after')).toBe(false);
      });

      it('changes nothing after a drop on the dragged row, or a drop that leaves the row where it is, and draws no line on the dragged row', async () => {
         const { changes } = editor();

         await dragOverRow(rows()[1]!, rows()[1]!, 'before');
         expect(rows()[1]!.classList.contains('drop-before')).toBe(false);
         await dragRow(rows()[1]!, rows()[1]!, 'before');
         await dragRow(rows()[1]!, rows()[0]!, 'after');

         expect(changes).toEqual([]);
         expect(screen.getByRole('status').textContent).toBe('');
      });

      it('takes no part in a drag that did not start on one of its rows', async () => {
         const { changes } = editor();
         const target = rows()[1]!;

         // `fireEvent` returns true for an event that no handler canceled.
         expect(await fireEvent(target, createEvent.dragOver(target, { dataTransfer }))).toBe(true);
         expect(target.classList.contains('drop-before') || target.classList.contains('drop-after')).toBe(false);

         expect(await fireEvent(target, createEvent.drop(target, { dataTransfer }))).toBe(true);
         expect(changes).toEqual([]);
      });

      it('offers each row to the drag, with no skip toggle when its owner offers none', () => {
         editor();

         expect(rows().map((row) => row.getAttribute('draggable'))).toEqual(['true', 'true', 'true']);
         expect(screen.queryByRole('button', { name: /^Skip stop/ })).toBeNull();
      });
   });
});
