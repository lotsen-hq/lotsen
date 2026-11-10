<script lang="ts">
   import { mount, unmount, untrack } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import type { KnownDiff, PickedAnchor, PickHandler } from '../lib/viewState';
   import type { LineFocus } from '../lib/openStop.svelte';
   import type { DiffLayout } from '../lib/diffLayout';
   import { editorSurfaces, type EditorSurface } from '../lib/editor/surface';
   import type { ExplanationView } from '../lib/explanationView.svelte';
   import { fileMarkLabels, formatSize } from '../lib/labels';
   import { InlineHost } from '../lib/inlineHost.svelte';
   import { paneSplit } from '../lib/paneSplit.svelte';
   import { type FilePaths } from '../lib/pin';
   import { noPassageDecorations, type PassageDecorations } from '../lib/passageMarks';
   import { codeTarget } from '../lib/selectionAsk';
   import type { ThreadMarker } from '../lib/threadView';
   import type { FileMark } from '../lib/types';
   import DiffHeading from './DiffHeading.svelte';
   import PaneSplitter from './PaneSplitter.svelte';
   import SelectionAsk from './SelectionAsk.svelte';

   let {
      path,
      oldPath = null,
      known,
      lineEndingsOnly = false,
      mark = null,
      layout = 'sideBySide',
      focus = null,
      decorations = noPassageDecorations,
      explanation = null,
      stacked = false,
      outsideStop = false,
      onloadlarge = () => {},
      onask = null,
      onopenthread = () => {},
      fileAnchor = null,
      fileThreads = [],
   }: {
      path: string | null;
      oldPath?: string | null;
      known: KnownDiff | null;
      /** Whether the file's only change is its line endings, which collapses its diff. */
      lineEndingsOnly?: boolean;
      /** Claude's mark on the file, which collapses its diff, or null for none. */
      mark?: FileMark | null;
      layout?: DiffLayout;
      /** The line to scroll to once the file's editors show it, or null to leave the scroll where the editors start. */
      focus?: LineFocus | null;
      /** The decorations of the file's passages, which the editors follow without remounting. */
      decorations?: PassageDecorations;
      /** The page's explanation, which the editors show below its inline passage when that passage is in this file, or null for none. */
      explanation?: ExplanationView | null;
      /**
       * Whether the file sits in a stack of files that scrolls as one, so the editors grow to their content and the file shows a header with its path. A single file's path shows in the diff toolbar.
       * A mark or a line-endings-only change does not collapse a stacked diff, because the stack shows the file for its passages.
       */
      stacked?: boolean;
      /** Whether the header says that the reviewer added the file from outside the open stop. */
      outsideStop?: boolean;
      onloadlarge?: () => void;
      /** Hands what the reviewer selected or picked in the file to the Threads panel, or null when the page offers no ask. */
      onask?: PickHandler | null;
      /** Opens a thread in the Threads panel, by its ID, when the reviewer activates its marker. */
      onopenthread?: (threadId: string) => void;
      /** The anchor of the file as a whole, which its stacked header asks about, or null for none. */
      fileAnchor?: Extract<PickedAnchor, { kind: 'file' }> | null;
      /** The markers of the threads asked about the file, which its stacked header shows. */
      fileThreads?: readonly ThreadMarker[];
   } = $props();

   let host = $state<HTMLDivElement>();
   let mountFailed = $state(false);
   const surfaces = editorSurfaces();
   /** The editors that show the file's texts now, with the file's paths as they were when they mounted, or null while none do. */
   let mounted = $state.raw<{ editor: EditorSurface; path: string; paths: FilePaths } | null>(null);
   const collapsible = $derived(!stacked && (lineEndingsOnly || mark !== null));
   // The path of the open file when its diff shows expanded. Each file the reviewer opens starts collapsed when it is collapsible.
   let expandedPath = $state<string | null>(null);
   let previousPath: string | null = null;
   const collapsed = $derived(collapsible && expandedPath !== path);

   // A file that opened expanded stays expanded when a mark arrives, so the diff does not close under the reviewer. A passage or a code reference into the file expands it.
   $effect.pre(() => {
      const current = path;
      if (current !== previousPath) {
         previousPath = current;
         expandedPath = null;
      }

      if (current !== null && (!collapsible || focus?.path === current)) {
         expandedPath = current;
      }
   });

   // A diff that belongs to another file than the selected one is not shown.
   const shown = $derived(known !== null && known.path === path ? known : null);
   const texts = $derived(!collapsed && shown?.state === 'loaded' && shown.diff.content === 'text' ? { path: shown.path, diff: shown.diff } : null);

   // A derived value signals only a change of the layout, even when the parent replaces every prop at once, as the test renderer does, so a change of another prop does not remount the editors.
   const mountedLayout = $derived(layout);

   // Each file and layout gets a new editor, and the cleanup destroys the one before it.
   $effect(() => {
      const parent = host;
      const current = texts;
      const currentLayout = mountedLayout;
      mountFailed = false;
      if (!parent || current === null) {
         return;
      }

      const base = untrack(() => oldPath) ?? current.path;

      let editor: EditorSurface | null = null;
      const canceled = new AbortController();
      surfaces.diff({ parent, path: current.path, layout: currentLayout, diff: current.diff, signal: canceled.signal }).then(
         (surface) => {
            // A teardown can run after the surface is built and before this callback, so the callback releases that surface.
            if (canceled.signal.aborted) {
               surface?.destroy();
               return;
            }

            if (surface === null) {
               return;
            }

            editor = surface;
            const paths = { base: current.diff.baseText === null ? null : base, head: current.diff.headText === null ? null : current.path };
            mounted = { editor, path: current.path, paths };
         },
         () => {
            if (!canceled.signal.aborted) {
               mountFailed = true;
            }
         },
      );
      return () => {
         canceled.abort();
         mounted = null;
         editor?.destroy();
      };
   });

   $effect(() => {
      mounted?.editor.showPassages(decorations);
   });

   const split = paneSplit();

   // @codemirror/merge 6.12.3 changes the row only in `reconfigure` and for revert controls, and the viewer uses neither, so the splitter stays where it is mounted.
   $effect(() => {
      const panes = mounted?.editor.panes;
      if (!panes) {
         return;
      }

      const splitter = mount(PaneSplitter, { target: panes.row, anchor: panes.head, props: { split, panes } });
      return () => void unmount(splitter);
   });

   const inline = new InlineHost(
      () => (path === null ? null : explanation),
      () => ({ base: oldPath ?? path, head: path }),
   );

   $effect(() => {
      const current = mounted;
      void inline.key;
      return untrack(() => inline.show(current?.editor ?? null));
   });

   // A selection asks about the file that the editors show, which a new file's load does not change before they go.
   const askTarget = $derived.by(() => {
      const current = mounted;
      return current === null || onask === null ? null : (range: Range) => codeTarget(current.editor.selection(range), current.paths, current.editor);
   });

   /** Opens the thread of a marker that the editors draw, which carries its ID. */
   function openMarker(event: Event): void {
      const marker = event.target instanceof Element ? event.target.closest<HTMLElement>('.lotsen-threadMarkers [data-thread-id]') : null;
      if (marker !== null && host?.contains(marker)) {
         onopenthread(marker.dataset.threadId!);
      }
   }

   // Each new editor and each new focus scrolls once, so a layout change keeps the focused line in view.
   $effect(() => {
      const current = mounted;
      const target = focus;
      if (current === null || target === null || target.path !== current.path) {
         return;
      }

      current.editor.focus({ side: target.side, line: target.line });
   });
