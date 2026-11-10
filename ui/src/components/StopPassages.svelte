<!-- A stop's passages as a table, one button per row: the role bar, the label, the file name and the lines. A passage that is not at the pin does not open. -->
<script lang="ts">
   import { linesLabel, passageRoleLabels } from '../lib/labels';
   import { atPin, type PinCommits } from '../lib/pin';
   import type { CodeReference, StopPassage } from '../lib/types';
   import TooltipGroup from './TooltipGroup.svelte';
   import WithTooltip from './WithTooltip.svelte';

   let {
      passages,
      onopenpassage,
      commits = null,
   }: {
      passages: readonly StopPassage[];
      onopenpassage: (passage: CodeReference) => void;
      /** The commits of the tour's pin, against which {@link atPin} tells the passages that open, or null while the tour is not pinned. */
      commits?: PinCommits | null;
   } = $props();

   const fileName = (path: string) => path.slice(path.lastIndexOf('/') + 1);
   const lineNumbers = ({ side, startLine, endLine }: StopPassage) => `${side === 'base' ? 'base ' : ''}${startLine === endLine ? startLine : `${startLine}–${endLine}`}`;

   // The name carries the role that the bar shows only by its color.
   function accessibleName(passage: StopPassage): string {
      const role = passageRoleLabels[passage.role].toLowerCase();
      return [...(passage.label ? [passage.label] : []), role, passage.path, linesLabel(passage), ...(atPin(passage, commits) ? [] : ['at an earlier commit'])].join(', ');
   }
</script>

<TooltipGroup>
   <ul class="stop-passages" aria-label="Code">
      {#each passages as passage, index (index)}
         {@const opens = atPin(passage, commits)}
         <li>
            <!-- aria-disabled keeps the row's tooltips, which a disabled button would not show. -->
            <button type="button" aria-label={accessibleName(passage)} aria-disabled={opens ? undefined : 'true'} onclick={() => opens && onopenpassage(passage)}>
               <WithTooltip text={passageRoleLabels[passage.role]}>
                  {#snippet trigger({ props })}
                     <span {...props} class="role" data-role={passage.role}></span>
                  {/snippet}
               </WithTooltip>
               {#if passage.label}
                  <span class="label" data-cell>{passage.label}</span>
               {:else}
                  <span class="label unlabeled" data-cell>{passageRoleLabels[passage.role].toLowerCase()}</span>
               {/if}
               <WithTooltip text={passage.path}>
                  {#snippet trigger({ props })}
                     <span {...props} class="file" data-cell>{fileName(passage.path)}</span>
                  {/snippet}
               </WithTooltip>
               <span class="lines" data-cell>{lineNumbers(passage)}</span>
            </button>
         </li>
      {/each}
   </ul>
</TooltipGroup>
