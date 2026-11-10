import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { AnswerType } from '../../src/lib/types';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { attachClaude, backend, openTour, readTour, seedTour, startApp, updateIteration } from '../harness';
import { tourSwitcherName } from '../tourPicker';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

const cartBase = 'export function total(lines: number[]): number {\n   return lines.reduce((sum, line) => sum + line, 0);\n}\n';
const cartHead = 'export function total(lines: number[]): number {\n   const sum = lines.reduce((acc, line) => acc + line, 0);\n   return Math.round(sum);\n}\nexport const cap = 100;\n';
const retryBase = 'export const retries = 3;\n';
const retryHead = 'export const retries = 5;\nexport const delay = 100;\n';

const passage = (path: string, startLine: number, endLine = startLine) => ({ side: 'head', path, startLine, endLine, role: 'primary' });

interface Seed {
   tourId: string;
   /** Cart totals (inspect, at version 2), Rounding drift (risk), Retry policy (question), Old prices (context, pending and skipped). */
   stopIds: [string, string, string, string];
   /** The check of the first version of Cart totals, and Cart totals' two checks now. */
   checks: { c0: string; c1: string; c2: string; c3: string };
   /** The threads: an open one on code with Cart totals under view, a resolved one on Rounding drift, an outdated one on the tour. */
   threads: { open: string; resolved: string; outdated: string };
   base: string;
   head: string;
}

async function answer(world: World, tourId: string, stopId: string, checkId: string, text: string, type: AnswerType): Promise<void> {
   await world.reviewer.call('answer_check', { tourId, stopId, checkId, text });
   if (type !== 'decision') {
      await world.reviewer.call('set_answer_type', { tourId, stopId, checkId, type });
   }
}

async function askAndAnswer(world: World, claude: ClaudeSession, input: Record<string, unknown>): Promise<string> {
   const thread = await world.reviewer.call('ask_question', input as never);
   const question = await claude.next('question');
   await claude.call('reply', { tourId: input.tourId, questionId: question.questionId, resolution: 'done', text: 'Answered.' });
   return thread.id;
}

/**
 * Builds a tour that fills every part of the summary: a received decision, a draft concern, a sent open question, an outdated answer, a risk, a thread in each state and a skip. Its session
 * stays bound.
 */
async function buildReview(world: World): Promise<{ seed: Seed; claude: ClaudeSession }> {
   const seeded = await seedTour(world, {
      base: { 'src/cart.ts': cartBase, 'src/retry.ts': retryBase },
      head: { 'src/cart.ts': cartHead, 'src/retry.ts': retryHead },
      chapters: [
         {
            title: 'Checkout',
            stops: [
               { title: 'Cart totals', kind: 'inspect', content: { headline: 'The first rule.', passages: [passage('src/cart.ts', 2, 3)], checks: [{ question: 'Is the old rule kept?' }] } },
               { title: 'Rounding drift', kind: 'risk', content: { headline: 'Drift.', scenario: 'Two discounts round twice.', passages: [passage('src/cart.ts', 3)] } },
               { title: 'Retry policy', kind: 'question', content: { headline: 'Retries.', passages: [passage('src/retry.ts', 1)], checks: [{ question: 'How often should it retry?' }] } },
               { title: 'Old prices', kind: 'context' },
            ],
         },
      ],
   });
   const claude = seeded.claude!;
   const tourId = seeded.tourId;
   const stopIds = seeded.stopIds as Seed['stopIds'];
   const checkIds = async (index: number) => {
      const stop = (await readTour(world, tourId)).plan!.stops[index]!;
      return stop.stage === 'written' ? stop.content.checks!.map((check) => check.id) : [];
   };

   const [c0] = await checkIds(0);
   const [c3] = await checkIds(2);
   await answer(world, tourId, stopIds[0], c0!, 'Keep the old rule.', 'decision');
   await world.reviewer.call('send_answers', { tourId });
   await claude.call('put_stop', {
      tourId,
      stopId: stopIds[0],
      headline: 'Totals round.',
      passages: [passage('src/cart.ts', 2, 3)],
      checks: [{ question: 'Is rounding right?' }, { question: 'Should the cap move?' }],
   });
   const [c1, c2] = await checkIds(0);
   await answer(world, tourId, stopIds[0], c1!, 'Rounding is right.', 'decision');
   await world.reviewer.call('send_answers', { tourId });
   await claude.next('answers');
   const received = await claude.next('answers');
   await claude.call('fetch_answers', { tourId, batchId: received.batchId });
   await answer(world, tourId, stopIds[2], c3!, 'Ask the platform team.', 'openQuestion');
   await world.reviewer.call('send_answers', { tourId });
   await answer(world, tourId, stopIds[0], c2!, 'The cap belongs in config.', 'concern');

   const open = await askAndAnswer(world, claude, { tourId, text: 'Why round here?', anchor: { kind: 'code', side: 'head', path: 'src/cart.ts', startLine: 3, endLine: 4 }, stopId: stopIds[0] });
   const resolved = await askAndAnswer(world, claude, { tourId, text: 'Is drift tested?', anchor: { kind: 'stop', stopId: stopIds[1] } });
   await world.reviewer.call('set_thread_state', { tourId, threadId: resolved, state: 'resolved' });
   const outdated = await askAndAnswer(world, claude, { tourId, text: 'What about refunds?', anchor: { kind: 'tour' } });
   await claude.call('outdate_thread', { tourId, threadId: outdated });
   await world.reviewer.call('set_stop_skipped', { tourId, stopId: stopIds[3], skipped: true });
   return { seed: { tourId, stopIds, checks: { c0: c0!, c1: c1!, c2: c2!, c3: c3! }, threads: { open, resolved, outdated }, base: seeded.base, head: seeded.head }, claude };
}

