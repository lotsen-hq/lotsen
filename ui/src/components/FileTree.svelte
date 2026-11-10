<script lang="ts">
   import ChevronRight from '@lucide/svelte/icons/chevron-right';
   import Folder from '@lucide/svelte/icons/folder';
   import FolderOpen from '@lucide/svelte/icons/folder-open';
   import Square from '@lucide/svelte/icons/square';
   import SquareCheck from '@lucide/svelte/icons/square-check';
   import { SvelteSet } from 'svelte/reactivity';
   import { holds, nodesOutside, visibleRows, type FileTreeFile, type FileTreeNode } from '../lib/fileTree';
   import { changeKindLabels, fileMarkLabels, lineEndingsOnlyLabel } from '../lib/labels';
   import { TreeFocus } from '../lib/treeFocus.svelte';
   import FileIcon from './FileIcon.svelte';
   import TooltipGroup from './TooltipGroup.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      nodes,
      selectedPath,
      onselect,
      label = 'Changed files',
      undimmedPaths = null,
      showReasons,
      reviewedPaths = new Set(),
      onreview,
   }: {
      nodes: FileTreeNode[];
      selectedPath: string | null;
      onselect: (path: string) => void;
      label?: string;
      /** The paths of the files that stay bright while every other file dims, with the folders that hold only dimmed files, or null to dim none. */
      undimmedPaths?: ReadonlySet<string> | null;
      /** Whether a marked file's reason shows under its name. The row's tooltip and label hold it either way. */
      showReasons: boolean;
      /** The paths of the files whose checkbox is checked. */
      reviewedPaths?: ReadonlySet<string>;
      /** Sets or clears a file's review mark. Without it the file rows have no checkbox. */
      onreview?: (path: string, reviewed: boolean) => void;
   } = $props();

   // Every folder starts expanded, so the set holds the folders the user collapsed.
   const collapsed = new SvelteSet<string>();
   const tree = new TreeFocus();

   const rows = $derived(visibleRows(nodes, collapsed));
   const dimmed = $derived(undimmedPaths === null ? new Set<string>() : nodesOutside(nodes, undimmedPaths));
   // A file row beside folders leaves the twisty's column empty, so its name lines up with theirs.
   const nested = $derived(nodes.some((node) => node.type === 'folder'));
   // The tags and the reason are hidden from assistive technology, so the label says them.
   const fileLabel = (file: FileTreeFile) =>
      `${file.name}, ${changeKindLabels[file.kind]}${file.oldPath === null ? '' : ` from ${file.oldPath}`}${file.lineEndingsOnly ? `, ${lineEndingsOnlyLabel}` : ''}` +
      (file.mark === null ? '' : `, ${fileMarkLabels[file.mark.mark]}: ${file.mark.reason}`);
   // A narrow column cuts off the row's paths and its mark's reason, and the reason may be hidden, so the tooltip spells them out.
   const fileTooltip = (file: FileTreeFile) =>
      (file.oldPath === null ? file.path : `${file.oldPath} → ${file.path}`) + (file.mark === null ? '' : `\n${fileMarkLabels[file.mark.mark]}: ${file.mark.reason}`);
   const tabStop = $derived(tree.tabStop(rows, selectedPath));
   const handlers = $derived({
      activate: (node: FileTreeNode) => {
         if (node.type === 'file') {
            onselect(node.path);
         } else if (collapsed.has(node.id)) {
            collapsed.delete(node.id);
         } else {
            collapsed.add(node.id);
         }
      },
      expand: (node: FileTreeNode) => collapsed.delete(node.id),
      collapse: (node: FileTreeNode) => collapsed.add(node.id),
      check: onreview === undefined ? undefined : (node: FileTreeNode) => onreview(node.path, !reviewedPaths.has(node.path)),
   });
</script>

