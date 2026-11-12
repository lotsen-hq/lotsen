<script lang="ts">
   import { tick, untrack } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import * as Dialog from '$lib/components/ui/dialog';
   import { checkName, failureReason, maxNameLength, namedLayouts, removeLayout, renameLayout, type NamedLayout } from '../lib/namedLayouts';

   let {
      open = $bindable(false),
      onCloseAutoFocus,
   }: {
      open?: boolean;
      /** Moves focus as the dialog closes, in place of the focus that the dialog would restore. */
      onCloseAutoFocus: (event: Event) => void;
   } = $props();

   const id = $props.id();
   const layouts = $derived(namedLayouts());
   // The one row that edits its name or asks before its delete, by the layout's id.
   let editing = $state<{ id: string; step: 'rename' | 'delete' } | null>(null);
   let name = $state('');
   let busy = $state(false);
   let error = $state<string | null>(null);
   let field = $state<HTMLInputElement | null>(null);
   let confirmButton = $state<HTMLElement | null>(null);
   const check = $derived(editing?.step === 'rename' ? checkName(name, editing.id) : null);
   const refusal = $derived.by(() => {
      if (check?.kind === 'taken') {
         return `A layout named ${check.layout.name} already exists.`;
      }

      return check?.kind === 'refused' && name !== '' ? check.reason : null;
   });
   let content = $state<HTMLElement | null>(null);

   $effect.pre(() => {
      if (open) {
         untrack(() => {
            editing = null;
            error = null;
         });
      }
   });

   // A row's buttons give way to its edit, so focus moves to the edit's main control.
   $effect(() => (editing?.step === 'rename' ? field : confirmButton)?.focus());

   // A layout that another page deleted ends its row's edit.
   $effect(() => {
      if (editing !== null && !layouts.some((layout) => layout.id === editing!.id)) {
         editing = null;
      }
   });

   function edit(layout: NamedLayout, step: 'rename' | 'delete'): void {
      editing = { id: layout.id, step };
      name = layout.name;
      error = null;
   }

   // Ends the row's edit, and moves focus to the row's Rename button, or to the dialog when the row is gone, while the dialog stays open.
   async function finish(layoutId: string): Promise<void> {
      editing = null;
      await tick();
      if (!open) {
         return;
      }

      (content?.querySelector<HTMLElement>(`[data-rename="${CSS.escape(layoutId)}"]`) ?? content)?.focus();
   }

   async function run(layoutId: string, change: () => Promise<void>): Promise<void> {
      if (busy) {
         return;
      }

      busy = true;
      error = null;
      try {
         await change();
         await finish(layoutId);
      } catch (failure) {
         error = failureReason(failure);
      } finally {
         busy = false;
      }
   }

   function rename(event: SubmitEvent, layout: NamedLayout): void {
      event.preventDefault();
      if (check?.kind === 'free') {
         void run(layout.id, () => renameLayout(layout.id, name));
      }
   }
</script>

<Dialog.Root bind:open>
   <Dialog.Content bind:ref={content} escapeKeydownBehavior={busy ? 'ignore' : 'close'} interactOutsideBehavior={busy ? 'ignore' : 'close'} showCloseButton={!busy} {onCloseAutoFocus}>
      <Dialog.Header>
         <Dialog.Title>Manage layouts</Dialog.Title>
         <Dialog.Description>Rename or delete the layouts that you saved.</Dialog.Description>
      </Dialog.Header>
      {#if layouts.length === 0}
         <p class="text-sm text-muted-foreground">No layouts are saved.</p>
      {:else}
         <ul class="flex max-h-80 flex-col gap-1 overflow-y-auto" aria-label="Saved layouts">
            {#each layouts as layout (layout.id)}
               <li class="flex flex-col gap-2 rounded-md border px-2 py-1.5">
                  {#if editing?.id === layout.id && editing.step === 'rename'}
                     <form class="flex flex-col gap-2" onsubmit={(event) => rename(event, layout)}>
                        <label for="{id}-name" class="sr-only">New name of {layout.name}</label>
                        <input
                           id="{id}-name"
                           bind:this={field}
                           bind:value={name}
                           autocomplete="off"
                           class={['h-8 rounded-md border bg-transparent px-2 text-sm', refusal === null ? 'border-input focus-visible:border-ring' : 'border-destructive']}
                           aria-invalid={refusal !== null}
                           aria-describedby="{id}-rules{refusal === null ? '' : ` ${id}-refusal`}"
                        />
                        <p id="{id}-rules" class="text-sm text-muted-foreground">Up to {maxNameLength} characters, and no other layout's name.</p>
                        {#if refusal !== null}
                           <p id="{id}-refusal" class="text-sm text-destructive">{refusal}</p>
                        {/if}
                        <div class="flex justify-end gap-2">
                           <Button variant="outline" size="sm" disabled={busy} onclick={() => void finish(layout.id)}>Cancel</Button>
                           <Button type="submit" size="sm" disabled={busy || check?.kind !== 'free'}>Save name</Button>
                        </div>
                     </form>
                  {:else if editing?.id === layout.id && editing.step === 'delete'}
                     <p class="text-sm">Delete the layout {layout.name}? This cannot be undone.</p>
                     <div class="flex justify-end gap-2">
                        <Button variant="outline" size="sm" disabled={busy} onclick={() => void finish(layout.id)}>Cancel</Button>
                        <Button bind:ref={confirmButton} variant="destructive" size="sm" disabled={busy} onclick={() => void run(layout.id, () => removeLayout(layout.id))}>Delete</Button>
                     </div>
                  {:else}
                     <div class="flex items-center gap-2">
                        <div class="flex min-w-0 flex-1 flex-col">
                           <span class="truncate" title={layout.name}>{layout.name}</span>
                           {#if layout.record === undefined}
                              <span class="text-sm text-muted-foreground">Saved by another version of lotsen</span>
                           {/if}
                        </div>
                        <Button variant="outline" size="sm" disabled={busy} aria-label="Rename {layout.name}" data-rename={layout.id} onclick={() => edit(layout, 'rename')}>Rename</Button>
                        <Button variant="outline" size="sm" disabled={busy} aria-label="Delete {layout.name}" onclick={() => edit(layout, 'delete')}>Delete</Button>
                     </div>
                  {/if}
               </li>
            {/each}
         </ul>
      {/if}
      {#if busy}
         <p class="text-sm text-muted-foreground" role="status">Saving the change…</p>
      {/if}
      {#if error !== null}
         <p class="text-sm text-destructive" role="alert">{error}</p>
      {/if}
   </Dialog.Content>
</Dialog.Root>
