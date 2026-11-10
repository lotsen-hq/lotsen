import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import { actionOf, messagesOf } from '../../src/lib/threadView';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { QuestionThread, ReviewSummary } from '../../src/lib/types';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { attachClaude, backend, cartContent, cartHead, openTour, readTour, seedTour, startApp, updateIteration } from '../harness';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

interface Seed {
   tourId: string;
   base: string;
   head: string;
}

const withPlan = backend().template('summarize-plan', async (world): Promise<Seed> => {
   const content = { ...cartContent('Totals round.'), checks: [{ question: 'Is rounding right?' }] };
   const seeded = await seedTour(world, { chapters: [{ title: 'Checkout', stops: [{ title: 'Cart totals', kind: 'question', content }] }] });
   return { tourId: seeded.tourId, base: seeded.base, head: seeded.head };
});

const withoutPlan = backend().template('summarize-no-plan', async (world): Promise<Seed> => {
   const seeded = await seedTour(world);
   return { tourId: seeded.tourId, base: seeded.base, head: seeded.head };
});

/** A world copied from the template, with a bound session unless `bound` is false. */
async function summaryWorld(bound = true, template = withPlan): Promise<{ world: World; tourId: string; seed: Seed; claude: ClaudeSession | null }> {
   const world = await backend().world({ template });
   const seed = world.seed as Seed;
   return { world, tourId: seed.tourId, seed, claude: bound ? await attachClaude(world, seed.tourId) : null };
}

/** Starts the app on the world and opens the tour's summary page. */
async function openSummary(world: World, transport?: Transport): Promise<{ app: LotsenApp; transport: Transport }> {
   const started = await startApp(world, transport === undefined ? {} : { transport });
   await openTour(started.app);
   started.app.openSummary();
   await waitFor(() => expect(region(/^Decisions/)).toBeTruthy());
   return started;
}

/** Loads the page again at the current address and history entry, as the browser's reload does, on the same network. */
async function startAgain(app: LotsenApp, world: World, transport: Transport): Promise<LotsenApp> {
   app.stop();
   cleanup();
   return (await startApp(world, { transport })).app;
}

const noPlanReason = /the tour has no plan yet, so Claude has no review to summarize/i;
const summaryPage = () => within(screen.getByRole('main', { name: 'Summary' }));
const actions = () => within(summaryPage().getByRole('group', { name: 'Summary actions' }));
const region = (name: string | RegExp) => within(summaryPage().getByRole('region', { name }));
const synthesis = () => summaryPage().queryByRole('region', { name: "Claude's synthesis" });
const summarizeButton = () => actions().getByRole('button', { name: 'Summarize' }) as HTMLButtonElement;

async function summarizeThreads(world: World, tourId: string): Promise<QuestionThread[]> {
   return (await readTour(world, tourId)).threads.filter((thread) => actionOf(thread)?.kind === 'summarize');
}

/** Returns the summary as Claude reads it, whose fingerprint a synthesis names. */
async function claudeSummary(claude: ClaudeSession, tourId: string): Promise<ReviewSummary> {
   return (await claude.call('read_summary', { tourId })) as ReviewSummary;
}

async function writeSynthesis(claude: ClaudeSession, tourId: string, basedOn: string): Promise<void> {
   await claude.call('write_synthesis', { tourId, text: 'The review keeps the rounding. See [the line](ref:head/src/cart.ts#L2-2).', basedOn });
}

async function startThread(claude: ClaudeSession, tourId: string, text: string): Promise<void> {
   await claude.call('start_thread', { tourId, text, anchor: { kind: 'tour' } });
}

/** Moves the tour's pin to a new head that changes only another file, so no stop goes stale and the summary reads the same. */
async function movePin(world: World, seed: Seed): Promise<void> {
   const head = await world.repo.commit('feature', 'Another change', { ...cartHead, 'src/other.ts': 'export const other = 1;\n' }, seed.head);
   await world.pullRequest.set(head, seed.base, { iteration: 2 });
   await world.reviewer.call('check_iteration', { tourId: seed.tourId });
   await world.idle();
   await updateIteration(world, seed.tourId);
}

const shownSynthesis = () =>
   waitFor(() => {
      const found = synthesis();
      expect(found).not.toBeNull();
      return within(found!);
   });

