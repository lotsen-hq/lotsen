<script lang="ts">
   import { createDockview, themeLight, type DockviewApi, type DockviewGroupPanel, type IHeaderActionsRenderer, type ITabRenderer } from 'dockview';
   import { getAllContexts, mount, onMount, unmount, type Snippet } from 'svelte';
   import { arrangementRecord } from '../lib/arrangements';
   import { cspNonce } from '../lib/cspNonce';
   import { savedLayout } from '../lib/layoutRecord';
   import { PanelDocument } from '../lib/panelDocument.svelte';
   import type { WorkspaceLayout } from '../lib/workspaceLayout.svelte';
   import { panelIds, type PanelId } from '../lib/workspacePanels';
   import GroupActions from './GroupActions.svelte';
   import PanelContent from './PanelContent.svelte';

   type LeftPanel = 'files' | 'plan';

   const themeAttributes = ['class', 'data-color-scheme', 'data-syntax-colors'];

   let {
      layout,
      tourId,
      defaultLeft,
      diffTitle,
      files,
      plan,
      diff,
      stop,
      threads,
   }: {
      layout: WorkspaceLayout;
      /** The selected tour, or null while none is. */
      tourId: string | null;
      /** The panel of Files and Plan that a tour opens on while they share a group, until the reviewer picks one for that tour. */
      defaultLeft: LeftPanel;
      /** The title of the diff's tab. */
      diffTitle: string;
      files: Snippet;
      plan: Snippet;
      /** The diff, which renders its own toolbar. */
      diff: Snippet;
      stop: Snippet;
      threads: Snippet;
   } = $props();

   let frame: HTMLElement;
   let root: HTMLElement;
   const elements = $state<Partial<Record<PanelId, HTMLElement>>>({});
   let api = $state<DockviewApi | null>(null);

   // The group headers and the panels mount their own components, which need the page's contexts, such as its tooltip delays.
   const contexts = getAllContexts();

   /** Keeps the root element of a popout window's page in step with the theme class, the color scheme and the syntax color family of this page's root, which the palettes in `app.css` key on. */
   function followTheme(popout: Window): () => void {
      const source = document.documentElement;
      const copy = () => {
         for (const name of themeAttributes) {
            const value = source.getAttribute(name);
            if (value === null) {
               popout.document.documentElement.removeAttribute(name);
            } else {
               popout.document.documentElement.setAttribute(name, value);
            }
         }
      };
      const changes = new MutationObserver(copy);
      changes.observe(source, { attributes: true, attributeFilter: themeAttributes });
      copy();
      return () => changes.disconnect();
   }

   function groupActions(group: DockviewGroupPanel): IHeaderActionsRenderer {
      const element = document.createElement('div');
      element.className = 'h-full';
      let actions: ReturnType<typeof mount> | null = null;
      return {
         element,
         init: () => (actions = mount(GroupActions, { target: element, props: { layout, group }, context: contexts })),
         dispose: () => void (actions && unmount(actions)),
      };
   }

   function panelTab(): ITabRenderer {
      const element = document.createElement('div');
      element.className = 'dv-default-tab';
      const title = document.createElement('div');
      title.className = 'dv-default-tab-content';
      element.append(title);
      let subscription: { dispose(): void } | null = null;
      return {
         element,
         init: (parameters) => {
            title.textContent = parameters.title;
            subscription = parameters.api.onDidTitleChange((event) => (title.textContent = event.title));
         },
         dispose: () => subscription?.dispose(),
      };
   }

   onMount(() => {
      // Svelte listens for delegated events at a root's mount target and the main document (mount in svelte 5.57.2's render.js), so each panel is a root of its own, whose
      // handlers still run in a popout window, as popout.spec.ts checks.
      const snippets = { files, plan, diff, stop, threads };
      const panels = Object.fromEntries(panelIds.map((id) => [id, new PanelDocument(elements[id]!)])) as Record<PanelId, PanelDocument>;
      const contents = panelIds.map((id) => mount(PanelContent, { target: elements[id]!, props: { content: snippets[id], panel: panels[id] }, context: contexts }));

      // Dockview rounds the sizes it measures to whole pixels (watchElementResize in dockview-core 8.9.0's resizable.ts and shell.ts), which lays the groups out past a
      // container of a fractional size, so the container takes the frame's size rounded down.
      const fit = () => {
         const { width, height } = frame.getBoundingClientRect();
         root.style.width = `${Math.floor(width)}px`;
         root.style.height = `${Math.floor(height)}px`;
      };
      const resizes = new ResizeObserver(fit);
      resizes.observe(frame);
      fit();

      const dock = createDockview(root, {
         theme: themeLight,
         disableFloatingGroups: true,
         nonce: (document) => cspNonce(document),
         // The panels render in the hidden holder below, and Dockview moves each one's element into its group.
         createComponent: (options) => ({ element: elements[options.name as PanelId]!, init: () => {} }),
         // Dockview calls createTabComponent only for a tab with a component name (createTabComponent in dockview-core 8.9.0's dockviewPanelModel.ts).
         defaultTabComponent: 'title',
         createTabComponent: () => panelTab(),
         createRightHeaderActionComponent: groupActions,
      });
      // Dockview's live regions announce through aria-live alone, so the page's own status and alert elements stay the only ones with those roles.
      for (const region of root.querySelectorAll('[aria-live]')) {
         region.removeAttribute('role');
      }

      // Dockview opens a tab on a pointer press, so a click without one, as from a screen reader, opens it here.
      const openTab = (event: MouseEvent) => {
         const id = event.target instanceof Element ? event.target.closest('[data-tab-panel-id]')?.getAttribute('data-tab-panel-id') : null;
         if (id) {
            dock.getPanel(id)?.api.setActive();
         }
      };
      // Delete and Backspace on a tab would close its panel for good, so they do nothing. The group's Hide button hides a panel.
      const keepPanel = (event: KeyboardEvent) => {
         if ((event.key === 'Delete' || event.key === 'Backspace') && event.target instanceof Element && event.target.matches('[role="tab"]')) {
            event.stopPropagation();
         }
      };
      const handleTabs = (target: GlobalEventHandlers) => {
         target.addEventListener('click', openTab);
         target.addEventListener('keydown', keepPanel, true);
         return () => {
            target.removeEventListener('click', openTab);
            target.removeEventListener('keydown', keepPanel, true);
         };
      };
      const unhandleRoot = handleTabs(root);

      // A popout window's tabs sit in its own document, outside the root, so its document gets the same tab handlers.
      const detach = layout.attach(
         dock,
         (record) => savedLayout.write(record),
         (popout) => {
            const unfollow = followTheme(popout);
            const unhandle = handleTabs(popout.document);
            return () => (unfollow(), unhandle());
         },
      );
      const saved = savedLayout.read();
      // The Default layout stays unsaved, so it never takes the server's key from a saved layout that the browser storage of another origin holds.
      if (saved === null || !layout.load(saved)) {
         layout.load(arrangementRecord('default', layout.titles));
      }

      // Each panel's document follows its element on the onDidMovePanel that WorkspaceLayout.attach describes.
      const moves = dock.onDidMovePanel(({ panel }) => panels[panel.id as PanelId]?.follow());

      const choices = dock.onDidActivePanelChange((event) => {
         if (!applying && (event.panel?.id === 'files' || event.panel?.id === 'plan')) {
            chosen[tourId ?? ''] = event.panel.id;
         }
      });
      api = dock;
      return () => {
         resizes.disconnect();
         unhandleRoot();
         choices.dispose();
         moves.dispose();
         detach();
         dock.dispose();
         contents.forEach((content) => unmount(content));
         panelIds.forEach((id) => panels[id].dispose());
      };
   });

   $effect(() => layout.setTitle('diff', diffTitle));

   // A panel the reviewer picked of Files and Plan holds for that tour, and the default applies until then, also after a layout loads.
   const chosen: Record<string, LeftPanel> = {};
   let applying = false;
   $effect(() => {
      void layout.loads;
      const shown = chosen[tourId ?? ''] ?? defaultLeft;
      const panel = api?.getPanel(shown);
      if (panel && panel.group === api?.getPanel(shown === 'files' ? 'plan' : 'files')?.group && panel.group.activePanel !== panel) {
         applying = true;
         layout.quietly(() => panel.api.setActive());
         applying = false;
      }
   });
</script>

<!-- `isolate` keeps Dockview's z-indexes below the overlays that bits-ui portals to the body. The tour picker test in `workspace.spec.ts` pins it. -->
<div bind:this={frame} class="isolate min-h-0 flex-1 overflow-hidden">
   <div bind:this={root}></div>
</div>

<div hidden>
   {#each panelIds as id (id)}
      <div
         bind:this={elements[id]}
         class={['h-full', id === 'diff' ? 'flex flex-col' : 'scroll-box', (id === 'files' || id === 'plan') && 'text-base', (id === 'stop' || id === 'threads') && 'bg-card']}
      ></div>
   {/each}
</div>