const review = backend().template('summaryPage-review', async (world) => (await buildReview(world)).seed);

/** A world copied from the review template, whose session did not survive the seal. */
async function reviewWorld(): Promise<{ world: World; seed: Seed }> {
   const world = await backend().world({ template: review });
   const seed = world.seed as Seed;
   return { world, seed };
}

/** Starts the app on the world, opens the tour and the Plan panel. */
async function open(world: World, transport?: Transport): Promise<{ app: LotsenApp; transport: Transport }> {
   const started = await startApp(world, transport === undefined ? {} : { transport });
   await openTour(started.app);
   await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   await screen.findByRole('tabpanel', { name: 'Plan' });
   return started;
}

/** Loads the page again at the current address and history entry, as the browser's reload does, on the same network. */
async function startAgain(app: LotsenApp, world: World, transport: Transport): Promise<LotsenApp> {
   app.stop();
   cleanup();
   return (await startApp(world, { transport })).app;
}

/** Moves through the history, and waits for the page to restore the entry. */
async function traverse(step: () => void) {
   const popped = new Promise<void>((resolve) => window.addEventListener('popstate', () => resolve(), { once: true }));
   step();
   await popped;
}

const planPanel = () => within(screen.getByRole('tabpanel', { name: 'Plan' }));
const stopNavigation = () => within(screen.getByRole('navigation', { name: 'Stops' }));
const summaryPage = () => within(screen.getByRole('main', { name: 'Summary' }));
const region = (name: string | RegExp) => within(summaryPage().getByRole('region', { name }));
const items = (name: string | RegExp) => summaryPage().getByRole('region', { name }).querySelectorAll(':scope li');
const itemTexts = (name: string | RegExp) => [...items(name)].map((item) => item.textContent.replace(/\s+/g, ' ').trim());
const summaryLoads = (transport: Transport) => transport.requests.filter((sent) => sent.operation === 'read_summary').length;
const showsSummary = async () => expect(await screen.findByRole('main', { name: 'Summary' })).toBeTruthy();
const focusedCheck = () => document.activeElement?.closest('[data-check-id]')?.getAttribute('data-check-id') ?? null;

async function openSummary() {
   await fireEvent.click(planPanel().getByRole('button', { name: 'Summary' }));
   await waitFor(() => expect(region(/^Decisions/)).toBeTruthy());
}

function focusOutside(): HTMLInputElement {
   const input = document.createElement('input');
   document.body.append(input);
   input.focus();
   return input;
}

async function startThread(claude: ClaudeSession, tourId: string, text: string): Promise<void> {
   await claude.call('start_thread', { tourId, text, anchor: { kind: 'tour' } });
}

