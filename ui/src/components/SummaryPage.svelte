<!--
   The tour's summary page, which the center shows after the plan's last stop: the review summary as the server built it, with each item linked to its stop, its check or its thread.
   It writes nothing. The reviewer's answers are plain text, and only the questions, the scenarios and Claude's synthesis that the server rendered go in as HTML.
-->
<script lang="ts">
   import type { Snippet } from 'svelte';
   import { Button } from '$lib/components/ui/button';
   import type { KnownSummary } from '../lib/tourRecord.svelte';
   import { answerStateText, answerStateTexts, outdatedAnswerText } from '../lib/answers';
   import { anchorLabel, shortened } from '../lib/labels';
   import type { LinkHover } from '../lib/linkHover';
   import { pinnedReferenceAtPin, type PinCommits } from '../lib/pin';
   import { threadWords } from '../lib/threadView';
   import { summaryThreadKindLabel } from '../lib/tourActions';
   import type { CheckAnswer, CodeReference, Plan, QuestionThread, SummaryAnswer, SummaryThread } from '../lib/types';
   import Prose from './Prose.svelte';
   import StopKindPill from './StopKindPill.svelte';
   import WarningNotice from './WarningNotice.svelte';

   let {
      known,
      plan = null,
      threads = [],
      answers = [],
      boundSessionId = null,
      onopenstop,
      onopencheck,
      onopenthread,
      onfollowreference,
      hover = null,
      atPin,
      pinCommits = null,
      actions,
   }: {
      /** The selected tour's review summary. */
      known: KnownSummary;
      /** The tour's plan, which names the stop of a thread's anchor, or null while the page has none. */
      plan?: Plan | null;
      /** The tour's threads, whose words the threads of the summary show. */
      threads?: readonly QuestionThread[];
      /** The tour's answers, whose state each answer of the summary shows as its check does. */
      answers?: readonly CheckAnswer[];
      /** The ID of the session bound to the tour, or null when none is. */
      boundSessionId?: string | null;
      onopenstop: (stopId: string) => void;
      /** Opens an answer's check, at the stop version that the answer stays on when it is outdated. */
      onopencheck: (answer: SummaryAnswer) => void;
      onopenthread: (threadId: string) => void;
      /** Follows a `ref:` link in a question or a scenario to its code reference. */
      onfollowreference: (reference: CodeReference) => void;
      /** What the `ref:` links report hovering to, the page's one {@link LinkHover}, or null for nothing. */
      hover?: LinkHover | null;
      /** Returns whether a `ref:` link in the prose of a stop is at the pin, for the stop's current version, or for its earlier version `version`. */
      atPin: (stopId: string, version: number | undefined) => (reference: CodeReference) => boolean;
      /** The commits of the tour's pin, against which a `ref:` link of the synthesis is at an earlier commit when the synthesis was written at another pin, or null while the tour is not pinned. */
      pinCommits?: PinCommits | null;
      /** The buttons that act on the summary, or undefined for none. Each one that needs a session shows disabled with its reason while none is bound. */
      actions?: Snippet;
   } = $props();

   const id = $props.id();
   const threadLength = 160;
   const byId = $derived(new Map(threads.map((thread) => [thread.id, thread])));

   // A thread that the page does not know yet, as when the summary arrives before the thread's event, shows what the summary says of it.
   function threadLabel(entry: SummaryThread): string {
      const thread = byId.get(entry.id);
      if (thread !== undefined) {
         return shortened(threadWords(thread), threadLength);
      }

      return summaryThreadKindLabel(entry) ?? (entry.note ? 'A note' : 'A thread');
   }

   // The reviewer's locale and time zone, at the minute.
   function writtenAt(timestamp: string): string {
      const time = Date.parse(timestamp);
      return Number.isNaN(time) ? timestamp : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(time);
   }

   // An answer that the page does not know yet, as when the summary arrives before the answer's event, shows the state that the summary gives it.
   function stateLabel(entry: SummaryAnswer): string {
      const known = answers.find((answer) => answer.stopId === entry.stopId && answer.checkId === entry.checkId);
      if (known !== undefined) {
         return answerStateText(known, boundSessionId).label;
      }

      return entry.outdatedOn === undefined ? answerStateTexts[entry.state].label : outdatedAnswerText.label;
   }
