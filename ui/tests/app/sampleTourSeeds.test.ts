import { describe, expect, it } from 'vitest';
import { seedPinningTour, type SampleSeed } from '../../demo/seed';
import type { ChangedFileList, QuestionThread, StopContent, Tour, TourSummary } from '../../src/lib/contract.generated';
import type { World } from '../backend/backend';
import { readTour } from '../harness';
import { resetPageEachTest } from './appPanels';
import { contentOf, messagesOf, questionsOf, sampleWorld, specs } from './sampleWorld';

resetPageEachTest();

async function readAll(world: World, seed: SampleSeed): Promise<Map<string, Tour>> {
   const keys = specs.map((spec) => spec.key);
   const tours = await Promise.all(keys.map((key) => readTour(world, seed.tours[key]!.tourId)));
   return new Map(keys.map((key, index) => [key, tours[index]!]));
}

const lastRequest = (thread: QuestionThread) => questionsOf(thread).at(-1)?.requests.at(-1);
const waits = (thread: QuestionThread) => lastRequest(thread)?.state === 'pending';
const proseOf = (content: StopContent) => {
   const { checks, ...fields } = content.html;
   return [...Object.values(fields), ...(checks ?? []).flatMap((check) => Object.values(check))].join('');
};

describe('the seeded sample tours', () => {
   it('seed through the real operations, with every tour state, pull request state and pin state, and a pinned tour without a plan', async () => {
      const { world } = await sampleWorld();
      await seedPinningTour(world, await world.claude());

      const { tours } = (await world.reviewer.call('list_tours', {})) as { tours: TourSummary[] };

      expect(new Set(tours.map((tour) => tour.state))).toEqual(new Set(['generating', 'ready', 'inProgress', 'finished', 'archived']));
      expect(new Set(tours.map((tour) => tour.pullRequestState))).toEqual(new Set(['active', 'completed', 'abandoned']));
      expect(new Set(tours.map((tour) => tour.pin.state))).toEqual(new Set(['pinning', 'pinned', 'failed']));
      expect(tours.some((tour) => tour.pin.state === 'pinned' && tour.stopCount === 0)).toBe(true);
      await world.pullRequest.release();
   });

   it('hold a plan with every stop kind, pending, ready and revised stops, every section on some stops and not on others, and labeled passages', async () => {
      const { world, seed } = await sampleWorld();
      const stops = [...(await readAll(world, seed)).values()].flatMap((tour) => tour.plan?.stops ?? []);
      const contents = stops.flatMap((stop) => contentOf(stop) ?? []);

      expect(new Set(stops.map((stop) => stop.kind))).toEqual(new Set(['overview', 'context', 'inspect', 'risk', 'question']));
      expect(new Set(stops.map((stop) => stop.status))).toEqual(new Set(['pending', 'ready', 'revised']));
      for (const field of ['how', 'why', 'designChoices', 'pinnedBy', 'scenario', 'checks'] as const) {
         expect(
            contents.some((content) => content[field] !== undefined),
            field,
         ).toBe(true);
         expect(
            contents.some((content) => content[field] === undefined),
            field,
         ).toBe(true);
      }

      expect(contents.some((content) => content.passages.some((passage) => passage.role === 'related' && passage.label !== undefined))).toBe(true);
   });

   it('hold a check with a recommendation and a suggestion of every answer type, a check with neither, an answered and an unanswered check in one stop, and an answer on a dropped check', async () => {
      const { world, tour } = await sampleWorld();
      const checkout = await readTour(world, tour('checkout-rework').tourId);
      const stops = checkout.plan!.stops;
      const checks = stops.flatMap((stop) => contentOf(stop)?.checks ?? []);

      expect(checks.some((check) => check.recommendation !== undefined && new Set(check.suggestions?.map((suggestion) => suggestion.type)).size === 3)).toBe(true);
      expect(checks.some((check) => check.recommendation === undefined && check.suggestions === undefined)).toBe(true);
      expect(new Set(checks.map((check) => check.id)).size).toBe(checks.length);
      const answered = stops.map((stop) => ({ checks: contentOf(stop)?.checks ?? [], answers: checkout.answers.filter((answer) => answer.stopId === stop.id) }));
      expect(
         answered.some(
            ({ checks, answers }) => checks.some((check) => answers.some((answer) => answer.checkId === check.id)) && checks.some((check) => !answers.some((answer) => answer.checkId === check.id)),
         ),
      ).toBe(true);
      expect(answered.some(({ checks, answers }) => answers.some((answer) => !checks.some((check) => check.id === answer.checkId)))).toBe(true);
   });

   it('hold an answer in every state, with sent answers whose session ended, which the next send holds again for the bound session', async () => {
      const { world, tour, claude } = await sampleWorld();
      const tourId = tour('checkout-rework').tourId;
      const { answers } = await readTour(world, tourId);

      expect(new Set(answers.map((answer) => answer.state))).toEqual(new Set(['draft', 'sent', 'received']));
      expect(answers.filter((answer) => answer.state === 'sent').every((answer) => answer.batch!.sessionId !== claude.sessionId)).toBe(true);
      await world.reviewer.call('send_answers', { tourId });
      const sent = (await readTour(world, tourId)).answers.filter((answer) => answer.state === 'sent');
      expect(sent.length).toBeGreaterThan(2);
      expect(sent.every((answer) => answer.batch!.sessionId === claude.sessionId)).toBe(true);
   });

   it('hold prose with every element the server renders, ref: links into a changed and an unchanged file, an https: link and a link that renders as text', async () => {
      const { world, tour } = await sampleWorld();
      const checkout = await readTour(world, tour('checkout-rework').tourId);
      const contents = checkout.plan!.stops.flatMap((stop) => contentOf(stop) ?? []);
      const rendered = new DOMParser().parseFromString(contents.map(proseOf).join(''), 'text/html').body;

      expect(new Set([...rendered.querySelectorAll('*')].map((element) => element.tagName.toLowerCase()))).toEqual(new Set(['p', 'ul', 'ol', 'li', 'em', 'strong', 'code', 'pre', 'br', 'a']));
      const { files } = (await world.reviewer.call('list_changed_files', { tourId: checkout.id })) as ChangedFileList;
      const changed = new Set(files.flatMap((file) => (file.oldPath === undefined ? [file.path] : [file.path, file.oldPath])));
      const references = [...rendered.querySelectorAll<HTMLElement>('a[data-ref-path]')].map((anchor) => anchor.dataset.refPath!);
      expect(references.some((path) => changed.has(path))).toBe(true);
      expect(references.some((path) => !changed.has(path))).toBe(true);
      expect(rendered.querySelector('a[href^="https:"]')).not.toBeNull();
      const markdown = contents.flatMap(({ headline, how, why, designChoices, pinnedBy, scenario, checks }) => [
         headline,
         how,
         why,
         designChoices,
         pinnedBy,
         scenario,
         ...(checks ?? []).flatMap((check) => [check.question, check.recommendation]),
      ]);
      expect(markdown.some((text) => /\]\((?!https:|ref:)[^)]+\)/.test(text ?? ''))).toBe(true);
   });

   it('change files of every kind and mark', async () => {
      const { world, tour } = await sampleWorld();
      const { files } = (await world.reviewer.call('list_changed_files', { tourId: tour('checkout-rework').tourId })) as ChangedFileList;

      expect(new Set(files.map((file) => file.kind))).toEqual(new Set(['added', 'modified', 'deleted', 'renamed']));
      expect(files.some((file) => file.binary)).toBe(true);
      expect(files.some((file) => file.large !== undefined)).toBe(true);
      expect(files.some((file) => file.lineEndingsOnly)).toBe(true);
   });

   it('hold a mechanical and a generated file, each with its reason, and a passage and a ref: link into one of them', async () => {
      const { world, tour } = await sampleWorld();
      const checkout = await readTour(world, tour('checkout-rework').tourId);
      const marks = checkout.plan!.fileMarks;
      const marked = new Set(marks.map((mark) => mark.path));
      const contents = checkout.plan!.stops.flatMap((stop) => contentOf(stop) ?? []);

      expect(new Set(marks.map((mark) => mark.mark))).toEqual(new Set(['mechanical', 'generated']));
      expect(marks.every((mark) => mark.reason.trim() !== '')).toBe(true);
      expect(contents.some((content) => content.passages.some((passage) => marked.has(passage.path)))).toBe(true);
      const rendered = new DOMParser().parseFromString(contents.map(proseOf).join(''), 'text/html').body;
      expect([...rendered.querySelectorAll<HTMLElement>('a[data-ref-path]')].some((anchor) => marked.has(anchor.dataset.refPath!))).toBe(true);
   });
});

