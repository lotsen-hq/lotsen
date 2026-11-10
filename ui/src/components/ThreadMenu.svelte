<!--
   A thread's menu, which opens from its ⋯ button at the end of the area, from a right-click anywhere in the area, and from the context-menu key or Shift+F10 while the focus is in the area.
   Both menus hold the same items: for a sent thread its resolve or reopen, the link to its anchor, the copy of its question, the cancel and re-send of a question that waits, and its delete,
   which asks first. For a note its edit, its send, its choice for a note batch and its delete.
-->
<script lang="ts">
   import Check from '@lucide/svelte/icons/check';
   import Code from '@lucide/svelte/icons/code';
   import Copy from '@lucide/svelte/icons/copy';
   import Ellipsis from '@lucide/svelte/icons/ellipsis';
   import Pencil from '@lucide/svelte/icons/pencil';
   import RefreshCw from '@lucide/svelte/icons/refresh-cw';
   import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
   import Send from '@lucide/svelte/icons/send';
   import Trash2 from '@lucide/svelte/icons/trash-2';
   import X from '@lucide/svelte/icons/x';
   import type { Snippet } from 'svelte';
   import * as AlertDialog from '$lib/components/ui/alert-dialog';
   import { buttonVariants } from '$lib/components/ui/button';
   import * as ContextMenu from '$lib/components/ui/context-menu';
   import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
   import { disabledReason, threadOffers } from '../lib/controls';
   import type { PinCommits } from '../lib/pin';
   import { PendingWrite } from '../lib/pendingWrite.svelte';
   import { getThreadActions, type WriteResult } from '../lib/threadActions.svelte';
   import type { ThreadWrites } from '../lib/threadsView.svelte';
   import { anchorOpens, lastQuestion, showAnchorLabel, threadWords } from '../lib/threadView';
   import type { Plan, QuestionThread, ReviewerThreadState, ThreadAnchor, TourControls } from '../lib/types';

   let {
      thread,
      controls,
      plan,
      commits = null,
      writes,
      unsavedEdit = null,
      chosen = false,
      onchoose = () => {},
      onedit,
      onopenanchor,
      onwritten = () => {},
      label,
      class: className = '',
      buttonClass = '',
      children,
   }: {
      thread: QuestionThread;
      /** The tour's controls, which say whether a question can go again or be canceled and whether a note can go, or null while the page has none. */
      controls: TourControls | null;
      /** The tour's plan, which tells whether a stop anchor's stop is still in it, or null while the tour has none. */
      plan: Plan | null;
      /** The commits of the tour's pin, against which the menu tells whether the anchor stays on an earlier commit, or null while the tour is not pinned. */
      commits?: PinCommits | null;
      /** The thread's writes, which the row and the thread view share, so a write in flight in one holds back the same write in the other. */
      writes: ThreadWrites;
      /** Why the note cannot go while the reviewer edits it, as `ThreadDrafts.unsavedEditReason` says, or null while no edit of it is open. */
      unsavedEdit?: string | null;
      /** Whether the reviewer chose the note for the next note batch. */
      chosen?: boolean;
      /** Chooses the note for the next note batch, or leaves it out. */
      onchoose?: (chosen: boolean) => void;
      /** Starts the edit of the note. */
      onedit: () => void;
      /** Opens where the thread was asked. */
      onopenanchor: (anchor: ThreadAnchor) => void;
      /** Places the focus after a write of the menu succeeded, by the thread's ID, because the write's response may remove the thread or the control that had the focus. */
      onwritten?: (threadId: string) => void;
      /** The accessible name of the ⋯ button. */
      label: string;
      /** The classes of the area, which lays out the children and the ⋯ button after them. */
      class?: string;
      /** The classes that place the ⋯ button in the area. */
      buttonClass?: string;
      /** The area's content, before the ⋯ button. */
      children: Snippet;
   } = $props();

   const id = $props.id();
   const actions = getThreadActions();
   const deleteWrite = new PendingWrite();

   let atButton = $state(false);
   let atPointer = $state(false);
   let confirming = $state(false);
   let button = $state<HTMLElement | null>(null);
   /** Whether a key opened the ⋯ menu and no pointer has pressed in the area since. */
   let keyed = false;

   const note = $derived(thread.kind === 'note' ? thread : null);
   const offers = $derived(threadOffers(controls, thread.id));
   const waiting = $derived(thread.status.waiting !== undefined);
   const located = $derived(anchorOpens(thread, plan, commits));
   const cancelReason = $derived(disabledReason(offers?.cancel));
   const resendReason = $derived(disabledReason(offers?.resend));
   const noteReason = $derived(disabledReason(offers?.sendNote, unsavedEdit));
   // A thread that Claude started holds Claude's message where another thread holds the reviewer's question.
   const copyLabel = $derived(thread.kind === 'started' ? 'Copy the message' : 'Copy the question');

   // The context-menu key and Shift+F10 open the menu at the ⋯ button, where the keyboard's arrows, Enter and Escape work as they do after a click.
   function openFromKeyboard(event: KeyboardEvent): void {
      if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
         event.preventDefault();
         atPointer = false;
         atButton = true;
         keyed = true;
      }
   }

   // Windows fires a contextmenu event when the context-menu key comes up, after the keydown opened the ⋯ menu, and the prevented event opens no second menu.
   // A right-click presses a pointer first, so it still opens the area's menu.
   function keepOneMenu(event: MouseEvent): void {
      if (atButton && keyed) {
         event.preventDefault();
      }
   }

   async function settle(pending: PendingWrite, write: () => Promise<WriteResult>): Promise<boolean> {
      const threadId = thread.id;
      const result = await pending.run(write);
      if (result?.ok !== true) {
         return false;
      }

      onwritten(threadId);
      return true;
   }

   function setState(state: ReviewerThreadState): void {
      const threadId = thread.id;
      void settle(writes.menu, () => actions.setState(threadId, state));
   }

   function act(write: (questionId: string) => Promise<WriteResult>): void {
      const questionId = lastQuestion(thread)?.id;
      if (questionId !== undefined) {
         void settle(writes.menu, () => write(questionId));
      }
   }

   function copy(): void {
      const words = threadWords(thread);
      const failure = copyLabel === 'Copy the message' ? 'The message could not be copied.' : 'The question could not be copied.';
      void writes.menu.run(async () => {
         try {
            await navigator.clipboard.writeText(words);
            return { ok: true };
         } catch {
            return { ok: false, refusal: failure };
         }
      });
   }

   function sendNote(): void {
      const threadId = thread.id;
      void settle(writes.note, () => actions.sendNotes([threadId]));
   }

   function discard(): void {
      const threadId = thread.id;
      void settle(writes.note, () => actions.discardNote(threadId));
   }

   function askToDelete(): void {
      deleteWrite.clear();
      confirming = true;
   }

   async function confirmDelete(): Promise<void> {
      const threadId = thread.id;
      if (await settle(deleteWrite, () => actions.deleteThread(threadId))) {
         confirming = false;
      }
   }
