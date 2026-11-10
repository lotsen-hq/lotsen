import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { cartLineRevision, explainedTotals, oldFormAnswer, pinningTour } from '../../demo/sampleTours';
import { putStopInput, seedPinningTour, writeStop, type LiveSamples } from '../../demo/seed';
import type { QuestionThread } from '../../src/lib/contract.generated';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { SessionEventPayload } from '../backend/backend';
import { openStop, readTour, startApp } from '../harness';
import { planPanel, resetPageEachTest } from './appPanels';
import { chooseFromPlanMenu, isSelected, levelOption, sendButton, sendLevelChange } from './planMenu';
import { alerts, contentOf, questionsOf, sampleWorld, select, showTab, specs, threadsPanel } from './sampleWorld';
import { backToList, openThreadView, threadViewElement } from './threadView';

resetPageEachTest();

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));

// The live steps leave questions in the session's inbox, so a test that waits for its own question passes over those.
async function nextQuestion(claude: LiveSamples['claude'], matches: (question: SessionEventPayload) => boolean): Promise<SessionEventPayload> {
   for (;;) {
      const question = await claude.next('question');
      if (matches(question)) {
         return question;
      }
   }
}

/** Returns the ID of the selected tour's thread that holds the question. */
function threadOfQuestion(app: LotsenApp, questionId: string): string {
   return app.selectedThreads.find((thread) => thread.kind !== 'note' && thread.messages.some((message) => message.id === questionId))!.id;
}

