import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import SettingsPanel from '../src/components/SettingsPanel.svelte';
import type { PruneOutcome } from '../src/lib/viewState';
import { Settings } from '../src/lib/settings.svelte';

async function prune(outcome: PruneOutcome) {
   render(SettingsPanel, { props: { settings: new Settings(window), onprune: () => Promise.resolve(outcome) } });
   await fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
   const settings = within(await screen.findByRole('dialog', { name: 'Settings' }));
   await fireEvent.click(settings.getByRole('button', { name: 'Prune unused clones' }));
   return settings;
}

describe('the prune of the clones', () => {
   it('names more than one clone in the plural', async () => {
      const settings = await prune({ removed: 2 });

      await waitFor(() => expect(settings.getByRole('status').textContent).toBe('Removed 2 clones.'));
   });

   it('shows the reason of a failed prune', async () => {
      const settings = await prune({ failure: 'only the UI can prune the clones, not the CLI.' });

      expect((await settings.findByRole('alert')).textContent).toBe('only the UI can prune the clones, not the CLI.');
   });
});
