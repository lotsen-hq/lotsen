import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { AnswerType, CheckAnswer } from '../../src/lib/types';
import type { ClaudeSession, Transport, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openStop, openTour, readTour, seedTour, startApp, withHolds } from '../harness';
import { threadViewElement } from './threadView';

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
   stopIds: string[];
   /** The IDs of the checks that the server gave: the rounding and the test check of the first stop, and the ship check of the second. */
   checks: { rounding: string; test: string; ship: string };
}

const rounding = {
   question: 'Is rounding per line right?',
   suggestions: [
      { text: 'Round per line.', type: 'decision' },
      { text: 'Totals may drift by a cent.', type: 'concern' },
   ],
};

const test = { question: 'Is the test enough?' };

// Two question stops, the first with a check that has suggestions and one that has none, the second with one check, and a stop without checks.
const checks = backend().template('answers-checks', async (world) => {
   const seeded = await seedTour(world, {
      chapters: [
         {
            title: 'Checkout',
            stops: [
               { title: 'Rounding', kind: 'question', content: { ...cartContent('Decide the rounding.'), checks: [rounding, test] } },
               { title: 'Shipping', kind: 'question', content: { ...cartContent('Another stop.'), checks: [{ question: 'Ship it?' }] } },
               { title: 'Notes', kind: 'inspect', content: cartContent('No checks here.') },
            ],
         },
      ],
   });
   const stops = (await readTour(world, seeded.tourId)).plan!.stops;
   const idsOf = (index: number) => (stops[index]!.stage === 'written' ? (stops[index]!.content.checks ?? []).map((check) => check.id) : []);
   const [roundingId, testId] = idsOf(0);
   return { tourId: seeded.tourId, stopIds: seeded.stopIds, checks: { rounding: roundingId!, test: testId!, ship: idsOf(1)[0]! } } satisfies Seed;
});

async function checksWorld(): Promise<{ world: World } & Seed> {
   const world = await backend().world({ template: checks });
   return { world, ...(world.seed as Seed) };
}

async function boundWorld(): Promise<{ world: World; claude: ClaudeSession } & Seed> {
   const seeded = await checksWorld();
   return { ...seeded, claude: await attachClaude(seeded.world, seeded.tourId) };
}

// Opens the tour at the first stop, on the transport when one is given.
async function open(world: World, seed: Seed, transport?: Transport): Promise<{ app: LotsenApp; transport: Transport }> {
   const started = await startApp(world, { transport });
   await openTour(started.app);
   await openStop(started.app, seed.stopIds[0]!);
   await stopPanel().findByText('Decide the rounding.');
   return started;
}

// Stores the reviewer's answer to a check of the first stop, as another view does, from a suggestion or in the reviewer's own words, with the type when it is given.
async function answer(world: World, seed: Seed, checkId: string, text: string, options: { fromSuggestion?: boolean; type?: AnswerType } = {}): Promise<void> {
   await world.reviewer.call('answer_check', { tourId: seed.tourId, stopId: seed.stopIds[0]!, checkId, text, fromSuggestion: options.fromSuggestion ?? false });
   if (options.type !== undefined) {
      await world.reviewer.call('set_answer_type', { tourId: seed.tourId, stopId: seed.stopIds[0]!, checkId, type: options.type });
   }
}

// Writes the first stop again with the checks, as Claude's put_stop does.
async function rewrite(claude: ClaudeSession, seed: Seed, kept: Record<string, unknown>[]): Promise<void> {
   await claude.call('put_stop', { tourId: seed.tourId, stopId: seed.stopIds[0], ...cartContent('Decide the rounding.'), checks: kept });
}

async function stored(world: World, tourId: string): Promise<CheckAnswer[]> {
   return (await readTour(world, tourId)).answers;
}

// Sends the batch to Claude's session and has the session fetch it, as Claude does on the batch's event.
async function receive(claude: ClaudeSession, tourId: string): Promise<void> {
   const event = await claude.next('answers');
   await claude.call('fetch_answers', { tourId, batchId: event.batchId });
}

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));
const answerGroup = (number: number) => within(stopPanel().getByRole('group', { name: `Your answer to check ${number}` }));
const typeChip = (number: number) => answerGroup(number).getByRole('combobox', { name: 'Answer type' }) as HTMLSelectElement;
const ownAnswer = (number: number) => answerGroup(number).getByRole('textbox', { name: 'Your own answer' }) as HTMLTextAreaElement;
const clearIdle = (number: number) => waitFor(() => expect(answerGroup(number).getByRole('button', { name: 'Clear' }).getAttribute('aria-disabled')).toBe('false'));
const editButton = (number: number) => answerGroup(number).getByRole('button', { name: 'Edit' }) as HTMLButtonElement;
const saveButton = (number: number) => answerGroup(number).getByRole('button', { name: 'Save' }) as HTMLButtonElement;
const cancelButton = (number: number) => answerGroup(number).getByRole('button', { name: 'Cancel' }) as HTMLButtonElement;
const checkElement = (checkId: string) => document.querySelector<HTMLElement>(`[data-check-id="${checkId}"]`)!;
const groupElement = (number: number) => stopPanel().getByRole('group', { name: `Your answer to check ${number}` });
const typeWords = (number: number, words: string) => fireEvent.input(ownAnswer(number), { target: { value: words } });

// Opens editing once the form's last write has ended, because the form takes one write at a time and Edit waits for it.
async function edit(number: number): Promise<void> {
   await waitFor(() => expect(editButton(number).getAttribute('aria-disabled')).toBe('false'));
   await fireEvent.click(editButton(number));
   await waitFor(() => expect(ownAnswer(number)).toBeTruthy());
}

const expectCollapsed = (number: number) => {
   const group = answerGroup(number);
   expect(group.queryByRole('textbox')).toBeNull();
   expect(group.queryByRole('list', { name: 'Suggested answers' })).toBeNull();
   expect(group.queryByText(/Pick a suggested answer or write your own/)).toBeNull();
   expect(group.queryByRole('button', { name: 'Save' })).toBeNull();
   expect(group.queryByRole('button', { name: 'Cancel' })).toBeNull();
   expect(groupElement(number).hasAttribute('aria-describedby')).toBe(false);
   expect(group.getByRole('button', { name: 'Edit' })).toBeTruthy();
   expect(group.getByRole('button', { name: 'Clear' })).toBeTruthy();
};
const unsent = () => document.querySelector('[data-unsent-answers]')?.textContent ?? null;
const answerState = (number: number) => answerGroup(number).queryByText(/./, { selector: '[data-answer-state]' });
const topBar = () => within(screen.getAllByRole('banner')[0]!);
const sendButton = () => topBar().getByRole('button', { name: 'Send answers' }) as HTMLButtonElement;
const sendButtonGone = () => topBar().queryByRole('button', { name: 'Send answers' }) === null;
const sendRow = () => document.querySelector<HTMLElement>('[data-send-row]');
const rowButton = () => within(sendRow()!).getByRole('button', { name: 'Send answers' }) as HTMLButtonElement;
const sendRequests = (transport: Transport) => transport.requests.filter((request) => request.operation === 'send_answers');
const showNewVersion = async () => fireEvent.click(await screen.findByRole('button', { name: 'Show the new version' }));

