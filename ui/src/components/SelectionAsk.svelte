<!--
   The ask that a selection of code or of a stop's prose offers: a button below the selection's end that hands what it covers to the Threads panel, or a note on why it offers none.
   It sits in the positioned element around the selectable content, after that content, so the keyboard reaches it next.
-->
<script lang="ts">
   import BookOpen from '@lucide/svelte/icons/book-open';
   import MessageSquarePlus from '@lucide/svelte/icons/message-square-plus';
   import { untrack } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import { isElement, isHTMLElement } from '../lib/dom';
   import { panelDocument } from '../lib/panelDocument.svelte';
   import type { PickedAnchor, PickHandler } from '../lib/viewState';
   import type { SelectionExplain, SelectionTarget } from '../lib/selectionAsk';

   let {
      within,
      targetOf,
      onask,
      explain = null,
   }: {
      /** The element whose selections count, or undefined before it mounts. */
      within: HTMLElement | undefined;
      /** Returns what a selection that touches `within` asks about, or null when it offers nothing. */
      targetOf: (range: Range) => SelectionTarget | null;
      onask: PickHandler;
      /** The Explain that a selection of a stop's text offers beside the ask, or null for none. */
      explain?: SelectionExplain | null;
   } = $props();

   /** The space between the selection and the affordance, in pixels. */
   const gap = 4;

   let target = $state.raw<SelectionTarget | null>(null);
   let explainRefusal = $state<string | null>(null);
   let explaining = $state(false);
   let selected: Range | null = null;
   let element = $state<HTMLElement>();
   /** Whether the primary button is down after a press outside the affordance, as in a drag that selects. */
   let selecting = $state(false);
   const home = panelDocument();

   // Each document has its own selection, so the listeners follow the panel into a popout window and back.
   $effect(() => {
      const container = within;
      const read = targetOf;
      const { document, window } = home;
      if (!container) {
         return;
      }

      const update = () => {
         const selection = document.getSelection();
         const range = selection !== null && selection.rangeCount > 0 && !selection.isCollapsed ? selection.getRangeAt(0) : null;
         selected = range !== null && range.intersectsNode(container) ? range : null;
         target = selected === null ? null : read(selected);
         explainRefusal = null;
         place();
      };
      untrack(update);
      document.addEventListener('selectionchange', update);
      // An editor scrolls inside the container, which moves the selection without a selection change.
      window.addEventListener('scroll', place, { capture: true, passive: true });
      return () => {
         document.removeEventListener('selectionchange', update);
         window.removeEventListener('scroll', place, { capture: true });
         target = null;
         selected = null;
      };
   });

   // The affordance sits below the selection's end, on the lines that a drag downward reaches next, so it lets the pointer through to them during the drag.
   $effect(() => {
      const { document } = home;
      const press = (event: MouseEvent) => {
         selecting = event.button === 0 && !(isElement(event.target) && element?.contains(event.target));
      };
      const release = () => {
         selecting = false;
      };
      document.addEventListener('mousedown', press, true);
      document.addEventListener('mouseup', release, true);
      return () => {
         document.removeEventListener('mousedown', press, true);
         document.removeEventListener('mouseup', release, true);
         selecting = false;
      };
   });

   $effect(() => {
      if (element && target !== null) {
         place();
      }
   });

   async function sendExplain(anchor: PickedAnchor): Promise<void> {
      if (explain === null || explaining || anchor.kind !== 'stopText') {
         return;
      }

      const range = selected;
      explaining = true;
      const refusal = await explain.send(anchor);
      explaining = false;
      const selection = home.document.getSelection();
      if (range === null || selection === null || selection.rangeCount === 0 || selection.getRangeAt(0) !== range) {
         return;
      }

      explainRefusal = refusal;
      // A stored action ends the selection, so the affordance stays closed and a second click cannot store the action twice.
      if (refusal === null) {
         selection.removeAllRanges();
         target = null;
      }
   }

   /** Moves the affordance below the end of the selection, or above its start where the element it is positioned in ends first. jsdom has no layout, so it stays at the start there. */
   function place(): void {
      const parent = element?.offsetParent;
      const rects = selected?.getClientRects?.();
      if (!element || !isHTMLElement(parent) || !rects || rects.length === 0) {
         return;
      }

      const box = parent.getBoundingClientRect();
      const end = rects[rects.length - 1]!;
      const below = end.bottom - box.top + gap;
      const top = below + element.offsetHeight <= parent.clientHeight ? below : rects[0]!.top - box.top - gap - element.offsetHeight;
      const left = Math.min(Math.max(end.left - box.left, 0), Math.max(parent.clientWidth - element.offsetWidth, 0));
      element.style.top = `${Math.max(top, 0) + parent.scrollTop}px`;
      element.style.left = `${left + parent.scrollLeft}px`;
   }
</script>

{#if target !== null}
   <div bind:this={element} class={['absolute top-0 left-0 z-20', selecting && 'pointer-events-none']}>
      {#if 'refusal' in target}
         <p class="rounded-md border bg-popover px-2 py-1 text-xs text-popover-foreground shadow-md">{target.refusal}</p>
      {:else}
         {@const picked = target}
         <!-- A mouse press on the button would move the selection, so it keeps the selection and the click still asks. -->
         <div class="flex flex-wrap items-center gap-1">
            <Button
               size="sm"
               variant="secondary"
               class="shadow-md"
               aria-label="Ask Claude about {picked.label}"
               onmousedown={(event: MouseEvent) => event.preventDefault()}
               onclick={() => onask(picked.anchor, picked.quote)}
            >
               <MessageSquarePlus aria-hidden="true" />
               Ask Claude
            </Button>
            {#if explain !== null && picked.anchor.kind === 'stopText'}
               <Button
                  size="sm"
                  variant="secondary"
                  class="shadow-md"
                  aria-label="Explain {picked.label}"
                  disabled={explain.unavailable !== null}
                  aria-disabled={explaining}
                  onmousedown={(event: MouseEvent) => event.preventDefault()}
                  onclick={() => sendExplain(picked.anchor)}
               >
                  <BookOpen aria-hidden="true" />
                  Explain
               </Button>
            {/if}
         </div>
         {#if explain?.unavailable != null && picked.anchor.kind === 'stopText'}
            <p class="mt-1 rounded-md border bg-popover px-2 py-1 text-xs text-popover-foreground shadow-md">{explain.unavailable}</p>
         {:else if explainRefusal !== null}
            <p class="mt-1 rounded-md border bg-popover px-2 py-1 text-xs text-destructive shadow-md" role="alert">{explainRefusal}</p>
         {/if}
      {/if}
   </div>
{/if}
