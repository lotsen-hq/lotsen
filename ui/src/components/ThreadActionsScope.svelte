<!--
   Gives the components inside the thread actions of one tour. A scope keeps the actions it was created with, so an owner that changes tours creates a new scope.
-->
<script lang="ts">
   import { untrack, type Snippet } from 'svelte';
   import { setThreadActions, type ThreadActions } from '../lib/threadActions.svelte';

   let { actions, children }: { actions: ThreadActions; children: Snippet } = $props();

   setThreadActions(untrack(() => actions));
</script>

{@render children()}
