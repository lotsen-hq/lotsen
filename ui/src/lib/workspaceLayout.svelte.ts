import type { DockviewApi, DockviewGroupPanel, IDockviewGroupPanel, IDockviewPanel, PopoutWindowFailure } from 'dockview';
import { arrangementRecord, type ArrangementId } from './arrangements';
import { layoutVersion, withoutPopouts, type LayoutRecord, type PanelHome } from './layoutRecord';
import { panelIds, type PanelId } from './workspacePanels';

/**
 * Holds which panels of the workspace are hidden and which group is maximized, for the top bar, the group headers and the workspace to share.
 *
 * Dockview holds the workspace layout, and this class changes it only through Dockview's API. A hidden panel sits alone in a group that Dockview hides, so the panel
 * stays mounted and keeps its state. That group is the panel's own grid group, or a floating group when the panel shared its group with others.
 *
 * It saves the workspace layout as a {@link LayoutRecord} after each change but the page's own, and loads one. A load is the page's own change, so a layout that the workspace
 * falls back to stays unsaved until the reviewer changes it.
 *
 * It also holds which panels show in a popout window. A popout window holds one panel, because Dockview's Pop out takes one and the workspace refuses a drop from another window.
 */
export class WorkspaceLayout {
   /** Whether each panel is hidden from the Panels menu or its group's Hide button. A maximized group hides the others without hiding their panels. */
   hidden = $state<Record<PanelId, boolean>>({ files: false, plan: false, diff: false, stop: false, threads: false });
   /** Whether each panel shows in a popout window. Such a panel shows, so it is not hidden. */
   poppedOut = $state<Record<PanelId, boolean>>({ files: false, plan: false, diff: false, stop: false, threads: false });
   /** Whether each panel's popout window is opening, from the Pop out click until Dockview's open of the window has settled, opened or not. */
   opening = $state<Record<PanelId, boolean>>({ files: false, plan: false, diff: false, stop: false, threads: false });
   /** What the top bar tells the reviewer about the last popout window that did not open, or null when the last one opened or the reviewer dismissed the notice. */
   popoutFailure = $state<string | null>(null);
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
   // The group that each popped-out panel shared and its tab's place there, for the panel's return from its window.
   private returns: Partial<Record<PanelId, PanelHome>> = {};
   // The panels to hide once their popout window has opened or failed to, and has returned them to the main window.
   private closing: Partial<Record<PanelId, true>> = {};
   private popouts: { window: Window; teardown: () => void }[] = [];

