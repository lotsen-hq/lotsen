<!--
   A stop's headline, prose sections and checks, which the Stop panel and the inline block both show, with the markers of the threads asked about text in each and the ask that a selection offers.
   A check shows its question, Claude's recommendation and the reviewer's answer, or the suggested answers as plain text where the page offers no answering.
-->
<script lang="ts">
   import { tick } from 'svelte';
   import ChevronRight from '@lucide/svelte/icons/chevron-right';
   import * as Collapsible from '$lib/components/ui/collapsible';
   import type { CheckOpening, PickHandler } from '../lib/viewState';
   import type { AnswerWrites, CheckAnswers, CheckAnswerWrites } from '../lib/answers';
   import { hasAnswersToSend } from '../lib/controls';
   import type { LinkHover } from '../lib/linkHover';
   import type { OpenSections } from '../lib/openSections.svelte';
   import { panelDocument } from '../lib/panelDocument.svelte';
   import { stopTextTarget, type SelectionExplain } from '../lib/selectionAsk';
   import type { ThreadMarker } from '../lib/threadView';
   import type { CodeReference, StopContent } from '../lib/types';
   import CheckAnswerForm from './CheckAnswerForm.svelte';
   import Prose from './Prose.svelte';
   import SendAnswersRow from './SendAnswersRow.svelte';
   import SelectionAsk from './SelectionAsk.svelte';
   import ThreadMarkers from './ThreadMarkers.svelte';

   let {
      content,
      opened,
      onfollowreference,
      focusedPassage = null,
      hover = null,
      atPin = null,
      stopId,
      version,
      threads = [],
      onask = null,
      explain = null,
      onopenthread = () => {},
      answers = null,
      openedCheck = null,
   }: {
      content: StopContent;
      /** The sections the reviewer opened or closed, which the parent keeps per stop. */
      opened: OpenSections;
      /** Follows a `ref:` link in the prose to its code reference. */
      onfollowreference: (reference: CodeReference) => void;
      /** The focused passage, whose `ref:` links the prose highlights, or null for none. */
      focusedPassage?: CodeReference | null;
      /** What the `ref:` links in the prose report hovering to, the page's one {@link LinkHover}, or null for nothing. */
      hover?: LinkHover | null;
      /** Returns whether a `ref:` link's code reference is at the pin, or null when every link is. */
      atPin?: ((reference: CodeReference) => boolean) | null;
      /** The stop's ID, which a stop text anchor names. */
      stopId: string;
      /** The number of the stop's version that the content is, which a stop text anchor names, or undefined to let the server take the current one. */
      version?: number;
      /** The markers of the threads asked about text in the stop, each with its prose field. */
      threads?: readonly (ThreadMarker & { field: string })[];
      /** Hands text that the reviewer selected to the Threads panel, or null when the page offers no ask. */
      onask?: PickHandler | null;
      /** The Explain that a selection offers beside the ask, or null for none. */
      explain?: SelectionExplain | null;
      onopenthread?: (threadId: string) => void;
      /** The answers to the stop's checks, or null when the page offers no answering. */
      answers?: CheckAnswers | null;
      /** The check to open its section for, scroll to and focus, unless another explanation did it, or null for none. */
      openedCheck?: CheckOpening | null;
   } = $props();

   let root = $state<HTMLElement>();
   const home = panelDocument();
   // A sent batch empties the count before the server's controls close the offer.
   const send = $derived(answers?.send && hasAnswersToSend(answers.send.offer) && answers.send.unsent > 0 ? answers.send : null);
   // Removing the row drops the focus on its button to the page body, so the focus moves to the last check. A pre effect reads the focus before the row leaves the page.
   $effect.pre(() => {
      if (send === null && root?.querySelector('[data-send-row]')?.contains(home.document.activeElement)) {
         void tick().then(() => [...(root?.querySelectorAll<HTMLElement>('[data-check-id]') ?? [])].at(-1)?.focus());
      }
   });
   const checkWrites = (writes: AnswerWrites, checkId: string): CheckAnswerWrites => ({
      onanswer: (input) => writes.onanswer(checkId, input),
      onsettype: (type) => writes.onsettype(checkId, type),
      onclear: () => writes.onclear(checkId),
   });
   const markersOf = (field: string) => threads.filter((thread) => thread.field === field);

   type TextField = 'scenario' | 'how' | 'why' | 'designChoices' | 'pinnedBy';

   const textSections: { field: TextField; title: string }[] = [
      { field: 'scenario', title: 'Scenario' },
      { field: 'how', title: 'How' },
      { field: 'why', title: 'Why' },
      { field: 'designChoices', title: 'Design choices' },
      { field: 'pinnedBy', title: 'Pinned by' },
   ];

   const html = $derived(content.html);

   // Each check with its HTML and its index in the content, which numbers it. A stop text anchor names its prose fields by the check's ID.
   const checks = $derived((content.checks ?? []).flatMap((check, index) => (html.checks?.[index] ? [{ check, html: html.checks[index], index }] : [])));

   // An explanation without the check leaves the request to one that shows it.
   $effect(() => {
      const wanted = openedCheck;
      if (wanted === null || !checks.some(({ check }) => check.id === wanted.checkId) || !wanted.take()) {
         return;
      }

      opened.set('checks', true);
      void tick().then(() => {
         const item = [...(root?.querySelectorAll<HTMLElement>('[data-check-id]') ?? [])].find((element) => element.dataset.checkId === wanted.checkId);
         item?.scrollIntoView({ block: 'nearest' });
         item?.focus();
      });
   });

   const sections = $derived.by(() => {
      const texts = textSections.flatMap(({ field, title }) => (html[field] ? [{ id: field, title, html: html[field], checks: null }] : []));
      return checks.length ? [...texts, { id: 'checks', title: 'Checks', html: null, checks }] : texts;
   });
