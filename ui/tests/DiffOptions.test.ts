import { fireEvent, render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import DiffOptions from '../src/components/DiffOptions.svelte';
import type { DiffLayout } from '../src/lib/diffLayout';

const diffProps = { layout: 'sideBySide', onlayout: () => {}, ignoreWhitespace: false, onignorewhitespace: () => {}, dim: true, ondim: () => {}, highlight: true, onhighlight: () => {} } as const;

describe('the diff options', () => {
   it.each<[DiffLayout, string, DiffLayout]>([
      ['sideBySide', 'true', 'inline'],
      ['inline', 'false', 'sideBySide'],
   ])('shows the %s layout as the side-by-side toggle pressed %s, and reports a click as a change to %s', async (layout, pressed, next) => {
      const onlayout = vi.fn();
      render(DiffOptions, { props: { ...diffProps, layout, onlayout } });
      const toggle = screen.getByRole('button', { name: 'Side by side' });
      expect(toggle.getAttribute('aria-pressed')).toBe(pressed);

      await fireEvent.click(toggle);

      expect(onlayout).toHaveBeenCalledWith(next);
   });

   it('shows the whitespace choice it is given and reports a change of it', async () => {
      const onignorewhitespace = vi.fn();
      render(DiffOptions, { props: { ...diffProps, ignoreWhitespace: true, onignorewhitespace } });
      const toggle = screen.getByRole('button', { name: 'Ignore whitespace' });
      expect(toggle.getAttribute('aria-pressed')).toBe('true');

      await fireEvent.click(toggle);

      expect(onignorewhitespace).toHaveBeenCalledWith(false);
   });

   it('offers the collapse toggle only when given a collapse state, and reports a change of it', async () => {
      const oncollapse = vi.fn();
      const view = render(DiffOptions, { props: { ...diffProps, collapse: false, oncollapse } });
      const toggle = screen.getByRole('button', { name: 'Collapse to passages' });
      expect(toggle.getAttribute('aria-pressed')).toBe('false');

      await fireEvent.click(toggle);

      expect(oncollapse).toHaveBeenCalledWith(true);
      await view.rerender({ collapse: null });
      expect(screen.queryByRole('button', { name: 'Collapse to passages' })).toBeNull();
   });

   it.each([true, false])('shows the dimming choice %s and reports a change of it', async (dim) => {
      const ondim = vi.fn();
      render(DiffOptions, { props: { ...diffProps, dim, ondim } });
      const toggle = screen.getByRole('button', { name: 'Dim outside the passages' });
      expect(toggle.getAttribute('aria-pressed')).toBe(String(dim));

      await fireEvent.click(toggle);

      expect(ondim).toHaveBeenCalledWith(!dim);
   });

   it.each([true, false])('shows the highlighting choice %s and reports a change of it', async (highlight) => {
      const onhighlight = vi.fn();
      render(DiffOptions, { props: { ...diffProps, highlight, onhighlight } });
      const toggle = screen.getByRole('button', { name: 'Highlight the passages' });
      expect(toggle.getAttribute('aria-pressed')).toBe(String(highlight));

      await fireEvent.click(toggle);

      expect(onhighlight).toHaveBeenCalledWith(!highlight);
   });

   it('offers only the decoration toggles for a file without a diff', () => {
      render(DiffOptions, { props: { dim: true, ondim: () => {}, highlight: true, onhighlight: () => {} } });

      expect(screen.getAllByRole('button').map((button) => button.getAttribute('aria-label'))).toEqual(['Highlight the passages', 'Dim outside the passages']);
   });
});
