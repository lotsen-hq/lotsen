import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import { attachClaude, backend, cartContent, openStop, openTour, readTour, seedTour, startApp, type SeededTour } from '../harness';
import type { World } from '../backend/backend';
import { stopMenuButton } from './stopToolbar';
import { openThreadView } from './threadView';

const threadsPanel = () => within(screen.getByRole('region', { name: 'Threads' }));
const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

const cart = backend().template('worlds-cart', async (world) => {
   const seeded = await seedTour(world, {
      chapters: [
         {
            title: 'Cart',
            stops: [
               { title: 'Totals', kind: 'inspect', content: cartContent('The total rounds.') },
               { title: 'Later', kind: 'risk' },
            ],
         },
      ],
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds } satisfies Pick<SeededTour, 'tourId' | 'stopIds'>;
});

async function cartWorld(): Promise<{ world: World; tourId: string; stopIds: string[] }> {
   const world = await backend().world({ template: cart });
   const { tourId, stopIds } = world.seed as Pick<SeededTour, 'tourId' | 'stopIds'>;
   return { world, tourId, stopIds };
}

async function ask(words: string): Promise<void> {
   await fireEvent.input(threadsPanel().getByRole('textbox', { name: 'Question for Claude' }), { target: { value: words } });
   await fireEvent.click(threadsPanel().getByRole('button', { name: 'Send' }));
}

describe('the app on a real server', () => {
   it("sends a question that the bound session receives, and shows the session's reply in its thread", async () => {
      const { world, tourId } = await cartWorld();
      const claude = await attachClaude(world, tourId);
      const { app } = await startApp(world);
      await openTour(app);

      await ask('Why does the total round?');
      const question = await claude.next('question');
      expect(question).toMatchObject({ tourId, text: 'Why does the total round?' });
      await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Because **cents** add up.' });
      await openThreadView();

      expect(await threadsPanel().findByText('cents')).toBeTruthy();
      await waitFor(() => expect(threadsPanel().queryByText(/Waiting for Claude's reply/)).toBeNull());
   });

   it("shows the server's refusal of the stop's actions once the bound session ends, and offers them again when a session attaches", async () => {
      const { world, tourId, stopIds } = await cartWorld();
      const claude = await attachClaude(world, tourId);
      const { app } = await startApp(world);
      await openTour(app);
      await openStop(app, stopIds[0]!);
      await waitFor(() => expect(stopMenuButton('Ask Claude')?.hasAttribute('disabled')).toBe(false));

      await claude.drop();

      await waitFor(() => expect(stopMenuButton('Ask Claude')?.hasAttribute('disabled')).toBe(true));
      expect(stopPanel().getByText(/is attached to no Claude Code session/)).toBeTruthy();
      await attachClaude(world, tourId);
      await waitFor(() => expect(stopMenuButton('Ask Claude')?.hasAttribute('disabled')).toBe(false));
   });

   it("keeps a question that waits while the server's answer to the ask is held, and applies the answer once released", async () => {
      const { world, tourId } = await cartWorld();
      await attachClaude(world, tourId);
      const { app, transport } = await startApp(world);
      await openTour(app);
      const release = transport.hold('ask_question');
      const answered = transport.answered('ask_question');

      await ask('Held?');
      // The server stored the question, and its thread event reaches the page before the held answer.
      await waitFor(async () => expect((await readTour(world, tourId)).threads).toHaveLength(1));
      expect(await threadsPanel().findByText('Held?')).toBeTruthy();
      release();
      await answered;

      expect(threadsPanel().getAllByText('Held?')).toHaveLength(1);
   });
});
