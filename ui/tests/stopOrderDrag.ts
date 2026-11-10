import { createEvent, fireEvent } from '@testing-library/svelte';
import { vi } from 'vitest';
import type { DropPlace } from '../src/lib/stopOrder';

const dataTransfer = { setData: () => {}, effectAllowed: 'none', dropEffect: 'none' };

function dragEvent(type: 'dragOver' | 'drop', element: Element, clientY: number): Event {
   const event = createEvent[type](element, { dataTransfer });
   // jsdom has no DragEvent, so the generic event that stands in for it drops `clientY` from its init.
   Object.defineProperty(event, 'clientY', { value: clientY });
   return event;
}

/** Fires the events of a drag of a row of the stop order editor over the row of another stop, with the pointer on the given half of that row. */
export async function dragOverRow(row: Element, target: Element, place: DropPlace): Promise<void> {
   vi.spyOn(target, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 100, 300, 40));
   await fireEvent.dragStart(row, { dataTransfer });
   await fireEvent(target, dragEvent('dragOver', target, place === 'before' ? 110 : 130));
}

/** Fires the events of a whole drag of a row of the stop order editor and its drop on the given half of another row. */
export async function dragRow(row: Element, target: Element, place: DropPlace): Promise<void> {
   await dragOverRow(row, target, place);
   await fireEvent(target, dragEvent('drop', target, place === 'before' ? 110 : 130));
   await fireEvent.dragEnd(row, { dataTransfer });
}
