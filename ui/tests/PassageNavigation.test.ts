import { cleanup, fireEvent, render, screen, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import PassageNavigation from '../src/components/PassageNavigation.svelte';
import type { FilePaths } from '../src/lib/pin';
import type { FileSide, StopPassage } from '../src/lib/types';
import { hoverTooltip } from './tooltip';

function stopPassage(side: FileSide, path: string, startLine: number, endLine: number, role: StopPassage['role'] = 'related', label?: string): StopPassage {
   return { side, path, startLine, endLine, role, commit: 'c'.repeat(40), text: 'text', ...(label === undefined ? {} : { label }) };
}

const passages = [stopPassage('head', 'src/cart.ts', 5, 7, 'primary'), stopPassage('head', 'src/pay.ts', 30, 31, 'related', 'caller'), stopPassage('base', 'src/cart.ts', 20, 20, 'related', 'before')];
const cart: FilePaths = { base: 'src/cart.ts', head: 'src/cart.ts' };

function renderNavigation(index: number, openFile: FilePaths | null, list: readonly StopPassage[] = passages) {
   const onprevious = vi.fn();
   const onnext = vi.fn();
   render(PassageNavigation, { props: { passages: list, index, openFile, onprevious, onnext } });
   const element = screen.getByRole('navigation', { name: 'Passages' });
   const navigation = within(element);
   return { navigation, onprevious, onnext, text: () => shownText(element), spoken: () => navigation.getByRole('status').textContent.trim() };
}

/** The navigation's visible text, without its status, which only a screen reader reads. */
function shownText(navigation: HTMLElement): string {
   const shown = navigation.cloneNode(true) as HTMLElement;
   shown.querySelector('[role="status"]')?.remove();
   return shown.textContent.replace(/\s+/g, ' ').trim();
}

describe('the passage navigation', () => {
   it('shows the count and the focused passage’s lines without its file while the open file holds the passage', () => {
      expect(renderNavigation(0, cart).text()).toBe('1/3 lines 5–7');
   });

   it('shows the passage’s file name while another file is open, with the full path in its tooltip', async () => {
      const { navigation, text } = renderNavigation(0, { base: 'src/pay.ts', head: 'src/pay.ts' });

      expect(text()).toBe('1/3 cart.ts lines 5–7');
      expect(await hoverTooltip(navigation.getByText('cart.ts'))).toBe('src/cart.ts');
   });

   it('shows the passage’s file name while no file is open', () => {
      expect(renderNavigation(0, null).text()).toBe('1/3 cart.ts lines 5–7');
   });

   it('shows a related passage’s label in the tooltip of its lines', async () => {
      const { navigation, text } = renderNavigation(1, { base: 'src/pay.ts', head: 'src/pay.ts' });

      expect(text()).toBe('2/3 lines 30–31');
      expect(await hoverTooltip(navigation.getByText('lines 30–31'))).toBe('caller');
   });

   it('compares the file at the passage’s side, so a base passage names its file while an added file of its path is open', () => {
      expect(renderNavigation(2, cart).text()).toBe('3/3 line 20 at the base');
      cleanup();

      expect(renderNavigation(2, { base: null, head: 'src/cart.ts' }).text()).toBe('3/3 cart.ts line 20 at the base');
   });

   it('matches a renamed file’s base passage by the old path', () => {
      const moved = [stopPassage('base', 'src/old.ts', 3, 4)];

      expect(renderNavigation(0, { base: 'src/old.ts', head: 'src/new.ts' }, moved).text()).toBe('1/1 lines 3–4 at the base');
   });

   it('disables Previous passage at the first passage and Next passage at the last, and asks for the passage beside the focused one', () => {
      const first = renderNavigation(0, cart);
      expect(first.navigation.getByRole('button', { name: 'Previous passage' }).hasAttribute('disabled')).toBe(true);
      fireEvent.click(first.navigation.getByRole('button', { name: 'Next passage' }));
      expect(first.onnext).toHaveBeenCalledOnce();
      cleanup();

      const last = renderNavigation(2, cart);
      expect(last.navigation.getByRole('button', { name: 'Next passage' }).hasAttribute('disabled')).toBe(true);
      fireEvent.click(last.navigation.getByRole('button', { name: 'Previous passage' }));
      expect(last.onprevious).toHaveBeenCalledOnce();
   });

   it('announces the position in words with the passage’s label, its path while another file is open, and its lines, apart from the buttons', () => {
      const { navigation, spoken } = renderNavigation(1, cart);

      expect(spoken()).toBe('Passage 2 of 3, caller, src/pay.ts lines 30–31');
      expect(navigation.getByRole('status').querySelector('button')).toBeNull();
      expect(navigation.getByText('2/3').closest('[aria-hidden="true"]')).not.toBeNull();
      cleanup();

      expect(renderNavigation(0, cart).spoken()).toBe('Passage 1 of 3, lines 5–7');
   });

   it('says that a stop without passages has none', () => {
      expect(renderNavigation(-1, cart, []).text()).toBe('This stop has no passages.');
   });
});
