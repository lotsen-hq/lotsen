import type { Stop } from './types';

/** Returns the stop IDs with the stop at `index` moved by `offset` places, or the same order when the move would leave the list. */
export function movedStop(order: readonly string[], index: number, offset: -1 | 1): string[] {
   const target = index + offset;
   if (index < 0 || index >= order.length || target < 0 || target >= order.length) {
      return [...order];
   }

   const moved = [...order];
   [moved[index], moved[target]] = [moved[target]!, moved[index]!];
   return moved;
}

/** Where a dragged stop lands, relative to the stop under the pointer. */
export type DropPlace = 'before' | 'after';

/** Returns the stop IDs with the stop `stopId` moved to the place beside `targetId`, or the same order for a drop on the stop itself or a stop that the order does not name. */
export function droppedStop(order: readonly string[], stopId: string, targetId: string, place: DropPlace): string[] {
   if (stopId === targetId || !order.includes(stopId) || !order.includes(targetId)) {
      return [...order];
   }

   const moved = order.filter((id) => id !== stopId);
   moved.splice(moved.indexOf(targetId) + (place === 'after' ? 1 : 0), 0, stopId);
   return moved;
}

/** Returns whether the proposed order differs from the order of the plan's stops. */
export function isReordered(order: readonly string[], stops: readonly Stop[]): boolean {
   return order.length !== stops.length || order.some((id, index) => id !== stops[index]!.id);
}

/**
 * Returns the reviewer's proposed order while it names each stop of the plan once, or else the plan's order.
 *
 * The server refuses a reorder that is not each stop of the latest plan once, so a plan that adds or drops a stop starts the proposal again from the plan's order.
 */
export function reconciledOrder(order: readonly string[], stops: readonly Stop[]): string[] {
   const ids = new Set(stops.map((stop) => stop.id));
   return order.length === stops.length && new Set(order).size === order.length && order.every((id) => ids.has(id)) ? [...order] : stops.map((stop) => stop.id);
}
