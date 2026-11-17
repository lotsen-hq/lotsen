import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import PlanPanel from '../src/components/PlanPanel.svelte';
import type { Offer, Plan, Stop, TourControls } from '../src/lib/types';
import { eventFixtures } from './contract/fixtures.generated';

function stop(id: string, title: string, kind: Stop['kind'], status: Stop['status'], chapter: string): Stop {
   return status === 'pending'
      ? { id, title, kind, chapter, stage: 'pending', anchorHint: null, status }
      : { id, title, kind, chapter, stage: 'written', status, version: status === 'ready' ? 1 : 2, earlier: [], content: { headline: title, passages: [], html: { headline: title } } };
}

const plan: Plan = {
   chapters: [
      { id: 'a', title: 'What the change does' },
      { id: 'b', title: 'Before you approve' },
   ],
   stops: [
      stop('s1', 'The checkout at a glance', 'overview', 'ready', 'a'),
      stop('s2', 'Gateway errors reach the customer', 'risk', 'pending', 'a'),
      stop('s3', 'Does anything post the old form?', 'question', 'revised', 'b'),
   ],
   fileMarks: [],
};

function renderPlan(currentStopId: string | null = null, attachment: 'confirmed' | 'none' = 'confirmed') {
   const onopen = vi.fn();
   const result = render(PlanPanel, { props: { tourSelected: true, known: { plan }, error: null, attachment, currentStopId, onopen, changed: new Set(['s3']) } });
   const rows = () => screen.getAllByRole('button');
   const cells = (row: HTMLElement) => [...row.querySelectorAll('[data-cell]')].map((cell) => cell.textContent.replace(/\s+/g, ' ').trim());
   return { ...result, rows, cells, onopen };
}

describe('the plan list', () => {
   it('shows each stop’s number, kind and title, numbered across the chapters, under its chapter heading', () => {
      const { rows, cells } = renderPlan();

      expect(screen.getAllByRole('heading').map((heading) => heading.textContent.trim())).toEqual(['What the change does', 'Before you approve']);
      expect(rows().map(cells)).toEqual([
         ['1', 'Overview', 'The checkout at a glance'],
         ['2', 'Risk', 'Gateway errors reach the customer', 'Writing'],
         ['3', 'Question', 'Does anything post the old form?', 'Revised'],
      ]);
   });

   it('shows no status for a ready stop, and marks a stop being written and its title', () => {
      const { rows } = renderPlan();
      const [ready, pending, revised] = rows();

      expect(ready!.querySelector('.status')).toBeNull();
      expect(within(pending!).getByText('Writing').closest('.status')).not.toBeNull();
      expect(pending!.querySelector('.title.writing')).not.toBeNull();
      expect(ready!.querySelector('.title.writing')).toBeNull();
      expect(within(revised!).getByText('Revised').closest('.status')).not.toBeNull();
   });

   it('says a pending stop is not written yet, with no spinner or dot, while no session is attached', () => {
      const { rows } = renderPlan(null, 'none');
      const pending = rows()[1]!;

      expect(within(pending).getByText('Not written yet').closest('.status')).not.toBeNull();
      expect(within(pending).queryByText('Writing')).toBeNull();
      expect(pending.querySelector('.spinner')).toBeNull();
      expect(pending.querySelector('.dot')).toBeNull();
      expect(pending.getAttribute('aria-label')).toBe('2 Risk: Gateway errors reach the customer, Not written yet');
   });

   it('keeps the dot on a revised stop while no session is attached', () => {
      const { rows } = renderPlan(null, 'none');

      expect(rows()[2]!.querySelector('.dot')).not.toBeNull();
   });

   it('marks the open stop with aria-current="step", and only that stop', async () => {
      const { rows, rerender } = renderPlan('s2');

      expect(rows().map((row) => row.getAttribute('aria-current'))).toEqual([null, 'step', null]);

      await rerender({ currentStopId: 's3' });

      expect(rows().map((row) => row.getAttribute('aria-current'))).toEqual([null, null, 'step']);
   });

   it('gives the open stop’s row the classes of any other row plus `current`', () => {
      const { rows } = renderPlan('s2');
      const classes = (row: HTMLElement) => [...row.classList].sort();

      expect(classes(rows()[1]!).filter((name) => name !== 'current')).toEqual(classes(rows()[0]!));
      expect(rows()[1]!.classList.contains('current')).toBe(true);
   });

   it('names each row with its number, kind, title and status, with the kind and the title apart', () => {
      const { rows } = renderPlan();

      expect(rows()).toEqual([
         screen.getByRole('button', { name: '1 Overview: The checkout at a glance' }),
         screen.getByRole('button', { name: '2 Risk: Gateway errors reach the customer, Writing' }),
         screen.getByRole('button', { name: '3 Question: Does anything post the old form?, Revised' }),
      ]);
   });

   it('opens a stop on a click', async () => {
      const { rows, onopen } = renderPlan();

      await fireEvent.click(rows()[2]!);

      expect(onopen).toHaveBeenCalledExactlyOnceWith('s3');
   });
});

