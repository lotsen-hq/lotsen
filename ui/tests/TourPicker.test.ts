import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import TourPicker from '../src/components/TourPicker.svelte';
import type { TourSummary } from '../src/lib/types';
import { hoverTooltip } from './tooltip';

const pinnedPin = { state: 'pinned', mergeBase: 'a'.repeat(40), head: 'b'.repeat(40), iteration: 1, remoteUrl: 'https://platform.test/shop.git' } as const;

function summary(id: string, label: string): TourSummary {
   const target = { kind: 'pullRequest', platform: 'test', url: `https://platform.test/pull/${id}` } as const;
   return {
      id,
      label,
      target,
      familiarity: 'some',
      createdAt: '2026-10-26T12:00:00Z',
      updatedAt: '2026-10-26T12:00:00Z',
      pin: pinnedPin,
      stopCount: 2,
      state: 'ready',
      pullRequestState: 'active',
      revision: 1,
   };
}

const tours = [summary('1', 'contoso/shop/web #1'), summary('2', 'contoso/shop/api #2')];

function renderSwitcher(selected: TourSummary | null) {
   const onselect = vi.fn();
   const result = render(TourPicker, {
      props: {
         tours,
         hiddenCount: 0,
         showHidden: false,
         loaded: true,
         error: null,
         selectedTourId: selected?.id ?? null,
         selectedTour: selected,
         onselect,
         onshowhidden: vi.fn(),
         onarchive: vi.fn(),
         ondelete: vi.fn(),
      },
   });
   const list = () => screen.queryByRole('navigation', { name: 'Tours' });
   return { ...result, onselect, list };
}

describe('the tour switcher', () => {
   it('reads Choose a tour while no tour is selected', async () => {
      renderSwitcher(null);
      const switcher = screen.getByRole('button', { name: 'Choose a tour' });

      expect(switcher.textContent.trim()).toBe('Choose a tour');
      expect(await hoverTooltip(switcher)).toBe('Choose a tour');
   });

   it('shows the selected tour’s label, names the tour in its accessible name, and gives the label and URL in its tooltip', async () => {
      renderSwitcher(tours[1]!);
      const switcher = screen.getByRole('button', { name: 'Tour: contoso/shop/api #2, choose another' });

      expect(switcher.textContent.trim()).toBe('contoso/shop/api #2');
      expect(await hoverTooltip(switcher)).toBe('contoso/shop/api #2\nhttps://platform.test/pull/2');
   });

   it('opens the tour list, and a choice closes it and names the chosen tour', async () => {
      const { list, onselect } = renderSwitcher(tours[0]!);
      const switcher = screen.getByRole('button', { name: /^Tour: / });

      await fireEvent.click(switcher);
      expect(switcher.getAttribute('aria-expanded')).toBe('true');
      await fireEvent.click(within(list()!).getByRole('button', { name: /#2/ }));

      expect(onselect).toHaveBeenCalledWith('2');
      await waitFor(() => expect(list()).toBeNull());
      expect(switcher.getAttribute('aria-expanded')).toBe('false');
   });

   it('returns focus to itself when Escape closes the list', async () => {
      const { list } = renderSwitcher(null);
      const switcher = screen.getByRole('button', { name: 'Choose a tour' });
      switcher.focus();

      await fireEvent.click(switcher);
      expect(list()).not.toBeNull();
      await fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });

      await waitFor(() => expect(list()).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(switcher));
   });

   it('closes the list on one Escape after opening from the keyboard, while the focused tour shows its tooltip', async () => {
      renderSwitcher(tours[0]!);
      const switcher = screen.getByRole('button', { name: /^Tour: / });
      // jsdom never positions the popover, which leaves the list hidden from role queries, so this finds it by its markup.
      const list = () => document.querySelector('nav[aria-labelledby="tour-list-heading"]');
      switcher.focus();

      await fireEvent.keyDown(switcher, { key: 'Enter' });
      // A browser moves the focus to the first tour as the list opens, which jsdom does not do while the list is hidden.
      const focusedTour = await waitFor(() => list()!.querySelector('button')!);
      focusedTour.focus();
      expect(await hoverTooltip(focusedTour)).toBe('https://platform.test/pull/1');
      await fireEvent.keyDown(focusedTour, { key: 'Escape' });

      await waitFor(() => expect(list()).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(switcher));
   });

   it('closes the list on one Escape while an archive button shows its tooltip', async () => {
      const { list } = renderSwitcher(tours[0]!);
      // A click opens the list without the keyboard focus that would show the switcher's own tooltip.
      await fireEvent.click(screen.getByRole('button', { name: /^Tour: / }));
      const nav = () => document.querySelector('nav[aria-labelledby="tour-list-heading"]');
      const archive = await waitFor(() => nav()!.querySelector<HTMLElement>('button[aria-label="Archive this tour"]')!);
      await fireEvent.pointerEnter(archive, { pointerType: 'mouse' });
      // The button's own description names its tour, so the test finds the tooltip by its text.
      const tooltips = () => [...document.querySelectorAll('[data-slot="tooltip-content"]')].map((tooltip) => tooltip.textContent.trim());
      await waitFor(() => expect(tooltips()).toEqual(['Archive this tour']), { timeout: 2000 });
      await fireEvent.keyDown(archive, { key: 'Escape' });

      await waitFor(() => expect(nav()).toBeNull());
      expect(list()).toBeNull();
   });

   it('stays the same element when the first tour is chosen, so it keeps the focus that the closing list returns', async () => {
      const { rerender } = renderSwitcher(null);
      const switcher = screen.getByRole('button', { name: 'Choose a tour' });

      await rerender({ selectedTourId: '1', selectedTour: tours[0]! });

      expect(switcher.isConnected).toBe(true);
      expect(switcher.getAttribute('aria-label')).toBe('Tour: contoso/shop/web #1, choose another');
   });
});
