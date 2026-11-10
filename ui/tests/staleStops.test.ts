import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import ExplanationBlock from '../src/components/ExplanationBlock.svelte';
import type { StaleStatus } from '../src/lib/openStop.svelte';
import type { WrittenStop } from '../src/lib/types';
import { outputFixtures } from './contract/fixtures.generated';
import { explanationOf } from './explanation';

// The server's iteration update to iteration 2, which names s4.
const update = outputFixtures.update_iteration;
const pin = update.pin as Extract<typeof update.pin, { state: 'pinned' }>;
const earlierHead = 'b'.repeat(40);

const reference = (side: 'base' | 'head') => `<a data-ref-side="${side}" data-ref-path="a.ts" data-ref-start-line="1" data-ref-end-line="1">the ${side} line</a>`;

/** Stop s4, stale since iteration 2, with its passage at the head of iteration 1 and links to both sides. */
const stale: WrittenStop = {
   id: 's4',
   title: 'Cart adds',
   kind: 'inspect',
   chapter: 'c',
   stage: 'written',
   status: 'ready',
   version: 1,
   earlier: [],
   staleOn: 2,
   content: {
      headline: 'Cart adds.',
      passages: [{ side: 'head', path: 'a.ts', startLine: 1, endLine: 1, role: 'primary', commit: earlierHead, text: 'const a = 2;' }],
      html: { headline: `<p>${reference('head')} and ${reference('base')}</p>\n` },
   },
};

function renderInline(status: StaleStatus): void {
   const { view } = explanationOf(stale, { mode: 'inline', fields: { stale: status }, page: { commits: { base: pin.mergeBase, head: pin.head } } });
   render(ExplanationBlock, { props: { view, host: 'inline' } });
}

describe('a stale stop in the inline explanation', () => {
   it('shows its marker, saying that Claude is revising it while the update waits', () => {
      renderInline({ kind: 'stale', since: 2, target: 2, revising: true, earlierPassages: true });

      expect(screen.getByRole('status', { name: 'Stale stop' }).textContent).toMatch(/Stale since update 2: Claude is revising this stop for update 2\. Its passages are lines from before update 2, /);
   });

   it('names the update that the tour moves to, which a stop that stayed stale across a later update differs from', () => {
      renderInline({ kind: 'stale', since: 2, target: 3, revising: true, earlierPassages: true });

      expect(screen.getByRole('status', { name: 'Stale stop' }).textContent).toMatch(/Stale since update 2: Claude is revising this stop for update 3\. Its passages are lines from before update 2, /);
   });

   it("says the new update while the page does not know the number of the tour's pin", () => {
      renderInline({ kind: 'stale', since: 2, target: null, revising: false, earlierPassages: false });

      expect(screen.getByRole('status', { name: 'Stale stop' }).textContent).toMatch(/this stop waits for Claude to revise it for the new update\./);
   });

   it('marks its notice as a warning', () => {
      renderInline({ kind: 'stale', since: 2, target: 2, revising: false, earlierPassages: false });

      const notice = screen.getByRole('status', { name: 'Stale stop' });
      expect(notice.textContent).toMatch(/Stale since update 2: this stop waits for Claude to revise it for update 2\./);
      expect(notice.querySelector('svg[aria-hidden="true"]')).not.toBeNull();
   });

   it('reads its ref: links as at an earlier commit', () => {
      renderInline({ kind: 'stale', since: 2, target: 2, revising: false, earlierPassages: true });

      expect(screen.getByRole('link', { name: 'the head line, at an earlier commit' }).getAttribute('aria-disabled')).toBe('true');
   });
});
