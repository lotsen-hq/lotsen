import { describe, expect, it, vi } from 'vitest';
import { ApiError, createApi, operationPath, proseHeader, UnauthorizedError } from '../src/lib/api';
import type { OperationError, TourControls } from '../src/lib/types';

function answering(status: number, body: unknown) {
   return vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => Response.json(body, { status }));
}

// Each call the fake received, as the operation's name and its whole input.
function calls(fetchFn: ReturnType<typeof answering>): [string, unknown][] {
   return fetchFn.mock.calls.map(([url, init]) => {
      expect(init?.method).toBe('POST');
      return [String(url).slice(operationPath('').length), JSON.parse(String(init?.body))];
   });
}

describe('the operation wire', () => {
   it("posts each call to its operation's path with the whole input as the body, the UI credential, and prose as HTML", async () => {
      const fetchFn = answering(200, { output: { id: 'thread-1' }, revision: 3 });
      const input = { text: 'Why?', anchor: { kind: 'stop' as const, stopId: 's1' }, stopId: 's1' };

      const thread = await createApi(fetchFn, 'token').askQuestion('tour/1', input);

      expect(thread).toEqual({ output: { id: 'thread-1' }, revision: 3 });
      const [url, init] = fetchFn.mock.calls[0]!;
      expect(url).toBe('/api/ops/ask_question');
      expect(init?.method).toBe('POST');
      expect(JSON.parse(String(init?.body))).toEqual({ tourId: 'tour/1', ...input });
      const headers = new Headers(init?.headers);
      expect(headers.get('Authorization')).toBe('Bearer token');
      expect(headers.get('Content-Type')).toBe('application/json');
      expect(headers.get(proseHeader)).toBe('html');
   });

   it('sends an empty object to an operation that takes no input', async () => {
      const fetchFn = answering(200, {});
      const api = createApi(fetchFn, 'token');

      await api.listTours();
      await api.pruneClones();

      expect(calls(fetchFn)).toEqual([
         ['list_tours', {}],
         ['prune_clones', {}],
      ]);
   });

   it('names each tour, question, thread, stop and check in the input', async () => {
      const fetchFn = answering(200, { output: {}, revision: 1 });
      const api = createApi(fetchFn, 'token');
      const pin = { mergeBase: 'a'.repeat(40), head: 'b'.repeat(40), iteration: 2, remoteUrl: 'https://example.test/repo.git' };

      await api.readTour('t1');
      await api.readSummary('t1');
      await api.resendQuestion('t1', 'q 1');
      await api.cancelQuestion('t1', 'q 1');
      await api.askFollowUp('t1', 'th 1', { text: 'And then?', stopId: 's1' });
      await api.setThreadState('t1', 'th 1', 'resolved');
      await api.removeThread('t1', 'th 1');
      await api.editNote('t1', 'n1', 'Words.');
      await api.discardNote('t1', 'n1');
      await api.sendNotes('t1', ['n1', 'n2']);
      await api.answerCheck('t1', 's1', 'c1', { text: 'Yes.', fromSuggestion: true });
      await api.setAnswerType('t1', 's1', 'c1', 'concern');
      await api.clearAnswer('t1', 's1', 'c1');
      await api.sendAnswers('t1');
      await api.setStopSkipped('t1', 's2', true);
      await api.setStopDone('t1', 's2', 2);
      await api.setStopDone('t1', 's2', null);
      await api.recordProgress('t1', { stopId: 's2' });
      await api.recordProgress('t1', { stopId: 's2', version: 2 });
      await api.recordProgress('t1', 'summary');
      await api.setTourArchived('t1', true);
      await api.deleteTour('t1');
      await api.changeFamiliarity('t1', 'new', null);
      await api.changeFamiliarity('t1', 'new', 's2');
      await api.changeFamiliarity('t1', 'new', null, 'Skip the glossary.');
      await api.checkIteration('t1');
      await api.updateIteration('t1', pin);

      expect(calls(fetchFn)).toEqual([
         ['read_state', { tourId: 't1' }],
         ['read_summary', { tourId: 't1' }],
         ['resend_question', { tourId: 't1', questionId: 'q 1' }],
         ['cancel_question', { tourId: 't1', questionId: 'q 1' }],
         ['ask_follow_up', { tourId: 't1', threadId: 'th 1', text: 'And then?', stopId: 's1' }],
         ['set_thread_state', { tourId: 't1', threadId: 'th 1', state: 'resolved' }],
         ['remove_thread', { tourId: 't1', threadId: 'th 1' }],
         ['edit_note', { tourId: 't1', threadId: 'n1', text: 'Words.' }],
         ['discard_note', { tourId: 't1', threadId: 'n1' }],
         ['send_notes', { tourId: 't1', threadIds: ['n1', 'n2'] }],
         ['answer_check', { tourId: 't1', stopId: 's1', checkId: 'c1', text: 'Yes.', fromSuggestion: true }],
         ['set_answer_type', { tourId: 't1', stopId: 's1', checkId: 'c1', type: 'concern' }],
         ['clear_answer', { tourId: 't1', stopId: 's1', checkId: 'c1' }],
         ['send_answers', { tourId: 't1' }],
         ['set_stop_skipped', { tourId: 't1', stopId: 's2', skipped: true }],
         ['set_stop_done', { tourId: 't1', stopId: 's2', version: 2 }],
         ['set_stop_done', { tourId: 't1', stopId: 's2', version: null }],
         ['record_reading_progress', { tourId: 't1', stopId: 's2' }],
         ['record_reading_progress', { tourId: 't1', stopId: 's2', version: 2 }],
         ['record_reading_progress', { tourId: 't1', summary: true }],
         ['set_tour_archived', { tourId: 't1', archived: true }],
         ['delete_tour', { tourId: 't1' }],
         ['change_familiarity', { tourId: 't1', familiarity: 'new' }],
         ['change_familiarity', { tourId: 't1', familiarity: 'new', stopId: 's2' }],
         ['change_familiarity', { tourId: 't1', familiarity: 'new', text: 'Skip the glossary.' }],
         ['check_iteration', { tourId: 't1' }],
         ['update_iteration', { tourId: 't1', pin }],
      ]);
   });

   it('hands the controls of a response to the callback, and returns the output alone', async () => {
      const controls = { tourId: 't1', revision: 4 } as unknown as TourControls;
      const received: TourControls[] = [];

      const tour = await createApi(answering(200, { output: { id: 't1' }, revision: 4, controls }), 'token', (each) => received.push(each)).readTour('t1');

      expect(tour).toEqual({ id: 't1' });
      expect(received).toEqual([controls]);
   });

   it('refuses the answer to a write that the page merges by revision when the body carries none', async () => {
      const asked = createApi(answering(200, { output: { id: 'thread-1' } }), 'token').askQuestion('t1', { text: 'Why?', anchor: { kind: 'tour' } });

      await expect(asked).rejects.toThrow("without the tour's revision");
   });

   it("throws the server's refusal of a call", async () => {
      const refusal: OperationError = { code: 'noSessionAttached', reason: 'tourId: no session.', problem: 'no session.', field: 'tourId' };

      const asked = createApi(answering(409, refusal), 'token').askQuestion('t1', { text: 'Why?', anchor: { kind: 'tour' } });

      await expect(asked).rejects.toMatchObject({ status: 409, error: refusal });
   });
});

