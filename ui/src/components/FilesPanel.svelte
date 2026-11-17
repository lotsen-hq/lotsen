<script lang="ts">
   import type { LucideIcon } from '@lucide/svelte';
   import FileDiff from '@lucide/svelte/icons/file-diff';
   import FolderGit2 from '@lucide/svelte/icons/folder-git-2';
   import Group from '@lucide/svelte/icons/group';
   import List from '@lucide/svelte/icons/list';
   import ListChevronsDownUp from '@lucide/svelte/icons/list-chevrons-down-up';
   import ListTree from '@lucide/svelte/icons/list-tree';
   import LocateFixed from '@lucide/svelte/icons/locate-fixed';
   import MapPin from '@lucide/svelte/icons/map-pin';
   import MessageSquareText from '@lucide/svelte/icons/message-square-text';
   import SunDim from '@lucide/svelte/icons/sun-dim';
   import { tick, untrack } from 'svelte';
   import type { KnownDirectory } from '../lib/viewState';
   import type { KnownFiles } from '../lib/tourRecord.svelte';
   import { isHTMLElement } from '../lib/dom';
   import {
      buildFileList,
      buildFileTree,
      dimOtherFilesPreference,
      fileLayoutPreference,
      flatNodes,
      groupByStop,
      groupByStopPreference,
      groupVisitedByStop,
      showMarkReasonsPreference,
      treeNodes,
      visitedFiles,
      type ChangedFileRow,
      type FileLayout,
      type FileTreeNode,
      type RevealRequest,
      type VisitedFileRow,
   } from '../lib/fileTree';
   import { panelDocument } from '../lib/panelDocument.svelte';
   import { pinFailedLines, pinningLines } from '../lib/labels';
   import { pinCommits, visitedCode } from '../lib/pin';
   import { changedFilesAt, stopVisits } from '../lib/repositoryTree';
   import type { FileReference } from '../lib/stopFiles';
   import type { ChangedFile, FileMark, FileSide, Stop, TourSummary } from '../lib/types';
   import FileTree from './FileTree.svelte';
   import IconToggle from './IconToggle.svelte';
   import RepositoryTree from './RepositoryTree.svelte';
   import ToolbarToggle, { toolbarToggleClass } from './ToolbarToggle.svelte';
   import TooltipButton from './TooltipButton.svelte';

   let {
      tour,
      known,
      selectedPath,
      selectedText = null,
      fileMarks = new Map(),
      currentStop = null,
      stops = [],
      reviewedPaths = new Set(),
      reviewError = null,
      onselect,
      onreview,
      onopen = () => {},
      listDirectory = () => new Promise(() => {}),
      revealRequest = null,
   }: {
      tour: TourSummary | null;
      known: KnownFiles | undefined;
      /** The path of the open changed file, which the trees mark, or null when none is open. */
      selectedPath: string | null;
      /** The open unchanged file, or null when none is open. */
      selectedText?: FileReference | null;
      /** Claude's marks on the changed files, by path. */
      fileMarks?: ReadonlyMap<string, FileMark>;
      /** The stop the reviewer has open, whose visited files at the pin the list can show first, or null when none is open. */
      currentStop?: Stop | null;
      /** The plan's stops, whose visited files give a repository tour's tour markers. */
      stops?: readonly Stop[];
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
      /** The last jump's request to reveal a file, which the panel follows in its current view alone, or null before any. */
      revealRequest?: RevealRequest | null;
   } = $props();

   // The list view is a pull request's changed files, or the files a repository tour's stops visit.
   type View = 'list' | 'repository';
   type ViewOption = { id: View; label: string; icon: LucideIcon };
   const repositoryView: ViewOption = { id: 'repository', label: 'Repository', icon: FolderGit2 };
   const changedViews: ViewOption[] = [{ id: 'list', label: 'Changed', icon: FileDiff }, repositoryView];
   const visitedViews: ViewOption[] = [{ id: 'list', label: 'Visited', icon: MapPin }, repositoryView];
   const sides: { id: FileSide; label: string }[] = [
      { id: 'head', label: 'Head' },
      { id: 'base', label: 'Base' },
   ];

   const layouts: { id: FileLayout; label: string; icon: LucideIcon }[] = [
      { id: 'tree', label: 'Tree', icon: ListTree },
      { id: 'flat', label: 'Flat', icon: List },
   ];

   let view = $state<View>('list');
   // The panel's choices read their preferences, so a change in another page shows here too.
   const layout = $derived(fileLayoutPreference.read());
   const grouped = $derived(groupByStopPreference.read() === 'true');
   const dimOthers = $derived(dimOtherFilesPreference.read() === 'true');
   const showReasons = $derived(showMarkReasonsPreference.read() === 'true');
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
   // A repository tour's one commit, which its tree lists, or an empty string for a tour without a snapshot.
   const snapshotHead = $derived(tour?.pin.state === 'snapshot' ? tour.pin.head : '');
   const visits = $derived(snapshotHead === '' ? new Map<string, number>() : stopVisits(stops, snapshotHead));
   const reviewedCount = $derived(files.filter((file) => reviewedPaths.has(file.path)).length);
   const stopGroups = $derived(groupByStop(files, currentStop === null ? [] : visitedCode(currentStop, pinCommits(tour?.pin))));
   const groups = $derived(grouped ? stopGroups : null);
   const undimmedPaths = $derived(dimOthers && stopGroups !== null ? new Set(stopGroups.stop.map((file) => file.path)) : null);
   const visited = $derived(visitedFiles(visits));
   const visitedStopGroups = $derived(groupVisitedByStop(visited, currentStop === null ? [] : visitedCode(currentStop, pinCommits(tour?.pin))));
   const visitedGroups = $derived(grouped ? visitedStopGroups : null);
   const visitedUndimmedPaths = $derived(dimOthers && visitedStopGroups !== null ? new Set(visitedStopGroups.stop.map((file) => file.path)) : null);
   const snapshotSelectedPath = $derived(selectedText?.side === 'head' ? selectedText.path : null);
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

   const nodesOf = (list: readonly ChangedFile[]): FileTreeNode<ChangedFileRow>[] => (layout === 'flat' ? buildFileList(list, fileMarks) : buildFileTree(list, fileMarks));
   // Derived apart from the FileTree props, so a review toggle or Show reasons does not rebuild and re-sort the nodes.
   const stopNodes = $derived(groups ? nodesOf(groups.stop) : []);
   const restNodes = $derived(nodesOf(groups?.rest ?? files));
   const visitedNodesOf = (list: readonly VisitedFileRow[]): FileTreeNode<VisitedFileRow>[] => (layout === 'flat' ? flatNodes(list) : treeNodes(list));

   function chooseLayout(next: FileLayout) {
      fileLayoutPreference.write(next);
   }

   function toggleGrouped() {
      groupByStopPreference.write(grouped ? 'false' : 'true');
   }

   function toggleDimOthers() {
      dimOtherFilesPreference.write(dimOthers ? 'false' : 'true');
   }

   function toggleShowReasons() {
      showMarkReasonsPreference.write(showReasons ? 'false' : 'true');
   }

   // The element of the changed or the visited view, whichever the tour has.
   let listView = $state<HTMLElement | null>(null);
   const home = panelDocument();
   // The row that held the focus as an update began, or null when the focus was outside the list view.
   let focusedBefore: HTMLElement | null = null;

   // A stop update or a layout change can move the focused file to another tree, or rebuild its tree, which drops the focus to the page.
   // The pre effect runs before the DOM update, so it still sees the row that held the focus, and the effect after it focuses that file's new row.
   $effect.pre(() => {
      void [groups, files, visitedGroups, visited, layout, listView];
      const active = home.document.activeElement;
      focusedBefore = isHTMLElement(active) && listView?.contains(active) ? active : null;
   });

   $effect(() => {
      void [groups, files, visitedGroups, visited, layout, listView];
      const row = focusedBefore;
      focusedBefore = null;
      const active = home.document.activeElement;
      if (row !== null && !row.isConnected && listView !== null && (active === null || active === home.document.body)) {
         [...listView.querySelectorAll<HTMLElement>('[role="treeitem"]')].find((candidate) => candidate.dataset.id === row.dataset.id)?.focus();
      }
   });

   function chooseView(next: View) {
      view = next;
      repositoryOpened ||= next === 'repository';
   }

   // The trees that the toolbar's Collapse all and the reveals drive: the list view's two trees, of which only the other files' shows while ungrouped, and the whole-repository view's.
   let stopTree = $state<FileTree>();
   let otherTree = $state<FileTree>();
   let repositoryTree = $state<RepositoryTree>();

   // The open file at its side: a repository tour's text at the head, else the open text, or the open changed file at the head, or at the base for a deleted file.
   const openFile = $derived.by((): FileReference | null => {
      if (tour?.target.kind === 'repository') {
         return selectedText?.side === 'head' ? selectedText : null;
      }

      if (selectedText !== null || selectedPath === null) {
         return selectedText;
      }

      return { side: files.find((file) => file.path === selectedPath)?.kind === 'deleted' ? 'base' : 'head', path: selectedPath };
   });

   /** Returns the file's path at a side: its own at its side, a changed file's path there, or the same path for a file that the loaded changed files lack, or null when the side lacks the file. */
   function pathAt(file: FileReference, at: FileSide): string | null {
      if (file.side === at) {
         return file.path;
      }

      if (known?.state !== 'loaded') {
         return null;
      }

      const changed = changedFilesAt(files, file.side).get(file.path);
      return changed === undefined ? file.path : ([...changedFilesAt(files, at)].find(([, candidate]) => candidate === changed)?.[0] ?? null);
   }

   /** Reveals the file in the current view, which never changes, and returns whether the view holds the file. */
   function revealInView(file: FileReference): boolean {
      if (view === 'repository') {
         const path = pathAt(file, side);
         if (path !== null) {
            void repositoryTree?.reveal(path);
         }

         return path !== null;
      }

      const path = tour?.target.kind === 'repository' ? (file.side === 'head' && visits.has(file.path) ? file.path : null) : (changedFilesAt(files, file.side).get(file.path)?.path ?? null);
      return path !== null && [stopTree, otherTree].some((tree) => tree?.reveal(path) === true);
   }

   /** Reveals the open file, in the whole-repository view at the file's side when the current view does not hold the file. */
   async function revealOpenFile() {
      const file = openFile;
      if (file === null || revealInView(file)) {
         return;
      }

      chooseView('repository');
      side = file.side;
      await tick();
      await repositoryTree?.reveal(file.path);
   }

   function collapseAll() {
      if (view === 'repository') {
         repositoryTree?.collapseAll();
      } else {
         stopTree?.collapseAll();
         otherTree?.collapseAll();
      }
   }

   // The panel follows only the requests that come after it mounts, so a tour's new panel does not reveal the last jump of the tour before.
   let revealed = untrack(() => revealRequest?.sequence ?? 0);
   $effect(() => {
      const request = revealRequest;
      if (request !== null && request.sequence > revealed) {
         revealed = request.sequence;
         untrack(() => revealInView(request));
      }
   });
