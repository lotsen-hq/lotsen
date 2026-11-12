import type { DockviewApi, DockviewGroupPanel, IDockviewPanel } from 'dockview';
import { arrangementRecord, type ArrangementId } from './arrangements';
import { layoutVersion, type LayoutRecord, type PanelHome } from './layoutRecord';
import { panelIds, type PanelId } from './workspacePanels';

/**
 * Holds which panels of the workspace are hidden and which group is maximized, for the top bar, the group headers and the workspace to share.
 *
 * Dockview holds the workspace layout, and this class changes it only through Dockview's API. A hidden panel sits alone in a group that Dockview hides, so the panel
 * stays mounted and keeps its state. That group is the panel's own grid group, or a floating group when the panel shared its group with others.
 *
 * It saves the workspace layout as a {@link LayoutRecord} after each change but the page's own, and loads one. A load is the page's own change, so a layout that the workspace
 * falls back to stays unsaved until the reviewer changes it.
 */
export class WorkspaceLayout {
   /** Whether each panel is hidden from the Panels menu or its group's Hide button. A maximized group hides the others without hiding their panels. */
   hidden = $state<Record<PanelId, boolean>>({ files: false, plan: false, diff: false, stop: false, threads: false });
   /** The id of the Dockview group that fills the workspace, or null when every group has its own place. */
   maximizedGroup = $state<string | null>(null);
   /** The number of layouts that {@link WorkspaceLayout.load} loaded, which changes with each load. */
   loads = $state(0);
   /** Each panel's title, which its tab shows. */
   titles = $state<Record<PanelId, string>>({ files: 'Files', plan: 'Plan', diff: 'Code', stop: 'Stop', threads: 'Threads' });

   private api: DockviewApi | null = null;
   private menu: HTMLElement | null = null;
   private save: ((record: LayoutRecord) => void) | null = null;
   // The number of the page's own changes whose report from Dockview has not arrived yet, as quietly says.
   private quiet = 0;
   private origins: Partial<Record<PanelId, PanelHome>> = {};

   /**
    * Lets this class read and change the workspace layout that Dockview holds.
    *
    * @param save Keeps the workspace layout after each change, other than one that {@link WorkspaceLayout.quietly} makes.
    * @returns A function that detaches this class again, for when the workspace unmounts.
    */
   attach(api: DockviewApi, save: (record: LayoutRecord) => void): () => void {
      this.api = api;
      this.save = save;
      const read = () => (this.maximizedGroup = api.groups.find((group) => group.api.isMaximized())?.id ?? null);
      // Dockview hides a group by giving it no size (ViewItem.setVisible in dockview-core 8.9.0's splitview.ts), so the hidden attribute also takes the group out of
      // the tab order and the accessibility tree.
      const follow = (group: DockviewGroupPanel) => group.api.onDidVisibilityChange((event) => (group.element.hidden = !event.isVisible));
      const subscriptions = [api.onDidMaximizedGroupChange(read), api.onDidLayoutChange(() => this.quiet === 0 && this.persist()), api.onDidAddGroup(follow), ...api.groups.map(follow)];
      read();
      return () => {
         subscriptions.forEach((subscription) => subscription.dispose());
         if (this.api === api) {
            this.api = null;
            this.save = null;
         }
      };
   }

   /** Returns the workspace layout as a record. It omits which group is maximized, so a load shows every group in its place. */
   snapshot(): LayoutRecord | null {
      if (!this.api) {
         return null;
      }

      const dockview = this.api.toJSON();
      // The grid holds the maximized group's place too, which SerializedDockview's type omits (Gridview.serialize in dockview-core 8.9.0's gridview.ts).
      delete (dockview.grid as typeof dockview.grid & { maximizedNode?: unknown }).maximizedNode;
      const hidden = panelIds.filter((id) => this.hidden[id]).map((id) => ({ id, home: this.origins[id] ?? null }));
      return { version: layoutVersion, dockview, hidden };
   }

   /**
    * Lays out the workspace as the record says, and hides its hidden panels again, as a change of the page's own that saves nothing.
    *
    * The panels that the workspace holds stay mounted, so each keeps its state.
    *
    * @returns Whether Dockview loaded the record. When it did not, the workspace's layout is undefined, and the caller lays out another one.
    */
   load(record: LayoutRecord): boolean {
      const api = this.api;
      if (!api) {
         return false;
      }

      return this.quietly(() => {
         try {
            api.fromJSON(record.dockview, { reuseExistingPanels: true });
            this.origins = {};
            for (const id of panelIds) {
               this.hidden[id] = false;
            }

            for (const { id, home } of record.hidden) {
               api.getPanel(id)?.group.api.setVisible(false);
               this.hidden[id] = true;
               if (home !== null) {
                  this.origins[id] = home;
               }
            }

            // A group that loads hidden fires no change of its visibility.
            for (const group of api.groups) {
               group.element.hidden = !group.api.isVisible;
            }

            this.loads++;
            return true;
         } catch {
            return false;
         }
      });
   }

