import Content from './context-menu-content.svelte';
import Trigger from './context-menu-trigger.svelte';
import Root from './context-menu.svelte';

// The items are those of the dropdown menu, because bits-ui builds both menus on the same item primitives.
export {
   Root,
   Trigger,
   Content,
   //
   Root as ContextMenu,
   Trigger as ContextMenuTrigger,
   Content as ContextMenuContent,
};
