<script lang="ts">
   import { tick } from 'svelte';
   import BookOpen from '@lucide/svelte/icons/book-open';
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import ChevronRight from '@lucide/svelte/icons/chevron-right';
   import ExternalLink from '@lucide/svelte/icons/external-link';
   import GitPullRequest from '@lucide/svelte/icons/git-pull-request';
   import { buttonVariants } from '$lib/components/ui/button';
   import * as AlertDialog from '$lib/components/ui/alert-dialog';
   import * as Popover from '$lib/components/ui/popover';
   import { tourName } from '../lib/tourFamily';
   import { repositoryHeading, splitLabel, type RepositoryHeading } from '../lib/tourLabel';
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
      breadcrumb = [],
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
      /** The selected dive's breadcrumb, from its root down to its parent, each step a button that opens that tour, or no tours for a tour that is not a dive. */
      breadcrumb?: readonly TourSummary[];
      onselect: (tourId: string) => void;
      onshowhidden: (show: boolean) => void;
      /** Archives a tour or unarchives it, and returns null once stored, or the reason the write failed. */
      onarchive: (tourId: string, archived: boolean) => Promise<string | null>;
      /** Deletes a tour for good, and returns null once it is gone, or the reason the delete failed. */
      ondelete: (tourId: string) => Promise<string | null>;
   } = $props();

   const id = $props.id();
   let open = $state(false);
   let anchor = $state<HTMLElement | null>(null);
   let listTrigger = $state<HTMLElement | null>(null);
   // The tour whose delete the dialog asks to confirm. The dialog lives outside the list's popover, which closes when the dialog opens.
   let deleting = $state<TourSummary | null>(null);
   let confirming = $state(false);
   let busy = $state(false);
   let deleteError = $state<string | null>(null);
   // The list trigger's name and tooltip. A null tooltip would render the trigger as a new element, which drops the focus that the closing list returns to it.
   const triggerName = $derived(selectedTour ? 'Choose another tour' : 'Choose a tour');
   // Only an https: URL becomes a link.
   const targetUrl = $derived(selectedTour && URL.canParse(selectedTour.target.url) && new URL(selectedTour.target.url).protocol === 'https:' ? selectedTour.target.url : null);
   const heading = $derived(selectedTour === null ? null : repositoryHeading(selectedTour));
   const deletedName = $derived(deleting === null ? '' : deleting.title === undefined ? `of ${deleting.label}` : `“${deleting.title}” of ${deleting.label}`);

   /** Moves the focus to the switcher's trigger, which stays in place when another tour opens. */
   export function focusSwitcher(): void {
      listTrigger?.focus();
   }

   // The step's button leaves with the breadcrumb or with the dive's title, so the focus moves to the switcher's trigger, as a choice in the list leaves it there.
   function openStep(tourId: string): void {
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

<div bind:this={anchor} class="flex max-w-full min-w-0 items-center gap-2">
   {#if selectedTour && heading !== null}
      {@render repositoryTour(selectedTour, heading)}
   {:else if selectedTour && targetUrl !== null}
      <WithTooltip text={`${selectedTour.label}\nOpen the pull request in a new tab\n${targetUrl}`}>
         {#snippet trigger({ props })}
            <!-- The underline shows without hover, so the label reads as a link. -->
            <a
               {...props}
               href={targetUrl}
               target="_blank"
               rel="noopener noreferrer"
               class="inline-flex h-8 min-w-0 items-center gap-1.5 rounded-sm underline decoration-muted-foreground/50 underline-offset-4 hover:decoration-current focus-visible:decoration-current focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden"
            >
               <GitPullRequest class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
               {@render label(selectedTour.label)}
               <ExternalLink class="size-3 shrink-0 text-muted-foreground" aria-hidden="true" />
            </a>
         {/snippet}
      </WithTooltip>
   {:else if selectedTour}
      <span class="flex min-w-0 items-center gap-1.5">
         <GitPullRequest class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
         <WithTooltip text={`${selectedTour.label}\n${selectedTour.target.url}`}>
            {#snippet trigger({ props })}
               <span {...props} class="flex min-w-0">{@render label(selectedTour.label)}</span>
            {/snippet}
         </WithTooltip>
      </span>
   {/if}
   <Popover.Root bind:open>
      <Popover.Trigger>
         {#snippet child({ props: popoverProps })}
            <WithTooltip text={triggerName} {...popoverProps}>
               {#snippet trigger({ props })}
                  <!-- The same element with or without a selected tour, so it keeps the focus that the closing list returns when the first tour is chosen. -->
                  <button
                     {...props}
                     bind:this={listTrigger}
                     type="button"
                     class={selectedTour ? buttonVariants({ variant: 'ghost', size: 'icon-sm' }) : buttonVariants({ variant: 'outline', size: 'sm', class: 'max-w-full min-w-0 shrink font-normal' })}
                     aria-label={triggerName}
                  >
                     {#if !selectedTour}
                        <span class="truncate text-muted-foreground">Choose a tour</span>
                     {/if}
                     <ChevronDown class={selectedTour ? 'size-4 shrink-0' : 'size-3 shrink-0'} />
                  </button>
               {/snippet}
            </WithTooltip>
         {/snippet}
      </Popover.Trigger>
      <!-- The list anchors to the whole switcher, so it opens below the label's left edge. -->
      <Popover.Content customAnchor={anchor} align="start" class="scroll-box max-h-96 w-96 gap-0 p-0">
         <TourList {tours} {hiddenCount} {showHidden} {loaded} {error} {selectedTourId} onselect={select} {onshowhidden} {onarchive} ondelete={askToDelete} />
      </Popover.Content>
   </Popover.Root>
</div>

<!-- The pull request number never truncates, and a narrow bar shows it alone, with the repository left to the accessible name and the tooltip. -->
{#snippet label(text: string)}
   {@const parts = splitLabel(text)}
   {#if parts === null}
      <span class="min-w-0 truncate">{text}</span>
   {:else}
      <span class="flex min-w-0">
         <span class="min-w-0 truncate @max-bar-repository:sr-only" data-label-part="repository">{parts.repository}</span>
         <!-- The space starts the number's line, so it leaves with the repository, and the link's underline runs through it. -->
         <span class="shrink-0 decoration-spaces" data-label-part="number"><span class="@max-bar-repository:hidden">&nbsp;</span>{parts.number}</span>
      </span>
   {/if}
{/snippet}

<!-- The title truncates first, and a narrow bar keeps the title alone, with the repository and the ref left to the screen reader and the title's tooltip. -->
{#snippet repositoryTour(tour: TourSummary, { title, repository, ref }: RepositoryHeading)}
   {@const place = ref === null ? repository : `${repository}@${ref}`}
   <span class="flex min-w-0 items-center gap-1.5" data-repository-heading>
      <BookOpen class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      {#if breadcrumb.length > 0}
         {@render diveBreadcrumb(title, place)}
      {:else}
         {@render headingTitle(title, place)}
      {/if}
      <span class="flex max-w-1/2 min-w-0 shrink-0 items-center text-sm text-muted-foreground @max-bar-repository:sr-only">
         {#if targetUrl !== null}
            <WithTooltip text={`Open the repository in a new tab\n${targetUrl}`}>
               {#snippet trigger({ props })}
                  <a
                     {...props}
                     href={targetUrl}
                     target="_blank"
                     rel="noopener noreferrer"
                     class="min-w-0 truncate rounded-sm underline decoration-muted-foreground/50 underline-offset-4 hover:decoration-current focus-visible:decoration-current focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden"
                     data-heading-part="repository">{repository}</a
                  >
               {/snippet}
            </WithTooltip>
         {:else}
            <WithTooltip text={tour.target.url}>
               {#snippet trigger({ props })}
                  <span {...props} class="min-w-0 truncate" data-heading-part="repository">{repository}</span>
               {/snippet}
            </WithTooltip>
         {/if}
         {#if ref !== null}
            <span class="min-w-0 truncate" data-heading-part="ref">@{ref}</span>
         {/if}
      </span>
   </span>
{/snippet}

{#snippet headingTitle(title: string, place: string)}
   <WithTooltip text={`${title}\n${place}`}>
      {#snippet trigger({ props })}
         <span {...props} class="min-w-0 truncate" data-heading-part="title">{title}</span>
      {/snippet}
   </WithTooltip>
{/snippet}

<!-- Each step before the dive's title truncates on its own. A narrow bar keeps only the parent's step, after an ellipsis that stands for the steps it hides. -->
{#snippet diveBreadcrumb(title: string, place: string)}
   <nav aria-label="Breadcrumb" class="flex min-w-0 items-center" data-breadcrumb>
      <ol class="flex min-w-0 items-center gap-1">
         {#each breadcrumb as step, index (step.id)}
            {@const parent = index === breadcrumb.length - 1}
            {#if parent && index > 0}
               <li class="hidden shrink-0 text-muted-foreground @max-bar-repository:flex" aria-hidden="true">…<ChevronRight class="size-3.5" /></li>
            {/if}
            <li class={['flex max-w-28 min-w-0 shrink items-center gap-1 text-sm', !parent && '@max-bar-repository:hidden']} data-breadcrumb-step>
               <WithTooltip text={`Open the tour ${tourName(step)}`}>
                  {#snippet trigger({ props })}
                     <button
                        {...props}
                        type="button"
                        class="h-8 min-w-0 cursor-pointer truncate rounded-sm text-muted-foreground underline decoration-muted-foreground/50 underline-offset-4 hover:text-foreground hover:decoration-current focus-visible:decoration-current focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden"
                        onclick={() => openStep(step.id)}>{tourName(step)}</button
                     >
                  {/snippet}
               </WithTooltip>
               <ChevronRight class="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
            </li>
         {/each}
         <li class="flex min-w-0 items-center" aria-current="page">{@render headingTitle(title, place)}</li>
      </ol>
   </nav>
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
            The tour {deletedName} is deleted for good, with its plan, stops, threads, notes and answers. This cannot be undone. Claude's session can no longer work on it.
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
