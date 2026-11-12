<script lang="ts">
   import type { KnownDiff, KnownText, PickHandler } from '../lib/viewState';
   import type { LineFocus } from '../lib/openStop.svelte';
   import type { DiffLayout } from '../lib/diffLayout';
   import type { ExplanationView } from '../lib/explanationView.svelte';
   import { panelDocument } from '../lib/panelDocument.svelte';
   import { scrolledToEnd, shownStackFile, type StackFile } from '../lib/stopFiles';
   import type { ChangedFile, CodeReference, FileMark, FileSide, QuestionThread, Stop } from '../lib/types';
   import StackedFile from './StackedFile.svelte';

   let {
      files,
      stop,
      fileMarks = new Map(),
      layout = 'sideBySide',
      ignoreWhitespace = false,
      focus = null,
      hovered = null,
      target = null,
      explanation = null,
      threads = [],
      onask = null,
      onopenthread = () => {},
      loadDiff,
      loadText,
      pin = null,
      ontop = () => {},
   }: {
      /** The files to stack, or null while the changed files load, which decides how each file shows. */
      files: readonly StackFile[] | null;
      /** The open stop. */
      stop: Stop;
      /** Claude's marks on the changed files, by path. */
      fileMarks?: ReadonlyMap<string, FileMark>;
      layout?: DiffLayout;
      ignoreWhitespace?: boolean;
      /** The line that the file at the focus's path scrolls to once its editors show it, or null for none. */
      focus?: LineFocus | null;
      /** The hovered reference, which the file it points into highlights, or null for none. */
      hovered?: CodeReference | null;
      /** The file to scroll to, by its key, or null for none. Each object scrolls once. */
      target?: { key: string } | null;
      /** The page's explanation, which shows below its inline passage in the file that holds that passage, or null for none. */
      explanation?: ExplanationView | null;
      /** The tour's threads, which each file marks where they were asked about it. */
      threads?: readonly QuestionThread[];
      /** Hands what the reviewer selected or picked in a file to the Threads panel, or null when the page offers no ask. */
      onask?: PickHandler | null;
      onopenthread?: (threadId: string) => void;
      loadDiff: (file: ChangedFile, options: { includeLarge: boolean; ignoreWhitespace: boolean }) => Promise<KnownDiff>;
      loadText: (side: FileSide, path: string, includeLarge: boolean) => Promise<KnownText>;
      /** The pin that the tour's files are read at, as {@link LotsenApp.filesPin} keys it, whose change loads each file's content again. */
      pin?: string | null;
      /** Reports the key of the file that the reviewer reads, as {@link shownStackFile} finds it, or null before layout, whenever the stack scrolls, resizes or changes. */
      ontop?: (key: string | null) => void;
   } = $props();

   let viewport = $state<HTMLDivElement | null>();
   let list = $state<HTMLUListElement | null>();

   // The file that the reviewer opened last, until they scroll the stack themselves. A scroll counts as theirs once input reaches the viewport: the scroll that an open causes does not.
   let opened: string | null = null;
   let reviewerInput = false;
   let lastTarget: { key: string } | null = null;
   const home = panelDocument();

   // The stack measures at most once a frame, however many scroll and resize events arrive. A new stack or a new target measures again, as neither needs to scroll or resize it.
   // The viewport is observed too, since a shorter viewport changes what shows without resizing the stack.
   $effect(() => {
      void files;
      void target;
      const scroller = viewport;
      const stacked = list;
      const { window } = home;
      // The list's binding turns null, not undefined, once the changed files load again.
      if (!scroller || !stacked) {
         return;
      }

      let frame = 0;
      const measure = () => {
         frame = 0;
         const extents = [...stacked.children].map((item) => {
            const { top, bottom } = item.getBoundingClientRect();
            return { key: (item as HTMLElement).dataset.key ?? '', top, bottom };
         });
         const { top, bottom } = scroller.getBoundingClientRect();
         ontop(shownStackFile(extents, { top, bottom, atEnd: scrolledToEnd(scroller) }, opened));
      };
      const schedule = () => {
         frame ||= window.requestAnimationFrame(measure);
      };
      const input = () => {
         reviewerInput = true;
      };
      const scrolled = () => {
         if (reviewerInput) {
            opened = null;
         }

         schedule();
      };
      const resizes = new window.ResizeObserver(schedule);
      resizes.observe(stacked);
      resizes.observe(scroller);
      const inputs = ['wheel', 'touchmove', 'keydown', 'pointerdown'] as const;
      for (const name of inputs) {
         scroller.addEventListener(name, input, { passive: true });
      }
      scroller.addEventListener('scroll', scrolled, { passive: true });
      schedule();
      return () => {
         window.cancelAnimationFrame(frame);
         resizes.disconnect();
         for (const name of inputs) {
            scroller.removeEventListener(name, input);
         }
         scroller.removeEventListener('scroll', scrolled);
      };
   });

   // The editors scroll to a focused line on the next measure, after this scroll, so a passage lands on its line and any other file at its top.
   $effect(() => {
      const wanted = target;
      if (wanted !== lastTarget) {
         lastTarget = wanted;
         opened = wanted?.key ?? null;
         reviewerInput = false;
      }

      const item = wanted === null ? undefined : [...(list?.children ?? [])].find((element) => element instanceof HTMLElement && element.dataset.key === wanted.key);
      item?.scrollIntoView({ block: 'start' });
   });
</script>

<div class="scroll-box h-full min-h-0" bind:this={viewport}>
   {#if files === null}
      <p class="p-4 text-muted-foreground">Loading the changed files…</p>
   {:else}
      {#if stop.stage === 'pending'}
         <p class="p-4 text-muted-foreground">Claude has not written this stop yet.</p>
      {:else if stop.content.passages.length === 0}
         <p class="p-4 text-muted-foreground">This stop has no passages.</p>
      {/if}
      <ul bind:this={list} aria-label="Files in this stop">
         {#each files as file (file.key)}
            <li class="border-b-4 border-muted" data-key={file.key}>
               <StackedFile
                  {file}
                  mark={file.kind === 'diff' ? (fileMarks.get(file.file.path) ?? null) : null}
                  {layout}
                  {ignoreWhitespace}
                  {focus}
                  {hovered}
                  {explanation}
                  {threads}
                  {onask}
                  {onopenthread}
                  {loadDiff}
                  {loadText}
                  {pin}
               />
            </li>
         {/each}
      </ul>
   {/if}
</div>
