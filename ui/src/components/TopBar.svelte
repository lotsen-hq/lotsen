<script lang="ts">
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import PanelsTopLeft from '@lucide/svelte/icons/panels-top-left';
   import X from '@lucide/svelte/icons/x';
   import type { Snippet } from 'svelte';
   import { Button, buttonVariants } from '$lib/components/ui/button';
   import { DropdownMenu as DropdownMenuPrimitive } from 'bits-ui';
   import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
   import { idleAnswerSend, type AnswerSendState } from '../lib/answers';
   import { disabledReason, hasAnswersToSend } from '../lib/controls';
   import type { GenerationStatus } from '../lib/generationStatus';
   import type { Attachment } from '../lib/labels';
   import type { Offer } from '../lib/types';
   import type { PruneOutcome } from '../lib/viewState';
   import type { ConnectionState } from '../lib/eventStream';
   import type { Settings } from '../lib/settings.svelte';
   import { arrangements } from '../lib/arrangements';
   import { namedLayouts } from '../lib/namedLayouts';
   import type { WorkspaceLayout } from '../lib/workspaceLayout.svelte';
   import { panelIds } from '../lib/workspacePanels';
   import ManageLayoutsDialog from './ManageLayoutsDialog.svelte';
   import SaveLayoutDialog from './SaveLayoutDialog.svelte';
   import SettingsPanel from './SettingsPanel.svelte';
   import Wordmark from './Wordmark.svelte';
   import GenerationIcon from './GenerationIcon.svelte';
   import StatusChip from './StatusChip.svelte';
   import AnswerRefusalNotice from './AnswerRefusalNotice.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      connection,
      attachment,
      generation = null,
      sendAnswers,
      unsentAnswers = 0,
      answerSend = idleAnswerSend,
      onsendanswers = async () => null,
      ondismissrefusal = () => {},
      layout,
      settings,
      onprune,
      onversion,
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
      /** Clears the open tour's standing refusal of a send, which the strip under the bar shows. */
      ondismissrefusal?: () => void;
      layout: WorkspaceLayout;
      settings: Settings;
      /** Removes the clones that no tour refers to, as the settings panel offers. */
      onprune: () => Promise<PruneOutcome>;
      /** Returns the server's build, or null when it could not be read, for the settings panel's footer. */
      onversion: () => Promise<string | null>;
      /** The tour switcher. */
      picker: Snippet;
      /** The stop navigation. */
      navigation: Snippet;
      /** The open tour's iteration check, which the bar shows beside the tour switcher, absent while it has none. */
      iteration?: Snippet;
   } = $props();

   const id = $props.id();
   const unavailable = $derived(attachment === null ? null : disabledReason(sendAnswers));
   const unsentLabel = $derived(unsentAnswers === 1 ? '1 unsent answer' : `${unsentAnswers} unsent answers`);
   let sendButton = $state<HTMLElement | null>(null);
   let bar = $state<HTMLElement | null>(null);

   function send(): void {
      if (unavailable === null) {
         void onsendanswers();
      }
   }

   // Dismiss leaves with the strip, so the focus moves to Send answers, or to the bar while the bar shows none.
   function dismissRefusal(): void {
      ondismissrefusal();
      (sendButton ?? bar)?.focus();
   }

   let panelsMenu = $state<HTMLElement | null>(null);
   $effect(() => (panelsMenu ? layout.registerMenu(panelsMenu) : undefined));

   // Another page's change of a named layout changes the menu and never this page's workspace.
   const savedLayouts = $derived(namedLayouts());
   let saving = $state(false);
   let managing = $state(false);

   // The item that opened a dialog left with the menu, so the focus returns to the menu's button.
   function focusPanelsMenu(event: Event): void {
      event.preventDefault();
      panelsMenu?.focus();
   }
</script>

