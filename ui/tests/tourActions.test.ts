import { describe, expect, it } from 'vitest';
import { diveReason, offeredFollowUps, stopActionReasons, stopsWithWaitingActions } from '../src/lib/controls';
import { defaultThreadFilter, filteredThreads, nowhere, revealing } from '../src/lib/threadFilters';
import { actionOf, waitingDives } from '../src/lib/threadView';
import { divePlaceholder } from '../src/lib/tourActions';
import type { QuestionThread, ThreadAnchor, TourAction, TourControls } from '../src/lib/types';
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

describe('the follow-up actions of the summary page', () => {
   it('are those that the controls offer, in their order, and none before the controls arrive', () => {
      const controls = eventFixtures.controlsChanged;
      const withoutComments = Object.fromEntries(Object.entries(controls.offers.followUps).filter(([kind]) => kind !== 'draftComments'));

      expect(offeredFollowUps(controls)).toEqual(['draftWorkItems', 'walkOpenQuestions', 'draftComments']);
      expect(offeredFollowUps({ ...controls, offers: { ...controls.offers, followUps: withoutComments } })).toEqual(['draftWorkItems', 'walkOpenQuestions']);
      expect(offeredFollowUps(null)).toEqual([]);
   });
});

describe('the dives', () => {
   const controls = eventFixtures.controlsChanged;
   const closed = { state: 'closed', code: 'noSessionAttached', reason: 'this tour is attached to no Claude Code session' } as const;
   const withDives = (dive: TourControls['offers']['dive']): TourControls => ({
      ...controls,
      offers: { ...controls.offers, ...(dive === undefined ? {} : { dive }), stops: { s1: { ...controls.offers.stops['s1']!, ...(dive === undefined ? {} : { dive }) } } },
   });

   it('are offered only where the controls hold a dive offer, with the reason of a closed one', () => {
      expect(diveReason({ state: 'open' })).toBeNull();
      expect(diveReason(closed)).toBe('This tour is attached to no Claude Code session');
      expect(diveReason(undefined)).toBeUndefined();
      expect(stopActionReasons(withDives({ state: 'open' }), 's1').dive).toBeNull();
      expect(stopActionReasons(withDives(closed), 's1').dive).toBe('This tour is attached to no Claude Code session');
      expect('dive' in stopActionReasons(withDives(undefined), 's1')).toBe(false);
      expect('dive' in stopActionReasons(null, 's1')).toBe(false);
   });

   it('take optional words from a stop only', () => {
      expect(divePlaceholder('Rounding')).toBe('Leave it empty and the dive covers the whole stop "Rounding".');
      expect(divePlaceholder(null)).toBeNull();
   });

   it('wait on the stop or the whole tour that they start from, while Claude has not replied', () => {
      const dive: TourAction = { kind: 'dive', purpose: 'orient', familiarity: 'some' };
      const waiting = (id: string, anchor: ThreadAnchor, action: TourAction = dive): QuestionThread => ({
         ...asked(id, action),
         anchor,
         status: { waiting: { questionId: `${id}-q`, requestId: `${id}-r`, sessionId: 'session-1' }, questions: [] },
      });
      const threads = [
         waiting('stop', { kind: 'stop', stopId: 's1' }),
         waiting('tour', { kind: 'tour' }),
         asked('answered', dive),
         waiting('explain', { kind: 'stop', stopId: 's1' }, { kind: 'explain', stopVersion: 1 }),
      ];

      expect(waitingDives(threads, 's1').map((thread) => thread.id)).toEqual(['stop']);
      expect(waitingDives(threads, 's2')).toEqual([]);
      expect(waitingDives(threads, null).map((thread) => thread.id)).toEqual(['tour']);
   });
});
