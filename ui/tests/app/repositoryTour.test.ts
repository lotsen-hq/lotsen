import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { backend, openStop, openTour, repositoryUrl, seedRepositoryTour, startApp, type RepositoryTourSpec } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { planPanel, resetPageEachTest } from './appPanels';
import { chooseFromPlanMenu, levelOption, planActions, sendButton } from './planMenu';

resetPageEachTest();

const title = 'How the cart adds up';
const passage = { side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 2, role: 'primary' };

// An orient tour at a tag, with a context stop that has gotchas and tryIt, and an overview stop without them.
const tagged: RepositoryTourSpec = {
   ref: 'v1.2.0',
   familiarity: 'familiar',
   chapters: [
      {
         title: 'Cart',
         stops: [
            { title: 'The cart', kind: 'overview', content: { headline: 'The cart sums its lines.', passages: [passage] } },
            {
               title: 'Rounding',
               kind: 'context',
               content: {
                  headline: 'Totals round once.',
                  how: 'The total rounds the sum.',
                  gotchas: 'A line total is *not* rounded.',
                  tryIt: 'Add two lines of `0.5` and read the total.',
                  passages: [passage],
               },
            },
         ],
      },
   ],
};

const taggedTour = backend().template('repositoryTour-tagged', async (world) => {
   const seeded = await seedRepositoryTour(world, { ...tagged, bound: false });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds };
});

async function openTagged(): Promise<{ app: LotsenApp; world: World; tourId: string; stopIds: string[] }> {
   const world = await backend().world({ template: taggedTour });
   const seed = world.seed as { tourId: string; stopIds: string[] };
   const { app } = await startApp(world);
   await openTour(app, title);
   return { app, world, ...seed };
}

const banner = () => within(screen.getByRole('banner'));
const headingPart = (name: string) => screen.getByRole('banner').querySelector<HTMLElement>(`[data-heading-part="${name}"]`);
const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));

async function showPlanPanel(): Promise<void> {
   await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   await screen.findByRole('tabpanel', { name: 'Plan' });
}

describe('a repository tour', () => {
   it('shows its title in the top bar, with the repository linked and the ref beside it, and no update check', async () => {
      await openTagged();

      expect(headingPart('title')?.textContent).toBe(title);
      expect(banner().getByRole('link', { name: 'shop' }).getAttribute('href')).toBe(repositoryUrl);
      expect(headingPart('ref')?.textContent).toBe('@v1.2.0');
      expect(banner().queryByRole('button', { name: 'Check the PR for a new update' })).toBeNull();
      expect(screen.queryByText(/iteration/i)).toBeNull();
   });

   it('leaves the ref out of the top bar for the default branch', async () => {
      const world = await backend().world();
      await seedRepositoryTour(world, { title: 'The default branch' });
      const { app } = await startApp(world);
      await openTour(app, 'The default branch');

      expect(headingPart('repository')?.textContent).toBe('shop');
      expect(headingPart('ref')).toBeNull();
   });

   it('lists its title, repository and ref, with its purpose, level and stops', async () => {
      await openTagged();

      const list = await openTourList();
      const row = list.getAllByRole('button', tourButton).find((button) => button.textContent?.includes(title))!;

      expect(row.textContent.replace(/\s+/g, ' ').trim()).toBe(`${title} Ready shop@v1.2.0 Orient · Familiar · 2 stops`);
   });

   it("shows its purpose and level in the Plan panel's line, and changes the level as a pull request tour does", async () => {
      const world = await backend().world({ template: taggedTour });
      const { tourId } = world.seed as { tourId: string };
      const claude = await world.claude();
      await claude.call('attach_tour', { tourId });
      const { app } = await startApp(world);
      await openTour(app, title);
      await showPlanPanel();

      expect(planActions().getByText('Orient · Familiar', { selector: 'p' })).toBeTruthy();
      await chooseFromPlanMenu('Re-plan…');
      await fireEvent.click(levelOption('New to the code'));
      await fireEvent.click(sendButton());

      await waitFor(() => expect(app.selectedFamiliarity).toBe('new'));
      await waitFor(() => expect(planActions().getByText('Orient · New', { selector: 'p' })).toBeTruthy());
      expect(await claude.next('question')).toEqual(expect.objectContaining({ decision: 'familiarityChanged' }));
   });

   it("shows a stop's gotchas and tryIt as sections of the Stop panel, as the server rendered them", async () => {
      const { app, stopIds } = await openTagged();

      await openStop(app, stopIds[1]!);

      expect(await stopPanel().findByRole('button', { name: 'Gotchas' })).toBeTruthy();
      expect(stopPanel().getByRole('button', { name: 'Try it' })).toBeTruthy();
      expect(stopPanel().getByText('not', { selector: 'em' }).parentElement?.textContent).toBe('A line total is not rounded.');
      expect(stopPanel().getByText('0.5', { selector: 'code' })).toBeTruthy();
   });

   it('leaves the sections out of a stop without gotchas or tryIt', async () => {
      const { app, stopIds } = await openTagged();

      await openStop(app, stopIds[0]!);

      await stopPanel().findByText('The cart sums its lines.');
      expect(stopPanel().queryByRole('button', { name: 'Gotchas' })).toBeNull();
      expect(stopPanel().queryByRole('button', { name: 'Try it' })).toBeNull();
   });

   it('shows its plan as a proposal for plan first, and Approve sends it', async () => {
      const world = await backend().world();
      const { stopIds } = await seedRepositoryTour(world, { planFirst: true, chapters: [{ title: 'Cart', stops: [{ title: 'The cart', kind: 'overview' }] }] });
      const { app, transport } = await startApp(world);
      await openTour(app, title);
      await showPlanPanel();
      const proposal = within(planPanel().getByRole('region', { name: 'Plan proposal' }));

      await fireEvent.click(proposal.getByRole('button', { name: 'Approve plan' }));

      await waitFor(() => expect(transport.requests.filter((request) => request.operation === 'approve_plan').map(({ input }) => input!.stopIds)).toEqual([stopIds]));
      await waitFor(() => expect(app.selectedPlanWaits).toBe(false));
   });
});
