<!--
   The Threads panel's ask box under the thread list, which asks Claude about the open stop, the tour or the pick that waits in it, or saves the words as a note. A chip's menu chooses what the
   box asks about, and a pick that the box asks about shows as a quote above the field. The box is a Composer.
-->
<script lang="ts">
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import StickyNote from '@lucide/svelte/icons/sticky-note';
   import X from '@lucide/svelte/icons/x';
   import { Button } from '$lib/components/ui/button';
   import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
   import { tick } from 'svelte';
   import { disabledReason } from '../lib/controls';
   import { anchorLabel, attachmentOf } from '../lib/labels';
   import { panelDocument } from '../lib/panelDocument.svelte';
   import { PendingWrite } from '../lib/pendingWrite.svelte';
   import { getThreadActions } from '../lib/threadActions.svelte';
   import type { Pick, ThreadsView } from '../lib/threadsView.svelte';
   import type { AnchorInput, Plan, Stop, TourBinding, TourControls } from '../lib/types';
   import type { AskAbout, PickedAnchor } from '../lib/viewState';
   import Composer from './Composer.svelte';
   import TooltipButton from './TooltipButton.svelte';

   let {
      view,
      binding,
      controls,
      plan,
      stop,
      hidden = false,
   }: {
      /** The tour's threads view, which holds the pick that waits in the box. */
      view: ThreadsView;
      /** The session bound to the tour, or null when none is, whose attachment level the box names. */
      binding: TourBinding | null;
      /** The tour's controls, which say whether the reviewer can ask now, or null while the page has none. */
      controls: TourControls | null;
      /** The tour's plan, which names the stops, or null while the tour has none. */
      plan: Plan | null;
      /** The open stop, or null when none is open. */
      stop: Stop | null;
      /** Whether the box is hidden, as while a thread view shows. It stays mounted, so its words, its pick and its expansion stay. */
      hidden?: boolean;
   } = $props();

   const id = $props.id();
   const actions = getThreadActions();
   const asking = new PendingWrite();

   let about = $state<'stop' | 'tour' | 'picked'>('stop');
   let text = $state('');
   let box = $state<Composer>();
   const home = panelDocument();

   const picked = $derived(view.picked);
   // A question about the stop needs an open stop, so without one the box asks about the tour and keeps the reviewer's choice for the next stop. A pick that went asks about the stop.
   const asked = $derived<AskAbout>(about === 'picked' && picked !== null ? picked.anchor : about === 'tour' || stop === null ? 'tour' : 'stop');
   const choiceValue = $derived(typeof asked === 'object' ? 'picked' : asked);
   const stopNumber = $derived(stop === null ? 0 : (plan?.stops.findIndex((candidate) => candidate.id === stop.id) ?? -1) + 1);
   const stopName = $derived(stopNumber > 0 ? `Stop ${stopNumber}` : 'This stop');
   const pickName = $derived(picked?.anchor.kind === 'file' ? 'The file' : 'The selected text');
   const chip = $derived(typeof asked === 'object' ? pickName : asked === 'stop' ? stopName : 'The tour');
   const placeholders: Record<string, string> = {
      stop: 'Ask Claude about this stop',
      tour: 'Ask Claude about the tour',
      code: 'Ask Claude about these lines',
      file: 'Ask Claude about this file',
      stopText: 'Ask Claude about this text',
   };

   const attachment = $derived(attachmentOf(binding));
   const unavailable = $derived(disabledReason(controls?.offers.ask));
   const describedBy = $derived(
      [unavailable === null ? null : `${id}-unavailable`, attachment === 'unconfirmed' ? `${id}-unconfirmed` : null, `${id}-permissions`, asking.refusal === null ? null : `${id}-refusal`]
         .filter((part) => part !== null)
         .join(' '),
   );
   const empty = $derived(text.trim() === '');

   // Each pick waits in the box, which may sit in a panel that the pick just showed, so the focus moves once the panel shows.
   $effect(() => {
      if (picked !== null) {
         about = 'picked';
         void tick().then(() => home.window.requestAnimationFrame(() => box?.focus()));
      }
   });

   /** Moves the focus to the box's field. */
   export function focus(): void {
      box?.focus();
   }

   /** Returns where a pick is, as its quote names it: the stop of a stop's text, or the lines or the file. */
   function whereOf(anchor: PickedAnchor): string {
      return anchor.kind === 'stopText' ? anchorLabel({ kind: 'stop', stopId: anchor.stopId }, plan) : anchorLabel(anchor, plan);
   }

   function choose(value: string): void {
      about = value === 'picked' ? 'picked' : value === 'stop' ? 'stop' : 'tour';
   }

   // A pick that went leaves the box asking about the open stop, or the tour while no stop is open, as `asked` says.
   function remove(pick: Pick): void {
      view.release(pick);
      box?.focus();
   }

   function anchorOf(about: AskAbout): AnchorInput {
      return typeof about === 'object' ? about : about === 'stop' && stop !== null ? { kind: 'stop', stopId: stop.id } : { kind: 'tour' };
   }

   // Submitting the form sends the question, or saves it as a note while no session can receive it, as the keys that send do.
   async function send(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      await write(unavailable !== null);
   }

   // The question names the open stop as the one the reviewer viewed, whatever it is about. Once the server stores a question or a note about the pick, the pick no longer waits.
   async function write(asNote: boolean): Promise<void> {
      if (asking.busy || empty) {
         return;
      }

      const sent = text;
      const pick = picked;
      const target = asked;
      const anchor = anchorOf(target);
      const stopId = stop?.id ?? null;
      const result = await asking.run(() => (asNote ? actions.saveNote(anchor, sent, stopId) : actions.ask(anchor, sent, stopId)));
      // The box stays editable while the question sends, so words typed meanwhile stay.
      if (result?.ok === true) {
         if (pick !== null && pick.anchor === target) {
            view.release(pick);
         }

         if (text === sent) {
            text = '';
         }
      }

      box?.focus();
   }
