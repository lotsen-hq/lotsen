import { describe, expect, it, beforeEach } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { ThreadActions, WriteResult } from '../../src/lib/threadActions.svelte';
import type { UpdatedThread } from '../../src/lib/types';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, cartContent, checkIteration, openStop, openTour, pushIteration, readTour, seedTour, startApp, updateIteration, withEventHolds } from '../harness';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

interface Seeded {
   tourId: string;
   stopIds: string[];
   base: string;
   head: string;
}

const chapters = [
   {
      title: 'Cart',
      stops: [
         { title: 'Cart adds', kind: 'question' as const, content: { ...cartContent('Cart adds.'), checks: [{ question: 'Is zero an error?' }] } },
         { title: 'Line totals', kind: 'inspect' as const, content: cartContent('Line totals.') },
      ],
   },
];

const written = backend().template('tourRows-written', async (world) => {
   const { tourId, stopIds, base, head } = await seedTour(world, { chapters, bound: false });
   return { tourId, stopIds, base, head } satisfies Seeded;
});

// A plan-first tour whose plan waits for the reviewer's approval, so its stops have no content.
const awaited = backend().template('tourRows-awaited', async (world) => {
   const { tourId, stopIds, base, head } = await seedTour(world, {
      planFirst: true,
      chapters: chapters.map((chapter) => ({ ...chapter, stops: chapter.stops.map(({ title, kind }) => ({ title, kind })) })),
      bound: false,
   });
   return { tourId, stopIds, base, head } satisfies Seeded;
});

/** What a write's setup and run know of the world. */
interface Context {
   world: World;
   tour: Seeded;
   claude: ClaudeSession;
   checkId: string;
   /** The thread that the write's setup made, or the empty string. */
   threadId: string;
}

/** A write of the open tour at its first stop, and the row parts that its response writes, whose earlier events the row then drops. */
interface Write {
   name: string;
   template?: typeof awaited;
   /** Makes what the write needs, before the page loads, and returns the thread it works on, if any. */
   setup?: (context: Omit<Context, 'threadId'>) => Promise<string>;
   /** Makes what the write needs once the earlier events are stored, given the pull request's head then. */
   prepare?: (context: Omit<Context, 'threadId'>, head: string) => Promise<void>;
   run: (app: LotsenApp, context: Context) => Promise<string | null>;
   writes?: 'pin' | 'familiarity';
}

const note = async ({ world, tour }: Omit<Context, 'threadId'>) =>
   ((await world.reviewer.call('save_note', { tourId: tour.tourId, text: 'A concern.', anchor: { kind: 'tour' } })) as UpdatedThread).thread.id;
const answer = async ({ world, tour, checkId }: Omit<Context, 'threadId'>) => {
   await world.reviewer.call('answer_check', { tourId: tour.tourId, stopId: tour.stopIds[0]!, checkId, text: 'Refuse zero.' });
   return '';
};

/** Runs a write of the open tour's thread actions, and returns null once the server stored it, or the reason it was refused. */
async function threadWrite(app: LotsenApp, write: (actions: ThreadActions) => Promise<WriteResult>): Promise<string | null> {
   const result = await write(app.selectedThreadActions!);
   return result.ok ? null : result.refusal;
}

const writes: Write[] = [
   { name: 'a note save', run: (app, { tour }) => threadWrite(app, (actions) => actions.saveNote({ kind: 'tour' }, 'A concern.', tour.stopIds[0]!)) },
   { name: 'a note edit', setup: note, run: (app, { threadId }) => threadWrite(app, (actions) => actions.editNote(threadId, 'A sharper concern.')) },
   { name: 'a note discard', setup: note, run: (app, { threadId }) => threadWrite(app, (actions) => actions.discardNote(threadId)) },
   { name: 'a note send', setup: note, run: (app, { threadId }) => threadWrite(app, (actions) => actions.sendNotes([threadId])) },
   {
      name: 'a thread state change',
      setup: async ({ claude, tour }) => ((await claude.call('start_thread', { tourId: tour.tourId, text: 'A concern of mine.', anchor: { kind: 'tour' } })) as UpdatedThread).thread.id,
      run: (app, { threadId }) => threadWrite(app, (actions) => actions.setState(threadId, 'resolved')),
   },
   { name: 'an answer type', setup: answer, run: (app, { checkId }) => app.setAnswerType(checkId, 'concern') },
   { name: 'an answer clear', setup: answer, run: (app, { checkId }) => app.clearAnswer(checkId) },
   { name: 'an answer send', setup: answer, run: (app) => app.sendAnswers('topBar') },
   { name: 'a skip', run: (app, { tour }) => app.setStopSkipped(tour.stopIds[1]!, true) },
   { name: 'a familiarity change', run: (app) => app.changeFamiliarity('new'), writes: 'familiarity' },
   {
      name: 'a plan approval',
      template: awaited,
      // The order names every stop of the latest plan, the third stop that the held plan event brings included.
      run: async (app, { world, tour }) =>
         app.approvePlan(
            (await readTour(world, tour.tourId)).plan!.stops.map((stop) => stop.id),
            'new',
            '',
         ),
      writes: 'familiarity',
   },
   {
      name: 'an iteration update',
      // The pull request moves on to iteration 3, which the update moves the tour to.
      prepare: async ({ world, tour }, head) => {
         const latest = await world.repo.commit('feature', 'Iteration 3', { 'docs/more.md': 'More notes.\n' }, head);
         await world.pullRequest.set(latest, tour.base, { iteration: 3, state: 'abandoned' });
         await checkIteration(world, tour.tourId);
      },
      run: (app) => app.updateIteration(),
      writes: 'pin',
   },
];