describe('file API', () => {
   it('reads a file text at a side, with its path as it is', async () => {
      const body = { content: 'text', path: 'src/a b&c.ts', side: 'base', commit: 'a'.repeat(40), text: 'x\n' };
      const fetchFn = answering(200, { output: body });

      const text = await createApi(fetchFn, 'token').readFileText('tour/1', 'base', 'src/a b&c.ts');

      expect(text).toEqual(body);
      expect(calls(fetchFn)).toEqual([['read_file_text', { tourId: 'tour/1', side: 'base', path: 'src/a b&c.ts' }]]);
   });

   it('lists the changed files and diffs a file of the tour', async () => {
      const fetchFn = answering(200, {});
      const api = createApi(fetchFn, 'token');

      await api.listChangedFiles('t1');
      await api.diffFile('t1', 'src/a.ts');

      expect(calls(fetchFn)).toEqual([
         ['list_changed_files', { tourId: 't1' }],
         ['diff_file', { tourId: 't1', path: 'src/a.ts' }],
      ]);
   });

   it('lists a directory at a side, with the path only below the root', async () => {
      const fetchFn = answering(200, {});
      const api = createApi(fetchFn, 'token');

      await api.listDirectory('t1', 'head', null);
      await api.listDirectory('t1', 'base', 'src/a b');

      expect(calls(fetchFn)).toEqual([
         ['list_directory', { tourId: 't1', side: 'head' }],
         ['list_directory', { tourId: 't1', side: 'base', path: 'src/a b' }],
      ]);
   });

   it("names only the options that differ from the server's defaults", async () => {
      const fetchFn = answering(200, {});
      const api = createApi(fetchFn, 'token');

      await api.diffFile('t1', 'lib/new.ts', { oldPath: 'src/old.ts' });
      await api.diffFile('t1', 'big.json', { includeLarge: true });
      await api.readFileText('t1', 'head', 'big.json', true);
      await api.diffFile('t1', 'a.ts', { ignoreWhitespace: false });
      await api.diffFile('t1', 'lib/new.ts', { oldPath: 'src/old.ts', includeLarge: true, ignoreWhitespace: true });

      expect(calls(fetchFn)).toEqual([
         ['diff_file', { tourId: 't1', path: 'lib/new.ts', oldPath: 'src/old.ts' }],
         ['diff_file', { tourId: 't1', path: 'big.json', large: 'include' }],
         ['read_file_text', { tourId: 't1', side: 'head', path: 'big.json', large: 'include' }],
         ['diff_file', { tourId: 't1', path: 'a.ts' }],
         ['diff_file', { tourId: 't1', path: 'lib/new.ts', oldPath: 'src/old.ts', large: 'include', whitespace: 'ignore' }],
      ]);
   });

   it('carries the refusal of a tour that is not pinned, with its pin', async () => {
      const refusal = {
         code: 'tourNotPinned',
         reason: 'tourId: tour t1 is still pinning.',
         field: 'tourId',
         pin: { state: 'pinning' },
      };

      const api = createApi(answering(409, refusal), 'token');
      const error = await api.listChangedFiles('t1').catch((caught: unknown) => caught);

      expect(error).toBeInstanceOf(ApiError);
      expect((error as ApiError).status).toBe(409);
      expect((error as ApiError).error).toEqual(refusal);
   });

   it('leaves the refusal null when the error body is not one', async () => {
      const fetchFn = vi.fn(async () => new Response('Bad gateway', { status: 502 }));

      const error = await createApi(fetchFn, 'token')
         .diffFile('t1', 'a')
         .catch((caught: unknown) => caught);

      expect((error as ApiError).error).toBeNull();
   });
});

