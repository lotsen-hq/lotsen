import type { SerializedDockview, SerializedPopoutGroup } from 'dockview';
import { storedPreference } from './preference';
import { panelIds, type PanelId } from './workspacePanels';

/** The version of {@link LayoutRecord}. Change it when the record's shape changes, so a page never applies a record that it cannot read. */
export const layoutVersion = 1;

/** The group that a hidden or popped-out panel shared, and its tab's place there, to which the panel goes back when it shows or its window closes. */
export interface PanelHome {
   groupId: string;
   index: number;
}

/** A panel of the workspace that the reviewer hid, with the group it goes back to, or null when it had a group of its own. */
export interface HiddenPanel {
   id: PanelId;
   home: PanelHome | null;
}

/** Dockview's serialized layout without popout windows, as {@link withoutPopouts} gives it. A layout with popout groups, as `toJSON` gives it, does not compile as one. */
export type LayoutDockview = Omit<SerializedDockview, 'popoutGroups'> & { popoutGroups?: never };

/**
 * A workspace layout as the page keeps it: Dockview's serialized layout with the hidden panels, which Dockview does not record.
 *
 * The saved layout, the ready-made arrangements and the reviewer's named layouts all take this form, so one check and one load serve them all.
 */
export interface LayoutRecord {
   version: typeof layoutVersion;
   dockview: LayoutDockview;
   hidden: HiddenPanel[];
}

/** Returns the record that the text holds as JSON, or undefined when the text is not JSON or holds no record, as {@link layoutFrom} decides. */
export function parseLayout(text: string): LayoutRecord | undefined {
   let value: unknown;
   try {
      value = JSON.parse(text);
   } catch {
      return undefined;
   }

   return layoutFrom(value);
}

/**
 * Returns the value as a record, or undefined when it is not a record of this version with exactly the workspace's panels.
 *
 * It checks the shape that the workspace relies on. Dockview checks the rest of its own layout as it loads it, and the workspace loads the Default layout when that load fails.
 * A record that holds popout windows comes back with their panels in their place, as {@link withoutPopouts} puts them.
 */
export function layoutFrom(value: unknown): LayoutRecord | undefined {
   if (!isObject(value) || value.version !== layoutVersion || !isObject(value.dockview) || !isObject(value.dockview.grid) || !isObject(value.dockview.panels) || !Array.isArray(value.hidden)) {
      return undefined;
   }

   const panels = Object.keys(value.dockview.panels);
   if (panels.length !== panelIds.length || !panelIds.every((id) => panels.includes(id))) {
      return undefined;
   }

   const hidden = value.hidden as unknown[];
   const ids = hidden.map((panel) => (isObject(panel) ? panel.id : undefined));
   if (!hidden.every(isHiddenPanel) || new Set(ids).size !== ids.length) {
      return undefined;
   }

   try {
      return { ...(value as unknown as LayoutRecord), dockview: withoutPopouts(value.dockview as unknown as SerializedDockview) };
   } catch {
      return undefined;
   }
}

/** The layout of the workspace as a page saved it last, or null when none is saved or the saved one cannot be read. The workspace reads it once, as it mounts. */
export const savedLayout = storedPreference<LayoutRecord | null>('lotsen.workspaceLayout', parseLayout, JSON.stringify, null);

type GridNode = SerializedDockview['grid']['root'];
type GroupState = NonNullable<SerializedPopoutGroup['data']>;

/**
 * Returns Dockview's serialized layout with the panels of each popout window back in the main window, and no popout window.
 *
 * Each popout's panels go where closing its window puts them (disposePopoutWindow in dockview-core 8.9.0's dockviewComponent.ts): to the group that the popout left, which shows
 * again, or to a group of their own at the start of the grid when that group is gone. A panel goes back to its tab's place in that group when `places` holds it, as
 * `WorkspaceLayout` moves it once its window closes, and to the group's end otherwise. A layout record holds no popout window, because the browser blocks a window that a load
 * opens without the reviewer's click.
 *
 * @param places The group that each popped-out panel shared and its tab's place there, by the panel's id.
 */
export function withoutPopouts(dockview: SerializedDockview, places: Partial<Record<string, PanelHome>> = {}): LayoutDockview {
   const { popoutGroups, ...layout } = dockview;
   if (popoutGroups === undefined) {
      return layout;
   }

   const grid = structuredClone(layout.grid);
   let activeGroup = layout.activeGroup;
   for (const popout of popoutGroups) {
      const groups = popout.data !== undefined ? [popout.data] : popout.grid !== undefined ? leaves(popout.grid.root) : [];
      const views = groups.flatMap((group) => group.views);
      if (views.length === 0) {
         continue;
      }

      const active = groups.find((group) => group.activeView !== undefined)?.activeView;
      const path = popout.gridReferenceGroup === undefined ? [] : pathTo(grid.root, popout.gridReferenceGroup);
      let home: GroupState;
      if (path.length > 0) {
         home = path.at(-1)!.data as GroupState;
         const joined = [...home.views];
         for (const view of views) {
            const place = places[view];
            joined.splice(place?.groupId === home.id ? Math.min(place.index, joined.length) : joined.length, 0, view);
         }

         home.views = joined;
         // A hidden node of the path is the group that the popout left, or a branch that hid with it.
         path.forEach((node) => delete node.visible);
      } else {
         // Dockview serializes the grid's root as a branch (Gridview.serialize in dockview-core 8.9.0's gridview.ts), and it scales the sizes to the workspace as it loads them.
         home = { views, id: groups[0]!.id };
         const siblings = grid.root.data as GridNode[];
         const size = siblings.reduce((sum, node) => sum + (node.size ?? 0), 0) / Math.max(siblings.length, 1);
         siblings.unshift({ type: 'leaf', data: home, size });
      }

      home.activeView = active ?? home.activeView;
      if (groups.some((group) => group.id === activeGroup)) {
         activeGroup = home.id;
      }
   }

   return { ...layout, grid, activeGroup };
}

function leaves(node: GridNode): GroupState[] {
   return node.type === 'leaf' ? [node.data as GroupState] : (node.data as GridNode[]).flatMap(leaves);
}

// Returns the nodes from the root to the leaf of the group, or an empty path when no leaf holds the group.
function pathTo(node: GridNode, groupId: string): GridNode[] {
   if (node.type === 'leaf') {
      return (node.data as GroupState).id === groupId ? [node] : [];
   }

   for (const child of node.data as GridNode[]) {
      const path = pathTo(child, groupId);
      if (path.length > 0) {
         return [node, ...path];
      }
   }

   return [];
}

function isHiddenPanel(value: unknown): value is HiddenPanel {
   if (!isObject(value) || !(panelIds as readonly unknown[]).includes(value.id)) {
      return false;
   }

   const home = value.home;
   return home === null || (isObject(home) && typeof home.groupId === 'string' && Number.isInteger(home.index) && (home.index as number) >= 0);
}

function isObject(value: unknown): value is Record<string, unknown> {
   return typeof value === 'object' && value !== null && !Array.isArray(value);
}
