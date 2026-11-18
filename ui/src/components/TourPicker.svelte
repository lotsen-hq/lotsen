<script lang="ts">
   import { tick } from 'svelte';
   import BookOpen from '@lucide/svelte/icons/book-open';
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import CornerLeftUp from '@lucide/svelte/icons/corner-left-up';
   import ExternalLink from '@lucide/svelte/icons/external-link';
   import GitPullRequest from '@lucide/svelte/icons/git-pull-request';
   import { buttonVariants } from '$lib/components/ui/button';
   import * as AlertDialog from '$lib/components/ui/alert-dialog';
   import * as Popover from '$lib/components/ui/popover';
   import { firstDives, flatDives, subtreeOf, tourName, type DiveBranch } from '../lib/tourFamily';
   import { switcherLines } from '../lib/tourLabel';
   import type { TourSummary } from '../lib/types';
   import TooltipButton from './TooltipButton.svelte';
   import TourList from './TourList.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      tours,
      allTours,
      hiddenCount,
      showHidden,
      loaded,
      error,
      selectedTourId,
      selectedTour,
      parent = null,
      onselect,
      onshowhidden,
      onarchive,
      ondelete,
   }: {
      /** The tours that the list shows. */
      tours: TourSummary[];
      /** Every tour, the hidden ones too, from which the list's archive buttons and the delete confirmation name the dives below a tour, or undefined for the listed tours alone. */
      allTours?: readonly TourSummary[];
      /** The number of tours that the list hides while hidden tours do not show. */
      hiddenCount: number;
      showHidden: boolean;
      loaded: boolean;
      error: string | null;
      selectedTourId: string | null;
      /** The selected tour's summary, or null while no tour is selected or the list does not hold it yet. */
      selectedTour: TourSummary | null;
      /** The tour that the selected dive's up button opens, or null for a tour that is not a dive. */
      parent?: TourSummary | null;
      onselect: (tourId: string) => void;
      onshowhidden: (show: boolean) => void;
      /** Archives a tour or unarchives it, and returns null once stored, or the reason the write failed. */
      onarchive: (tourId: string, archived: boolean) => Promise<string | null>;
      /** Deletes a tour for good, and returns null once it is gone, or the reason the delete failed. */
      ondelete: (tourId: string) => Promise<string | null>;
   } = $props();

   const id = $props.id();
   const everyTour = $derived(allTours ?? tours);
   // The confirmation names this many dives at most and counts the rest, so the dialog stays short for a large family.
   const maxShownDives = 10;
   let open = $state(false);
   let listTrigger = $state<HTMLElement | null>(null);
   // The tour whose delete the dialog asks to confirm. The dialog lives outside the list's popover, which closes when the dialog opens.
   let deleting = $state<TourSummary | null>(null);
   let confirming = $state(false);
   let busy = $state(false);
   let deleteError = $state<string | null>(null);
   const lines = $derived(selectedTour === null ? null : switcherLines(selectedTour));
   const topLine = $derived(lines === null || lines.place === null ? null : lines.commit === null ? lines.place : `${lines.place} · ${lines.commit}`);
   // The name holds the bottom line, the text that a speech user reads on the button. A null tooltip would render the trigger as a new element, which drops the focus that the closing list returns to it.
   const triggerName = $derived(lines === null ? 'Choose a tour' : `${lines.title}, choose another tour`);
   const triggerTooltip = $derived(lines === null ? 'Choose a tour' : [lines.title, topLine, 'Choose another tour'].filter((line) => line !== null).join('\n'));
   // Only an https: URL becomes a link.
   const targetUrl = $derived(selectedTour && URL.canParse(selectedTour.target.url) && new URL(selectedTour.target.url).protocol === 'https:' ? selectedTour.target.url : null);
   const linkName = $derived(selectedTour?.target.kind === 'repository' ? 'Open the repository in a new tab' : 'Open the pull request in a new tab');
   const deletedName = $derived(deleting === null ? '' : deleting.title === undefined ? `of ${deleting.label}` : `“${deleting.title}” of ${deleting.label}`);
   // The dives as the reviewer confirmed them, which the dialog keeps while the delete runs and while the dialog closes, as the deleted tours leave the list.
   let confirmedDives = $state.raw<DiveBranch[] | null>(null);
   // Until the confirmation, the dives come from the current list, so a dive that Claude creates while the dialog is open shows too.
   const deletedDives = $derived(confirmedDives ?? (deleting === null ? [] : subtreeOf(deleting.id, everyTour)));
   const deletedDiveCount = $derived(flatDives(deletedDives).length);
   const shownDives = $derived(firstDives(deletedDives, maxShownDives));
   const unshownDiveCount = $derived(deletedDiveCount - flatDives(shownDives).length);

   /** Moves the focus to the switcher's trigger, which stays in place when another tour opens. */
   export function focusSwitcher(): void {
      listTrigger?.focus();
   }

   // The up button leaves once the parent is a root, so the focus moves to the switcher's trigger, as a choice in the list leaves it there.
   function openParent(tourId: string): void {
      onselect(tourId);
      void tick().then(focusSwitcher);
   }

   function select(tourId: string) {
      open = false;
      onselect(tourId);
   }

   function askToDelete(tour: TourSummary) {
      open = false;
      deleting = tour;
      confirmedDives = null;
      deleteError = null;
      confirming = true;
   }

   async function confirmDelete() {
      if (deleting === null || busy) {
         return;
      }

      busy = true;
      confirmedDives = deletedDives;
      try {
         deleteError = await ondelete(deleting.id);
      } finally {
         busy = false;
      }

      if (deleteError === null) {
         confirming = false;
      } else {
         confirmedDives = null;
      }
   }