</script>

{#snippet sendLabel()}<Send aria-hidden="true" />Send to Claude{/snippet}
{#snippet cancelLabel()}<X aria-hidden="true" />Cancel{/snippet}
{#snippet resendLabel()}<RefreshCw aria-hidden="true" />Send again{/snippet}

<!-- An item whose offer is closed stays focusable with the server's reason under it, as the filter menu's last state does, and selecting it does nothing. -->
{#snippet refusable(key: string, reason: string | null, busy: boolean, select: () => void, label: Snippet)}
   <DropdownMenu.Item
      disabled={busy}
      aria-disabled={reason === null ? undefined : 'true'}
      aria-describedby={reason === null ? undefined : `${id}-${key}-unavailable`}
      onSelect={(event) => (reason === null ? select() : event.preventDefault())}>{@render label()}</DropdownMenu.Item
   >
   {#if reason !== null}
      <p id="{id}-{key}-unavailable" class="mx-2 mb-1 text-xs text-muted-foreground">{reason}</p>
   {/if}
{/snippet}

{#snippet items()}
   {#if note !== null}
      <DropdownMenu.Item onSelect={onedit}><Pencil aria-hidden="true" />Edit</DropdownMenu.Item>
      {@render refusable('send', noteReason, writes.note.busy, sendNote, sendLabel)}
      <DropdownMenu.CheckboxItem checked={chosen} onCheckedChange={onchoose}>Send with other notes</DropdownMenu.CheckboxItem>
      <DropdownMenu.Separator />
      <DropdownMenu.Item class="text-destructive data-highlighted:text-destructive" disabled={writes.note.busy} onSelect={discard}><Trash2 aria-hidden="true" />Delete</DropdownMenu.Item>
   {:else}
      {#if thread.state !== 'resolved'}
         <DropdownMenu.Item disabled={writes.menu.busy} onSelect={() => setState('resolved')}><Check aria-hidden="true" />Resolve</DropdownMenu.Item>
      {/if}
      {#if thread.state !== 'open'}
         <DropdownMenu.Item disabled={writes.menu.busy} onSelect={() => setState('open')}><RotateCcw aria-hidden="true" />Reopen</DropdownMenu.Item>
      {/if}
      {#if located}
         <DropdownMenu.Item onSelect={() => onopenanchor(thread.anchor)}><Code aria-hidden="true" />{showAnchorLabel(thread)}</DropdownMenu.Item>
      {/if}
      <DropdownMenu.Item disabled={writes.menu.busy} onSelect={copy}><Copy aria-hidden="true" />{copyLabel}</DropdownMenu.Item>
      {#if waiting}
         <DropdownMenu.Separator />
         {@render refusable('cancel', cancelReason, writes.menu.busy, () => act((questionId) => actions.cancel(questionId)), cancelLabel)}
         {@render refusable('resend', resendReason, writes.menu.busy, () => act((questionId) => actions.resend(questionId)), resendLabel)}
      {/if}
      <DropdownMenu.Separator />
      <DropdownMenu.Item class="text-destructive data-highlighted:text-destructive" onSelect={askToDelete}><Trash2 aria-hidden="true" />Delete thread…</DropdownMenu.Item>
   {/if}
{/snippet}

<ContextMenu.Root bind:open={atPointer}>
   <ContextMenu.Trigger class="{className} focus-visible:outline-hidden" onkeydown={openFromKeyboard} onpointerdown={() => (keyed = false)} oncontextmenu={keepOneMenu}>
      {@render children()}
      <DropdownMenu.Root bind:open={atButton}>
         <DropdownMenu.Trigger bind:ref={button} class="{buttonVariants({ variant: 'ghost', size: 'icon-sm' })} shrink-0 {buttonClass}" aria-label={label}>
            <Ellipsis aria-hidden="true" />
         </DropdownMenu.Trigger>
         <!-- The browser's own menu stays closed on a right-click or a context-menu key inside the open menu. -->
         <DropdownMenu.Content align="end" class="w-52" oncontextmenu={(event: MouseEvent) => event.preventDefault()}>
            {@render items()}
         </DropdownMenu.Content>
      </DropdownMenu.Root>
   </ContextMenu.Trigger>
   <ContextMenu.Content class="w-52" oncontextmenu={(event: MouseEvent) => event.preventDefault()}>
      {@render items()}
   </ContextMenu.Content>
</ContextMenu.Root>

<AlertDialog.Root bind:open={confirming}>
   <AlertDialog.Content
      escapeKeydownBehavior={deleteWrite.busy ? 'ignore' : 'close'}
      onCloseAutoFocus={(event) => {
         // A delete removes the ⋯ button with the thread, and the host places the focus then.
         event.preventDefault();
         if (button?.isConnected) {
            button.focus();
         }
      }}
   >
      <AlertDialog.Header>
         <AlertDialog.Title>Delete this thread?</AlertDialog.Title>
         <AlertDialog.Description>
            The thread is deleted for good, with every message in it. This cannot be undone.{waiting ? ' Its question that waits for Claude is canceled.' : ''} A Claude session that works on the tour is
            told that you deleted it.
         </AlertDialog.Description>
      </AlertDialog.Header>
      {#if deleteWrite.busy}
         <p id="{id}-deleting" class="text-sm text-muted-foreground" role="status">Deleting the thread…</p>
      {/if}
      {#if deleteWrite.refusal !== null}
         <p class="text-sm text-destructive" role="alert">{deleteWrite.refusal}</p>
      {/if}
      <AlertDialog.Footer>
         <AlertDialog.Cancel disabled={deleteWrite.busy} aria-describedby={deleteWrite.busy ? `${id}-deleting` : undefined}>Cancel</AlertDialog.Cancel>
         <AlertDialog.Action variant="destructive" disabled={deleteWrite.busy} aria-describedby={deleteWrite.busy ? `${id}-deleting` : undefined} onclick={() => void confirmDelete()}
            >Delete thread</AlertDialog.Action
         >
      </AlertDialog.Footer>
   </AlertDialog.Content>
</AlertDialog.Root>
