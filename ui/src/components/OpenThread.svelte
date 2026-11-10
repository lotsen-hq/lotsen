<!--
   The thread view: one thread of the Threads panel in place of the thread list. A header holds the back arrow, the link to where the thread was asked, the title, the resolve or reopen and the
   thread's ⋯ button, and a right-click on the header opens the same menu, as `ThreadMenu` says. Under it the messages scroll in the order they were written, then a question that waits for Claude
   with its cancel and re-send. Claude's messages open with Claude's name. The line under each message holds its time and state, and the reviewer's messages name their writer there. The follow-up
   box stays at the bottom. A note shows its words as a draft, with its edit, its send and its choice for a note batch at the bottom instead. The reviewer's messages show as text and Claude's as
   the server rendered them, as chat bubbles or across the panel's width, as the message style says.
-->
<script lang="ts">
   import ArrowLeft from '@lucide/svelte/icons/arrow-left';
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import Sparkle from '@lucide/svelte/icons/sparkle';
   import { onDestroy, tick, type Snippet } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import { disabledReason, outcomeOf, threadOffers } from '../lib/controls';
   import { anchorLabel, messageTime, shortened } from '../lib/labels';
   import type { LinkHover } from '../lib/linkHover';
   import type { MessageStyle } from '../lib/messageStyle';
   import type { PinCommits } from '../lib/pin';
   import { PendingWrite } from '../lib/pendingWrite.svelte';
   import { getThreadActions, type WriteResult } from '../lib/threadActions.svelte';
   import type { ThreadDrafts } from '../lib/threadsView.svelte';
   import { sendOnModifiedEnter } from '../lib/dom';
   import {
      anchorOpens,
      subjectOfQuestion,
      earlierCommitOf,
      lastQuestion,
      lastQuestionAnswered,
      lastRequest,
      maxQuestionLength,
      messagesOf,
      questionOfRequest,
      questionWords,
      replyResolution,
      threadStateLabels,
      threadTitle,
      writerOf,
      type AgentMessage,
      type Question,
   } from '../lib/threadView';
   import { subjectSummary } from '../lib/tourActions';
   import type { CodeReference, Plan, QuestionThread, RequestState, ReviewerThreadState, ThreadAnchor, ThreadMessage, TourControls } from '../lib/types';
   import Composer from './Composer.svelte';
   import Prose from './Prose.svelte';
   import ThreadMenu from './ThreadMenu.svelte';
   import TooltipButton from './TooltipButton.svelte';

   let {
      thread,
      controls,
      plan,
      focusedPassage,
      hover,
      messageStyle = 'bubbles',
      drafts,
      viewedStopId = null,
      chosen = false,
      onchoose = () => {},
      onwritten = () => {},
      onback,
      onfollowreference,
      onopenanchor,
      commits = null,
   }: {
      thread: QuestionThread;
      /** The tour's controls, which say whether the thread's question can go again, take a follow-up or a cancel, and whether a note can go, or null while the page has none. */
      controls: TourControls | null;
      /** The tour's plan, which names the anchor's stop, or null while the tour has none. */
      plan: Plan | null;
      /** The focused passage, whose `ref:` links the replies highlight, or null for none. */
      focusedPassage: CodeReference | null;
      /** What the links in the replies report hovering to, or null for nothing. */
      hover: LinkHover | null;
      /** How the view lays out the messages. */
      messageStyle?: MessageStyle;
      /** The tour's drafts, which keep the words of the follow-up and of a note's edit while the view does not show. */
      drafts: ThreadDrafts;
      /** The stop under view, which a follow-up names as the one the reviewer viewed, or null when none is open. */
      viewedStopId?: string | null;
      /** Whether the reviewer chose the note for the next note batch. */
      chosen?: boolean;
      /** Chooses the note for the next note batch, or leaves it out. */
      onchoose?: (chosen: boolean) => void;
      /** Places the focus after a write of the view succeeded, by the thread's ID, because the write's response may remove the thread or a control that had the focus. */
      onwritten?: (threadId: string) => void;
      /** Shows the thread list again. */
      onback: () => void;
      onfollowreference: (reference: CodeReference) => void;
      /** Opens where the thread was asked. */
      onopenanchor: (anchor: ThreadAnchor) => void;
      /** The commits of the tour's pin, against which the thread tells whether its anchor stays on an earlier commit, or null while the tour is not pinned. */
      commits?: PinCommits | null;
   } = $props();

   const id = $props.id();
   const replyStates: Record<RequestState, string> = {
      done: 'Done',
      needsAnswer: 'Needs an answer',
      declined: 'Declined',
      pending: 'Done',
      unanswered: 'Done',
      canceled: 'Done',
   };
   const notWaiting: Partial<Record<RequestState, string>> = {
      unanswered: 'Unanswered: the session detached from the tour before Claude replied.',
      canceled: 'Canceled. Claude may still answer it.',
   };

   const actions = getThreadActions();
   // One write per control, so a write in flight or a refusal of one control leaves the others as they are.
   const questionWrite = new PendingWrite();
   const stateWrite = new PendingWrite();
   // The writes that keep a draft belong to the thread, so a view that mounts again while one is in flight holds back a second one.
   const writes = $derived(drafts.writesOf(thread.id));
   const followUpWrite = $derived(writes.followUp);
   const noteWrite = $derived(writes.note);

   let location = $state<HTMLButtonElement>();
   let editor = $state<HTMLTextAreaElement>();
   // The times under the messages count days from when the view opened.
   const now = new Date();

   // A thread that goes, as when the tour changes, leaves no hovered reference behind.
   onDestroy(() => {
      if (location) {
         hover?.leave('pointer', location);
         hover?.leave('focus', location);
      }
   });

   // The reviewer saw the refusal of each write that ended while this view showed, so the next view leaves it out. A write still in flight has no refusal yet, so its refusal shows in the next view.
   $effect(() => {
      const shown = writes;
      return () => {
         shown.followUp.clear();
         shown.note.clear();
         shown.menu.clear();
      };
   });

   const followUpText = $derived(drafts.followUp(thread.id));
   const editText = $derived(drafts.noteEdit(thread.id));
   const editing = $derived(editText !== null);
   const title = $derived(threadTitle(thread));
   const question = $derived(lastQuestion(thread));
   const last = $derived(lastRequest(thread));
   const offers = $derived(threadOffers(controls, thread.id));
   const answered = $derived(lastQuestionAnswered(thread));
   const waiting = $derived(thread.status.waiting !== undefined);
   // Send again shows for every question without an answer, and the controls say whether it can go.
   const resendable = $derived(last !== null && !answered);
   const followable = $derived(thread.kind !== 'note' && !waiting);
   const resendReason = $derived(disabledReason(offers?.resend));
   const followUpReason = $derived(disabledReason(offers?.followUp));
   const unsavedEdit = $derived(thread.kind === 'note' ? drafts.unsavedEditReason(thread) : null);
   const noteReason = $derived(disabledReason(offers?.sendNote, unsavedEdit));
   // A reason that the follow-up box shows already describes Send again too, so it shows once.
   const resendReasonShared = $derived(followable && resendReason !== null && resendReason === followUpReason);
   const note = $derived(thread.kind === 'note' ? thread : null);
   const bubbles = $derived(messageStyle === 'bubbles');
   // The commit that the anchor stays on, whose lines or file the pin may hold elsewhere or not at all, so the anchor neither opens nor highlights.
   const earlierCommit = $derived(earlierCommitOf(thread, commits));
   const located = $derived(anchorOpens(thread, plan, commits));
   const reference = $derived<CodeReference | null>(
      thread.anchor.kind === 'code' && earlierCommit === null ? { side: thread.anchor.side, path: thread.anchor.path, startLine: thread.anchor.startLine, endLine: thread.anchor.endLine } : null,
   );

   // A reply follows the question it answers, unless a follow-up came first, and the reply then names its question.
   function answeredEarlier(reply: AgentMessage, index: number): Question | null {
      if (reply.kind === 'agentMessage') {
         return null;
      }

      const answered = questionOfRequest(thread, reply.requestId);
      const before = messagesOf(thread)
         .slice(0, index)
         .findLast((message): message is Question => message.kind === 'question');
      return answered !== null && answered.id !== before?.id ? answered : null;
   }

   // The view may lose the control that wrote, or the thread itself, with a write's response, so the panel places the focus after each write that succeeded.
   async function settle(pending: PendingWrite, write: () => Promise<WriteResult>): Promise<boolean> {
      const threadId = thread.id;
      const result = await pending.run(write);
      if (result?.ok !== true) {
         return false;
      }

      onwritten(threadId);
      return true;
   }

   async function sendFollowUp(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (followUpReason !== null || followUpWrite.busy || followUpText.trim() === '') {
         return;
      }

      const threadId = thread.id;
      const sent = followUpText;
      // Words typed while the follow-up was sending stay.
      if ((await settle(followUpWrite, () => actions.followUp(threadId, sent, viewedStopId))) && drafts.followUp(threadId) === sent) {
         drafts.setFollowUp(threadId, '');
      }
   }

   async function setState(state: ReviewerThreadState): Promise<void> {
      const threadId = thread.id;
      await settle(stateWrite, () => actions.setState(threadId, state));
   }

   // A question before the last one cannot wait, so it shows only how it ended without an answer.
   function endedUnanswered(question: Question): string | null {
      const last = question.requests.at(-1);
      return question.id === lastQuestion(thread)?.id || outcomeOf(thread, question.id) === 'answered' || last === undefined ? null : (notWaiting[last.state] ?? null);
   }

   // Claude has a question once the session that its last request went to fetched it. Claude's agent message answers no question, so only a reply has a state to show.
   function stateOf(message: ThreadMessage): string | null {
      switch (message.kind) {
         case 'question':
            return message.requests.at(-1)?.receivedAt === undefined ? 'Sent' : 'Received';
         case 'reply':
            return replyStates[replyResolution(thread, message)];
         case 'agentMessage':
            return null;
      }
   }

   function startEdit(): void {
      if (note !== null) {
         drafts.startNoteEdit(note);
      }
   }

   async function editFromMenu(): Promise<void> {
      startEdit();
      await tick();
      editor?.focus();
   }

   async function saveEdit(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (editText === null || editText.trim() === '') {
         return;
      }

      if (noteWrite.busy) {
         return;
      }

      const threadId = thread.id;
      const words = editText;
      writes.saving = words;
      const saved = await settle(noteWrite, () => actions.editNote(threadId, words));
      writes.saving = null;
      // Words typed while the save was in flight keep the editor open.
      if (saved && drafts.noteEdit(threadId) === words) {
         drafts.setNoteEdit(threadId, null);
      }
   }

   function sendNote(): void {
      const threadId = thread.id;
      void settle(noteWrite, () => actions.sendNotes([threadId]));
   }

   function act(write: (questionId: string) => Promise<WriteResult>): void {
      const questionId = question?.id;
      if (questionId !== undefined) {
         void settle(questionWrite, () => write(questionId));
      }
   }
