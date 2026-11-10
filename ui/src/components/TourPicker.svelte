<script lang="ts">
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import * as AlertDialog from '$lib/components/ui/alert-dialog';
   import * as Popover from '$lib/components/ui/popover';
   import type { TourSummary } from '../lib/types';
   import TourList from './TourList.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      tours,
      hiddenCount,
      showHidden,
      loaded,
      error,
      selectedTourId,
      selectedTour,
      onselect,
      onshowhidden,
      onarchive,
      ondelete,
   }: {
      /** The tours that the list shows. */
      tours: TourSummary[];
      /** The number of tours that the list hides while hidden tours do not show. */
      hiddenCount: number;
      showHidden: boolean;
      loaded: boolean;
      error: string | null;
      selectedTourId: string | null;
      /** The selected tour's summary, or null while no tour is selected or the list does not hold it yet. */
      selectedTour: TourSummary | null;
      onselect: (tourId: string) => void;
      onshowhidden: (show: boolean) => void;
      /** Archives a tour or unarchives it, and returns null once stored, or the reason the write failed. */
      onarchive: (tourId: string, archived: boolean) => Promise<string | null>;
      /** Deletes a tour for good, and returns null once it is gone, or the reason the delete failed. */
      ondelete: (tourId: string) => Promise<string | null>;
   } = $props();

   const id = $props.id();
   let open = $state(false);
   let switcher = $state<HTMLElement | null>(null);
   // The tour whose delete the dialog asks to confirm. The dialog lives outside the list's popover, which closes when the dialog opens.
   let deleting = $state<TourSummary | null>(null);
   let confirming = $state(false);
   let busy = $state(false);
   let deleteError = $state<string | null>(null);
   // Never null: a null text renders the trigger as a new element, which would drop the focus that closing the list returns to it.
   const tooltip = $derived(selectedTour ? `${selectedTour.label}\n${selectedTour.target.url}` : 'Choose a tour');

   function select(tourId: string) {
      open = false;
      onselect(tourId);
   }

   function askToDelete(tour: TourSummary) {
      open = false;
      deleting = tour;
      deleteError = null;
      confirming = true;
   }

   async function confirmDelete() {
      if (deleting === null || busy) {
         return;
      }

      busy = true;
      try {
         deleteError = await ondelete(deleting.id);
      } finally {
         busy = false;
      }

      if (deleteError === null) {
         confirming = false;
      }
   }
</script>

<Popover.Root bind:open>
   <Popover.Trigger>
      {#snippet child({ props: popoverProps })}
         <WithTooltip text={tooltip} {...popoverProps}>
            {#snippet trigger({ props })}
               <button
                  {...props}
                  bind:this={switcher}
                  type="button"
                  class="flex max-w-full min-w-0 items-center gap-1 rounded-md border px-2 py-1 hover:bg-accent focus-visible:border-ring"
                  aria-label={selectedTour ? `Tour: ${selectedTour.label}, choose another` : 'Choose a tour'}
               >
                  <span class={['truncate', !selectedTour && 'text-muted-foreground']}>{selectedTour ? selectedTour.label : 'Choose a tour'}</span>
                  <ChevronDown class="size-3 shrink-0" />
               </button>
            {/snippet}
         </WithTooltip>
      {/snippet}
   </Popover.Trigger>
   <Popover.Content align="start" class="scroll-box max-h-96 w-96 gap-0 p-0">
      <TourList {tours} {hiddenCount} {showHidden} {loaded} {error} {selectedTourId} onselect={select} {onshowhidden} {onarchive} ondelete={askToDelete} />
   </Popover.Content>
</Popover.Root>

<AlertDialog.Root bind:open={confirming}>
   <!-- The delete button left with the list, so the focus returns to the switcher. -->
   <AlertDialog.Content
      escapeKeydownBehavior={busy ? 'ignore' : 'close'}
      onCloseAutoFocus={(event) => {
         event.preventDefault();
         switcher?.focus();
      }}
   >
      <AlertDialog.Header>
         <AlertDialog.Title>Delete this tour?</AlertDialog.Title>
         <AlertDialog.Description>
            The tour of {deleting?.label} is deleted for good, with its plan, stops, threads, notes and answers. This cannot be undone. Claude's session can no longer work on it.
         </AlertDialog.Description>
      </AlertDialog.Header>
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
