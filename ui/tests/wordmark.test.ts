import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import Wordmark from '../src/components/Wordmark.svelte';

describe('the wordmark', () => {
   it('is one image named Lotsen', () => {
      render(Wordmark);

      expect(screen.getByRole('img', { name: 'Lotsen' })).not.toBeNull();
   });

   it('takes the text color of the theme, and keeps the blue accent in both themes', () => {
      const { container } = render(Wordmark);

      const fills = new Set([...container.querySelectorAll('path, polygon')].map((shape) => shape.getAttribute('fill')));

      expect(fills).toEqual(new Set(['currentColor', '#1A66F7']));
   });
});