</script>

{#snippet stopLink(stopId: string, title: string | undefined)}
   {#if title === undefined}
      <span class="text-muted-foreground">A stop no longer in the plan</span>
   {:else}
      <Button variant="link" size="sm" class="h-auto p-0" onclick={() => onopenstop(stopId)}>{title}</Button>
   {/if}
{/snippet}

{#snippet group(key: string, title: string, count: number, body: Snippet)}
   <section aria-labelledby="{id}-{key}">
      <h3 id="{id}-{key}" class="summary-heading">{title} <span class="text-muted-foreground tabular-nums">{count}</span></h3>
      {@render body()}
   </section>
{/snippet}

{#snippet answerList(answers: readonly SummaryAnswer[], empty: string, after?: Snippet)}
   {#if answers.length === 0 && after === undefined}
      <p class="text-muted-foreground">{empty}</p>
   {:else}
      <ul class="summary-list">
         {#each answers as answer (`${answer.stopId}/${answer.checkId}`)}
            <li class="summary-item">
               {#if answer.questionHtml !== undefined}
                  <Prose html={answer.questionHtml} onopenreference={onfollowreference} {hover} atPin={atPin(answer.stopId, answer.outdatedOn)} />
               {:else}
                  <p class="text-muted-foreground">A check no longer in the plan</p>
               {/if}
               <p class="summary-answer">{answer.text}</p>
               <div class="summary-meta">
                  <span class="summary-state">{stateLabel(answer)}</span>
                  {@render stopLink(answer.stopId, answer.stopTitle)}
                  {#if answer.stopTitle !== undefined && answer.question !== undefined}
                     <Button variant="link" size="sm" class="h-auto p-0" onclick={() => onopencheck(answer)}>
                        {answer.outdatedOn === undefined ? 'Go to the check' : `Go to the check in version ${answer.outdatedOn}`}
                     </Button>
                  {/if}
               </div>
            </li>
         {/each}
         {@render after?.()}
      </ul>
   {/if}
{/snippet}

{#snippet threadList(entries: readonly SummaryThread[])}
   {#if entries.length === 0}
      <p class="text-muted-foreground">None yet.</p>
   {:else}
      <ul class="summary-list">
         {#each entries as entry (entry.id)}
            {@const kindLabel = summaryThreadKindLabel(entry)}
            <li class="summary-item">
               <Button variant="link" size="sm" class="h-auto justify-start p-0 text-left whitespace-normal" onclick={() => onopenthread(entry.id)}>{threadLabel(entry)}</Button>
               <div class="summary-meta">
                  {#if entry.note}
                     <span class="summary-state">Note</span>
                  {:else if kindLabel !== null}
                     <span class="summary-state">{kindLabel}</span>
                  {/if}
                  <span class="min-w-0 text-muted-foreground">{anchorLabel(entry.anchor, plan)}</span>
               </div>
            </li>
         {/each}
      </ul>
   {/if}
{/snippet}

<div class="scroll-box h-full">
   <div class="summary-page">
      <header>
         <h2 class="text-xl font-semibold">Summary</h2>
         <p class="text-muted-foreground">What the review of this tour has produced so far.</p>
      </header>
      {#if actions !== undefined}
         <div class="flex flex-col gap-3" role="group" aria-label="Summary actions">
            {@render actions()}
         </div>
      {/if}
      {#if known.state === 'loading'}
         <p class="text-muted-foreground">Loading the summary…</p>
      {:else if known.state === 'failed'}
         <div role="alert">
            <p class="text-destructive">{known.message}</p>
            {#if known.reason !== null}
               <p class="text-muted-foreground">{known.reason}</p>
            {/if}
         </div>
      {:else}
         {@const summary = known.summary}
         {#if summary.synthesis !== undefined}
            {@const synthesis = summary.synthesis}
            <section aria-labelledby="{id}-synthesis" class="flex flex-col gap-2">
               <h3 id="{id}-synthesis" class="summary-heading mb-0">Claude's synthesis</h3>
               <p class="text-sm text-muted-foreground">Written <time datetime={synthesis.writtenAt}>{writtenAt(synthesis.writtenAt)}</time></p>
               {#if synthesis.predatesSummary}
                  <WarningNotice role="note">This synthesis predates changes to the summary below, such as new answers or resolved threads. Summarize again for one that covers them.</WarningNotice>
               {/if}
               <div class="summary-item">
                  {#if synthesis.html !== undefined}
                     <Prose html={synthesis.html} onopenreference={onfollowreference} {hover} atPin={pinnedReferenceAtPin(synthesis.pin, pinCommits)} />
                  {:else}
                     <p class="whitespace-pre-wrap">{synthesis.text}</p>
                  {/if}
               </div>
            </section>
         {/if}
         {#snippet decisions()}
            {@render answerList(summary.answers.decisions, 'None yet.')}
         {/snippet}
         {#snippet risks()}
            {#each summary.risks as risk (risk.stopId)}
               <li class="summary-item">
                  <div class="summary-meta">
                     <StopKindPill kind="risk" />
                     {@render stopLink(risk.stopId, risk.title)}
                  </div>
                  {#if risk.scenarioHtml !== undefined}
                     <Prose html={risk.scenarioHtml} onopenreference={onfollowreference} {hover} atPin={atPin(risk.stopId, undefined)} />
                  {:else}
                     <p class="text-muted-foreground">Claude has not written this stop yet.</p>
                  {/if}
               </li>
            {/each}
         {/snippet}
         {#snippet concerns()}
            {@render answerList(summary.answers.concerns, 'None yet.', summary.risks.length > 0 ? risks : undefined)}
         {/snippet}
         {#snippet openQuestions()}
            {@render answerList(summary.answers.openQuestions, 'None yet.')}
         {/snippet}
         {#snippet outdatedAnswers()}
            {@render answerList(summary.answers.outdated, 'None.')}
         {/snippet}
         {#snippet openThreads()}
            {@render threadList(summary.threads.open)}
         {/snippet}
         {#snippet resolvedThreads()}
            {@render threadList(summary.threads.resolved)}
         {/snippet}
         {#snippet outdatedThreads()}
            {@render threadList(summary.threads.outdated)}
         {/snippet}
         {#snippet skippedStops()}
            {#if summary.skippedStops.length === 0}
               <p class="text-muted-foreground">None.</p>
            {:else}
               <ul class="summary-list">
                  {#each summary.skippedStops as skipped (skipped.stopId)}
                     <li>{@render stopLink(skipped.stopId, skipped.title)}</li>
                  {/each}
               </ul>
            {/if}
         {/snippet}
         {#snippet staleStops()}
            {#if summary.staleStops.length === 0}
               <p class="text-muted-foreground">None.</p>
            {:else}
               <ul class="summary-list">
                  {#each summary.staleStops as stale (stale.stopId)}
                     <li class="summary-meta">
                        {@render stopLink(stale.stopId, stale.title)}
                        <span class="text-muted-foreground">Stale since update {stale.staleOn}</span>
                     </li>
                  {/each}
               </ul>
            {/if}
         {/snippet}
         {@render group('decisions', 'Decisions', summary.answers.decisions.length, decisions)}
         {@render group('concerns', 'Concerns', summary.answers.concerns.length + summary.risks.length, concerns)}
         {@render group('open-questions', 'Open questions', summary.answers.openQuestions.length, openQuestions)}
         {#if summary.answers.outdated.length > 0}
            {@render group('outdated-answers', 'Outdated answers', summary.answers.outdated.length, outdatedAnswers)}
         {/if}
         {@render group('open-threads', 'Open threads', summary.threads.open.length, openThreads)}
         {@render group('resolved-threads', 'Resolved threads', summary.threads.resolved.length, resolvedThreads)}
         {@render group('outdated-threads', 'Outdated threads', summary.threads.outdated.length, outdatedThreads)}
         {@render group('skipped-stops', 'Skipped stops', summary.skippedStops.length, skippedStops)}
         {@render group('stale-stops', 'Stale stops', summary.staleStops.length, staleStops)}
      {/if}
   </div>
</div>
