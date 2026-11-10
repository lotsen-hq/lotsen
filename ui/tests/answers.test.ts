import { describe, expect, it } from 'vitest';
import { awaitsSending, stopAnswers, unsentAnswerCount } from '../src/lib/answers';
import { hasAnswersToSend } from '../src/lib/controls';
import { TourRecord } from '../src/lib/tourRecord.svelte';
import type { CheckAnswer, Plan, StopCheck, StopContent, Tour } from '../src/lib/types';
import { outputFixtures } from './contract/fixtures.generated';

const roundingCheck: StopCheck = {
   id: 'chk-rnd',
   question: 'Is rounding per line right?',
   suggestions: [
      { text: 'Round per line.', type: 'decision' },
      { text: 'Totals may drift by a cent.', type: 'concern' },
   ],
};

const testCheck: StopCheck = { id: 'chk-tst', question: 'Is the test enough?' };

function content(headline: string, checks: StopCheck[]): StopContent {
   return {
      headline,
      passages: [],
      checks,
      html: { headline: `<p>${headline}</p>\n`, checks: checks.map((check) => ({ question: `<p>${check.question}</p>\n` })) },
   };
}

const plan: Plan = {
   chapters: [{ id: 'c', title: 'Checkout' }],
   stops: [
      {
         id: 's-q',
         title: 'Rounding',
         kind: 'question',
         chapter: 'c',
         stage: 'written',
         status: 'ready',
         version: 1,
         earlier: [],
         content: content('Decide the rounding.', [roundingCheck, testCheck]),
      },
      {
         id: 's-o',
         title: 'Overview',
         kind: 'question',
         chapter: 'c',
         stage: 'written',
         status: 'ready',
         version: 1,
         earlier: [],
         content: content('Another stop.', [{ id: 'chk-other', question: 'Ship it?' }]),
      },
   ],
   fileMarks: [],
};

// The fixture tour with this file's plan, whose loads differ in their answers and revision alone.
const tour: Tour = { ...outputFixtures.read_state, plan };

function answer(checkId: string, text: string, overrides: Partial<CheckAnswer> = {}): CheckAnswer {
   return { stopId: 's-q', checkId, text, type: 'decision', state: 'draft', savedAt: '2026-10-28T10:00:00+00:00', ...overrides };
}

describe('the answers that the page knows', () => {
   const first = answer('chk-rnd', 'Round per line.');
   const changed = answer('chk-rnd', 'Round per line.', { type: 'concern' });
   const recordOf = (answers: CheckAnswer[], revision: number) => {
      const record = new TourRecord('t');
      record.applyLoad({ ...tour, answers, revision });
      return record;
   };
   const shown = (record: TourRecord) => [...stopAnswers(record.answers, 's-q').values()];

   it('keep a write over an older load that the write overtook', () => {
      const record = recordOf([], 3);
      record.putAnswer(changed, 5);
      record.applyLoad({ ...tour, answers: [first], revision: 4 });

      expect(shown(record)).toEqual([changed]);
   });

   it('keep a clear over an older load or event that still holds the answer', () => {
      const record = recordOf([first], 1);
      record.clearAnswer('s-q', 'chk-rnd', 5);
      record.applyLoad({ ...tour, answers: [first], revision: 4 });
      record.putAnswer(changed, 4);

      expect(shown(record)).toEqual([]);
   });

   it('drop an answer that a newer load lacks, because the answer was cleared before the load', () => {
      const record = recordOf([first], 1);
      record.applyLoad({ ...tour, answers: [], revision: 5 });

      expect(shown(record)).toEqual([]);
   });

   it("take a newer event over a write's older response", () => {
      const record = recordOf([], 1);
      record.putAnswer(changed, 5);
      record.putAnswer(first, 3);

      expect(shown(record)).toEqual([changed]);
   });

   it('count only the drafts to checks that the plan holds now', () => {
      const answers = [first, answer('chk-tst', 'Yes.'), answer('chk-gone', 'Dropped by a rewrite.')];

      expect(unsentAnswerCount(answers, plan, null)).toBe(2);
      expect(unsentAnswerCount(answers, null, null)).toBe(0);
   });

   it('count a sent answer only while the session that its batch went to is not the bound session, and never a received one', () => {
      const batch = { id: 'b1', sessionId: 'session-1', sentAt: '2026-10-28T10:00:02+00:00' };
      const sent = answer('chk-rnd', 'Round per line.', { state: 'sent', batch });
      const received = answer('chk-tst', 'Yes.', { state: 'received', batch: { ...batch, receivedAt: '2026-10-28T10:00:03+00:00' } });
      const answers = [sent, received];

      expect(unsentAnswerCount(answers, plan, 'session-1')).toBe(0);
      expect(unsentAnswerCount(answers, plan, 'session-2')).toBe(1);
      expect(unsentAnswerCount(answers, plan, null)).toBe(1);
      expect([awaitsSending(sent, 'session-1'), awaitsSending(received, null)]).toEqual([false, false]);
   });

   it('keep a received answer over an older load or response that holds it as sent', () => {
      const batch = { id: 'b1', sessionId: 'session-1', sentAt: '2026-10-28T10:00:02+00:00' };
      const sent = answer('chk-rnd', 'Round per line.', { state: 'sent', batch });
      const received = answer('chk-rnd', 'Round per line.', { state: 'received', batch: { ...batch, receivedAt: '2026-10-28T10:00:05+00:00' } });
      const record = recordOf([], 1);
      record.putAnswer(received, 5);
      record.applyLoad({ ...tour, answers: [sent], revision: 2 });
      record.putAnswer(sent, 2);

      expect(shown(record)).toEqual([received]);
   });
});

describe('the send control of the answers', () => {
   it('shows while the server says a send holds an answer, also when the binding refuses the send', () => {
      expect(hasAnswersToSend({ state: 'open' })).toBe(true);
      expect(hasAnswersToSend({ state: 'closed', code: 'noSessionAttached', reason: 'this tour is attached to no Claude Code session.' })).toBe(true);
      expect(hasAnswersToSend({ state: 'closed', code: 'noAnswersToSend', reason: 'this tour has no answer to send.' })).toBe(false);
      expect(hasAnswersToSend(undefined)).toBe(false);
   });
});
