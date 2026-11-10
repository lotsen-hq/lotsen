import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import FilesPanel from '../src/components/FilesPanel.svelte';
import type { KnownFiles } from '../src/lib/tourRecord.svelte';
import type { TourSummary } from '../src/lib/types';
import { outputFixtures } from './contract/fixtures.generated';

const tour = outputFixtures.list_tours.tours[0] as TourSummary;

function show(known: KnownFiles): HTMLElement {
   render(FilesPanel, { props: { tour, known, selectedPath: null, onselect: () => {} } });
   return screen.getByRole('alert');
}

// A pinned tour stays pinned on the server, and the server keeps a repository that it can always read in the flow tests, so these refusals of the list have no real cause there.
describe('the changed files of a pinned tour that the server refuses to list', () => {
   it('shows the pin that the server gives when it refuses the list because the tour is not pinned', () => {
      const alert = show({ state: 'notPinned', pin: { state: 'failed', reason: 'Pin it again.' } });

      expect(alert.textContent).toContain('could not be pinned');
      expect(alert.textContent).toContain('Pin it again.');
   });

   it('tells the user why the changed files could not be loaded when the repository is unavailable', () => {
      const alert = show({ state: 'failed', message: 'The changed files could not be loaded.', reason: 'Git could not reach the repository.' });

      expect(alert.textContent).toContain('The changed files could not be loaded.');
      expect(alert.textContent).toContain('Git could not reach the repository.');
   });
});
