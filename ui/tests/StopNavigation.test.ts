import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import StopNavigation from '../src/components/StopNavigation.svelte';
import type { Plan, Stop } from '../src/lib/types';
import { hoverTooltip, openTooltip } from './tooltip';

function stop(id: string, title: string, chapter: string): Stop {
   return { id, title, kind: 'inspect', chapter, stage: 'pending', anchorHint: null, status: 'pending' };
}

const plan: Plan = {
   chapters: [
      { id: 'pay', title: 'Paying' },
      { id: 'approve', title: 'Before you approve' },
   ],
   stops: [stop('s1', 'The new payment step', 'pay'), stop('s2', 'Does anything still post the old payment form?', 'approve'), stop('s3', 'Orphan', 'gone')],
   fileMarks: [],
};

function renderNavigation(index: number, shown: Plan | null | undefined = plan, summary = false) {
   const onprevious = vi.fn();
   const onnext = vi.fn();
   const result = render(StopNavigation, { props: { plan: shown, index, summary, summaryFollows: true, onprevious, onnext } });
   const element = screen.getByRole('navigation', { name: 'Stops' });
   const text = () => element.querySelector('p')!;
   return { ...result, navigation: within(element), element, text, shown: () => text().textContent.replace(/\s+/g, ' ').trim(), onprevious, onnext };
}

describe('the stop navigation', () => {
   it('puts both buttons before the text', () => {
      const { element, text } = renderNavigation(1);
      const labels = within(element)
         .getAllByRole('button')
         .map((button) => button.getAttribute('aria-label'));
      const next = within(element).getByRole('button', { name: 'Next stop' });

      expect(labels).toEqual(['Previous stop', 'Next stop']);
      expect(next.compareDocumentPosition(text()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
   });

   it('shows the open stop’s position, chapter and title, with the full text in a tooltip for when it is cut short', async () => {
      const { shown, text } = renderNavigation(1);

      expect(shown()).toBe('Stop 2 of 3 · Before you approve · Does anything still post the old payment form?');
      expect(text().hasAttribute('data-state')).toBe(true);
      expect(await hoverTooltip(text())).toBe('Stop 2 of 3 · Before you approve · Does anything still post the old payment form?');
   });

   it('leaves out a chapter the plan does not name', async () => {
      const { shown, text } = renderNavigation(2);

      expect(shown()).toBe('Stop 3 of 3 · Orphan');
      expect(await hoverTooltip(text())).toBe('Stop 3 of 3 · Orphan');
   });

   it('shows the stop count without a tooltip while no stop is open, and no stops while the plan loads', () => {
      const closed = renderNavigation(-1);
      expect(closed.shown()).toBe('3 stops');
      expect(closed.text().hasAttribute('data-state')).toBe(false);
      closed.unmount();

      expect(renderNavigation(-1, null).shown()).toBe('No stops yet');
   });

   it('follows the open stop as it changes and as it closes, as a tour switch does', async () => {
      const { shown, text, rerender } = renderNavigation(0);
      await rerender({ index: 1 });
      expect(shown()).toBe('Stop 2 of 3 · Before you approve · Does anything still post the old payment form?');
      expect(text().hasAttribute('data-state')).toBe(true);

      await rerender({ plan: undefined, index: -1 });
      expect(shown()).toBe('No stops yet');
      expect(text().hasAttribute('data-state')).toBe(false);
   });

   it('keeps one live region as a stop opens from none and closes, so a screen reader announces each change', async () => {
      const { element, rerender } = renderNavigation(-1);
      const live = element.querySelector('[aria-live="polite"]')!;

      await rerender({ index: 0 });
      expect(live.isConnected).toBe(true);
      expect(live.textContent.replace(/\s+/g, ' ').trim()).toBe('Stop 1 of 3 · Paying · The new payment step');

      await rerender({ index: -1 });
      expect(live.isConnected).toBe(true);
      expect(live.textContent.replace(/\s+/g, ' ').trim()).toBe('3 stops');
   });

   it('disables Previous stop at the first stop, and asks for the stop beside the open one', () => {
      const first = renderNavigation(0);
      expect(first.navigation.getByRole('button', { name: 'Previous stop' }).hasAttribute('disabled')).toBe(true);
      fireEvent.click(first.navigation.getByRole('button', { name: 'Next stop' }));
      expect(first.onnext).toHaveBeenCalledOnce();
      first.unmount();

      const last = renderNavigation(2);
      fireEvent.click(last.navigation.getByRole('button', { name: 'Previous stop' }));
      expect(last.onprevious).toHaveBeenCalledOnce();
   });

   it('names Next after the summary page on the last stop, and asks for it', async () => {
      const { navigation, onnext } = renderNavigation(2);
      const next = navigation.getByRole('button', { name: 'Summary' });

      expect(next.hasAttribute('disabled')).toBe(false);
      expect(await hoverTooltip(next)).toBe('Summary');
      await fireEvent.click(next);
      expect(onnext).toHaveBeenCalledOnce();
   });

   it('shows the summary page as the position, with Previous stop enabled and Next stop disabled', async () => {
      const { navigation, shown, text, onprevious } = renderNavigation(-1, plan, true);

      expect(shown()).toBe('Summary · After the last stop');
      expect(await hoverTooltip(text())).toBe('Summary · After the last stop');
      expect(navigation.getByRole('button', { name: 'Next stop' }).hasAttribute('disabled')).toBe(true);
      await fireEvent.click(navigation.getByRole('button', { name: 'Previous stop' }));
      expect(onprevious).toHaveBeenCalledOnce();
   });

   it('shows the tooltip of a disabled Next stop on the summary page, which stays disabled, inert and out of the tab order', async () => {
      const { navigation, onnext } = renderNavigation(-1, plan, true);
      const next = navigation.getByRole('button', { name: 'Next stop' });
      // A disabled button gets no pointer events in a browser, so the pointer reaches the element around it.
      const hovered = next.closest('[data-state]')!;
      expect(hovered).not.toBe(next);
      expect(hovered.hasAttribute('disabled')).toBe(false);
      expect(hovered.hasAttribute('tabindex')).toBe(false);

      expect(await hoverTooltip(hovered)).toBe('Next stop');

      expect(next.hasAttribute('disabled')).toBe(true);
      expect(next.getAttribute('aria-label')).toBe('Next stop');
      // jsdom runs a dispatched click's listeners even on a disabled button, so the button gets click(), which skips them as a browser does.
      next.click();
      await fireEvent.click(hovered);
      expect(onnext).not.toHaveBeenCalled();
      next.focus();
      expect(document.activeElement).not.toBe(next);
   });

   it('keeps the Next stop button and its tooltip as the button turns disabled on the summary page and enabled again', async () => {
      const { navigation, rerender, onnext } = renderNavigation(1);
      const next = navigation.getByRole('button', { name: 'Next stop' });
      expect(await hoverTooltip(next)).toBe('Next stop');

      await rerender({ index: -1, summary: true });
      expect(navigation.getByRole('button', { name: 'Next stop' })).toBe(next);
      expect(next.hasAttribute('disabled')).toBe(true);
      expect(next.hasAttribute('data-state')).toBe(false);
      expect(await openTooltip(next.closest('[data-state]')!)).toBe('Next stop');

      await rerender({ index: 1, summary: false });
      expect(navigation.getByRole('button', { name: 'Next stop' })).toBe(next);
      expect(next.hasAttribute('data-state')).toBe(true);
      await fireEvent.click(next);
      expect(onnext).toHaveBeenCalledOnce();
   });
});
