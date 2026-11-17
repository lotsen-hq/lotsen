import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { backend, startApp } from '../harness';
import { resetPageEachTest, worldOf } from './appPanels';
import { openPinnedTour, passage, planStop, seedStops, stopPanel, totals, type StopsSeed } from './stopProseWorld';
import { nextPassage } from './stopViewSupport';

resetPageEachTest();

const linked = backend().template('fileReveal-linked', (world) =>
   seedStops(world, [
      {
         title: 'Cart checks',
         kind: 'context',
         content: {
            headline: 'See [the caller](ref:head/src/pay.ts#L31-35) and [the type](ref:head/src/catalog.ts#L3-4).',
            passages: [passage('head', 'src/cart.ts', 5, 7), passage('head', 'src/pay.ts', 30, 31, 'caller')],
         },
      },
      totals,
   ]),
);

const link = (name: string) => stopPanel().getByRole('link', { name, hidden: true });
const modeSwitch = () => within(screen.getByRole('group', { name: 'View mode' }));

async function openLinkedStop(): Promise<LotsenApp> {
   const { world } = await worldOf<StopsSeed>(linked);
   const { app } = await startApp(world);
   await openPinnedTour(app);
   fireEvent.click(planStop('Cart checks'));
   await stopPanel().findByRole('heading', { name: 'Cart checks' });
   return app;
}

describe("the Files panel's reveal requests", () => {
   it('following a ref: link sends one reveal request for its file, and following the same link twice sends two', async () => {
      const app = await openLinkedStop();
      expect(app.revealRequest).toBeNull();

      fireEvent.click(link('the type'));
      expect(app.revealRequest).toEqual({ side: 'head', path: 'src/catalog.ts', sequence: 1 });

      fireEvent.click(link('the type'));
      expect(app.revealRequest).toEqual({ side: 'head', path: 'src/catalog.ts', sequence: 2 });
   });

   it("opening a thread's code anchor or file anchor sends one reveal request for its file", async () => {
      const app = await openLinkedStop();

      app.openAnchor({ kind: 'code', side: 'head', path: 'src/pay.ts', startLine: 30, endLine: 31, commit: app.selectedPinCommits!.head, text: 'pay line 30' });
      expect(app.revealRequest).toEqual({ side: 'head', path: 'src/pay.ts', sequence: 1 });

      app.openAnchor({ kind: 'file', side: 'base', path: 'src/catalog.ts', commit: app.selectedPinCommits!.base });
      expect(app.revealRequest).toEqual({ side: 'base', path: 'src/catalog.ts', sequence: 2 });
   });

   it('opening a stop, passage navigation and scrolling the stop-files stack send no reveal request', async () => {
      const app = await openLinkedStop();
      fireEvent.click(within(stopPanel().getByRole('list', { name: 'Code' })).getByRole('button', { name: /caller/ }));
      await waitFor(() => expect(app.selectedFilePath).toBe('src/pay.ts'));

      fireEvent.click(modeSwitch().getByRole('button', { name: 'Step through' }));
      nextPassage();
      fireEvent.click(planStop('Cart totals'));
      await stopPanel().findByRole('heading', { name: 'Cart totals' });
      fireEvent.click(planStop('Cart checks'));
      await stopPanel().findByRole('heading', { name: 'Cart checks' });

      fireEvent.click(modeSwitch().getByRole('button', { name: 'Stop files' }));
      await waitFor(() => expect(app.stopFiles?.length).toBe(2));
      const last = app.stopFiles!.at(-1)!;
      app.setStackTop(last.key);
      await waitFor(() => expect(app.stackFilePath).toBe(last.kind === 'diff' ? last.file.path : null));

      expect(app.revealRequest).toBeNull();
   });
});
