<!--
   A one-line repeat of a stop's title and notices at the top of the Stop panel, which the panel shows once the full header has scrolled away.
   The bar overlays the content, so showing it moves nothing. It repeats the full header, so it holds no controls and assistive technology skips it.
-->
<script lang="ts">
   import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
   import type { Stop } from '../lib/types';
   import StopKindPill from './StopKindPill.svelte';

   let {
      stop,
      visible,
      versionNotice,
      staleNotice,
      height = $bindable(0),
   }: {
      stop: Stop;
      /** Whether the bar shows. A hidden bar keeps its layout, so `height` is right for the first scroll that shows it. */
      visible: boolean;
      /** What the stop version notice says, or null when the panel shows no such notice. */
      versionNotice: string | null;
      /** What the stale notice says, or null when the panel shows no such notice. */
      staleNotice: string | null;
      /** The height of the bar in pixels, which the bar writes. */
      height?: number;
   } = $props();
</script>

<div class="sticky top-0 z-10 h-0" aria-hidden="true">
   <div data-stop-bar class={['absolute inset-x-0 top-0 border-b bg-card shadow-sm select-none', !visible && 'invisible']} bind:clientHeight={height}>
      <div class="flex items-baseline gap-2 px-4 py-1 text-sm">
         <StopKindPill kind={stop.kind} />
         <h3 class="min-w-0 flex-1 truncate font-semibold">{stop.title}</h3>
      </div>
      {#if versionNotice !== null}
         <p class="truncate border-t bg-accent px-4 py-0.5 text-xs text-accent-foreground" title={versionNotice}>{versionNotice}</p>
      {/if}
      {#if staleNotice !== null}
         <div class="flex items-center gap-1.5 border-t bg-warning-surface px-4 py-0.5 text-xs text-foreground" title={staleNotice}>
            <TriangleAlert class="size-3.5 shrink-0 text-warning-mark" />
            <p class="min-w-0 truncate">{staleNotice}</p>
         </div>
      {/if}
   </div>
</div>
