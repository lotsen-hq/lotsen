import { describe, expect, it } from 'vitest';
import { stopsWithWaitingActions } from '../src/lib/controls';
import { defaultThreadFilter, filteredThreads, nowhere, revealing } from '../src/lib/threadFilters';
import { actionOf } from '../src/lib/threadView';
import type { QuestionThread, TourAction } from '../src/lib/types';
import { eventFixtures } from './contract/fixtures.generated';

const time = '2026-10-29T10:00:00+00:00';

function asked(id: string, action?: TourAction): QuestionThread {
   return {
      kind: 'asked',
      id,
      anchor: { kind: 'stop', stopId: 's1' },
      state: 'open',
      messages: [{ kind: 'question', id: `${id}-q`, createdAt: time, text: action === undefined ? 'Why?' : '', requests: [{ id: `${id}-r`, state: 'pending', sessionId: 'session-1', sentAt: time }] }],
      status: { questions: [] },
      ...(action === undefined ? {} : { action }),
   };
}

const explain = asked('a1', { kind: 'explain', stopVersion: 1 });
const question = asked('plain');

describe('the threads of tour actions', () => {
   it('are the threads whose first question holds an action', () => {
      const threads = [explain, asked('a2', { kind: 'revise', stopVersion: 1 }), question];

      expect(threads.map((thread) => actionOf(thread)?.kind ?? null)).toEqual(['explain', 'revise', null]);
   });

   it("wait on the stops that the controls' waiting work names", () => {
      expect([...stopsWithWaitingActions(eventFixtures.controlsChanged.offers.pending)]).toEqual(['s3']);
      expect(stopsWithWaitingActions(undefined).size).toBe(0);
   });

   it('show again when the reviewer opens one while the filter hides them', () => {
      const hidden = { ...defaultThreadFilter, actions: false };

      expect(revealing(hidden, explain, nowhere).actions).toBe(true);
      expect(revealing(hidden, question, nowhere)).toEqual(hidden);
      expect(filteredThreads([explain, question], hidden, nowhere).map((thread) => thread.id)).toEqual(['plain']);
   });
});
