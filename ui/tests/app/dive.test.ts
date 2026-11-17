import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { diveNeedsWords } from '../../src/lib/tourActions';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { QuestionThread } from '../../src/lib/types';
import type { ClaudeSession, World } from '../backend/backend';
import { attachClaude, backend, cartContent, openStop, openTour, seedRepositoryTour, seedTour, startApp } from '../harness';
import { resetPageEachTest } from './appPanels';
import { chooseFromPlanMenu, closePlanMenu, isItemDisabled, openPlanMenu, planActions, planMenuNames } from './planMenu';
import { chooseFromStopMenu, expectNoStopMenuItem, expectStopMenuItem, openStopMenu, stopMenuButton } from './stopToolbar';

resetPageEachTest();

const title = 'How the cart adds up';
const passage = { side: 'head', path: 'src/cart.ts', startLine: 2, endLine: 2, role: 'primary' };

interface Seed {
   tourId: string;
   stopIds: string[];
}

// A prepare tour at the familiar level, with one written stop.
const repositoryTour = backend().template('dive-repository', async (world): Promise<Seed> => {
   const seeded = await seedRepositoryTour(world, {
      purpose: 'prepare',
      familiarity: 'familiar',
      bound: false,
      chapters: [{ title: 'Cart', stops: [{ title: 'Rounding', kind: 'context', content: { headline: 'Totals round once.', passages: [passage] } }] }],
   });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds };
});

const pullRequestTour = backend().template('dive-pullRequest', async (world): Promise<Seed> => {
   const seeded = await seedTour(world, { bound: false, chapters: [{ title: 'Cart', stops: [{ title: 'Totals', kind: 'inspect', content: cartContent('The total rounds.') }] }] });
   return { tourId: seeded.tourId, stopIds: seeded.stopIds };
});

/** Opens the repository tour with a bound session, on the Plan panel or on its stop. */
async function openRepositoryTour(at: 'plan' | 'stop'): Promise<{ app: LotsenApp; world: World; claude: ClaudeSession } & Seed> {
   const world = await backend().world({ template: repositoryTour });
   const seed = world.seed as Seed;
   const claude = await attachClaude(world, seed.tourId);
   const { app } = await startApp(world);
   await openTour(app, title);
   if (at === 'stop') {
      await openStop(app, seed.stopIds[0]!);
   } else {
      await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
   }

   return { app, world, claude, ...seed };
}

const stopPanel = () => within(screen.getByRole('region', { name: 'Stop' }));

/** The radio option of the form's group, by the group's label and the option's name. */
function option(scope: ReturnType<typeof within>, group: string, name: string): HTMLElement {
   return within(scope.getByRole('radiogroup', { name: group })).getByRole('radio', { name });
}

const isChecked = (radio: HTMLElement) => radio.getAttribute('aria-checked') === 'true';

/** Fetches the session's next question as Claude does, and returns its thread. */
async function fetchedThread(claude: ClaudeSession, tourId: string): Promise<QuestionThread> {
   const question = await claude.next('question');
   const fetched = (await claude.call('fetch_question', { tourId, questionId: question.questionId })) as { thread: QuestionThread };
   return fetched.thread;
}