describe('a plan with parts', () => {
   const parted: Plan = {
      parts: [
         { id: 'p1', title: 'The data model' },
         { id: 'p2', title: 'The API' },
      ],
      chapters: [
         { id: 'a', title: 'The tables', part: 'p1' },
         { id: 'b', title: 'The migrations', part: 'p1' },
         { id: 'c', title: 'The endpoints', part: 'p2' },
      ],
      stops: [
         stop('s1', 'The orders table', 'inspect', 'ready', 'a'),
         stop('s2', 'The new index', 'inspect', 'ready', 'b'),
         stop('s3', 'The backfill', 'risk', 'ready', 'b'),
         stop('s4', 'The order endpoint', 'inspect', 'ready', 'c'),
      ],
      fileMarks: [],
   };

   function renderParted(folded: ReadonlySet<string> = new Set()) {
      const onfold = vi.fn();
      const result = render(PlanPanel, { props: { tourSelected: true, known: { plan: parted }, error: null, attachment: 'confirmed' as const, folded, onfold } });
      const partButton = (title: string) => screen.getByRole('button', { name: title });
      const stopNames = () => screen.queryAllByRole('button', { name: /^\d+ / }).map((row) => row.getAttribute('aria-label'));
      return { ...result, onfold, partButton, stopNames };
   }

   it('puts each part’s heading above its chapters’ headings, one level higher', () => {
      renderParted();

      expect(screen.getAllByRole('heading').map((heading) => [heading.tagName, heading.textContent.trim()])).toEqual([
         ['H3', 'The data model'],
         ['H4', 'The tables'],
         ['H4', 'The migrations'],
         ['H3', 'The API'],
         ['H4', 'The endpoints'],
      ]);
   });

   it('shows a plain heading for each part, and every part unfolded, while the page offers no folds', () => {
      render(PlanPanel, { props: { tourSelected: true, known: { plan: parted }, error: null, attachment: 'confirmed' as const, folded: new Set(['p1']) } });

      expect(screen.queryByRole('button', { name: 'The data model' })).toBeNull();
      expect(screen.getAllByRole('heading', { level: 3 }).map((heading) => heading.textContent.trim())).toEqual(['The data model', 'The API']);
      expect(screen.getByRole('button', { name: /^1 Inspect: The orders table/ })).toBeTruthy();
   });

   it('shows every part unfolded at first, with every stop', () => {
      const { partButton, stopNames } = renderParted();

      expect(partButton('The data model').getAttribute('aria-expanded')).toBe('true');
      expect(partButton('The API').getAttribute('aria-expanded')).toBe('true');
      expect(stopNames()).toEqual(['1 Inspect: The orders table', '2 Inspect: The new index', '3 Risk: The backfill', '4 Inspect: The order endpoint']);
   });

   it('folds an unfolded part on a click, and unfolds a folded one', async () => {
      const { partButton, onfold, rerender } = renderParted();

      await fireEvent.click(partButton('The data model'));
      expect(onfold).toHaveBeenLastCalledWith('p1', true);

      await rerender({ folded: new Set(['p1']) });
      await fireEvent.click(partButton('The data model'));
      expect(onfold).toHaveBeenLastCalledWith('p1', false);
   });

   it('shows only the heading of a folded part, without its chapters and stops', () => {
      const { partButton } = renderParted(new Set(['p1']));

      expect(partButton('The data model').getAttribute('aria-expanded')).toBe('false');
      expect(screen.getAllByRole('heading').map((heading) => heading.textContent.trim())).toEqual(['The data model', 'The API', 'The endpoints']);
   });

   it('numbers the stops across the whole tour, those of a folded part included', () => {
      const { stopNames } = renderParted(new Set(['p1']));

      expect(stopNames()).toEqual(['4 Inspect: The order endpoint']);
   });
});

describe('a plan without parts', () => {
   it('renders as before: chapter headings at the top level and no fold buttons', () => {
      const { container } = renderPlan();

      expect(screen.getAllByRole('heading').map((heading) => heading.tagName)).toEqual(['H3', 'H3']);
      expect(container.querySelector('[aria-expanded]')).toBeNull();
      expect([...container.querySelectorAll('ol.plan > li > h3')].map((heading) => heading.textContent.trim())).toEqual(['What the change does', 'Before you approve']);
   });
});

