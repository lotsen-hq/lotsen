<script lang="ts">
   import type { KnownText } from '../lib/viewState';
   import type { ThreadMarker } from '../lib/threadView';
   import FileThreads from './FileThreads.svelte';
   import OpenPath from './OpenPath.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      known,
      outsideStop = false,
      threads = [],
      onopenthread = () => {},
      onask = null,
   }: {
      known: KnownText;
      /** Whether the reviewer added the file from outside the open stop. */
      outsideStop?: boolean;
      /** The markers of the threads asked about the file. */
      threads?: readonly ThreadMarker[];
      onopenthread?: (threadId: string) => void;
      /** Hands the file to the Threads panel, or null when the page offers no ask. */
      onask?: (() => void) | null;
   } = $props();

   const commit = $derived(known.state === 'loaded' ? known.text.commit : null);
</script>

<OpenPath path={known.path} />
{#if outsideStop}
   <span class="shrink-0 rounded-sm border px-1 text-xs text-muted-foreground">Outside the stop</span>
{/if}
<WithTooltip text={commit}>
   {#snippet trigger({ props })}
      <span {...props} class="shrink-0 font-mono text-sm text-muted-foreground">{known.side}{commit === null ? '' : ` ${commit.slice(0, 8)}`}</span>
   {/snippet}
</WithTooltip>
<FileThreads name={known.side === 'base' ? `${known.path} at the base` : known.path} {threads} {onopenthread} {onask} />
