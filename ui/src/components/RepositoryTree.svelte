<script lang="ts">
   import ChevronRight from '@lucide/svelte/icons/chevron-right';
   import Folder from '@lucide/svelte/icons/folder';
   import FolderOpen from '@lucide/svelte/icons/folder-open';
   import { onMount } from 'svelte';
   import { SvelteMap, SvelteSet } from 'svelte/reactivity';
   import { Button } from '$lib/components/ui/button';
   import type { KnownDirectory } from '../lib/viewState';
   import { holds } from '../lib/fileTree';
   import { changeKindLabels, repositoryEntryTags } from '../lib/labels';
   import { repositoryNodes, repositoryRows, type DirectoryState, type RepositoryTreeNode } from '../lib/repositoryTree';
   import { TreeFocus } from '../lib/treeFocus.svelte';
   import type { ChangedFile } from '../lib/types';
   import FileIcon from './FileIcon.svelte';
   import TooltipGroup from './TooltipGroup.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      listDirectory,
      changed,
      selectedPath,
      onopen,
   }: {
      /** Lists a directory at the tree's side, or the root directory for null. */
      listDirectory: (path: string | null) => Promise<KnownDirectory>;
      /** The changed files by their path at the tree's side. */
      changed: ReadonlyMap<string, ChangedFile>;
      selectedPath: string | null;
      onopen: (path: string) => void;
   } = $props();

   // Every folder starts collapsed and lists its entries the first time it expands, so a large repository costs one request per folder the reviewer opens.
   const listings = new SvelteMap<string, DirectoryState>();
   const expanded = new SvelteSet<string>();
   const tree = new TreeFocus();

   const rows = $derived(repositoryRows(listings, expanded));
   const tabStop = $derived(tree.tabStop(rows, selectedPath));
   const root = $derived(listings.get(''));

   async function load(path: string) {
      listings.set(path, { state: 'loading' });
      const known = await listDirectory(path === '' ? null : path);
      listings.set(path, known.state === 'loaded' ? { state: 'loaded', nodes: repositoryNodes(known.listing.entries) } : known);
   }

   // A folder whose listing failed lists again each time it expands.
   function expand(node: RepositoryTreeNode) {
      expanded.add(node.id);
      const state = listings.get(node.path)?.state;
      if (state !== 'loaded' && state !== 'loading') {
         void load(node.path);
      }
   }

   const handlers = {
      activate: (node: RepositoryTreeNode) => {
         if (node.type === 'folder') {
            if (expanded.has(node.id)) {
               expanded.delete(node.id);
            } else {
               expand(node);
            }
         } else if (node.entry !== 'submodule') {
            onopen(node.path);
         }
      },
      expand,
      collapse: (node: RepositoryTreeNode) => expanded.delete(node.id),
   };

   onMount(() => void load(''));

   function label(node: RepositoryTreeNode, change: ChangedFile | undefined): string {
      const tag = repositoryEntryTags[node.entry];
      return `${node.name}${change ? `, ${changeKindLabels[change.kind]}` : ''}${tag ? `, ${tag}` : ''}`;
   }
</script>

{#snippet status(state: DirectoryState | undefined)}
   {#if state?.state === 'failed'}
      <p>{state.message}</p>
      {#if state.reason}
         <p>{state.reason}</p>
      {/if}
   {:else}
      <p>Loading…</p>
   {/if}
{/snippet}

{#snippet branch(path: string)}
   {@const listing = listings.get(path)}
   {#if listing?.state === 'loaded'}
      {#each listing.nodes as node (node.id)}
         {#if node.type === 'folder'}
            {@const isExpanded = expanded.has(node.id)}
            {@const current = !isExpanded && holds(node.path, selectedPath)}
            <!-- Folders cannot be selected, so they carry no aria-selected. -->
            <!-- svelte-ignore a11y_role_has_required_aria_props -->
            <li role="treeitem" data-id={node.id} aria-label={node.name} aria-expanded={isExpanded} aria-current={current ? 'true' : undefined} tabindex={tabStop === node.id ? 0 : -1}>
               <WithTooltip text={node.path}>
                  {#snippet trigger({ props })}
                     <span {...props} class="row" class:current>
                        <span class="twisty" aria-hidden="true"><ChevronRight class={[isExpanded && 'rotate-90']} /></span>
                        <span class="icon text-folder" aria-hidden="true">
                           {#if isExpanded}<FolderOpen />{:else}<Folder />{/if}
                        </span>
                        <span class="label"><span class="name">{node.name}</span></span>
                     </span>
                  {/snippet}
               </WithTooltip>
               {#if isExpanded}
                  <ul role="group">
                     {@render branch(node.path)}
                  </ul>
               {/if}
            </li>
         {:else}
            {@const change = changed.get(node.path)}
            {@const tag = repositoryEntryTags[node.entry]}
            <li
               role="treeitem"
               data-id={node.id}
               aria-label={label(node, change)}
               aria-selected={node.path === selectedPath}
               aria-current={node.path === selectedPath ? 'true' : undefined}
               aria-disabled={node.entry === 'submodule' ? 'true' : undefined}
               tabindex={tabStop === node.id ? 0 : -1}
            >
               <WithTooltip text={node.path}>
                  {#snippet trigger({ props })}
                     <span {...props} class="row {change ? `kind-${change.kind}` : ''}" class:current={node.path === selectedPath}>
                        <span class="twisty" aria-hidden="true"></span>
                        <FileIcon path={node.path} entry={node.entry} />
                        {#if tag}
                           <span class="tag" aria-hidden="true">{tag}</span>
                        {/if}
                        <span class="label"><span class="name">{node.name}</span></span>
                        {#if change}
                           <span class="marker" aria-hidden="true">{changeKindLabels[change.kind].charAt(0)}</span>
                        {/if}
                     </span>
                  {/snippet}
               </WithTooltip>
            </li>
         {/if}
      {/each}
   {:else}
      <li role="none" class={['px-4 py-0.5', listing?.state === 'failed' ? 'text-destructive' : 'text-muted-foreground']}>
         {@render status(listing)}
      </li>
   {/if}
{/snippet}

{#if root?.state === 'loaded'}
   <TooltipGroup>
      <ul
         class="tree"
         role="tree"
         aria-label="Repository files"
         onkeydown={(event) => tree.keydown(event, rows, tabStop, handlers)}
         onclick={(event) => tree.click(event, rows, handlers.activate)}
         onfocusin={(event) => tree.focusin(event)}
      >
         {@render branch('')}
      </ul>
   </TooltipGroup>
{:else}
   <div class={['px-4 py-0.5', root?.state === 'failed' ? 'text-destructive' : 'text-muted-foreground']} role={root?.state === 'failed' ? 'alert' : undefined}>
      {@render status(root)}
      {#if root?.state === 'failed'}
         <Button variant="outline" size="sm" class="mt-1" onclick={() => void load('')}>Try again</Button>
      {/if}
   </div>
{/if}
