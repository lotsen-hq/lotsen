<script lang="ts">
   import type { ComponentProps } from 'svelte';
   import ThreadActionsScope from '../../src/components/ThreadActionsScope.svelte';
   import ThreadsPanel from '../../src/components/ThreadsPanel.svelte';
   import type { ThreadActions } from '../../src/lib/threadActions.svelte';

   let { actions, ...panel }: ComponentProps<typeof ThreadsPanel> & { actions: ThreadActions } = $props();
</script>

<!-- A Threads panel with the tour's thread actions, as the page gives them. -->
<ThreadActionsScope {actions}>
   <ThreadsPanel {...panel} />
</ThreadActionsScope>
