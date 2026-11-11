<script lang="ts">
   import { untrack } from 'svelte';
   import * as Popover from '$lib/components/ui/popover';
   import { panelDocument, type DocumentPlace } from '../../src/lib/panelDocument.svelte';

   let { read }: { read: (place: DocumentPlace) => void } = $props();

   untrack(() => read)(panelDocument());
</script>

<!-- An open popover, whose content renders in the portal target that the component's panel gives. -->
<Popover.Root open>
   <Popover.Trigger>Open</Popover.Trigger>
   <Popover.Content>In the panel</Popover.Content>
</Popover.Root>
