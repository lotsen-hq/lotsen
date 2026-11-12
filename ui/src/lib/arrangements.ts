import { Orientation, type SerializedDockview } from 'dockview';
import { layoutVersion, type LayoutRecord } from './layoutRecord';
import { panelIds, type PanelId } from './workspacePanels';

/** A ready-made arrangement of the workspace, which the Panels menu applies. */
export type ArrangementId = 'default' | 'columns' | 'explanationAbove';

/** One part of an arrangement: a group of tabs, or a row or a column of parts, with its share of the length that its parent splits. */
type Part = { tabs: readonly PanelId[]; active: PanelId; share: number } | { parts: readonly Part[]; share: number };

const filesAndPlan: Part = { tabs: ['files', 'plan'], active: 'plan', share: 0.22 };
const stopAboveThreads: Part = {
   parts: [
      { tabs: ['stop'], active: 'stop', share: 0.6 },
      { tabs: ['threads'], active: 'threads', share: 0.4 },
   ],
   share: 0.28,
};

/** The arrangements in the order of the Panels menu, each a row of columns. */
export const arrangements: readonly { id: ArrangementId; label: string; columns: readonly Part[] }[] = [
   { id: 'default', label: 'Default', columns: [filesAndPlan, { tabs: ['diff'], active: 'diff', share: 0.5 }, stopAboveThreads] },
   {
      id: 'columns',
      label: 'Columns',
      columns: [
         {
            parts: [
               { tabs: ['files'], active: 'files', share: 0.5 },
               { tabs: ['plan'], active: 'plan', share: 0.5 },
            ],
            share: 0.22,
         },
         { tabs: ['diff'], active: 'diff', share: 0.5 },
         stopAboveThreads,
      ],
   },
   {
      id: 'explanationAbove',
      label: 'Explanation above the diff',
      columns: [
         filesAndPlan,
         {
            parts: [
               { tabs: ['stop'], active: 'stop', share: 0.4 },
               { tabs: ['diff'], active: 'diff', share: 0.6 },
            ],
            share: 0.5,
         },
         { tabs: ['threads'], active: 'threads', share: 0.28 },
      ],
   },
];

// Dockview scales a loaded layout to the workspace's size, so the record's lengths only carry the shares.
const length = 1000;

type GridNode = SerializedDockview['grid']['root'];

/**
 * Returns the layout record of an arrangement, with every panel shown.
 *
 * @param titles Each panel's title, which its tab shows.
 */
export function arrangementRecord(id: ArrangementId, titles: Record<PanelId, string>): LayoutRecord {
   const arrangement = arrangements.find((candidate) => candidate.id === id)!;
   let groups = 0;
   // A branch splits the length of its own axis among its parts, and each part spans the other axis whole.
   const node = (part: Part, size: number): GridNode =>
      'tabs' in part
         ? { type: 'leaf', data: { views: [...part.tabs], activeView: part.active, id: String(++groups) }, size }
         : { type: 'branch', data: part.parts.map((child) => node(child, child.share * length)), size };
   const root: GridNode = { type: 'branch', data: arrangement.columns.map((column) => node(column, column.share * length)), size: length };
   const panels = Object.fromEntries(panelIds.map((panel) => [panel, { id: panel, contentComponent: panel, tabComponent: 'title', title: titles[panel] }]));
   return { version: layoutVersion, dockview: { grid: { root, width: length, height: length, orientation: Orientation.HORIZONTAL }, panels }, hidden: [] };
}
