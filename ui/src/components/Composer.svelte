<!--
   The box in which the reviewer writes to Claude at the bottom of the Threads panel: the ask box under the thread list and the follow-up box under a thread view. The field is one line that
   grows with the words, with the expand and send buttons inside it, or a taller editor once the reviewer expands it. Enter sends from the one-line field and Ctrl+Enter from the editor.
-->
<script lang="ts">
   import Maximize2 from '@lucide/svelte/icons/maximize-2';
   import Minimize2 from '@lucide/svelte/icons/minimize-2';
   import SendIcon from '@lucide/svelte/icons/send';
   import { Button } from '$lib/components/ui/button';
   import { tick, type Snippet } from 'svelte';
   import { sendOnEnter, sendOnModifiedEnter } from '../lib/dom';
   import { maxQuestionLength } from '../lib/threadView';
   import TooltipButton from './TooltipButton.svelte';

   let {
      text = $bindable(''),
      label,
      placeholder,
      describedBy,
      disabled = false,
      expandLabel,
      sendLabel,
      sendBlocked,
      sendDescribedBy,
      busy,
      hidden = false,
      onsubmit,
      header,
      quote,
      tools,
      actions,
      notices,
      footer,
   }: {
      /** The words in the field. */
      text?: string;
      /** The field's accessible name. */
      label: string;
      placeholder: string;
      /** The IDs of the elements that describe the field, or undefined for none. */
      describedBy?: string;
      /** Whether the field takes no words, as while the controls refuse what it sends. */
      disabled?: boolean;
      /** The name of the button that expands the one-line field. */
      expandLabel: string;
      /** The name of the send buttons. */
      sendLabel: string;
      /** Whether the controls refuse what the box sends, which disables its send buttons. */
      sendBlocked: boolean;
      /** The IDs of the elements that say why the send buttons are disabled, or undefined for none. */
      sendDescribedBy?: string;
      /** Whether a write of the box is in flight. */
      busy: boolean;
      /** Whether the box is hidden. It stays mounted, so its words and its expansion stay. */
      hidden?: boolean;
      /** Handles the form's submission: a send button, or a key that sends. */
      onsubmit: (event: SubmitEvent) => void;
      /** The start of the row above the field, such as what the box asks about. The expanded editor's shrink button ends the row. */
      header?: Snippet;
      /** What the box quotes above the field, with the classes that fit it to the one-line field or to the editor. */
      quote?: Snippet<[classes: string]>;
      /** Icon buttons inside the one-line field, before its expand button. */
      tools?: Snippet;
      /** Buttons in the expanded editor's last row, before its send button. */
      actions?: Snippet;
      /** Lines under the field, such as a refusal and why the box cannot send. */
      notices?: Snippet;
      /** Lines at the end of the box. */
      footer?: Snippet;
   } = $props();

   const id = $props.id();
   let expanded = $state(false);
   let field = $state<HTMLTextAreaElement>();
   const empty = $derived(text.trim() === '');

   // The one-line field grows with the words up to its maximum height, past which it scrolls. A hidden panel gives it no scroll height, so it keeps the height of its one row there.
   $effect(() => {
      void text;
      if (field !== undefined && !expanded) {
         field.style.height = 'auto';
         if (field.scrollHeight > 0) {
            field.style.height = `${field.scrollHeight}px`;
         }
      }
   });

   /** Moves the focus to the box's field. */
   export function focus(): void {
      field?.focus();
   }

   async function resize(to: boolean): Promise<void> {
      expanded = to;
      await tick();
      field?.focus();
   }
</script>

{#snippet input(rows: number, classes: string)}
   <label for="{id}-field" class="sr-only">{label}</label>
   <textarea
      bind:this={field}
      bind:value={text}
      id="{id}-field"
      {rows}
      {disabled}
      {placeholder}
      maxlength={maxQuestionLength}
      aria-describedby={describedBy || undefined}
      class="w-full min-w-0 resize-none bg-transparent text-sm placeholder:text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50 {classes}"
      onkeydown={expanded ? sendOnModifiedEnter : sendOnEnter}></textarea>
{/snippet}

<!-- The expanded editor gives up height to fit a short panel, while the one-line field keeps its own. -->
<form class="flex flex-col gap-2 border-t px-4 py-3 {expanded ? 'min-h-0' : 'shrink-0'}" {hidden} {onsubmit}>
   {#if header !== undefined || expanded}
      <div class="flex min-w-0 items-center gap-1.5 text-xs text-muted-foreground">
         {@render header?.()}
         {#if expanded}
            <TooltipButton variant="ghost" size="icon-xs" class="ml-auto" tooltip="Make the box smaller" aria-label="Make the box smaller" onclick={() => resize(false)}>
               <Minimize2 aria-hidden="true" />
            </TooltipButton>
         {/if}
      </div>
   {/if}
   {#if expanded}
      {@render quote?.('rounded-md border')}
      {@render input(
         7,
         'min-h-16 flex-1 rounded-md border border-input px-3 py-2 shadow-xs focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden dark:bg-input/30',
      )}
   {:else}
      <div class="flex min-w-0 flex-col overflow-hidden rounded-lg border border-input shadow-xs focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/50 dark:bg-input/30">
         {@render quote?.('border-b')}
         <div class="flex items-end gap-0.5 py-1 pr-1 pl-3">
            {@render input(1, 'max-h-40 py-1.5 focus-visible:ring-0')}
            {@render tools?.()}
            <TooltipButton variant="ghost" size="icon-sm" tooltip={expandLabel} aria-label={expandLabel} onclick={() => resize(true)}>
               <Maximize2 aria-hidden="true" />
            </TooltipButton>
            <div class="mx-0.5 mb-2 h-4 w-px shrink-0 bg-border" aria-hidden="true"></div>
            <TooltipButton
               type="submit"
               variant="ghost"
               size="icon-sm"
               class="text-primary"
               tooltip={sendLabel}
               aria-label={sendLabel}
               disabled={sendBlocked || empty}
               aria-disabled={busy}
               aria-describedby={sendDescribedBy}
            >
               <SendIcon aria-hidden="true" />
            </TooltipButton>
         </div>
      </div>
   {/if}
   {@render notices?.()}
   {#if expanded}
      <div class="flex items-center gap-2">
         {@render actions?.()}
         <span class="ml-auto text-xs text-muted-foreground">Ctrl+Enter</span>
         <Button type="submit" size="sm" aria-label={sendLabel} disabled={sendBlocked || empty} aria-disabled={busy} aria-describedby={sendDescribedBy}>
            <SendIcon aria-hidden="true" />
            Send
         </Button>
      </div>
   {:else}
      <p class="text-xs text-muted-foreground">Enter sends, Shift+Enter adds a line.</p>
   {/if}
   {@render footer?.()}
</form>
