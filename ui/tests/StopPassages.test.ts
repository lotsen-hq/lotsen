import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import StopPassages from '../src/components/StopPassages.svelte';
import type { FileSide, StopPassage } from '../src/lib/types';
import { hoverTooltip } from './tooltip';

function stopPassage(side: FileSide, path: string, startLine: number, endLine: number, role: StopPassage['role'], label?: string): StopPassage {
   return { side, path, startLine, endLine, role, commit: 'c'.repeat(40), text: 'text', ...(label === undefined ? {} : { label }) };
}

const passages = [
   stopPassage('head', 'src/checkout/cart.ts', 5, 7, 'primary'),
   stopPassage('head', 'src/pay.ts', 30, 31, 'related', 'caller'),
   stopPassage('base', 'src/checkout/cart.ts', 20, 20, 'related', 'before'),
   stopPassage('head', 'src/catalog.ts', 3, 4, 'related'),
];

function renderPassages() {
   const onopenpassage = vi.fn();
   render(StopPassages, { props: { passages, onopenpassage } });
   const list = within(screen.getByRole('list', { name: 'Code' }));
   const rows = list.getAllByRole('button');
   const cells = (row: HTMLElement) => [...row.querySelectorAll('[data-cell]')].map((cell) => cell.textContent.trim());
   return { list, rows, cells, onopenpassage };
}

describe('the passage list of a stop', () => {
   it('shows one row per passage with its label, file name and lines, and names the row with its role, path and lines', () => {
      const { rows, cells } = renderPassages();

      expect(rows.map(cells)).toEqual([
         ['primary passage', 'cart.ts', '5–7'],
         ['caller', 'pay.ts', '30–31'],
         ['before', 'cart.ts', 'base 20'],
         ['related passage', 'catalog.ts', '3–4'],
      ]);
      expect(rows.map((row) => row.getAttribute('aria-label'))).toEqual([
         'primary passage, src/checkout/cart.ts, lines 5–7',
         'caller, related passage, src/pay.ts, lines 30–31',
         'before, related passage, src/checkout/cart.ts, line 20 at the base',
         'related passage, src/catalog.ts, lines 3–4',
      ]);
   });

   it('opens a row’s passage on a click', async () => {
      const { rows, onopenpassage } = renderPassages();

      await fireEvent.click(rows[1]!);

      expect(onopenpassage).toHaveBeenCalledExactlyOnceWith(passages[1]);
   });

   it('opens a row’s passage from the keyboard, as each row is a button in the tab order', async () => {
      const { rows, onopenpassage } = renderPassages();

      for (const row of rows) {
         expect(row.tagName).toBe('BUTTON');
         expect(row.tabIndex).toBe(0);
      }
      rows[2]!.focus();
      expect(document.activeElement).toBe(rows[2]);
      // A browser turns Enter and Space on a focused button into a click, which jsdom does not.
      await fireEvent.click(document.activeElement!);

      expect(onopenpassage).toHaveBeenCalledExactlyOnceWith(passages[2]);
   });

   it('shows the full path in the file name’s tooltip and the role in the role bar’s tooltip', async () => {
      const { rows } = renderPassages();

      expect(await hoverTooltip(within(rows[0]!).getByText('cart.ts'))).toBe('src/checkout/cart.ts');
      expect(await hoverTooltip(rows[0]!.querySelector('[data-role]')!)).toBe('Primary passage');
      expect(await hoverTooltip(rows[1]!.querySelector('[data-role]')!)).toBe('Related passage');
   });
});
