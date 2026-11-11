<!--
   The Threads panel of one tour. It shows the thread list, or in its place the thread view of the thread that the reviewer opened. The thread list holds the filters and the tour's threads
   that the filters show, newest first, in a box that scrolls by itself, with the bar of the chosen notes and the ask box below it at the bottom of the panel. The filters scroll with the
   threads, so a short panel still fits the ask box. The thread view brings its own follow-up box.
-->
<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import { tick, untrack } from 'svelte';
   import { disabledReason, threadOffers } from '../lib/controls';
   import type { LinkHover } from '../lib/linkHover';
   import type { MessageStyle } from '../lib/messageStyle';
   import { panelDocument } from '../lib/panelDocument.svelte';
   import type { PinCommits } from '../lib/pin';
   import { PendingWrite } from '../lib/pendingWrite.svelte';
   import { getThreadActions } from '../lib/threadActions.svelte';
   import type { ThreadsView } from '../lib/threadsView.svelte';
   import { isNote } from '../lib/threadView';
   import type { CodeReference, Plan, QuestionThread, Stop, ThreadAnchor, TourBinding, TourControls } from '../lib/types';
   import AskBox from './AskBox.svelte';
   import OpenThread from './OpenThread.svelte';
   import ThreadFilterMenus from './ThreadFilterMenus.svelte';
   import ThreadRow from './ThreadRow.svelte';

   let {
      view,
      binding,
      controls,
      plan,
      stop,
      focusedPassage = null,
      onfollowreference,
      hover = null,
      opened = null,
      onopenanchor = () => {},
      commits = null,
      messageStyle = 'bubbles',
   }: {
      /** The tour's threads view, which holds the tour's threads, the filter, the thread whose thread view shows and the pick that waits in the box. */
      view: ThreadsView;
      /** The session bound to the tour, or null when none is, whose attachment level the ask box names. */
      binding: TourBinding | null;
      /** The tour's controls, which say what the reviewer can do now and which threads wait, or null while the page has none. */
      controls: TourControls | null;
      /** The tour's plan, which names the stops of the anchors, or null while the tour has none. */
      plan: Plan | null;
      /** The open stop, or null when none is open. */
      stop: Stop | null;
      /** The focused passage, whose `ref:` links the answers highlight, or null for none. */
      focusedPassage?: CodeReference | null;
      /** Follows a `ref:` link in an answer to its code reference. */
      onfollowreference: (reference: CodeReference) => void;
      /** What the `ref:` links in the replies report hovering to, the page's one {@link LinkHover}, or null for nothing. */
      hover?: LinkHover | null;
      /** The thread that the reviewer opened last from its marker or a link, whose thread view the panel focuses, or null for none. Each new object does it again. */
      opened?: { id: string } | null;
      /** Opens where a thread was asked. */
      onopenanchor?: (anchor: ThreadAnchor) => void;
      /** The commits of the tour's pin, against which each thread tells whether its anchor stays on an earlier commit, or null while the tour is not pinned. */
      commits?: PinCommits | null;
      /** How the thread view lays out the messages. */
      messageStyle?: MessageStyle;
   } = $props();

   const id = $props.id();
   const actions = getThreadActions();
   const batch = new PendingWrite();

   let chosen = $state<readonly string[]>([]);
   let box = $state<AskBox>();
   let panel = $state<HTMLElement>();
   const home = panelDocument();
   let status = $state<HTMLElement>();
   // Where the reviewer last scrolled the thread list to, which it takes again when it shows after a thread view, whatever opened that view.
   let listScroll = 0;
   let listScroller: HTMLElement | undefined;
   // The row times count days from the time that the thread list showed last.
   let now = $state(new Date());

   const threads = $derived(view.threads);
   const shownThread = $derived(view.opened);
   const newestFirst = $derived([...view.shown].reverse());
   // A note that another view sent or discarded leaves the choice, in the order the reviewer started the threads.
   const chosenThreads = $derived(threads.filter(isNote).filter((note) => chosen.includes(note.id)));
   const chosenNotes = $derived(chosenThreads.map((note) => note.id));
   const batchUnavailable = $derived(
      chosenThreads.map((note) => disabledReason(threadOffers(controls, note.id)?.sendNote, view.drafts.unsavedEditReason(note))).find((reason) => reason !== null) ?? null,
   );

   $effect(() => {
      const wanted = opened;
      if (wanted === null) {
         return;
      }

      void tick().then(() => home.window.requestAnimationFrame(() => focusThread(wanted.id)?.scrollIntoView({ block: 'nearest' })));
   });

   // Chromium fires a scroll event at the next frame, which may come after a thread view replaced the list, and then delivers it to the removed list, whose `scrollTop` reads 0.
   // So the panel reads the position before the list leaves the page, and ignores a scroll event on a removed list. The layout test of a thread view that opened before the
   // scroll event fired pins both.
   $effect.pre(() => {
      if (shownThread !== null && listScroller?.isConnected === true && listScroller.closest('[hidden]') === null) {
         listScroll = listScroller.scrollTop;
      }
   });

   function keepListScroll(scroller: HTMLElement): void {
      if (scroller.isConnected) {
         listScroll = scroller.scrollTop;
      }
   }

   /**
    * Focuses the thread view of the thread when it shows, else the thread's row in the thread list, and returns the element it focused, or undefined when neither shows.
    *
    * @param keepScroll Whether the focus leaves the list's scroll position as it is, as after the back arrow restored it.
    */
   function focusThread(threadId: string, keepScroll = false): HTMLElement | undefined {
      const shown = panel?.querySelector<HTMLElement>(`article[data-thread-id="${CSS.escape(threadId)}"], li[data-thread-id="${CSS.escape(threadId)}"] button`) ?? undefined;
      shown?.focus({ preventScroll: keepScroll });
      return shown;
   }

   // The control that wrote in a thread may go with the response, as Send in the follow-up box does while the question waits, and a discard removes the thread view. So once the response
   // shows, the panel moves a focus that the page lost to the thread's view or row, found by its ID, else to the list's status, else to the ask box. A focus that the reviewer moved to another
   // element stays there.
   async function focusAfterWrite(threadId: string): Promise<void> {
      await tick();
      const focused = home.document.activeElement;
      if (focused !== null && focused !== home.document.body && focused.isConnected) {
         return;
      }

      if (focusThread(threadId) === undefined) {
         (status ?? box)?.focus();
      }
   }

   async function open(threadId: string): Promise<void> {
      view.open(threadId);
      await tick();
      focusThread(threadId);
   }

   async function back(threadId: string): Promise<void> {
      view.close();
      await tick();
      if (focusThread(threadId, true) === undefined) {
         status?.focus({ preventScroll: true });
      }
   }

   // The list mounts anew each time it shows, so it takes the scroll position that it had before the thread view opened, and its rows count days from then. A folded panel is hidden,
   // and a hidden box ignores a scroll position set on it, so a list that mounts there takes its position once the panel shows.
   function restoreScroll(scroller: HTMLElement): (() => void) | void {
      listScroller = scroller;
      now = new Date();
      if (scroller.closest('[hidden]') === null) {
         scroller.scrollTop = listScroll;
         return;
      }

      // Untracked, so a move of the panel into a popout window or back does not set the list's scroll position again.
      const shown = new (untrack(() => home.window).ResizeObserver)(() => {
         if (scroller.closest('[hidden]') === null) {
            shown.disconnect();
            scroller.scrollTop = listScroll;
         }
      });
      shown.observe(scroller);
      return () => shown.disconnect();
   }

   // A note's edit lives in its thread view, so Edit in a row's menu opens the view with the edit started.
   async function editNote(thread: QuestionThread): Promise<void> {
      if (!isNote(thread)) {
         return;
      }

      view.drafts.startNoteEdit(thread);
      view.open(thread.id);
      await tick();
      panel?.querySelector<HTMLElement>(`article[data-thread-id="${CSS.escape(thread.id)}"] textarea`)?.focus();
   }

   function choose(threadId: string, on: boolean): void {
      chosen = on ? [...chosen, threadId] : chosen.filter((candidate) => candidate !== threadId);
   }

   async function sendChosen(): Promise<void> {
      if (batchUnavailable !== null || chosenNotes.length === 0) {
         return;
      }

      const notes = chosenNotes;
      const result = await batch.run(() => actions.sendNotes(notes));
      if (result?.ok === true) {
         chosen = chosen.filter((threadId) => !notes.includes(threadId));
      }
   }
