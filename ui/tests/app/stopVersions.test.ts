import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import { versionOf } from '../../src/lib/stops';
import type { WrittenStop } from '../../src/lib/types';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, openStop, openTour, prLabel, readTour, seedTour, startApp, withEventHolds, withHolds } from '../harness';
import { openTourList, tourButton } from '../tourPicker';

beforeEach(() => {
   sessionStorage.clear();
   localStorage.clear();
   localStorage.setItem('lotsen.placement', 'side');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

// Every line of the file changes, so a passage anywhere in it touches the diff, as a question stop's primary passage must.
const roundFile = (factor: number) => Array.from({ length: 24 }, (_, index) => `export const v${index + 1} = ${(index + 1) * factor};\n`).join('');
const passage = (startLine: number) => ({ side: 'head', path: 'src/round.ts', startLine, endLine: startLine + 1, role: 'primary' });

interface Seed {
   tourId: string;
   otherId: string;
   stopId: string;
   keptId: string;
   droppedId: string;
}

// An overview stop and a question stop with two checks, a how and a passage at line 3.
const rounding = backend().template('stopVersions-rounding', async (world): Promise<Seed> => {
   const seeded = await seedTour(world, {
      base: { 'src/round.ts': roundFile(1) },
      head: { 'src/round.ts': roundFile(2) },
      chapters: [
         {
            title: 'Checkout',
            stops: [
               { title: 'Overview', kind: 'overview', content: { headline: 'Another stop.' } },
               {
                  title: 'Rounding',
                  kind: 'question',
                  content: { headline: 'Decide the rounding.', how: 'How one.', checks: [{ question: 'Keep the rounding?' }, { question: 'Is the test enough?' }], passages: [passage(3)] },
               },
            ],
         },
      ],
   });
   const stop = (await readTour(world, seeded.tourId)).plan!.stops[1] as WrittenStop;
   const [kept, dropped] = stop.content.checks!;
   return { tourId: seeded.tourId, otherId: seeded.stopIds[0]!, stopId: seeded.stopIds[1]!, keptId: kept!.id, droppedId: dropped!.id };
});

interface Rounding extends Seed {
   world: World;
   claude: ClaudeSession;
}

async function roundingWorld(): Promise<Rounding> {
   const world = await backend().world({ template: rounding });
   const seed = world.seed as Seed;
   return { world, claude: await attachClaude(world, seed.tourId), ...seed };
}

/** Writes the question stop again as Claude, later on the server's clock, keeping only the kept check. */
async function revise({ world, claude, tourId, stopId, keptId }: Rounding, headline: string, how: string, startLine: number): Promise<void> {
   await world.clock.advance(1000);
   await claude.call('put_stop', { tourId, stopId, headline, how, checks: [{ id: keptId, question: 'Keep the rounding?' }], passages: [passage(startLine)] });
}

const second = (rounding: Rounding) => revise(rounding, 'Decide the rounding again.', 'How two.', 10);
const third = (rounding: Rounding) => revise(rounding, 'Decide the rounding a third time.', 'How three.', 20);

async function answer({ world, tourId, stopId }: Rounding, checkId: string, text = 'An answer.'): Promise<void> {
   await world.reviewer.call('answer_check', { tourId, stopId, checkId, text });
}

async function stopTextNote({ world, tourId, stopId }: Rounding, field: string, text: string): Promise<string> {
   const saved = await world.reviewer.call('save_note', { tourId, text: 'Why?', anchor: { kind: 'stopText', stopId, field, text } });
   return saved.thread.id;
}

// The page's network with the holds of both harness wrappers.
const heldTransport = (world: World) => withEventHolds(withHolds(world.transport()));

async function openAt(rounding: Rounding, stopId = rounding.stopId, transport = heldTransport(rounding.world)): Promise<LotsenApp> {
   const { app } = await startApp(rounding.world, { transport });
   await openTour(app);
   await openStop(app, stopId);
   return app;
}

const questionStop = (app: LotsenApp, rounding: Rounding) => app.selectedPlan?.plan?.stops.find((stop) => stop.id === rounding.stopId);

describe('a revision of a stop', () => {
   it("keeps the replaced version, makes the dropped check's answer outdated and drops it from the unsent count", async () => {
      const rounding = await roundingWorld();
      await answer(rounding, rounding.keptId);
      await answer(rounding, rounding.droppedId);
      const how = await stopTextNote(rounding, 'how', 'one');
      const kept = await stopTextNote(rounding, `checks[${rounding.keptId}].question`, 'rounding');
      const app = await openAt(rounding);
      await waitFor(() => expect(app.unsentAnswers).toBe(2));

      await second(rounding);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.currentStop?.status).toBe('revised');
      expect(app.selectedStopVersions.map((known) => [known.number, known.content.headline])).toEqual([[1, 'Decide the rounding.']]);
      expect(app.selectedStopAnswers.get(rounding.droppedId)?.outdatedOn).toBe(1);
      expect(app.unsentAnswers).toBe(1);
      expect(app.selectedThreads.map((thread) => [thread.id, thread.state, thread.anchor.kind === 'stopText' ? thread.anchor.version : null])).toEqual([
         [how, 'outdated', 1],
         [kept, 'open', 2],
      ]);
   });

   it('keeps what the revision event brought when the load of the tour that the server answered before it arrives after it', async () => {
      const rounding = await roundingWorld();
      await answer(rounding, rounding.droppedId);
      const transport = heldTransport(rounding.world);
      const { app } = await startApp(rounding.world, { transport });
      const load = transport.holdAnswered('read_state');
      (await openTourList())
         .getAllByRole('button', tourButton)
         .find((button) => button.textContent?.includes(prLabel))!
         .click();
      await load.answered;

      await second(rounding);
      // The revision's event has reached the page once the tour list holds the revision's update time.
      const revisedAt = (await readTour(rounding.world, rounding.tourId)).updatedAt;
      await waitFor(() => expect(app.tours.find((tour) => tour.id === rounding.tourId)?.updatedAt).toBe(revisedAt));
      await load.release();
      await waitFor(() => expect(versionOf(questionStop(app, rounding))).toBe(2));
      app.openStop(rounding.stopId);

      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(versionOf(app.selectedStop)).toBe(2);
      expect(app.selectedStopVersions.map((known) => known.number)).toEqual([1]);
      expect(app.selectedStopAnswers.get(rounding.droppedId)?.outdatedOn).toBe(1);
   });

   it('shows the earlier versions from a load of a tour revised before the page opened', async () => {
      const rounding = await roundingWorld();
      await second(rounding);

      const app = await openAt(rounding);

      await waitFor(() => expect(app.selectedStopVersions.map((known) => known.number)).toEqual([1]));
   });
});

describe('the stop under view', () => {
   const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
   const banner = () => stopPanel().queryByRole('status', { name: 'Stop version' });
   const answerGroup = (number: number) => within(stopPanel().getByRole('group', { name: `Your answer to check ${number}` }));

   it('stays as it was when Claude revises it, with a banner that shows the new version', async () => {
      const rounding = await roundingWorld();
      const app = await openAt(rounding);
      await stopPanel().findByText('Decide the rounding.');

      await second(rounding);

      const shown = await waitFor(() => {
         const found = banner();
         expect(found).not.toBeNull();
         return within(found!);
      });
      expect(shown.getByText(/Claude revised this stop/)).toBeDefined();
      expect(stopPanel().getByText('Decide the rounding.')).toBeDefined();
      expect(stopPanel().queryByText('Decide the rounding again.')).toBeNull();
      expect(versionOf(app.currentStop)).toBe(2);
      expect(versionOf(app.selectedStop)).toBe(1);
      expect(app.stepPassages.map((known) => known.startLine)).toEqual([3]);
      expect(app.focusedPassage?.startLine).toBe(3);

      await fireEvent.click(shown.getByRole('button', { name: 'Show the new version' }));

      await stopPanel().findByText('Decide the rounding again.');
      expect(banner()).toBeNull();
      expect(versionOf(app.selectedStop)).toBe(2);
      expect(app.stepPassages.map((known) => known.startLine)).toEqual([10]);
   });

   it('updates a stop that the reviewer is not viewing without a banner', async () => {
      const rounding = await roundingWorld();
      const app = await openAt(rounding, rounding.otherId);
      await stopPanel().findByText('Another stop.');

      await second(rounding);

      await waitFor(() => expect(versionOf(questionStop(app, rounding))).toBe(2));
      expect(banner()).toBeNull();
      app.openStop(rounding.stopId);
      await stopPanel().findByText('Decide the rounding again.');
      expect(banner()).toBeNull();
   });

   it('stays on the version the reviewer read when a second revision arrives while the banner shows, and shows the newest one', async () => {
      const rounding = await roundingWorld();
      const app = await openAt(rounding);
      await second(rounding);
      await waitFor(() => expect(banner()).not.toBeNull());

      await third(rounding);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(3));
      expect(versionOf(app.selectedStop)).toBe(1);
      expect(stopPanel().getByText('Decide the rounding.')).toBeDefined();
      await fireEvent.click(within(banner()!).getByRole('button', { name: 'Show the new version' }));
      await stopPanel().findByText('Decide the rounding a third time.');
   });

   it('releases the hold when the reviewer opens another stop, so coming back shows the newest version', async () => {
      const rounding = await roundingWorld();
      const app = await openAt(rounding);
      await second(rounding);
      await waitFor(() => expect(banner()).not.toBeNull());

      app.openStop(rounding.otherId);
      await stopPanel().findByText('Another stop.');
      app.openStop(rounding.stopId);

      await stopPanel().findByText('Decide the rounding again.');
      expect(banner()).toBeNull();
   });

   it('closes a held stop that a new plan drops', async () => {
      const rounding = await roundingWorld();
      const app = await openAt(rounding);
      await second(rounding);
      await waitFor(() => expect(banner()).not.toBeNull());

      await rounding.claude.call('set_plan', {
         tourId: rounding.tourId,
         chapters: [{ id: 'c1', title: 'Checkout' }],
         stops: [{ id: rounding.otherId, title: 'Overview', kind: 'overview', chapter: 'c1' }],
      });

      await waitFor(() => expect(app.selectedStop).toBeNull());
      expect(app.shownVersion).toBeNull();
      expect(banner()).toBeNull();
   });

   // The page learns of the revision from a reload while its event is held, and the event arrives once the reviewer has shown the new version.
   it('makes no second hold from a late revision event after the reviewer showed the new version', async () => {
      const rounding = await roundingWorld();
      const transport = heldTransport(rounding.world);
      const app = await openAt(rounding, rounding.stopId, transport);
      await stopPanel().findByText('Decide the rounding.');
      const releaseEvents = transport.holdKinds('stopRevised');
      await second(rounding);
      app.selectTour(rounding.tourId, 'wholePr');
      await waitFor(() => expect(banner()).not.toBeNull());
      await fireEvent.click(within(banner()!).getByRole('button', { name: 'Show the new version' }));
      await stopPanel().findByText('Decide the rounding again.');

      releaseEvents();

      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(banner()).toBeNull();
      expect(versionOf(app.selectedStop)).toBe(2);
   });

   it('holds the version the reviewer read when the revision comes only from the reload after the event stream reconnects', async () => {
      const rounding = await roundingWorld();
      const transport = heldTransport(rounding.world);
      const app = await openAt(rounding, rounding.stopId, transport);
      await stopPanel().findByText('Decide the rounding.');

      let releaseConnects = transport.holdEvents();
      await second(rounding);
      releaseConnects();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      await waitFor(() => expect(banner()).not.toBeNull());
      expect(within(banner()!).getByText(/Claude revised this stop/)).toBeDefined();
      expect(stopPanel().getByText('Decide the rounding.')).toBeDefined();
      expect(app.stepPassages.map((known) => known.startLine)).toEqual([3]);
      const picker = stopPanel().getByRole('combobox', { name: 'Stop version' }) as HTMLSelectElement;
      expect(picker.value).toBe('1');
      expect([...picker.options].map((option) => option.textContent)).toEqual(['Version 1', 'Version 2, current']);

      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      releaseConnects = transport.holdEvents();
      await third(rounding);
      releaseConnects();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(3));
      expect(versionOf(app.selectedStop)).toBe(1);
      await fireEvent.click(within(banner()!).getByRole('button', { name: 'Show the new version' }));
      await stopPanel().findByText('Decide the rounding a third time.');
   });

   // The response to the reviewer's request names the revise as waiting, and no plan arrives before the reload, whose controls name nothing waiting any more.
   it("shows a revision that the reviewer asked for while reading the stop when the reload after a reconnect brings it with Claude's reply", async () => {
      const rounding = await roundingWorld();
      const transport = heldTransport(rounding.world);
      const app = await openAt(rounding, rounding.stopId, transport);
      await stopPanel().findByText('Decide the rounding.');
      expect(await app.requestAction('revise', 'stop', 'Say more.')).toBeNull();
      const question = await rounding.claude.next('question');

      const releaseConnects = transport.holdEvents();
      await second(rounding);
      await rounding.claude.call('reply', { tourId: rounding.tourId, questionId: question.questionId as string, resolution: 'done', text: 'Revised.' });
      releaseConnects();

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toBeNull();
      expect(versionOf(app.selectedStop)).toBe(2);
   });

   it("holds the version the reviewer read for a revision that nobody asked for after Claude answered the reviewer's revise without a revision", async () => {
      const rounding = await roundingWorld();
      const app = await openAt(rounding);
      await stopPanel().findByText('Decide the rounding.');
      expect(await app.requestAction('revise', 'stop', 'Say more.')).toBeNull();
      const question = await rounding.claude.next('question');
      await rounding.claude.call('reply', { tourId: rounding.tourId, questionId: question.questionId as string, resolution: 'declined' });
      await waitFor(() => expect(app.selectedControls?.offers.pending.stops).toEqual({}));

      await second(rounding);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      expect(app.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'revised' } });
   });

   it('holds the version the reviewer read when a load brings the revision before its event, and the late event moves nothing', async () => {
      const rounding = await roundingWorld();
      const transport = heldTransport(rounding.world);
      const app = await openAt(rounding, rounding.stopId, transport);
      await stopPanel().findByText('Decide the rounding.');
      const releaseEvents = transport.holdKinds('stopRevised');
      await second(rounding);

      app.selectTour(rounding.tourId, 'wholePr');

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(2));
      await waitFor(() => expect(banner()).not.toBeNull());
      expect(versionOf(app.selectedStop)).toBe(1);
      releaseEvents();

      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(app.shownVersion).toEqual({ number: 1, current: 2, reason: { kind: 'revised' } });
      expect(stopPanel().getByText('Decide the rounding.')).toBeDefined();
   });

   it('shows no banner for a reload that brings the version on screen', async () => {
      const rounding = await roundingWorld();
      const transport = heldTransport(rounding.world);
      const app = await openAt(rounding, rounding.stopId, transport);
      await stopPanel().findByText('Decide the rounding.');
      const loads = () => transport.requests.filter((request) => request.operation === 'read_state').length;
      const before = loads();

      app.selectTour(rounding.tourId, 'wholePr');

      await waitFor(() => expect(loads()).toBe(before + 1));
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(banner()).toBeNull();
      expect(app.shownVersion).toBeNull();
   });

   it('stays on an earlier version that the reviewer opened when a revision arrives, without the held banner', async () => {
      const rounding = await roundingWorld();
      const app = await openAt(rounding, rounding.otherId);
      await second(rounding);
      await waitFor(() => expect(versionOf(questionStop(app, rounding))).toBe(2));
      app.openStop(rounding.stopId);
      await stopPanel().findByText('Decide the rounding again.');
      app.openVersion(1);
      await stopPanel().findByText('Decide the rounding.');

      await third(rounding);

      await waitFor(() => expect(versionOf(app.currentStop)).toBe(3));
      expect(app.shownVersion).toEqual({ number: 1, current: 3, reason: { kind: 'opened' } });
      expect(within(banner()!).getByText(/version 1 of 3/)).toBeDefined();
      expect(within(banner()!).queryByText(/Claude revised this stop/)).toBeNull();
   });

   it('keeps the words that the reviewer typed into the answer of a kept check through the banner to the new version', async () => {
      const rounding = await roundingWorld();
      await openAt(rounding);
      await fireEvent.input(answerGroup(1).getByRole('textbox', { name: 'Your own answer' }), { target: { value: 'Half up.' } });

      await second(rounding);
      await waitFor(() => expect(banner()).not.toBeNull());
      expect(answerGroup(1).queryByRole('textbox')).toBeNull();
      await fireEvent.click(within(banner()!).getByRole('button', { name: 'Show the new version' }));

      await stopPanel().findByText('Decide the rounding again.');
      expect((answerGroup(1).getByRole('textbox', { name: 'Your own answer' }) as HTMLTextAreaElement).value).toBe('Half up.');
   });

   it('opens an earlier version read-only, with its outdated answers marked and left out of the unsent count', async () => {
      const rounding = await roundingWorld();
      await answer(rounding, rounding.keptId);
      await answer(rounding, rounding.droppedId, 'Dropped words.');
      const app = await openAt(rounding, rounding.otherId);
      await second(rounding);
      await waitFor(() => expect(versionOf(questionStop(app, rounding))).toBe(2));
      app.openStop(rounding.stopId);
      await stopPanel().findByText('Decide the rounding again.');
      expect(app.unsentAnswers).toBe(1);
      expect(stopPanel().queryByText('Dropped words.')).toBeNull();

      const picker = stopPanel().getByRole('combobox', { name: 'Stop version' }) as HTMLSelectElement;
      expect([...picker.options].map((option) => option.textContent)).toEqual(['Version 1', 'Version 2, current']);
      await fireEvent.change(picker, { target: { value: '1' } });

      await stopPanel().findByText('Decide the rounding.');
      expect(within(banner()!).getByText(/version 1 of 2/)).toBeDefined();
      const dropped = answerGroup(2);
      expect(dropped.getByText('Dropped words.')).toBeDefined();
      expect(dropped.getByText('Outdated')).toBeDefined();
      expect(dropped.queryByRole('textbox')).toBeNull();
      expect(dropped.queryByRole('button')).toBeNull();
      expect(dropped.queryByRole('combobox')).toBeNull();
      expect(answerGroup(1).queryByRole('textbox')).toBeNull();
      expect(app.unsentAnswers).toBe(1);

      await fireEvent.click(within(banner()!).getByRole('button', { name: 'Show the current version' }));
      await stopPanel().findByText('Decide the rounding again.');
      expect(banner()).toBeNull();
   });

   it('marks a stop text thread on the version it was asked on', async () => {
      const rounding = await roundingWorld();
      await stopTextNote(rounding, 'how', 'one');
      const app = await openAt(rounding, rounding.otherId);
      await second(rounding);
      await waitFor(() => expect(versionOf(questionStop(app, rounding))).toBe(2));
      app.openStop(rounding.stopId);
      await stopPanel().findByText('Decide the rounding again.');
      const markers = () => stopPanel().queryAllByRole('button', { name: /^Open the thread/ });
      expect(markers()).toHaveLength(0);

      app.openVersion(1);

      await stopPanel().findByText('Decide the rounding.');
      await waitFor(() => expect(markers()).toHaveLength(1));
   });

   it('offers no ask about text selected in an earlier version', async () => {
      const rounding = await roundingWorld();
      await openAt(rounding);
      const askButton = () => stopPanel().queryByRole('button', { name: /^Ask Claude about “/ });
      selectAll(await stopPanel().findByText('Decide the rounding.'));
      await waitFor(() => expect(askButton()).not.toBeNull());
      document.getSelection()!.removeAllRanges();
      document.dispatchEvent(new Event('selectionchange'));
      await waitFor(() => expect(askButton()).toBeNull());

      await second(rounding);
      await waitFor(() => expect(banner()).not.toBeNull());
      selectAll(stopPanel().getByText('Decide the rounding.'));

      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(askButton()).toBeNull();
      document.getSelection()!.removeAllRanges();
   });
});

function selectAll(element: Element): void {
   const range = document.createRange();
   range.selectNodeContents(element);
   document.getSelection()!.removeAllRanges();
   document.getSelection()!.addRange(range);
   document.dispatchEvent(new Event('selectionchange'));
}
