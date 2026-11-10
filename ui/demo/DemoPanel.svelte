<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import type { LotsenApp } from '$lib/app.svelte';
   import type { ClaudeSession, SessionEventPayload } from '../tests/backend/backend';
   import { laterContentOf, writeStop, type LiveSamples } from './seed';

   let { app, live }: { app: LotsenApp; live: LiveSamples } = $props();

   type Resolution = 'done' | 'needsAnswer' | 'declined';

   interface Received {
      key: string;
      session: ClaudeSession;
      sessionLabel: string;
      question: SessionEventPayload;
      text: string;
      resolution: Resolution;
      busy: boolean;
      error: string | null;
   }

   // The world's real agent sessions, which the page's sample tours are bound to.
   const sessions = $derived([
      { label: 'Claude', session: live.claude },
      { label: 'Claude, unconfirmed', session: live.unconfirmed },
   ]);

   let inbox = $state<Received[]>([]);
   let stopError = $state<string | null>(null);
   let writing = $state<string | null>(null);

   const pendingStops = $derived((app.selectedPlan?.plan?.stops ?? []).filter((stop) => stop.stage === 'pending'));
   const tourLabel = (tourId: string) => app.tours.find((tour) => tour.id === tourId)?.label ?? tourId;
   const boundSession = () => sessions.find((entry) => entry.session.sessionId === app.selectedBinding?.sessionId)?.session ?? live.claude;

   // Long-polls each session's questions while the panel shows, newest first. A poll that times out finds no question, and any other failure waits a moment before the next poll.
   $effect(() => {
      let stopped = false;
      for (const entry of sessions) {
         void (async () => {
            while (!stopped) {
               try {
                  const question = await entry.session.next('question', 20_000);
                  if (!stopped) {
                     inbox.unshift({
                        key: `${entry.session.sessionId}:${String(question.requestId)}`,
                        session: entry.session,
                        sessionLabel: entry.label,
                        question,
                        text: '',
                        resolution: 'done',
                        busy: false,
                        error: null,
                     });
                  }
               } catch (error) {
                  if (!String(error).includes('event within')) {
                     await new Promise((resolve) => setTimeout(resolve, 2000));
                  }
               }
            }
         })();
      }

      return () => (stopped = true);
   });

   async function write(stopId: string): Promise<void> {
      const later = app.selectedTourId === null ? undefined : laterContentOf(live.seed, app.selectedTourId, stopId);
      if (later === undefined) {
         return;
      }

      writing = stopId;
      stopError = null;
      try {
         await writeStop(boundSession(), later.tour, later.stopKey, later.content);
      } catch (error) {
         stopError = error instanceof Error ? error.message : String(error);
      } finally {
         writing = null;
      }
   }

   async function reply(item: Received): Promise<void> {
      item.busy = true;
      item.error = null;
      try {
         const text = item.text.trim();
         await item.session.call('reply', { tourId: item.question.tourId, questionId: item.question.questionId, resolution: item.resolution, ...(text === '' ? {} : { text }) });
         inbox.splice(inbox.indexOf(item), 1);
      } catch (error) {
         item.error = error instanceof Error ? error.message : String(error);
         item.busy = false;
      }
   }

   async function ack(item: Received): Promise<void> {
      try {
         await item.session.call('ack', { nonce: item.question.nonce });
      } catch (error) {
         item.error = error instanceof Error ? error.message : String(error);
      }
   }

   function described(question: SessionEventPayload): string {
      const kind = question.action === undefined ? (question.decision === undefined ? null : `Decision: ${String(question.decision)}`) : `Tour action: ${String(question.action)}`;
      const text = String(question.text ?? '');
      return kind === null ? text : text === '' ? kind : `${kind}. ${text}`;
   }
</script>

<!-- The bottom left corner holds the end of the file tree, which scrolls, while the right column keeps a folded panel's buttons at the bottom. -->
<details class="fixed bottom-4 left-4 z-50 max-h-3/4 w-96 overflow-y-auto rounded-md border bg-card text-sm shadow-md" open>
   <summary class="cursor-pointer px-3 py-2 font-semibold">Demo: act as Claude</summary>
   <div class="flex flex-col gap-3 px-3 pb-3">
      <section aria-label="Pending stops" class="flex flex-col gap-1">
         <h2 class="text-xs font-semibold text-muted-foreground uppercase">Pending stops of the open tour</h2>
         {#each pendingStops as stop (stop.id)}
            {@const hasContent = app.selectedTourId !== null && laterContentOf(live.seed, app.selectedTourId, stop.id) !== undefined}
            <Button variant="outline" size="sm" class="h-auto whitespace-normal" disabled={!hasContent || writing !== null} onclick={() => void write(stop.id)}>Write “{stop.title}”</Button>
         {:else}
            <p class="text-muted-foreground">The open tour has no pending stop.</p>
         {/each}
         {#if stopError !== null}
            <p role="alert" class="text-destructive">{stopError}</p>
         {/if}
      </section>
      <section aria-label="Claude's inbox" class="flex flex-col gap-2">
         <h2 class="text-xs font-semibold text-muted-foreground uppercase">Claude's inbox</h2>
         {#each inbox as item (item.key)}
            <form
               class="flex flex-col gap-1.5 rounded-md border p-2"
               onsubmit={(event) => {
                  event.preventDefault();
                  void reply(item);
               }}
            >
               <p class="text-xs text-muted-foreground">{item.sessionLabel}, {tourLabel(item.question.tourId)}</p>
               <p class="whitespace-pre-wrap">{described(item.question)}</p>
               <label for="reply-{item.key}" class="sr-only">Claude's reply</label>
               <textarea
                  id="reply-{item.key}"
                  bind:value={item.text}
                  rows="2"
                  placeholder="Claude's reply, in Markdown"
                  class="min-h-10 w-full resize-y rounded-md border border-input bg-transparent px-3 py-1.5 text-sm shadow-xs placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-hidden dark:bg-input/30"
               ></textarea>
               <div class="flex items-center justify-end gap-2">
                  {#if item.question.nonce !== undefined}
                     <Button type="button" variant="ghost" size="sm" onclick={() => void ack(item)}>Ack</Button>
                  {/if}
                  <label for="resolution-{item.key}" class="sr-only">Resolution</label>
                  <select id="resolution-{item.key}" bind:value={item.resolution} class="h-8 rounded-md border border-input bg-background px-2 text-sm">
                     <option value="done">Done</option>
                     <option value="needsAnswer">Needs an answer</option>
                     <option value="declined">Declined</option>
                  </select>
                  <Button type="submit" size="sm" disabled={item.busy}>Reply</Button>
               </div>
               {#if item.error !== null}
                  <p role="alert" class="text-destructive">{item.error}</p>
               {/if}
            </form>
         {:else}
            <p class="text-muted-foreground">No question waits for Claude.</p>
         {/each}
      </section>
   </div>
</details>