</script>

<div bind:this={panel} class="flex h-full min-h-0 flex-col">
   {#if shownThread !== null}
      {#key shownThread.id}
         <OpenThread
            thread={shownThread}
            {controls}
            {plan}
            {focusedPassage}
            {hover}
            {messageStyle}
            drafts={view.drafts}
            viewedStopId={stop?.id ?? null}
            chosen={chosen.includes(shownThread.id)}
            onchoose={(on) => choose(shownThread.id, on)}
            onwritten={focusAfterWrite}
            onback={() => back(shownThread.id)}
            {onfollowreference}
            {onopenanchor}
            {commits}
         />
      {/key}
   {:else}
      <div class="scroll-box min-h-0 flex-1" onscroll={(event) => keepListScroll(event.currentTarget)} {@attach restoreScroll}>
         {#if threads.length === 0}
            <p class="px-4 py-3 text-sm text-muted-foreground">No questions yet.</p>
         {:else}
            <div class="flex flex-col gap-2 border-b px-4 py-2" role="group" aria-label="Filter threads">
               <div class="flex flex-wrap items-center gap-2">
                  <ThreadFilterMenus {view} {plan} />
               </div>
               <p bind:this={status} class="text-xs text-muted-foreground focus-visible:outline-hidden" tabindex="-1">
                  {view.missing === 'stop'
                     ? 'There is no current stop. Open a stop to see its threads.'
                     : view.missing === 'file'
                       ? 'There is no current file. Open a file to see its threads.'
                       : view.counts.shown === 0
                         ? 'No threads match the filters.'
                         : `Showing ${view.counts.shown} of ${view.counts.total} threads.`}
               </p>
            </div>
            {#if newestFirst.length > 0}
               <ul aria-label="Threads">
                  {#each newestFirst as thread (thread.id)}
                     <ThreadRow
                        {thread}
                        {controls}
                        {plan}
                        {commits}
                        writes={view.drafts.writesOf(thread.id)}
                        unsavedEdit={isNote(thread) ? view.drafts.unsavedEditReason(thread) : null}
                        {now}
                        newReply={view.isNew(thread)}
                        chosen={chosen.includes(thread.id)}
                        onchoose={(on) => choose(thread.id, on)}
                        onopen={() => open(thread.id)}
                        onedit={() => editNote(thread)}
                        {onopenanchor}
                        onwritten={focusAfterWrite}
                     />
                  {/each}
               </ul>
            {/if}
         {/if}
      </div>
   {/if}
   {#if chosenNotes.length > 0}
      <div class="flex shrink-0 flex-col gap-1 border-t px-4 py-2" role="group" aria-label="Send notes" hidden={shownThread !== null}>
         <div class="flex flex-wrap items-center gap-3">
            <p class="flex-1 text-sm" aria-live="polite">{chosenNotes.length === 1 ? '1 note chosen.' : `${chosenNotes.length} notes chosen.`}</p>
            <Button
               variant="outline"
               size="sm"
               disabled={batchUnavailable !== null}
               aria-disabled={batch.busy}
               aria-describedby={batchUnavailable === null ? undefined : `${id}-batch-unavailable`}
               onclick={sendChosen}>Send chosen notes</Button
            >
         </div>
         {#if batchUnavailable !== null}
            <p id="{id}-batch-unavailable" class="text-sm text-muted-foreground">{batchUnavailable}</p>
         {/if}
         {#if batch.refusal !== null}
            <p class="text-sm text-destructive" role="alert">{batch.refusal}</p>
         {/if}
      </div>
   {/if}
   <AskBox bind:this={box} {view} {binding} {controls} {plan} {stop} hidden={shownThread !== null} />
</div>