describe('Claude and the platform on the sample tours', () => {
   it('create a tour that lists first while its pin waits for the platform, and shows its changed files once the platform answers', async () => {
      const { world } = await sampleWorld();
      const { app } = await startApp(world);
      const pinning = await seedPinningTour(world, await world.claude());
      await waitFor(() => expect(app.tours[0]?.id).toBe(pinning.tourId));
      await select(app, pinningTour.key);

      await world.pullRequest.release();

      await waitFor(() => expect(app.selectedFiles?.state).toBe('loaded'));
   });

   it('fail a pin, after which the tour shows why', async () => {
      const { world } = await sampleWorld();
      const { app } = await startApp(world);
      await seedPinningTour(world, await world.claude());
      await select(app, pinningTour.key);
      await showTab('Files');

      await world.pullRequest.fail('The platform refused the access token. Sign in to the platform again, then create the tour again.');
      await world.pullRequest.release();

      await waitFor(() => expect(alerts()).toEqual([expect.stringContaining('could not be pinned')]));
   });

   it('set a plan on the pinned tour without one', async () => {
      const { world, tour, unconfirmed } = await sampleWorld();
      const { app } = await startApp(world);
      await select(app, 'search-filters');

      await unconfirmed.call('set_plan', {
         tourId: tour('search-filters').tourId,
         chapters: [{ id: 'c1', title: 'The filters' }],
         stops: [{ title: 'What the filters add', kind: 'overview', chapter: 'c1' }],
      });

      await waitFor(() => expect(app.selectedPlan?.plan?.stops.length).toBe(1));
   });

   it('write a pending stop, after which the stop is ready with its content', async () => {
      const { world, tour, claude } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      const stopId = checkout.stops['oldForm']!;
      expect(app.selectedPlan?.plan?.stops.find((stop) => stop.id === stopId)?.status).toBe('pending');

      const later = specs
         .find((spec) => spec.key === 'checkout-rework')!
         .chapters!.flatMap((chapter) => chapter.stops)
         .find((stop) => stop.key === 'oldForm')!.later!;
      await writeStop(claude, checkout, 'oldForm', later);

      await waitFor(() => expect(app.selectedPlan?.plan?.stops.find((stop) => stop.id === stopId)).toMatchObject({ status: 'ready', content: { headline: later.headline } }));
   });

   it('revise a stop, after which its answers to dropped checks are outdated and the thread on its changed text is too', async () => {
      const { world, tour, claude } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');

      await writeStop(claude, checkout, 'cartLine', cartLineRevision);

      await waitFor(() => expect(app.selectedPlan?.plan?.stops.find((stop) => stop.id === checkout.stops['cartLine'])).toMatchObject({ status: 'revised', version: 2 }));
      const latest = await readTour(world, checkout.tourId);
      const answers = latest.answers.filter((answer) => answer.stopId === checkout.stops['cartLine']);
      expect(answers.map((answer) => [answer.checkId, answer.outdatedOn])).toEqual([
         [checkout.checks['zero-quantity'], undefined],
         [checkout.checks['cart-limit'], 1],
         [checkout.checks['line-merge'], 1],
      ]);
      expect(latest.threads.find((thread) => thread.id === checkout.threads['refused-add'])?.state).toBe('outdated');
   });

   it('carry out the waiting tour action on the stop under view, which shows the new version at once and stops showing the action as waiting', async () => {
      const { world, tour, claude } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      await openStop(app, checkout.stops['totals']!);
      await stopPanel().findByText('Claude is explaining this stop deeper.');

      await writeStop(claude, checkout, 'totals', explainedTotals);
      await claude.call('reply', { tourId: checkout.tourId, questionId: checkout.questions['explain-totals'], resolution: 'done', text: 'Expanded *how* with the rounding rule.' });

      await waitFor(() => expect(app.currentStop?.stage === 'written' ? app.currentStop.version : null).toBe(2));
      expect(app.shownVersion).toBeNull();
      await waitFor(() => expect(stopPanel().queryByText('Claude is explaining this stop deeper.')).toBeNull());
      expect(alerts()).toEqual([]);
   });

   it('update the checkout tour, after which its stale stops show their marker and a thread stays on the earlier commit, then carry out the update while the reviewer reads a stale stop', async () => {
      const { world, tour, claude, seed } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      await openStop(app, checkout.stops['payment']!);
      await screen.findByRole('status', { name: 'New update' });

      await fireEvent.click(screen.getByRole('button', { name: /^Move to update \d+$/ }));

      await waitFor(() => expect(screen.getByRole('status', { name: 'Stale stop' }).textContent).toMatch(/Claude is revising this stop for update \d+\./));
      expect(app.fileDecorations.passages).toEqual([]);
      // The update's response and its threadUpdated event reach the page in either order, so the thread can still be open when the marker shows.
      await waitFor(() => expect(app.selectedThreads.find((thread) => thread.id === checkout.threads['negative-total'])?.state).toBe('outdated'));
      const left = app.selectedThreads.find((thread) => thread.id === checkout.threads['negative-total'])!;
      expect(left.anchor).toMatchObject({ commit: seed.heads[0] });
      expect(app.selectedThreadsAtPin.some((thread) => thread.id === checkout.threads['negative-total'])).toBe(false);
      expect(app.selectedThreadsAtPin.some((thread) => thread.id === checkout.threads['rounding'])).toBe(true);

      const decision = await nextQuestion(claude, (question) => question.decision === 'iterationUpdated');
      const stop = (await readTour(world, checkout.tourId)).plan!.stops.find((candidate) => candidate.id === checkout.stops['payment'])!;
      await claude.call('put_stop', { ...putStopInput(checkout, 'payment', { headline: contentOf(stop)!.headline, passages: [['head', 'src/checkout/payment.ts', 11, 23]] }) });
      await claude.call('reply', { tourId: checkout.tourId, questionId: decision.questionId, resolution: 'done', text: 'Revised the stale stops.' });

      await waitFor(() => expect(app.currentStop?.stage === 'written' ? app.currentStop.version : null).toBe(3));
      expect(app.shownVersion).toMatchObject({ number: 2, reason: { kind: 'iterationUpdate' } });
      expect(alerts()).toEqual([]);
   });

   it('carry out a reorder that the reviewer sends from the Plan panel, which waits until the plan arrives in the new order', async () => {
      const { world, tour, claude } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      await showTab('Plan');

      await chooseFromPlanMenu('Reorder…');
      await fireEvent.click(planPanel().getByRole('button', { name: 'Move “Gateway errors reach the customer as they are” up' }));
      await fireEvent.click(planPanel().getByRole('button', { name: 'Send to Claude' }));
      await planPanel().findByText('Claude is reordering the plan.');
      const request = await nextQuestion(claude, (question) => question.action === 'reorder');
      const latest = await readTour(world, checkout.tourId);
      const order = ['overview', 'totals', 'gateway', 'payment', 'cartLine', 'oldForm', 'productData'].map((key) => checkout.stops[key]!);
      await claude.call('set_plan', {
         tourId: checkout.tourId,
         chapters: latest.plan!.chapters,
         stops: order.map((id) => {
            const stop = latest.plan!.stops.find((candidate) => candidate.id === id)!;
            return { id, title: stop.title, kind: stop.kind, chapter: stop.chapter };
         }),
         fileMarks: latest.plan!.fileMarks,
      });
      await claude.call('reply', { tourId: checkout.tourId, questionId: request.questionId, resolution: 'done', text: 'Put the stops in your order.' });

      await waitFor(() => expect(app.selectedPlan?.plan?.stops.map((stop) => stop.id)).toEqual(order));
      await waitFor(() => expect(planPanel().queryByText('Claude is reordering the plan.')).toBeNull());
      expect(alerts()).toEqual([]);
   });

   it("carry out the approval of the waiting plan, after which Claude wrote its stops in the reviewer's order", async () => {
      const { world, tour, claude } = await sampleWorld();
      const wishlist = tour('wishlist');
      const { app } = await startApp(world);
      await select(app, 'wishlist');
      await showTab('Plan');
      const proposal = () => within(planPanel().getByRole('region', { name: 'Plan proposal' }));

      await fireEvent.click(proposal().getByRole('button', { name: 'Move “Building the query string” up' }));
      await fireEvent.click(proposal().getByRole('button', { name: 'Approve plan' }));
      await planPanel().findByText('Claude is writing the stops of the plan you approved. You can change the level once Claude replies.');
      const approval = await nextQuestion(claude, (question) => question.decision === 'planApproved');
      const latest = await readTour(world, wishlist.tourId);
      const order = ['query', 'what', 'empty', 'price'].map((key) => wishlist.stops[key]!);
      await claude.call('set_plan', {
         tourId: wishlist.tourId,
         chapters: latest.plan!.chapters,
         stops: order.map((id) => {
            const stop = latest.plan!.stops.find((candidate) => candidate.id === id)!;
            return { id, title: stop.title, kind: id === wishlist.stops['what'] ? 'context' : stop.kind, chapter: stop.chapter };
         }),
      });
      for (const stop of specs.find((spec) => spec.key === 'wishlist')!.chapters!.flatMap((chapter) => chapter.stops)) {
         await writeStop(claude, wishlist, stop.key, { ...stop.later!, passages: stop.later!.passages ?? [['head', 'src/search/filters.ts', 1, 5]] });
      }

      await claude.call('reply', { tourId: wishlist.tourId, questionId: approval.questionId, resolution: 'done', text: 'Wrote the stops in your order.' });

      await waitFor(() => expect(app.selectedPlan?.plan?.stops.map((stop) => stop.id)).toEqual(order));
      await waitFor(() => expect(app.selectedPlan?.plan?.stops.every((stop) => stop.status === 'ready')).toBe(true));
      await waitFor(() => expect(planPanel().queryByText('Claude is writing the stops of the plan you approved. You can change the level once Claude replies.')).toBeNull());
      expect(alerts()).toEqual([]);
   });

   it('decline a change of the familiarity level, after which Send at that level asks Claude again', async () => {
      const { world, tour, claude } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      await showTab('Plan');
      const rewriting = 'Claude is rewriting the stops you have not read for the new familiarity level.';

      await sendLevelChange('New to the code');
      await planPanel().findByText(rewriting);
      const change = await nextQuestion(claude, (question) => question.decision === 'familiarityChanged');
      await claude.call('reply', { tourId: checkout.tourId, questionId: change.questionId, resolution: 'declined' });

      await planPanel().findByText(/Claude declined the last change to this level\. To ask Claude again, open Re-plan…/);
      await chooseFromPlanMenu('Re-plan…');
      expect(isSelected(levelOption('New to the code, ask Claude again now'))).toBe(true);
      await fireEvent.click(sendButton());
      await planPanel().findByText(rewriting);
      expect(await nextQuestion(claude, (question) => question.decision === 'familiarityChanged')).toBeTruthy();
      expect(alerts()).toEqual([]);
   });

   it('send the answers from the top bar, after which Claude fetches them and they show as received', async () => {
      const { world, tour, claude } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      await openStop(app, checkout.stops['cartLine']!);
      const states = () =>
         stopPanel()
            .queryAllByText(/./, { selector: '[data-answer-state]' })
            .map((state) => state.textContent);
      await waitFor(() => expect(states()).toEqual(['Received', 'Not received', 'Not received']));
      expect(document.querySelector('[data-unsent-answers]')?.textContent).toBe('3 unsent answers');

      await fireEvent.click(within(screen.getAllByRole('banner')[0]!).getByRole('button', { name: 'Send answers' }));

      await waitFor(() => expect(states()).toEqual(['Received', 'Sent', 'Sent']));
      expect(document.querySelector('[data-unsent-answers]')).toBeNull();
      const batch = await claude.next('answers');
      await claude.call('fetch_answers', { tourId: checkout.tourId, batchId: batch.batchId });
      await waitFor(() => expect(states()).toEqual(['Received', 'Received', 'Received']));
   });

   it('answer the received question, after which its thread shows the answer', async () => {
      const { world, tour, claude } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      const view = await openThreadView(threadOfQuestion(app, checkout.questions['old-form']!));
      expect(await view.findByText(/Claude may ask in the terminal/)).toBeTruthy();

      await claude.call('reply', { tourId: checkout.tourId, questionId: checkout.questions['old-form'], resolution: 'done', text: oldFormAnswer });

      expect(await view.findByRole('link', { name: 'the form' })).toBeTruthy();
      await waitFor(() => expect(view.queryByText(/Claude may ask in the terminal/)).toBeNull());
   });

   it('let Claude fetch a waiting question, after which its thread shows it received', async () => {
      const { world, tour, claude } = await sampleWorld();
      const checkout = tour('checkout-rework');
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      await openThreadView(threadOfQuestion(app, checkout.questions['currency']!));
      // The question waits as the thread's last question, so the last message's line names its state.
      const lastState = () => [...threadViewElement()!.querySelectorAll('[data-delivery]')].at(-1)?.textContent;
      expect(lastState()).toBe('Sent');

      await claude.call('fetch_question', { tourId: checkout.tourId, questionId: checkout.questions['currency'] });

      await waitFor(() => expect(lastState()).toBe('Received'));
   });

   it('let Claude start a thread, add a message to it, outdate it and delete it, each shown live', async () => {
      const { world, tour, claude } = await sampleWorld();
      const tourId = tour('checkout-rework').tourId;
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      const before = app.selectedThreads.length;

      const { thread } = (await claude.call('start_thread', { tourId, anchor: { kind: 'tour' }, text: 'A test of a coupon on a rounded price would pin the order.' })) as { thread: QuestionThread };
      expect(await threadsPanel().findByText(/A test of a coupon on a rounded price/)).toBeTruthy();
      await claude.call('add_thread_message', { tourId, threadId: thread.id, text: 'Updated: the revised stop now covers this.' });
      expect(await threadsPanel().findByText('Claude: Updated: the revised stop now covers this.')).toBeTruthy();
      await claude.call('outdate_thread', { tourId, threadId: thread.id });
      await waitFor(() => expect(app.selectedThreads.find((candidate) => candidate.id === thread.id)?.state).toBe('outdated'));
      expect(threadsPanel().queryByText(/A test of a coupon on a rounded price/)).toBeNull();
      await claude.call('delete_thread', { tourId, threadId: thread.id });

      await waitFor(() => expect(app.selectedThreads).toHaveLength(before));
      expect(alerts()).toEqual([]);
   });

   it("send the checkout tour's two notes as a batch from the Threads panel", async () => {
      const { world } = await sampleWorld();
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      const drafts = app.selectedThreads.filter((thread) => thread.kind === 'note');
      expect(drafts).toHaveLength(2);

      for (const draft of drafts) {
         await fireEvent.click((await openThreadView(draft.id)).getByRole('checkbox', { name: 'Send with other notes' }));
         await backToList();
      }

      await waitFor(() => expect((threadsPanel().getByRole('button', { name: 'Send chosen notes' }) as HTMLButtonElement).disabled).toBe(false));
      await fireEvent.click(threadsPanel().getByRole('button', { name: 'Send chosen notes' }));

      await waitFor(() => expect(app.selectedThreads.filter((thread) => thread.kind === 'note')).toEqual([]));
   });

   it('answer a canceled question late, after which its thread shows the answer', async () => {
      const { world, tour, claude } = await sampleWorld();
      const tourId = tour('checkout-rework').tourId;
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      const asked = await world.reviewer.call('ask_question', { tourId, text: 'Do coupons stack?', anchor: { kind: 'tour' } });
      const questionId = questionsOf(asked)[0]!.id;
      await world.reviewer.call('cancel_question', { tourId, questionId });
      const view = await openThreadView((asked as QuestionThread).id);
      expect(await view.findByText(/Canceled\. Claude may still answer it\./)).toBeTruthy();

      await claude.call('reply', { tourId, questionId, resolution: 'done', text: 'They do not stack.' });

      await waitFor(() => expect(view.queryByText(/Canceled\. Claude may still answer it\./)).toBeNull());
      expect(view.getByText('They do not stack.')).toBeTruthy();
   });

   it('end the session of the tour with a waiting question, then attach an unconfirmed one to another tour and confirm it', async () => {
      const { world, tour, claude } = await sampleWorld();
      const { app } = await startApp(world);
      await select(app, 'checkout-rework');
      const bar = screen.getByRole('banner');
      const view = await openThreadView(app.selectedThreads.find((thread) => thread.status.waiting !== undefined)!.id);
      expect(view.getByText(/Claude may ask in the terminal/)).toBeTruthy();

      await claude.drop();

      await waitFor(() => expect(bar.textContent).toContain('No session'));
      await waitFor(() => expect(view.queryByText(/Claude may ask in the terminal/)).toBeNull());
      expect(view.getByRole('button', { name: 'Send again' })).toBeTruthy();
      await select(app, 'readme-only');
      const unconfirmed = await world.claude({ confirm: false });
      await unconfirmed.call('attach_tour', { tourId: tour('readme-only').tourId });
      await waitFor(() => expect(bar.textContent).toContain('Unconfirmed'));
      const probe = await unconfirmed.next('probe');
      await unconfirmed.call('ack', { nonce: probe.nonce });
      await waitFor(() => expect(bar.textContent).toContain('Confirmed'));
   });

   it('drop the event stream, after which the page reconnects', async () => {
      const { world } = await sampleWorld();
      const { transport } = await startApp(world);
      const streams = () => transport.requests.filter((request) => request.path === '/api/events').length;
      expect(streams()).toBe(1);

      transport.cutEventStreams();

      await waitFor(() => expect(streams()).toBe(2));
   });
});
