<!--
   The stop toolbar: the Ask Claude menu on the left, then on the right the done checkbox, the skip toggle and the ⋯ menu. The done checkbox and the skip toggle take the icons and colors of the Plan panel's row, so a state looks the same in both panels.
   A checked done checkbox also boxes its check, because the color alone stands out too little at the top of the panel.
-->
<script lang="ts">
   import Check from '@lucide/svelte/icons/check';
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import CopyCheck from '@lucide/svelte/icons/copy-check';
   import Ellipsis from '@lucide/svelte/icons/ellipsis';
   import SkipForward from '@lucide/svelte/icons/skip-forward';
   import SquareCheckBig from '@lucide/svelte/icons/square-check-big';
   import { tick } from 'svelte';
   import { buttonVariants } from '$lib/components/ui/button';
   import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
   import { stopActionReasons } from '../lib/controls';
   import { doneClick } from '../lib/doneClick';
   import { skipTooltip, stopDoneCheckboxLabel, stopDoneTooltip } from '../lib/labels';
   import { versionOf } from '../lib/stops';
   import { tourActionLabels, type StopActionKind } from '../lib/tourActions';
   import type { Stop, TourControls } from '../lib/types';
   import type { DoneState } from '../lib/tourRecord.svelte';
   import type { KnownVersion } from '../lib/versions';
   import TooltipButton from './TooltipButton.svelte';

   let {
      stop,
      move,
      onchoose,
      onfocusform,
      offersActions,
      sending,
      controls,
      versions,
      current,
      onopenversion,
      skipped,
      onskip,
      done,
      onsetdone,
   }: {
      /** The stop at the version that the panel shows. */
      stop: Stop;
      /** The ⋯ menu's move of the stop's explanation to the other placement, with its label, or null while the reviewer cannot move it. */
      move: { label: string; run: () => void } | null;
      /** Shows the form of a tour action below the stop toolbar. */
      onchoose: (kind: StopActionKind) => void;
      /** Moves the focus to the form of the chosen tour action, once it has mounted. */
      onfocusform: () => void;
      /** Whether the page offers the stop's tour actions. */
      offersActions: boolean;
      /** Whether a tour action of the form below the stop toolbar is on its way to the server. The Ask Claude menu waits for it, so the server's answer reaches the form that sent it. */
      sending: boolean;
      /** The tour's controls, which say whether each tour action can go, or null while the page has none. */
      controls: TourControls | null;
      /** The stop's earlier versions, oldest first. */
      versions: readonly KnownVersion[];
      /** The number of the stop's current version, or undefined while it is pending. */
      current: number | undefined;
      /** Shows a version of the stop by its number. */
      onopenversion: (number: number) => void;
      /** Whether the reviewer skipped the stop. */
      skipped: boolean;
      /** Skips the stop or takes its skip back, as `StopPanel`'s prop says, or null when the page offers no skip. */
      onskip: ((skipped: boolean) => Promise<string | null>) | null;
      /** The stop's done state, which compares the reviewer's done mark with the stop's current version. */
      done: DoneState;
      /** Marks a version of the stop done, as `StopPanel`'s prop says, or null when the page offers no done mark. */
      onsetdone: ((version: number | null) => Promise<string | null>) | null;
   } = $props();

   const id = $props.id();
   const kinds: readonly StopActionKind[] = ['explain', 'revise'];

   const written = $derived(stop.stage === 'written');
   const reasons = $derived(stopActionReasons(controls, stop.id));
   // Both actions usually share their reason, such as an unattached session, so the stop toolbar shows each reason once.
   const shownReasons = $derived(offersActions && written ? [...new Set(kinds.map((kind) => reasons[kind]).filter((reason) => reason !== null))] : []);
   const askDisabled = $derived(reasons.explain !== null && reasons.explain === reasons.revise);
   /** Whether the next close of the Ask Claude menu moves the focus into the form that it opened. */
   let focusForm = false;

   const shown = $derived(versionOf(stop) ?? current);
   const click = $derived(current === undefined || shown === undefined ? null : doneClick(done, current, shown));
   const numbers = $derived(current === undefined || versions.length === 0 ? [] : [...versions.map((version) => version.number), current]);

   let skipping = $state(false);
   let skipRefusal = $state<string | null>(null);
   let marking = $state(false);
   let doneRefusal = $state<string | null>(null);

   function choose(kind: StopActionKind): void {
      onchoose(kind);
      focusForm = true;
   }

   // The menu returns the focus to its trigger when it closes, so the form takes the focus only after that.
   function placeFocus(event: Event): void {
      if (focusForm) {
         event.preventDefault();
         focusForm = false;
         void tick().then(onfocusform);
      }
   }

   async function toggleSkip(): Promise<void> {
      if (onskip === null || skipping) {
         return;
      }

      skipping = true;
      skipRefusal = await onskip(!skipped);
      skipping = false;
   }

   async function toggleDone(): Promise<void> {
      if (onsetdone === null || click === null || click.earlier || marking) {
         return;
      }

      marking = true;
      doneRefusal = await onsetdone(click.clears ? null : click.version);
      marking = false;
   }