</script>

<div class="flex max-w-full min-w-0 items-center gap-1">
   {#if parent !== null}
      {@const up = parent}
      {@const upName = `Back to ${tourName(up)}`}
      <TooltipButton tooltip={upName} variant="ghost" size="icon-sm" class="shrink-0" aria-label={upName} onclick={() => openParent(up.id)}>
         <CornerLeftUp aria-hidden="true" />
      </TooltipButton>
   {/if}
   <Popover.Root bind:open>
      <Popover.Trigger>
         {#snippet child({ props: popoverProps })}
            <WithTooltip text={triggerTooltip} {...popoverProps}>
               {#snippet trigger({ props })}
                  <!-- The same element with or without a selected tour, so it keeps the focus that the closing list returns when the first tour is chosen.
                     The top line describes the button in place of the tooltip, which exists only while it shows. -->
                  <button
                     {...props}
                     bind:this={listTrigger}
                     type="button"
                     class={lines === null
                        ? buttonVariants({ variant: 'outline', size: 'sm', class: 'max-w-full min-w-0 shrink font-normal' })
                        : buttonVariants({ variant: 'ghost', size: 'sm', class: 'max-w-full min-w-24 shrink justify-start gap-1.5 px-1.5 text-left font-normal' })}
                     aria-label={triggerName}
                     {...topLine === null ? {} : { 'aria-describedby': `${id}-place` }}
                  >
                     {#if lines === null}
                        <span class="truncate text-muted-foreground">Choose a tour</span>
                     {:else}
                        {#if selectedTour?.target.kind === 'repository'}
                           <BookOpen class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                        {:else}
                           <GitPullRequest class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                        {/if}
                        <!-- The two lines fill the button's height, 2rem, so the bar stays as tall with a tour open. -->
                        <span class="flex min-w-0 flex-col">
                           {#if lines.place !== null}
                              <!-- The place truncates, and the commit never does. -->
                              <span id="{id}-place" class="flex min-w-0 text-xs leading-3.5 text-muted-foreground" data-switcher-line="place"
                                 ><span class="min-w-0 truncate">{lines.place}</span>{#if lines.commit !== null}<span class="shrink-0"
                                       >&nbsp;·&nbsp;<span class="font-mono" data-tour-commit>{lines.commit}</span></span
                                    >{/if}</span
                              >
                           {/if}
                           <span class="min-w-0 truncate leading-4.5" data-switcher-line="title">{lines.title}</span>
                        </span>
                     {/if}
                     <ChevronDown class={lines === null ? 'size-3 shrink-0' : 'size-4 shrink-0 text-muted-foreground'} aria-hidden="true" />
                  </button>
               {/snippet}
            </WithTooltip>
         {/snippet}
      </Popover.Trigger>
      <Popover.Content align="start" class="scroll-box max-h-96 w-96 gap-0 p-0">
         <TourList {tours} allTours={everyTour} {hiddenCount} {showHidden} {loaded} {error} {selectedTourId} onselect={select} {onshowhidden} {onarchive} ondelete={askToDelete} />
      </Popover.Content>
   </Popover.Root>
   {#if targetUrl !== null}
      <TooltipButton tooltip={`${linkName}\n${targetUrl}`} variant="ghost" size="icon-sm" class="shrink-0" href={targetUrl} target="_blank" rel="noopener noreferrer" aria-label={linkName}>
         <ExternalLink aria-hidden="true" />
      </TooltipButton>
   {/if}
</div>

<!-- Each dive's list nests under it, as the family does. -->
{#snippet diveList(branches: DiveBranch[], labelledBy?: string)}
   <ul class="mt-1 list-disc pl-5" aria-labelledby={labelledBy}>
      {#each branches as branch (branch.tour.id)}
         <li class="wrap-anywhere">
            {tourName(branch.tour)}
            {#if branch.dives.length > 0}
               {@render diveList(branch.dives)}
            {/if}
         </li>
      {/each}
   </ul>
{/snippet}

<AlertDialog.Root bind:open={confirming}>
   <!-- The delete button left with the list, so the focus returns to the list's trigger. -->
   <AlertDialog.Content
      escapeKeydownBehavior={busy ? 'ignore' : 'close'}
      onCloseAutoFocus={(event) => {
         event.preventDefault();
         listTrigger?.focus();
      }}
   >
      <AlertDialog.Header>
         <AlertDialog.Title>Delete this tour?</AlertDialog.Title>
         <AlertDialog.Description>
            {#if deletedDiveCount === 0}
               The tour {deletedName} is deleted for good, with its plan, stops, threads, notes and answers. This cannot be undone. Claude's session can no longer work on it.
            {:else}
               The tour {deletedName} and {deletedDiveCount === 1 ? 'the dive' : `the ${deletedDiveCount} dives`} below it are deleted for good, each with its plan, stops, threads, notes and answers. This
               cannot be undone. Claude's session can no longer work on them.
            {/if}
         </AlertDialog.Description>
      </AlertDialog.Header>
      {#if deletedDiveCount > 0}
         <div class="text-sm" data-deleted-dives>
            <p id="{id}-dives" class="font-medium">Dives deleted with it</p>
            {@render diveList(shownDives, `${id}-dives`)}
            {#if unshownDiveCount > 0}
               <p class="mt-1 text-muted-foreground">and {unshownDiveCount === 1 ? '1 more dive' : `${unshownDiveCount} more dives`}</p>
            {/if}
         </div>
      {/if}
      {#if busy}
         <p id="{id}-deleting" class="text-sm text-muted-foreground" role="status">Deleting the tour…</p>
      {/if}
      {#if deleteError}
         <p class="text-sm text-destructive" role="alert">{deleteError}</p>
      {/if}
      <AlertDialog.Footer>
         <AlertDialog.Cancel disabled={busy} aria-describedby={busy ? `${id}-deleting` : undefined}>Cancel</AlertDialog.Cancel>
         <AlertDialog.Action variant="destructive" disabled={busy} aria-describedby={busy ? `${id}-deleting` : undefined} onclick={() => void confirmDelete()}>Delete tour</AlertDialog.Action>
      </AlertDialog.Footer>
   </AlertDialog.Content>
</AlertDialog.Root>
