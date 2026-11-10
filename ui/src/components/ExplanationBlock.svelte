<!--
   The explanation of the stop under view, in either host. Inline, a diff editor mounts it into its widget below the stop's primary passage, outside the page's component tree, with the stop's title,
   its version banner and its stale notice. In the Stop panel, the panel shows those itself.
-->
<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import type { ExplanationHost, ExplanationView } from '../lib/explanationView.svelte';
   import StopExplanation from './StopExplanation.svelte';
   import StopKindPill from './StopKindPill.svelte';
   import StopStaleNotice from './StopStaleNotice.svelte';
   import StopVersionBanner from './StopVersionBanner.svelte';
   import ThreadMarkers from './ThreadMarkers.svelte';

   let { view, host }: { view: ExplanationView; host: ExplanationHost } = $props();

   const content = $derived(view.content);
</script>

{#snippet explanation()}
   {#if content !== null}
      <StopExplanation
         content={content.stop.content}
         opened={view.sections(host)}
         onfollowreference={(reference) => view.followReference(reference)}
         focusedPassage={content.focusedPassage}
         hover={view.hover}
         atPin={content.atPin}
         stopId={content.stop.id}
         version={content.stop.version}
         threads={content.textThreads}
         onask={view.ask}
         explain={view.explain}
         onopenthread={(threadId) => view.openThread(threadId)}
         answers={view.answers}
         openedCheck={view.openedCheck(host)}
      />
   {/if}
{/snippet}

{#if host === 'panel'}
   {@render explanation()}
{:else if view.stop !== null}
   {@const stop = view.stop}
   <section class="rounded-md border border-l-4 border-l-primary bg-card px-4 py-3 text-card-foreground" aria-label="Explanation of {stop.title}">
      <header class="flex flex-wrap items-baseline gap-x-3">
         <StopKindPill kind={stop.kind} />
         <h3 class="min-w-40 flex-1 font-semibold">{stop.title}</h3>
         <ThreadMarkers threads={content?.markers ?? []} onopenthread={(threadId) => view.openThread(threadId)} />
         {#if view.movable}
            <Button variant="outline" size="sm" class="ml-auto" onclick={() => view.move('side')}>Show in the Stop panel</Button>
         {/if}
      </header>
      {#if view.shownVersion !== null}
         <StopVersionBanner shown={view.shownVersion} onshowcurrent={() => view.showCurrentVersion()} />
      {/if}
      <StopStaleNotice stale={view.stale} />
      {@render explanation()}
   </section>
{/if}
