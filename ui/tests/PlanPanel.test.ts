import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import PlanPanel from '../src/components/PlanPanel.svelte';
import type { Plan, Stop } from '../src/lib/types';

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
