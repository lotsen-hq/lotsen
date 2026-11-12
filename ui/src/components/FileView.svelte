<script lang="ts">
   import { untrack } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import type { KnownText, PickHandler } from '../lib/viewState';
   import type { LineFocus } from '../lib/openStop.svelte';
   import { editorSurfaces, type EditorSurface } from '../lib/editor/surface';
   import type { ExplanationView } from '../lib/explanationView.svelte';
   import { atSide, noPassageDecorations, type PassageDecorations } from '../lib/passageMarks';
   import { formatSize } from '../lib/labels';
   import { InlineHost } from '../lib/inlineHost.svelte';
   import { codeTarget } from '../lib/selectionAsk';
   import type { ThreadMarker } from '../lib/threadView';
   import type { FileSide } from '../lib/types';
   import FileHeading from './FileHeading.svelte';
   import SelectionAsk from './SelectionAsk.svelte';

   let {
      known,
      focus = null,
      decorations = noPassageDecorations,
      explanation = null,
      stacked = false,
      outsideStop = false,
      onloadlarge = () => {},
      onask = null,
      onopenthread = () => {},
      fileThreads = [],
   }: {
      known: KnownText;
      /** The line to scroll to once the editor shows the file, or null to leave the scroll at the top. */
      focus?: LineFocus | null;
      /** The decorations of the file's passages, which the editor follows without remounting. */
      decorations?: PassageDecorations;
      /** The page's explanation, which the editor shows below its inline passage when that passage is in this file, or null for none. */
      explanation?: ExplanationView | null;
      /** Whether the file sits in a stack of files that scrolls as one, so the editor grows to its content and the file shows a header with its path. A single file's path shows in the diff toolbar. */
      stacked?: boolean;
      /** Whether the header says that the reviewer added the file from outside the open stop. */
      outsideStop?: boolean;
      onloadlarge?: () => void;
      /** Hands what the reviewer selected or picked in the file to the Threads panel, or null when the page offers no ask. */
      onask?: PickHandler | null;
      /** Opens a thread in the Threads panel, by its ID, when the reviewer activates its marker. */
      onopenthread?: (threadId: string) => void;
      /** The markers of the threads asked about the file, which its stacked header shows. */
      fileThreads?: readonly ThreadMarker[];
   } = $props();

   let host = $state<HTMLDivElement>();
   let mountFailed = $state(false);
   const surfaces = editorSurfaces();
   /** The editor that shows the file now, or null while none does. */
   let mounted = $state.raw<{ editor: EditorSurface; side: FileSide; path: string } | null>(null);
   const loaded = $derived(known.state === 'loaded' ? known.text : null);
   const text = $derived(loaded?.content === 'text' ? { side: loaded.side, path: loaded.path, text: loaded.text } : null);

   // Each file gets a new editor, and the cleanup destroys the one before it.
   $effect(() => {
      const parent = host;
      const current = text;
      mountFailed = false;
      if (!parent || current === null) {
         return;
      }

      let editor: EditorSurface | null = null;
      const canceled = new AbortController();
      surfaces.file({ parent, path: current.path, text: current.text, side: current.side, signal: canceled.signal }).then(
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
            mounted = { editor, side: current.side, path: current.path };
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

   // Only an unchanged file's decorations hold passages at both sides, and its sides hold the same text.
   const sideDecorations = $derived(atSide(decorations, known.side));

   $effect(() => {
      mounted?.editor.showPassages(sideDecorations);
   });

   const inline = new InlineHost(
      () => explanation,
      () => ({ base: known.side === 'base' ? known.path : null, head: known.side === 'head' ? known.path : null }),
   );

   $effect(() => {
      const current = mounted;
      void inline.key;
      return untrack(() => inline.show(current?.editor ?? null));
   });

   // A selection asks about the file that the editor shows, which a new file's load does not change before it goes.
   const askTarget = $derived.by(() => {
      const current = mounted;
      if (current === null || onask === null) {
         return null;
      }

      const paths = { base: current.side === 'base' ? current.path : null, head: current.side === 'head' ? current.path : null };
      return (range: Range) => codeTarget(current.editor.selection(range), paths, current.editor);
   });

   /** Opens the thread of a marker that the editor draws, which carries its ID. */
   function openMarker(event: Event): void {
      const marker = event.target instanceof Element ? event.target.closest<HTMLElement>('.lotsen-threadMarkers [data-thread-id]') : null;
      if (marker !== null && host?.contains(marker)) {
         onopenthread(marker.dataset.threadId!);
      }
   }

   const collapsing = $derived(sideDecorations.collapse);

   // Each new editor, each new focus and each switch of the collapse to passages scrolls to the focused line once. A collapse moves the line, and nothing else scrolls back to it.
   $effect(() => {
      const current = mounted;
      const target = focus;
      void collapsing;
      if (current !== null && target !== null && target.side === current.side && target.path === current.path) {
         current.editor.focus({ side: target.side, line: target.line });
      }
   });
</script>

<div class={['relative flex flex-col', !stacked && 'h-full min-h-0']}>
   {#if stacked}
      <header class="flex items-center gap-4 border-b bg-card px-4 py-1.5">
         <FileHeading {known} {outsideStop} threads={fileThreads} {onopenthread} onask={onask === null ? null : () => onask({ kind: 'file', side: known.side, path: known.path }, null)} />
      </header>
   {/if}
   {#if known.state === 'loading'}
      <p class="p-4 text-muted-foreground">Loading the file…</p>
   {:else if known.state === 'notPinned' && known.pin.state === 'pinning'}
      <p class="p-4 text-muted-foreground">Pinning the pull request to its commits…</p>
   {:else if known.state === 'notPinned'}
      <div class="p-4 text-destructive *:mb-1" role="alert">
         <p>The pull request could not be pinned.</p>
         {#if known.pin.state === 'failed'}
            <p>{known.pin.reason}</p>
         {/if}
      </div>
   {:else if known.state === 'failed'}
      <div class="p-4 text-destructive *:mb-1" role="alert">
         <p>{known.message}</p>
         {#if known.reason}
            <p>{known.reason}</p>
         {/if}
      </div>
   {:else if known.text.content === 'binary'}
      <p class="p-4 text-muted-foreground">Binary file. It has no text to show.</p>
   {:else if known.text.content === 'large'}
      <div class="p-4 text-muted-foreground *:mb-1">
         <p>This file is {formatSize(known.text.size)}, so it is collapsed.</p>
         <Button variant="outline" size="sm" class="mt-2" onclick={onloadlarge}>Load the file</Button>
      </div>
   {:else if mountFailed}
      <p class="p-4 text-destructive" role="alert">The file could not be shown.</p>
   {/if}
   <!-- The markers are buttons, which take the keys and the clicks: the editor's element only delegates them. -->
   <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
   <div class={['editor', !stacked && 'min-h-0 flex-1']} bind:this={host} hidden={text === null || mountFailed} onclick={openMarker}></div>
   {#if askTarget !== null && onask !== null}
      <SelectionAsk within={host} targetOf={askTarget} {onask} />
   {/if}
</div>
