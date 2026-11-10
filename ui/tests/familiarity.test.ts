import { cleanup, fireEvent, render, screen } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import FamiliarityPicker from '../src/components/FamiliarityPicker.svelte';
import type { Familiarity } from '../src/lib/types';

afterEach(() => cleanup());

describe('the familiarity picker', () => {
   it('presses the given level and reports a press of another level only', async () => {
      const picked: Familiarity[] = [];
      render(FamiliarityPicker, { props: { value: 'some', onchange: (level: Familiarity) => picked.push(level) } });

      await fireEvent.click(screen.getByRole('button', { name: 'Knows some of the code' }));
      await fireEvent.click(screen.getByRole('button', { name: 'New to the code' }));

      expect(screen.getByRole('button', { name: 'Knows some of the code' }).getAttribute('aria-pressed')).toBe('true');
      expect(screen.getByRole('button', { name: 'Familiar with the code' }).getAttribute('aria-pressed')).toBe('false');
      expect(picked).toEqual(['new']);
   });
});