</script>

{#snippet chooser()}
   <span>Ask about</span>
   <DropdownMenu.Root>
      <DropdownMenu.Trigger
         aria-label="Ask about {chip}"
         class="inline-flex h-6 min-w-0 items-center gap-1 rounded-full border bg-muted px-2 text-xs text-foreground hover:bg-accent focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden aria-expanded:border-ring"
      >
         <span class="truncate">{chip}</span>
         <ChevronDown class="size-3 shrink-0" aria-hidden="true" />
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align="start" class="w-72">
         <DropdownMenu.RadioGroup value={choiceValue} onValueChange={choose}>
            <DropdownMenu.RadioItem value="tour">The tour</DropdownMenu.RadioItem>
            {#if stop !== null}
               <DropdownMenu.RadioItem value="stop"><span class="truncate">{anchorLabel({ kind: 'stop', stopId: stop.id }, plan)}</span></DropdownMenu.RadioItem>
            {/if}
            {#if picked !== null}
               <DropdownMenu.RadioItem value="picked">
                  <span class="truncate">{pickName} <span class="text-muted-foreground">· {whereOf(picked.anchor)}</span></span>
               </DropdownMenu.RadioItem>
            {/if}
         </DropdownMenu.RadioGroup>
      </DropdownMenu.Content>
   </DropdownMenu.Root>
{/snippet}

{#snippet quote(classes: string)}
   {#if typeof asked === 'object' && picked !== null}
      {@const pick = picked}
      <div role="group" aria-label={pickName} class="flex items-start gap-2 bg-muted py-1.5 pr-1.5 pl-2.5 {classes}">
         <div class="min-w-0 flex-1 border-l-3 border-primary pl-2">
            <p class="truncate text-xs text-muted-foreground">{whereOf(pick.anchor)}</p>
            {#if pick.quote !== null}
               <p class="truncate text-xs {pick.anchor.kind === 'code' ? 'font-mono' : ''}">{pick.quote}</p>
            {/if}
         </div>
         <TooltipButton
            variant="ghost"
            size="icon-xs"
            tooltip="Remove"
            aria-label="Remove the {pick.anchor.kind === 'file' ? 'file' : 'selection'} and ask about {stop === null ? 'the tour' : stopName.toLowerCase()}"
            onclick={() => remove(pick)}
         >
            <X aria-hidden="true" />
         </TooltipButton>
      </div>
   {/if}
{/snippet}

{#snippet saveTool()}
   <TooltipButton variant="ghost" size="icon-sm" tooltip="Save as note" aria-label="Save as note" disabled={empty} aria-disabled={asking.busy} onclick={() => write(true)}>
      <StickyNote aria-hidden="true" />
   </TooltipButton>
{/snippet}

{#snippet saveAction()}
   <Button type="button" variant="outline" size="sm" disabled={empty} aria-disabled={asking.busy} onclick={() => write(true)}>Save as note</Button>
{/snippet}

{#snippet notices()}
   {#if asking.refusal !== null}
      <p id="{id}-refusal" class="text-sm text-destructive" role="alert">{asking.refusal}</p>
   {/if}
   {#if unavailable !== null}
      <p id="{id}-unavailable" class="text-sm">{unavailable}</p>
   {:else if attachment === 'unconfirmed'}
      <p id="{id}-unconfirmed" class="text-sm text-muted-foreground">
         Claude has not confirmed yet that questions reach this session, so this one may not arrive. A waiting question can be canceled or sent again.
      </p>
   {/if}
{/snippet}

{#snippet permissions()}
   <p id="{id}-permissions" class="text-xs text-muted-foreground">Claude acts on what you ask with the permissions of its session.</p>
{/snippet}

<Composer
   bind:this={box}
   bind:text
   label="Question for Claude"
   placeholder={placeholders[typeof asked === 'object' ? asked.kind : asked] ?? ''}
   {describedBy}
   expandLabel="Write a longer question"
   sendLabel="Send"
   sendBlocked={unavailable !== null}
   sendDescribedBy={unavailable === null ? undefined : `${id}-unavailable`}
   busy={asking.busy}
   {hidden}
   onsubmit={send}
   header={chooser}
   {quote}
   tools={saveTool}
   actions={saveAction}
   {notices}
   footer={permissions}
/>