</script>

<div class="stop-explanation relative" bind:this={root}>
   <Prose class="headline" field="headline" html={html.headline} onopenreference={onfollowreference} {focusedPassage} {hover} {atPin} />
   <ThreadMarkers threads={markersOf('headline')} {onopenthread} />
   {#each sections as section (section.id)}
      {@const shown = opened.get(section.id) ?? true}
      <Collapsible.Root bind:open={() => shown, (open) => opened.set(section.id, open)} class="section">
         <!-- A section's markers sit by its heading, so they show while the section is closed. -->
         <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <Collapsible.Trigger class="group section-heading caps-label">
               <ChevronRight class="size-3 transition-transform group-aria-expanded:rotate-90" />
               {section.title}
            </Collapsible.Trigger>
            <ThreadMarkers threads={section.checks ? (shown ? [] : threads.filter((thread) => thread.field.startsWith('checks['))) : markersOf(section.id)} {onopenthread} />
         </div>
         <Collapsible.Content class="section-body">
            {#if section.checks}
               <ol class="checks">
                  <!-- Keyed by check ID, so a rewrite that moves or drops a check takes the unsent words in its answer form with it. The store refuses a tour whose check IDs repeat. -->
                  {#each section.checks as { check, html: checkHtml, index } (check.id)}
                     <li data-check-id={check.id} tabindex="-1">
                        <Prose field="checks[{check.id}].question" html={checkHtml.question} onopenreference={onfollowreference} {focusedPassage} {hover} {atPin} {shown} />
                        <ThreadMarkers threads={markersOf(`checks[${check.id}].question`)} {onopenthread} />
                        {#if checkHtml.recommendation}
                           <div class="recommendation">
                              <span class="caps-label">Recommendation</span>
                              <Prose field="checks[{check.id}].recommendation" html={checkHtml.recommendation} onopenreference={onfollowreference} {focusedPassage} {hover} {atPin} {shown} />
                              <ThreadMarkers threads={markersOf(`checks[${check.id}].recommendation`)} {onopenthread} />
                           </div>
                        {/if}
                        {#if answers !== null}
                           <CheckAnswerForm
                              {check}
                              number={index + 1}
                              answer={answers.byCheck.get(check.id)}
                              boundSessionId={answers.boundSessionId}
                              writes={answers.writes === null ? null : checkWrites(answers.writes, check.id)}
                           />
                        {:else if check.suggestions?.length}
                           <div class="suggestions">
                              <span class="caps-label" aria-hidden="true">Suggested answers</span>
                              <ul aria-label="Suggested answers">
                                 {#each check.suggestions as suggestion, position (position)}
                                    <li>{suggestion.text}</li>
                                 {/each}
                              </ul>
                           </div>
                        {/if}
                     </li>
                  {/each}
               </ol>
               {#if send !== null}
                  <SendAnswersRow {send} />
               {/if}
            {:else if section.html}
               <Prose field={section.id} html={section.html} onopenreference={onfollowreference} {focusedPassage} {hover} {atPin} {shown} />
            {/if}
         </Collapsible.Content>
      </Collapsible.Root>
   {/each}
   {#if onask !== null}
      <SelectionAsk within={root} targetOf={(range) => (root ? stopTextTarget(range, root, stopId, version) : null)} {onask} {explain} />
   {/if}
</div>
