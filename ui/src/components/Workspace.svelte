<script lang="ts">
   import type { Snippet } from 'svelte';
   import * as Resizable from '$lib/components/ui/resizable';
   import { layoutStorage, type FoldableId, type WorkspaceLayout } from '../lib/workspaceLayout.svelte';
   import PanelActions from './PanelActions.svelte';

   let {
      layout,
      left,
      center,
      stop,
      threads,
   }: {
      layout: WorkspaceLayout;
      /** The left column, which renders its own header with the panel's actions. */
      left: Snippet;
      /** The diff, which renders its own header with the panel's actions. */
      center: Snippet;
      stop: Snippet;
      threads: Snippet;
   } = $props();

   type Pane = ReturnType<typeof Resizable.Pane>;
   let leftPane = $state<Pane>();
   let rightPane = $state<Pane>();
   let stopPane = $state<Pane>();
   let threadsPane = $state<Pane>();

   // isCollapsed reads the group's layout, so the folded state follows every way a pane folds, including Enter on a splitter, which calls no pane callback.
   function register(id: FoldableId, pane: () => Pane | undefined) {
      $effect(() => {
         const mounted = pane();
         return mounted && layout.register(id, mounted);
      });
      $effect(() => layout.setFolded(id, pane()?.isCollapsed() ?? false));
   }

   register('left', () => leftPane);
   register('right', () => rightPane);
   register('stop', () => stopPane);
   register('threads', () => threadsPane);

   const rightPanels: { id: 'stop' | 'threads'; title: string; label: string; closes: { column: 'right'; title: string } | null }[] = [
      { id: 'stop', title: 'Stop', label: 'the stop', closes: { column: 'right', title: 'the stop and the threads' } },
      { id: 'threads', title: 'Threads', label: 'the threads', closes: null },
   ];
</script>

<!-- A folded panel's pane has no height, so its header shows outside the pane group, above or below the neighbor that took its space. -->
{#snippet header(panel: (typeof rightPanels)[number])}
   <div class="flex shrink-0 items-center gap-2 border-b bg-card py-1 pr-2 pl-4">
      <h2 class="flex-1 text-xs font-semibold tracking-wider text-muted-foreground uppercase">{panel.title}</h2>
      <PanelActions {layout} panel={panel.id} title={panel.label} foldable={panel.id} closes={panel.closes} />
   </div>
{/snippet}

<!-- A folded panel stays mounted, so the stop keeps the sections the reviewer opened. -->
{#snippet body(panel: (typeof rightPanels)[number])}
   <section class="flex h-full flex-col" aria-label={panel.title} hidden={layout.folded[panel.id]}>
      {@render header(panel)}
      <div class="scroll-box min-h-0 flex-1">
         {@render (panel.id === 'stop' ? stop : threads)()}
      </div>
   </section>
{/snippet}

<Resizable.PaneGroup direction="horizontal" autoSaveId="lotsen.columns" storage={layoutStorage} class="min-h-0 flex-1">
   <Resizable.Pane bind:this={leftPane} id="left-column" order={1} defaultSize={22} minSize={12} collapsible collapsedSize={0} hidden={!layout.shows('tour')}>
      {@render left()}
   </Resizable.Pane>
   <Resizable.Handle hidden={layout.maximized !== null} aria-label="Resize the left column" />
   <Resizable.Pane id="center-column" order={2} defaultSize={50} minSize={20} hidden={!layout.shows('diff')}>
      {@render center()}
   </Resizable.Pane>
   <Resizable.Handle hidden={layout.maximized !== null} aria-label="Resize the right column" />
   <Resizable.Pane bind:this={rightPane} id="right-column" order={3} defaultSize={28} minSize={15} collapsible collapsedSize={0} hidden={!layout.shows('stop') && !layout.shows('threads')}>
      <div class="flex h-full flex-col bg-card">
         {#if layout.folded.stop && layout.shows('stop')}
            {@render header(rightPanels[0]!)}
         {/if}
         <Resizable.PaneGroup direction="vertical" autoSaveId="lotsen.rightColumn" storage={layoutStorage} class="min-h-0 flex-1">
            <Resizable.Pane bind:this={stopPane} id="stop-panel" order={1} defaultSize={60} minSize={15} collapsible collapsedSize={0} hidden={!layout.shows('stop')}>
               {@render body(rightPanels[0]!)}
            </Resizable.Pane>
            <Resizable.Handle hidden={layout.maximized !== null} aria-label="Resize the stop and the threads" />
            <Resizable.Pane bind:this={threadsPane} id="threads-panel" order={2} defaultSize={40} minSize={15} collapsible collapsedSize={0} hidden={!layout.shows('threads')}>
               {@render body(rightPanels[1]!)}
            </Resizable.Pane>
         </Resizable.PaneGroup>
         {#if layout.folded.threads && layout.shows('threads')}
            {@render header(rightPanels[1]!)}
         {/if}
      </div>
   </Resizable.Pane>
</Resizable.PaneGroup>
