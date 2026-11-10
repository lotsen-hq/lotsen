<script lang="ts">
   import { fileMarkLabels } from '../lib/labels';
   import type { ThreadMarker } from '../lib/threadView';
   import type { FileMark } from '../lib/types';
   import FileThreads from './FileThreads.svelte';
   import OpenPath from './OpenPath.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      path,
      oldPath = null,
      mark = null,
      outsideStop = false,
      threads = [],
      onopenthread = () => {},
      onask = null,
   }: {
      path: string;
      /** A renamed file's path at the base, or null for any other file. */
      oldPath?: string | null;
      /** Claude's mark on the file, or null for none. */
      mark?: FileMark | null;
      /** Whether the reviewer added the file from outside the open stop. */
      outsideStop?: boolean;
      /** The markers of the threads asked about the file. */
      threads?: readonly ThreadMarker[];
      onopenthread?: (threadId: string) => void;
      /** Hands the file to the Threads panel, or null when the page offers no ask. */
      onask?: (() => void) | null;
   } = $props();
</script>

<OpenPath path={oldPath === null ? path : `${oldPath} → ${path}`} />
{#if outsideStop}
   <span class="shrink-0 rounded-sm border px-1 text-xs text-muted-foreground">Outside the stop</span>
{/if}
{#if mark !== null}
   <WithTooltip text={mark.reason}>
      {#snippet trigger({ props })}
         <span {...props} class="shrink-0 rounded-sm border px-1 text-xs text-muted-foreground">{fileMarkLabels[mark.mark]}</span>
      {/snippet}
   </WithTooltip>
{/if}
<FileThreads name={path} {threads} {onopenthread} {onask} />
