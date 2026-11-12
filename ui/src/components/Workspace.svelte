<script lang="ts">
   import { createDockview, themeLight, type DockviewApi, type DockviewGroupPanel, type IHeaderActionsRenderer, type ITabRenderer } from 'dockview';
   import { getAllContexts, mount, onMount, unmount, type Snippet } from 'svelte';
   import { cspNonce } from '../lib/cspNonce';
   import { savedLayout } from '../lib/layoutRecord';
   import { panelIds, type PanelId, type WorkspaceLayout } from '../lib/workspaceLayout.svelte';
   import GroupActions from './GroupActions.svelte';

   type LeftPanel = 'files' | 'plan';

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

   // The group headers mount their own components, which need the page's contexts, such as its tooltip delays.
   const contexts = getAllContexts();

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

   /** Lays out the Default layout: Files and Plan as tabs on the left, the code in the center, and the stop above the threads on the right. */
   function defaultLayout(dock: DockviewApi): void {
      dock.clear();
      const title = (id: PanelId) => layout.titles[id];
      dock.addPanel({ id: 'files', component: 'files', title: title('files') });
      dock.addPanel({ id: 'plan', component: 'plan', title: title('plan'), position: { referencePanel: 'files', direction: 'within' } });
      dock.addPanel({ id: 'diff', component: 'diff', title: title('diff'), position: { referencePanel: 'files', direction: 'right' } });
      dock.addPanel({ id: 'stop', component: 'stop', title: title('stop'), position: { referencePanel: 'diff', direction: 'right' } });
      dock.addPanel({ id: 'threads', component: 'threads', title: title('threads'), position: { referencePanel: 'stop', direction: 'below' } });
      dock.getPanel('files')!.group.api.setSize({ width: dock.width * 0.22 });
      dock.getPanel('stop')!.group.api.setSize({ width: dock.width * 0.28, height: dock.height * 0.6 });
   }

   onMount(() => {
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
         // The panels render in the hidden holder below, inside the page's component tree, and Dockview moves each one's element into its group.
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

      // Dockview hides a group by giving it no size (ViewItem.setVisible in dockview-core 8.9.0's splitview.ts), so the hidden attribute also takes the group out of
      // the tab order and the accessibility tree.
      const follow = (group: DockviewGroupPanel) => group.api.onDidVisibilityChange((event) => (group.element.hidden = !event.isVisible));
      const visibility = [dock.onDidAddGroup(follow)];

      const detach = layout.attach(dock, (record) => savedLayout.write(record));
      const saved = savedLayout.read();
      if (saved === null || !layout.load(saved)) {
         defaultLayout(dock);
      }

      visibility.push(...dock.groups.map(follow));
      for (const group of dock.groups) {
         group.element.hidden = !group.api.isVisible;
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
      root.addEventListener('click', openTab);
      root.addEventListener('keydown', keepPanel, true);

      const choices = dock.onDidActivePanelChange((event) => {
         if (!applying && (event.panel?.id === 'files' || event.panel?.id === 'plan')) {
            chosen[tourId ?? ''] = event.panel.id;
         }
      });
      api = dock;
      return () => {
         resizes.disconnect();
         root.removeEventListener('click', openTab);
         root.removeEventListener('keydown', keepPanel, true);
         choices.dispose();
         visibility.forEach((subscription) => subscription.dispose());
         detach();
         dock.dispose();
      };
   });

   $effect(() => layout.setTitle('diff', diffTitle));

   // A panel the reviewer picked of Files and Plan holds for that tour, and the default applies until then.
   const chosen: Record<string, LeftPanel> = {};
   let applying = false;
   $effect(() => {
      const shown = chosen[tourId ?? ''] ?? defaultLeft;
      const panel = api?.getPanel(shown);
      if (panel && panel.group === api?.getPanel(shown === 'files' ? 'plan' : 'files')?.group && panel.group.activePanel !== panel) {
         applying = true;
         panel.api.setActive();
         applying = false;
      }
   });
</script>

<div bind:this={frame} class="min-h-0 flex-1 overflow-hidden">
   <div bind:this={root}></div>
</div>

<div hidden>
   {#each panelIds as id (id)}
      <div
         bind:this={elements[id]}
         class={['h-full', id === 'diff' ? 'flex flex-col' : 'scroll-box', (id === 'files' || id === 'plan') && 'text-base', (id === 'stop' || id === 'threads') && 'bg-card']}
      >
         {@render { files, plan, diff, stop, threads }[id]()}
      </div>
   {/each}
</div>
