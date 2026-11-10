<!-- The new-iteration notice's button, which moves the open tour onto the newer iteration. It says why when it is disabled, and shows the server's reason when it refuses the update. -->
<script lang="ts">
   import LoaderCircle from '@lucide/svelte/icons/loader-circle';
   import { Button } from '$lib/components/ui/button';

   let {
      iteration,
      unavailable,
      onupdate,
   }: {
      /** The number of the newer iteration, which the button names. */
      iteration: number;
      /** Why the reviewer cannot update now, or null when the button works. */
      unavailable: string | null;
      /** Updates the tour, and returns null once the server stored the update or its reason for a refusal. */
      onupdate: () => Promise<string | null>;
   } = $props();

   const id = $props.id();
   let updating = $state(false);
   let refusal = $state<string | null>(null);

   async function update(): Promise<void> {
      if (updating || unavailable !== null) {
         return;
      }

      updating = true;
      refusal = null;
      try {
         refusal = await onupdate();
      } finally {
         updating = false;
      }
   }
</script>

<Button
   variant="outline"
   size="sm"
   class="h-7"
   disabled={unavailable !== null}
   aria-disabled={updating}
   aria-busy={updating}
   aria-describedby={unavailable === null ? undefined : `${id}-unavailable`}
   onclick={update}
>
   {#if updating}
      <LoaderCircle class="size-4 animate-spin" aria-hidden="true" />
   {/if}
   Move to update {iteration}
</Button>
{#if unavailable !== null}
   <p id="{id}-unavailable" class="text-xs text-muted-foreground">{unavailable}</p>
{/if}
{#if refusal !== null}
   <p role="alert" class="min-w-0 text-destructive">{refusal}</p>
{/if}
