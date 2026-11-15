<!-- The icon of a generation phase: an hourglass while the plan waits for the reviewer, a spinner while Claude works. -->
<script lang="ts">
   import Hourglass from '@lucide/svelte/icons/hourglass';
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import type { GenerationPhase } from '../lib/generationStatus';

   let { phase }: { phase: GenerationPhase } = $props();
</script>

{#if phase === 'awaitingApproval'}
   <Hourglass class="size-4 shrink-0" aria-hidden="true" />
{:else}
   <LoaderCircle class="size-4 shrink-0 animate-spin" aria-hidden="true" />
{/if}
