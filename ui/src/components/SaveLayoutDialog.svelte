<script lang="ts">
   import { untrack } from 'svelte';
   import { Button, buttonVariants } from '$lib/components/ui/button';
   import * as Dialog from '$lib/components/ui/dialog';
   import type { LayoutRecord } from '../lib/layoutRecord';
   import { checkName, failureReason, maxNameLength, saveLayout, type NamedLayout } from '../lib/namedLayouts';

   let {
      open = $bindable(false),
      snapshot,
      onCloseAutoFocus,
   }: {
      open?: boolean;
      /** Returns the workspace layout to save, or null while the workspace shows none. */
      snapshot: () => LayoutRecord | null;
      /** Moves focus as the dialog closes, in place of the focus that the dialog would restore. */
      onCloseAutoFocus: (event: Event) => void;
   } = $props();

   const id = $props.id();
   let name = $state('');
   // The layout that has the name, while the dialog asks whether to replace it.
   let replacing = $state<NamedLayout | null>(null);
   let busy = $state(false);
   let error = $state<string | null>(null);
   let field = $state<HTMLInputElement | null>(null);
   let replaceButton = $state<HTMLElement | null>(null);
   const check = $derived(checkName(name));
   const refusal = $derived(check.kind === 'refused' && name !== '' ? check.reason : null);

   $effect.pre(() => {
      if (open) {
         untrack(() => {
            name = '';
            replacing = null;
            error = null;
         });
      }
   });

   // Each step's controls replace the other's, so focus moves to the new step's main control.
   $effect(() => (replacing === null ? field : replaceButton)?.focus());

   async function submit(event: SubmitEvent): Promise<void> {
      event.preventDefault();
      if (busy || check.kind === 'refused') {
         return;
      }

      if (check.kind === 'taken' && replacing === null) {
         replacing = check.layout;
         return;
      }

      await save();
   }

   async function save(): Promise<void> {
      const record = snapshot();
      if (busy || record === null) {
         return;
      }

      busy = true;
      error = null;
      try {
         await saveLayout(name, record);
         open = false;
      } catch (failure) {
         error = failureReason(failure);
      } finally {
         busy = false;
      }
   }
</script>

<Dialog.Root bind:open>
   <Dialog.Content class="sm:max-w-sm" escapeKeydownBehavior={busy ? 'ignore' : 'close'} interactOutsideBehavior={busy ? 'ignore' : 'close'} showCloseButton={!busy} {onCloseAutoFocus}>
      {#if replacing === null}
         <form class="contents" onsubmit={submit}>
            <Dialog.Header>
               <Dialog.Title>Save layout</Dialog.Title>
               <Dialog.Description>Save the current workspace layout under a name, to apply it again from the Panels menu.</Dialog.Description>
            </Dialog.Header>
            <div class="flex flex-col gap-2">
               <label for="{id}-name" class="font-medium">Name</label>
               <input
                  id="{id}-name"
                  bind:value={name}
                  bind:this={field}
                  autocomplete="off"
                  class={['h-8 rounded-md border bg-transparent px-2 text-sm', refusal === null ? 'border-input focus-visible:border-ring' : 'border-destructive']}
                  aria-invalid={refusal !== null}
                  aria-describedby="{id}-rules{refusal === null ? '' : ` ${id}-refusal`}"
               />
               <p id="{id}-rules" class="text-sm text-muted-foreground">Up to {maxNameLength} characters, and no other layout's name.</p>
               {#if refusal !== null}
                  <p id="{id}-refusal" class="text-sm text-destructive">{refusal}</p>
               {/if}
            </div>
            {#if busy}
               <p class="text-sm text-muted-foreground" role="status">Saving the layout…</p>
            {/if}
            {#if error !== null}
               <p class="text-sm text-destructive" role="alert">{error}</p>
            {/if}
            <Dialog.Footer>
               <Dialog.Close class={buttonVariants({ variant: 'outline' })} disabled={busy}>Cancel</Dialog.Close>
               <Button type="submit" disabled={busy || check.kind === 'refused'}>Save</Button>
            </Dialog.Footer>
         </form>
      {:else}
         <Dialog.Header>
            <Dialog.Title>Replace the layout {replacing.name}?</Dialog.Title>
            <Dialog.Description>A layout with this name exists. Replacing it saves the current workspace layout in its place.</Dialog.Description>
         </Dialog.Header>
         {#if busy}
            <p class="text-sm text-muted-foreground" role="status">Saving the layout…</p>
         {/if}
         {#if error !== null}
            <p class="text-sm text-destructive" role="alert">{error}</p>
         {/if}
         <Dialog.Footer>
            <Button variant="outline" disabled={busy} onclick={() => (replacing = null)}>Cancel</Button>
            <Button bind:ref={replaceButton} disabled={busy} onclick={() => void save()}>Replace</Button>
         </Dialog.Footer>
      {/if}
   </Dialog.Content>
</Dialog.Root>
