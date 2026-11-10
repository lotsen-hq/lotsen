<!-- Renders a component with the editor surfaces that a test gives, so the component's editors are recorded, with the props that the test changes through `props`. -->
<script lang="ts">
   import type { Component } from 'svelte';
   import { provideEditorSurfaces, type EditorSurfaces } from '../../src/lib/editor/surface';
   import type { Reactive } from '../reactive.svelte';

   // eslint-disable-next-line @typescript-eslint/no-explicit-any -- the host renders any component a test gives, with the props the test types itself.
   let { surfaces, component: Shown, props }: { surfaces: EditorSurfaces; component: Component<any>; props: Reactive<Record<string, unknown>> } = $props();

   // svelte-ignore state_referenced_locally
   provideEditorSurfaces(surfaces);
</script>

<Shown {...props.value} />
