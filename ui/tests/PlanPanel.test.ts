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