describe('answering a check in the Stop panel', () => {
   it("offers the suggestions and a box for the reviewer's own words, and says what they are for, without answer types", async () => {
      const { world, ...seed } = await checksWorld();
      await open(world, seed);

      const group = answerGroup(1);
      expect(group.getAllByRole('button', { pressed: false }).map((button) => button.textContent)).toEqual(['Round per line.', 'Totals may drift by a cent.']);
      expect(group.getByRole('textbox', { name: 'Your own answer' })).toBeTruthy();
      expect(group.getByText(/Pick a suggested answer or write your own/)).toBeTruthy();
      expect(group.queryByRole('combobox', { name: 'Answer type' })).toBeNull();
      expect(stopPanel().getByRole('group', { name: 'Your answer to check 1' }).textContent).not.toMatch(/decision|concern|open question/i);
      expect(unsent()).toBeNull();
   });

   it("shows a picked suggestion as the answer with the suggestion's type on its chip, and counts it as unsent", async () => {
      const { world, ...seed } = await checksWorld();
      await open(world, seed);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Totals may drift by a cent.' }));

      await waitFor(() => expect(typeChip(1).value).toBe('concern'));
      expect(answerGroup(1).getByText('Totals may drift by a cent.', { selector: 'p' })).toBeTruthy();
      expect(answerGroup(1).getByText('Draft')).toBeTruthy();
      await waitFor(async () => expect(await stored(world, seed.tourId)).toMatchObject([{ checkId: seed.checks.rounding, text: 'Totals may drift by a cent.', type: 'concern', state: 'draft' }]));
      expect(unsent()).toBe('1 unsent answer');
   });

   it('shows written words as the answer with the decision chip, and empties the box of the words that became the answer', async () => {
      const { world, ...seed } = await checksWorld();
      await open(world, seed);

      await fireEvent.input(ownAnswer(2), { target: { value: 'It covers both scopes.' } });
      await fireEvent.click(answerGroup(2).getByRole('button', { name: 'Answer' }));

      await waitFor(() => expect(typeChip(2).value).toBe('decision'));
      expect(answerGroup(2).getByText('It covers both scopes.')).toBeTruthy();
      expect(await stored(world, seed.tourId)).toMatchObject([{ checkId: seed.checks.test, text: 'It covers both scopes.', type: 'decision' }]);
      await clearIdle(2);
      await fireEvent.click(answerGroup(2).getByRole('button', { name: 'Clear' }));
      await waitFor(() => expect(ownAnswer(2).value).toBe(''));
   });

   it('stores a type that the reviewer chooses on the chip and shows it', async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.test, 'Not sure.');
      await open(world, seed);
      await waitFor(() => expect(typeChip(2).value).toBe('decision'));

      await fireEvent.change(typeChip(2), { target: { value: 'openQuestion' } });

      await waitFor(async () => expect(await stored(world, seed.tourId)).toMatchObject([{ checkId: seed.checks.test, text: 'Not sure.', type: 'openQuestion' }]));
      expect(typeChip(2).value).toBe('openQuestion');
      expect(typeChip(2).selectedOptions[0]!.textContent).toBe('Open question');
   });

   it("shows the server's reason when it refuses the type of an answer that another view cleared meanwhile", async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.test, 'Not sure.');
      const { transport } = await open(world, seed);
      await waitFor(() => expect(typeChip(2).value).toBe('decision'));
      const release = transport.hold('set_answer_type', 'request');

      await fireEvent.change(typeChip(2), { target: { value: 'concern' } });
      await waitFor(() => expect(transport.requests.some((request) => request.operation === 'set_answer_type')).toBe(true));
      await world.reviewer.call('clear_answer', { tourId: seed.tourId, stopId: seed.stopIds[0]!, checkId: seed.checks.test });
      release();

      expect((await answerGroup(2).findByRole('alert')).textContent).toContain('has no answer');
      expect(await stored(world, seed.tourId)).toEqual([]);
   });

   it('shows the stored type again on the chip when the request to change it fails', async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.test, 'Not sure.');
      const { transport } = await open(world, seed);
      await waitFor(() => expect(typeChip(2).value).toBe('decision'));
      transport.fail('set_answer_type', { status: 500 });

      await fireEvent.change(typeChip(2), { target: { value: 'openQuestion' } });

      await answerGroup(2).findByRole('alert');
      expect(typeChip(2).value).toBe('decision');
   });

   it('shows the stored type on the chip when the reviewer changes it while another write of the form is in flight', async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { app, transport } = await open(world, seed);
      await waitFor(() => expect(typeChip(1).value).toBe('decision'));
      const release = transport.hold('answer_check');
      await edit(1);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Totals may drift by a cent.' }));
      await waitFor(() => expect(app.selectedStopAnswers.get(seed.checks.rounding)?.type).toBe('concern'));
      await fireEvent.change(typeChip(1), { target: { value: 'openQuestion' } });
      expect(typeChip(1).value).toBe('concern');
      release();

      await clearIdle(1);
      expect(typeChip(1).value).toBe('concern');
      expect(await stored(world, seed.tourId)).toMatchObject([{ checkId: seed.checks.rounding, type: 'concern' }]);
      expect(transport.requests.some((request) => request.operation === 'set_answer_type')).toBe(false);
   });

   it("shows the type of a later write from another view on the chip after the reviewer's change is stored", async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.test, 'Not sure.');
      await open(world, seed);
      await waitFor(() => expect(typeChip(2).value).toBe('decision'));

      await fireEvent.change(typeChip(2), { target: { value: 'openQuestion' } });
      await waitFor(async () => expect(await stored(world, seed.tourId)).toMatchObject([{ checkId: seed.checks.test, type: 'openQuestion' }]));
      await clearIdle(2);
      await world.clock.advance(60_000);
      await world.reviewer.call('set_answer_type', { tourId: seed.tourId, stopId: seed.stopIds[0]!, checkId: seed.checks.test, type: 'concern' });

      await waitFor(() => expect(typeChip(2).value).toBe('concern'));
   });

   it('keeps a type that the reviewer changed when the reviewer picks the chosen suggestion again', async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true, type: 'openQuestion' });
      const { transport } = await open(world, seed);
      await waitFor(() => expect(typeChip(1).value).toBe('openQuestion'));
      // The page writes its preferences on its own, so only the requests on the tour count.
      const tourRequests = () => transport.requests.filter((request) => request.input?.tourId !== undefined).length;
      const requests = tourRequests();
      await edit(1);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Round per line.' }));

      expect(tourRequests()).toBe(requests);
      expect(typeChip(1).value).toBe('openQuestion');
   });

   it('replaces the answer when the reviewer picks another suggestion, and clears it', async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await open(world, seed);
      await waitFor(() => expect(typeChip(1).value).toBe('decision'));
      await edit(1);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Totals may drift by a cent.' }));
      await waitFor(() => expect(typeChip(1).value).toBe('concern'));
      // The form takes one write at a time, so the clear waits until the pick's write ends.
      await clearIdle(1);
      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Clear' }));

      await waitFor(() => expect(answerGroup(1).queryByRole('combobox', { name: 'Answer type' })).toBeNull());
      expect(await stored(world, seed.tourId)).toEqual([]);
      expect(unsent()).toBeNull();
   });

   it('keeps the drafts when the reviewer moves to another stop and back, and after a reload', async () => {
      const { world, ...seed } = await checksWorld();
      const { app } = await open(world, seed);
      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Round per line.' }));
      await waitFor(() => expect(typeChip(1).value).toBe('decision'));
      await clearIdle(1);

      app.openStop(seed.stopIds[1]!);
      await stopPanel().findByText('Another stop.');
      expect(unsent()).toBe('1 unsent answer');
      app.openStop(seed.stopIds[0]!);
      await stopPanel().findByText('Decide the rounding.');
      expect(typeChip(1).value).toBe('decision');

      app.stop();
      cleanup();
      await open(world, seed);
      await waitFor(() => expect(typeChip(1).value).toBe('decision'));
      await edit(1);
      expect(answerGroup(1).getByRole('button', { name: 'Round per line.' }).getAttribute('aria-pressed')).toBe('true');
   });

   it("counts the unsent answers of the tour's current checks across its stops, but not one to a check that a rewrite dropped", async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.test, 'Yes.');
      await answer(world, seed, seed.checks.rounding, 'Dropped.');
      await world.reviewer.call('answer_check', { tourId: seed.tourId, stopId: seed.stopIds[1]!, checkId: seed.checks.ship, text: 'Ship it.' });
      await rewrite(claude, seed, [{ ...test, id: seed.checks.test }]);
      await open(world, seed);

      await waitFor(() => expect(unsent()).toBe('2 unsent answers'));
   });

   it('shows an answer that another view stores, and its clear, from their events', async () => {
      const { world, ...seed } = await checksWorld();
      await open(world, seed);

      await answer(world, seed, seed.checks.test, 'From another tab.', { type: 'concern' });

      await waitFor(() => expect(typeChip(2).value).toBe('concern'));
      expect(answerGroup(2).getByText('From another tab.')).toBeTruthy();
      await world.reviewer.call('clear_answer', { tourId: seed.tourId, stopId: seed.stopIds[0]!, checkId: seed.checks.test });
      await waitFor(() => expect(answerGroup(2).queryByRole('combobox', { name: 'Answer type' })).toBeNull());
   });

   it('keeps the unsent words and the refusal in the box of their own check when a rewrite of the stop moves the check', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await open(world, seed);
      // The server refuses an answer with a control character.
      const words = 'Words meant for rounding.\u0007';
      await fireEvent.input(ownAnswer(1), { target: { value: words } });
      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Answer' }));
      await answerGroup(1).findByRole('alert');

      await rewrite(claude, seed, [
         { ...test, id: seed.checks.test },
         { ...rounding, id: seed.checks.rounding },
      ]);
      // The stop under view holds the version that the reviewer read until the reviewer shows the new one.
      await showNewVersion();

      await waitFor(() => expect(answerGroup(1).queryByRole('button', { name: 'Round per line.' })).toBeNull());
      expect(ownAnswer(1).value).toBe('');
      expect(answerGroup(1).queryByRole('alert')).toBeNull();
      expect(ownAnswer(2).value).toBe(words);
      expect(answerGroup(2).getByRole('alert').textContent).toContain('control character');
   });

   it('drops the unsent words of a check that a rewrite of the stop removes, so they never become the answer to the check now in its place', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await open(world, seed);
      await fireEvent.input(ownAnswer(1), { target: { value: 'Words meant for rounding.' } });

      await rewrite(claude, seed, [{ ...test, id: seed.checks.test }]);
      // The stop under view holds the version that the reviewer read until the reviewer shows the new one.
      await showNewVersion();

      await waitFor(() => expect(answerGroup(1).queryByRole('button', { name: 'Round per line.' })).toBeNull());
      expect(ownAnswer(1).value).toBe('');
      expect((answerGroup(1).getByRole('button', { name: 'Answer' }) as HTMLButtonElement).disabled).toBe(true);
   });

   it("stores no answer to a check that Claude drops while the reviewer's pick is on its way, and holds the version that the reviewer read", async () => {
      const { world, claude, ...seed } = await boundWorld();
      const { transport } = await open(world, seed);
      const release = transport.hold('answer_check', 'request');

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Round per line.' }));
      await waitFor(() => expect(transport.requests.some((request) => request.operation === 'answer_check')).toBe(true));
      await rewrite(claude, seed, [{ ...test, id: seed.checks.test }]);
      const refused = transport.answered('answer_check');
      release();
      await refused;

      // The held version takes no answers, so the refusal has no form to show in.
      expect(await screen.findByRole('button', { name: 'Show the new version' })).toBeTruthy();
      expect(answerGroup(1).getByText('Not answered.')).toBeTruthy();
      expect(await stored(world, seed.tourId)).toEqual([]);
   });
});

