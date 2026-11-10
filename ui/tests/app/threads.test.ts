import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { World } from '../backend/backend';
import { attachClaude, backend, cartContent, readTour, seedTour, startApp, withHolds, openTour } from '../harness';
import { openThreadView, threadView, threadViewElement } from './threadView';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

// A negative check needs the page to have handled what the server sent, which no condition on the page shows.
const settled = () => new Promise((resolve) => setTimeout(resolve, 50));

interface Seed {
   tourId: string;
   threadId: string;
}

// The tour holds one thread whose question the seeding session answered.
const answeredThread = backend().template('threads-answered', async (world): Promise<Seed> => {
   const { tourId, claude } = await seedTour(world, { chapters: [{ title: 'Checkout', stops: [{ title: 'Cart totals', kind: 'inspect', content: cartContent('The total rounds.') }] }] });
   const asked = (await world.reviewer.call('ask_question', { tourId, text: 'Asked earlier', anchor: { kind: 'tour' } })) as { id: string };
   const question = await claude!.next('question');
   await claude!.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Answered earlier.' });
   return { tourId, threadId: asked.id };
});

const threadList = () => threadView();
const shownTexts = () => [...threadViewElement()!.querySelectorAll('[data-question], [data-reply], [data-waiting]')].map((element) => element.textContent?.trim());

async function followUp(text: string) {
   await fireEvent.input(threadList().getByRole('textbox', { name: 'Follow-up for Claude' }), { target: { value: text } });
   await fireEvent.click(threadList().getByRole('button', { name: 'Send the follow-up' }));
}

async function answeredWorld(): Promise<{ world: World } & Seed> {
   const world = await backend().world({ template: answeredThread });
   return { world, ...(world.seed as Seed) };
}

describe('a follow-up in the app', () => {
   it('is sent, shows as waiting in that thread, and the reply appears below it', async () => {
      const { world, tourId, threadId } = await answeredWorld();
      const claude = await attachClaude(world, tourId);
      const { app, transport } = await startApp(world);
      await openTour(app);
      await (await openThreadView()).findByText('Answered earlier.');

      await followUp('And the parser?');

      await threadList().findByText(/Waiting for Claude's reply/);
      expect(app.selectedThreads).toHaveLength(1);
      expect(transport.requests.find((request) => request.operation === 'ask_follow_up')?.input).toEqual({
         tourId,
         threadId,
         text: 'And the parser?',
         ...(app.selectedStopId === null ? {} : { stopId: app.selectedStopId }),
      });
      expect(shownTexts()).toEqual(['Asked earlier', 'Answered earlier.', 'And the parser?', expect.stringMatching(/Waiting for Claude's reply/)]);

      const question = await claude.next('question');
      await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'It is fine.' });

      await threadList().findByText('It is fine.');
      expect(shownTexts()).toEqual(['Asked earlier', 'Answered earlier.', 'And the parser?', 'It is fine.']);
   });

   it('lays out the messages of the thread view as the message style in the settings says', async () => {
      const { world } = await answeredWorld();
      const { app } = await startApp(world);
      await openTour(app);
      await openThreadView();
      const alignments = () =>
         [...threadViewElement()!.querySelectorAll<HTMLElement>('[data-writer]')].map((message) => (message.classList.contains('items-stretch') ? 'stretch' : message.dataset.writer));
      expect(alignments()).toEqual(['You', 'Claude']);
      expect(threadViewElement()!.querySelector('[data-writer="You"]')?.classList.contains('items-end')).toBe(true);

      await fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
      await fireEvent.click(within(await screen.findByRole('dialog', { name: 'Settings' })).getByRole('radio', { name: 'Full width' }));

      await waitFor(() => expect(alignments()).toEqual(['stretch', 'stretch']));
   });

   it('cancels and sends a waiting follow-up again', async () => {
      const { world, tourId, threadId } = await answeredWorld();
      await attachClaude(world, tourId);
      const { app } = await startApp(world);
      await openTour(app);
      await (await openThreadView()).findByText('Answered earlier.');
      await followUp('And the parser?');
      await threadList().findByText(/Waiting for Claude's reply/);

      await fireEvent.click(threadList().getByRole('button', { name: 'Cancel' }));
      await threadList().findByText(/Canceled\. Claude may still answer it\./);
      const resend = threadList().getByRole('button', { name: 'Send again' });
      await waitFor(() => expect(resend.getAttribute('aria-disabled')).toBe('false'));
      await fireEvent.click(resend);

      await threadList().findByText(/Waiting for Claude's reply/);
      const stored = (await readTour(world, tourId)).threads.find((thread) => thread.id === threadId)!;
      const question = stored.kind === 'note' ? undefined : stored.messages[2];
      expect(question?.kind === 'question' && question.requests.map((each) => each.state)).toEqual(['canceled', 'pending']);
   });

   it("shows the reply when it arrives before the follow-up's response", async () => {
      const { world, tourId } = await answeredWorld();
      const claude = await attachClaude(world, tourId);
      const { app, transport } = await startApp(world);
      await openTour(app);
      await (await openThreadView()).findByText('Answered earlier.');
      const release = transport.hold('ask_follow_up');
      const answered = transport.answered('ask_follow_up');

      await followUp('And the parser?');
      const question = await claude.next('question');
      await claude.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Answered first.' });
      await threadList().findByText('Answered first.');
      release();
      await answered;
      await settled();

      expect(shownTexts()).toEqual(['Asked earlier', 'Answered earlier.', 'And the parser?', 'Answered first.']);
   });

   it('keeps the follow-up over a reload that the server answered before the follow-up', async () => {
      const { world, tourId } = await answeredWorld();
      await attachClaude(world, tourId);
      const transport = withHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app);
      await (await openThreadView()).findByText('Answered earlier.');
      const reload = transport.holdAnswered('read_state');
      transport.cutEventStreams();
      await reload.answered;

      await followUp('And the parser?');
      await threadList().findByText(/Waiting for Claude's reply/);
      await reload.release();
      await settled();

      expect(shownTexts()).toEqual(['Asked earlier', 'Answered earlier.', 'And the parser?', expect.stringMatching(/Waiting for Claude's reply/)]);
   });
});

describe("a thread's delete in the app", () => {
   it("deletes the thread from its row's menu once the reviewer confirms, and tells Claude's session which thread it was", async () => {
      const { world, tourId, threadId } = await answeredWorld();
      const claude = await attachClaude(world, tourId);
      const { app } = await startApp(world);
      await openTour(app);
      const row = await waitFor(() => {
         const found = document.querySelector<HTMLElement>(`li[data-thread-id="${threadId}"]`);
         expect(found).not.toBeNull();
         return found!;
      });

      await fireEvent.keyDown(within(row).getByRole('button', { name: 'Thread actions' }), { key: 'Enter' });
      await fireEvent.click(within(await screen.findByRole('menu', { hidden: true })).getByRole('menuitem', { name: 'Delete thread…', hidden: true }));
      await fireEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete thread' }));

      expect(await claude.next('threadDeleted')).toEqual({ tourId, threadId });
      await waitFor(() => expect(document.querySelector(`li[data-thread-id="${threadId}"]`)).toBeNull());
      expect(app.selectedThreads).toEqual([]);
      expect((await readTour(world, tourId)).threads).toEqual([]);
   });
});
