import { describe, expect, it } from 'vitest';
import { generationStatus, type GenerationFacts } from '../src/lib/generationStatus';
import type { Plan, Stop } from '../src/lib/types';

function stop(index: number, status: Stop['status']): Stop {
   const id = `s${index}`;
   const title = `Stop ${index}`;
   return status === 'pending'
      ? { id, title, kind: 'inspect', chapter: 'c', stage: 'pending', anchorHint: null, status }
      : { id, title, kind: 'inspect', chapter: 'c', stage: 'written', status, version: status === 'ready' ? 1 : 2, earlier: [], content: { headline: title, passages: [], html: { headline: title } } };
}

function plan(...statuses: Stop['status'][]): Plan {
   return { chapters: [{ id: 'c', title: 'Chapter' }], stops: statuses.map((status, index) => stop(index + 1, status)), fileMarks: [] };
}

const pinned: GenerationFacts['pin'] = { state: 'pinned', mergeBase: 'base', head: 'head', iteration: 1, remoteUrl: 'https://platform.test/shop.git' };

// An attached session writing a pinned tour whose first of three stops is written.
function facts(overrides: Partial<GenerationFacts> = {}): GenerationFacts {
   return { target: 'pullRequest', state: 'generating', pin: pinned, plan: plan('ready', 'pending', 'pending'), planWaits: false, attachment: 'confirmed', ...overrides };
}

const writing = (line: string) => ({ phase: 'writing', line });

describe('the generation status of a tour', () => {
   it('is absent while the page has not loaded the plan, whatever the session', () => {
      expect(generationStatus(facts({ plan: undefined }))).toBeNull();
      expect(generationStatus(facts({ plan: undefined, attachment: 'none' }))).toBeNull();
   });

   it('is absent for an archived tour, even with pending stops and no session', () => {
      expect(generationStatus(facts({ state: 'archived' }))).toBeNull();
      expect(generationStatus(facts({ state: 'archived', attachment: 'none', planWaits: true }))).toBeNull();
   });

   it('is absent for a plan with no pending stop, even with no session', () => {
      expect(generationStatus(facts({ plan: plan('ready', 'revised') }))).toBeNull();
      expect(generationStatus(facts({ plan: plan('ready', 'revised'), attachment: 'none' }))).toBeNull();
   });

   it('asks for the approval of a waiting plan', () => {
      expect(generationStatus(facts({ plan: plan('pending', 'pending'), planWaits: true }))).toEqual({ phase: 'awaitingApproval', line: 'The plan waits for your approval' });
   });

   it('asks for the approval of a waiting plan with no session', () => {
      expect(generationStatus(facts({ plan: plan('pending', 'pending'), planWaits: true, attachment: 'none' }))).toEqual({ phase: 'awaitingApproval', line: 'The plan waits for your approval' });
   });

   it('is absent with no session, with a plan or without, because the status chip says that the tour has none', () => {
      expect(generationStatus(facts({ attachment: 'none' }))).toBeNull();
      expect(generationStatus(facts({ attachment: 'none', plan: null }))).toBeNull();
   });

   it('shows reading and planning as one line until a plan exists, whatever the pin', () => {
      const planning = { phase: 'planning', line: 'Claude is reading the PR and planning the tour…' };

      expect(generationStatus(facts({ plan: null }))).toEqual(planning);
      expect(generationStatus(facts({ plan: null, pin: { state: 'pinning' }, attachment: 'unconfirmed' }))).toEqual(planning);
   });

   it('says that the tour is pinning while a plan has pending stops', () => {
      expect(generationStatus(facts({ pin: { state: 'pinning' } }))).toEqual({ phase: 'pinning', line: 'Pinning the pull request to its commits…' });
   });

   it('names the repository for a repository tour while it plans and pins', () => {
      expect(generationStatus(facts({ target: 'repository', plan: null }))).toEqual({ phase: 'planning', line: 'Claude is reading the repository and planning the tour…' });
      expect(generationStatus(facts({ target: 'repository', pin: { state: 'pinning' } }))).toEqual({ phase: 'pinning', line: 'Pinning the repository to its commit…' });
   });

   it('shows the writing line for a repository tour at its snapshot', () => {
      expect(generationStatus(facts({ target: 'repository', pin: { state: 'snapshot', head: 'head', remoteUrl: 'https://platform.test/shop.git' } }))).toEqual(
         writing('Claude is writing stop 2 of 3…'),
      );
   });

   it('is absent when the pin failed and a plan has pending stops', () => {
      expect(generationStatus(facts({ pin: { state: 'failed', reason: 'The clone failed.' } }))).toBeNull();
   });

   it('takes N from the number of stops that are not pending, plus one', () => {
      expect(generationStatus(facts({ plan: plan('pending', 'pending') }))).toEqual(writing('Claude is writing stop 1 of 2…'));
      expect(generationStatus(facts({ plan: plan('ready', 'revised', 'pending', 'pending') }))).toEqual(writing('Claude is writing stop 3 of 4…'));
   });

   it('still shows the writing line for a tour in progress with pending stops', () => {
      expect(generationStatus(facts({ state: 'inProgress', plan: plan('ready', 'ready', 'pending', 'pending', 'pending') }))).toEqual(writing('Claude is writing stop 3 of 5…'));
   });
});