describe('Summarize on the summary page', () => {
   it('tags the box as optional, says what an empty box does, and gives it the focus', async () => {
      const { world } = await summaryWorld();
      await openSummary(world);
      await waitFor(() => expect(summarizeButton().disabled).toBe(false));

      await fireEvent.click(summarizeButton());

      const box = actions().getByRole('textbox', { name: 'What should the synthesis focus on? Optional' }) as HTMLTextAreaElement;
      expect(box.placeholder).toBe('Leave it empty and Claude summarizes without guidance.');
      await waitFor(() => expect(document.activeElement).toBe(box));
   });

   it("sends the action on the tour with the reviewer's words, and shows it waiting with a link to its thread", async () => {
      const { world, tourId } = await summaryWorld();
      const { app, transport } = await openSummary(world);
      await waitFor(() => expect(summarizeButton().disabled).toBe(false));

      await fireEvent.click(summarizeButton());
      await fireEvent.input(actions().getByRole('textbox', { name: 'What should the synthesis focus on? Optional' }), { target: { value: 'The rounding.' } });
      const release = transport.hold('request_tour_action');
      const answered = transport.answered('request_tour_action');
      await fireEvent.click(actions().getByRole('button', { name: 'Send to Claude' }));

      // The server announces the action's thread before it answers, so the page shows it waiting while the response is held.
      expect(await actions().findByText('Claude is summarizing the review.')).toBeTruthy();
      const [thread] = await summarizeThreads(world, tourId);
      expect(messagesOf(thread!)[0]!.text).toBe('The rounding.');
      release();
      await answered;

      expect(actions().queryByRole('alert')).toBeNull();
      await waitFor(() => expect(summarizeButton().disabled).toBe(true));
      expect(actions().getByText(/Claude still summarizes the review/)).toBeTruthy();
      expect(actions().queryByRole('textbox')).toBeNull();
      await fireEvent.click(actions().getByRole('button', { name: 'Show the thread' }));
      await waitFor(() => expect(app.openedThread).toEqual({ id: thread!.id }));
   });

   it('sends no words when the reviewer leaves the box empty', async () => {
      const { world, tourId } = await summaryWorld();
      const { transport } = await openSummary(world);
      await waitFor(() => expect(summarizeButton().disabled).toBe(false));

      await fireEvent.click(summarizeButton());
      await fireEvent.click(actions().getByRole('button', { name: 'Send to Claude' }));

      await waitFor(() => expect(transport.requests.filter((sent) => sent.operation === 'request_tour_action').map((sent) => sent.input)).toEqual([{ tourId, action: { kind: 'summarize' } }]));
      await waitFor(async () => expect(await summarizeThreads(world, tourId)).toHaveLength(1));
   });

   it("is disabled with the server's reason while no session is attached", async () => {
      const { world } = await summaryWorld(false);
      await openSummary(world);

      await waitFor(() => expect(summarizeButton().disabled).toBe(true));
      const reason = actions().getByText(/is attached to no Claude Code session/);
      expect(summarizeButton().getAttribute('aria-describedby')).toBe(reason.id);
   });

   it("is disabled with the server's reason while the tour has no plan", async () => {
      const { world } = await summaryWorld(true, withoutPlan);
      const { app } = await startApp(world);
      await openTour(app);

      app.openSummary();

      await waitFor(() => expect(actions().getByText(noPlanReason)).toBeTruthy());
      expect(summarizeButton().disabled).toBe(true);
   });

   it('does not say the tour has no plan while a reload on the summary page loads the tour', async () => {
      const { world } = await summaryWorld();
      const { app, transport } = await openSummary(world);
      const release = transport.hold('read_state', 'request');

      const reloaded = await startAgain(app, world, transport);

      await waitFor(() => expect(region(/^Decisions/)).toBeTruthy());
      expect(reloaded.selectedPlan).toBeUndefined();
      expect(actions().queryByText(noPlanReason)).toBeNull();
      release();
      await waitFor(() => expect(reloaded.selectedPlan).toBeDefined());
      await waitFor(() => expect(summarizeButton().disabled).toBe(false));
   });

   it('does not say the tour has no plan while a reload on the summary page cannot load the tour', async () => {
      const { world } = await summaryWorld();
      const { app, transport } = await openSummary(world);
      transport.fail('read_state', { status: 500 });

      const reloaded = await startAgain(app, world, transport);

      await waitFor(() => expect(reloaded.selectedTourError).toBe('The tour could not be loaded.'));
      expect(actions().queryByText(noPlanReason)).toBeNull();
   });

   it("shows the server's refusal and keeps the reviewer's words", async () => {
      const { world } = await summaryWorld();
      await openSummary(world);
      await waitFor(() => expect(summarizeButton().disabled).toBe(false));
      // The words break a limit that the tour's controls do not check, so the button stays enabled and the server refuses.
      const words = 'x'.repeat(10_001);

      await fireEvent.click(summarizeButton());
      await fireEvent.input(actions().getByRole('textbox'), { target: { value: words } });
      await fireEvent.click(actions().getByRole('button', { name: 'Send to Claude' }));

      expect((await actions().findByRole('alert')).textContent).toMatch(/holds at most 10000 characters/);
      expect((actions().getByRole('textbox') as HTMLTextAreaElement).value).toBe(words);
   });

   it('shows a summarize that waits from the load, and is enabled again once Claude replies', async () => {
      const { world, tourId, claude } = await summaryWorld();
      await world.reviewer.call('request_tour_action', { tourId, action: { kind: 'summarize' } });
      await openSummary(world);

      expect(await actions().findByText('Claude is summarizing the review.')).toBeTruthy();
      expect(summarizeButton().disabled).toBe(true);

      const question = await claude!.next('question');
      await claude!.call('reply', { tourId, questionId: question.questionId, resolution: 'done', text: 'Wrote the synthesis.' });

      await waitFor(() => expect(summarizeButton().disabled).toBe(false));
      expect(actions().queryByText('Claude is summarizing the review.')).toBeNull();
   });
});

