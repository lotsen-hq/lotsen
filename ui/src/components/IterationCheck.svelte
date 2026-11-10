<!-- The top bar's button that checks the open tour's pull request for a new iteration. It is disabled and its icon turns while the check runs. -->
<script lang="ts">
   import RefreshCw from '@lucide/svelte/icons/refresh-cw';
   import TooltipButton from './TooltipButton.svelte';

   let { running, oncheck }: { running: boolean; oncheck: () => void } = $props();
</script>

<TooltipButton
   tooltip={running ? 'Checking the PR for a new update…' : 'Check the PR for a new update'}
   variant="ghost"
   size="icon-sm"
   class="shrink-0"
   aria-label="Check the PR for a new update"
   aria-busy={running}
   disabled={running}
   onclick={oncheck}
>
   <RefreshCw class={['size-4', running && 'animate-spin']} aria-hidden="true" />
</TooltipButton>
