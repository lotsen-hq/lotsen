import { fireEvent, render, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import { stopOf, writtenOf } from './stops';
import ExplanationBlock from '../src/components/ExplanationBlock.svelte';
import StopPanel from '../src/components/StopPanel.svelte';
import type { ExplanationView } from '../src/lib/explanationView.svelte';
import { CheckOpening } from '../src/lib/viewState';
import { steppedTarget } from '../src/lib/skips';
import type { Stop, StopContent } from '../src/lib/types';
import { explainedStop, explanationOf } from './explanation';

function content(headline: string, extra: Partial<StopContent> = {}): StopContent {
   const checks = extra.checks ?? [];
   return {
      headline,
      passages: [],
      ...extra,
      html: {
         headline: `<p>${headline}</p>\n`,
         ...(extra.scenario === undefined ? {} : { scenario: `<p>${extra.scenario}</p>\n` }),
         ...(checks.length === 0 ? {} : { checks: checks.map((check) => ({ question: `<p>${check.question}</p>\n` })) }),
      },
   };
}

function stop(id: string, title: string, kind: Stop['kind'], body: StopContent | null, version = 1, staleOn?: number): Stop {
   return stopOf({ id, title, kind, chapter: 'c', content: body, version, ...(staleOn === undefined ? {} : { staleOn }) });
}

const stops: Stop[] = [
   stop(
      's1',
      'Cart totals',
      'inspect',
      content('Totals round.', {
         checks: [
            { id: 'c1', question: 'Is rounding right?' },
            { id: 'c2', question: 'Should the cap move?' },
         ],
      }),
      2,
   ),
   stop('s2', 'Rounding drift', 'risk', content('Drift.', { scenario: 'Two discounts round twice.' })),
   stop('s3', 'Retry policy', 'question', content('Retries.', { checks: [{ id: 'c3', question: 'How often should it retry?' }] }), 1, 2),
   stop('s4', 'Old prices', 'context', null),
];

describe('where next and back go from a position in the plan', () => {
   it('goes to the summary page from the last stop that is not skipped, and from any stop after it', () => {
      expect(steppedTarget(stops, 2, new Set(['s4']), 1, true)).toBe('summary');
      expect(steppedTarget(stops, 3, new Set(['s4']), 1, true)).toBe('summary');
      expect(steppedTarget(stops, 1, new Set(['s4']), 1, true)).toBe(2);
   });

   it('goes back from the summary page to the last stop that is not skipped, and nowhere further on', () => {
      expect(steppedTarget(stops, 'summary', new Set(['s4']), -1, true)).toBe(2);
      expect(steppedTarget(stops, 'summary', new Set(), -1, true)).toBe(3);
      expect(steppedTarget(stops, 'summary', new Set(), 1, true)).toBeNull();
      expect(steppedTarget(stops, 'summary', new Set(stops.map((each) => each.id)), -1, true)).toBeNull();
   });

   it('goes nowhere from a plan without stops, and nowhere back while no stop is open', () => {
      expect(steppedTarget([], -1, new Set(), 1, true)).toBeNull();
      expect(steppedTarget(stops, -1, new Set(), -1, true)).toBeNull();
   });

   it('goes nowhere after the last stop that is not skipped when no summary page follows, and still from stop to stop', () => {
      expect(steppedTarget(stops, 2, new Set(['s4']), 1, false)).toBeNull();
      expect(steppedTarget(stops, 3, new Set(), 1, false)).toBeNull();
      expect(steppedTarget(stops, 1, new Set(['s4']), 1, false)).toBe(2);
      expect(steppedTarget(stops, -1, new Set(), 1, false)).toBe(0);
      expect(steppedTarget(stops, 3, new Set(), -1, false)).toBe(2);
   });
});

describe('a check that the summary page opens', () => {
   beforeEach(() => {
      Element.prototype.scrollIntoView = () => {};
      return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
   });

   const passage = { side: 'head', path: 'src/cart.ts', startLine: 1, endLine: 1, role: 'primary', commit: 'b'.repeat(40), text: 'x' } as const;
   const written = writtenOf(stops[0]);
   // Stop s1 with a primary passage, so inline placement puts its explanation inline.
   const stop1: Stop = { ...written, content: { ...written.content, passages: [passage] } };
   const focusedCheck = () => document.activeElement?.closest('[data-check-id]')?.getAttribute('data-check-id') ?? null;
   const checksButton = (container: HTMLElement) => within(container).getByRole('button', { name: 'Checks' });
   const settled = () => new Promise((resolve) => setTimeout(resolve, 20));

   function focusOutside(): HTMLInputElement {
      const input = document.createElement('input');
      document.body.append(input);
      input.focus();
      return input;
   }

   const inlineBlock = (view: ExplanationView) => render(ExplanationBlock, { props: { view, host: 'inline' } });

   it("opens in the inline block that replaces the Stop panel's stand-in, and not again when the stand-in comes back", async () => {
      const { view } = explanationOf(stop1, { mode: 'inline', page: { openedCheck: new CheckOpening('s1', 'c1') } });
      render(StopPanel, { props: { stop: stop1, view, onopenpassage: () => {} } });
      await settled();
      expect(focusedCheck()).toBeNull();

      const placed = view.place();
      const block = inlineBlock(view);
      await waitFor(() => expect(focusedCheck()).toBe('c1'));
      expect(block.container.contains(document.activeElement)).toBe(true);
      block.unmount();
      placed();
      const input = focusOutside();
      await settled();

      expect(document.activeElement).toBe(input);
      input.remove();
   });

   it('opens once, and not again when the editor draws the inline block anew', async () => {
      const { view } = explanationOf(stop1, { mode: 'inline', page: { openedCheck: new CheckOpening('s1', 'c1') } });
      const first = inlineBlock(view);
      await waitFor(() => expect(focusedCheck()).toBe('c1'));
      await fireEvent.click(checksButton(first.container));
      first.unmount();
      const input = focusOutside();

      const second = inlineBlock(view);
      await settled();

      expect(document.activeElement).toBe(input);
      expect(checksButton(second.container).getAttribute('aria-expanded')).toBe('false');
      input.remove();
   });

   it('opens in the first explanation that shows the check, after one that does not', async () => {
      const withoutChecks: Stop = { ...written, content: content('Totals round.') };
      const { view, stop } = explanationOf(withoutChecks, { page: { openedCheck: new CheckOpening('s1', 'c1') } });
      const { rerender } = render(StopPanel, { props: { stop: withoutChecks, view, onopenpassage: () => {} } });
      await settled();
      expect(focusedCheck()).toBeNull();

      stop.value = explainedStop(written);
      await rerender({ stop: written });

      await waitFor(() => expect(focusedCheck()).toBe('c1'));
   });
});
