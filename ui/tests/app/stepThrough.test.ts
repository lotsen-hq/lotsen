import { EditorView } from '@codemirror/view';
import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { DiffLayout } from '../../src/lib/diffLayout';
import type { Tour } from '../../src/lib/types';
import type { Template, Transport, World } from '../backend/backend';
import { attachClaude, backend, seedTour, startApp } from '../harness';
import { resetPageEachTest, worldOf } from './appPanels';
import {
   chooseMode,
   lineMarks,
   nextPassage,
   openPinnedTour,
   openStopIn,
   passage,
   passageNavigation,
   passagePosition,
   planStatus,
   planStop,
   previousPassage,
   reviseViewedStop,
   rounding,
   scattered,
   scatteredSpec,
   scrolledTo,
   spyOnEditorsEachTest,
   stopFiles,
   stopNavigation,
   stopPanel,
   totals,
   type SeededIds,
} from './stopViewSupport';

resetPageEachTest();
spyOnEditorsEachTest();

const seeded = (name: string, spec: Parameters<typeof seedTour>[1]) =>
   backend().template(name, async (world): Promise<SeededIds> => {
      const { tourId, stopIds } = await seedTour(world, spec);
      return { tourId, stopIds };
   });

const scatteredTour = seeded('stepThrough-scattered', scatteredSpec);
const bothSidesTour = seeded('stepThrough-bothSides', {
   ...stopFiles,
   chapters: [
      {
         title: 'Chapter',
         stops: [
            {
               title: 'Both sides',
               kind: 'overview',
               content: { headline: 'The catalog keeps its model.', passages: [passage('head', 'src/catalog.ts', 3, 4), passage('base', 'src/catalog.ts', 10, 11, 'old')] },
            },
         ],
      },
   ],
});
const withoutPassages = seeded('stepThrough-noPassages', {
   ...stopFiles,
   chapters: [{ title: 'Chapter', stops: [{ title: 'The change at a glance', kind: 'overview', content: { headline: 'An overview.' } }] }],
});

// Two related passages repeat the primary passage's lines, with the same label as each other, and Claude placed one before the primary.
const same = () => passage('head', 'src/cart.ts', 5, 7, 'same lines');
const [repeatedPrimary, repeatedCaller] = [passage('head', 'src/cart.ts', 5, 7), passage('head', 'src/pay.ts', 30, 31, 'caller')];
const repeated = { headline: 'The cart checks its quantities.', passages: [same(), repeatedPrimary, same(), repeatedCaller] };
const repeatedTour = seeded('stepThrough-repeated', {
   ...stopFiles,
   chapters: [
      {
         title: 'Chapter',
         stops: [
            { title: 'The change at a glance', kind: 'overview', content: repeated },
            { title: 'Cart totals', kind: 'inspect', content: totals },
         ],
      },
   ],
});
const sameLines = 'lines 5–7';

async function started(template: Template<SeededIds> = scatteredTour): Promise<{ world: World; seed: SeededIds; app: LotsenApp; transport: Transport }> {
   const { world, seed } = await worldOf(template);
   const { app, transport } = await startApp(world);
   return { world, seed, app, transport };
}

