<!--
   The top bar's Move button of an open repository tour, which shows while its ref can move, and the dialog that moves the tour and the dives that the reviewer ticks to the latest commit of the ref.
   The dialog checks for a move each time it opens, and checks again after a refusal of the move, so it shows what a move takes now.
-->
<script lang="ts">
   import { tick } from 'svelte';
   import FastForward from '@lucide/svelte/icons/fast-forward';
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import { Button, buttonVariants } from '$lib/components/ui/button';
   import * as Dialog from '$lib/components/ui/dialog';
   import { moveBranches, tickDive, tickedDives, type MoveBranch, type MoveCheckOutcome, type MoveOffer } from '../lib/tourMoves';
   import WithTooltip from './WithTooltip.svelte';

   let {
      commit,
      offer,
      refName,
      oncheck,
      onmove,
      onfocuslost,
   }: {
      /** The commit that the tour shows, its snapshot's head. */
      commit: string;
      /** Whether the tour's ref can move, or undefined before a check. The Move button shows only for a movable ref. */
      offer: MoveOffer | undefined;
      /** The tour's ref, or null for the default branch. */
      refName: string | null;
      /** Checks the tour for a move. */
      oncheck: () => Promise<MoveCheckOutcome>;
      /** Moves the tour and the dives with the IDs to the commit, and returns null once moved, or the reason the move was refused. */
      onmove: (commit: string, diveIds: readonly string[]) => Promise<string | null>;
      /** Moves the focus to a stable control of the bar, once the dialog closes after a check that found the ref cannot move, which takes the Move button away. */
      onfocuslost: () => void;
   } = $props();

   const id = $props.id();
   const moveName = 'Move the tour to the latest commit…';
   const short = (full: string) => full.slice(0, 8);
   const place = $derived(refName === null ? 'the default branch' : refName);
   let open = $state(false);
   let moveButton = $state<HTMLElement | null>(null);
   let confirmButton = $state<HTMLElement | null>(null);
   let outcome = $state.raw<MoveCheckOutcome | null>(null);
   let checking = $state(false);
   let moving = $state(false);
   let refusal = $state<string | null>(null);
   // The dives that the reviewer unticked, which a check again keeps, so a dive that a new check offers starts ticked.
   let unticked = $state.raw<ReadonlySet<string>>(new Set());
   const checked = $derived(outcome !== null && 'checked' in outcome ? outcome.checked : null);
   const dives = $derived(checked?.dives ?? []);
   const branches = $derived(moveBranches(dives));
   const ticked = $derived(new Set(tickedDives(dives, unticked)));
   const busy = $derived(checking || moving);
   // A tour at the latest commit moves only through its dives, so with every dive unticked a move would refuse.
   const nothingTicked = $derived(checked?.move === 'available' && checked.current === checked.latest && ticked.size === 0);

   // Counts the checks, so the answer of a check that a later one replaced, as after a close and a reopen, changes nothing.
   let checks = 0;

   async function check(): Promise<void> {
      const request = ++checks;
      checking = true;
      let answer: MoveCheckOutcome;
      try {
         answer = await oncheck();
      } finally {
         if (request === checks) {
            checking = false;
         }
      }

      if (request !== checks) {
         return;
      }

      outcome = answer;

      // The check's controls replace the status line, so the focus moves to the move it offers.
      if (checked?.move === 'available') {
         await tick();
         confirmButton?.focus();
      }
   }

   function start(): void {
      outcome = null;
      refusal = null;
      unticked = new Set();
      open = true;
      void check();
   }

   async function move(): Promise<void> {
      if (checked?.move !== 'available' || busy || nothingTicked) {
         return;
      }

      moving = true;
      refusal = null;
      let refused: string | null;
      try {
         refused = await onmove(checked.latest, [...ticked]);
      } finally {
         moving = false;
      }

      if (refused === null) {
         open = false;
         return;
      }

      refusal = refused;
      await check();
   }

   function setTicked(tourId: string, on: boolean): void {
      unticked = on ? tickDive(dives, unticked, tourId) : new Set([...unticked, tourId]);
   }
</script>

