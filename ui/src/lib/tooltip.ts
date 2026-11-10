import type { Tooltip } from 'bits-ui';
import { getContext, hasContext, setContext } from 'svelte';

const scopeKey = Symbol('tooltip scope');
const groupKey = Symbol('tooltip group');

/** The tooltip that a group's triggers share, which shows the text of the trigger it opens for, read from the trigger's payload. */
export type TooltipGroupTether = Tooltip.Tether<() => string>;

/**
 * Marks the calling component as a tooltip scope, during its initialization.
 *
 * @returns Whether an ancestor is already a scope, so the caller needs no provider of its own.
 */
export function enterTooltipScope(): boolean {
   if (hasContext(scopeKey)) {
      return true;
   }

   setContext(scopeKey, true);
   return false;
}

/** Makes the calling component's tooltips share the group's tooltip, during its initialization. */
export function setTooltipGroup(tether: TooltipGroupTether): void {
   setContext(groupKey, tether);
}

/** Returns the tooltip group of the calling component, during its initialization, or undefined when it is in none. */
export function tooltipGroup(): TooltipGroupTether | undefined {
   return getContext<TooltipGroupTether | undefined>(groupKey);
}
