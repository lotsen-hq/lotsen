import { tick } from 'svelte';
import { fileTreeKeyAction, type TreeNode, type TreeRow } from './fileTree';

/** What a tree does with a node when a key press or a click asks for it. */
export interface TreeHandlers<N extends TreeNode> {
   /** Selects a file or toggles a folder. */
   activate(node: N): void;
   expand(node: N): void;
   collapse(node: N): void;
   /** Toggles a file's review mark, in a tree whose files are checkable. A tree without it ignores the checkbox column. */
   check?(node: N): void;
}

/** Provides the focus of a tree view and its key and click handling, with one tab stop, as the WAI-ARIA tree view pattern describes. */
export class TreeFocus {
   /** The ID of the row that last had the focus, or null before any row had it. */
   focusedId = $state<string | null>(null);

   /** Returns the ID of the row that takes the tab stop: the focused row, then the selected file, then the first row, each when the tree shows it, or null when it shows no row. */
   tabStop<N extends TreeNode>(rows: readonly TreeRow<N>[], selectedId: string | null): string | null {
      const shown = (id: string | null) => rows.find((row) => row.node.id === id)?.node;
      if (shown(this.focusedId)) {
         return this.focusedId;
      }

      return shown(selectedId)?.type === 'file' ? selectedId : (rows[0]?.node.id ?? null);
   }

   /** Moves the focus to a row of the tree element, once the tree has rendered it. */
   async focus(tree: HTMLElement, id: string): Promise<void> {
      this.focusedId = id;
      await tick();
      // Node IDs are paths, which a CSS attribute selector would have to escape.
      [...tree.querySelectorAll<HTMLElement>('[role="treeitem"]')].find((row) => row.dataset.id === id)?.focus();
   }

   /** Handles a key press on the tree, from the row that holds the tab stop. */
   keydown<N extends TreeNode>(event: KeyboardEvent, rows: readonly TreeRow<N>[], tabStop: string | null, handlers: TreeHandlers<N>): void {
      const action = tabStop === null ? null : fileTreeKeyAction(rows, tabStop, event.key, { checkable: handlers.check !== undefined });
      if (action === null) {
         return;
      }

      event.preventDefault();
      const node = rows.find((row) => row.node.id === action.id)!.node;
      if (action.type === 'focus') {
         void this.focus(event.currentTarget as HTMLElement, action.id);
      } else if (action.type === 'expand') {
         handlers.expand(node);
      } else if (action.type === 'collapse') {
         handlers.collapse(node);
      } else if (action.type === 'check') {
         handlers.check?.(node);
      } else {
         handlers.activate(node);
      }
   }

   /** Keeps the focused row when the focus moves into the tree by a click or the Tab key. */
   focusin(event: FocusEvent): void {
      const id = (event.target as HTMLElement).dataset.id;
      if (id !== undefined) {
         this.focusedId = id;
      }
   }

   /** Focuses the row that a click lands on, and checks its file when the click lands on the row's checkbox, or else activates the row. */
   click<N extends TreeNode>(event: MouseEvent, rows: readonly TreeRow<N>[], activate: (node: N) => void, check?: (node: N) => void): void {
      // A group's indent belongs to the parent treeitem, so only a click on a row counts.
      const target = event.target as Element;
      const row = target.closest('.row');
      const id = row?.closest<HTMLElement>('[role="treeitem"]')?.dataset.id;
      const node = id === undefined ? undefined : rows.find((candidate) => candidate.node.id === id)?.node;
      if (node) {
         void this.focus(event.currentTarget as HTMLElement, node.id);
         if (check !== undefined && node.type === 'file' && row!.contains(target.closest('.check'))) {
            check(node);
         } else {
            activate(node);
         }
      }
   }
}