</script>

<div class="stop-toolbar mt-2 flex flex-wrap items-center gap-1">
   {#if offersActions && written}
      <DropdownMenu.Root>
         <DropdownMenu.Trigger
            class={buttonVariants({ variant: 'outline', size: 'sm' })}
            disabled={askDisabled || sending}
            aria-describedby={askDisabled ? `${id}-unavailable-${shownReasons.indexOf(reasons.explain!)}` : undefined}
         >
            Ask Claude<ChevronDown aria-hidden="true" />
         </DropdownMenu.Trigger>
         <DropdownMenu.Content align="start" class="w-44" onCloseAutoFocus={placeFocus}>
            {#each kinds as kind (kind)}
               {@const reason = reasons[kind]}
               <DropdownMenu.Item disabled={reason !== null} aria-describedby={reason === null ? undefined : `${id}-unavailable-${shownReasons.indexOf(reason)}`} onSelect={() => choose(kind)}
                  >{tourActionLabels[kind]}…</DropdownMenu.Item
               >
            {/each}
         </DropdownMenu.Content>
      </DropdownMenu.Root>
   {/if}
   <div class="ml-auto flex items-center gap-1">
      <!-- A pending stop has nothing to read yet, so it gets no checkbox. -->
      {#if onsetdone !== null && click !== null && written}
         {@const tooltip = stopDoneTooltip(done, click)}
         <TooltipButton
            {tooltip}
            variant="ghost"
            size="icon-sm"
            class={`done-toggle ${done.state === 'done' ? 'checked' : done.state === 'earlier' ? 'earlier' : ''}`}
            role="checkbox"
            aria-label={stopDoneCheckboxLabel(done, click)}
            aria-checked={done.state === 'done'}
            disabled={marking || click.earlier}
            onclick={toggleDone}
         >
            {#if done.state === 'done'}<SquareCheckBig />{:else if done.state === 'earlier'}<CopyCheck />{:else}<Check />{/if}
         </TooltipButton>
      {/if}
      <!-- A skip belongs to the stop, not to a version, so an earlier version offers it too. -->
      {#if onskip !== null}
         <TooltipButton
            tooltip={skipTooltip(skipped)}
            variant={skipped ? 'secondary' : 'ghost'}
            size="icon-sm"
            class="skip-toggle"
            aria-label="Skip stop"
            aria-pressed={skipped}
            disabled={skipping}
            onclick={toggleSkip}
         >
            <SkipForward />
         </TooltipButton>
      {/if}
      {#if move !== null || numbers.length > 0}
         <DropdownMenu.Root>
            <DropdownMenu.Trigger class={buttonVariants({ variant: 'ghost', size: 'icon-sm' })} aria-label="More stop actions">
               <Ellipsis aria-hidden="true" />
            </DropdownMenu.Trigger>
            <DropdownMenu.Content align="end" class="w-48">
               {#if move !== null}
                  <DropdownMenu.Item onSelect={move.run}>{move.label}</DropdownMenu.Item>
               {/if}
               {#if numbers.length > 0}
                  {#if move !== null}
                     <DropdownMenu.Separator />
                  {/if}
                  <DropdownMenu.RadioGroup value={String(shown)} onValueChange={(value) => onopenversion(Number(value))}>
                     <DropdownMenu.GroupHeading>Version</DropdownMenu.GroupHeading>
                     {#each numbers as number (number)}
                        <DropdownMenu.RadioItem value={String(number)}>{number === current ? `Version ${number}, current` : `Version ${number}`}</DropdownMenu.RadioItem>
                     {/each}
                  </DropdownMenu.RadioGroup>
               {/if}
            </DropdownMenu.Content>
         </DropdownMenu.Root>
      {/if}
   </div>
</div>
{#each shownReasons as reason, index (reason)}
   <p id="{id}-unavailable-{index}" class="mt-1 text-xs text-muted-foreground">{reason}</p>
{/each}
{#if skipRefusal !== null}
   <p class="mt-1 text-xs text-destructive" role="alert">{skipRefusal}</p>
{/if}
{#if doneRefusal !== null}
   <p class="mt-1 text-xs text-destructive" role="alert">{doneRefusal}</p>
{/if}
