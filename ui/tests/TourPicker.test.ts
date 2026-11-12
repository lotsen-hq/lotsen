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

function withUrl(tour: TourSummary, url: string): TourSummary {
   return { ...tour, target: { ...tour.target, url } };
}

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
   it('reads Choose a tour as one button, with no link, while no tour is selected', async () => {
      renderSwitcher(null);
      const switcher = screen.getByRole('button', { name: 'Choose a tour' });

      expect(screen.getAllByRole('button')).toEqual([switcher]);
      expect(switcher.textContent.trim()).toBe('Choose a tour');
      expect(screen.queryByRole('link')).toBeNull();
      expect(await hoverTooltip(switcher)).toBe('Choose a tour');
   });

   it('links the selected tour’s label to its pull request in a new tab, with the URL in the link’s tooltip', async () => {
      renderSwitcher(tours[1]!);
      const link = screen.getByRole('link', { name: 'contoso/shop/api #2' });

      expect(link.getAttribute('href')).toBe('https://platform.test/pull/2');
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
      expect(await hoverTooltip(link)).toBe('Open the pull request in a new tab\nhttps://platform.test/pull/2');
   });

   it('links a URL whose https: scheme is in upper case', () => {
      renderSwitcher(withUrl(tours[0]!, 'HTTPS://platform.test/pull/1'));

      expect(screen.getByRole('link', { name: 'contoso/shop/web #1' }).getAttribute('href')).toBe('HTTPS://platform.test/pull/1');
   });

   it.each(['http://platform.test/pull/1', 'javascript:alert(1)', 'data:text/html,x', 'not a url'])(
      'shows the label of a tour whose URL is %s as text, and Choose another tour still opens the list',
      async (url) => {
         const { list } = renderSwitcher(withUrl(tours[0]!, url));

         expect(screen.queryByRole('link')).toBeNull();
         const label = screen.getByText('contoso/shop/web #1');
         expect(await hoverTooltip(label)).toBe(`contoso/shop/web #1\n${url}`);
         await fireEvent.click(screen.getByRole('button', { name: 'Choose another tour' }));
         expect(list()).not.toBeNull();
      },
   );

   it('opens the tour list from Choose another tour, and a choice closes it and names the chosen tour', async () => {
      const { list, onselect } = renderSwitcher(tours[0]!);
      const chooser = screen.getByRole('button', { name: 'Choose another tour' });
      expect(await hoverTooltip(chooser)).toBe('Choose another tour');

      await fireEvent.click(chooser);
      expect(chooser.getAttribute('aria-expanded')).toBe('true');
      await fireEvent.click(within(list()!).getByRole('button', { name: /#2/ }));

      expect(onselect).toHaveBeenCalledWith('2');
      await waitFor(() => expect(list()).toBeNull());
      expect(chooser.getAttribute('aria-expanded')).toBe('false');
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
      const chooser = screen.getByRole('button', { name: 'Choose another tour' });
      // jsdom never positions the popover, which leaves the list hidden from role queries, so this finds it by its markup.
      const list = () => document.querySelector('nav[aria-labelledby="tour-list-heading"]');
      chooser.focus();

      await fireEvent.keyDown(chooser, { key: 'Enter' });
      // A browser moves the focus to the first tour as the list opens, which jsdom does not do while the list is hidden.
      const focusedTour = await waitFor(() => list()!.querySelector('button')!);
      focusedTour.focus();
      expect(await hoverTooltip(focusedTour)).toBe('https://platform.test/pull/1');
      await fireEvent.keyDown(focusedTour, { key: 'Escape' });

      await waitFor(() => expect(list()).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(chooser));
   });

   it('closes the list on one Escape while an archive button shows its tooltip', async () => {
      const { list } = renderSwitcher(tours[0]!);
      // A click opens the list without the keyboard focus that would show the trigger's own tooltip.
      await fireEvent.click(screen.getByRole('button', { name: 'Choose another tour' }));
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

   it('keeps the same trigger element when the first tour is chosen, so it keeps the focus that the closing list returns', async () => {
      const { rerender } = renderSwitcher(null);
      const switcher = screen.getByRole('button', { name: 'Choose a tour' });

      await rerender({ selectedTourId: '1', selectedTour: tours[0]! });

      expect(switcher.isConnected).toBe(true);
      expect(switcher.getAttribute('aria-label')).toBe('Choose another tour');
   });
});
