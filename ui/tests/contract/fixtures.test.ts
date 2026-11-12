import { cleanup, render, screen, waitFor } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/App.svelte';
import { createApi, operationPath, type Api } from '../../src/lib/api';
import { LotsenApp } from '../../src/lib/app.svelte';
import { diffLayoutPreference } from '../../src/lib/diffLayout';
import type { EventName, OperationName, Operations, Stop, Tour } from '../../src/lib/types';
import { tourSwitcherName } from '../tourPicker';
import { eventFixtures, outputFixtures } from './fixtures.generated';

// Every fixture is of the one tour that ContractSamples builds.
const fixtureTour: Tour = outputFixtures.read_state;
const tourId = fixtureTour.id;

type Outputs = { [N in OperationName]: Operations[N]['output'] };

/** The page's network to no server: a replay of the golden fixtures. */
interface FixtureReplay {
   readonly fetch: typeof fetch;
   /** The operations that the page called, in the order called. */
   readonly calls: readonly OperationName[];
   /** Sends the event's fixture on every open event stream. */
   emit(name: EventName): void;
}

/**
 * Returns a replay that answers each operation with its output fixture, or with the output that the test gives in its place, and serves the event fixtures on the event stream. It holds no rule
 * of the server's: an operation on a tour answers in the response body with the revision and the controls of the `controlsChanged` fixture, which is the fixtures' sample of the controls.
 */
function replay(outputs: Partial<Outputs> = {}): FixtureReplay {
   const encoder = new TextEncoder();
   const streams = new Set<ReadableStreamDefaultController<Uint8Array>>();
   const calls: OperationName[] = [];
   const { revision } = eventFixtures.controlsChanged;
   const fetchFn: typeof fetch = async (input, init) => {
      const path = String(input);
      if (path === '/api/events') {
         let opened!: ReadableStreamDefaultController<Uint8Array>;
         const body = new ReadableStream<Uint8Array>({
            start: (controller) => {
               opened = controller;
               streams.add(controller);
               controller.enqueue(encoder.encode(`event: hello\ndata: ${JSON.stringify(eventFixtures.hello)}\n\n`));
            },
            cancel: () => void streams.delete(opened),
         });
         init?.signal?.addEventListener('abort', () => streams.delete(opened), { once: true });
         return new Response(body, { headers: { 'Content-Type': 'text/event-stream' } });
      }

      const name = (Object.keys(outputFixtures) as OperationName[]).find((candidate) => path === operationPath(candidate));
      if (name === undefined) {
         return new Response(null, { status: 404 });
      }

      calls.push(name);
      const onTour = typeof init?.body === 'string' && 'tourId' in (JSON.parse(init.body) as object);
      const output = structuredClone(Object.hasOwn(outputs, name) ? outputs[name] : outputFixtures[name]);
      return Response.json(onTour ? { output, revision, controls: structuredClone(eventFixtures.controlsChanged) } : { output });
   };
   return {
      fetch: fetchFn,
      calls,
      emit(name) {
         const chunk = encoder.encode(`event: ${name}\ndata: ${JSON.stringify(eventFixtures[name])}\n\n`);
         for (const stream of streams) {
            stream.enqueue(chunk);
         }
      },
   };
}

let server: FixtureReplay;
let app: LotsenApp | undefined;

beforeEach(() => {
   sessionStorage.clear();
   sessionStorage.setItem('lotsen.uiCredential', 'credential');
   history.replaceState(null, '', '/');
   Element.prototype.scrollIntoView = () => {};
   return () => delete (Element.prototype as Partial<Element>).scrollIntoView;
});

afterEach(() => {
   app?.stop();
   app = undefined;
   cleanup();
});

/** Returns the fixture tour with the changes, one store before the events, as a fresh copy. */
function tourWith(change: (tour: Tour) => Partial<Tour> = () => ({})): Tour {
   const tour = structuredClone(fixtureTour);
   return { ...tour, revision: tour.revision - 1, ...change(tour) };
}