describe('stepping through a stop', () => {
   it.each<DiffLayout>(['sideBySide', 'inline'])('steps through the stop’s passages from the primary passage in Claude’s order and back, focusing one at a time, %s', async (layout) => {
      localStorage.setItem('lotsen.diffLayout', layout);
      const headEditor = layout === 'inline' ? 0 : 1;
      const baseLine20 = layout === 'inline' ? 'cart head line 20' : 'cart base line 20';
      const { app } = await started();
      await openStopIn(app, 'Step through');

      expect(await scrolledTo('Diff', 'src/cart.ts', 'head', 5)).toBe('cart line 5');
      expect(passagePosition()).toBe('1/4 lines 5–7');
      expect(passageNavigation().getByRole('button', { name: 'Previous passage' }).hasAttribute('disabled')).toBe(true);
      await waitFor(() => expect([4, 5, 7, 8, 20].map((line) => lineMarks('Diff', headEditor).get(line))).toEqual(['dimmed', 'primary focused', 'primary focused', 'dimmed', 'dimmed']));

      nextPassage();
      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
      expect(passagePosition()).toBe('2/4 lines 30–31');
      await waitFor(() => expect([29, 30, 31, 32].map((line) => lineMarks('Diff', headEditor).get(line))).toEqual(['dimmed', 'related focused', 'related focused', 'dimmed']));
      const next = passageNavigation().getByRole('button', { name: 'Next passage' });

      nextPassage();
      expect(await scrolledTo('File', 'src/catalog.ts', 'head', 3)).toBe('catalog line 3');
      // The toolbar outlives the change from the diff view to the file view, so the button keeps the reviewer's focus.
      expect(passageNavigation().getByRole('button', { name: 'Next passage' })).toBe(next);
      expect(passagePosition()).toBe('3/4 lines 3–4');
      await waitFor(() => expect([2, 3, 4, 5].map((line) => lineMarks('File').get(line))).toEqual(['dimmed', 'related focused', 'related focused', 'dimmed']));

      nextPassage();
      expect(await scrolledTo('Diff', 'src/cart.ts', 'base', 20)).toBe(baseLine20);
      expect(passagePosition()).toBe('4/4 line 20 at the base');
      expect(passageNavigation().getByRole('button', { name: 'Next passage' }).hasAttribute('disabled')).toBe(true);
      if (layout === 'sideBySide') {
         await waitFor(() => expect([5, 19, 20, 21].map((line) => lineMarks('Diff', 0).get(line))).toEqual(['dimmed', 'dimmed', 'related focused', 'dimmed']));
         expect(lineMarks('Diff', 1).get(5)).toBe('dimmed');
      }

      previousPassage();
      expect(await scrolledTo('File', 'src/catalog.ts', 'head', 3)).toBe('catalog line 3');
      previousPassage();
      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
      previousPassage();
      expect(await scrolledTo('Diff', 'src/cart.ts', 'head', 5)).toBe('cart line 5');
      expect(passagePosition()).toBe('1/4 lines 5–7');
   });

   it('focuses a passage of an unchanged file at either side of the file, whichever side the passage is at', async () => {
      const { app } = await started(bothSidesTour);
      await openPinnedTour(app);
      chooseMode('Step through');
      fireEvent.click(planStop('Both sides'));
      const catalogMarks = () => [2, 3, 4, 5, 9, 10, 11, 12].map((line) => lineMarks('File').get(line));
      const fileEditor = () => EditorView.findFromDOM(screen.getByRole('main', { name: 'File' }).querySelector<HTMLElement>('.cm-editor')!);
      /** Opens the file at a side from the whole-repository view, and waits for the editor of that side to replace the one before it. */
      async function openSide(side: 'base' | 'head') {
         const before = fileEditor();
         app.openRepositoryFile(side, 'src/catalog.ts');
         await waitFor(() => expect(app.selectedText).toMatchObject({ state: 'loaded', side }));
         await waitFor(() => expect(fileEditor()).not.toBe(before));
      }

      const primaryFocused = ['dimmed', 'primary focused', 'primary focused', 'dimmed', 'dimmed', 'dimmed', 'dimmed', 'dimmed'];
      const relatedFocused = ['dimmed', 'dimmed', 'dimmed', 'dimmed', 'dimmed', 'related focused', 'related focused', 'dimmed'];
      await scrolledTo('File', 'src/catalog.ts', 'head', 3);
      await waitFor(() => expect(catalogMarks()).toEqual(primaryFocused));
      await openSide('base');
      await waitFor(() => expect(catalogMarks()).toEqual(primaryFocused));

      nextPassage();
      await scrolledTo('File', 'src/catalog.ts', 'base', 10);
      await waitFor(() => expect(catalogMarks()).toEqual(relatedFocused));
      await openSide('head');
      await waitFor(() => expect(catalogMarks()).toEqual(relatedFocused));
   });

   it('leaves the next stop to the top bar at the last passage, and opens the next stop at its primary passage', async () => {
      const { app } = await started();
      await openStopIn(app, 'Step through', 'Cart totals');
      expect(passagePosition()).toBe('1/1 lines 20–22');
      expect(passageNavigation().getByRole('button', { name: 'Next passage' }).hasAttribute('disabled')).toBe(true);

      fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));

      expect(await scrolledTo('Diff', 'src/cart.ts', 'head', 5)).toBe('cart line 5');
      expect(passagePosition()).toBe('1/4 lines 5–7');
   });

   it('says that a stop without passages has none', async () => {
      const { app } = await started(withoutPassages);
      await openStopIn(app, 'Step through');

      expect(passageNavigation().getByRole('status').textContent?.trim()).toBe('This stop has no passages.');
      expect(passageNavigation().getByRole('button', { name: 'Next passage' }).hasAttribute('disabled')).toBe(true);
      expect(passageNavigation().getByRole('button', { name: 'Previous passage' }).hasAttribute('disabled')).toBe(true);
   });
});

