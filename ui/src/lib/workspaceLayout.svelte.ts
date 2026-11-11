import type { DockviewApi, DockviewGroupPanel, IDockviewPanel } from 'dockview';

/** A panel of the workspace. */
export type PanelId = 'files' | 'plan' | 'diff' | 'stop' | 'threads';

/** The workspace's panels, in the order of the Panels menu. */
export const panelIds: readonly PanelId[] = ['files', 'plan', 'diff', 'stop', 'threads'];

/** Where a hidden panel sat, so that showing it puts it back as a tab of the group it shared. */
interface Origin {
   groupId: string;
   index: number;
}

/**
 * Holds which panels of the workspace are hidden and which group is maximized, for the top bar, the group headers and the workspace to share.
 *
 * Dockview holds the workspace layout, and this class changes it only through Dockview's API. A hidden panel sits alone in a group that Dockview hides, so the panel
 * stays mounted and keeps its state. That group is the panel's own grid group, or a floating group when the panel shared its group with others.
 */
export class WorkspaceLayout {
   /** Whether each panel is hidden from the Panels menu or its group's Hide button. A maximized group hides the others without hiding their panels. */
   hidden = $state<Record<PanelId, boolean>>({ files: false, plan: false, diff: false, stop: false, threads: false });
   /** The id of the Dockview group that fills the workspace, or null when every group has its own place. */
   maximizedGroup = $state<string | null>(null);
   /** Each panel's title, which its tab shows. */
   titles = $state<Record<PanelId, string>>({ files: 'Files', plan: 'Plan', diff: 'Code', stop: 'Stop', threads: 'Threads' });

   private api: DockviewApi | null = null;
   private menu: HTMLElement | null = null;
   private readonly origins: Partial<Record<PanelId, Origin>> = {};

   /**
    * Lets this class read and change the workspace layout that Dockview holds.
    *
    * @returns A function that detaches this class again, for when the workspace unmounts.
    */
   attach(api: DockviewApi): () => void {
      this.api = api;
      const read = () => (this.maximizedGroup = api.groups.find((group) => group.api.isMaximized())?.id ?? null);
      const subscription = api.onDidMaximizedGroupChange(read);
      read();
      return () => {
         subscription.dispose();
         if (this.api === api) {
            this.api = null;
         }
      };
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
      this.api?.getPanel(id)?.api.setTitle(title);
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

      this.restore();
      const group = panel.group;
      if (group.panels.length > 1) {
         // A floating group sits over the grid, so the panel leaves without a split that would resize the groups around it.
         this.origins[id] = { groupId: group.id, index: group.panels.indexOf(panel) };
         api.addFloatingGroup(panel);
      }

      panel.group.api.setVisible(false);
      this.hidden[id] = true;
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

      this.restore();
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
   }

   private restore(): void {
      if (this.api?.hasMaximizedGroup()) {
         this.api.exitMaximizedGroup();
      }
   }
}

function panelOf(panel: IDockviewPanel | undefined): PanelId | null {
   return panel !== undefined && (panelIds as readonly string[]).includes(panel.id) ? (panel.id as PanelId) : null;
}