function replaced(tour: Tour, stopId: string, replace: (stop: Stop) => Stop): Partial<Tour> {
   return { plan: tour.plan && { ...tour.plan, stops: tour.plan.stops.map((stop) => (stop.id === stopId ? replace(stop) : stop)) } };
}

/** Starts a fresh app on a replay with the outputs in place of the fixtures, and opens the fixture tour unless the tour list is empty. */
async function startWith(outputs: Partial<Outputs> = {}): Promise<LotsenApp> {
   server = replay(outputs);
   const opened = new LotsenApp({ environment: { fetch: server.fetch, location, history, storage: sessionStorage }, retryDelaysMs: [5] });
   app = opened;
   render(App, { props: { app: opened } });
   await screen.findByRole('button', { name: tourSwitcherName });
   await waitFor(() => expect(opened.connection).toBe('live'));
   await waitFor(() => expect(opened.toursLoaded).toBe(true));
   if (opened.tours.length > 0) {
      opened.selectTour(tourId, 'wholePr');
      await waitFor(() => expect(opened.selectedPlan?.plan).toBeDefined());
   }

   return opened;
}

const startOn = (tour: Tour) => startWith({ read_state: tour });
const stopOf = (opened: LotsenApp, stopId: string) => opened.selectedPlan?.plan?.stops.find((stop) => stop.id === stopId);

