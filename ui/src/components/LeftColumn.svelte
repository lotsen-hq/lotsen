<script lang="ts">
   import type { Snippet } from 'svelte';
   import * as Tabs from '$lib/components/ui/tabs';

   type Tab = 'plan' | 'files';

   let {
      tourId,
      defaultTab,
      plan,
      files,
      actions,
   }: {
      tourId: string | null;
      defaultTab: Tab;
      plan: Snippet;
      files: Snippet;
      /** The column's buttons, at the end of the tab row. */
      actions: Snippet;
   } = $props();

   const tabs: { id: Tab; label: string }[] = [
      { id: 'files', label: 'Files' },
      { id: 'plan', label: 'Plan' },
   ];

   // A tab the user chose holds for that tour, and the default applies until then.
   const chosen = $state<Record<string, Tab>>({});
   const active = $derived(chosen[tourId ?? ''] ?? defaultTab);

   function choose(tab: Tab) {
      chosen[tourId ?? ''] = tab;
   }

   // Tabs reports only a change of tab, so a click or key press on the tab already shown records the choice here.
   function onkeydown(event: KeyboardEvent, tab: Tab) {
      if (event.key === 'Enter' || event.key === ' ') {
         choose(tab);
      }
   }
</script>

<section class="flex h-full min-h-0 flex-col" aria-label="Tour">
   <Tabs.Root bind:value={() => active, (tab) => choose(tab as Tab)} class="min-h-0 flex-1 gap-0">
      <div class="flex items-center border-b pr-2">
         <Tabs.List variant="line" class="flex-1 justify-start gap-0 rounded-none p-0 group-data-horizontal/tabs:h-auto">
            {#each tabs as tab (tab.id)}
               <Tabs.Trigger
                  value={tab.id}
                  onclick={() => choose(tab.id)}
                  onkeydown={(event) => onkeydown(event, tab.id)}
                  class="h-auto flex-none rounded-none px-4 py-2 text-base font-normal text-muted-foreground after:bg-primary group-data-horizontal/tabs:after:bottom-0 focus-visible:ring-inset"
               >
                  {tab.label}
               </Tabs.Trigger>
            {/each}
         </Tabs.List>
         {@render actions()}
      </div>
      <!-- Both panels stay mounted while hidden, so the file tree keeps its expanded folders across tab switches. -->
      <!-- The plan's stops and the file tree's rows take focus themselves, so neither panel is a tab stop. -->
      <Tabs.Content value="plan" tabindex={-1} class="scroll-box min-h-0 text-base">
         {@render plan()}
      </Tabs.Content>
      <Tabs.Content value="files" tabindex={-1} class="scroll-box min-h-0 text-base">
         {@render files()}
      </Tabs.Content>
   </Tabs.Root>
</section>