describe('when the open stop changes while stepping through', () => {
   const [caller, primary, model, before] = scattered.passages;

   it('follows the focused passage to its new place when a revision reorders the passages', async () => {
      const { world, seed, app } = await started();
      const claude = await attachClaude(world, seed.tourId);
      await openStopIn(app, 'Step through');
      nextPassage();
      nextPassage();
      nextPassage();
      await scrolledTo('Diff', 'src/cart.ts', 'base', 20);

      await reviseViewedStop(claude, seed.tourId, seed.stopIds[0]!, { ...scattered, passages: [primary, before, caller] });

      await waitFor(() => expect(passagePosition()).toBe('2/3 line 20 at the base'));
      nextPassage();
      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
   });

   it('focuses the primary passage when a revision drops the focused passage, without moving the view', async () => {
      const { world, seed, app } = await started();
      const claude = await attachClaude(world, seed.tourId);
      await openStopIn(app, 'Step through');
      nextPassage();
      nextPassage();
      await scrolledTo('File', 'src/catalog.ts', 'head', 3);

      await reviseViewedStop(claude, seed.tourId, seed.stopIds[0]!, { ...scattered, passages: [caller, primary] });

      await waitFor(() => expect(passagePosition()).toBe('1/2 cart.ts lines 5–7'));
      expect(screen.getByRole('main', { name: 'File' })).toBeTruthy();
      nextPassage();
      expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
      expect(passagePosition()).toBe('2/2 lines 30–31');
   });

   it('shows no passage of a pending stop, and focuses the primary passage once its stopPut arrives during a plan load', async () => {
      const { world, seed, app, transport } = await started();
      const claude = await attachClaude(world, seed.tourId);
      await openStopIn(app, 'Step through', 'Rounding');
      expect(screen.queryByRole('navigation', { name: 'Passages' })).toBeNull();
      // A dropped event stream makes the page load the plan again once it reconnects, and the transport holds that load's answer.
      const reads = () => transport.requests.filter((request) => request.operation === 'read_state').length;
      const readsBefore = reads();
      const release = transport.hold('read_state');
      transport.cutEventStreams();
      await waitFor(() => expect(reads()).toBe(readsBefore + 1));
      const titles = ['The change at a glance', 'Cart totals', 'Rounding', 'The old form'];
      const kinds = ['overview', 'inspect', 'risk', 'question'];
      const planned = (await claude.call('set_plan', {
         tourId: seed.tourId,
         chapters: [{ id: 'c1', title: 'Chapter' }],
         stops: [...seed.stopIds.map((id, index) => ({ id, title: titles[index], kind: kinds[index], chapter: 'c1' })), { title: 'Added quietly', kind: 'inspect', chapter: 'c1' }],
      })) as Tour;
      await claude.call('put_stop', { tourId: seed.tourId, stopId: planned.plan!.stops[4]!.id, ...totals });

      await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds[2], ...rounding });
      expect(await scrolledTo('Diff', 'src/cart.ts', 'head', 20)).toBe('cart head line 20');
      release();

      await waitFor(() => expect(planStatus('Added quietly')).toBeNull());
      expect(planStatus('Rounding')).toBeNull();
      expect(passagePosition()).toBe('1/1 line 20');
   });

   it('keeps the focused passage focused when a revision changes its label', async () => {
      const { world, seed, app } = await started();
      const claude = await attachClaude(world, seed.tourId);
      await openStopIn(app, 'Step through');
      nextPassage();
      nextPassage();
      await scrolledTo('File', 'src/catalog.ts', 'head', 3);

      await reviseViewedStop(claude, seed.tourId, seed.stopIds[0]!, { ...scattered, passages: [caller, primary, { ...model!, label: 'the model type' }, before] });

      await waitFor(() => expect(within(stopPanel().getByRole('list', { name: 'Code' })).queryByRole('button', { name: /the model type/ })).not.toBeNull());
      await waitFor(() => expect(passagePosition()).toBe('3/4 lines 3–4'));
      expect([2, 3, 4, 5].map((line) => lineMarks('File').get(line))).toEqual(['dimmed', 'related focused', 'related focused', 'dimmed']);
   });

   it('keeps the focused passage focused when a revision makes it related and another passage primary', async () => {
      const { world, seed, app } = await started();
      const claude = await attachClaude(world, seed.tourId);
      await openStopIn(app, 'Step through');
      await scrolledTo('Diff', 'src/cart.ts', 'head', 5);

      await reviseViewedStop(claude, seed.tourId, seed.stopIds[0]!, { ...scattered, passages: [{ ...caller!, role: 'primary' }, { ...primary!, role: 'related' }, model, before] });

      await waitFor(() => expect(passagePosition()).toBe('2/4 lines 5–7'));
      await waitFor(() => expect([4, 5, 7, 8].map((line) => lineMarks('Diff', 1).get(line))).toEqual(['dimmed', 'related focused', 'related focused', 'dimmed']));
   });

   describe('with passages over the same lines', () => {
      it('visits each of them in turn with Next passage, and back with Previous passage', async () => {
         const { app } = await started(repeatedTour);
         await openStopIn(app, 'Step through');
         expect(passagePosition()).toBe('1/4 lines 5–7');

         nextPassage();
         await waitFor(() => expect(passagePosition()).toBe(`2/4 ${sameLines}`));
         nextPassage();
         await waitFor(() => expect(passagePosition()).toBe(`3/4 ${sameLines}`));
         nextPassage();
         expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
         expect(passagePosition()).toBe('4/4 lines 30–31');

         previousPassage();
         await waitFor(() => expect(passagePosition()).toBe(`3/4 ${sameLines}`));
         previousPassage();
         await waitFor(() => expect(passagePosition()).toBe(`2/4 ${sameLines}`));
         previousPassage();
         await waitFor(() => expect(passagePosition()).toBe('1/4 lines 5–7'));
      });

      it('focuses the one the reviewer opens from the stop', async () => {
         const { app } = await started(repeatedTour);
         await openStopIn(app, 'Step through');
         const code = () => within(stopPanel().getByRole('list', { name: 'Code' })).getAllByRole('button');

         fireEvent.click(code()[2]!);
         await waitFor(() => expect(passagePosition()).toBe(`3/4 ${sameLines}`));

         fireEvent.click(code()[0]!);
         await waitFor(() => expect(passagePosition()).toBe(`2/4 ${sameLines}`));
      });

      it('keeps the focused one when a revision reorders them, and the one left when a revision drops the other', async () => {
         const { world, seed, app } = await started(repeatedTour);
         const claude = await attachClaude(world, seed.tourId);
         await openStopIn(app, 'Step through');
         nextPassage();
         nextPassage();
         await waitFor(() => expect(passagePosition()).toBe(`3/4 ${sameLines}`));

         await reviseViewedStop(claude, seed.tourId, seed.stopIds[0]!, { ...repeated, passages: [repeatedCaller, repeatedPrimary, same(), same()] });
         await waitFor(() => expect(passagePosition()).toBe(`4/4 ${sameLines}`));

         await reviseViewedStop(claude, seed.tourId, seed.stopIds[0]!, { ...repeated, passages: [repeatedPrimary, same(), repeatedCaller] });
         await waitFor(() => expect(passagePosition()).toBe(`2/3 ${sameLines}`));
         nextPassage();
         expect(await scrolledTo('Diff', 'src/pay.ts', 'head', 30)).toBe('pay line 30');
      });
   });
});
