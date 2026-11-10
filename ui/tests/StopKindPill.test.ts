import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import ExplanationBlock from '../src/components/ExplanationBlock.svelte';
import PlanPanel from '../src/components/PlanPanel.svelte';
import StopPanel from '../src/components/StopPanel.svelte';
import type { Plan, Stop } from '../src/lib/types';
import { explanationOf } from './explanation';

const stop: Stop = { id: 's1', title: 'Gateway errors reach the customer', kind: 'risk', chapter: 'a', stage: 'pending', anchorHint: null, status: 'pending' };
const plan: Plan = { chapters: [{ id: 'a', title: 'Before you approve' }], stops: [stop], fileMarks: [] };

const pills = (container: HTMLElement) => [...container.querySelectorAll('[data-kind]')].map((pill) => ({ className: pill.className, kind: pill.getAttribute('data-kind'), text: pill.textContent }));

describe('a stop’s kind', () => {
   it('shows as the same pill in the Plan, the Stop panel and an inline explanation', () => {
      const inPlan = render(PlanPanel, { props: { tourSelected: true, known: { plan }, error: null, attachment: 'confirmed' } });
      const { view } = explanationOf(stop);
      const inPanel = render(StopPanel, { props: { stop, view, onopenpassage: () => {} } });
      const inline = render(ExplanationBlock, { props: { view, host: 'inline' } });

      const expected = [{ className: 'kind-pill', kind: 'risk', text: 'Risk' }];
      expect(pills(inPlan.container)).toEqual(expected);
      // The panel's header and its bar each show the pill.
      expect(pills(inPanel.container)).toEqual([...expected, ...expected]);
      expect(pills(inline.container)).toEqual(expected);
   });
});