describe('the summary page', () => {
   describe('opens after the last stop', () => {
      it('from Next on the last stop that is not skipped, and Back returns to that stop', async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         app.openStop(seed.stopIds[2]);
         await waitFor(() => expect(stopNavigation().getByRole('button', { name: 'Summary' })).toBeTruthy());

         await fireEvent.click(stopNavigation().getByRole('button', { name: 'Summary' }));

         await showsSummary();
         expect(app.selectedStopId).toBeNull();
         expect(screen.getByRole('navigation', { name: 'Stops' }).textContent).toMatch(/Summary/);
         expect((stopNavigation().getByRole('button', { name: 'Next stop' }) as HTMLButtonElement).disabled).toBe(true);

         await fireEvent.click(stopNavigation().getByRole('button', { name: 'Previous stop' }));

         await waitFor(() => expect(app.selectedStopId).toBe(seed.stopIds[2]));
         expect(screen.queryByRole('main', { name: 'Summary' })).toBeNull();
      });

      it("from nowhere while the plan waits for the reviewer's approval: Next on the last stop is disabled and the Plan panel has no entry", async () => {
         const world = await backend().world();
         const seeded = await seedTour(world, {
            planFirst: true,
            chapters: [
               {
                  title: 'Checkout',
                  stops: [
                     { title: 'Cart totals', kind: 'inspect' },
                     { title: 'Retry policy', kind: 'question' },
                  ],
               },
            ],
         });
         const { app } = await open(world);
         app.openStop(seeded.stopIds[1]!);
         await waitFor(() => expect(screen.getByRole('navigation', { name: 'Stops' }).textContent).toMatch(/Retry policy/));

         const next = stopNavigation().getByRole('button', { name: 'Next stop' }) as HTMLButtonElement;
         expect(next.disabled).toBe(true);
         app.openNextStop();

         expect(app.showsSummary).toBe(false);
         expect(app.selectedStopId).toBe(seeded.stopIds[1]);
         expect(planPanel().queryByRole('button', { name: 'Summary' })).toBeNull();
      });

      it("from the Plan panel's entry after the last chapter, which marks the summary as current until a stop opens", async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         const entry = () => planPanel().getByRole('button', { name: 'Summary' });
         expect(entry().getAttribute('aria-current')).toBeNull();

         await fireEvent.click(entry());

         await showsSummary();
         expect(entry().getAttribute('aria-current')).toBe('step');
         await fireEvent.click(planPanel().getByRole('button', { name: /^1 / }));
         await waitFor(() => expect(app.selectedStopId).toBe(seed.stopIds[0]));
         expect(entry().getAttribute('aria-current')).toBeNull();
         expect(screen.queryByRole('main', { name: 'Summary' })).toBeNull();
      });

      it('in place of the open stop and its file, and the Stop panel says so', async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         app.openStop(seed.stopIds[0]);
         await waitFor(() => expect(app.selectedStop).not.toBeNull());

         await openSummary();

         expect(app.selectedStopId).toBeNull();
         expect(app.selectedFilePath).toBeNull();
         expect(within(screen.getByRole('region', { name: 'Stop' })).getByText(/summary/i)).toBeTruthy();
      });
   });

   describe('shows the review so far with no session attached', () => {
      it("groups the answers into decisions, concerns and open questions, with each question, the reviewer's words as text and the answer's state", async () => {
         const { world } = await reviewWorld();
         await open(world);
         await openSummary();

         expect(itemTexts(/^Decisions/)).toEqual([expect.stringMatching(/Is rounding right\?.*Rounding is right\..*Received.*Cart totals/)]);
         expect(itemTexts(/^Concerns/)[0]).toMatch(/Should the cap move\?.*The cap belongs in config\..*Draft.*Cart totals/);
         expect(itemTexts(/^Open questions/)).toEqual([expect.stringMatching(/How often should it retry\?.*Ask the platform team\..*Not received.*Retry policy/)]);
         expect(itemTexts(/^Outdated answers/)).toEqual([expect.stringMatching(/Is the old rule kept\?.*Keep the old rule\.\s*Outdated.*version 1/)]);
         expect(
            region(/^Decisions/)
               .getByText('Is rounding right?')
               .closest('.markdown'),
         ).not.toBeNull();
      });

      // The answer must go to a session that is still attached, and a template's session ends at its seal, so the test builds the whole review and takes longer than one flow.
      it("names a sent answer's state as its check does while the session that it went to is attached", async () => {
         const world = await backend().world();
         await buildReview(world);
         await open(world);
         await openSummary();

         expect(itemTexts(/^Open questions/)[0]).toMatch(/Ask the platform team\.\s*Sent\s*Retry policy/);
      }, 60_000);

      it("names a sent answer's state as its check does while another session is attached", async () => {
         const { world, seed } = await reviewWorld();
         await attachClaude(world, seed.tourId);
         await open(world);
         await openSummary();

         expect(itemTexts(/^Open questions/)[0]).toMatch(/Ask the platform team\.\s*Not received\s*Retry policy/);
      });

      it('lists the risk stops as concerns with their scenario', async () => {
         const { world } = await reviewWorld();
         await open(world);
         await openSummary();

         expect(itemTexts(/^Concerns/)[1]).toMatch(/Risk.*Rounding drift.*Two discounts round twice\./);
      });

      it('lists the threads by state, and the skipped stops', async () => {
         const { world } = await reviewWorld();
         await open(world);
         await openSummary();

         expect(itemTexts(/^Open threads/)).toEqual([expect.stringMatching(/Why round here\?.*src\/cart\.ts/)]);
         expect(itemTexts(/^Resolved threads/)).toEqual([expect.stringMatching(/Is drift tested\?.*Rounding drift/)]);
         expect(itemTexts(/^Outdated threads/)).toEqual([expect.stringMatching(/What about refunds\?.*The tour/)]);
         expect(itemTexts(/^Skipped stops/)).toEqual(['Old prices']);
      });

      it('lists a stop that an iteration update left stale, with the iteration', async () => {
         const { world, seed } = await reviewWorld();
         await attachClaude(world, seed.tourId);
         const head = await world.repo.commit('feature', 'Retry less', { 'src/retry.ts': 'export const retries = 7;\nexport const delay = 100;\n' }, seed.head);
         await world.pullRequest.set(head, seed.base, { iteration: 2 });
         await world.reviewer.call('check_iteration', { tourId: seed.tourId });
         await world.idle();
         await updateIteration(world, seed.tourId);
         await open(world);
         await openSummary();

         expect(itemTexts(/^Stale stops/)).toEqual([expect.stringMatching(/Retry policy.*Stale since update 2/)]);
      });

      it('says when a group is empty, leaves out the outdated answers when there are none, and offers Summarize disabled without a session', async () => {
         const world = await backend().world();
         const content = { headline: 'Totals round.', passages: [passage('src/cart.ts', 2)] };
         await seedTour(world, { bound: false, chapters: [{ title: 'Checkout', stops: [{ title: 'Cart totals', kind: 'inspect', content }] }] });
         await open(world);
         await openSummary();

         expect(region(/^Decisions/).getByText('None yet.')).toBeTruthy();
         expect(region(/^Concerns/).getByText('None yet.')).toBeTruthy();
         expect(summaryPage().queryByRole('region', { name: /^Outdated answers/ })).toBeNull();
         const summarize = within(summaryPage().getByRole('group', { name: 'Summary actions' })).getByRole('button', { name: 'Summarize' }) as HTMLButtonElement;
         await waitFor(() => expect(summarize.disabled).toBe(true));
      });

      it('names an answer whose stop is gone without a link', async () => {
         const { world, seed } = await reviewWorld();
         const claude = await attachClaude(world, seed.tourId);
         const [s1, s2, , s4] = seed.stopIds;
         await claude.call('set_plan', {
            tourId: seed.tourId,
            chapters: [{ id: 'c1', title: 'Checkout' }],
            stops: [
               { id: s1, title: 'Cart totals', kind: 'inspect', chapter: 'c1' },
               { id: s2, title: 'Rounding drift', kind: 'risk', chapter: 'c1' },
               { id: s4, title: 'Old prices', kind: 'context', chapter: 'c1' },
            ],
         });
         await open(world);
         await openSummary();

         expect(itemTexts(/^Open questions/)[0]).toMatch(/A check no longer in the plan.*Ask the platform team\..*A stop no longer in the plan/);
         expect(within(items(/^Open questions/)[0] as HTMLElement).queryByRole('button')).toBeNull();
      });
   });

   describe('links each item', () => {
      it('to its stop', async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         await openSummary();

         await fireEvent.click(within(items(/^Skipped stops/)[0] as HTMLElement).getByRole('button', { name: 'Old prices' }));

         await waitFor(() => expect(app.selectedStopId).toBe(seed.stopIds[3]));
      });

      it("to its check, which opens in the stop's Checks section with the focus on it", async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         await openSummary();

         await fireEvent.click(within(items(/^Concerns/)[0] as HTMLElement).getByRole('button', { name: 'Go to the check' }));

         await waitFor(() => expect(focusedCheck()).toBe(seed.checks.c2));
         expect(app.selectedStopId).toBe(seed.stopIds[0]);
         expect(
            within(screen.getByRole('region', { name: 'Stop' }))
               .getByRole('button', { name: 'Checks' })
               .getAttribute('aria-expanded'),
         ).toBe('true');
      });

      it('to its check in a section that the reviewer closed', async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         app.openStop(seed.stopIds[0]);
         const checks = await within(screen.getByRole('region', { name: 'Stop' })).findByRole('button', { name: 'Checks' });
         await fireEvent.click(checks);
         await waitFor(() => expect(checks.getAttribute('aria-expanded')).toBe('false'));
         await openSummary();

         await fireEvent.click(within(items(/^Decisions/)[0] as HTMLElement).getByRole('button', { name: 'Go to the check' }));

         await waitFor(() => expect(focusedCheck()).toBe(seed.checks.c1));
      });

      it('to the check of an outdated answer at the stop version that holds it', async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         await openSummary();

         await fireEvent.click(within(items(/^Outdated answers/)[0] as HTMLElement).getByRole('button', { name: 'Go to the check in version 1' }));

         await waitFor(() => expect(focusedCheck()).toBe(seed.checks.c0));
         expect(app.shownVersion?.number).toBe(1);
      });

      it('to its thread, which opens in the Threads panel', async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         await openSummary();

         await fireEvent.click(within(items(/^Resolved threads/)[0] as HTMLElement).getByRole('button', { name: /Is drift tested\?/ }));

         await waitFor(() => expect(app.openedThread).toEqual({ id: seed.threads.resolved }));
         expect(app.selectedThreadFilter.states).toContain('resolved');
         await showsSummary();
      });
   });

   it('opens a check each time the reviewer goes to it from the summary page', async () => {
      const { world, seed } = await reviewWorld();
      await open(world);
      await openSummary();
      await fireEvent.click(within(items(/^Concerns/)[0] as HTMLElement).getByRole('button', { name: 'Go to the check' }));
      await waitFor(() => expect(focusedCheck()).toBe(seed.checks.c2));
      const input = focusOutside();
      await openSummary();

      await fireEvent.click(within(items(/^Concerns/)[0] as HTMLElement).getByRole('button', { name: 'Go to the check' }));

      await waitFor(() => expect(focusedCheck()).toBe(seed.checks.c2));
      input.remove();
   });

   describe('in the browser history', () => {
      it('has its own entry, which back and forward restore', async () => {
         const { world, seed } = await reviewWorld();
         const { app } = await open(world);
         app.openStop(seed.stopIds[2]);
         await waitFor(() => expect(app.selectedStopId).toBe(seed.stopIds[2]));
         const length = history.length;

         await fireEvent.click(stopNavigation().getByRole('button', { name: 'Summary' }));

         await showsSummary();
         expect(location.hash).toBe(`#tour=${seed.tourId}&view=summary`);
         expect(history.length).toBe(length + 1);
         await traverse(() => history.back());
         await waitFor(() => expect(app.selectedStopId).toBe(seed.stopIds[2]));
         expect(screen.queryByRole('main', { name: 'Summary' })).toBeNull();
         await traverse(() => history.forward());
         await showsSummary();
         expect(app.selectedStopId).toBeNull();
      });

      it('comes back after a reload', async () => {
         const { world, seed } = await reviewWorld();
         const { app, transport } = await open(world);
         await openSummary();

         const reloaded = await startAgain(app, world, transport);

         await showsSummary();
         await waitFor(() => expect(region(/^Decisions/)).toBeTruthy());
         expect(reloaded.selectedTourId).toBe(seed.tourId);
      });

      it('comes back after a reload before the plan loads', async () => {
         const { world } = await reviewWorld();
         const { app, transport } = await open(world);
         await openSummary();
         const release = transport.hold('read_state', 'request');

         const reloaded = await startAgain(app, world, transport);

         await waitFor(() => expect(region(/^Decisions/)).toBeTruthy());
         expect(reloaded.selectedPlan).toBeUndefined();
         release();
         await waitFor(() => expect(reloaded.selectedPlan).toBeDefined());
         await showsSummary();
      });

      it('falls back to the tour list when the tour is gone', async () => {
         const { world, seed } = await reviewWorld();
         const { app, transport } = await open(world);
         await openSummary();
         await world.reviewer.call('delete_tour', { tourId: seed.tourId });

         const reloaded = await startAgain(app, world, transport);

         await screen.findByRole('button', { name: tourSwitcherName });
         await waitFor(() => expect(reloaded.toursLoaded).toBe(true));
         expect(reloaded.selectedTourId).toBeNull();
         expect(screen.queryByRole('main', { name: 'Summary' })).toBeNull();
      });
   });

   describe("follows the tour's events", () => {
      it('shows an answer that an event brings without a reload', async () => {
         const { world, seed } = await reviewWorld();
         await open(world);
         await openSummary();

         await world.reviewer.call('answer_check', { tourId: seed.tourId, stopId: seed.stopIds[2], checkId: seed.checks.c3, text: 'Every five seconds.' });

         // An answer whose text changes becomes a draft and keeps its type.
         await waitFor(() => expect(itemTexts(/^Open questions/)).toEqual([expect.stringMatching(/How often should it retry\?.*Every five seconds\..*Draft/)]));
         expect(itemTexts(/^Decisions/)).toHaveLength(1);
      });

      it('shows a thread that an event brings, and a skip', async () => {
         const { world, seed } = await reviewWorld();
         const claude = await attachClaude(world, seed.tourId);
         await open(world);
         await openSummary();

         await startThread(claude, seed.tourId, 'Rounding needs a test.');
         await waitFor(() => expect(itemTexts(/^Open threads/)).toHaveLength(2));
         expect(itemTexts(/^Open threads/)[1]).toMatch(/Rounding needs a test\./);

         await world.reviewer.call('set_stop_skipped', { tourId: seed.tourId, stopId: seed.stopIds[0], skipped: true });
         await waitFor(() => expect(itemTexts(/^Skipped stops/)).toEqual(['Cart totals', 'Old prices']));
      });

      it('keeps what an event brought when an older response arrives late', async () => {
         const { world, seed } = await reviewWorld();
         const claude = await attachClaude(world, seed.tourId);
         const { transport } = await open(world);
         await openSummary();
         const before = summaryLoads(transport);
         const release = transport.hold('read_summary');
         await startThread(claude, seed.tourId, 'First.');
         await waitFor(() => expect(summaryLoads(transport)).toBe(before + 1));

         await startThread(claude, seed.tourId, 'Second.');
         await waitFor(() => expect(itemTexts(/^Open threads/)).toHaveLength(3));
         const late = transport.answered('read_summary');
         release();
         await late;

         expect(itemTexts(/^Open threads/)).toHaveLength(3);
      });

      it('shows no response to a request that a later one superseded, even while it holds no summary', async () => {
         const { world, seed } = await reviewWorld();
         const claude = await attachClaude(world, seed.tourId);
         const { transport } = await open(world);
         const first = transport.hold('read_summary');
         await fireEvent.click(planPanel().getByRole('button', { name: 'Summary' }));
         await showsSummary();
         const second = transport.hold('read_summary');
         await startThread(claude, seed.tourId, 'First.');
         await waitFor(() => expect(summaryLoads(transport)).toBe(2));

         const firstAnswered = transport.answered('read_summary');
         first();
         await firstAnswered;

         expect(summaryPage().getByText('Loading the summary…')).toBeTruthy();
         second();
         await waitFor(() => expect(itemTexts(/^Open threads/)).toHaveLength(2));
      });

      it('does not come back when the reviewer opens a stop while it loads', async () => {
         const { world, seed } = await reviewWorld();
         const { app, transport } = await open(world);
         const release = transport.hold('read_summary');
         await fireEvent.click(planPanel().getByRole('button', { name: 'Summary' }));
         await showsSummary();
         expect(summaryPage().getByText('Loading the summary…')).toBeTruthy();

         app.openStop(seed.stopIds[1]);
         const late = transport.answered('read_summary');
         release();
         await late;

         expect(app.selectedStopId).toBe(seed.stopIds[1]);
         expect(screen.queryByRole('main', { name: 'Summary' })).toBeNull();
      });

      it('loads nothing while the page shows a stop', async () => {
         const { world, seed } = await reviewWorld();
         const claude = await attachClaude(world, seed.tourId);
         const { app, transport } = await open(world);
         app.openStop(seed.stopIds[0]);
         await waitFor(() => expect(app.selectedStopId).toBe(seed.stopIds[0]));
         const threads = (await readTour(world, seed.tourId)).threads.length;

         await startThread(claude, seed.tourId, 'Later.');
         await waitFor(() => expect(app.selectedThreads).toHaveLength(threads + 1));

         expect(summaryLoads(transport)).toBe(0);
      });
   });
});
