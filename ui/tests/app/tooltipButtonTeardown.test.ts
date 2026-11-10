import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Tour } from '../../src/lib/types';
import { backend, openTour, prLabel, prUrl, startApp } from '../harness';

vi.mock('../../src/components/DiffHeading.svelte', async () => await import('../fixtures/TooltipButtonDiffHeading.svelte'));

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   history.replaceState(null, '', '/');
});

// The first tour's plan marks its one changed file as generated. The second tour pins a pull request whose source is its target, so it changes no files.
const twoTours = backend().template('tooltipButtonTeardown-two', async (world) => {
   const base = await world.repo.commit('main', 'The base', { 'a.ts': 'a\n' });
   const head = await world.repo.commit('feature', 'The change', { 'a.ts': 'b\n' }, base);
   await world.pullRequest.set(head, base);
   const first = await world.claude();
   const tour = (await first.call('create_tour', { prUrl, familiarity: 'some' })) as Tour;
   await world.idle();
   await first.call('set_plan', {
      tourId: tour.id,
      chapters: [{ id: 'c1', title: 'Chapter' }],
      stops: [{ title: 'A stop', kind: 'inspect', chapter: 'c1' }],
      fileMarks: [{ path: 'a.ts', mark: 'generated', reason: 'A tool writes it.' }],
   });
   await world.pullRequest.set(base, base);
   const second = await world.claude();
   await second.call('create_tour', { prUrl: 'https://platform.test/shop/pull/43', familiarity: 'some' });
   await world.idle();
});

describe('a TooltipButton whose disabled state changes in the update that unmounts it', () => {
   it('leaves the page updating when the reviewer switches tours with a marked file open', async () => {
      const world = await backend().world({ template: twoTours });
      const { app } = await startApp(world);
      await openTour(app, prLabel);
      const files = async () => within(await screen.findByRole('tabpanel', { name: 'Files' }));
      fireEvent.click(await (await files()).findByText('a.ts'));
      await waitFor(() => expect(screen.getByRole('main', { name: 'Diff' }).textContent).toContain('generated'));

      await openTour(app, 'shop #43');

      expect(await (await files()).findByText(/changes no files/)).toBeTruthy();
   });
});