describe('editing an answered check', () => {
   async function answeredWorld(type?: AnswerType) {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true, type });
      const { app, transport } = await open(world, seed);
      await waitFor(() => expect(typeChip(1)).toBeTruthy());
      return { world, seed, app, transport };
   }

   const answerRequests = (transport: Transport) => transport.requests.filter((request) => request.operation === 'answer_check').length;

   it('shows an answered check as its answer with Edit and Clear, without suggestions, a box or the hint', async () => {
      await answeredWorld();

      expectCollapsed(1);
      expect(answerGroup(1).getByText('Round per line.', { selector: 'p' })).toBeTruthy();
      expect(answerState(1)?.textContent).toBe('Draft');
   });

   it('collapses a check once the reviewer picks a suggestion, and moves focus to Edit', async () => {
      const { world, ...seed } = await checksWorld();
      await open(world, seed);
      const suggestion = answerGroup(1).getByRole('button', { name: 'Round per line.' });
      suggestion.focus();

      await fireEvent.click(suggestion);

      await waitFor(() => expectCollapsed(1));
      await waitFor(() => expect(document.activeElement).toBe(editButton(1)));
   });

   it('collapses a check once the reviewer writes an answer, and moves focus to Edit', async () => {
      const { world, ...seed } = await checksWorld();
      await open(world, seed);
      await typeWords(1, 'Half up.');
      const send = answerGroup(1).getByRole('button', { name: 'Answer' });
      send.focus();

      await fireEvent.click(send);

      await waitFor(() => expectCollapsed(1));
      await waitFor(() => expect(document.activeElement).toBe(editButton(1)));
   });

   const heldWrites = [
      { write: 'a first pick', answered: false, act: () => fireEvent.click(answerGroup(1).getByRole('button', { name: 'Round per line.' })) },
      {
         write: 'a first written answer',
         answered: false,
         act: async () => {
            await typeWords(1, 'Half up.');
            await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Answer' }));
         },
      },
      {
         write: 'a Save',
         answered: true,
         act: async () => {
            await typeWords(1, 'Round per invoice.');
            await fireEvent.click(saveButton(1));
         },
      },
      { write: 'a pick while editing', answered: true, act: () => fireEvent.click(answerGroup(1).getByRole('button', { name: 'Totals may drift by a cent.' })) },
   ];

   it.each((['request', 'response'] as const).flatMap((held) => heldWrites.map((write) => ({ held, ...write }))))(
      "leaves focus on another check's box when the reviewer moves there during $write, held at its $held",
      async ({ held, answered, act }) => {
         const { world, ...seed } = await checksWorld();
         if (answered) {
            await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
         }
         const { transport } = await open(world, seed);
         if (answered) {
            await waitFor(() => expect(typeChip(1)).toBeTruthy());
            await edit(1);
         }
         const release = transport.hold('answer_check', held);

         await act();
         await waitFor(() => expect(transport.requests.some((request) => request.operation === 'answer_check')).toBe(true));
         ownAnswer(2).focus();
         release();

         await waitFor(() => expect(editButton(1).getAttribute('aria-disabled')).toBe('false'));
         // The focus move comes after the write's own continuation, so the assertion waits for the queued work to end.
         await new Promise((resolve) => setTimeout(resolve));
         expect(document.activeElement).toBe(ownAnswer(2));
      },
   );

   it("moves focus to Edit when the reviewer is still on this check's card as the write ends", async () => {
      const { world, ...seed } = await checksWorld();
      const { transport } = await open(world, seed);
      const release = transport.hold('answer_check', 'response');

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Round per line.' }));
      await waitFor(() => expect(typeChip(1)).toBeTruthy());
      typeChip(1).focus();
      release();

      await waitFor(() => expect(document.activeElement).toBe(editButton(1)));
   });

   it('keeps the words in the box when the first written answer is refused', async () => {
      const { world, ...seed } = await checksWorld();
      await open(world, seed);
      const words = 'Refused words.\u0007';
      await typeWords(1, words);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Answer' }));

      await answerGroup(1).findByRole('alert');
      expect(ownAnswer(1).value).toBe(words);
   });

   it("opens editing with a focused box that holds the answer's words, the suggestions with the current one marked and no hint, and hides Clear", async () => {
      await answeredWorld();

      await edit(1);

      const group = answerGroup(1);
      expect(ownAnswer(1).value).toBe('Round per line.');
      await waitFor(() => expect(document.activeElement).toBe(ownAnswer(1)));
      expect(group.queryByText('Round per line.', { selector: 'p' })).toBeNull();
      expect(group.getByRole('button', { name: 'Round per line.' }).getAttribute('aria-pressed')).toBe('true');
      expect(group.getByRole('button', { name: 'Totals may drift by a cent.' }).getAttribute('aria-pressed')).toBe('false');
      expect(group.queryByRole('button', { name: 'Clear' })).toBeNull();
      expect(group.queryByRole('button', { name: 'Edit' })).toBeNull();
      expect(typeChip(1).value).toBe('decision');
      expect(answerState(1)?.textContent).toBe('Draft');
      expect(group.queryByText(/Pick a suggested answer or write your own/)).toBeNull();
      expect(groupElement(1).hasAttribute('aria-describedby')).toBe(false);
      const buttons = Array.from(groupElement(1).querySelectorAll('button')).map((button) => button.textContent);
      expect(buttons.slice(-2)).toEqual(['Cancel', 'Save']);
   });

   it("replaces the answer on Save, keeps the answer's type, closes editing and returns focus to Edit", async () => {
      const { world, seed } = await answeredWorld('concern');
      await edit(1);

      await typeWords(1, 'Round per invoice.');
      await fireEvent.click(saveButton(1));

      await waitFor(() => expectCollapsed(1));
      await waitFor(() => expect(document.activeElement).toBe(editButton(1)));
      expect(answerGroup(1).getByText('Round per invoice.', { selector: 'p' })).toBeTruthy();
      expect(await stored(world, seed.tourId)).toMatchObject([{ checkId: seed.checks.rounding, text: 'Round per invoice.', type: 'concern' }]);
   });

   it.each(['', '   \n '])('disables Save while the box holds %j, and writes nothing on the keyboard shortcut', async (words) => {
      const { transport } = await answeredWorld();
      await edit(1);
      const before = answerRequests(transport);

      await typeWords(1, words);
      expect(saveButton(1).disabled).toBe(true);
      await fireEvent.keyDown(ownAnswer(1), { key: 'Enter', ctrlKey: true });

      expect(answerRequests(transport)).toBe(before);
      expect(ownAnswer(1).value).toBe(words);
   });

   it.each(['Round per line.', '  Round per line.\n', 'Round  per\nline.'])('closes editing with no write when the words are unchanged apart from whitespace: %j', async (words) => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await world.reviewer.call('send_answers', { tourId: seed.tourId });
      await receive(claude, seed.tourId);
      const { transport } = await open(world, seed);
      await waitFor(() => expect(answerState(1)?.textContent).toBe('Received'));
      await edit(1);

      await typeWords(1, words);
      await fireEvent.click(saveButton(1));

      await waitFor(() => expectCollapsed(1));
      await waitFor(() => expect(document.activeElement).toBe(editButton(1)));
      expect(answerRequests(transport)).toBe(0);
      expect(answerState(1)?.textContent).toBe('Received');
   });

   it('replaces the answer on a pick, closes editing and returns focus to Edit', async () => {
      const { world, seed } = await answeredWorld();
      await edit(1);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Totals may drift by a cent.' }));

      await waitFor(() => expectCollapsed(1));
      await waitFor(() => expect(document.activeElement).toBe(editButton(1)));
      expect(await stored(world, seed.tourId)).toMatchObject([{ text: 'Totals may drift by a cent.', type: 'concern' }]);
   });

   it("closes editing with no write when the reviewer picks the current suggestion, and keeps the reviewer's type", async () => {
      const { transport } = await answeredWorld('openQuestion');
      await edit(1);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Round per line.' }));

      await waitFor(() => expectCollapsed(1));
      await waitFor(() => expect(document.activeElement).toBe(editButton(1)));
      expect(answerRequests(transport)).toBe(0);
      expect(typeChip(1).value).toBe('openQuestion');
   });

   it('closes editing on Cancel, leaves the answer as it was, returns focus to Edit and drops the words from the box', async () => {
      const { world, seed, transport } = await answeredWorld();
      await edit(1);

      await typeWords(1, 'Something else.');
      await fireEvent.click(cancelButton(1));

      await waitFor(() => expectCollapsed(1));
      await waitFor(() => expect(document.activeElement).toBe(editButton(1)));
      expect(answerRequests(transport)).toBe(0);
      expect(await stored(world, seed.tourId)).toMatchObject([{ text: 'Round per line.' }]);
      await edit(1);
      expect(ownAnswer(1).value).toBe('Round per line.');
   });

   it('leaves the box of a check empty when the reviewer clears its answer after an edit', async () => {
      await answeredWorld();
      await edit(1);
      await fireEvent.click(cancelButton(1));
      await clearIdle(1);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Clear' }));

      await waitFor(() => expect(ownAnswer(1).value).toBe(''));
   });

   it("keeps editing open with the words and the server's reason when it refuses a Save, and drops the reason on Cancel", async () => {
      await answeredWorld();
      await edit(1);
      const words = 'Refused words.\u0007';

      await typeWords(1, words);
      await fireEvent.click(saveButton(1));

      expect((await answerGroup(1).findByRole('alert')).textContent).toContain('control character');
      expect(ownAnswer(1).value).toBe(words);
      await fireEvent.click(cancelButton(1));
      await waitFor(() => expectCollapsed(1));
      expect(answerGroup(1).queryByRole('alert')).toBeNull();
   });

   it.each([
      {
         close: 'a Save of the original words',
         closeEdit: async () => {
            await typeWords(1, 'Round per line.');
            await fireEvent.click(saveButton(1));
         },
      },
      { close: 'a pick of the current suggestion', closeEdit: () => fireEvent.click(answerGroup(1).getByRole('button', { name: 'Round per line.' })) },
   ])("drops a refused Save's reason on $close", async ({ closeEdit }) => {
      await answeredWorld();
      await edit(1);
      await typeWords(1, 'Refused words.\u0007');
      await fireEvent.click(saveButton(1));
      await answerGroup(1).findByRole('alert');

      await closeEdit();

      await waitFor(() => expectCollapsed(1));
      expect(answerGroup(1).queryByRole('alert')).toBeNull();
   });

   it('keeps editing open when the reviewer tries to Cancel a Save in flight, so a refusal cannot lose the words', async () => {
      const { transport } = await answeredWorld();
      await edit(1);
      const release = transport.hold('answer_check', 'request');
      const words = 'Refused words.\u0007';

      await typeWords(1, words);
      await fireEvent.click(saveButton(1));
      await waitFor(() => expect(cancelButton(1).getAttribute('aria-disabled')).toBe('true'));
      await fireEvent.click(cancelButton(1));
      expect(ownAnswer(1).value).toBe(words);
      release();

      await answerGroup(1).findByRole('alert');
      expect(ownAnswer(1).value).toBe(words);
   });

   it('changes the type on the chip while the answer is collapsed', async () => {
      const { world, seed } = await answeredWorld();

      await fireEvent.change(typeChip(1), { target: { value: 'concern' } });

      await waitFor(async () => expect(await stored(world, seed.tourId)).toMatchObject([{ type: 'concern' }]));
      expectCollapsed(1);
   });

   it('drops a refusal of the chip when the reviewer opens editing', async () => {
      const { transport } = await answeredWorld();
      transport.fail('set_answer_type', { status: 500 });
      await fireEvent.change(typeChip(1), { target: { value: 'concern' } });
      await answerGroup(1).findByRole('alert');

      await edit(1);

      expect(answerGroup(1).queryByRole('alert')).toBeNull();
   });

   it("keeps the reviewer's words when another view changes the answer, and judges unchanged against the live answer", async () => {
      const { world, seed } = await answeredWorld();
      await edit(1);

      await answer(world, seed, seed.checks.rounding, 'From another tab.');
      await waitFor(async () => expect(await stored(world, seed.tourId)).toMatchObject([{ text: 'From another tab.' }]));
      await waitFor(() => expect(answerGroup(1).getByRole('button', { name: 'Round per line.' }).getAttribute('aria-pressed')).toBe('false'));
      expect(ownAnswer(1).value).toBe('Round per line.');

      await fireEvent.click(saveButton(1));

      await waitFor(() => expectCollapsed(1));
      expect(await stored(world, seed.tourId)).toMatchObject([{ text: 'Round per line.' }]);
   });

   it('writes nothing when the words in the box match what another view stored during the edit', async () => {
      const { world, seed, transport } = await answeredWorld();
      await edit(1);
      await typeWords(1, 'From another tab.');

      await answer(world, seed, seed.checks.rounding, 'From another tab.');
      await waitFor(() => expect(answerGroup(1).getByRole('button', { name: 'Round per line.' }).getAttribute('aria-pressed')).toBe('false'));
      await fireEvent.click(saveButton(1));

      await waitFor(() => expectCollapsed(1));
      expect(answerRequests(transport)).toBe(0);
   });

   it('falls back to Not answered with an empty box when another view clears the answer during an edit, and a later answer stays collapsed', async () => {
      const { world, seed } = await answeredWorld();
      await edit(1);
      await typeWords(1, 'Half-written words.');

      await world.reviewer.call('clear_answer', { tourId: seed.tourId, stopId: seed.stopIds[0]!, checkId: seed.checks.rounding });

      await waitFor(() => expect(answerGroup(1).queryByRole('combobox', { name: 'Answer type' })).toBeNull());
      expect(ownAnswer(1).value).toBe('');
      expect(answerGroup(1).getByRole('button', { name: 'Answer' })).toBeTruthy();
      expect(answerGroup(1).queryByRole('button', { name: 'Save' })).toBeNull();
      expect(answerGroup(1).getByText(/Pick a suggested answer or write your own/)).toBeTruthy();

      await answer(world, seed, seed.checks.rounding, 'Answered again.');

      await waitFor(() => expect(typeChip(1)).toBeTruthy());
      expectCollapsed(1);
   });

   it("shows the answer read-only while a revision takes the check's writes away, and resumes the edit on the new version", async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await open(world, seed);
      await waitFor(() => expect(typeChip(1)).toBeTruthy());
      await edit(1);
      await typeWords(1, 'Half-written words.');

      await rewrite(claude, seed, [
         { ...rounding, id: seed.checks.rounding },
         { ...test, id: seed.checks.test },
      ]);
      await screen.findByRole('button', { name: 'Show the new version' });

      const group = answerGroup(1);
      expect(group.queryByRole('textbox')).toBeNull();
      expect(group.queryByRole('button', { name: 'Save' })).toBeNull();
      expect(group.queryByRole('button', { name: 'Edit' })).toBeNull();
      expect(group.getByText('Round per line.', { selector: 'p' })).toBeTruthy();
      expect(groupElement(1).hasAttribute('aria-describedby')).toBe(false);

      await showNewVersion();

      await waitFor(() => expect(ownAnswer(1).value).toBe('Half-written words.'));
      expect(saveButton(1)).toBeTruthy();
   });
});