<!-- A path in the flat view loses its start, as the diff toolbar's OpenPath does, so the file name stays visible longest. -->
{#snippet path(text: string)}
   {@const directory = text.slice(0, text.lastIndexOf('/') + 1)}
   {@const name = text.slice(directory.length)}
   {#if directory === ''}
      <span class="path"><span class="name">{name}</span></span>
   {:else}
      <span class="path"><span class="directory" dir="rtl"><bdi>{directory}</bdi></span><span class="name" dir="rtl"><bdi>{name}</bdi></span></span>
   {/if}
{/snippet}

{#snippet branch(children: FileTreeNode[])}
   {#each children as node (node.id)}
      {#if node.type === 'folder'}
         {@const expanded = !collapsed.has(node.id)}
         {@const current = !expanded && holds(node.path, selectedPath)}
         <!-- Folders cannot be selected, so they carry no aria-selected. -->
         <!-- svelte-ignore a11y_role_has_required_aria_props -->
         <li role="treeitem" data-id={node.id} aria-label={node.name} aria-expanded={expanded} aria-current={current ? 'true' : undefined} tabindex={tabStop === node.id ? 0 : -1}>
            <WithTooltip text={node.path}>
               {#snippet trigger({ props })}
                  <span {...props} class="row" class:current class:dimmed={dimmed.has(node.id)}>
                     <span class="twisty" aria-hidden="true"><ChevronRight class={[expanded && 'rotate-90']} /></span>
                     <span class="icon text-folder" aria-hidden="true">
                        {#if expanded}<FolderOpen />{:else}<Folder />{/if}
                     </span>
                     <span class="label"><span class="name">{node.name}</span></span>
                  </span>
               {/snippet}
            </WithTooltip>
            {#if expanded}
               <ul role="group">
                  {@render branch(node.children)}
               </ul>
            {/if}
         </li>
      {:else}
         {@const reviewed = reviewedPaths.has(node.path)}
         <!-- The row itself is the checkbox for assistive technology, and Space toggles it, so the tree keeps one tab stop. -->
         <li
            role="treeitem"
            data-id={node.id}
            aria-label={fileLabel(node)}
            aria-checked={onreview === undefined ? undefined : reviewed}
            aria-selected={node.path === selectedPath}
            aria-current={node.path === selectedPath ? 'true' : undefined}
            tabindex={tabStop === node.id ? 0 : -1}
         >
            <WithTooltip text={fileTooltip(node)}>
               {#snippet trigger({ props })}
                  <span {...props} class="row kind-{node.kind}" class:current={node.path === selectedPath} class:dimmed={dimmed.has(node.id)}>
                     {#if onreview !== undefined}
                        <span class={['check', reviewed && 'checked']} aria-hidden="true">
                           {#if reviewed}<SquareCheck />{:else}<Square />{/if}
                        </span>
                     {:else if nested}
                        <span class="twisty" aria-hidden="true"></span>
                     {/if}
                     <FileIcon path={node.path} />
                     {#if node.lineEndingsOnly}
                        <span class="tag" aria-hidden="true">{lineEndingsOnlyLabel}</span>
                     {/if}
                     {#if node.mark !== null}
                        <span class="tag" aria-hidden="true">{fileMarkLabels[node.mark.mark]}</span>
                     {/if}
                     <span class="content">
                        <span class="label">
                           {#if node.oldPath !== null}
                              <span class="old-path" dir="rtl" aria-hidden="true"><bdi>{node.oldPath}</bdi></span>
                              <span class="old-path-arrow" aria-hidden="true">→</span>
                           {/if}
                           {@render path(node.name)}
                        </span>
                        {#if node.mark !== null && showReasons}
                           <span class="reason" aria-hidden="true">{node.mark.reason}</span>
                        {/if}
                     </span>
                     <span class="marker" aria-hidden="true">{changeKindLabels[node.kind].charAt(0)}</span>
                  </span>
               {/snippet}
            </WithTooltip>
         </li>
      {/if}
   {/each}
{/snippet}

<TooltipGroup>
   <ul
      class="tree"
      role="tree"
      aria-label={label}
      onkeydown={(event) => tree.keydown(event, rows, tabStop, handlers)}
      onclick={(event) => tree.click(event, rows, handlers.activate, handlers.check)}
      onfocusin={(event) => tree.focusin(event)}
   >
      {@render branch(nodes)}
   </ul>
</TooltipGroup>
