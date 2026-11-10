<script lang="ts">
   import type { Snippet } from 'svelte';
   import * as Tooltip from '$lib/components/ui/tooltip';
   import { tooltipGroup } from '../lib/tooltip';
   import { mergeProps } from 'svelte-toolbelt';
   import TooltipScope from './TooltipScope.svelte';

   let {
      text,
      trigger,
      deferEscape = false,
      // Other props, such as an outer trigger's, merge into the props that the trigger receives.
      ...attributes
   }: Record<string, unknown> & {
      /** The tooltip's text, in which a newline breaks the line, or null to show the trigger without a tooltip. A tooltip is no accessible name, so an icon button still needs its aria-label. */
      text: string | null;
      /** Renders the element that shows the tooltip on hover and keyboard focus, spreading props onto it. */
      trigger: Snippet<[{ props: Record<string, unknown> }]>;
      /** Lets Escape close the popover or dialog that holds the trigger, not the tooltip first. Outside a TooltipGroup only. */
      deferEscape?: boolean;
   } = $props();

   const group = tooltipGroup();
   // bits-ui reads the props of `Tooltip.Trigger` as it unmounts, and a read of state that changes in that update stalls rendering (`tooltipButtonTeardown.test.ts`).
   // So `Tooltip.Trigger` gets only values that never change, such as this payload. `attributes`, whose reads run the callers' expressions, merge into the props that `trigger` receives.
   const readText = () => text ?? '';

   // The element keeps its own focusability and type, or those that `attributes` give it: the trigger's would make a tree row a tab stop and give a span a button's type.
   function withoutFocusAndType(props: Record<string, unknown>): Record<string, unknown> {
      const rest = { ...props };
      delete rest.tabindex;
      delete rest.type;
      return rest;
   }
</script>

{#snippet triggerFor(tether: typeof group)}
   <Tooltip.Trigger {tether} payload={readText}>
      {#snippet child({ props })}
         {@render trigger({ props: mergeProps(attributes, withoutFocusAndType(props)) })}
      {/snippet}
   </Tooltip.Trigger>
{/snippet}

{#if text === null}
   {@render trigger({ props: attributes })}
{:else if group}
   {@render triggerFor(group)}
{:else}
   <TooltipScope>
      <Tooltip.Root>
         {@render triggerFor(undefined)}
         <Tooltip.Content class="whitespace-pre-line" escapeKeydownBehavior={deferEscape ? 'defer-otherwise-close' : 'close'}>{text}</Tooltip.Content>
      </Tooltip.Root>
   </TooltipScope>
{/if}
