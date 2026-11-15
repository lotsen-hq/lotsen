<!-- The top bar's status chip: the open tour's attachment state, or the connection while it is not live, with the details in a popover. -->
<script lang="ts">
   import CircleCheck from '@lucide/svelte/icons/circle-check';
   import CircleDashed from '@lucide/svelte/icons/circle-dashed';
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import Unplug from '@lucide/svelte/icons/unplug';
   import type { LucideIcon } from '@lucide/svelte';
   import { Button } from '$lib/components/ui/button';
   import * as Popover from '$lib/components/ui/popover';
   import type { ConnectionState } from '../lib/eventStream';
   import type { GenerationStatus } from '../lib/generationStatus';
   import { attachmentTexts, connectionLabels, type Attachment } from '../lib/labels';
   import GenerationIcon from './GenerationIcon.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      connection,
      attachment,
      generation = null,
   }: {
      connection: ConnectionState;
      /** The open tour's attachment state, or null while no tour is open. The chip then shows only a connection that is not live. */
      attachment: Attachment | null;
      /** What Claude is doing on the open tour, which the popover repeats, or null when the bar shows nothing of it. */
      generation?: GenerationStatus | null;
   } = $props();

   const id = $props.id();
   const icons: Record<Attachment, LucideIcon> = { none: Unplug, unconfirmed: CircleDashed, confirmed: CircleCheck };
   const session = $derived(attachment === null ? null : attachmentTexts[attachment]);
   const SessionIcon = $derived(attachment === null ? null : icons[attachment]);
   // A connection that is not live takes over the chip, because the page receives no change of the session until the stream returns.
   const live = $derived(connection === 'live');
   const label = $derived(live ? (session?.label ?? null) : connectionLabels[connection]);
   const name = $derived(label === null ? null : live ? `Claude session: ${label}` : `Connection: ${label}`);
   const description = $derived(session === null ? null : live ? session.explanation : `Claude session: ${session.label}. ${session.explanation}`);
   const tooltip = $derived(description === null ? name : `${name}\n${description}`);
   const sessionTone = $derived(attachment === 'confirmed' ? 'text-success' : 'text-muted-foreground');
   let open = $state(false);
   // The popover leaves with the chip without a close event, so it would open by itself when the chip returns.
   $effect.pre(() => {
      if (name === null) {
         open = false;
      }
   });
</script>

<!-- Always mounted, so a screen reader hears each change of the connection, which the chip shows only while it is not live. -->
<span class="sr-only" role="status" data-connection-status>{connectionLabels[connection]}</span>
{#if name !== null}
   <Popover.Root bind:open>
      <Popover.Trigger>
         {#snippet child({ props: popoverProps })}
            <WithTooltip text={tooltip} suppressed={open} {...popoverProps}>
               {#snippet trigger({ props })}
                  <!-- The chip keeps its colors on hover and while the popover is open, so the chip reads as a status beside the bar's actions.
                     The amber chip's text keeps the foreground color, as a warning notice's text does, because the warning mark on the warning fill is too faint for text in the Catppuccin light palette.
                     The label is an element of its own, so it can be hidden while the icon and the chip's name stay. -->
                  <Button
                     {...props}
                     variant="ghost"
                     size="sm"
                     class={[
                        'shrink-0 rounded-full border-input font-normal @max-bar-chip:w-8 @max-bar-chip:px-0',
                        !live
                           ? 'border-warning bg-warning-surface text-foreground hover:bg-warning-surface aria-expanded:bg-warning-surface'
                           : attachment === 'confirmed'
                             ? 'text-success hover:text-success aria-expanded:text-success'
                             : 'text-muted-foreground hover:text-muted-foreground aria-expanded:text-muted-foreground',
                     ]}
                     aria-label={name}
                     aria-describedby={description === null ? undefined : `${id}-description`}
                     data-status-chip={live ? 'session' : 'connection'}
                  >
                     {#if live && SessionIcon !== null}
                        <SessionIcon aria-hidden="true" />
                     {:else}
                        <LoaderCircle class="animate-spin text-warning-mark" aria-hidden="true" />
                     {/if}
                     <span class="@max-bar-chip:sr-only" data-chip-label>{label}</span>
                  </Button>
               {/snippet}
            </WithTooltip>
         {/snippet}
      </Popover.Trigger>
      <Popover.Content align="end" class="w-80 gap-0 p-3" role="dialog" aria-label="Claude session and connection">
         <dl class="flex flex-col gap-2">
            {#if session !== null && SessionIcon !== null}
               <div class="flex gap-2">
                  <dt class="flex w-26 shrink-0 items-center gap-2 self-start font-medium"><SessionIcon class={['size-4 shrink-0', sessionTone]} aria-hidden="true" />Session</dt>
                  <dd data-status-row="session">{session.label}. {session.explanation}</dd>
               </div>
            {/if}
            <div class="flex gap-2">
               <dt class="flex w-26 shrink-0 items-center gap-2 self-start font-medium">
                  {#if live}
                     <span class="mx-1 size-2 shrink-0 rounded-full bg-success" aria-hidden="true"></span>
                  {:else}
                     <LoaderCircle class="size-4 shrink-0 animate-spin text-warning-mark" aria-hidden="true" />
                  {/if}
                  Connection
               </dt>
               <dd data-status-row="connection">{live ? 'Live. Updates arrive as Claude writes.' : connectionLabels[connection]}</dd>
            </div>
            {#if generation !== null}
               <div class="flex gap-2 border-t pt-2">
                  <dt class="flex w-26 shrink-0 items-center gap-2 self-start font-medium"><GenerationIcon phase={generation.phase} />Now</dt>
                  <dd data-status-row="generation">{generation.line}</dd>
               </div>
            {/if}
         </dl>
      </Popover.Content>
   </Popover.Root>
   {#if description !== null}
      <span id="{id}-description" class="sr-only">{description}</span>
   {/if}
{/if}