describe("a waiting plan's request for the focus", () => {
   /** The fixture tour's controls, with the plan's approval as the offer says. */
   function controlsWith(approvePlan: Offer): TourControls {
      const { tourId, revision, offers } = structuredClone(eventFixtures.controlsChanged);
      return { tourId, revision, offers: { ...offers, approvePlan } };
   }

   function renderProposal(approvePlan: Offer, focusRequests = 0) {
      const props = {
         tourSelected: true,
         known: { plan },
         error: null,
         attachment: 'confirmed',
         familiarity: 'some',
         planWaits: true,
         onapprove: async () => null,
         controls: controlsWith(approvePlan),
      } as const;
      const result = render(PlanPanel, { props: { ...props, focusRequests } });
      return { ...result, request: (count: number) => result.rerender({ ...props, focusRequests: count }) };
   }

   it('moves the focus to Approve plan', async () => {
      const { request } = renderProposal({ state: 'open' });

      await request(1);

      await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Approve plan' })));
   });

   it('moves the focus to the proposal while Approve plan is disabled', async () => {
      const { request } = renderProposal({ state: 'closed', code: 'noSessionAttached', reason: 'The tour is attached to no Claude Code session' });
      expect((screen.getByRole('button', { name: 'Approve plan' }) as HTMLButtonElement).disabled).toBe(true);

      await request(1);

      await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('region', { name: 'Plan proposal' })));
   });

   it('leaves the focus where it is for a request made before the proposal mounted', async () => {
      renderProposal({ state: 'open' }, 2);

      // Two frames pass, so a focus that the proposal moved after its first frame would show.
      await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));

      expect(document.activeElement).toBe(document.body);
   });
});

describe('a dive from the whole tour', () => {
   const inMenu = { hidden: true } as const;

   function renderDive(onaction: (action: { kind: string }, text: string) => Promise<string | null>) {
      const { tourId, revision, offers } = structuredClone(eventFixtures.controlsChanged);
      const controls: TourControls = { tourId, revision, offers: { ...offers, dive: { state: 'open' } } };
      render(PlanPanel, { props: { tourSelected: true, known: { plan }, error: null, attachment: 'confirmed', familiarity: 'some', purpose: 'orient', controls, onaction } });
      return within(screen.getByRole('region', { name: 'Plan actions' }));
   }

   async function choose(actions: ReturnType<typeof within>, name: string): Promise<void> {
      await fireEvent.keyDown(actions.getByRole('button', { name: 'Plan actions' }), { key: 'Enter' });
      await fireEvent.click(within(await screen.findByRole('menu', inMenu)).getByRole('menuitem', { name, hidden: true }));
      await waitFor(() => expect(screen.queryByRole('menu', inMenu)).toBeNull());
   }

   it('holds the ⋯ menu back while the dive is on its way, then closes only its own form', async () => {
      let release: (refusal: string | null) => void = () => {};
      const onaction = vi.fn(() => new Promise<string | null>((resolve) => (release = resolve)));
      const actions = renderDive(onaction);
      await choose(actions, 'Dive…');
      await fireEvent.input(actions.getByRole('textbox', { name: 'What should the dive cover?' }), { target: { value: 'The cache.' } });

      await fireEvent.click(actions.getByRole('button', { name: 'Send to Claude' }));

      expect(onaction).toHaveBeenCalledExactlyOnceWith({ kind: 'dive', purpose: 'orient', familiarity: 'some' }, 'The cache.');
      expect((actions.getByRole('button', { name: 'Plan actions' }) as HTMLButtonElement).disabled).toBe(true);
      release(null);
      await waitFor(() => expect(actions.queryByRole('textbox', { name: 'What should the dive cover?' })).toBeNull());
      expect((actions.getByRole('button', { name: 'Plan actions' }) as HTMLButtonElement).disabled).toBe(false);
   });

   it('drops the last refusal when the reviewer chooses Dive… again', async () => {
      const actions = renderDive(async () => 'The tour is not pinned to a commit yet.');
      await choose(actions, 'Dive…');
      await fireEvent.input(actions.getByRole('textbox', { name: 'What should the dive cover?' }), { target: { value: 'The cache.' } });
      await fireEvent.click(actions.getByRole('button', { name: 'Send to Claude' }));
      expect(await actions.findByRole('alert')).toBeTruthy();

      await choose(actions, 'Dive…');

      expect(actions.queryByRole('alert')).toBeNull();
      expect((actions.getByRole('textbox', { name: 'What should the dive cover?' }) as HTMLTextAreaElement).value).toBe('The cache.');
   });
});
