import type { SerializedDockview } from 'dockview';
import { storedPreference } from './preference';
import { panelIds, type PanelId } from './workspacePanels';

/** The version of {@link LayoutRecord}. Change it when the record's shape changes, so a page never applies a record that it cannot read. */
export const layoutVersion = 1;

/** The group that a hidden panel shared, and its tab's place there, to which the panel goes back when it shows. */
export interface PanelHome {
   groupId: string;
   index: number;
}

/** A panel of the workspace that the reviewer hid, with the group it goes back to, or null when it had a group of its own. */
export interface HiddenPanel {
   id: PanelId;
   home: PanelHome | null;
}

/**
 * A workspace layout as the page keeps it: Dockview's serialized layout with the hidden panels, which Dockview does not record.
 *
 * The saved layout, the ready-made arrangements and the reviewer's named layouts all take this form, so one check and one load serve them all.
 */
export interface LayoutRecord {
   version: typeof layoutVersion;
   dockview: SerializedDockview;
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

   return value as unknown as LayoutRecord;
}

/** The layout of the workspace as a page saved it last, or null when none is saved or the saved one cannot be read. The workspace reads it once, as it mounts. */
export const savedLayout = storedPreference<LayoutRecord | null>('lotsen.workspaceLayout', parseLayout, JSON.stringify, null);

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
