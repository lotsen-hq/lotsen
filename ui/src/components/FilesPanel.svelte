<script lang="ts">
   import type { LucideIcon } from '@lucide/svelte';
   import FileDiff from '@lucide/svelte/icons/file-diff';
   import FolderGit2 from '@lucide/svelte/icons/folder-git-2';
   import Group from '@lucide/svelte/icons/group';
   import List from '@lucide/svelte/icons/list';
   import ListTree from '@lucide/svelte/icons/list-tree';
   import MessageSquareText from '@lucide/svelte/icons/message-square-text';
   import SunDim from '@lucide/svelte/icons/sun-dim';
   import type { KnownDirectory } from '../lib/viewState';
   import type { KnownFiles } from '../lib/tourRecord.svelte';
   import { contentOf } from '../lib/stops';
   import {
      buildFileList,
      buildFileTree,
      dimOtherFilesPreference,
      fileLayoutPreference,
      groupByStop,
      groupByStopPreference,
      showMarkReasonsPreference,
      type FileLayout,
      type FileTreeNode,
   } from '../lib/fileTree';
   import { atPin, pinCommits } from '../lib/pin';
   import { changedFilesAt } from '../lib/repositoryTree';
   import type { FileReference } from '../lib/stopFiles';
   import type { ChangedFile, FileMark, FileSide, Stop, TourSummary } from '../lib/types';
   import FileTree from './FileTree.svelte';
   import IconToggle from './IconToggle.svelte';
   import RepositoryTree from './RepositoryTree.svelte';
   import ToolbarToggle from './ToolbarToggle.svelte';

   let {
      tour,
      known,
      selectedPath,
      selectedText = null,
      fileMarks = new Map(),
      currentStop = null,
      reviewedPaths = new Set(),
      reviewError = null,
      onselect,
      onreview,
      onopen = () => {},
      listDirectory = () => new Promise(() => {}),
   }: {
      tour: TourSummary | null;
      known: KnownFiles | undefined;
      /** The path of the open changed file, which the trees mark, or null when none is open. */
      selectedPath: string | null;
      /** The open unchanged file, or null when none is open. */
      selectedText?: FileReference | null;
      /** Claude's marks on the changed files, by path. */
      fileMarks?: ReadonlyMap<string, FileMark>;
      /** The stop the reviewer has open, whose files at the pin the list can show first, or null when none is open. */
      currentStop?: Stop | null;
      /** The paths of the changed files that the reviewer marked reviewed. */
      reviewedPaths?: ReadonlySet<string>;
      /** Why the reviewer's last review mark could not be stored, or null when nothing failed. */
      reviewError?: string | null;
      onselect: (path: string) => void;
      /** Sets or clears a changed file's review mark. Without it the files have no checkbox and the panel no count. */
      onreview?: (path: string, reviewed: boolean) => void;
      /** Opens a file of the whole-repository view at a side. */
      onopen?: (side: FileSide, path: string) => void;
      /** Lists a directory of the tour at a side, or the root directory for a null path. */
      listDirectory?: (side: FileSide, path: string | null) => Promise<KnownDirectory>;
   } = $props();

   type View = 'changed' | 'repository';
   const views: { id: View; label: string; icon: LucideIcon }[] = [
      { id: 'changed', label: 'Changed', icon: FileDiff },
      { id: 'repository', label: 'Repository', icon: FolderGit2 },
   ];
   const sides: { id: FileSide; label: string }[] = [
      { id: 'head', label: 'Head' },
      { id: 'base', label: 'Base' },
   ];

   const layouts: { id: FileLayout; label: string; icon: LucideIcon }[] = [
      { id: 'tree', label: 'Tree', icon: ListTree },
      { id: 'flat', label: 'Flat', icon: List },
   ];

   let view = $state<View>('changed');
   let layout = $state(fileLayoutPreference.read());
   let grouped = $state(groupByStopPreference.read() === 'true');
   let dimOthers = $state(dimOtherFilesPreference.read() === 'true');
   let showReasons = $state(showMarkReasonsPreference.read() === 'true');
   let side = $state<FileSide>('head');
   // The repository tree lists the root when it mounts, so it mounts the first time the reviewer opens its view and then stays.
   let repositoryOpened = $state(false);

   // A pin that is not pinned shows first: the tour's own, or the one in the server's refusal of the list.
   const pin = $derived(tour?.pin.state !== 'pinned' ? tour?.pin : known?.state === 'notPinned' ? known.pin : undefined);
   // Without a loaded list of changed files, the repository tree marks no file as changed.
   const files = $derived(known?.state === 'loaded' ? known.list.files : []);
   const changedAtSide = $derived(changedFilesAt(files, side));
   // The commit that the repository view lists, which an update of the tour to a new iteration changes.
   const sideCommit = $derived(tour?.pin.state === 'pinned' ? (side === 'base' ? tour.pin.mergeBase : tour.pin.head) : '');
   const reviewedCount = $derived(files.filter((file) => reviewedPaths.has(file.path)).length);
   // Only the passages at the pin group and un-dim files, as they alone get decorations and a place in the stop-files stack.
   const stopPassages = $derived((contentOf(currentStop)?.passages ?? []).filter((passage) => atPin(passage, pinCommits(tour?.pin))));
   const stopGroups = $derived(groupByStop(files, stopPassages));
   const groups = $derived(grouped ? stopGroups : null);
   const undimmedPaths = $derived(dimOthers && stopGroups !== null ? new Set(stopGroups.stop.map((file) => file.path)) : null);
   const repositorySelectedPath = $derived.by(() => {
      if (selectedText !== null) {
         return selectedText.side === side ? selectedText.path : null;
      }

      for (const [path, file] of changedAtSide) {
         if (file.path === selectedPath) {
            return path;
         }
      }

      return null;
   });

   const nodesOf = (list: readonly ChangedFile[]): FileTreeNode[] => (layout === 'flat' ? buildFileList(list, fileMarks) : buildFileTree(list, fileMarks));

   function chooseLayout(next: FileLayout) {
      layout = next;
      fileLayoutPreference.write(next);
   }

   function toggleGrouped() {
      grouped = !grouped;
      groupByStopPreference.write(grouped ? 'true' : 'false');
   }

   function toggleDimOthers() {
      dimOthers = !dimOthers;
      dimOtherFilesPreference.write(dimOthers ? 'true' : 'false');
   }

   function toggleShowReasons() {
      showReasons = !showReasons;
      showMarkReasonsPreference.write(showReasons ? 'true' : 'false');
   }

   let changedView = $state<HTMLElement | null>(null);
   // The row that held the focus as an update began, or null when the focus was outside the changed-file view.
   let focusedBefore: HTMLElement | null = null;

   // A stop update or a layout change can move the focused file to another tree, or rebuild its tree, which drops the focus to the page.
   // The pre effect runs before the DOM update, so it still sees the row that held the focus, and the effect after it focuses that file's new row.
   $effect.pre(() => {
      void [groups, files, layout, changedView];
      const active = document.activeElement;
      focusedBefore = active instanceof HTMLElement && changedView?.contains(active) ? active : null;
   });

   $effect(() => {
      void [groups, files, layout, changedView];
      const row = focusedBefore;
      focusedBefore = null;
      const active = document.activeElement;
      if (row !== null && !row.isConnected && changedView !== null && (active === null || active === document.body)) {
         [...changedView.querySelectorAll<HTMLElement>('[role="treeitem"]')].find((candidate) => candidate.dataset.id === row.dataset.id)?.focus();
      }
   });

   function chooseView(next: View) {
      view = next;
      repositoryOpened ||= next === 'repository';
   }
