<script lang="ts">
   import type { SideBySidePanes } from '../lib/editor/surface';
   import { paneSplitBounds, type PaneSplit } from '../lib/paneSplit.svelte';

   let { split, panes }: { split: PaneSplit; panes: SideBySidePanes } = $props();

   let dragging = false;

   // The merge view's theme gives each pane a flex basis of 0, so the grow factors divide the row's width. The policy allows styles set through the CSSOM.
   $effect(() => {
      panes.base.style.flexGrow = String(split.percent);
      panes.head.style.flexGrow = String(100 - split.percent);
   });

   function start(event: PointerEvent) {
      if (event.button !== 0) {
         return;
      }

      // The default action would select the text that the pointer crosses.
      event.preventDefault();
      (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
      dragging = true;
   }

   function move(event: PointerEvent) {
      if (!dragging) {
         return;
      }

      const { left, width } = panes.row.getBoundingClientRect();
      if (width > 0) {
         split.set(((event.clientX - left) / width) * 100);
      }
   }

   function keydown(event: KeyboardEvent) {
      if (split.key(event.key, event.shiftKey)) {
         event.preventDefault();
      }
   }
</script>

<!--
   The classes match the workspace's column splitters, with a hit area wider than the line.
   A focusable separator is a widget role (https://www.w3.org/TR/wai-aria-1.2/#widget_roles), which Svelte's checks count as non-interactive.
-->
<!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
<div
   role="separator"
   tabindex="0"
   aria-label="Resize the base and head panes"
   aria-orientation="vertical"
   aria-valuenow={Math.round(split.percent)}
   aria-valuemin={paneSplitBounds.min}
   aria-valuemax={paneSplitBounds.max}
   class="relative z-10 w-px shrink-0 cursor-col-resize touch-none bg-border after:absolute after:inset-y-0 after:left-1/2 after:w-1 after:-translate-x-1/2 focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden"
   onpointerdown={start}
   onpointermove={move}
   onpointerup={() => (dragging = false)}
   onlostpointercapture={() => (dragging = false)}
   ondblclick={() => split.reset()}
   onkeydown={keydown}
></div>
