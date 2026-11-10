import { describe, expect, it } from 'vitest';
import { planFormSend, type PlanFormState } from '../src/lib/planForm';

const open: PlanFormState = { current: 'some', selected: 'some', words: '', askAgain: false, replanReason: null, levelReason: null };

describe('what Send does in the Re-plan form', () => {
   it('sends the replan at the current level with words', () => {
      expect(planFormSend({ ...open, words: 'Fewer stops.' })).toEqual({ send: 'replan' });
   });

   it('sends the level at another level, with or without words', () => {
      expect(planFormSend({ ...open, selected: 'new' })).toEqual({ send: 'level', level: 'new' });
      expect(planFormSend({ ...open, selected: 'new', words: 'Keep it short.' })).toEqual({ send: 'level', level: 'new' });
   });

   it('sends the current level at the current level without words when the controls say to ask Claude again', () => {
      expect(planFormSend({ ...open, askAgain: true })).toEqual({ send: 'level', level: 'some' });
   });

   it('sends the replan at the current level with words, whatever asks again, because the words ask for something specific', () => {
      expect(planFormSend({ ...open, askAgain: true, words: 'Fewer stops.' })).toEqual({ send: 'replan' });
   });

   it('sends nothing at the current level without words, and gives no reason', () => {
      expect(planFormSend(open)).toEqual({ send: null, reason: null });
      expect(planFormSend({ ...open, words: '  \n' })).toEqual({ send: null, reason: null });
   });

   it("holds the replan back with the server's reason while its offer is closed", () => {
      expect(planFormSend({ ...open, words: 'Fewer stops.', replanReason: 'A replan waits.' })).toEqual({ send: null, reason: 'A replan waits.' });
   });

   it("holds the level back with the server's reason while its offer is closed", () => {
      expect(planFormSend({ ...open, selected: 'new', levelReason: 'A change waits.' })).toEqual({ send: null, reason: 'A change waits.' });
      expect(planFormSend({ ...open, askAgain: true, levelReason: 'A change waits.' })).toEqual({ send: null, reason: 'A change waits.' });
   });

   it('sends the level while the replan offer is closed, and the replan while the level offer is closed', () => {
      expect(planFormSend({ ...open, selected: 'new', replanReason: 'A replan waits.' })).toEqual({ send: 'level', level: 'new' });
      expect(planFormSend({ ...open, words: 'Fewer stops.', levelReason: 'A change waits.' })).toEqual({ send: 'replan' });
   });
});