<header bind:this={bar} class="flex items-center gap-4 border-b bg-card px-4 py-1.5 outline-hidden" tabindex="-1">
   <Wordmark />
   <!-- The bar has three zones: which tour, where in it, then Claude and the workspace. The update check sits in the tour zone, so only the label moves it. -->
   <div class="flex max-w-72 min-w-0 shrink-0 items-center gap-2" data-tour-zone>
      {@render picker()}
      {@render iteration?.()}
   </div>
   {#if attachment !== null}
      <div class="h-5 w-px shrink-0 bg-border" aria-hidden="true" data-zone-divider></div>
   {/if}
   <!-- The floor keeps the buttons and some of the text, so a tight bar shrinks the line beside them first. It drops in a narrow window, where the higher floor pushes the bar's last controls out. -->
   <div class="flex min-w-32 flex-1 xl:min-w-44">
      {@render navigation()}
   </div>
   {#if attachment !== null}
      <div class="h-5 w-px shrink-0 bg-border" aria-hidden="true" data-zone-divider></div>
   {/if}
   <!-- One live region holds the count in words, so the visible count, in the button or alone, stays hidden from screen readers. -->
   {#if unsentAnswers > 0}
      <span id="{id}-count" class="sr-only" role="status" data-unsent-answers>{unsentLabel}</span>
   {/if}
   {#if hasAnswersToSend(sendAnswers)}
      <!-- The tooltip never turns null, because a change to or from null mounts the button again and drops its focus. -->
      <WithTooltip text={unavailable ?? 'Send your answers to Claude'}>
         {#snippet trigger({ props })}
            <!-- An unavailable send stays focusable, so the keyboard reaches its reason, and send() ignores its click.
               The label and the count are elements of their own, so a narrow bar can hide the label and keep the count. -->
            <Button
               {...props}
               bind:ref={sendButton}
               variant="outline"
               size="sm"
               class={['shrink-0', unavailable !== null && 'opacity-50']}
               aria-disabled={unavailable !== null || answerSend.sending}
               aria-describedby={[unsentAnswers > 0 ? `${id}-count` : null, unavailable === null ? null : `${id}-unavailable`, answerSend.refusal === null ? null : `${id}-refusal`]
                  .filter((part) => part !== null)
                  .join(' ') || undefined}
               onclick={send}
            >
               {#if answerSend.sending}
                  <LoaderCircle class="animate-spin" aria-hidden="true" />
                  <span data-send-label>{unsentAnswers > 0 ? `Sending ${unsentAnswers}…` : 'Sending…'}</span>
               {:else}
                  <span data-send-label>Send answers</span>
                  {#if unsentAnswers > 0}
                     {@render count()}
                  {/if}
               {/if}
            </Button>
         {/snippet}
      </WithTooltip>
      <!-- The button's description replaces the tooltip's, which exists only while the tooltip shows, so the reason also lives here. -->
      {#if unavailable !== null}
         <span id="{id}-unavailable" class="sr-only">{unavailable}</span>
      {/if}
   {:else if unsentAnswers > 0}
      <WithTooltip text={unsentLabel}>
         {#snippet trigger({ props })}
            {@render count(props)}
         {/snippet}
      </WithTooltip>
   {/if}
   {#if generation !== null}
      <div class="flex max-w-96 min-w-0 items-center gap-1.5 text-sm text-muted-foreground" role="status" data-generation-status>
         <GenerationIcon phase={generation.phase} />
         <span class="truncate" title={generation.line}>{generation.line}</span>
      </div>
   {/if}
   {#if layout.popoutFailure !== null}
      <div class="flex max-w-80 min-w-0 items-center gap-0.5" data-popout-failure>
         <span class="truncate text-sm text-destructive" role="alert" title={layout.popoutFailure}>{layout.popoutFailure}</span>
         <Button variant="ghost" size="icon-sm" class="shrink-0" aria-label="Dismiss the popout notice" onclick={() => (layout.popoutFailure = null)}><X aria-hidden="true" /></Button>
      </div>
   {/if}
   <StatusChip {connection} {attachment} {generation} />
   <DropdownMenu.Root>
      <DropdownMenu.Trigger bind:ref={panelsMenu} class="{buttonVariants({ variant: 'outline', size: 'sm' })} shrink-0">
         <PanelsTopLeft aria-hidden="true" />
         Panels
         <ChevronDown aria-hidden="true" />
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align="end" class="w-56">
         {#each panelIds as id (id)}
            <DropdownMenu.CheckboxItem closeOnSelect={false} bind:checked={() => !layout.hidden[id], () => layout.toggleHidden(id)}>{layout.titles[id]}</DropdownMenu.CheckboxItem>
         {/each}
         <DropdownMenu.Separator />
         <DropdownMenuPrimitive.Group>
            <DropdownMenu.GroupHeading>Arrange</DropdownMenu.GroupHeading>
            {#each arrangements as arrangement (arrangement.id)}
               <DropdownMenu.Item onSelect={() => layout.apply(arrangement.id)}>{arrangement.label}</DropdownMenu.Item>
            {/each}
         </DropdownMenuPrimitive.Group>
         <DropdownMenu.Separator />
         <DropdownMenuPrimitive.Group>
            <DropdownMenu.GroupHeading>My layouts</DropdownMenu.GroupHeading>
            {#each savedLayouts as saved (saved.id)}
               {@const record = saved.record}
               {#if record === undefined}
                  <DropdownMenu.Item disabled class="flex-col items-start gap-0">
                     <span class="max-w-full truncate">{saved.name}</span>
                     <span class="text-xs text-muted-foreground">Saved by another version of lotsen</span>
                  </DropdownMenu.Item>
               {:else}
                  <DropdownMenu.Item onSelect={() => layout.applyRecord(record)}><span class="truncate">{saved.name}</span></DropdownMenu.Item>
               {/if}
            {/each}
            <DropdownMenu.Item onSelect={() => (saving = true)}>Save layout as…</DropdownMenu.Item>
            <DropdownMenu.Item onSelect={() => (managing = true)}>Manage layouts…</DropdownMenu.Item>
         </DropdownMenuPrimitive.Group>
         <DropdownMenu.Separator />
         <DropdownMenu.Item onSelect={() => layout.apply('default')}>Reset layout</DropdownMenu.Item>
      </DropdownMenu.Content>
   </DropdownMenu.Root>
   <SaveLayoutDialog bind:open={saving} snapshot={() => layout.snapshot()} onCloseAutoFocus={focusPanelsMenu} />
   <ManageLayoutsDialog bind:open={managing} onCloseAutoFocus={focusPanelsMenu} />
   <SettingsPanel {settings} {onprune} {onversion} />
</header>
<AnswerRefusalNotice {answerSend} reasonId="{id}-refusal" ondismiss={dismissRefusal} />

{#snippet count(props: Record<string, unknown> = {})}
   <span
      {...props}
      class="inline-flex h-5 min-w-5 shrink-0 items-center justify-center rounded-full bg-muted px-1.5 text-xs font-semibold text-muted-foreground tabular-nums"
      aria-hidden="true"
      data-unsent-count>{unsentAnswers}</span
   >
{/snippet}
