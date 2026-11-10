<!-- A stop's kind as a filled pill in the kind's colors, the same in the Plan, the Stop panel and an inline explanation. -->
<script lang="ts">
   import type { HTMLAttributes } from 'svelte/elements';
   import { stopKindLabels } from '../lib/labels';
   import type { StopKind } from '../lib/types';

   let { kind, ...attributes }: { kind: StopKind } & Omit<HTMLAttributes<HTMLSpanElement>, 'class'> = $props();
</script>

<span {...attributes} class="kind-pill" data-kind={kind}>{stopKindLabels[kind] ?? kind}</span>