</script>

<div class={['relative flex flex-col', !stacked && 'h-full min-h-0']}>
   {#if path === null}
      <p class="p-4 text-muted-foreground">Select a changed file to see its diff.</p>
   {:else}
      {#if stacked}
         <header class="flex items-center gap-4 border-b bg-card px-4 py-1.5">
            <DiffHeading {path} {oldPath} {mark} {outsideStop} threads={fileThreads} {onopenthread} onask={onask !== null && fileAnchor !== null ? () => onask(fileAnchor, null) : null} />
         </header>
      {/if}
      {#if collapsed}
         <div class="p-4 text-muted-foreground *:mb-1">
            {#if mark !== null}
               <p>Claude marked this file as {fileMarkLabels[mark.mark]}, so the diff is collapsed.</p>
               <p class="text-foreground">{mark.reason}</p>
            {/if}
            {#if lineEndingsOnly}
               <p>Only the line endings changed, so the diff is collapsed.</p>
            {/if}
            <Button variant="outline" size="sm" class="mt-2" onclick={() => (expandedPath = path)}>Show the diff</Button>
         </div>
      {:else if shown === null || shown.state === 'loading'}
         <p class="p-4 text-muted-foreground">Loading the diff…</p>
      {:else if shown.state === 'notPinned' && shown.pin.state === 'pinning'}
         <p class="p-4 text-muted-foreground">Pinning the pull request to its commits…</p>
      {:else if shown.state === 'notPinned'}
         <div class="p-4 text-destructive *:mb-1" role="alert">
            <p>The pull request could not be pinned.</p>
            {#if shown.pin.state === 'failed'}
               <p>{shown.pin.reason}</p>
            {/if}
         </div>
      {:else if shown.state === 'failed'}
         <div class="p-4 text-destructive *:mb-1" role="alert">
            <p>{shown.message}</p>
            {#if shown.reason}
               <p>{shown.reason}</p>
            {/if}
         </div>
      {:else if shown.diff.content === 'binary'}
         <p class="p-4 text-muted-foreground">Binary changed. The file has no text to show.</p>
      {:else if shown.diff.content === 'large'}
         <div class="p-4 text-muted-foreground *:mb-1">
            <p>This file is {formatSize(shown.diff.size)}, so its diff is collapsed.</p>
            <Button variant="outline" size="sm" class="mt-2" onclick={onloadlarge}>Load the diff</Button>
         </div>
      {:else if mountFailed}
         <p class="p-4 text-destructive" role="alert">The diff could not be shown.</p>
      {/if}
      <!-- The markers are buttons, which take the keys and the clicks: the editors' element only delegates them. -->
      <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
      <div class={['editor', !stacked && 'min-h-0 flex-1']} bind:this={host} hidden={texts === null || mountFailed} onclick={openMarker}></div>
      {#if askTarget !== null && onask !== null}
         <SelectionAsk within={host} targetOf={askTarget} {onask} />
      {/if}
   {/if}
</div>
