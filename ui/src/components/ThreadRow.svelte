<!--
   One row of the thread list: a marker of the thread's state, where the thread is, when it last changed, its title and its last message on one line, then the thread's ⋯ button. A click on the
   row opens the thread's thread view, and its menu opens from the ⋯ button or a right-click, as `ThreadMenu` says.
-->
<script lang="ts">
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import StickyNote from '@lucide/svelte/icons/sticky-note';
   import { anchorLabel, rowTime } from '../lib/labels';
   import type { PinCommits } from '../lib/pin';
   import type { ThreadWrites } from '../lib/threadsView.svelte';
   import { lastActivity, lastLine, threadMark, threadStateLabels, threadTitle, type ThreadMark } from '../lib/threadView';
   import type { Plan, QuestionThread, ThreadAnchor, TourControls } from '../lib/types';
   import ThreadMenu from './ThreadMenu.svelte';

   let {
      thread,
      controls,
      plan,
      commits = null,
      writes,
      unsavedEdit = null,
      newReply,
      now,
      chosen = false,
      onchoose = () => {},
      onopen,
      onedit,
      onopenanchor,
      onwritten = () => {},
   }: {
      thread: QuestionThread;
      /** The tour's controls, which the thread's menu reads, or null while the page has none. */
      controls: TourControls | null;
      /** The tour's plan, which names the anchor's stop, or null while the tour has none. */
      plan: Plan | null;
      /** The commits of the tour's pin, or null while the tour is not pinned. */
      commits?: PinCommits | null;
      /** The thread's writes, which the row's menu shares with the thread view. */
      writes: ThreadWrites;
      /** Why the note cannot go while the reviewer edits it, as `ThreadDrafts.unsavedEditReason` says, or null while no edit of it is open. */
      unsavedEdit?: string | null;
      /** Whether Claude's last message in the thread is new, as `ThreadsView.isNew` says. */
      newReply: boolean;
      /** The time against which the row tells when the thread last changed. */
      now: Date;
      /** Whether the reviewer chose the note for the next note batch. */
      chosen?: boolean;
      /** Chooses the note for the next note batch, or leaves it out. */
      onchoose?: (chosen: boolean) => void;
      /** Opens the thread's thread view. */
      onopen: () => void;
      /** Opens the note's thread view with its edit started. */
      onedit: () => void;
      /** Opens where the thread was asked. */
      onopenanchor: (anchor: ThreadAnchor) => void;
      /** Places the focus after a write of the menu succeeded, by the thread's ID. */
      onwritten?: (threadId: string) => void;
   } = $props();

   const markLabels: Record<Exclude<ThreadMark, null>, string> = { waiting: 'Waiting for Claude', note: 'Note', yourTurn: 'Your turn', newReply: 'New reply' };

   const mark = $derived(threadMark(thread, newReply));
   const title = $derived(threadTitle(thread));
</script>

<li data-thread-id={thread.id} class="border-b hover:bg-accent/50 has-aria-expanded:bg-accent/50">
   <ThreadMenu
      {thread}
      {controls}
      {plan}
      {commits}
      {writes}
      {unsavedEdit}
      {chosen}
      {onchoose}
      {onedit}
      {onopenanchor}
      {onwritten}
      label={thread.kind === 'note' ? 'Note actions' : 'Thread actions'}
      class="flex min-w-0 items-start pr-1.5"
      buttonClass="mt-2"
   >
      <button type="button" class="flex min-w-0 flex-1 gap-2.5 py-2.5 pr-1 pl-3 text-left focus-visible:bg-accent/50 focus-visible:outline-hidden" onclick={onopen}>
         <span class="flex w-3 shrink-0 justify-center pt-1.5" data-mark={mark ?? undefined}>
            {#if mark === 'waiting'}
               <LoaderCircle class="size-3 animate-spin text-muted-foreground" aria-hidden="true" />
            {:else if mark === 'note'}
               <StickyNote class="size-3 text-muted-foreground" aria-hidden="true" />
            {:else if mark === 'yourTurn'}
               <span class="size-2 rounded-full bg-your-turn" aria-hidden="true"></span>
            {:else if mark === 'newReply'}
               <span class="size-2 rounded-full bg-primary" aria-hidden="true"></span>
            {/if}
            {#if mark !== null}
               <span class="sr-only">{markLabels[mark]}:</span>
            {/if}
         </span>
         <span class="flex min-w-0 flex-1 flex-col">
            <span class="flex min-w-0 items-baseline gap-2 text-xs text-muted-foreground">
               <span class="min-w-0 flex-1 truncate" data-where>{anchorLabel(thread.anchor, plan)}</span>
               {#if thread.state !== 'open'}
                  <span class="shrink-0 rounded-sm bg-muted px-1.5">{threadStateLabels[thread.state]}</span>
               {/if}
               {#if mark === 'yourTurn'}
                  <span class="shrink-0 font-semibold text-your-turn" aria-hidden="true">Your turn</span>
               {/if}
               <span class="shrink-0" data-time>{rowTime(lastActivity(thread), now)}</span>
            </span>
            <span class="truncate font-semibold" data-title>
               {#if title.badge !== null}
                  <span class="mr-1 rounded-sm border px-1 text-xs font-medium text-muted-foreground">{title.badge}</span>
               {/if}
               {title.text}
            </span>
            <span class="truncate text-sm text-muted-foreground" data-last>{lastLine(thread)}</span>
         </span>
      </button>
   </ThreadMenu>
   {#if (writes.menu.refusal ?? writes.note.refusal) !== null}
      <p class="px-4 pb-2 text-sm text-destructive" role="alert">{writes.menu.refusal ?? writes.note.refusal}</p>
   {/if}
</li>
