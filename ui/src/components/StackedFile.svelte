<script lang="ts">
   import { untrack } from 'svelte';
   import type { KnownDiff, KnownText, PickHandler } from '../lib/viewState';
   import type { LineFocus } from '../lib/openStop.svelte';
   import type { DiffLayout } from '../lib/diffLayout';
   import type { ExplanationView } from '../lib/explanationView.svelte';
   import type { StackFile } from '../lib/stopFiles';
   import { withHoveredReference, withThreads } from '../lib/passageMarks';
   import { changedFileAnchor } from '../lib/selectionAsk';
   import { codeThreads, fileThreads } from '../lib/threadView';
   import type { ChangedFile, CodeReference, FileMark, FileSide, QuestionThread } from '../lib/types';
   import DiffView from './DiffView.svelte';
   import FileView from './FileView.svelte';

   let {
      file,
      mark = null,
      layout,
      ignoreWhitespace,
      focus,
      hovered = null,
      explanation = null,
      threads = [],
      onask = null,
      onopenthread = () => {},
      loadDiff,
      loadText,
      pin = null,
   }: {
      file: StackFile;
      /** Claude's mark on the file, or null for none. */
      mark?: FileMark | null;
      layout: DiffLayout;
      ignoreWhitespace: boolean;
      focus: LineFocus | null;
      /** The hovered reference, which the file highlights when it points into it, or null for none. */
      hovered?: CodeReference | null;
      /** The page's explanation, which shows below its inline passage when that passage is in this file, or null for none. */
      explanation?: ExplanationView | null;
      /** The tour's threads, which the file marks where they were asked about it. */
      threads?: readonly QuestionThread[];
      /** Hands what the reviewer selected or picked in the file to the Threads panel, or null when the page offers no ask. */
      onask?: PickHandler | null;
      onopenthread?: (threadId: string) => void;
      loadDiff: (file: ChangedFile, options: { includeLarge: boolean; ignoreWhitespace: boolean }) => Promise<KnownDiff>;
      loadText: (side: FileSide, path: string, includeLarge: boolean) => Promise<KnownText>;
      /** The pin that the file is read at, whose change loads its content again. */
      pin?: string | null;
   } = $props();

   let includeLarge = $state(false);
   let diff = $state.raw<KnownDiff | null>(null);
   let text = $state.raw<KnownText | null>(null);
   // Derived values signal only a change of the file, of its kind or of the tour's pin, so a new stack that keeps the file keeps its content and its editors.
   const key = $derived(file.key);
   const pinned = $derived(pin);
   const isDiff = $derived(file.kind === 'diff');
   const ignore = $derived(isDiff && ignoreWhitespace);
   const decorations = $derived(withThreads(withHoveredReference(file.decorations, file.paths, hovered), file.paths, codeThreads(threads)));
   const markers = $derived(fileThreads(threads, file.paths));

   // Each load supersedes the one before it, and the cleanup drops a response that arrives after the file left the stack. The effect reruns only for the values it reads before `untrack`.
   $effect(() => {
      void key;
      void pinned;
      const options = { includeLarge, ignoreWhitespace: ignore };
      let canceled = false;
      untrack(() => {
         if (file.kind === 'diff') {
            diff = { state: 'loading', path: file.file.path };
            void loadDiff(file.file, options).then((known) => {
               if (!canceled) {
                  diff = known;
               }
            });
         } else {
            text = { state: 'loading', side: file.side, path: file.path };
            void loadText(file.side, file.path, options.includeLarge).then((known) => {
               if (!canceled) {
                  text = known;
               }
            });
         }
      });

      return () => {
         canceled = true;
      };
   });
</script>

{#if file.kind === 'diff'}
   <DiffView
      stacked
      outsideStop={file.outside}
      path={file.file.path}
      oldPath={file.file.kind === 'renamed' ? file.file.oldPath : null}
      known={diff}
      lineEndingsOnly={file.file.lineEndingsOnly === true}
      {mark}
      {layout}
      {focus}
      {decorations}
      {explanation}
      {onask}
      {onopenthread}
      fileAnchor={changedFileAnchor(file.file)}
      fileThreads={markers}
      onloadlarge={() => (includeLarge = true)}
   />
{:else}
   <FileView
      stacked
      outsideStop={file.outside}
      known={text ?? { state: 'loading', side: file.side, path: file.path }}
      {focus}
      {decorations}
      {explanation}
      {onask}
      {onopenthread}
      fileThreads={markers}
      onloadlarge={() => (includeLarge = true)}
   />
{/if}
