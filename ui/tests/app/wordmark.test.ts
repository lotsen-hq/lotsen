import { screen, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { backend, startApp } from '../harness';
import { resetPageEachTest } from './appPanels';

resetPageEachTest();

describe('the top bar', () => {
   it('shows the Lotsen wordmark', async () => {
      await startApp(await backend().world());

      expect(within(screen.getByRole('banner')).getByRole('img', { name: 'Lotsen' })).not.toBeNull();
   });
});
