<script lang="ts">
   import type { ComponentProps } from 'svelte';
   import ThreadActionsScope from '../../src/components/ThreadActionsScope.svelte';
   import OpenThread from '../../src/components/OpenThread.svelte';
   import type { ThreadActions } from '../../src/lib/threadActions.svelte';

   let { actions, ...item }: ComponentProps<typeof OpenThread> & { actions: ThreadActions } = $props();
</script>

<!-- A thread view with the tour's thread actions, as the page gives them through its Threads panel. -->
<ThreadActionsScope {actions}>
   <OpenThread {...item} />
</ThreadActionsScope>