{#if offer === 'movable' || open}
   <!-- The switcher's top line shows the commit, and this description gives it to the button for a screen reader. -->
   <span id="{id}-commit" class="sr-only">The tour shows commit {short(commit)}</span>
   <WithTooltip text={moveName}>
      {#snippet trigger({ props })}
         <Button {...props} bind:ref={moveButton} variant="ghost" size="icon-sm" class="shrink-0" aria-label={moveName} aria-describedby="{id}-commit" onclick={start}>
            <FastForward aria-hidden="true" />
         </Button>
      {/snippet}
   </WithTooltip>
{/if}

{#snippet branchList(list: MoveBranch[], labelledBy?: string)}
   <ul class={labelledBy === undefined ? 'mt-1 flex flex-col gap-1 pl-6' : 'flex flex-col gap-1'} aria-labelledby={labelledBy}>
      {#each list as branch (branch.dive.tourId)}
         <li>
            <label class="flex items-start gap-2">
               <input
                  type="checkbox"
                  class="mt-0.5 size-4 shrink-0 accent-primary"
                  checked={ticked.has(branch.dive.tourId)}
                  disabled={busy}
                  onchange={(event) => setTicked(branch.dive.tourId, event.currentTarget.checked)}
               />
               <span class="wrap-anywhere">{branch.dive.title}</span>
            </label>
            {#if branch.dives.length > 0}
               {@render branchList(branch.dives)}
            {/if}
         </li>
      {/each}
   </ul>
{/snippet}

<Dialog.Root bind:open>
   <Dialog.Content
      escapeKeydownBehavior={moving ? 'ignore' : 'close'}
      interactOutsideBehavior={moving ? 'ignore' : 'close'}
      showCloseButton={false}
      onCloseAutoFocus={(event) => {
         event.preventDefault();
         if (moveButton?.isConnected === true && offer !== 'fixed') {
            moveButton.focus();
         } else {
            onfocuslost();
         }
      }}
   >
      <Dialog.Header>
         <Dialog.Title>Move to the latest commit</Dialog.Title>
         <Dialog.Description>
            {#if checked === null}
               The tour moves to the latest commit of {place}, with the dives that you tick. Claude then revises the stops that the move makes stale.
            {:else if checked.move === 'atLatest'}
               The tour and its dives are already at the latest commit of {place}, <span class="font-mono">{short(checked.latest)}</span>.
            {:else if checked.move === 'tag'}
               The tour shows the tag {place}, which names one commit for good, so it has no move.
            {:else if checked.move === 'commit'}
               The tour shows one commit, so it has no move.
            {:else if checked.current === checked.latest}
               The tour is at the latest commit of {place}, and the dives that you tick move to it. Claude then revises the stops that the move makes stale.
            {:else}
               The tour moves to the latest commit of {place}, with the dives that you tick. Claude then revises the stops that the move makes stale.
            {/if}
         </Dialog.Description>
      </Dialog.Header>
      {#if checked?.move === 'available'}
         <dl class="flex flex-col gap-1" data-move-commits>
            <div class="flex gap-4">
               <dt class="w-28 text-muted-foreground">Current commit</dt>
               <dd class="font-mono">{short(checked.current)}</dd>
            </div>
            <div class="flex gap-4">
               <dt class="w-28 text-muted-foreground">Latest commit</dt>
               <dd class="font-mono">{short(checked.latest)}</dd>
            </div>
         </dl>
         {#if branches.length > 0}
            <div data-move-dives>
               <p id="{id}-dives" class="mb-1 font-medium">Dives that move with it</p>
               {@render branchList(branches, `${id}-dives`)}
            </div>
         {/if}
      {/if}
      {#if checking}
         <p class="flex items-center gap-1.5 text-muted-foreground" role="status"><LoaderCircle class="size-4 animate-spin" aria-hidden="true" />Checking {place} for its latest commit…</p>
      {:else if moving}
         <p class="flex items-center gap-1.5 text-muted-foreground" role="status"><LoaderCircle class="size-4 animate-spin" aria-hidden="true" />Moving the tour…</p>
      {/if}
      {#if outcome !== null && 'failure' in outcome}
         <p class="text-destructive" role="alert">{outcome.failure}</p>
      {/if}
      {#if refusal !== null}
         <p class="text-destructive" role="alert" data-move-refusal>{refusal}</p>
      {/if}
      {#if nothingTicked}
         <p id="{id}-nothing" class="text-muted-foreground">Tick a dive to move. The tour is at the latest commit already.</p>
      {/if}
      <Dialog.Footer>
         {#if checked?.move === 'available'}
            <Dialog.Close class={buttonVariants({ variant: 'outline' })} disabled={moving}>Cancel</Dialog.Close>
            <!-- The button stays focusable while the move or a check runs, or while nothing would move, so the focus stays on it, and move() ignores its click. -->
            <Button
               bind:ref={confirmButton}
               class={busy || nothingTicked ? 'opacity-50' : undefined}
               aria-disabled={busy || nothingTicked}
               aria-busy={moving}
               aria-describedby={nothingTicked ? `${id}-nothing` : undefined}
               onclick={() => void move()}>Move</Button
            >
         {:else}
            {#if outcome !== null && 'failure' in outcome}
               <Button variant="outline" disabled={checking} onclick={() => void check()}>Check again</Button>
            {/if}
            <Dialog.Close class={buttonVariants({ variant: checked === null ? 'outline' : 'default' })}>Close</Dialog.Close>
         {/if}
      </Dialog.Footer>
   </Dialog.Content>
</Dialog.Root>
