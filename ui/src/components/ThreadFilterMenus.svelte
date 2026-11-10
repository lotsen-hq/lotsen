<!--
   The Threads panel's thread filter as two menu buttons. Show checks the thread states and the tour actions, each toggle leaving the menu open, and its button names what shows. Where picks one
   place: anywhere, the current stop or file, or one stop or file that a thread is about. Each state, stop and file shows how many threads choosing it would list.
-->
<script lang="ts">
   import ChevronDown from '@lucide/svelte/icons/chevron-down';
   import MapPin from '@lucide/svelte/icons/map-pin';
   import { buttonVariants } from '$lib/components/ui/button';
   import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
   import { anchorLabel, stopNumberLabel } from '../lib/labels';
   import { shownLabel, threadStates, withState, type ThreadPlace } from '../lib/threadFilters';
   import type { ThreadsView } from '../lib/threadsView.svelte';
   import { threadStateLabels } from '../lib/threadView';
   import type { Plan } from '../lib/types';

   let {
      view,
      plan,
   }: {
      /** The tour's threads view, which holds the filter, its choices and where the reviewer is. */
      view: ThreadsView;
      /** The tour's plan, which names the stops, or null while the tour has none. */
      plan: Plan | null;
   } = $props();

   const id = $props.id();
   const triggerClass = `${buttonVariants({ variant: 'outline', size: 'sm' })} min-w-0 max-w-56 gap-1.5 aria-expanded:border-ring`;

   const applied = $derived(view.applied);
   const lastState = $derived(applied.states.length === 1 ? applied.states[0] : null);
   const currentStopId = $derived(view.here.stopId);
   const currentFile = $derived(view.here.file === null ? null : (view.here.file.head ?? view.here.file.base));

   // Each place that the menu offers, by the value of its radio item.
   const places = $derived(
      new Map<string, ThreadPlace>([
         ['anywhere', { kind: 'anywhere' }],
         ['currentStop', { kind: 'currentStop' }],
         ['currentFile', { kind: 'currentFile' }],
         ...view.options.stopIds.map((stopId): [string, ThreadPlace] => [`stop:${stopId}`, { kind: 'stop', stopId }]),
         ...view.options.paths.map((path): [string, ThreadPlace] => [`file:${path}`, { kind: 'file', path }]),
      ]),
   );
   const chosenPlace = $derived(placeValue(applied.where));

   function placeValue(where: ThreadPlace): string {
      return where.kind === 'stop' ? `stop:${where.stopId}` : where.kind === 'file' ? `file:${where.path}` : where.kind;
   }

   function placeLabel(where: ThreadPlace): string {
      switch (where.kind) {
         case 'anywhere':
            return 'Anywhere';
         case 'currentStop':
            return 'Current stop';
         case 'currentFile':
            return 'Current file';
         case 'stop':
            return anchorLabel({ kind: 'stop', stopId: where.stopId }, plan);
         case 'file':
            return fileName(where.path);
      }
   }

   function fileName(path: string): string {
      return path.slice(path.lastIndexOf('/') + 1);
   }

   function choosePlace(value: string): void {
      const where = places.get(value);
      if (where !== undefined) {
         view.choose({ ...applied, where });
      }
   }
</script>

<DropdownMenu.Root>
   <DropdownMenu.Trigger class={triggerClass}>
      <span class="sr-only">Show:</span>
      <span class="truncate">{shownLabel(applied)}</span>
      <ChevronDown aria-hidden="true" />
   </DropdownMenu.Trigger>
   <DropdownMenu.Content align="start" class="w-64">
      {#each threadStates as state (state)}
         <DropdownMenu.CheckboxItem
            closeOnSelect={false}
            aria-disabled={state === lastState ? 'true' : undefined}
            aria-describedby={state === lastState ? `${id}-last-state` : undefined}
            onSelect={(event) => state === lastState && event.preventDefault()}
            bind:checked={() => applied.states.includes(state), (checked) => view.choose(withState(applied, state, checked))}
         >
            <span class="truncate">{threadStateLabels[state]}</span>
            <span class="ml-auto text-xs text-muted-foreground tabular-nums">{view.count({ ...applied, states: [state] })}</span>
         </DropdownMenu.CheckboxItem>
      {/each}
      {#if lastState !== null}
         <p id="{id}-last-state" class="mx-2 mt-0.5 mb-1 text-xs text-muted-foreground">At least one state stays shown.</p>
      {/if}
      <DropdownMenu.Separator />
      <DropdownMenu.CheckboxItem closeOnSelect={false} bind:checked={() => applied.actions, (actions) => view.choose({ ...applied, actions })}>Tour actions and decisions</DropdownMenu.CheckboxItem>
   </DropdownMenu.Content>
</DropdownMenu.Root>
<DropdownMenu.Root>
   <DropdownMenu.Trigger class={triggerClass}>
      <MapPin aria-hidden="true" />
      <span class="sr-only">Where:</span>
      <span class="truncate">{placeLabel(applied.where)}</span>
      <ChevronDown aria-hidden="true" />
   </DropdownMenu.Trigger>
   <DropdownMenu.Content align="start" class="w-72">
      <DropdownMenu.RadioGroup value={chosenPlace} onValueChange={choosePlace}>
         <DropdownMenu.RadioItem value="anywhere">Anywhere</DropdownMenu.RadioItem>
         <DropdownMenu.RadioItem value="currentStop">
            <span class="truncate">
               Current stop
               {#if currentStopId !== null}
                  <span class="text-muted-foreground">· {stopNumberLabel(currentStopId, plan)}</span>
               {/if}
            </span>
         </DropdownMenu.RadioItem>
         <DropdownMenu.RadioItem value="currentFile">
            <span class="truncate">
               Current file
               {#if currentFile !== null}
                  <span class="text-muted-foreground">· {fileName(currentFile)}</span>
               {/if}
            </span>
         </DropdownMenu.RadioItem>
      </DropdownMenu.RadioGroup>
      {#if view.options.stopIds.length > 0 || view.options.paths.length > 0}
         <DropdownMenu.Separator />
      {/if}
      {#if view.options.stopIds.length > 0}
         <DropdownMenu.RadioGroup value={chosenPlace} onValueChange={choosePlace}>
            <DropdownMenu.GroupHeading>Stops</DropdownMenu.GroupHeading>
            {#each view.options.stopIds as stopId (stopId)}
               <DropdownMenu.RadioItem value="stop:{stopId}">
                  <span class="truncate">{anchorLabel({ kind: 'stop', stopId }, plan)}</span>
                  <span class="ml-auto text-xs text-muted-foreground tabular-nums">{view.count({ ...applied, where: { kind: 'stop', stopId } })}</span>
               </DropdownMenu.RadioItem>
            {/each}
         </DropdownMenu.RadioGroup>
      {/if}
      {#if view.options.paths.length > 0}
         <DropdownMenu.RadioGroup value={chosenPlace} onValueChange={choosePlace}>
            <DropdownMenu.GroupHeading>Files</DropdownMenu.GroupHeading>
            {#each view.options.paths as path (path)}
               <DropdownMenu.RadioItem value="file:{path}">
                  <span class="truncate">{path}</span>
                  <span class="ml-auto text-xs text-muted-foreground tabular-nums">{view.count({ ...applied, where: { kind: 'file', path } })}</span>
               </DropdownMenu.RadioItem>
            {/each}
         </DropdownMenu.RadioGroup>
      {/if}
   </DropdownMenu.Content>
</DropdownMenu.Root>
