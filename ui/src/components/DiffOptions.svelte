<script lang="ts">
   import Columns2 from '@lucide/svelte/icons/columns-2';
   import FoldVertical from '@lucide/svelte/icons/fold-vertical';
   import Highlighter from '@lucide/svelte/icons/highlighter';
   import Space from '@lucide/svelte/icons/space';
   import SunDim from '@lucide/svelte/icons/sun-dim';
   import type { DiffLayout } from '../lib/diffLayout';
   import IconToggle from './IconToggle.svelte';

   let {
      layout = null,
      onlayout = () => {},
      ignoreWhitespace = null,
      onignorewhitespace = () => {},
      collapse = null,
      oncollapse = () => {},
      highlight,
      onhighlight,
      dim,
      ondim,
   }: {
      /** The diff's layout, or null to leave out the toggle, as for a file view. */
      layout?: DiffLayout | null;
      onlayout?: (layout: DiffLayout) => void;
      /** Whether the diff ignores whitespace, or null to leave out the toggle, as for a file view. */
      ignoreWhitespace?: boolean | null;
      onignorewhitespace?: (ignore: boolean) => void;
      /** Whether the diff collapses the regions away from the stop's passages, or null to leave out the toggle, as for a file without passages. */
      collapse?: boolean | null;
      oncollapse?: (collapse: boolean) => void;
      highlight: boolean;
      onhighlight: (highlight: boolean) => void;
      dim: boolean;
      ondim: (dim: boolean) => void;
   } = $props();
</script>

<div class="flex shrink-0 *:not-first:border-l-0" role="group" aria-label="Diff options">
   {#if layout !== null}
      <!-- With two layouts, one toggle does: released, it means inline. -->
      <IconToggle label="Side by side" icon={Columns2} pressed={layout === 'sideBySide'} onclick={() => onlayout(layout === 'sideBySide' ? 'inline' : 'sideBySide')} />
   {/if}
   {#if collapse !== null}
      <IconToggle label="Collapse to passages" icon={FoldVertical} pressed={collapse} onclick={() => oncollapse(!collapse)} />
   {/if}
   <IconToggle label="Highlight the passages" icon={Highlighter} pressed={highlight} onclick={() => onhighlight(!highlight)} />
   <IconToggle label="Dim outside the passages" icon={SunDim} pressed={dim} onclick={() => ondim(!dim)} />
   {#if ignoreWhitespace !== null}
      <IconToggle label="Ignore whitespace" icon={Space} pressed={ignoreWhitespace} onclick={() => onignorewhitespace(!ignoreWhitespace)} />
   {/if}
</div>
