<script lang="ts">
   import TooltipButton from '../../src/components/TooltipButton.svelte';
   import type { FileMark } from '../../src/lib/types';

   let { path, mark = null }: { path: string; mark?: FileMark | null } = $props();
</script>

<!-- DiffHeading with a TooltipButton whose `disabled` reads the mark, so the button unmounts in the update that changes what `disabled` reads. -->
<code>{path}</code>
{#if mark !== null}
   <TooltipButton tooltip="The mark" disabled={mark.reason !== ''}>{mark.mark}</TooltipButton>
{/if}
