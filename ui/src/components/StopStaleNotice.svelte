<!-- The notice over a stale stop's explanation: the iteration that made it stale, the iteration that its revision is for, and whether Claude is revising it or it waits for a revision. A stop that is not stale shows none. -->
<script lang="ts">
   import type { StaleStatus } from '../lib/openStop.svelte';
   import { staleNoticeText } from '../lib/stopNotices';
   import WarningNotice from './WarningNotice.svelte';

   let { stale }: { stale: StaleStatus } = $props();

   const text = $derived(staleNoticeText(stale));
</script>

{#if text !== null}
   <WarningNotice role="status" label="Stale stop" class="mt-2">{text}</WarningNotice>
{/if}