</script>

<!-- The full-width style has no sides to tell the writers apart, so the fill, Claude's bar and Claude's name do. -->
{#snippet bubble(mine: boolean, writer: string, time: string, state: string | null, content: Snippet)}
   <div class="flex flex-col {bubbles ? (mine ? 'items-end' : 'items-start') : 'items-stretch'}" data-writer={writer}>
      <div
         class="flex min-w-0 flex-col gap-1 px-3 py-2 {mine ? 'bg-primary/14 dark:bg-primary/18' : 'border-l-3 border-claude-marker bg-claude-fill'} {bubbles
            ? `max-w-5/6 rounded-xl ${mine ? 'rounded-br-sm' : 'rounded-tl-sm'}`
            : 'rounded-lg'}"
         data-bubble
      >
         {#if !mine}
            <span class="claude-name" data-writer-name><Sparkle class="size-3.5 shrink-0" aria-hidden="true" />{writer}</span>
         {/if}
         {@render content()}
      </div>
      <p class="mt-1 text-xs text-muted-foreground" data-meta>
         {`${mine ? `${writer} · ` : ''}${time}${state === null ? '' : ' · '}`}{#if state !== null}<span data-delivery>{state}</span>{/if}
      </p>
   </div>
{/snippet}

<article class="flex min-h-0 flex-1 flex-col focus-visible:outline-hidden" tabindex="-1" aria-labelledby="{id}-title" data-thread-id={thread.id}>
   <header class="shrink-0 border-b">
      <ThreadMenu
         {thread}
         {controls}
         {plan}
         {commits}
         {writes}
         {unsavedEdit}
         {chosen}
         {onchoose}
         onedit={() => void editFromMenu()}
         {onopenanchor}
         {onwritten}
         label="More thread actions"
         class="flex items-center gap-1 py-1.5 pr-2 pl-1"
      >
         <TooltipButton variant="ghost" size="icon-sm" class="shrink-0" tooltip="Back to all threads" aria-label="Back to all threads" onclick={onback}>
            <ArrowLeft aria-hidden="true" />
         </TooltipButton>
         <div class="flex min-w-0 flex-1 flex-col">
            {#if located}
               <!-- A code anchor highlights its lines while the pointer or visible keyboard focus is on the link, as a `ref:` link does. -->
               <button
                  bind:this={location}
                  type="button"
                  class="min-w-0 cursor-pointer truncate text-left text-xs text-muted-foreground underline-offset-2 hover:underline"
                  onclick={() => onopenanchor(thread.anchor)}
                  onmouseenter={(event) => reference !== null && hover?.enter('pointer', event.currentTarget, reference)}
                  onmouseleave={(event) => hover?.leave('pointer', event.currentTarget)}
                  onfocus={(event) => reference !== null && event.currentTarget.matches(':focus-visible') && hover?.enter('focus', event.currentTarget, reference)}
                  onblur={(event) => hover?.leave('focus', event.currentTarget)}
               >
                  {anchorLabel(thread.anchor, plan)}
               </button>
            {:else}
               <p class="min-w-0 truncate text-xs text-muted-foreground">{anchorLabel(thread.anchor, plan)}</p>
            {/if}
            <h3 id="{id}-title" class="truncate text-sm font-semibold">
               {#if title.badge !== null}
                  <span class="mr-1 rounded-sm border px-1 text-xs font-medium text-muted-foreground">{title.badge}</span>
               {/if}
               {title.text}
            </h3>
         </div>
         {#if thread.state !== 'open'}
            <span class="shrink-0 rounded-sm bg-muted px-1.5 text-xs text-muted-foreground">{threadStateLabels[thread.state]}</span>
         {/if}
         <!-- One button turns between Resolve and Reopen, so it keeps the focus through the state change. -->
         <Button variant="outline" size="sm" class="shrink-0" aria-disabled={stateWrite.busy} aria-describedby="{id}-title" onclick={() => setState(thread.state === 'resolved' ? 'open' : 'resolved')}
            >{thread.state === 'resolved' ? 'Reopen' : 'Resolve'}</Button
         >
         {#if thread.state === 'outdated'}
            <Button variant="outline" size="sm" class="shrink-0" aria-disabled={stateWrite.busy} aria-describedby="{id}-title" onclick={() => setState('open')}>Reopen</Button>
         {/if}
      </ThreadMenu>
   </header>
   {#if writes.menu.refusal !== null}
      <p class="shrink-0 border-b px-4 py-2 text-sm text-destructive" role="alert">{writes.menu.refusal}</p>
   {/if}
   {#if stateWrite.refusal !== null}
      <p class="shrink-0 border-b px-4 py-2 text-sm text-destructive" role="alert">{stateWrite.refusal}</p>
   {/if}
   <div class="scroll-box flex min-h-0 flex-1 flex-col gap-3 px-3 py-3" data-messages={messageStyle}>
      {#if earlierCommit !== null}
         <p class="text-xs text-muted-foreground" data-earlier-commit>On commit <span class="font-mono">{earlierCommit.slice(0, 8)}</span>, which the tour no longer shows.</p>
      {/if}
      {#if note !== null}
         {#if editText !== null}
            <form class="flex flex-col gap-2" onsubmit={saveEdit}>
               <label for="{id}-note" class="sr-only">Note</label>
               <textarea
                  bind:this={editor}
                  bind:value={() => editText ?? '', (text) => drafts.setNoteEdit(thread.id, text)}
                  id="{id}-note"
                  rows="3"
                  maxlength={maxQuestionLength}
                  class="min-h-12 w-full resize-y rounded-md border border-input bg-transparent px-3 py-2 text-sm shadow-xs focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden dark:bg-input/30"
                  onkeydown={sendOnModifiedEnter}></textarea>
               <div class="flex justify-end gap-2">
                  <Button variant="outline" size="sm" onclick={() => drafts.setNoteEdit(thread.id, null)}>Cancel</Button>
                  <Button type="submit" variant="outline" size="sm" disabled={editText.trim() === ''} aria-disabled={noteWrite.busy}>Save</Button>
               </div>
            </form>
         {:else}
            {#snippet noteWords()}
               <p class="whitespace-pre-wrap" data-question>{note.text}</p>
            {/snippet}
            {@render bubble(true, 'You', messageTime(note.savedAt, now), 'Draft', noteWords)}
         {/if}
      {/if}
      {#each messagesOf(thread) as message, index (message.id)}
         {#if message.kind === 'question'}
            {@const subject = subjectOfQuestion(thread, message)}
            {#snippet questionWordsShown()}
               {#if subject !== null}
                  <p class="text-sm" data-action>
                     <span class="rounded-sm bg-background/60 px-1.5 text-xs text-muted-foreground">{subject.kind === 'action' ? 'Tour action' : 'Your decision'}</span>
                     {subjectSummary(subject)}
                  </p>
               {/if}
               {#if message.text !== ''}
                  <p class="whitespace-pre-wrap" data-question>{message.text}</p>
               {:else}
                  <p class="sr-only">{questionWords(message, subject)}</p>
               {/if}
            {/snippet}
            {@render bubble(true, writerOf(message), messageTime(message.createdAt, now), stateOf(message), questionWordsShown)}
            {#if endedUnanswered(message) !== null}
               <p class="text-sm text-muted-foreground">{endedUnanswered(message)}</p>
            {/if}
         {:else}
            {@const earlier = answeredEarlier(message, index)}
            {#snippet replyShown()}
               {#if earlier !== null}
                  <p class="text-sm text-muted-foreground">Answers “{shortened(questionWords(earlier, subjectOfQuestion(thread, earlier)), 48)}”</p>
               {/if}
               {#if message.html !== undefined}
                  <div data-reply><Prose html={message.html} onopenreference={onfollowreference} {focusedPassage} {hover} /></div>
               {:else if message.text !== undefined}
                  <p class="whitespace-pre-wrap" data-reply>{message.text}</p>
               {/if}
            {/snippet}
            {@render bubble(false, writerOf(message), messageTime(message.createdAt, now), stateOf(message), replyShown)}
         {/if}
      {/each}
      {#if waiting}
         <p class="flex items-center gap-1.5 text-sm text-muted-foreground" data-waiting>
            <LoaderCircle class="size-4 shrink-0 animate-spin" aria-hidden="true" />
            Waiting for Claude's reply. Claude may ask in the terminal.
         </p>
      {:else if !answered && last !== null && notWaiting[last.state] !== undefined}
         <p class="text-sm text-muted-foreground">{notWaiting[last.state]}</p>
      {/if}
      {#if resendable || waiting}
         <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
            {#if waiting}
               <Button variant="outline" size="sm" aria-disabled={questionWrite.busy} onclick={() => act((questionId) => actions.cancel(questionId))}>Cancel</Button>
            {/if}
            {#if resendable}
               <Button
                  variant="outline"
                  size="sm"
                  disabled={resendReason !== null}
                  aria-disabled={questionWrite.busy}
                  aria-describedby={resendReason === null ? undefined : resendReasonShared ? `${id}-follow-up-unavailable` : `${id}-resend-unavailable`}
                  onclick={() => act((questionId) => actions.resend(questionId))}
               >
                  Send again
               </Button>
            {/if}
         </div>
         {#if questionWrite.refusal !== null}
            <p class="text-sm text-destructive" role="alert">{questionWrite.refusal}</p>
         {/if}
         {#if resendable && resendReason !== null && !resendReasonShared}
            <p id="{id}-resend-unavailable" class="text-sm text-muted-foreground">{resendReason}</p>
         {/if}
      {/if}
   </div>
   {#if note !== null}
      <div class="flex shrink-0 flex-col gap-2 border-t px-4 py-3" role="group" aria-label="Note">
         <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
            <label class="flex items-center gap-1.5 text-sm">
               <input type="checkbox" class="size-4 accent-primary" checked={chosen} onchange={(event) => onchoose(event.currentTarget.checked)} />
               Send with other notes
            </label>
            <div class="ml-auto flex items-center gap-2">
               {#if !editing}
                  <Button variant="outline" size="sm" aria-describedby="{id}-title" onclick={startEdit}>Edit</Button>
               {/if}
               <Button
                  variant="outline"
                  size="sm"
                  disabled={noteReason !== null}
                  aria-disabled={noteWrite.busy}
                  aria-describedby={noteReason === null ? undefined : `${id}-note-unavailable`}
                  onclick={sendNote}>Send to Claude</Button
               >
            </div>
         </div>
         {#if noteWrite.refusal !== null}
            <p class="text-sm text-destructive" role="alert">{noteWrite.refusal}</p>
         {/if}
         {#if noteReason !== null}
            <p id="{id}-note-unavailable" class="text-sm text-muted-foreground">{noteReason}</p>
         {/if}
      </div>
   {:else if followable}
      {#snippet followUpNotices()}
         {#if followUpWrite.refusal !== null}
            <p id="{id}-follow-up-refusal" class="text-sm text-destructive" role="alert">{followUpWrite.refusal}</p>
         {/if}
         {#if followUpReason !== null}
            <p id="{id}-follow-up-unavailable" class="text-sm text-muted-foreground">{followUpReason}</p>
         {/if}
      {/snippet}
      <Composer
         bind:text={() => followUpText, (text) => drafts.setFollowUp(thread.id, text)}
         label="Follow-up for Claude"
         placeholder="Follow up in this thread"
         describedBy={[followUpReason === null ? null : `${id}-follow-up-unavailable`, followUpWrite.refusal === null ? null : `${id}-follow-up-refusal`].filter((part) => part !== null).join(' ')}
         disabled={followUpReason !== null}
         expandLabel="Write a longer follow-up"
         sendLabel="Send the follow-up"
         sendBlocked={followUpReason !== null}
         sendDescribedBy={followUpReason === null ? undefined : `${id}-follow-up-unavailable`}
         busy={followUpWrite.busy}
         onsubmit={sendFollowUp}
         notices={followUpNotices}
      />
   {/if}
</article>
