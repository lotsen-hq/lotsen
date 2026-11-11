<!--
   Renders a component with the editor surfaces that a test gives, so the component's editors are recorded, with the props that the test changes through `props`.
   A test that moves the component's panel gives the panel document too.
-->
<script lang="ts">
   import type { Component } from 'svelte';
   import { provideEditorSurfaces, type EditorSurfaces } from '../../src/lib/editor/surface';
   import { providePanelDocument, type PanelDocument } from '../../src/lib/panelDocument.svelte';
   import type { Reactive } from '../reactive.svelte';

   let {
      surfaces,
      component: Shown,
      props,
      panel = null,
   }: {
      surfaces: EditorSurfaces;
      // eslint-disable-next-line @typescript-eslint/no-explicit-any -- the host renders any component a test gives, with the props the test types itself.
      component: Component<any>;
      props: Reactive<Record<string, unknown>>;
      panel?: PanelDocument | null;
   } = $props();

   // svelte-ignore state_referenced_locally
   provideEditorSurfaces(surfaces);
   // svelte-ignore state_referenced_locally
   if (panel !== null) {
      providePanelDocument(panel);
   }
</script>

<Shown {...props.value} />
