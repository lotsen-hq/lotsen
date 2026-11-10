import { cleanup, fireEvent, waitFor } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { attachClaude, backend, openStop, readTour, seedTour, settled, startApp } from '../harness';
import { hoverTooltip } from '../tooltip';
import { planPanel, resetPageEachTest, worldOf } from './appPanels';
import { openPinnedTour } from './fourStopTour';

resetPageEachTest();

// Every line of the file changes, so the question stop's passage touches the diff.
const roundFile = (factor: number) => Array.from({ length: 24 }, (_, index) => `export const v${index + 1} = ${(index + 1) * factor};\n`).join('');

const twoStops = backend().template('planPanelRevised-twoStops', async (world) => {
   const seeded = await seedTour(world, {
      base: { 'src/round.ts': roundFile(1) },
      head: { 'src/round.ts': roundFile(2) },
      chapters: [
         {
            title: 'Checkout',
            stops: [
               { title: 'Rounding', kind: 'overview', content: { headline: 'The first version.' } },
               {
                  title: 'Taxes',
                  kind: 'question',
                  content: { headline: 'Another stop.', checks: [{ question: 'Keep it?' }], passages: [{ side: 'head', path: 'src/round.ts', startLine: 3, endLine: 4, role: 'primary' }] },
               },
            ],
         },
      ],
   });
   return { tourId: seeded.tourId, roundingId: seeded.stopIds[0]!, taxesId: seeded.stopIds[1]! };
});

const stopRow = (title: string) => planPanel().getByRole('button', { name: new RegExp(title) });
const marker = (title: string) => stopRow(title).querySelector('.status');
const markerText = (title: string) => marker(title)?.textContent.trim() ?? null;

async function revisable() {
   const { world, seed } = await worldOf(twoStops);
   const claude = await attachClaude(world, seed.tourId);
   let round = 1;
   const revise = async (stopId = seed.roundingId) => {
      await world.clock.advance(1000);
      round += 1;
      await claude.call('put_stop', { tourId: seed.tourId, stopId, headline: `Revision ${round}.` });
   };
   return { world, seed, revise };
}

const seenVersionOf = async (world: Awaited<ReturnType<typeof revisable>>['world'], tourId: string, stopId: string) => {
   const stop = (await readTour(world, tourId)).plan!.stops.find((candidate) => candidate.id === stopId)!;
   return stop.stage === 'written' ? stop.seenVersion : undefined;
};