describe('review mark API', () => {
   const objectId = 'c'.repeat(40);

   it("sets a mark and clears it, each with the file's path", async () => {
      const fetchFn = answering(200, { marks: [] });
      const api = createApi(fetchFn, 'token');

      await api.markFileReviewed('tour/1', 'src/a b.ts', objectId);
      await api.markFileReviewed('t1', 'gone.ts', null);
      await api.clearFileReviewed('t1', 'src/a b.ts');

      expect(calls(fetchFn)).toEqual([
         ['mark_file_reviewed', { tourId: 'tour/1', path: 'src/a b.ts', headObjectId: objectId }],
         ['mark_file_reviewed', { tourId: 't1', path: 'gone.ts', headObjectId: null }],
         ['clear_file_reviewed', { tourId: 't1', path: 'src/a b.ts' }],
      ]);
   });

   it('throws the refusal, and an unauthorized error for a refused token', async () => {
      const refusal = { code: 'tourNotFound', reason: 'tourId: no tour has the ID.', field: 'tourId' };

      const refused = await createApi(answering(404, refusal), 'token')
         .markFileReviewed('t1', 'a.ts', objectId)
         .catch((caught: unknown) => caught);
      const unauthorized = await createApi(answering(401, {}), 'token')
         .clearFileReviewed('t1', 'a.ts')
         .catch((caught: unknown) => caught);

      expect((refused as ApiError).error).toEqual(refusal);
      expect(unauthorized).toBeInstanceOf(UnauthorizedError);
   });
});
