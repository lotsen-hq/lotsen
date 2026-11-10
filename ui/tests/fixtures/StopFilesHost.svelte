<script lang="ts">
   import type { ComponentProps } from 'svelte';
   import StopFiles from '../../src/components/StopFiles.svelte';
   import { providePaneSplit } from '../../src/lib/paneSplit.svelte';
   import type { StackFile } from '../../src/lib/stopFiles';

   let { files, ...rest }: ComponentProps<typeof StopFiles> = $props();
   providePaneSplit();
   let target = $state.raw<{ key: string } | null>(null);
   // The props' files show while this is undefined, until a test replaces them.
   let replaced = $state.raw<readonly StackFile[] | null | undefined>(undefined);

   export function open(key: string): void {
      target = { key };
   }

   export function replaceFiles(next: readonly StackFile[] | null): void {
      replaced = next;
   }
</script>

<!-- StopFiles with its target and its files in state of their own, so a test changes one prop without the others, and with one pane split for its files, as the page gives it. -->
<StopFiles {...rest} files={replaced === undefined ? files : replaced} {target} />
