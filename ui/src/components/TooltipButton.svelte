<script lang="ts">
   import { Button, type ButtonProps } from '$lib/components/ui/button';
   import WithTooltip from './WithTooltip.svelte';

   let {
      tooltip,
      children,
      disabled = false,
      deferEscape = false,
      ...rest
   }: ButtonProps & {
      /** The tooltip's text. It does not name the button, so an icon button still needs its aria-label. */
      tooltip: string;
      /** Lets Escape close the popover or dialog that holds the button, not the tooltip first, as `WithTooltip` says. */
      deferEscape?: boolean;
   } = $props();
</script>

<!--
   A disabled button gets no pointer events, so while it is disabled the span around it is the tooltip's trigger. The span takes no box while the button is
   enabled, and both elements stay mounted, so the flip moves neither the button nor its focus. The button's own props merge into the trigger's while it is the trigger.
-->
<WithTooltip text={tooltip} {deferEscape} {...disabled ? {} : rest}>
   {#snippet trigger({ props })}
      <span {...disabled ? props : {}} class={disabled ? 'inline-flex' : 'contents'}>
         <Button {...disabled ? rest : props} {disabled}>{@render children?.()}</Button>
      </span>
   {/snippet}
</WithTooltip>