describe('the Revised marker', () => {
   it('shows on no stop that the reviewer never opened, however often Claude revised it', async () => {
      const { world, revise } = await revisable();
      await revise();
      await revise();

      const { app } = await startApp(world);
      await openPinnedTour(app);

      await waitFor(() => expect(stopRow('Rounding')).toBeTruthy());
      expect(markerText('Rounding')).toBeNull();
   });

   it('appears on a stop the reviewer has seen once Claude revises it', async () => {
      const { world, seed, revise } = await revisable();
      const { app } = await startApp(world);
      await openPinnedTour(app);
      await openStop(app, seed.roundingId);
      await openStop(app, seed.taxesId);
      expect(markerText('Rounding')).toBeNull();

      await revise();

      await waitFor(() => expect(markerText('Rounding')).toBe('Revised'));
      expect(stopRow('Rounding').getAttribute('aria-label')).toContain('Revised');
      expect(markerText('Taxes')).toBeNull();
   });

   it('has a tooltip that says what the marker means', async () => {
      const { world, seed, revise } = await revisable();
      const { app } = await startApp(world);
      await openPinnedTour(app);
      await openStop(app, seed.roundingId);
      await openStop(app, seed.taxesId);
      await revise();
      await waitFor(() => expect(markerText('Rounding')).toBe('Revised'));

      expect(await hoverTooltip(marker('Rounding')!)).toBe('Changed since you last opened it');
   });

   it('clears when the reviewer opens the current version', async () => {
      const { world, seed, revise } = await revisable();
      const { app } = await startApp(world);
      await openPinnedTour(app);
      await openStop(app, seed.roundingId);
      await openStop(app, seed.taxesId);
      await revise();
      await waitFor(() => expect(markerText('Rounding')).toBe('Revised'));

      fireEvent.click(stopRow('Rounding'));

      await waitFor(() => expect(markerText('Rounding')).toBeNull());
      await waitFor(async () => expect(await seenVersionOf(world, seed.tourId, seed.roundingId)).toBe(2));
   });

   it('clears when the stop with the new version is already the last place that the server holds', async () => {
      const { world, seed, revise } = await revisable();
      await world.reviewer.call('record_reading_progress', { tourId: seed.tourId, stopId: seed.roundingId, version: 1 });
      await revise();

      const { app, transport } = await startApp(world);
      await openPinnedTour(app);

      await waitFor(() => expect(app.selectedStop?.id).toBe(seed.roundingId));
      await waitFor(() => expect(markerText('Rounding')).toBeNull());
      await waitFor(async () => expect(await seenVersionOf(world, seed.tourId, seed.roundingId)).toBe(2));
      expect(transport.requests.filter((request) => request.operation === 'record_reading_progress').map((request) => request.input)).toEqual([
         { tourId: seed.tourId, stopId: seed.roundingId, version: 2 },
      ]);
   });

   it('stays while the page holds the reviewer on the earlier version, and clears on moving to the new one', async () => {
      const { world, seed, revise } = await revisable();
      const { app } = await startApp(world);
      await openPinnedTour(app);
      await openStop(app, seed.roundingId);

      await revise();

      await waitFor(() => expect(app.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'revised' } }));
      await waitFor(() => expect(markerText('Rounding')).toBe('Revised'));
      expect(await seenVersionOf(world, seed.tourId, seed.roundingId)).toBe(1);

      app.showCurrentVersion();

      await waitFor(() => expect(app.shownVersion).toBeNull());
      await waitFor(() => expect(markerText('Rounding')).toBeNull());
      await waitFor(async () => expect(await seenVersionOf(world, seed.tourId, seed.roundingId)).toBe(2));
   });

   it('does not count an earlier version that the reviewer opens as the current one being seen', async () => {
      const { world, seed, revise } = await revisable();
      const { app } = await startApp(world);
      await openPinnedTour(app);
      await openStop(app, seed.roundingId);
      await revise();
      await revise();
      await waitFor(() => expect(app.shownVersion).toEqual({ number: 1, current: 3, reason: { kind: 'revised' } }));

      app.openVersion(2);

      await waitFor(() => expect(app.shownVersion?.number).toBe(2));
      await settled(app);
      expect(markerText('Rounding')).toBe('Revised');
      expect(await seenVersionOf(world, seed.tourId, seed.roundingId)).toBe(1);
   });

   it('stays after a reload of the page', async () => {
      const { world, seed, revise } = await revisable();
      const { app } = await startApp(world);
      await openPinnedTour(app);
      await openStop(app, seed.roundingId);
      await openStop(app, seed.taxesId);
      await revise();
      await waitFor(() => expect(markerText('Rounding')).toBe('Revised'));

      app.stop();
      cleanup();
      const reloaded = await startApp(world);
      await openPinnedTour(reloaded.app);

      await waitFor(() => expect(markerText('Rounding')).toBe('Revised'));
   });

   it('clears in another tab that has the tour open', async () => {
      const { world, seed, revise } = await revisable();
      const { app } = await startApp(world);
      await openPinnedTour(app);
      await openStop(app, seed.taxesId);
      await world.reviewer.call('record_reading_progress', { tourId: seed.tourId, stopId: seed.roundingId, version: 1 });
      await revise();
      await waitFor(() => expect(markerText('Rounding')).toBe('Revised'));

      await world.reviewer.call('record_reading_progress', { tourId: seed.tourId, stopId: seed.roundingId, version: 2 });

      await waitFor(() => expect(markerText('Rounding')).toBeNull());
   });
});