describe('sending the answers from the top bar', () => {
   it('sends every draft as one batch, shows each answer as sent and drops the count to zero, then shows them received once Claude fetches them', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await answer(world, seed, seed.checks.test, 'Yes.');
      const { transport } = await open(world, seed);
      await waitFor(() => expect(unsent()).toBe('2 unsent answers'));
      expect(answerState(1)?.getAttribute('data-answer-state')).toBe('draft');

      await fireEvent.click(sendButton());

      await waitFor(() => expect(answerState(1)?.textContent).toBe('Sent'));
      expect([answerState(1)?.getAttribute('data-answer-state'), answerState(2)?.getAttribute('data-answer-state')]).toEqual(['sent', 'sent']);
      expect(unsent()).toBeNull();
      // The server's controls close the send once they arrive, after the batch's own event.
      await waitFor(() => expect(sendButtonGone()).toBe(true));
      const answers = await stored(world, seed.tourId);
      const batchId = answers[0]!.batch!.id;
      expect(answers.map((entry) => [entry.state, entry.batch?.id])).toEqual([
         ['sent', batchId],
         ['sent', batchId],
      ]);
      expect(transport.requests.filter((request) => request.operation === 'send_answers')).toHaveLength(1);

      await receive(claude, seed.tourId);

      await waitFor(() => expect(answerState(1)?.textContent).toBe('Received'));
      expect(answerState(2)?.getAttribute('data-answer-state')).toBe('received');
   });

   it("disables Send with the server's reason in visible text when no session is attached, sends nothing on a click, and drafting still works", async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { transport } = await open(world, seed);
      await waitFor(() => expect(unsent()).toBe('1 unsent answer'));

      const button = sendButton();
      await waitFor(() => expect(button.disabled).toBe(true));
      const reason = document.getElementById(button.getAttribute('aria-describedby')!.split(' ')[0]!)!;
      await fireEvent.click(button);
      await fireEvent.input(ownAnswer(2), { target: { value: 'It covers both scopes.' } });
      await fireEvent.click(answerGroup(2).getByRole('button', { name: 'Answer' }));

      expect(reason.closest('.sr-only')).toBeNull();
      expect(reason.textContent).toMatch(/is attached to no Claude Code session/);
      await waitFor(() => expect(unsent()).toBe('2 unsent answers'));
      expect(transport.requests.some((request) => request.operation === 'send_answers')).toBe(false);
   });

   it.each(['unconfirmed', 'confirmed'] as const)('enables Send with no reason while a %s session is attached', async (level) => {
      const { world, ...seed } = await checksWorld();
      await attachClaude(world, seed.tourId, { confirm: level === 'confirmed' });
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await open(world, seed);
      await waitFor(() => expect(unsent()).toBe('1 unsent answer'));

      await waitFor(() => expect(sendButton().disabled).toBe(false));
      expect(sendButton().hasAttribute('aria-describedby')).toBe(false);
      expect(topBar().queryByText(/is attached to no Claude Code session/)).toBeNull();
      await fireEvent.click(sendButton());

      await waitFor(() => expect(answerState(1)?.textContent).toBe('Sent'));
   });

   it('shows neither Send nor its reason when no answer is unsent, with no session attached', async () => {
      const { world, ...seed } = await checksWorld();
      await open(world, seed);

      expect(unsent()).toBeNull();
      expect(sendButtonGone()).toBe(true);
      expect(topBar().queryByText(/is attached to no Claude Code session/)).toBeNull();
   });

   it("shows the server's reason when it refuses the send", async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { transport } = await open(world, seed);
      await waitFor(() => expect(sendButton().disabled).toBe(false));
      const release = transport.hold('send_answers', 'request');

      await fireEvent.click(sendButton());
      await waitFor(() => expect(transport.requests.some((request) => request.operation === 'send_answers')).toBe(true));
      // The session ends before the send reaches the server, which then has no session to send the batch to.
      await claude.drop();
      release();

      expect((await screen.findByRole('alert')).textContent).toContain('is attached to no Claude Code session');
      expect(answerState(1)?.getAttribute('data-answer-state')).toBe('draft');
   });

   it("keeps an answer received when the received event arrives before the send's response", async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { transport } = await open(world, seed);
      await waitFor(() => expect(sendButton().disabled).toBe(false));
      const release = transport.hold('send_answers');

      await fireEvent.click(sendButton());
      await waitFor(async () => expect((await stored(world, seed.tourId))[0]!.state).toBe('sent'));
      await waitFor(() => expect(answerState(1)?.textContent).toBe('Sent'));
      // The later clock makes the fetch newer than the held response.
      await world.clock.advance(60_000);
      await receive(claude, seed.tourId);
      await waitFor(() => expect(answerState(1)?.textContent).toBe('Received'));
      const answered = transport.answered('send_answers');
      release();

      await answered;
      await waitFor(() => expect(sendButtonGone()).toBe(true));
      expect(answerState(1)?.textContent).toBe('Received');
   });

   it('keeps an answer received when a load that started before the fetch answers after its event', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const transport = withHolds(world.transport());
      const { app } = await open(world, seed, transport);
      await waitFor(() => expect(sendButton().disabled).toBe(false));
      await fireEvent.click(sendButton());
      await waitFor(() => expect(answerState(1)?.textContent).toBe('Sent'));
      const load = transport.holdAnswered('read_state');
      transport.cutEventStreams();
      await load.answered;

      // The later clock makes the fetch newer than the held load.
      await world.clock.advance(60_000);
      await receive(claude, seed.tourId);
      await waitFor(() => expect(answerState(1)?.textContent).toBe('Received'));
      await load.release();
      await waitFor(() => expect(app.tourRecord(seed.tourId)?.planLoads).toBe(0));

      expect(answerState(1)?.textContent).toBe('Received');
   });

   it('shows a sent answer whose session is no longer attached as not received, counts it, and sends it again to the new session', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await world.reviewer.call('send_answers', { tourId: seed.tourId });
      await claude.drop();
      const next = await attachClaude(world, seed.tourId);
      await open(world, seed);

      await waitFor(() => expect(answerState(1)?.textContent).toBe('Not received'));
      expect(answerState(1)?.getAttribute('data-answer-state')).toBe('sent');
      expect(unsent()).toBe('1 unsent answer');
      await fireEvent.click(sendButton());

      await waitFor(() => expect(answerState(1)?.textContent).toBe('Sent'));
      expect((await stored(world, seed.tourId))[0]!.batch!.sessionId).toBe(next.sessionId);
      expect(unsent()).toBeNull();
   });

   it('makes a received answer a draft again when the reviewer changes it, and counts it', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await world.reviewer.call('send_answers', { tourId: seed.tourId });
      await receive(claude, seed.tourId);
      await open(world, seed);
      await waitFor(() => expect(answerState(1)?.textContent).toBe('Received'));
      expect(unsent()).toBeNull();
      await edit(1);

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Totals may drift by a cent.' }));

      await waitFor(() => expect(answerState(1)?.textContent).toBe('Draft'));
      expect(unsent()).toBe('1 unsent answer');
      await waitFor(async () => expect((await stored(world, seed.tourId))[0]).not.toHaveProperty('batch'));
   });
});

