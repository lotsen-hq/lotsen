<!--
   An editor of a proposed order of a plan's stops: each stop in the proposed order, with its number, kind, title and chapter. A whole row drags to before or after another row, and buttons
   that show on hover and on keyboard focus move it one place up or down. It holds no order of its own, so its owner keeps the order and decides what to do with it.
-->
<script lang="ts">
   import ArrowDown from '@lucide/svelte/icons/arrow-down';
   import ArrowUp from '@lucide/svelte/icons/arrow-up';
   import GripVertical from '@lucide/svelte/icons/grip-vertical';
   import SkipForward from '@lucide/svelte/icons/skip-forward';
   import { tick, type Snippet } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import { panelDocument } from '../lib/panelDocument.svelte';
   import { droppedStop, movedStop, type DropPlace } from '../lib/stopOrder';
   import type { Chapter, Stop } from '../lib/types';
   import StopKindPill from './StopKindPill.svelte';

   let {
      stops,
      chapters,
      order,
      onchange,
      label = 'Proposed order',
      skipped = new Set(),
      skipToggle,
   }: {
      /** The plan's stops, which the order names by ID. */
      stops: readonly Stop[];
      chapters: readonly Chapter[];
      /** The IDs of the plan's stops in the proposed order, each once. */
      order: readonly string[];
      /** Receives the order after a move. */
      onchange: (order: string[]) => void;
      /** The list's accessible name. */
      label?: string;
      /** The IDs of the stops that the reviewer skipped, which the rows mark. */
      skipped?: ReadonlySet<string>;
      /** The skip toggle of the stop with the ID, at the row's current number, or undefined when the rows offer no skip. */
      skipToggle?: Snippet<[stopId: string, number: number]>;
   } = $props();

   const id = $props.id();
   let announcement = $state('');
   /** The ID of the stop that the current drag holds, or null when no drag of this list is in progress. */
   let held: string | null = null;
   let dimmed = $state<string | null>(null);
   const home = panelDocument();
   let dropLine = $state<{ stopId: string; place: DropPlace } | null>(null);

   const listed = $derived.by(() => {
      const byId = new Map(stops.map((stop) => [stop.id, stop]));
      const titles = new Map(chapters.map((chapter) => [chapter.id, chapter.title]));
      return order.flatMap((stopId) => {
         const stop = byId.get(stopId);
         return stop === undefined ? [] : [{ stop, chapter: titles.get(stop.chapter) ?? stop.chapter }];
      });
   });

   const buttonId = (stopId: string, direction: 'up' | 'down') => `${id}-${stopId}-${direction}`;

   function apply(stopId: string, next: string[]): void {
      onchange(next);
      const title = listed.find((row) => row.stop.id === stopId)!.stop.title;
      announcement = `${title} is now ${next.indexOf(stopId) + 1} of ${next.length}.`;
   }

   // A keyed list moves the row's elements, which can drop the focus, so the focus returns to the moved stop's button, or to its other one at an end of the list.
   async function move(index: number, offset: -1 | 1): Promise<void> {
      const ids = listed.map((row) => row.stop.id);
      const stopId = ids[index]!;
      apply(stopId, movedStop(ids, index, offset));
      await tick();
      const direction = offset < 0 ? 'up' : 'down';
      const same = home.document.getElementById(buttonId(stopId, direction)) as HTMLButtonElement | null;
      const other = home.document.getElementById(buttonId(stopId, direction === 'up' ? 'down' : 'up'));
      (same !== null && !same.disabled ? same : other)?.focus();
   }

   function startDrag(event: DragEvent, stopId: string): void {
      if (event.dataTransfer !== null) {
         event.dataTransfer.setData('application/x-lotsen-stop', stopId);
         event.dataTransfer.effectAllowed = 'move';
      }

      held = stopId;
      // The row dims a frame after the drag starts, so the ghost keeps full strength.
      home.window.requestAnimationFrame(() => (dimmed = held));
   }

   function dragOver(event: DragEvent, stopId: string): void {
      if (held === null) {
         return;
      }

      event.preventDefault();
      if (event.dataTransfer !== null) {
         event.dataTransfer.dropEffect = 'move';
      }

      if (stopId === held) {
         dropLine = null;
         return;
      }

      const row = (event.currentTarget as HTMLElement).getBoundingClientRect();
      const place = event.clientY < row.top + row.height / 2 ? 'before' : 'after';
      if (dropLine?.stopId !== stopId || dropLine.place !== place) {
         dropLine = { stopId, place };
      }
   }

   function endDrag(): void {
      held = null;
      dimmed = null;
      dropLine = null;
   }

   function drop(event: DragEvent): void {
      if (held === null) {
         return;
      }

      event.preventDefault();
      const stopId = held;
      const line = dropLine;
      endDrag();
      if (line !== null) {
         const ids = listed.map((row) => row.stop.id);
         const next = droppedStop(ids, stopId, line.stopId, line.place);
         if (next.some((candidate, index) => candidate !== ids[index])) {
            apply(stopId, next);
         }
      }
   }

   function leaveList(event: DragEvent): void {
      if (!(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null)) {
         dropLine = null;
      }
   }
</script>

<ol aria-label={label} ondragleave={leaveList}>
   {#each listed as { stop, chapter }, index (stop.id)}
      {@const isSkipped = skipped.has(stop.id)}
      <li
         class={['stop-order-row', isSkipped && 'skipped-row', dimmed === stop.id && 'dragged', dropLine?.stopId === stop.id && `drop-${dropLine.place}`]}
         draggable="true"
         ondragstart={(event) => startDrag(event, stop.id)}
         ondragover={(event) => dragOver(event, stop.id)}
         ondrop={drop}
         ondragend={endDrag}
      >
         <GripVertical class="grip" aria-hidden="true" />
         <span class="number">{index + 1}</span>
         <span>
            <StopKindPill kind={stop.kind} /><span class="title">{stop.title}</span>
            {#if isSkipped}
               <span class="status skipped"><SkipForward aria-hidden="true" />Skipped</span>
            {/if}
            <span class="chapter">{chapter}</span>
         </span>
         <span class="flex items-center gap-1">
            {@render skipToggle?.(stop.id, index + 1)}
            <span class="move-buttons flex flex-col">
               <Button id={buttonId(stop.id, 'up')} variant="ghost" size="icon-xs" disabled={index === 0} aria-label="Move “{stop.title}” up" onclick={() => move(index, -1)}
                  ><ArrowUp aria-hidden="true" /></Button
               >
               <Button id={buttonId(stop.id, 'down')} variant="ghost" size="icon-xs" disabled={index === listed.length - 1} aria-label="Move “{stop.title}” down" onclick={() => move(index, 1)}
                  ><ArrowDown aria-hidden="true" /></Button
               >
            </span>
         </span>
      </li>
   {/each}
</ol>
<p class="sr-only" role="status">{announcement}</p>
