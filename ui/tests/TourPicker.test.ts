import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import TourPicker from '../src/components/TourPicker.svelte';
import type { TourSummary } from '../src/lib/types';
import { familySummary } from './family';
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

/** Returns the element that shows the label, whose repository and number are elements of their own. */
const labelText = (label: string) => screen.getByText((_, element) => element?.getAttribute('data-slot') === 'tooltip-trigger' && element.textContent.replace(/\s+/g, ' ') === label);

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

   it('links the selected tour’s label to its pull request in a new tab, with the label and the URL in the link’s tooltip', async () => {
      renderSwitcher(tours[1]!);
      const link = screen.getByRole('link', { name: 'contoso/shop/api #2' });

      expect(link.getAttribute('href')).toBe('https://platform.test/pull/2');
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
      expect(await hoverTooltip(link)).toBe('contoso/shop/api #2\nOpen the pull request in a new tab\nhttps://platform.test/pull/2');
   });

   it('shows the number of a label that ends in one as an element of its own that never shrinks, beside the repository that truncates', () => {
      renderSwitcher(tours[1]!);
      const link = screen.getByRole('link', { name: 'contoso/shop/api #2' });

      const number = within(link).getByText((_, element) => element?.textContent === '\u00a0#2' && element.classList.contains('shrink-0'));
      const repository = within(link).getByText('contoso/shop/api');
      expect(repository.classList.contains('truncate')).toBe(true);
      expect(repository.nextElementSibling).toBe(number);
   });

   it('shows a label that does not end in a number as one element that truncates', () => {
      renderSwitcher({ ...tours[0]!, label: 'https://platform.test/pull/1' });

      const label = within(screen.getByRole('link', { name: 'https://platform.test/pull/1' })).getByText('https://platform.test/pull/1');
      expect(label.classList.contains('truncate')).toBe(true);
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
         const label = labelText('contoso/shop/web #1');
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

function repositorySummary(ref?: string, url = 'https://platform.test/shop'): TourSummary {
   return {
      ...summary('3', ref === undefined ? 'shop' : `shop@${ref}`),
      target: { kind: 'repository', platform: 'test', url, ...(ref === undefined ? {} : { ref }) },
      pin: { state: 'snapshot', head: 'c'.repeat(40), remoteUrl: 'https://platform.test/shop.git' },
      purpose: 'orient',
      title: 'How the cart adds up',
   };
}

describe('a repository tour in the tour switcher', () => {
   const part = (name: string) => document.querySelector<HTMLElement>(`[data-heading-part="${name}"]`);

   it('shows the title, with the repository linked to its URL in a new tab and the ref beside it', async () => {
      renderSwitcher(repositorySummary('v1.2.0'));
      const link = screen.getByRole('link', { name: 'shop' });

      expect(part('title')?.textContent).toBe('How the cart adds up');
      expect(link.getAttribute('href')).toBe('https://platform.test/shop');
      expect(link.getAttribute('target')).toBe('_blank');
      expect(part('ref')?.textContent).toBe('@v1.2.0');
      expect(document.querySelector('.lucide-git-pull-request')).toBeNull();
      expect(await hoverTooltip(part('title')!)).toBe('How the cart adds up\nshop@v1.2.0');
      expect(await hoverTooltip(link)).toBe('Open the repository in a new tab\nhttps://platform.test/shop');
   });

   it('leaves the ref out for the default branch', () => {
      renderSwitcher(repositorySummary());

      expect(part('repository')?.textContent).toBe('shop');
      expect(part('ref')).toBeNull();
   });

   it('shows the repository of a URL that is not https as text', () => {
      renderSwitcher(repositorySummary(undefined, 'http://platform.test/shop'));

      expect(screen.queryByRole('link')).toBeNull();
      expect(part('repository')?.textContent).toBe('shop');
   });

   it('lists the tour by its title, repository and ref, with its purpose and level, and no pull request flag', async () => {
      const tour = { ...repositorySummary('v1.2.0'), pullRequestState: 'completed' as const };
      render(TourPicker, {
         props: {
            tours: [tour],
            hiddenCount: 0,
            showHidden: false,
            loaded: true,
            error: null,
            selectedTourId: null,
            selectedTour: null,
            onselect: vi.fn(),
            onshowhidden: vi.fn(),
            onarchive: vi.fn(),
            ondelete: vi.fn(),
         },
      });
      await fireEvent.click(screen.getByRole('button', { name: 'Choose a tour' }));
      const row = await screen.findByRole('button', { name: /How the cart adds up/ });

      expect(row.textContent.replace(/\s+/g, ' ').trim()).toBe('How the cart adds up Ready shop@v1.2.0 Orient · Some · 2 stops');
      expect(row.querySelector('[data-pull-request]')).toBeNull();
   });

   it('shows the empty list with both kinds of tour to start', async () => {
      render(TourPicker, {
         props: {
            tours: [],
            hiddenCount: 0,
            showHidden: false,
            loaded: true,
            error: null,
            selectedTourId: null,
            selectedTour: null,
            onselect: vi.fn(),
            onshowhidden: vi.fn(),
            onarchive: vi.fn(),
            ondelete: vi.fn(),
         },
      });
      await fireEvent.click(screen.getByRole('button', { name: 'Choose a tour' }));

      expect(await screen.findByText('No tours yet. Ask Claude to start a tour of a pull request or a repository.')).toBeDefined();
   });
});

describe('a tour family in the tour switcher', () => {
   const root = familySummary('root', 0);
   const dive = familySummary('dive', 2, { parent: 'root', root: 'root', stop: 's1' }, { state: 'inProgress' });
   const deeper = familySummary('deeper', 3, { parent: 'dive', root: 'root' });
   const other = familySummary('other', 1);

   function renderFamily(selected: TourSummary, breadcrumb: TourSummary[]) {
      const onselect = vi.fn();
      render(TourPicker, {
         props: {
            tours: [deeper, dive, other, root],
            hiddenCount: 0,
            showHidden: false,
            loaded: true,
            error: null,
            selectedTourId: selected.id,
            selectedTour: selected,
            breadcrumb,
            onselect,
            onshowhidden: vi.fn(),
            onarchive: vi.fn(),
            ondelete: vi.fn(),
         },
      });
      return { onselect, nav: () => within(screen.getByRole('navigation', { name: 'Breadcrumb' })) };
   }

   it("shows a dive's breadcrumb from the root down to the dive's title, each step a button that opens its tour", async () => {
      const { onselect, nav } = renderFamily(deeper, [root, dive]);

      expect(
         nav()
            .getAllByRole('listitem')
            .map((item) => item.textContent.trim()),
      ).toEqual(['Tour root', 'Tour dive', 'Tour deeper']);
      expect(nav().getByText('Tour deeper').closest('[aria-current="page"]')).not.toBeNull();
      expect(await hoverTooltip(nav().getByRole('button', { name: 'Tour root' }))).toBe('Open the tour Tour root');

      await fireEvent.click(nav().getByRole('button', { name: 'Tour root' }));
      expect(onselect).toHaveBeenCalledExactlyOnceWith('root');
   });

   it('keeps only the parent step, after an ellipsis, in a narrow bar', () => {
      const { nav } = renderFamily(deeper, [root, dive]);
      const steps = nav().getAllByRole('listitem');

      expect(steps[0]!.className).toContain('@max-bar-repository:hidden');
      expect(steps.find((step) => step.textContent.trim() === 'Tour dive')!.className).not.toContain('@max-bar-repository:hidden');
      expect(nav().getByText('…', { exact: false }).closest('li')!.getAttribute('aria-hidden')).toBe('true');
   });

   it('shows no breadcrumb for a tour that is not a dive', () => {
      renderFamily(root, []);

      expect(screen.queryByRole('navigation', { name: 'Breadcrumb' })).toBeNull();
      expect(document.querySelector('[data-heading-part="title"]')?.textContent).toBe('Tour root');
   });

   it("lists the family under its root, one level deep, with each dive's parent in place of the repository", async () => {
      renderFamily(root, []);
      await fireEvent.click(screen.getByRole('button', { name: 'Choose another tour' }));
      const list = within(await screen.findByRole('navigation', { name: 'Tours' }));
      const items = list.getAllByRole('listitem');

      expect(items.map((item) => `${item.querySelector('[id^="tour-label-"]')!.textContent}:${item.dataset.depth}`)).toEqual(['Tour other:0', 'Tour root:0', 'Tour dive:1', 'Tour deeper:1']);
      expect(items[2]!.querySelector('[data-dive-parent]')?.textContent.trim()).toBe('Dive from Tour root');
      expect(items[3]!.querySelector('[data-dive-parent]')?.textContent.trim()).toBe('Dive from Tour dive');
      expect(items[1]!.querySelector('[data-dive-parent]')).toBeNull();
   });
});
