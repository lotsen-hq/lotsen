import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import TourPicker from '../src/components/TourPicker.svelte';
import type { TourSummary } from '../src/lib/types';
import { familySummary } from './family';
import { hoverOwnTooltip, hoverTooltip } from './tooltip';

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

const line = (name: 'place' | 'title') => document.querySelector<HTMLElement>(`[data-switcher-line="${name}"]`);
const descriptionOf = (element: HTMLElement) =>
   document
      .getElementById(element.getAttribute('aria-describedby') ?? '')
      ?.textContent.replace(/\s+/g, ' ')
      .trim();
/** Returns the line's text as a browser renders it, where the space between the line's parts collapses. */
const textOf = (element: HTMLElement | null) => element?.textContent.replace(/\s+/g, ' ').trim();

describe('the tour switcher', () => {
   it('reads Choose a tour as one button, with no link and no up button, while no tour is selected', async () => {
      renderSwitcher(null);
      const switcher = screen.getByRole('button', { name: 'Choose a tour' });

      expect(screen.getAllByRole('button')).toEqual([switcher]);
      expect(switcher.textContent.trim()).toBe('Choose a tour');
      expect(screen.queryByRole('link')).toBeNull();
      expect(screen.queryByRole('button', { name: /^Back to / })).toBeNull();
      expect(await hoverTooltip(switcher)).toBe('Choose a tour');
   });

   it("shows a pull request tour's repository on the top line and its number on the bottom line, named by the bottom line and described by the top line", async () => {
      renderSwitcher(tours[1]!);
      const switcher = screen.getByRole('button', { name: 'Pull request #2, choose another tour' });

      expect(textOf(line('place'))).toBe('contoso/shop/api');
      expect(line('title')?.textContent).toBe('Pull request #2');
      expect(line('title')!.classList.contains('truncate')).toBe(true);
      expect(descriptionOf(switcher)).toBe('contoso/shop/api');
      expect(switcher.querySelector('.lucide-git-pull-request')).not.toBeNull();
      expect(await hoverOwnTooltip(switcher)).toBe('Pull request #2\ncontoso/shop/api\nChoose another tour');
   });

   it('shows a label that does not end in a number whole on one line, with no top line', () => {
      renderSwitcher({ ...tours[0]!, label: 'https://platform.test/pull/1' });
      const switcher = screen.getByRole('button', { name: 'https://platform.test/pull/1, choose another tour' });

      expect(line('title')?.textContent).toBe('https://platform.test/pull/1');
      expect(line('place')).toBeNull();
      expect(switcher.hasAttribute('aria-describedby')).toBe(false);
   });

   it('opens the tour list on a click on the bottom line, and a choice closes it and names the chosen tour', async () => {
      const { list, onselect } = renderSwitcher(tours[0]!);
      const switcher = screen.getByRole('button', { name: 'Pull request #1, choose another tour' });

      await fireEvent.click(line('title')!);
      expect(switcher.getAttribute('aria-expanded')).toBe('true');
      await fireEvent.click(within(list()!).getByRole('button', { name: /#2/ }));

      expect(onselect).toHaveBeenCalledWith('2');
      await waitFor(() => expect(list()).toBeNull());
      expect(switcher.getAttribute('aria-expanded')).toBe('false');
   });

   it('opens the tour list on a click on the top line', async () => {
      const { list } = renderSwitcher(tours[0]!);

      await fireEvent.click(line('place')!);

      expect(list()).not.toBeNull();
   });

   it('links the pull request in a new tab with an icon button beside the switcher, with the URL under the name in its tooltip', async () => {
      renderSwitcher(tours[1]!);
      const link = screen.getByRole('link', { name: 'Open the pull request in a new tab' });

      expect(link.getAttribute('href')).toBe('https://platform.test/pull/2');
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
      expect(link.textContent.trim()).toBe('');
      expect(await hoverTooltip(link)).toBe('Open the pull request in a new tab\nhttps://platform.test/pull/2');
   });

   it('links a URL whose https: scheme is in upper case', () => {
      renderSwitcher(withUrl(tours[0]!, 'HTTPS://platform.test/pull/1'));

      expect(screen.getByRole('link', { name: 'Open the pull request in a new tab' }).getAttribute('href')).toBe('HTTPS://platform.test/pull/1');
   });

   it.each(['http://platform.test/pull/1', 'javascript:alert(1)', 'data:text/html,x', 'not a url'])('shows no link for a tour whose URL is %s, and the switcher still opens the list', async (url) => {
      const { list } = renderSwitcher(withUrl(tours[0]!, url));

      expect(screen.queryByRole('link')).toBeNull();
      await fireEvent.click(screen.getByRole('button', { name: 'Pull request #1, choose another tour' }));
      expect(list()).not.toBeNull();
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
      const chooser = screen.getByRole('button', { name: 'Pull request #1, choose another tour' });
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
      await fireEvent.click(screen.getByRole('button', { name: 'Pull request #1, choose another tour' }));
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
      expect(switcher.getAttribute('aria-label')).toBe('Pull request #1, choose another tour');
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
   it('shows the repository, the ref and the short commit on the top line and the title on the bottom line', async () => {
      renderSwitcher(repositorySummary('v1.2.0'));
      const switcher = screen.getByRole('button', { name: 'How the cart adds up, choose another tour' });

      expect(textOf(line('place'))).toBe('shop@v1.2.0 · cccccccc');
      expect(line('title')?.textContent).toBe('How the cart adds up');
      expect(descriptionOf(switcher)).toBe('shop@v1.2.0 · cccccccc');
      expect(switcher.querySelector('.lucide-book-open')).not.toBeNull();
      expect(switcher.querySelector('.lucide-git-pull-request')).toBeNull();
      expect(await hoverOwnTooltip(switcher)).toBe('How the cart adds up\nshop@v1.2.0 · cccccccc\nChoose another tour');
   });

   it('truncates the repository and the ref, never the commit', () => {
      renderSwitcher(repositorySummary('v1.2.0'));
      const commit = line('place')!.querySelector<HTMLElement>('[data-tour-commit]')!;

      expect(commit.textContent).toBe('cccccccc');
      expect(commit.closest('.shrink-0')).not.toBeNull();
      expect(within(line('place')!).getByText('shop@v1.2.0').classList.contains('truncate')).toBe(true);
   });

   it('leaves the ref out for the default branch', () => {
      renderSwitcher(repositorySummary());

      expect(textOf(line('place'))).toBe('shop · cccccccc');
   });

   it('links the repository in a new tab with an icon button', async () => {
      renderSwitcher(repositorySummary('v1.2.0'));
      const link = screen.getByRole('link', { name: 'Open the repository in a new tab' });

      expect(link.getAttribute('href')).toBe('https://platform.test/shop');
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
      expect(await hoverTooltip(link)).toBe('Open the repository in a new tab\nhttps://platform.test/shop');
   });

   it.each(['http://platform.test/shop', 'javascript:alert(1)', 'data:text/html,x', 'not a url'])(
      'shows no link for a repository whose URL is %s, and the switcher still opens the list',
      async (url) => {
         const { list } = renderSwitcher(repositorySummary(undefined, url));

         expect(screen.queryByRole('link')).toBeNull();
         await fireEvent.click(screen.getByRole('button', { name: 'How the cart adds up, choose another tour' }));
         expect(list()).not.toBeNull();
      },
   );

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

   function renderFamily(selected: TourSummary, parent: TourSummary | null) {
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
            parent,
            onselect,
            onshowhidden: vi.fn(),
            onarchive: vi.fn(),
            ondelete: vi.fn(),
         },
      });
      return { onselect };
   }

   it("shows a dive's up button, named by its parent, which opens the parent and leaves the focus on the switcher", async () => {
      const { onselect } = renderFamily(deeper, dive);
      const up = screen.getByRole('button', { name: 'Back to Tour dive' });
      const switcher = screen.getByRole('button', { name: 'Tour deeper, choose another tour' });

      expect(up.querySelector('.lucide-corner-left-up')).not.toBeNull();
      expect(up.compareDocumentPosition(switcher) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(await hoverTooltip(up)).toBe('Back to Tour dive');
      up.focus();
      await fireEvent.click(up);

      expect(onselect).toHaveBeenCalledExactlyOnceWith('dive');
      await waitFor(() => expect(document.activeElement).toBe(switcher));
   });

   it('shows no up button for a tour that is not a dive', () => {
      renderFamily(root, null);

      expect(screen.queryByRole('button', { name: /^Back to / })).toBeNull();
      expect(line('title')?.textContent).toBe('Tour root');
   });

   it("lists the family under its root, one level deep, with each dive's parent in place of the repository", async () => {
      renderFamily(root, null);
      await fireEvent.click(screen.getByRole('button', { name: 'Tour root, choose another tour' }));
      const list = within(await screen.findByRole('navigation', { name: 'Tours' }));
      const items = list.getAllByRole('listitem');

      expect(items.map((item) => `${item.querySelector('[id^="tour-label-"]')!.textContent}:${item.dataset.depth}`)).toEqual(['Tour other:0', 'Tour root:0', 'Tour dive:1', 'Tour deeper:1']);
      expect(items[2]!.querySelector('[data-dive-parent]')?.textContent.trim()).toBe('Dive from Tour root');
      expect(items[3]!.querySelector('[data-dive-parent]')?.textContent.trim()).toBe('Dive from Tour dive');
      expect(items[1]!.querySelector('[data-dive-parent]')).toBeNull();
   });
});

describe('the delete confirmation of a tour family', () => {
   const root = familySummary('root', 0);
   const dive = familySummary('dive', 1, { parent: 'root', root: 'root', stop: 's1' });
   const deeper = familySummary('deeper', 2, { parent: 'dive', root: 'root' });
   // An archived dive, which the list hides and the delete still takes.
   const hidden = familySummary('hidden', 3, { parent: 'root', root: 'root' }, { state: 'archived' });
   const other = familySummary('other', 4);

   function renderFamily(allTours: TourSummary[], ondelete: (tourId: string) => Promise<string | null> = vi.fn(async () => null)) {
      const tours = allTours.filter((tour) => tour.state !== 'archived').reverse();
      render(TourPicker, {
         props: {
            tours,
            allTours,
            hiddenCount: allTours.length - tours.length,
            showHidden: false,
            loaded: true,
            error: null,
            selectedTourId: null,
            selectedTour: null,
            onselect: vi.fn(),
            onshowhidden: vi.fn(),
            onarchive: vi.fn(),
            ondelete,
         },
      });
   }

   /** Opens the list, clicks the delete button of the tour with the title, and returns the dialog. */
   async function askToDelete(title: string): Promise<HTMLElement> {
      await fireEvent.click(screen.getByRole('button', { name: 'Choose a tour' }));
      const list = within(await screen.findByRole('navigation', { name: 'Tours' }));
      const row = list.getAllByRole('listitem').find((item) => item.querySelector('[id^="tour-label-"]')?.textContent === title)!;
      await fireEvent.click(within(row).getByRole('button', { hidden: true, name: (_, element) => element.getAttribute('aria-label') === 'Delete this tour' }));
      return screen.findByRole('alertdialog', { name: 'Delete this tour?' });
   }

   // Each item as its text, followed by its nested items in brackets.
   const nested = (list: HTMLElement): string[] =>
      [...list.children].map((item) => {
         const inner = item.querySelector(':scope > ul');
         const text = [...item.childNodes]
            .filter((node) => node.nodeType === Node.TEXT_NODE)
            .map((node) => node.textContent)
            .join('')
            .trim();
         return inner === null ? text : `${text}[${nested(inner as HTMLElement).join(', ')}]`;
      });

   it('names every dive below the tour, nested as in the family and with the hidden ones, and says they are deleted with it', async () => {
      renderFamily([root, dive, deeper, hidden, other]);

      const dialog = await askToDelete('Tour root');

      expect(
         within(dialog)
            .getByText(/is deleted for good|are deleted for good/)
            .textContent.replace(/\s+/g, ' ')
            .trim(),
      ).toBe(
         "The tour “Tour root” of shop and the 3 dives below it are deleted for good, each with its plan, stops, threads, notes and answers. This cannot be undone. Claude's session can no longer work on them.",
      );
      const list = within(dialog).getByRole('list', { name: 'Dives deleted with it' });
      expect(nested(list)).toEqual(['Tour dive[Tour deeper]', 'Tour hidden']);
   });

   it("names a dive's own dives alone when the dive is deleted", async () => {
      renderFamily([root, dive, deeper, other]);

      const dialog = await askToDelete('Tour dive');

      expect(dialog.textContent).toContain('The tour “Tour dive” of shop and the dive below it are deleted for good');
      expect(nested(within(dialog).getByRole('list', { name: 'Dives deleted with it' }))).toEqual(['Tour deeper']);
   });

   it('asks as before for a tour without dives', async () => {
      renderFamily([root, dive, other]);

      const dialog = await askToDelete('Tour other');

      expect(
         within(dialog)
            .getByText(/is deleted for good/)
            .textContent.replace(/\s+/g, ' ')
            .trim(),
      ).toBe("The tour “Tour other” of shop is deleted for good, with its plan, stops, threads, notes and answers. This cannot be undone. Claude's session can no longer work on it.");
      expect(within(dialog).queryByRole('list')).toBeNull();
   });

   it('names the first ten dives of a large family and counts the rest', async () => {
      const dives = Array.from({ length: 13 }, (_, index) => familySummary(`d${index}`, index + 1, { parent: 'root', root: 'root' }));
      renderFamily([root, ...dives]);

      const dialog = await askToDelete('Tour root');

      expect(dialog.textContent).toContain('the 13 dives below it');
      expect(nested(within(dialog).getByRole('list', { name: 'Dives deleted with it' }))).toHaveLength(10);
      expect(within(dialog).getByText('and 3 more dives')).toBeTruthy();
   });

   it('shows the reason of a refused delete in the dialog, which keeps naming the dives', async () => {
      const reason = 'tour root and its dives stay as they are. Tour deeper could not be read.';
      renderFamily(
         [root, dive, deeper],
         vi.fn(async () => reason),
      );

      const dialog = await askToDelete('Tour root');
      await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete tour' }));

      expect((await within(dialog).findByRole('alert')).textContent).toBe(reason);
      expect(nested(within(dialog).getByRole('list', { name: 'Dives deleted with it' }))).toEqual(['Tour dive[Tour deeper]']);
   });

   it('names the dives below a tour on its archive button', async () => {
      renderFamily([root, dive, deeper, other]);
      await fireEvent.click(screen.getByRole('button', { name: 'Choose a tour' }));
      const list = within(await screen.findByRole('navigation', { name: 'Tours' }));
      const archiveNames = list.getAllByRole('listitem').map((item) => item.querySelector('button[aria-label^="Archive"]')!.getAttribute('aria-label'));

      expect(archiveNames).toEqual(['Archive this tour', 'Archive this tour and the 2 dives below it', 'Archive this tour and the dive below it', 'Archive this tour']);
   });
});