describe('the event fixtures, through the handler of each event', () => {
   it('tourCreated lists the tour', async () => {
      const opened = await startWith({ list_tours: { tours: [] } });
      server.emit('tourCreated');
      await waitFor(() => expect(opened.tours.find((tour) => tour.id === tourId)?.label).toBe(eventFixtures.tourCreated.label));
   });

   it('planSet stores the plan', async () => {
      const opened = await startOn(tourWith(() => ({ plan: null })));
      server.emit('planSet');
      await waitFor(() => expect(opened.selectedPlan?.plan?.stops.map((stop) => stop.id)).toEqual(eventFixtures.planSet.plan.stops.map((stop) => stop.id)));
   });

   it('stopPut stores the stop with the HTML of its content', async () => {
      const opened = await startOn(tourWith((tour) => replaced(tour, 's1', ({ id, title, kind, chapter }) => ({ stage: 'pending', status: 'pending', id, title, kind, chapter, anchorHint: null }))));
      server.emit('stopPut');
      const { stop } = eventFixtures.stopPut;
      await waitFor(() => expect(stopOf(opened, 's1')).toMatchObject({ stage: 'written', content: { html: { headline: stop.stage === 'written' ? stop.content.html.headline : null } } }));
   });

   it('stopRevised stores the new version, the outdated answers and the moved threads', async () => {
      const opened = await startOn(
         tourWith((tour) => ({
            ...replaced(tour, 's2', (stop) => (stop.stage === 'written' ? { ...stop, version: 1, status: 'ready', content: stop.earlier[0]!.content, earlier: [] } : stop)),
            answers: [],
            threads: tour.threads.filter((thread) => thread.id !== 't2'),
         })),
      );
      server.emit('stopRevised');
      await waitFor(() => expect(stopOf(opened, 's2')).toMatchObject({ stage: 'written', version: 2 }));
      expect(opened.selectedAnswers).toEqual(eventFixtures.stopRevised.answers);
      expect(opened.selectedThreads.map((thread) => thread.id)).toContain('t2');
   });

   it('tourPinned stores the pin', async () => {
      const opened = await startOn(tourWith(() => ({ pin: { state: 'pinning' } })));
      server.emit('tourPinned');
      await waitFor(() => expect(opened.selectedTour?.pin).toEqual(eventFixtures.tourPinned.pin));
   });

   it('availableIterationChanged stores the available iteration', async () => {
      const opened = await startOn(tourWith(() => ({ availableIteration: undefined })));
      server.emit('availableIterationChanged');
      await waitFor(() => expect(opened.selectedAvailableIteration).toEqual(eventFixtures.availableIterationChanged.availableIteration));
   });

   it('tourBindingChanged stores the binding', async () => {
      const [row] = structuredClone(outputFixtures.list_tours.tours);
      const opened = await startWith({ list_tours: { tours: [{ ...row!, binding: undefined, revision: row!.revision - 1 }] } });
      expect(opened.selectedBinding).toBeNull();
      server.emit('tourBindingChanged');
      await waitFor(() => expect(opened.selectedBinding).toEqual(eventFixtures.tourBindingChanged.binding));
   });

   it('threadCreated adds the thread with the HTML of its agent message', async () => {
      const created = eventFixtures.threadCreated.thread;
      const opened = await startOn(tourWith((tour) => ({ threads: tour.threads.filter((thread) => thread.id !== created.id) })));
      server.emit('threadCreated');
      await waitFor(() => expect(opened.selectedThreads.find((thread) => thread.id === created.id)).toEqual(created));
   });

   it('threadUpdated replaces the thread', async () => {
      const updated = eventFixtures.threadUpdated.thread;
      const opened = await startOn(tourWith((tour) => ({ threads: tour.threads.filter((thread) => thread.id !== updated.id) })));
      server.emit('threadUpdated');
      await waitFor(() => expect(opened.selectedThreads.find((thread) => thread.id === updated.id)).toEqual(updated));
   });

   it('threadDeleted removes the thread', async () => {
      const opened = await startOn(tourWith());
      await waitFor(() => expect(opened.selectedThreads.map((thread) => thread.id)).toContain(eventFixtures.threadDeleted.threadId));
      server.emit('threadDeleted');
      await waitFor(() => expect(opened.selectedThreads.map((thread) => thread.id)).not.toContain(eventFixtures.threadDeleted.threadId));
   });

   it('answerUpdated stores the answer', async () => {
      const opened = await startOn(tourWith(() => ({ answers: [] })));
      server.emit('answerUpdated');
      await waitFor(() => expect(opened.selectedAnswers).toEqual([eventFixtures.answerUpdated.answer]));
   });

   it('answerCleared removes the answer', async () => {
      const { stopId, checkId } = eventFixtures.answerCleared;
      const opened = await startOn(tourWith());
      server.emit('answerCleared');
      await waitFor(() => expect(opened.selectedAnswers.some((answer) => answer.stopId === stopId && answer.checkId === checkId)).toBe(false));
   });

   it('answerBatchUpdated stores the sent answers', async () => {
      const opened = await startOn(tourWith(() => ({ answers: [] })));
      server.emit('answerBatchUpdated');
      await waitFor(() => expect(opened.selectedAnswers).toEqual(eventFixtures.answerBatchUpdated.answers));
   });

   it('skipsUpdated stores the skipped stops', async () => {
      const opened = await startOn(tourWith((tour) => replaced(tour, 's6', ({ skipped: _, ...stop }) => stop)));
      server.emit('skipsUpdated');
      await waitFor(() => expect([...opened.selectedSkippedStops]).toEqual(eventFixtures.skipsUpdated.stopIds));
   });

   it('seenVersionUpdated stores the seen version', async () => {
      const opened = await startOn(tourWith());
      expect([...opened.selectedChangedStops]).toEqual(['s2']);
      server.emit('seenVersionUpdated');
      await waitFor(() => expect([...opened.selectedChangedStops]).toEqual([]));
   });

   it('familiarityChanged stores the level', async () => {
      const opened = await startOn(tourWith(() => ({ familiarity: 'new' })));
      server.emit('familiarityChanged');
      await waitFor(() => expect(opened.selectedFamiliarity).toBe(eventFixtures.familiarityChanged.familiarity));
   });

   it('planApproved stores the approval', async () => {
      const opened = await startOn(tourWith(() => ({ planApproval: 'awaited' })));
      server.emit('planApproved');
      await waitFor(() => expect(opened.selectedTour?.planApproval).toBe(eventFixtures.planApproved.planApproval));
   });

   it('tourStateChanged stores the state', async () => {
      const [row] = structuredClone(outputFixtures.list_tours.tours);
      const opened = await startWith({ list_tours: { tours: [{ ...row!, state: 'ready', revision: row!.revision - 1 }] }, read_state: tourWith(() => ({ progress: undefined })) });
      expect(opened.selectedTour?.state).not.toBe(eventFixtures.tourStateChanged.state);
      server.emit('tourStateChanged');
      await waitFor(() => expect(opened.selectedTour?.state).toBe(eventFixtures.tourStateChanged.state));
   });

   it('synthesisWritten loads the open summary page again', async () => {
      const opened = await startOn(tourWith());
      const loads = () => server.calls.filter((name) => name === 'read_summary').length;
      opened.openSummary();
      await waitFor(() => expect(loads()).toBe(1));
      server.emit('synthesisWritten');
      await waitFor(() => expect(loads()).toBe(2));
   });

   it('tourDeleted drops the tour', async () => {
      const opened = await startOn(tourWith());
      server.emit('tourDeleted');
      await waitFor(() => expect(opened.tours.map((tour) => tour.id)).not.toContain(tourId));
   });

   it('preferenceChanged stores the preference', async () => {
      // The earlier tests' reads leave their preferences in browser storage, which the page keeps for a key that the server lacks.
      localStorage.clear();
      await startWith({ read_preferences: { preferences: { 'lotsen.theme': 'dark' } } });
      expect(diffLayoutPreference.read()).toBe('sideBySide');
      server.emit('preferenceChanged');
      await waitFor(() => expect(diffLayoutPreference.read()).toBe(eventFixtures.preferenceChanged.value));
   });
});

