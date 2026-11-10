import { describe, expect, it } from 'vitest';
import { awaitsSending } from '../src/lib/answers';
import type { CheckAnswer, StopCheck, StopContent } from '../src/lib/types';
import { withEarlierVersions, type KnownVersion } from '../src/lib/versions';

function content(headline: string, checks: StopCheck[], how?: string): StopContent {
   return {
      headline,
      passages: [],
      checks,
      ...(how === undefined ? {} : { how }),
      html: { headline: `<p>${headline}</p>\n`, checks: checks.map((check) => ({ question: `<p>${check.question}</p>\n` })), ...(how === undefined ? {} : { how: `<p>${how}</p>\n` }) },
   };
}

function answer(checkId: string, overrides: Partial<CheckAnswer> = {}): CheckAnswer {
   return { stopId: 's-q', checkId, text: 'An answer.', type: 'decision', state: 'draft', savedAt: '2026-10-28T10:00:00+00:00', ...overrides };
}

function version(stopId: string, number: number): KnownVersion {
   return { stopId, number, title: 'Rounding', kind: 'question', content: content(`Version ${number}.`, []), replacedAt: '2026-10-28T10:00:00+00:00' };
}

describe('the earlier versions that the page knows', () => {
   it('merge loads and events in any order, each version once, oldest first for each stop', () => {
      const fromEvent = withEarlierVersions([], [version('s-q', 2)]);
      const merged = withEarlierVersions(fromEvent, [version('s-q', 1), version('s-o', 1), version('s-q', 2)]);

      expect(merged.map((known) => [known.stopId, known.number])).toEqual([
         ['s-q', 1],
         ['s-q', 2],
         ['s-o', 1],
      ]);
      expect(withEarlierVersions(withEarlierVersions([], [version('s-q', 1), version('s-o', 1)]), [version('s-q', 2)])).toEqual(merged);
   });

   it('never hold an outdated answer for the next send', () => {
      expect(awaitsSending(answer('chk-drop', { outdatedOn: 1 }), null)).toBe(false);
      expect(awaitsSending(answer('chk-drop', { state: 'sent', batch: { id: 'b', sessionId: 'gone', sentAt: '2026-10-28T10:00:00+00:00' }, outdatedOn: 1 }), 'bound')).toBe(false);
   });
});
