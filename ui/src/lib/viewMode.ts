import { preference } from './preference';
import type { StopPassage } from './types';

/** How the viewer shows a stop: the whole pull request with the rest dimmed, the stop's files stacked and collapsed to its passages, or one of the stop's passages at a time. */
export type ViewMode = 'wholePr' | 'stopFiles' | 'stepThrough';

/** The view modes in the order that the mode switch and the settings offer them, with their labels. */
export const viewModes: readonly { value: ViewMode; label: string }[] = [
   { value: 'wholePr', label: 'Whole PR' },
   { value: 'stopFiles', label: 'Stop files' },
   { value: 'stepThrough', label: 'Step through' },
];

const viewModeValues = viewModes.map((mode) => mode.value);

/** The view mode that the settings name as the default, the whole pull request when none is stored. */
export const defaultViewModePreference = preference<ViewMode>('lotsen.defaultViewMode', viewModeValues, 'wholePr');

/** Returns the order in which step through visits a stop's passages: the primary passage first, then the related passages in Claude's order. */
export function stepOrder(passages: readonly StopPassage[]): StopPassage[] {
   return [...passages.filter((passage) => passage.role === 'primary'), ...passages.filter((passage) => passage.role !== 'primary')];
}