describe('the output fixtures', () => {
   /** One call of each operation that the UI calls, through the API method that calls it. */
   const calls = {
      read_state: (api) => api.readTour(tourId),
      read_summary: (api) => api.readSummary(tourId),
      ask_question: (api) => api.askQuestion(tourId, { text: 'Why?', anchor: { kind: 'tour' } }),
      request_tour_action: (api) => api.requestTourAction(tourId, { action: { kind: 'replan' }, text: 'Shorter.' }),
      ask_follow_up: (api) => api.askFollowUp(tourId, 't2', { text: 'And?' }),
      resend_question: (api) => api.resendQuestion(tourId, 'q-t2'),
      cancel_question: (api) => api.cancelQuestion(tourId, 'q-t2'),
      set_thread_state: (api) => api.setThreadState(tourId, 't2', 'resolved'),
      remove_thread: (api) => api.removeThread(tourId, 't2'),
      save_note: (api) => api.saveNote(tourId, { text: 'Mine.', anchor: { kind: 'tour' } }),
      edit_note: (api) => api.editNote(tourId, 't1', 'Mine.'),
      discard_note: (api) => api.discardNote(tourId, 't1'),
      send_notes: (api) => api.sendNotes(tourId, ['t1']),
      answer_check: (api) => api.answerCheck(tourId, 's3', 'c1', { text: 'Keep it.' }),
      set_answer_type: (api) => api.setAnswerType(tourId, 's3', 'c1', 'concern'),
      clear_answer: (api) => api.clearAnswer(tourId, 's3', 'c1'),
      send_answers: (api) => api.sendAnswers(tourId),
      list_tours: (api) => api.listTours(),
      list_changed_files: (api) => api.listChangedFiles(tourId),
      read_file_text: (api) => api.readFileText(tourId, 'head', 'src/a.cs'),
      diff_file: (api) => api.diffFile(tourId, 'src/a.cs'),
      list_directory: (api) => api.listDirectory(tourId, 'head', null),
      mark_file_reviewed: (api) => api.markFileReviewed(tourId, 'src/a.cs', null),
      clear_file_reviewed: (api) => api.clearFileReviewed(tourId, 'src/a.cs'),
      set_stop_skipped: (api) => api.setStopSkipped(tourId, 's6', true),
      record_reading_progress: (api) => api.recordProgress(tourId, { stopId: 's2' }),
      set_tour_archived: (api) => api.setTourArchived(tourId, true),
      delete_tour: (api) => api.deleteTour(tourId),
      prune_clones: (api) => api.pruneClones(),
      change_familiarity: (api) => api.changeFamiliarity(tourId, 'new', null),
      approve_plan: (api) => api.approvePlan(tourId, { stopIds: ['s1'], skippedStops: [], familiarity: 'some' }),
      check_iteration: (api) => api.checkIteration(tourId),
      update_iteration: (api) => api.updateIteration(tourId, { mergeBase: 'a'.repeat(40), head: 'b'.repeat(40), iteration: 2, remoteUrl: 'https://example.test/repo.git' }),
      read_preferences: (api) => api.readPreferences(),
      write_preference: (api) => api.writePreference('lotsen.diffLayout', 'inline'),
   } satisfies { [N in OperationName]: (api: Api) => Promise<unknown> };

   // The API returns nothing of a review mark write, whose output only the call's success matters for.
   const returnsNothing: ReadonlySet<OperationName> = new Set(['mark_file_reviewed', 'clear_file_reviewed']);
   // These API methods return the output with the tour's revision, for the page to merge by it.
   const revised: ReadonlySet<OperationName> = new Set([
      'ask_question',
      'request_tour_action',
      'ask_follow_up',
      'resend_question',
      'cancel_question',
      'set_thread_state',
      'remove_thread',
      'save_note',
      'edit_note',
      'discard_note',
      'send_notes',
      'answer_check',
      'set_answer_type',
      'clear_answer',
      'send_answers',
      'set_stop_skipped',
      'record_reading_progress',
      'change_familiarity',
      'approve_plan',
      'update_iteration',
   ]);

   it.each(Object.keys(calls) as OperationName[])('%s reaches its caller as the server wrote it', async (name) => {
      const api = createApi(replay().fetch, 'credential');
      const expected = returnsNothing.has(name) ? undefined : revised.has(name) ? { output: outputFixtures[name], revision: eventFixtures.controlsChanged.revision } : outputFixtures[name];
      await expect(calls[name](api)).resolves.toEqual(expected);
   });

   it('list_tours lists each tour with its label and state', async () => {
      const opened = await startWith({ list_tours: outputFixtures.list_tours });
      await waitFor(() => expect(opened.tours.map(({ id, label, state }) => ({ id, label, state }))).toEqual(outputFixtures.list_tours.tours.map(({ id, label, state }) => ({ id, label, state }))));
   });

   it('read_state shows each stop, thread and answer of the tour', async () => {
      const opened = await startWith();
      await waitFor(() => expect(opened.selectedPlan?.plan?.stops.map((stop) => stop.id)).toEqual(fixtureTour.plan!.stops.map((stop) => stop.id)));
      expect(opened.selectedThreads.map((thread) => thread.id)).toEqual(expect.arrayContaining(['t1', 't2', 't3', 't4', 't5']));
      expect(opened.selectedAnswers).toEqual(fixtureTour.answers);
      opened.openStop('s2');
      const stop = fixtureTour.plan!.stops.find((each) => each.id === 's2')!;
      await waitFor(() => expect(opened.selectedStop).toEqual(stop));
      // The stop's headline renders as the HTML that the server wrote for it.
      await waitFor(() => expect(document.body.innerHTML).toContain('<strong>stores</strong>'));
   });

   it('read_summary shows the synthesis and each group of the summary', async () => {
      const opened = await startWith();
      opened.openSummary();
      await waitFor(() => expect(opened.selectedSummary?.state).toBe('loaded'));
      expect(opened.selectedSummary).toMatchObject({ state: 'loaded', summary: outputFixtures.read_summary });
   });
});