describe('the send row under the checks of a stop', () => {
   const openedRow = async (text: string) => {
      await waitFor(() => expect(sendRow()?.textContent).toContain(text));
      return sendRow()!;
   };

   it('shows no row before any answer, and the row with its count after the first', async () => {
      const { world, ...seed } = await boundWorld();
      await open(world, seed);
      expect(sendRow()).toBeNull();

      await fireEvent.click(answerGroup(1).getByRole('button', { name: 'Round per line.' }));

      await openedRow('1 answer waits to be sent.');
      expect(sendRow()!.compareDocumentPosition(groupElement(2)) & Node.DOCUMENT_POSITION_PRECEDING).toBeTruthy();
   });

   it("counts the tour's unsent answers, one on another stop included, as the top bar does", async () => {
      const { world, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await world.reviewer.call('answer_check', { tourId: seed.tourId, stopId: seed.stopIds[1]!, checkId: seed.checks.ship, text: 'Ship it.' });
      await open(world, seed);

      await openedRow('2 answers wait to be sent.');
      expect(unsent()).toBe('2 unsent answers');
   });

   it('sends one batch from the row, and the row goes away once nothing waits', async () => {
      const { world, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await answer(world, seed, seed.checks.test, 'Yes.');
      const { transport } = await open(world, seed);
      await openedRow('2 answers wait to be sent.');
      await waitFor(() => expect(rowButton().disabled).toBe(false));

      await fireEvent.click(rowButton());

      await waitFor(() => expect(answerState(1)?.textContent).toBe('Sent'));
      await waitFor(() => expect(sendRow()).toBeNull());
      expect(sendRequests(transport)).toHaveLength(1);
      const answers = await stored(world, seed.tourId);
      expect(new Set(answers.map((entry) => entry.batch?.id)).size).toBe(1);
   });

   it.each([
      { first: 'the row', second: 'the top bar', click: [() => rowButton(), () => sendButton()] },
      { first: 'the top bar', second: 'the row', click: [() => sendButton(), () => rowButton()] },
   ])('starts no second send from $second while a send from $first is in flight', async ({ click }) => {
      const { world, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { transport } = await open(world, seed);
      await openedRow('1 answer waits to be sent.');
      await waitFor(() => expect(rowButton().disabled).toBe(false));
      const release = transport.hold('send_answers', 'request');

      await fireEvent.click(click[0]!());
      await waitFor(() => expect(sendRequests(transport)).toHaveLength(1));
      await waitFor(() => expect(rowButton().getAttribute('aria-disabled')).toBe('true'));
      expect(sendButton().getAttribute('aria-disabled')).toBe('true');
      await fireEvent.click(click[1]!());
      release();

      await waitFor(() => expect(answerState(1)?.textContent).toBe('Sent'));
      expect(sendRequests(transport)).toHaveLength(1);
   });

   it('disables the button and shows the reason of a closed offer, and sends nothing on a click', async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { transport } = await open(world, seed);
      await openedRow('1 answer waits to be sent.');

      await waitFor(() => expect(rowButton().disabled).toBe(true));
      const reason = document.getElementById(rowButton().getAttribute('aria-describedby')!)!;
      expect(sendRow()!.contains(reason)).toBe(true);
      expect(reason.textContent).toMatch(/is attached to no Claude Code session/);
      await fireEvent.click(rowButton());

      expect(sendRequests(transport)).toHaveLength(0);
   });

   // The session ends before the send reaches the server, which then has no session to send the batch to.
   async function refusedSend(click: () => HTMLButtonElement): Promise<void> {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { transport } = await open(world, seed);
      await openedRow('1 answer waits to be sent.');
      await waitFor(() => expect(rowButton().disabled).toBe(false));
      const release = transport.hold('send_answers', 'request');

      await fireEvent.click(click());
      await waitFor(() => expect(sendRequests(transport)).toHaveLength(1));
      await claude.drop();
      release();
      await screen.findByRole('alert');
   }

   it("shows the server's reason in the row when it refuses a send from the row", async () => {
      await refusedSend(rowButton);

      expect(within(sendRow()!).getByRole('alert').textContent).toContain('is attached to no Claude Code session');
      expect(topBar().queryByRole('alert')).toBeNull();
   });

   it.each([
      { name: 'the row', start: () => rowButton(), starter: () => within(sendRow()!), other: () => topBar() },
      { name: 'the top bar', start: () => sendButton(), starter: () => topBar(), other: () => within(sendRow()!) },
   ])('shows a refused send at both controls, and only $name, which started it, alerts', async ({ start, starter, other }) => {
      const { world, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { transport } = await open(world, seed);
      await openedRow('1 answer waits to be sent.');
      await waitFor(() => expect(rowButton().disabled).toBe(false));
      transport.fail('send_answers', { status: 500 });

      await fireEvent.click(start());

      const reason = 'The answers could not be sent.';
      expect((await starter().findByRole('alert')).textContent).toBe(reason);
      expect(other().getByText(reason)).toBeTruthy();
      expect(other().queryByRole('alert')).toBeNull();
      expect(screen.getAllByRole('alert')).toHaveLength(1);
   });

   it('keeps the reason of a send from the row visible in the top bar when the reviewer moves to a stop without a row', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { app, transport } = await open(world, seed);
      await openedRow('1 answer waits to be sent.');
      await waitFor(() => expect(rowButton().disabled).toBe(false));
      const release = transport.hold('send_answers', 'request');
      await fireEvent.click(rowButton());
      await waitFor(() => expect(sendRequests(transport)).toHaveLength(1));

      app.openStop(seed.stopIds[2]!);
      await stopPanel().findByText('No checks here.');
      await claude.drop();
      release();

      // A closed offer shows the same words in muted text, so the selector picks the refusal.
      expect(await topBar().findByText(/is attached to no Claude Code session/, { selector: '.text-destructive' })).toBeTruthy();
      expect(sendRow()).toBeNull();
   });

   it('drops the reason at both controls when either starts a send', async () => {
      const { world, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { transport } = await open(world, seed);
      await openedRow('1 answer waits to be sent.');
      await waitFor(() => expect(sendButton().disabled).toBe(false));
      transport.fail('send_answers', { status: 500 });
      await fireEvent.click(sendButton());
      await topBar().findByRole('alert');
      const release = transport.hold('send_answers', 'request');

      await fireEvent.click(rowButton());

      await waitFor(() => expect(sendRequests(transport)).toHaveLength(2));
      expect(topBar().queryByText('The answers could not be sent.')).toBeNull();
      expect(within(sendRow()!).queryByText('The answers could not be sent.')).toBeNull();
      release();
      await waitFor(() => expect(answerState(1)?.textContent).toBe('Sent'));
   });

   describe('the focus when a send ends', () => {
      async function openedDraft() {
         const { world, ...seed } = await boundWorld();
         await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
         const { transport } = await open(world, seed);
         await openedRow('1 answer waits to be sent.');
         await waitFor(() => expect(rowButton().disabled).toBe(false));
         return { seed, transport };
      }

      it("moves the focus from the row's button to the last check when the send removes the row", async () => {
         const { seed } = await openedDraft();
         rowButton().focus();

         await fireEvent.click(rowButton());

         await waitFor(() => expect(sendRow()).toBeNull());
         await waitFor(() => expect((document.activeElement as HTMLElement).dataset.checkId).toBe(seed.checks.test));
      });

      it("keeps the focus on the row's button when the server refuses the send", async () => {
         const { transport } = await openedDraft();
         transport.fail('send_answers', { status: 500 });
         rowButton().focus();

         await fireEvent.click(rowButton());

         await within(sendRow()!).findByRole('alert');
         expect(document.activeElement).toBe(rowButton());
      });

      it('leaves the focus where the reviewer moved it when the send removes the row', async () => {
         await openedDraft();
         ownAnswer(2).focus();

         await fireEvent.click(rowButton());

         await waitFor(() => expect(sendRow()).toBeNull());
         await new Promise((resolve) => setTimeout(resolve));
         expect(document.activeElement).toBe(ownAnswer(2));
      });
   });

   it('shows no row on an earlier stop version, while the top bar keeps its control', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.test, 'Yes.');
      const { app } = await open(world, seed);
      await openedRow('1 answer waits to be sent.');
      await rewrite(claude, seed, [{ ...test, id: seed.checks.test }]);
      await showNewVersion();
      await waitFor(() => expect(sendRow()).not.toBeNull());

      app.openVersion(1);

      await waitFor(() => expect(answerGroup(2).queryByRole('button', { name: 'Edit' })).toBeNull());
      expect(sendRow()).toBeNull();
      expect(sendButtonGone()).toBe(false);
   });

   it('shows no row on a stop without checks', async () => {
      const { world, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { app } = await open(world, seed);
      await openedRow('1 answer waits to be sent.');

      app.openStop(seed.stopIds[2]!);

      await stopPanel().findByText('No checks here.');
      expect(sendRow()).toBeNull();
      expect(sendButtonGone()).toBe(false);
   });

   it('hides the row with a collapsed Checks section', async () => {
      const { world, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await open(world, seed);
      await openedRow('1 answer waits to be sent.');

      await fireEvent.click(stopPanel().getByRole('button', { name: 'Checks' }));

      await waitFor(() => expect(stopPanel().queryByRole('button', { name: 'Send answers' })).toBeNull());
   });

   it('shows the row in the inline explanation', async () => {
      localStorage.setItem('lotsen.diffLayout', 'inline');
      localStorage.setItem('lotsen.placement', 'inline');
      const { world, ...seed } = await boundWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      const { app } = await startApp(world);
      await openTour(app);
      await openStop(app, seed.stopIds[0]!);

      const block = await waitFor(() => {
         const found = document.querySelector<HTMLElement>('.editor section[aria-label="Explanation of Rounding"]');
         expect(found).not.toBeNull();
         return found!;
      });
      await waitFor(() => expect(block.querySelector('[data-send-row]')?.textContent).toContain('1 answer waits to be sent.'));
      expect(within(block).getByRole('button', { name: 'Send answers' })).toBeTruthy();
   });

   it('says nothing of the top bar in any check, unanswered or in edit mode', async () => {
      const { world, ...seed } = await checksWorld();
      await answer(world, seed, seed.checks.rounding, 'Round per line.', { fromSuggestion: true });
      await open(world, seed);
      await waitFor(() => expect(typeChip(1)).toBeTruthy());
      await edit(1);

      expect(groupElement(1).textContent).not.toMatch(/from the top bar/);
      expect(groupElement(2).textContent).not.toMatch(/from the top bar/);
      expect(answerGroup(2).getByText('Pick a suggested answer or write your own.')).toBeTruthy();
   });
});

describe('a thread that an answer led to', () => {
   it('shows its marker at the check and opens in the Threads panel', async () => {
      const { world, claude, ...seed } = await boundWorld();
      await open(world, seed);

      const words = 'Totals may drift by a cent.';
      const anchor = { kind: 'stopText', stopId: seed.stopIds[0], field: `checks[${seed.checks.rounding}].question`, text: rounding.question };
      await claude.call('start_thread', { tourId: seed.tourId, text: words, anchor });

      const marker = await within(checkElement(seed.checks.rounding)).findByRole('button', { name: `Open the thread: ${words}` });
      expect(within(checkElement(seed.checks.test)).queryByRole('button', { name: /^Open the thread/ })).toBeNull();

      await fireEvent.click(marker);

      await waitFor(() => expect(threadViewElement()?.textContent).toContain(words));
   });
});
