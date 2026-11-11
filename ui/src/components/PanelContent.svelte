<script lang="ts">
   import { BitsConfig } from 'bits-ui';
   import { untrack, type Snippet } from 'svelte';
   import { providePanelDocument, type PanelDocument } from '../lib/panelDocument.svelte';

   let {
      content,
      panel,
   }: {
      content: Snippet;
      /** The panel document of the panel, which the workspace creates once and never replaces. */
      panel: PanelDocument;
   } = $props();

   providePanelDocument(untrack(() => panel));
</script>

<!-- bits-ui finds a portal target given by a selector in the main page's document, so the panel's portals take its portal target as an element. -->
<BitsConfig defaultPortalTo={panel.portalTarget}>
   {@render content()}
</BitsConfig>
