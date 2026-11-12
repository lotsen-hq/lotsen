<script module lang="ts">
   import type { DocumentPlace } from '../../src/lib/panelDocument.svelte';
   import PanelReader from './PanelReader.svelte';

   /** What `panelDocument` returned to each `PanelReader` that `readsPanel` rendered, in the order they rendered. */
   export const places: DocumentPlace[] = [];

   export { readsPanel };
</script>

{#snippet readsPanel()}
   <PanelReader read={(place) => places.push(place)} />
{/snippet}