</script>

{#snippet groupHeading(text: string, space: 'pt-2' | 'pt-6')}
   <h3 class="px-4 {space} text-xs font-semibold tracking-wider text-muted-foreground uppercase">{text}</h3>
{/snippet}

<!-- The actions on the current view's trees, which stay mounted across a switch of the view, so a reveal that switches it keeps the focus on its button. -->
{#snippet treeActions()}
   <div class="flex *:not-first:*:border-l-0" role="group" aria-label="File tree actions">
      <TooltipButton tooltip="Collapse all" aria-label="Collapse all" variant="outline" size="icon-sm" class={toolbarToggleClass} disabled={view === 'list' && layout === 'flat'} onclick={collapseAll}>
         <ListChevronsDownUp />
      </TooltipButton>
      <TooltipButton
         tooltip="Reveal the open file"
         aria-label="Reveal the open file"
         variant="outline"
         size="icon-sm"
         class={toolbarToggleClass}
         disabled={openFile === null}
         onclick={() => void revealOpenFile()}
      >
         <LocateFixed />
      </TooltipButton>
   </div>
{/snippet}

{#snippet viewSwitch(options: ViewOption[])}
   <div class="flex *:not-first:border-l-0" role="group" aria-label="File view">
      {#each options as option (option.id)}
         <IconToggle label={option.label} icon={option.icon} pressed={view === option.id} onclick={() => chooseView(option.id)} />
      {/each}
   </div>
{/snippet}

<!-- The list view's controls, with Show reasons only where the files can carry Claude's marks. -->
{#snippet listControls(reasons: boolean)}
   <div class="flex *:not-first:border-l-0" role="group" aria-label="File layout">
      {#each layouts as option (option.id)}
         <IconToggle label={option.label} icon={option.icon} pressed={layout === option.id} onclick={() => chooseLayout(option.id)} />
      {/each}
   </div>
   <div class="flex *:not-first:border-l-0" role="group" aria-label="File options">
      <IconToggle label="Group by stop" icon={Group} pressed={grouped} onclick={toggleGrouped} />
      <IconToggle label="Dim other files" icon={SunDim} pressed={dimOthers} onclick={toggleDimOthers} />
      {#if reasons}
         <IconToggle label="Show reasons" icon={MessageSquareText} pressed={showReasons} onclick={toggleShowReasons} />
      {/if}
   </div>
{/snippet}

<div class="py-1">
   {#if !tour}
      <p class="px-4 py-1 text-muted-foreground">Select a tour to see its changed files.</p>
   {:else if pin?.state === 'pinning'}
      <p class="px-4 py-1 text-muted-foreground">{pinningLines[tour.target.kind]}</p>
   {:else if pin?.state === 'failed'}
      <div class="px-4 py-1 text-destructive *:mb-1" role="alert">
         <p>{pinFailedLines[tour.target.kind]}</p>
         <p>{pin.reason}</p>
      </div>
   {:else if tour.target.kind === 'repository'}
      <!-- A repository tour has no changed files and no base, so both its views show the snapshot's head, with no side to choose. -->
      <div class="flex flex-wrap items-center gap-2 px-4 pt-1 pb-2">
         {@render viewSwitch(visitedViews)}
         {#if view === 'list'}
            {@render listControls(false)}
         {/if}
         {@render treeActions()}
      </div>
      <div hidden={view !== 'list'} bind:this={listView}>
         {#if visited.length === 0}
            <p class="px-4 py-1 text-muted-foreground">No stop visits a file yet.</p>
         {:else}
            {#if visitedGroups}
               {@render groupHeading('This stop', 'pt-2')}
               {#key currentStop?.id}
                  <FileTree
                     bind:this={stopTree}
                     files={{ kind: 'visited', nodes: visitedNodesOf(visitedGroups.stop) }}
                     label="Visited files in this stop"
                     selectedPath={snapshotSelectedPath}
                     undimmedPaths={visitedUndimmedPaths}
                     onselect={(path) => onopen('head', path)}
                  />
               {/key}
               {#if visitedGroups.rest.length > 0}
                  {@render groupHeading('Other files', 'pt-6')}
               {/if}
            {/if}
            {#if !visitedGroups || visitedGroups.rest.length > 0}
               <FileTree
                  bind:this={otherTree}
                  files={{ kind: 'visited', nodes: visitedNodesOf(visitedGroups?.rest ?? visited) }}
                  label={visitedGroups ? 'Other visited files' : 'Visited files'}
                  selectedPath={snapshotSelectedPath}
                  undimmedPaths={visitedUndimmedPaths}
                  onselect={(path) => onopen('head', path)}
               />
            {/if}
         {/if}
      </div>
      {#if repositoryOpened}
         <div hidden={view !== 'repository'}>
            {#key snapshotHead}
               <RepositoryTree bind:this={repositoryTree} listDirectory={(path) => listDirectory('head', path)} {visits} selectedPath={snapshotSelectedPath} onopen={(path) => onopen('head', path)} />
            {/key}
         </div>
      {/if}
   {:else}
      <div class="flex flex-wrap items-center gap-2 px-4 pt-1 pb-2">
         {@render viewSwitch(changedViews)}
         {#if view === 'list'}
            {@render listControls(true)}
         {:else}
            <div class="flex *:not-first:border-l-0" role="group" aria-label="Side">
               {#each sides as option (option.id)}
                  <ToolbarToggle compact pressed={side === option.id} onclick={() => (side = option.id)}>{option.label}</ToolbarToggle>
               {/each}
            </div>
         {/if}
         {@render treeActions()}
      </div>
      <div hidden={view !== 'list'} bind:this={listView}>
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
                  <FileTree
                     bind:this={stopTree}
                     files={{ kind: 'changed', nodes: stopNodes, showReasons, reviewedPaths, onreview }}
                     label="Changed files in this stop"
                     {selectedPath}
                     {undimmedPaths}
                     {onselect}
                  />
               {/key}
               {#if groups.rest.length > 0}
                  {@render groupHeading('Other files', 'pt-6')}
               {/if}
            {/if}
            <!-- One tree holds the files outside the stop's group whether or not the list is grouped, so a stop update keeps its rows and their focus. -->
            {#if !groups || groups.rest.length > 0}
               <FileTree
                  bind:this={otherTree}
                  files={{ kind: 'changed', nodes: restNodes, showReasons, reviewedPaths, onreview }}
                  label={groups ? 'Other changed files' : 'Changed files'}
                  {selectedPath}
                  {undimmedPaths}
                  {onselect}
               />
            {/if}
         {/if}
      </div>
      {#if repositoryOpened}
         <div hidden={view !== 'repository'}>
            <!-- Each side and each commit gets its own tree, because two commits hold different folders. -->
            {#key `${side}:${sideCommit}`}
               <RepositoryTree
                  bind:this={repositoryTree}
                  listDirectory={(path) => listDirectory(side, path)}
                  changed={changedAtSide}
                  selectedPath={repositorySelectedPath}
                  onopen={(path) => onopen(side, path)}
               />
            {/key}
         </div>
      {/if}
   {/if}
</div>
