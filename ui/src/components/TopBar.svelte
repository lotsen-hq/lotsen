<script lang="ts">
   import Hourglass from '@lucide/svelte/icons/hourglass';
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import PanelsTopLeft from '@lucide/svelte/icons/panels-top-left';
   import Unplug from '@lucide/svelte/icons/unplug';
   import type { Snippet } from 'svelte';
   import { Button, buttonVariants } from '$lib/components/ui/button';
   import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
   import { announcesRefusal, idleAnswerSend, type AnswerSendState } from '../lib/answers';
   import { disabledReason, hasAnswersToSend } from '../lib/controls';
   import type { GenerationStatus } from '../lib/generationStatus';
   import type { Attachment } from '../lib/labels';
   import type { Offer } from '../lib/types';
   import type { PruneOutcome } from '../lib/viewState';
   import type { ConnectionState } from '../lib/eventStream';
   import type { Settings } from '../lib/settings.svelte';
   import { panelIds, type WorkspaceLayout } from '../lib/workspaceLayout.svelte';
   import SettingsPanel from './SettingsPanel.svelte';
   import Wordmark from './Wordmark.svelte';
   import AttachmentState from './AttachmentState.svelte';

   let {
      connection,
      attachment,
      generation = null,
      sendAnswers,
      unsentAnswers = 0,
      answerSend = idleAnswerSend,
      onsendanswers = async () => null,
      layout,
      settings,
      onprune,
      picker,
      navigation,
      iteration,
   }: {
      connection: ConnectionState;
      /** The open tour's attachment state, or null while no tour is open. */
      attachment: Attachment | null;
      /** What Claude is doing on the open tour, or null when the bar has nothing to say about it or no tour is open. */
      generation?: GenerationStatus | null;
      /** Whether the open tour takes a send of its answers, from its controls, or undefined while no tour is open or the page has no controls for it. */
      sendAnswers?: Offer;
      /** The number of the open tour's answers that the reviewer has not sent, which the bar only shows, 0 while no tour is open. */
      unsentAnswers?: number;
      /** The state of the open tour's send of its answers, which the row under a stop's checks shares. */
      answerSend?: AnswerSendState;
      /** Sends the open tour's answers to send as one batch. The outcome reaches the bar through `answerSend`. */
      onsendanswers?: () => Promise<string | null>;
      layout: WorkspaceLayout;
      settings: Settings;
      /** Removes the clones that no tour refers to, as the settings panel offers. */
      onprune: () => Promise<PruneOutcome>;
      /** The tour switcher. */
      picker: Snippet;
      /** The stop navigation, which starts at the same place whatever the tour switcher shows. */
      navigation: Snippet;
      /** The open tour's iteration check, absent while it has none. */
      iteration?: Snippet;
   } = $props();

   const id = $props.id();
   const unavailable = $derived(attachment === null ? null : disabledReason(sendAnswers));
   const refusal = $derived(answerSend.refusal?.reason ?? null);

   function send(): void {
      if (unavailable === null) {
         void onsendanswers();
      }
   }

   let panelsMenu = $state<HTMLElement | null>(null);
   $effect(() => (panelsMenu ? layout.registerMenu(panelsMenu) : undefined));

   const connectionLabels: Record<ConnectionState, string> = {
      connecting: 'Connecting…',
      live: 'Live',
      reconnecting: 'Reconnecting…',
   };
</script>

<header class="flex items-center gap-4 border-b bg-card px-4 py-1.5">
   <Wordmark />
   <!-- A fixed share of the bar, so a longer or shorter tour label moves nothing to its right. -->
   <div class="flex w-1/4 shrink-0 items-center">
      {@render picker()}
   </div>
   <!-- The floor keeps the buttons and some of the text, so a tight bar shrinks the line beside them first. It drops in a narrow window, where the higher floor pushes the bar's last controls out. -->
   <div class="flex min-w-32 flex-1 xl:min-w-44">
      {@render navigation()}
   </div>
   {#if unsentAnswers > 0}
      <span class="rounded-sm bg-muted px-1.5 text-sm text-muted-foreground" role="status" data-unsent-answers>{unsentAnswers === 1 ? '1 unsent answer' : `${unsentAnswers} unsent answers`}</span>
   {/if}
   {#if hasAnswersToSend(sendAnswers)}
      <Button
         variant="outline"
         size="sm"
         class="shrink-0"
         disabled={unavailable !== null}
         aria-disabled={answerSend.sending}
         aria-describedby={[unavailable === null ? null : `${id}-unavailable`, refusal === null ? null : `${id}-refusal`].filter((part) => part !== null).join(' ') || undefined}
         onclick={send}>Send answers</Button
      >
      {#if unavailable !== null}
         <span id="{id}-unavailable" class="max-w-64 truncate text-sm text-muted-foreground" title={unavailable}>{unavailable}</span>
      {/if}
      {#if refusal !== null}
         <span id="{id}-refusal" class="max-w-64 truncate text-sm text-destructive" role={announcesRefusal(answerSend, 'topBar') ? 'alert' : undefined} title={refusal}>{refusal}</span>
      {/if}
   {/if}
   {@render iteration?.()}
   {#if generation !== null}
      <div class="flex max-w-96 min-w-0 items-center gap-1.5 text-sm text-muted-foreground" role="status" data-generation-status>
         {#if generation.phase === 'awaitingApproval'}
            <Hourglass class="size-4 shrink-0" aria-hidden="true" />
         {:else if generation.phase === 'noSession'}
            <Unplug class="size-4 shrink-0" aria-hidden="true" />
         {:else}
            <LoaderCircle class="size-4 shrink-0 animate-spin" aria-hidden="true" />
         {/if}
         <span class="truncate" title={generation.line}>{generation.line}</span>
      </div>
   {/if}
   {#if attachment !== null}
      <AttachmentState {attachment} />
   {/if}
   <span class={['text-sm', connection === 'live' ? 'text-success' : 'text-muted-foreground']} role="status">{connectionLabels[connection]}</span>
   <DropdownMenu.Root>
      <DropdownMenu.Trigger bind:ref={panelsMenu} class="{buttonVariants({ variant: 'outline', size: 'sm' })} shrink-0">
         <PanelsTopLeft aria-hidden="true" />
         Panels
         <ChevronDown aria-hidden="true" />
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align="end" class="w-40">
         {#each panelIds as id (id)}
            <DropdownMenu.CheckboxItem closeOnSelect={false} bind:checked={() => !layout.hidden[id], () => layout.toggleHidden(id)}>{layout.titles[id]}</DropdownMenu.CheckboxItem>
         {/each}
      </DropdownMenu.Content>
   </DropdownMenu.Root>
   <SettingsPanel {settings} {onprune} />
</header>