   /**
    * Lets this class read and change the workspace layout that Dockview holds.
    *
    * @param save Keeps the workspace layout after each change, other than one that {@link WorkspaceLayout.quietly} makes.
    * @param preparePopout Sets up the page of a popout window that opens, and returns a function that undoes it as the window closes.
    * @returns A function that detaches this class again, for when the workspace unmounts.
    */
   attach(api: DockviewApi, save: (record: LayoutRecord) => void, preparePopout: (popout: Window) => () => void = () => () => {}): () => void {
      this.api = api;
      this.save = save;
      const read = () => (this.maximizedGroup = api.groups.find((group) => group.api.isMaximized())?.id ?? null);
      // Dockview hides a group by giving it no size (ViewItem.setVisible in dockview-core 8.9.0's splitview.ts), so the hidden attribute also takes the group out of
      // the tab order and the accessibility tree.
      const follow = (group: DockviewGroupPanel) => group.api.onDidVisibilityChange((event) => (group.element.hidden = !event.isVisible));
      const locate = () => {
         for (const id of panelIds) {
            this.poppedOut[id] = api.getPanel(id)?.api.location.type === 'popout';
         }
      };
      // Each teardown goes with its window, because a popout's group id can change while it is open, and the removal event reports a closed window as null.
      const release = (popout: Window | null) => {
         this.popouts.filter((entry) => entry.window === popout).forEach((entry) => entry.teardown());
         this.popouts = this.popouts.filter((entry) => entry.window !== popout);
      };
      const subscriptions = [
         api.onDidMaximizedGroupChange(read),
         api.onDidLayoutChange(() => this.quiet === 0 && this.persist()),
         api.onDidAddGroup(follow),
         ...api.groups.map(follow),
         // Dockview fires it for each panel that a popout window takes or returns, also when a load closes the window, once the panel's element is in its new group and document
         // (fireDidMovePanel in dockview-core 8.9.0's dockviewComponent.ts), as popout.spec.ts checks.
         api.onDidMovePanel(locate),
         api.onDidLayoutFromJSON(locate),
         api.onDidAddPopoutGroup(({ window }) => window && this.popouts.push({ window, teardown: preparePopout(window) })),
         api.onWillClosePopoutWindow(({ window }) => release(window)),
         api.onDidOpenPopoutWindowFail((failure) => (this.popoutFailure = popoutFailureNotice(failure))),
         // Dockview fires it while it still returns the window's panels to the main window (disposePopoutWindow in dockview-core 8.9.0's dockviewComponent.ts), so the settling waits.
         api.onDidRemovePopoutGroup(() => queueMicrotask(() => this.api === api && this.settle())),
         // An overlay without a group is at an edge of the main window's layout, the only place where Dockview fires one (wireEdgeDropTarget in dockview-core 8.9.0's dockviewComponent.ts).
         api.onWillShowOverlay((event) => {
            const source = event.getData();
            const from = source === undefined ? undefined : api.getGroup(source.groupId);
            if (from !== undefined && windowOf(from) !== (event.group === undefined ? window : windowOf(event.group))) {
               event.preventDefault();
            }
         }),
      ];
      read();
      locate();
      return () => {
         subscriptions.forEach((subscription) => subscription.dispose());
         this.popouts.forEach((entry) => entry.teardown());
         this.popouts = [];
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

      const dockview = withoutPopouts(this.api.toJSON(), this.returns);
      // The grid holds the maximized group's place too, which SerializedDockview's type omits (Gridview.serialize in dockview-core 8.9.0's gridview.ts).
      delete (dockview.grid as typeof dockview.grid & { maximizedNode?: unknown }).maximizedNode;
      const hidden = panelIds.filter((id) => this.hidden[id]).map((id) => ({ id, home: this.origins[id] ?? null }));
      return { version: layoutVersion, dockview, hidden };
   }

   /**
    * Lays out the workspace as the record says, and hides its hidden panels again, as a change of the page's own that saves nothing.
    *
    * The panels that the workspace holds stay mounted, so each keeps its state. Each popout window closes, and its panel goes where the record says. A window that is still
    * opening closes once it has opened, as {@link WorkspaceLayout.popOut} does.
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
            // A load while every group of a column hides lays the groups out at no width, as popout.spec.ts shows, so the workspace keeps the size that it had.
            const { width, height } = api;
            api.fromJSON(record.dockview, { reuseExistingPanels: true });
            if (width > 0 && height > 0 && (api.width !== width || api.height !== height)) {
               api.layout(width, height, true);
            }
            this.origins = {};
            this.returns = {};
            this.closing = {};
            for (const id of panelIds) {
               this.hidden[id] = false;
            }

            for (const { id, home } of record.hidden) {
               const panel = api.getPanel(id);
               if (panel === undefined) {
                  continue;
               }

               if (home !== null) {
                  this.origins[id] = home;
               }

               this.conceal(id, panel);
               this.hidden[id] = true;
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

   /** Lays out the workspace in the arrangement, as {@link WorkspaceLayout.applyRecord} does. */
   apply(id: ArrangementId): void {
      this.applyRecord(arrangementRecord(id, this.titles));
   }

   /**
    * Lays out the workspace as the record says, with each panel keeping its state, and saves it as the current workspace layout.
    *
    * @returns Whether Dockview loaded the record. When it did not, the workspace shows the Default arrangement, which stays unsaved.
    */
   applyRecord(record: LayoutRecord): boolean {
      if (this.load(record)) {
         this.persist();
         return true;
      }

      this.load(arrangementRecord('default', this.titles));
      return false;
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
    * A panel in a popout window hides once its window has closed and returned it to its place in the main window. A panel whose window is opening hides once the window has
    * opened and closed again, or failed to open.
    *
    * @param focusMenu Whether to move focus to the Panels menu, because the button that hid the panel hides with it.
    */
   hide(id: PanelId, focusMenu = false): void {
      const api = this.api;
      const panel = api?.getPanel(id);
      if (!api || !panel || this.hidden[id]) {
         return;
      }

      if (this.poppedOut[id] || this.opening[id]) {
         this.closing[id] = true;
         if (this.poppedOut[id]) {
            windowOf(panel.group).close();
         }

         return;
      }

      this.exitMaximized();
      this.conceal(id, panel);
      this.hidden[id] = true;
      this.persist();
      if (focusMenu) {
         this.menu?.focus();
      }
   }

   /**
    * Shows a hidden panel as the active tab of its group, back in the group it shared when that group still shows, and restores another maximized group first.
    *
    * A panel in a popout window only becomes the active tab there, and the main window stays as it is.
    */
   reveal(id: PanelId): void {
      const panel = this.api?.getPanel(id);
      if (!panel) {
         return;
      }

      if (this.poppedOut[id]) {
         panel.api.setActive();
         return;
      }

      if (this.api?.hasMaximizedGroup() && !panel.api.isMaximized()) {
         this.api.exitMaximizedGroup();
      }

      this.show(id);
      panel.api.setActive();
   }

   /** Maximizes the panel's group, or restores it when it is the maximized one. A panel in a popout window fills its window already, so this leaves it as it is. */
   toggleMaximized(id: PanelId): void {
      const panel = this.api?.getPanel(id);
      if (!panel || this.poppedOut[id]) {
         return;
      }

      if (panel.api.isMaximized()) {
         panel.api.exitMaximized();
      } else {
         panel.api.maximize();
      }
   }

   /**
    * Moves the panel into a popout window of its own and restores a maximized group first. Closing the window returns the panel to its group.
    *
    * Call it from the reviewer's click, because the browser blocks a window that opens without one. When the window does not open, the panel stays in its group, and the group
    * that was maximized is maximized again. When a layout loads while the window opens, the window closes again once it has opened.
    */
   async popOut(id: PanelId): Promise<void> {
      const api = this.api;
      const panel = api?.getPanel(id);
      if (!api || !panel || this.poppedOut[id] || this.opening[id]) {
         return;
      }

      this.popoutFailure = null;
      const maximized = api.groups.find((group) => group.api.isMaximized());
      this.exitMaximized();
      this.opening[id] = true;
      const loads = this.loads;
      const before = api.getPopouts().map((popout) => popout.window);
      if (panel.group.panels.length > 1) {
         this.returns[id] = { groupId: panel.group.id, index: panel.group.panels.indexOf(panel) };
      }

      const opened = await api.addPopoutGroup(panel).finally(() => (this.opening[id] = false));
      if (this.api !== api) {
         return;
      }

      if (this.loads !== loads) {
         // Dockview opens the window even after a load that came during the open, with the panel or with a group that the load removed. Closing it returns the panel to where
         // the load put it, as panelLayout.test.ts checks.
         delete this.closing[id];
         api.getPopouts().forEach(({ window }) => window !== null && !before.includes(window) && window.close());
         return;
      }

      if (!opened) {
         // Dockview moves the panel only once the window has opened, so the panel kept its place.
         delete this.returns[id];
      }

      if (this.closing[id]) {
         delete this.closing[id];
         this.hide(id);
      } else if (!opened && maximized !== undefined && api.groups.includes(maximized) && !api.hasMaximizedGroup()) {
         maximized.api.maximize();
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
      } else if (origin && home && (home.api.isVisible || home.panels.length === 0)) {
         // A hidden group without panels is the place of a popout window's panel, which comes back to it beside this one.
         panel.api.moveTo({ group: home, index: Math.min(origin.index, home.panels.length) });
         home.api.setVisible(true);
      } else {
         // The group it shared is gone or hidden, so the panel takes a group of its own beside it, or at the right edge.
         panel.api.moveTo({ group: api.addGroup(home ? { referenceGroup: home, direction: 'right' } : { direction: 'right' }) });
      }

      this.hidden[id] = false;
      this.persist();
   }

   // Puts a panel alone in a group and hides that group. A floating group sits over the grid, so a panel that shares its group leaves without a split that would resize the groups
   // around it, and the group it leaves becomes its home.
   private conceal(id: PanelId, panel: IDockviewPanel): void {
      const group = panel.group;
      if (group.panels.length > 1) {
         this.origins[id] = { groupId: group.id, index: group.panels.indexOf(panel) };
         this.api?.addFloatingGroup(panel);
      }

      panel.group.api.setVisible(false);
   }

   // Hides each panel that a closed popout window returned to the main window for a hide, and again each hidden panel that the window's panel joined in its group.
   private settle(): void {
      const api = this.api;
      if (!api) {
         return;
      }

      for (const id of panelIds) {
         const panel = api.getPanel(id);
         if (panel === undefined || panel.api.location.type === 'popout') {
            continue;
         }

         this.returnHome(id);
         if (this.closing[id]) {
            delete this.closing[id];
            this.hide(id);
         } else if (this.hidden[id] && (panel.group.panels.length > 1 || panel.group.api.isVisible)) {
            this.conceal(id, panel);
            this.persist();
         }
      }
   }

   // Moves a panel that left a shared group for a popout window back to its tab's place there, because Dockview returns it as the group's last tab
   // (moveGroupWithoutDestroying in dockview-core 8.9.0's dockviewComponent.ts).
   private returnHome(id: PanelId): void {
      const home = this.returns[id];
      const panel = this.api?.getPanel(id);
      if (home === undefined || panel === undefined || panel.api.location.type === 'popout') {
         return;
      }

      delete this.returns[id];
      const group = panel.group;
      const index = Math.min(home.index, group.panels.length - 1);
      if (group.id === home.groupId && group.panels.indexOf(panel) !== index) {
         panel.api.moveTo({ group, index });
      }
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

// The window that shows the group: its popout window's, or the main page's.
function windowOf(group: IDockviewGroupPanel): Window {
   return group.api.location.type === 'popout' ? group.api.location.getWindow() : window;
}

function panelOf(panel: IDockviewPanel | undefined): PanelId | null {
   return panel !== undefined && (panelIds as readonly string[]).includes(panel.id) ? (panel.id as PanelId) : null;
}

/** Returns what the top bar tells the reviewer about a popout window that did not open, or null when the window opened and closed before it loaded. */
export function popoutFailureNotice(failure: PopoutWindowFailure): string | null {
   switch (failure.reason) {
      case 'blocked':
         return 'The browser blocked the popout window. Allow pop-ups for this page, then pop out the panel again.';
      case 'closed':
         return null;
      default:
         return 'The popout window did not open. The browser console says why.';
   }
}