/**
 * Stores on the server, while the page's stream holds them back, an event of each row part: a new plan with a third stop, a check that finds the pull request abandoned, the reviewer's update
 * to iteration 2 in another page, and, unless the tour's plan waits, the reviewer's familiarity change in another page, which Claude answers.
 */
async function storeEarlierEvents({ world, tour, claude }: Omit<Context, 'threadId' | 'checkId'>, planWaits: boolean): Promise<string> {
   await claude.call('set_plan', {
      tourId: tour.tourId,
      chapters: [{ id: 'c1', title: 'Cart' }],
      stops: [...chapters[0]!.stops.map(({ title, kind }, index) => ({ title, kind, chapter: 'c1', id: tour.stopIds[index] })), { title: 'Refunds', kind: 'inspect', chapter: 'c1' }],
   });
   const head = await pushIteration(world, { base: tour.base, parent: tour.head }, { 'docs/notes.md': 'Notes.\n' }, 2);
   await checkIteration(world, tour.tourId);
   await updateIteration(world, tour.tourId);
   await world.pullRequest.set(head, tour.base, { iteration: 2, state: 'abandoned' });
   await checkIteration(world, tour.tourId);
   if (!planWaits) {
      await world.reviewer.call('change_familiarity', { tourId: tour.tourId, familiarity: 'familiar' });
      const question = await claude.next('question');
      await claude.call('reply', { tourId: tour.tourId, questionId: question.questionId, resolution: 'done', text: 'Rewritten.' });
   }

   await world.idle();
   return head;
}

describe('a write response that touches a tour row', () => {
   it.each(writes)('lets events stored before $name that arrive after its response update the row’s other parts', async ({ template, setup, prepare, run, writes: part }) => {
      // The templates' clocks moved on as they seeded, so each copy's clock starts after that.
      const world = await backend().world({ template: template ?? written, clockStart: '2026-01-01T13:00:00Z' });
      const tour = world.seed as Seeded;
      const claude = await attachClaude(world, tour.tourId);
      const first = (await readTour(world, tour.tourId)).plan!.stops[0]!;
      const context = { world, tour, claude, checkId: first.stage === 'written' ? (first.content.checks?.[0]?.id ?? '') : '' };
      const threadId = (await setup?.(context)) ?? '';
      const transport = withEventHolds(world.transport());
      const { app } = await startApp(world, { transport });
      await openTour(app, 'shop #42');
      await openStop(app, tour.stopIds[0]!);
      const release = transport.holdKinds('planSet', 'tourStateChanged', 'tourPinned', 'familiarityChanged');
      await world.clock.advance(1000);
      const head = await storeEarlierEvents(context, template === awaited);
      await prepare?.(context, head);
      const storedBefore = (await readTour(world, tour.tourId)).updatedAt;
      await world.clock.advance(1000);

      expect(await run(app, { ...context, threadId })).toBeNull();
      const writtenAt = app.tours[0]!.updatedAt;
      expect(Date.parse(writtenAt)).toBeGreaterThan(Date.parse(storedBefore));
      release();

      await expect.poll(() => app.tours[0]).toMatchObject({ stopCount: 3, state: 'inProgress', pullRequestState: 'abandoned' });
      expect(app.tours[0]).toMatchObject({
         pin: { iteration: part === 'pin' ? 3 : 2 },
         familiarity: part === 'familiarity' || template === awaited ? 'new' : 'familiar',
         updatedAt: writtenAt,
      });
   });
});