   /**
    * Makes a change of the page's own to the workspace layout, which saves nothing, such as a load or a tab that the page opens.
    *
    * Dockview reports a layout change in a microtask that its first change queues (AsapEvent in dockview-core 8.9.0's events.ts), so the change stays the page's own until a microtask
    * queued after it.
    */
   quietly<T>(change: () => T): T {
      this.quiet++;
      try {
         return change();
      } finally {
         queueMicrotask(() => this.quiet--);
      }
   }

   /** Lays out the workspace in the arrangement, with every panel shown and each keeping its state, and saves it. */
   apply(id: ArrangementId): void {
      if (this.load(arrangementRecord(id, this.titles))) {
         this.persist();
      }
   }

   /**
    * Lets {@link WorkspaceLayout.hide} move focus to the control that shows the panel again.
    *
    * @returns A function that removes the control again, for when it unmounts.
    */
   registerMenu(menu: HTMLElement): () => void {
      this.menu = menu;
      return () => {
         if (this.menu === menu) {
            this.menu = null;
         }
      };
   }

   /** The name of the panel in a button's label, such as "the stop". */
   label(id: PanelId): string {
      return `the ${this.titles[id].toLowerCase()}`;
   }

   /** Sets the title that the panel's tab shows. */
   setTitle(id: PanelId, title: string): void {
      this.titles[id] = title;
      this.quietly(() => this.api?.getPanel(id)?.api.setTitle(title));
   }

   /** Hides the panel, or shows it when it is hidden. */
   toggleHidden(id: PanelId): void {
      if (this.hidden[id]) {
         this.show(id);
      } else {
         this.hide(id);
      }
   }

   /**
    * Hides the panel and restores a maximized group first, so the change shows.
    *
    * @param focusMenu Whether to move focus to the Panels menu, because the button that hid the panel hides with it.
    */
   hide(id: PanelId, focusMenu = false): void {
      const api = this.api;
      const panel = api?.getPanel(id);
      if (!api || !panel || this.hidden[id]) {
         return;
      }

      this.exitMaximized();
      const group = panel.group;
      if (group.panels.length > 1) {
         // A floating group sits over the grid, so the panel leaves without a split that would resize the groups around it.
         this.origins[id] = { groupId: group.id, index: group.panels.indexOf(panel) };
         api.addFloatingGroup(panel);
      }

      panel.group.api.setVisible(false);
      this.hidden[id] = true;
      this.persist();
      if (focusMenu) {
         this.menu?.focus();
      }
   }

   /** Shows a hidden panel as the active tab of its group, back in the group it shared when that group still shows, and restores another maximized group first. */
   reveal(id: PanelId): void {
      const panel = this.api?.getPanel(id);
      if (!panel) {
         return;
      }

      if (this.api?.hasMaximizedGroup() && !panel.api.isMaximized()) {
         this.api.exitMaximizedGroup();
      }

      this.show(id);
      panel.api.setActive();
   }

   /** Maximizes the panel's group, or restores it when it is the maximized one. */
   toggleMaximized(id: PanelId): void {
      const panel = this.api?.getPanel(id);
      if (!panel) {
         return;
      }

      if (panel.api.isMaximized()) {
         panel.api.exitMaximized();
      } else {
         panel.api.maximize();
      }
   }

   /** Returns the workspace's panel of a Dockview group's active panel, or null when the group shows none. */
   activeIn(group: DockviewGroupPanel): PanelId | null {
      return panelOf(group.activePanel);
   }

   private show(id: PanelId): void {
      const api = this.api;
      const panel = api?.getPanel(id);
      if (!api || !panel || !this.hidden[id]) {
         return;
      }

      this.exitMaximized();
      const origin = this.origins[id];
      delete this.origins[id];
      const home = origin === undefined ? undefined : api.groups.find((group) => group.id === origin.groupId);
      if (panel.group.api.location.type === 'grid') {
         panel.group.api.setVisible(true);
      } else if (origin && home?.api.isVisible) {
         panel.api.moveTo({ group: home, index: Math.min(origin.index, home.panels.length) });
      } else {
         // The group it shared is gone or hidden, so the panel takes a group of its own beside it, or at the right edge.
         panel.api.moveTo({ group: api.addGroup(home ? { referenceGroup: home, direction: 'right' } : { direction: 'right' }) });
      }

      this.hidden[id] = false;
      this.persist();
   }

   private persist(): void {
      const record = this.snapshot();
      if (record !== null) {
         this.save?.(record);
      }
   }

   private exitMaximized(): void {
      if (this.api?.hasMaximizedGroup()) {
         this.api.exitMaximizedGroup();
      }
   }
}

function panelOf(panel: IDockviewPanel | undefined): PanelId | null {
   return panel !== undefined && (panelIds as readonly string[]).includes(panel.id) ? (panel.id as PanelId) : null;
}