describe('a dive from the whole tour', () => {
   it("sits in the Plan panel's ⋯ menu and presets the tour's purpose and level", async () => {
      await openRepositoryTour('plan');

      const menu = await openPlanMenu();
      expect(planMenuNames(menu)).toContain('Dive…');
      await closePlanMenu();
      await chooseFromPlanMenu('Dive…');

      expect(isChecked(option(planActions(), 'Purpose', 'Prepare'))).toBe(true);
      expect(isChecked(option(planActions(), 'Your familiarity', 'Familiar with the code'))).toBe(true);
      await waitFor(() => expect(document.activeElement).toBe(planActions().getByRole('textbox', { name: 'What should the dive cover?' })));
   });

   it('holds Send back with a reason until the reviewer writes words', async () => {
      await openRepositoryTour('plan');
      await chooseFromPlanMenu('Dive…');
      const send = planActions().getByRole('button', { name: 'Send to Claude' }) as HTMLButtonElement;

      expect(send.disabled).toBe(true);
      expect(planActions().getByRole('textbox', { name: 'What should the dive cover?' }).getAttribute('placeholder')).toBeNull();
      expect(document.getElementById(send.getAttribute('aria-describedby') ?? '')?.textContent).toBe(diveNeedsWords);

      await fireEvent.input(planActions().getByRole('textbox', { name: 'What should the dive cover?' }), { target: { value: 'The rounding rules.' } });

      expect(send.disabled).toBe(false);
      expect(planActions().queryByText(diveNeedsWords)).toBeNull();
   });

   it('reaches the session as a dive on the tour, with the purpose and level the reviewer picked and the words', async () => {
      const { claude, tourId } = await openRepositoryTour('plan');
      await chooseFromPlanMenu('Dive…');

      await fireEvent.click(option(planActions(), 'Purpose', 'Evaluate'));
      await fireEvent.click(option(planActions(), 'Your familiarity', 'New to the code'));
      await fireEvent.input(planActions().getByRole('textbox', { name: 'What should the dive cover?' }), { target: { value: 'The rounding rules.' } });
      await fireEvent.click(planActions().getByRole('button', { name: 'Send to Claude' }));

      const thread = await fetchedThread(claude, tourId);
      expect(thread).toMatchObject({ kind: 'asked', anchor: { kind: 'tour' }, action: { kind: 'dive', purpose: 'evaluate', familiarity: 'new' } });
      expect(thread.kind === 'asked' && thread.messages[0].text).toBe('The rounding rules.');
      await waitFor(() => expect(planActions().queryByRole('textbox', { name: 'What should the dive cover?' })).toBeNull());
      expect(await planActions().findByText('Claude is creating a dive.')).toBeTruthy();
   });
});

describe('a dive from a stop', () => {
   it("sits in the Stop panel's Ask Claude menu, and its words are optional, with a placeholder that names the stop", async () => {
      await openRepositoryTour('stop');

      await chooseFromStopMenu('Ask Claude', 'Dive…');

      const box = stopPanel().getByRole('textbox', { name: 'What should the dive cover? Optional' });
      expect(box.getAttribute('placeholder')).toBe('Leave it empty and the dive covers the whole stop "Rounding".');
      expect((stopPanel().getByRole('button', { name: 'Send to Claude' }) as HTMLButtonElement).disabled).toBe(false);
      expect(isChecked(option(stopPanel(), 'Purpose', 'Prepare'))).toBe(true);
      expect(isChecked(option(stopPanel(), 'Your familiarity', 'Familiar with the code'))).toBe(true);
      await waitFor(() => expect(document.activeElement).toBe(box));
   });

   it('reaches the session as a dive on the stop without words', async () => {
      const { claude, tourId, stopIds } = await openRepositoryTour('stop');
      await chooseFromStopMenu('Ask Claude', 'Dive…');

      await fireEvent.click(option(stopPanel(), 'Purpose', 'Orient'));
      await fireEvent.click(stopPanel().getByRole('button', { name: 'Send to Claude' }));

      const thread = await fetchedThread(claude, tourId);
      expect(thread).toMatchObject({ kind: 'asked', anchor: { kind: 'stop', stopId: stopIds[0] }, action: { kind: 'dive', purpose: 'orient', familiarity: 'familiar' } });
      expect(thread.kind === 'asked' && thread.messages[0].text).toBe('');
      expect(await stopPanel().findByText('Claude is creating a dive.')).toBeTruthy();
   });

   it('shows the Ask Claude menu without a form after Escape', async () => {
      await openRepositoryTour('stop');

      const menu = await openStopMenu('Ask Claude');
      expect(isItemDisabled(within(menu).getByRole('menuitem', { name: 'Dive…', hidden: true }))).toBe(false);
      await fireEvent.keyDown(menu, { key: 'Escape' });

      await waitFor(() => expect(document.activeElement).toBe(stopMenuButton('Ask Claude')));
      expect(stopPanel().queryByRole('textbox')).toBeNull();
   });
});

describe("a pull request's tour", () => {
   it('offers no dive in either menu', async () => {
      const world = await backend().world({ template: pullRequestTour });
      const { tourId, stopIds } = world.seed as Seed;
      await attachClaude(world, tourId);
      const { app } = await startApp(world);
      await openTour(app);
      await openStop(app, stopIds[0]!);

      await expectStopMenuItem('Ask Claude', 'Revise…');
      await expectNoStopMenuItem('Ask Claude', 'Dive…');
      await fireEvent.click(await screen.findByRole('tab', { name: 'Plan' }));
      const menu = await openPlanMenu();
      expect(planMenuNames(menu)).toEqual(['Re-plan…', 'Reorder…']);
      await closePlanMenu();
   });
});
