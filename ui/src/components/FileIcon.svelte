<script lang="ts">
   import { fileIcon, setiColors } from '../lib/fileIcons';
   import type { RepositoryEntryType } from '../lib/types';

   let { path, entry = 'file' }: { path: string; entry?: RepositoryEntryType } = $props();

   const icon = $derived(fileIcon(path, entry));
</script>

{#if icon.kind === 'seti'}
   <span class="icon seti" data-icon={icon.icon.name} aria-hidden="true" {@attach setiColors(icon.icon)}>{icon.icon.glyph}</span>
{:else}
   <span class="icon" aria-hidden="true"><icon.component /></span>
{/if}
