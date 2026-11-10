<!-- The open tour's attachment state for the top bar: an icon and a short label, with the one line on what works in its tooltip and in its description. -->
<script lang="ts">
   import CircleCheck from '@lucide/svelte/icons/circle-check';
   import CircleDashed from '@lucide/svelte/icons/circle-dashed';
   import Unplug from '@lucide/svelte/icons/unplug';
   import type { LucideIcon } from '@lucide/svelte';
   import { Button } from '$lib/components/ui/button';
   import { attachmentTexts, type Attachment } from '../lib/labels';
   import WithTooltip from './WithTooltip.svelte';

   let { attachment }: { attachment: Attachment } = $props();

   const id = $props.id();
   const icons: Record<Attachment, LucideIcon> = { none: Unplug, unconfirmed: CircleDashed, confirmed: CircleCheck };
   const text = $derived(attachmentTexts[attachment]);
   const Icon = $derived(icons[attachment]);
</script>

<WithTooltip text={`Claude session: ${text.label}\n${text.explanation}`}>
   {#snippet trigger({ props })}
      <!-- A button, so keyboard focus reaches the tooltip. The description replaces the tooltip's, which exists only while the tooltip is open. -->
      <Button
         {...props}
         variant="ghost"
         size="sm"
         class={['shrink-0 font-normal', attachment === 'confirmed' ? 'text-success' : 'text-muted-foreground']}
         aria-label="Claude session: {text.label}"
         aria-describedby="{id}-explanation"
      >
         <Icon aria-hidden="true" />
         {text.label}
      </Button>
   {/snippet}
</WithTooltip>
<span id="{id}-explanation" class="sr-only">{text.explanation}</span>
