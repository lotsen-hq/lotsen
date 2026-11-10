<!-- The markers of the threads asked about one place, such as a file's heading or a field of a stop's text. Each opens its thread in the Threads panel. -->
<script lang="ts">
   import MessageSquare from '@lucide/svelte/icons/message-square';
   import type { ThreadMarker } from '../lib/threadView';

   let { threads, onopenthread }: { threads: readonly ThreadMarker[]; onopenthread: (threadId: string) => void } = $props();
</script>

{#if threads.length > 0}
   <span class="thread-markers">
      {#each threads as thread (thread.id)}
         <button type="button" class="thread-marker" data-thread-id={thread.id} aria-label="Open the thread: {thread.label}" onclick={() => onopenthread(thread.id)}>
            <MessageSquare aria-hidden="true" />
            <span>{thread.label}</span>
         </button>
      {/each}
   </span>
{/if}
