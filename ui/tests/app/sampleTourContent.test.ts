import { screen, waitFor } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { ChangedFileList } from '../../src/lib/contract.generated';
import { openStop, startApp } from '../harness';
import { resetPageEachTest } from './appPanels';
import { alerts, contentOf, sampleWorld, select, specs } from './sampleWorld';

resetPageEachTest();

describe('the sample tours in the UI', () => {
   const planned = specs.filter((spec) => spec.chapters !== undefined).map((spec) => spec.key);
   // A sweep reads every file of a tour through the server, at several git runs per file, so it may take longer than a test of one flow.
   const sweepTimeout = 60_000;

   it.each(planned)(
      'open every stop, every passage and every ref: link of %s',
      async (key) => {
         const { world } = await sampleWorld();
         const { app } = await startApp(world);
         await select(app, key);
         await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));

         for (const stop of app.selectedPlan!.plan!.stops) {
            await openStop(app, stop.id);
            expect(await screen.findByRole('heading', { name: stop.title })).toBeTruthy();
            for (const passage of contentOf(stop)?.passages ?? []) {
               app.openPassage(passage);
               await waitFor(() => expect(app.selectedText?.state ?? app.selectedDiff?.state, `${passage.side}:${passage.path}`).toBe('loaded'));
            }

            for (const anchor of screen.getByRole('region', { name: 'Stop' }).querySelectorAll<HTMLElement>('a[data-ref-path]')) {
               const { refSide, refPath, refStartLine } = anchor.dataset;
               anchor.click();
               await waitFor(() => expect(app.selectedText?.state ?? app.selectedDiff?.state, `${refSide}:${refPath}`).toBe('loaded'));
               expect(app.focus?.line, `${refSide}:${refPath}`).toBe(Number(refStartLine));
            }
         }

         expect(alerts()).toEqual([]);
      },
      sweepTimeout,
   );

   // The checkout tour pins the first head and every other pinned tour the pushed one, so these two cover every pin of the sample tours.
   const pinnedKeys = ['checkout-rework', 'search-filters'];

   it.each(pinnedKeys.flatMap((key) => [false, true].map((ignoreWhitespace) => [key, ignoreWhitespace] as const)))(
      'load the diff of every changed file of %s, ignoring whitespace: %s',
      async (key, ignoreWhitespace) => {
         const { world, tour } = await sampleWorld();
         const { files } = (await world.reviewer.call('list_changed_files', { tourId: tour(key).tourId })) as ChangedFileList;
         const { app } = await startApp(world);
         await select(app, key);
         await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));

         app.setIgnoreWhitespace(ignoreWhitespace);
         for (const file of files) {
            app.selectFile(file.path);
            await waitFor(() => expect(app.selectedDiff).toMatchObject({ path: file.path, state: 'loaded' }));
            if (file.large !== undefined) {
               app.loadLargeDiff();
               await waitFor(() => expect(app.selectedDiff).toMatchObject({ state: 'loaded', diff: { content: 'text' } }));
            }
         }

         expect(alerts()).toEqual([]);
      },
      sweepTimeout,
   );

   it.each(pinnedKeys.flatMap((key) => (['base', 'head'] as const).map((side) => [key, side] as const)))(
      'list every folder of %s at the %s and open every file there',
      async (key, side) => {
         const { world, tour } = await sampleWorld();
         const { app } = await startApp(world);
         await select(app, key);
         await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));

         const folders: (string | null)[] = [null];
         for (let folder = folders.shift(); folder !== undefined; folder = folders.shift()) {
            const known = await app.listDirectory(tour(key).tourId, side, folder);
            expect(known.state, `${side}:${folder}`).toBe('loaded');
            for (const entry of known.state === 'loaded' ? known.listing.entries : []) {
               if (entry.type === 'directory') {
                  folders.push(entry.path);
               } else if (entry.type !== 'submodule') {
                  app.openRepositoryFile(side, entry.path);
                  // A changed file opens its diff, and every other file its text.
                  await waitFor(() => {
                     const text = app.selectedText;
                     expect(text === null ? app.selectedDiff?.state : text.path === entry.path ? text.state : 'stale', `${side}:${entry.path}`).toBe('loaded');
                  });
               }
            }
         }

         expect(alerts()).toEqual([]);
      },
      sweepTimeout,
   );
});