</script>

{#snippet groupHeading(text: string, space: 'pt-2' | 'pt-6')}
   <h3 class="px-4 {space} text-xs font-semibold tracking-wider text-muted-foreground uppercase">{text}</h3>
{/snippet}

<div class="py-1">
   {#if !tour}
      <p class="px-4 py-1 text-muted-foreground">Select a tour to see its changed files.</p>
   {:else if pin?.state === 'pinning'}
      <p class="px-4 py-1 text-muted-foreground">Pinning the pull request to its commits…</p>
   {:else if pin?.state === 'failed'}
      <div class="px-4 py-1 text-destructive *:mb-1" role="alert">
         <p>The pull request could not be pinned.</p>
         <p>{pin.reason}</p>
      </div>
   {:else}
      <div class="flex flex-wrap items-center gap-2 px-4 pt-1 pb-2">
         <div class="flex *:not-first:border-l-0" role="group" aria-label="File view">
            {#each views as option (option.id)}
               <IconToggle label={option.label} icon={option.icon} pressed={view === option.id} onclick={() => chooseView(option.id)} />
            {/each}
         </div>
         {#if view === 'changed'}
            <div class="flex *:not-first:border-l-0" role="group" aria-label="File layout">
               {#each layouts as option (option.id)}
                  <IconToggle label={option.label} icon={option.icon} pressed={layout === option.id} onclick={() => chooseLayout(option.id)} />
               {/each}
            </div>
            <div class="flex *:not-first:border-l-0" role="group" aria-label="File options">
               <IconToggle label="Group by stop" icon={Group} pressed={grouped} onclick={toggleGrouped} />
               <IconToggle label="Dim other files" icon={SunDim} pressed={dimOthers} onclick={toggleDimOthers} />
               <IconToggle label="Show reasons" icon={MessageSquareText} pressed={showReasons} onclick={toggleShowReasons} />
            </div>
         {:else}
            <div class="flex *:not-first:border-l-0" role="group" aria-label="Side">
               {#each sides as option (option.id)}
                  <ToolbarToggle compact pressed={side === option.id} onclick={() => (side = option.id)}>{option.label}</ToolbarToggle>
               {/each}
            </div>
         {/if}
      </div>
      <div hidden={view !== 'changed'} bind:this={changedView}>
         {#if known?.state === 'failed'}
            <div class="px-4 py-1 text-destructive *:mb-1" role="alert">
               <p>{known.message}</p>
               {#if known.reason}
                  <p>{known.reason}</p>
               {/if}
            </div>
         {:else if known?.state !== 'loaded'}
            <p class="px-4 py-1 text-muted-foreground">Loading the changed files…</p>
         {:else if files.length === 0}
            <p class="px-4 py-1 text-muted-foreground">This pull request changes no files.</p>
         {:else}
            {#if onreview !== undefined}
               <p class="px-4 pb-1 text-xs text-muted-foreground">{reviewedCount} of {files.length} reviewed</p>
            {/if}
            {#if reviewError !== null}
               <p class="px-4 pb-1 text-xs text-destructive" role="alert">{reviewError}</p>
            {/if}
            {#if groups}
               {@render groupHeading('This stop', 'pt-2')}
               <!-- Each stop gets its own tree, so a folder collapsed at one stop does not hide the files of the next. -->
               {#key currentStop?.id}
                  <FileTree nodes={nodesOf(groups.stop)} label="Changed files in this stop" {selectedPath} {undimmedPaths} {showReasons} {reviewedPaths} {onselect} {onreview} />
               {/key}
               {#if groups.rest.length > 0}
                  {@render groupHeading('Other files', 'pt-6')}
               {/if}
            {/if}
            <!-- One tree holds the files outside the stop's group whether or not the list is grouped, so a stop update keeps its rows and their focus. -->
            {#if !groups || groups.rest.length > 0}
               <FileTree
                  nodes={nodesOf(groups?.rest ?? files)}
                  label={groups ? 'Other changed files' : 'Changed files'}
                  {selectedPath}
                  {undimmedPaths}
                  {showReasons}
                  {reviewedPaths}
                  {onselect}
                  {onreview}
               />
            {/if}
         {/if}
      </div>
      {#if repositoryOpened}
         <div hidden={view !== 'repository'}>
            <!-- Each side and each commit gets its own tree, because two commits hold different folders. -->
            {#key `${side}:${sideCommit}`}
               <RepositoryTree listDirectory={(path) => listDirectory(side, path)} changed={changedAtSide} selectedPath={repositorySelectedPath} onopen={(path) => onopen(side, path)} />
            {/key}
         </div>
      {/if}
   {/if}
</div>
