<!-- A file heading's part of asking Claude: the markers of the threads about the file, and the button that asks about it. -->
<script lang="ts">
   import MessageSquarePlus from '@lucide/svelte/icons/message-square-plus';
   import type { ThreadMarker } from '../lib/threadView';
   import ThreadMarkers from './ThreadMarkers.svelte';
   import TooltipButton from './TooltipButton.svelte';

   let {
      name,
      threads,
      onopenthread,
      onask,
   }: {
      /** The file as the heading names it. */
      name: string;
      threads: readonly ThreadMarker[];
      onopenthread: (threadId: string) => void;
      /** Hands the file to the Threads panel, or null when the page offers no ask. */
      onask: (() => void) | null;
   } = $props();
</script>

<ThreadMarkers {threads} {onopenthread} class="in-file-heading" />
{#if onask !== null}
   <TooltipButton variant="ghost" size="icon-xs" class="shrink-0" tooltip="Ask Claude about this file" aria-label="Ask Claude about {name}" onclick={onask}>
      <MessageSquarePlus class="size-4" aria-hidden="true" />
   </TooltipButton>
{/if}
