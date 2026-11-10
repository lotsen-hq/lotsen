import type { CheckAnswers } from '../src/lib/answers';
import { ExplanationView, type ExplainedStop, type ExplanationPage } from '../src/lib/explanationView.svelte';
import { LinkHover } from '../src/lib/linkHover';
import type { PlacementOptions } from '../src/lib/placement';
import type { Stop } from '../src/lib/types';
import { Reactive } from './reactive.svelte';

/** A page for an explanation with no threads, answers, pin, controls or opened check, whose actions do nothing, with `page` over it. */
export function explanationPage(page: Partial<ExplanationPage> = {}): ExplanationPage {
   const answers: CheckAnswers = { byCheck: new Map(), boundSessionId: null, writes: null, send: null };
   return {
      threads: [],
      answers,
      commits: null,
      controls: null,
      openedCheck: null,
      blockCanShow: true,
      hover: new LinkHover(() => {}),
      followReference: () => {},
      openThread: () => {},
      pick: () => {},
      requestExplain: async () => null,
      movePlacement: () => {},
      showCurrentVersion: () => {},
      ...page,
   };
}

/** The stop under view as an explanation reads it: the stop at its current version, with its primary passage at the pin and none focused. */
export function explainedStop(stop: Stop, fields: Partial<ExplainedStop> = {}): ExplainedStop {
   const primary = stop.stage === 'written' ? (stop.content.passages.find((passage) => passage.role === 'primary') ?? null) : null;
   return { stop, primaryPassage: primary, focusedPassage: null, shownVersion: null, stale: { kind: 'current' }, ...fields };
}

/** The explanation of a stop that a test changes through `stop`, in the placement mode given, on a page of {@link explanationPage}. */
export function explanationOf(
   stop: Stop,
   options: { mode?: PlacementOptions['mode']; fields?: Partial<ExplainedStop>; page?: Partial<ExplanationPage> } = {},
): { view: ExplanationView; stop: Reactive<ExplainedStop> } {
   const source = new Reactive(explainedStop(stop, options.fields));
   const view = new ExplanationView(
      () => source.value,
      () => ({ mode: options.mode ?? 'side', threshold: 600, layout: 'inline' }),
      explanationPage(options.page),
   );
   return { view, stop: source };
}
