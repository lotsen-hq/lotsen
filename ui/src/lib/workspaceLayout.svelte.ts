import type { PaneGroupStorage } from 'paneforge';

/** A panel of the workspace: the tabs of the left column, the diff in the center, or the stop details or the threads in the right column. */
export type PanelId = 'tour' | 'diff' | 'stop' | 'threads';

/** A side column of the workspace. */
export type SideColumn = 'left' | 'right';

/** A pane that folds or collapses: a side column, or a panel of the right column. */
export type FoldableId = SideColumn | 'stop' | 'threads';

/** The part of a paneforge pane that folds and unfolds it. */
export interface FoldablePane {
   collapse(): void;
   expand(): void;
}

/**
 * Holds which panes of the workspace are folded and which panel is maximized, for the top bar and the workspace to share.
 *
 * The workspace reports each pane's folded state through {@link WorkspaceLayout.setFolded}, because a splitter folds a pane too. Paneforge keeps the sizes and the folded state in the browser,
 * so they survive a reload. The maximized panel lasts only until the page loads again.
 */
export class WorkspaceLayout {
   folded = $state<Record<FoldableId, boolean>>({ left: false, right: false, stop: false, threads: false });
   /** The panel that fills the workspace, or null when every panel has its own place. */
   maximized = $state<PanelId | null>(null);

   private readonly panes: Partial<Record<FoldableId, FoldablePane>> = {};
   private readonly toggles: Partial<Record<SideColumn, HTMLElement>> = {};

   /**
    * Lets {@link WorkspaceLayout.toggleFolded} fold and unfold a pane.
    *
    * @returns A function that removes the pane again, for when it unmounts.
    */
   register(id: FoldableId, pane: FoldablePane): () => void {
      this.panes[id] = pane;
      return () => {
         if (this.panes[id] === pane) {
            delete this.panes[id];
         }
      };
   }

   /**
    * Lets {@link WorkspaceLayout.close} move focus to the control that shows the column again.
    *
    * @returns A function that removes the control again, for when it unmounts.
    */
   registerToggle(column: SideColumn, toggle: HTMLElement): () => void {
      this.toggles[column] = toggle;
      return () => {
         if (this.toggles[column] === toggle) {
            delete this.toggles[column];
         }
      };
   }

   /** Folds the side column and moves focus to its registered toggle, because the button that closed the column folds with it. */
   close(column: SideColumn): void {
      this.panes[column]?.collapse();
      this.toggles[column]?.focus();
   }

   setFolded(id: FoldableId, folded: boolean): void {
      this.folded[id] = folded;
   }

   /** Folds the pane, or unfolds it when it is folded, and restores a maximized panel first, so the change shows. */
   toggleFolded(id: FoldableId): void {
      this.maximized = null;
      const pane = this.panes[id];
      if (this.folded[id]) {
         pane?.expand();
      } else {
         pane?.collapse();
      }
   }

   /** Shows a panel of the right column: unfolds the column and the panel, and restores another maximized panel first. */
   reveal(id: 'stop' | 'threads'): void {
      if (this.maximized !== null && this.maximized !== id) {
         this.maximized = null;
      }

      for (const pane of ['right', id] as const) {
         if (this.folded[pane]) {
            this.panes[pane]?.expand();
         }
      }
   }

   /** Maximizes the panel, or restores it when it is the maximized one. */
   toggleMaximized(id: PanelId): void {
      this.maximized = this.maximized === id ? null : id;
   }

   /** Whether the panel is visible: no panel is maximized, or this one is. */
   shows(id: PanelId): boolean {
      return this.maximized === null || this.maximized === id;
   }
}

// Browser storage can be missing or throw on access, as in a private window, so both calls catch its errors without a report.

/** The browser storage that keeps the splitter positions, when it allows it. Without it, the panes start at their default sizes. */
export const layoutStorage: PaneGroupStorage = {
   getItem(name) {
      try {
         return localStorage.getItem(name);
      } catch {
         return null;
      }
   },
   setItem(name, value) {
      try {
         localStorage.setItem(name, value);
      } catch {
         // The layout then lasts only until the page loads again.
      }
   },
};