describe('the seeded sample threads and sessions', () => {
   async function threadsOf() {
      const live = await sampleWorld();
      const tours = await readAll(live.world, live.seed);
      return { ...live, tours, threads: [...tours.values()].flatMap((tour) => tour.threads) };
   }

   it('cover a tour without a session and a session at each attachment level, with a waiting question under each level', async () => {
      const { world, tours } = await threadsOf();
      const { tours: listed } = (await world.reviewer.call('list_tours', {})) as { tours: TourSummary[] };

      expect(new Set(listed.map((tour) => tour.binding?.level ?? 'none'))).toEqual(new Set(['none', 'unconfirmed', 'confirmed']));
      const waiting = listed.filter((summary) => [...tours.values()].find((tour) => tour.id === summary.id)?.threads.some(waits));
      expect(new Set(waiting.map((tour) => tour.binding?.level))).toEqual(new Set(['unconfirmed', 'confirmed']));
   });

   it('cover a question in every request state, a reply of each resolution and a decline without words', async () => {
      const { threads } = await threadsOf();

      expect(new Set(threads.flatMap((thread) => questionsOf(thread).flatMap((question) => question.requests.map((request) => request.state))))).toEqual(
         new Set(['pending', 'done', 'needsAnswer', 'declined', 'unanswered', 'canceled']),
      );
      expect(threads.some((thread) => messagesOf(thread).some((message) => message.kind === 'reply' && message.html === undefined))).toBe(true);
   });

   it('cover a follow-up that waits and one that answers a reply that needs an answer', async () => {
      const { threads } = await threadsOf();
      const followedUp = threads.filter((thread) => questionsOf(thread).length > 1);

      expect(followedUp.some(waits)).toBe(true);
      expect(followedUp.some((thread) => questionsOf(thread)[0]!.requests.at(-1)!.state === 'needsAnswer' && lastRequest(thread)?.state === 'done')).toBe(true);
   });

   it('cover every kind of anchor, with code at the head, base lines that an inline diff shows as deleted, and text in a check', async () => {
      const { threads } = await threadsOf();

      expect(new Set(threads.map((thread) => thread.anchor.kind))).toEqual(new Set(['code', 'file', 'stopText', 'stop', 'tour']));
      expect(new Set(threads.flatMap((thread) => (thread.anchor.kind === 'code' ? [thread.anchor.side] : [])))).toEqual(new Set(['base', 'head']));
      expect(threads.some((thread) => thread.anchor.kind === 'stopText' && thread.anchor.field.startsWith('checks['))).toBe(true);
   });

   it('cover a revised stop with its earlier version, an outdated answer on that version and a stop text thread that the revision outdated', async () => {
      const { tours } = await threadsOf();
      const checkout = tours.get('checkout-rework')!;
      const revised = checkout.plan!.stops.flatMap((stop) => (stop.stage === 'written' && stop.status === 'revised' ? [stop] : []));

      expect(revised.length).toBeGreaterThan(0);
      expect(revised.every((stop) => stop.earlier.length === stop.version - 1)).toBe(true);
      expect(
         checkout.answers.some((answer) => revised.some((stop) => stop.id === answer.stopId && stop.earlier[answer.outdatedOn! - 1]?.content.checks?.some((check) => check.id === answer.checkId))),
      ).toBe(true);
      expect(
         checkout.threads.some((thread) => thread.state === 'outdated' && thread.anchor.kind === 'stopText' && revised.some((stop) => stop.version > (thread.anchor as { version: number }).version)),
      ).toBe(true);
   });

   it("cover a plan-first tour whose plan waits for the reviewer's approval, bound to a session", async () => {
      const { tours, world } = await threadsOf();
      const wishlist = tours.get('wishlist')!;
      const { tours: listed } = (await world.reviewer.call('list_tours', {})) as { tours: TourSummary[] };

      expect(wishlist.planApproval).toBe('awaited');
      expect(wishlist.plan!.stops.every((stop) => stop.status === 'pending')).toBe(true);
      expect(listed.find((tour) => tour.id === wishlist.id)?.binding?.level).toBe('confirmed');
   });

   it('cover a skipped stop beside stops that are not skipped', async () => {
      const { tours } = await threadsOf();
      const stops = tours.get('checkout-rework')!.plan!.stops;

      expect(stops.some((stop) => stop.skipped === true)).toBe(true);
      expect(stops.some((stop) => stop.skipped !== true)).toBe(true);
   });

   it('cover a note in a tour without a session and notes to batch in a tour with one, and a waiting question that Claude received and one it did not', async () => {
      const { tours, threads } = await threadsOf();
      const notes = (key: string) => tours.get(key)!.threads.filter((thread) => thread.kind === 'note').length;

      expect(notes('readme-only')).toBeGreaterThan(0);
      expect(notes('checkout-rework')).toBeGreaterThanOrEqual(2);
      expect(new Set(threads.filter(waits).map((thread) => lastRequest(thread)!.receivedAt !== undefined))).toEqual(new Set([true, false]));
   });

   it("cover a thread that Claude started, Claude's agent message in an answered thread, and an outdated thread that Claude started", async () => {
      const { threads } = await threadsOf();
      const agentMessages = threads.flatMap((thread) => messagesOf(thread).flatMap((message, index) => (message.kind === 'agentMessage' ? [{ thread, index }] : [])));

      expect(agentMessages.some(({ thread, index }) => index === 0 && thread.state === 'open')).toBe(true);
      expect(agentMessages.some(({ thread, index }) => index > 0 && questionsOf(thread).length > 0)).toBe(true);
      expect(threads.some((thread) => thread.state === 'outdated' && messagesOf(thread)[0]?.kind === 'agentMessage')).toBe(true);
   });

   it('cover a tour action of each kind, one waiting on a stop and one that Claude resolved, and a decision', async () => {
      const { threads } = await threadsOf();
      const actions = threads.filter((thread) => thread.kind === 'asked' && thread.action !== undefined);

      expect(new Set(actions.map((thread) => (thread.kind === 'asked' ? thread.action?.kind : null)))).toEqual(new Set(['explain', 'revise', 'replan', 'reorder']));
      expect(threads.flatMap((thread) => (thread.kind === 'decided' ? [thread.decision.kind] : []))).toEqual(['familiarityChanged']);
      expect(actions.some((thread) => waits(thread) && thread.anchor.kind === 'stop')).toBe(true);
      expect(actions.some((thread) => lastRequest(thread)?.state === 'done' && messagesOf(thread).at(-1)?.kind === 'reply')).toBe(true);
   });
});