describe("Claude's synthesis on the summary page", () => {
   it('shows once it arrives by event, with when Claude wrote it', async () => {
      const { world, tourId, claude } = await summaryWorld();
      await openSummary(world);
      expect(synthesis()).toBeNull();

      await writeSynthesis(claude!, tourId, (await claudeSummary(claude!, tourId)).summaryFingerprint!);

      const shown = await shownSynthesis();
      expect(shown.getByText('The review keeps the rounding.', { exact: false })).toBeTruthy();
      const writtenAt = (await world.reviewer.call('read_summary', { tourId })).synthesis!.writtenAt;
      expect(shown.getByText((_, element) => element?.tagName === 'TIME' && element.getAttribute('datetime') === writtenAt)).toBeTruthy();
      expect(shown.queryByRole('note')).toBeNull();
   });

   it("says that a synthesis older than the summary's last change predates it", async () => {
      const { world, tourId, claude } = await summaryWorld();
      await writeSynthesis(claude!, tourId, (await claudeSummary(claude!, tourId)).summaryFingerprint!);
      await startThread(claude!, tourId, 'A concern.');
      await openSummary(world);

      const note = (await shownSynthesis()).getByRole('note');
      expect(note.textContent).toMatch(/predates changes to the summary below/);
      expect(note.querySelector('svg[aria-hidden="true"]')).not.toBeNull();
   });

   it('says it predates the summary once a change arrives by event', async () => {
      const { world, tourId, claude } = await summaryWorld();
      await openSummary(world);
      await writeSynthesis(claude!, tourId, (await claudeSummary(claude!, tourId)).summaryFingerprint!);
      expect((await shownSynthesis()).queryByRole('note')).toBeNull();

      await startThread(claude!, tourId, 'A concern.');

      await waitFor(() => expect(within(synthesis()!).queryByRole('note')).not.toBeNull());
   });

   it("says it predates the summary when the summary changed between Claude's read and the write", async () => {
      const { world, tourId, claude } = await summaryWorld();
      await openSummary(world);
      const read = (await claudeSummary(claude!, tourId)).summaryFingerprint!;
      await startThread(claude!, tourId, 'A concern.');

      await writeSynthesis(claude!, tourId, read);

      await waitFor(() => expect(synthesis()).not.toBeNull());
      await waitFor(() => expect(within(synthesis()!).queryByRole('note')).not.toBeNull());
   });

   it("says it predates the summary when the pin moved between Claude's read and the write, though the summary reads the same", async () => {
      const { world, tourId, seed, claude } = await summaryWorld();
      await openSummary(world);
      const read = (await claudeSummary(claude!, tourId)).summaryFingerprint!;
      await movePin(world, seed);

      await writeSynthesis(claude!, tourId, read);

      await waitFor(() => expect(synthesis()).not.toBeNull());
      await waitFor(() => expect(within(synthesis()!).queryByRole('note')).not.toBeNull());
   });

   it('does not say it predates the summary when Claude read the summary at the current pin', async () => {
      const { world, tourId, seed, claude } = await summaryWorld();
      await openSummary(world);
      await movePin(world, seed);

      await writeSynthesis(claude!, tourId, (await claudeSummary(claude!, tourId)).summaryFingerprint!);

      await waitFor(() => expect(synthesis()).not.toBeNull());
      expect(within(synthesis()!).queryByRole('note')).toBeNull();
   });

   it('keeps the synthesis that a newer event brought when an older summary load answers after it', async () => {
      const { world, tourId, claude } = await summaryWorld();
      const { transport } = await openSummary(world);
      const loads = () => transport.requests.filter((sent) => sent.operation === 'read_summary').length;
      const before = loads();
      const release = transport.hold('read_summary');
      await startThread(claude!, tourId, 'A concern.');
      await waitFor(() => expect(loads()).toBe(before + 1));

      await writeSynthesis(claude!, tourId, (await claudeSummary(claude!, tourId)).summaryFingerprint!);
      await waitFor(() => expect(synthesis()).not.toBeNull());
      const late = transport.answered('read_summary');
      release();
      await late;

      expect(synthesis()).not.toBeNull();
   });
});
