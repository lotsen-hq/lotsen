// Seeds the sample tours of demo/sampleTours.ts into a world through the server's real operations, for the demo and for the tests of the sample tours. It runs in Node and in the browser.
import type { AnchorInput, ChangedFileList, QuestionThread, Stop, Tour, UpdatedBatch } from '../src/lib/contract.generated.ts';
import type { ClaudeSession, World } from '../tests/backend/backend.ts';
import { pinningTour, sampleRepository, sampleRepositoryUrl, sampleTours, type SampleAnchor, type SampleContent, type SampleStep, type SampleTour } from './sampleTours.ts';

/** The server's IDs of a seeded tour's stops, checks and threads, by the keys that its spec gives them, and the ID of each thread's last question. */
export interface SeededTour {
   tourId: string;
   stops: Record<string, string>;
   checks: Record<string, string>;
   threads: Record<string, string>;
   questions: Record<string, string>;
   /** The ID of the last batch of answers that the reviewer sent, or undefined before the first. */
   batchId?: string;
}

/** What the template of the sample tours holds: the commits of the sample repository and each seeded tour by its key. */
export interface SampleSeed {
   base: string;
   heads: [first: string, pushed: string];
   tours: Record<string, SeededTour>;
}

/** The name of the template that holds the sample tours, on whichever host builds it. */
export const sampleTemplateName = 'demo-sample-tours';

/** The pull request URL of a sample tour. */
export const prUrlOf = (tour: Pick<SampleTour, 'number'>) => `${sampleRepositoryUrl}/pull/${tour.number}`;

/** The label that the tour list gives a sample tour. */
export const labelOf = (tour: Pick<SampleTour, 'number'>) => `storefront #${tour.number}`;

/** The step of the template's clock between two steps of a tour, so the threads and answers of a tour show apart in time. */
const stepMs = 20_000;

/**
 * Seeds every sample tour on the world, oldest first, at its age before now on the world's clock, and returns their IDs.
 *
 * Each tour's steps run while the platform reports that tour's pull request, and the world is idle before the pull request changes, so no iteration check of one tour sees another's.
 */
export async function seedSampleTours(world: World): Promise<SampleSeed> {
   const base = await world.repo.commit('main', 'The release target', sampleRepository.base);
   const first = await world.repo.commit('feature', 'The checkout rework', sampleRepository.head, base);
   const pushed = await world.repo.commit('feature', 'Clearer payment messages', sampleRepository.push, first);
   const seed: SampleSeed = { base, heads: [first, pushed], tours: {} };
   const start = Date.now();
   // A world started without a clock start shows the host's start.
   let clock = Date.parse('2026-01-01T12:00:00Z');
   const advanceTo = async (time: number) => {
      if (time > clock) {
         await world.clock.advance(time - clock);
         clock = time;
      }
   };

   for (const tour of sampleTours()) {
      await advanceTo(start - tour.ageMinutes * 60_000);
      if (typeof tour.pin === 'object') {
         await world.pullRequest.fail(tour.pin.failed);
      } else if (tour.pin === 'firstHead') {
         await world.pullRequest.set(first, base, { iteration: 1 });
      } else {
         await world.pullRequest.set(pushed, base, { iteration: 2, ...(tour.pullRequestState === undefined ? {} : { state: tour.pullRequestState }) });
      }

      const claude = await world.claude();
      const seeded = await createTour(world, claude, tour);
      seed.tours[tour.key] = seeded;
      let session = claude;
      for (const step of tour.steps ?? []) {
         await advanceTo(clock + stepMs);
         session = await take(world, session, step, seeded, seed);
      }

      if (tour.pin === 'firstHead') {
         await world.pullRequest.set(pushed, base, { iteration: 2 });
         await world.reviewer.call('check_iteration', { tourId: seeded.tourId });
      } else if (tour.pullRequestState !== undefined) {
         await world.reviewer.call('check_iteration', { tourId: seeded.tourId });
      }

      await world.idle();
   }

   return seed;
}

/** The sessions that a world binds to the sample tours. */
export interface LiveSamples {
   /** The confirmed session, bound to every sample tour whose spec asks for a confirmed one. */
   claude: ClaudeSession;
   /** The unconfirmed session, bound to every sample tour whose spec asks for an unconfirmed one. */
   unconfirmed: ClaudeSession;
   seed: SampleSeed;
}

/** Binds the sessions of the sample tours on a world copied from the template, and takes their live steps. */
export async function liven(world: World, template: SampleSeed): Promise<LiveSamples> {
   const seed: SampleSeed = { ...template, tours: { ...template.tours } };
   const claude = await world.claude();
   const unconfirmed = await world.claude({ confirm: false });
   for (const tour of sampleTours().filter((candidate) => candidate.session !== undefined)) {
      const session = tour.session === 'confirmed' ? claude : unconfirmed;
      const seeded = cloneSeeded(seed.tours[tour.key]!);
      seed.tours[tour.key] = seeded;
      await session.call('attach_tour', { tourId: seeded.tourId });
      for (const step of tour.live ?? []) {
         await take(world, session, step, seeded, seed);
      }
   }

   return { claude, unconfirmed, seed };
}

/**
 * Creates the sample tour whose pin is in progress, as the session, while the platform holds its answers. The pin finishes, and every other resolve continues, once the world releases the
 * pull request.
 */
export async function seedPinningTour(world: World, claude: ClaudeSession): Promise<SeededTour> {
   await world.pullRequest.hold();
   return createTour(world, claude, pinningTour);
}

/** Returns the `put_stop` input of sample content on a seeded tour's stop, with the IDs of the checks that the stop keeps. */
export function putStopInput(seeded: SeededTour, stopKey: string, content: SampleContent): Record<string, unknown> {
   const { passages = [], checks, ...fields } = content;
   return {
      tourId: seeded.tourId,
      stopId: seeded.stops[stopKey],
      ...fields,
      passages: passages.map(([side, path, startLine, endLine, label], index) => ({
         side,
         path,
         startLine,
         endLine,
         role: index === 0 ? 'primary' : 'related',
         ...(label === undefined ? {} : { label }),
      })),
      ...(checks === undefined
         ? {}
         : {
              checks: checks.map(({ key, ...check }) => ({ ...check, ...(seeded.checks[key] === undefined ? {} : { id: seeded.checks[key] }) })),
           }),
   };
}

/** Writes sample content on a seeded tour's stop as the session, and records the IDs that the server gave its new checks. */
export async function writeStop(session: ClaudeSession, seeded: SeededTour, stopKey: string, content: SampleContent): Promise<Stop> {
   const stop = (await session.call('put_stop', putStopInput(seeded, stopKey, content))) as Stop;
   const written = stop.stage === 'written' ? (stop.content.checks ?? []) : [];
   (content.checks ?? []).forEach((check, index) => (seeded.checks[check.key] = written[index]!.id));
   return stop;
}

/** Returns the key and content that the demo panel writes on a pending stop of a seeded tour, or undefined when the spec has none. */
export function laterContentOf(seed: SampleSeed, tourId: string, stopId: string): { tour: SeededTour; stopKey: string; content: SampleContent } | undefined {
   for (const spec of sampleTours()) {
      const tour = seed.tours[spec.key];
      const stop = spec.chapters?.flatMap((chapter) => chapter.stops).find((candidate) => tour?.stops[candidate.key] === stopId);
      if (tour?.tourId === tourId && stop?.later !== undefined) {
         return { tour, stopKey: stop.key, content: stop.later };
      }
   }

   return undefined;
}

async function createTour(world: World, claude: ClaudeSession, spec: SampleTour): Promise<SeededTour> {
   const tour = (await claude.call('create_tour', { prUrl: prUrlOf(spec), familiarity: spec.familiarity, ...(spec.planFirst ? { planFirst: true } : {}) })) as Tour;
   const seeded: SeededTour = { tourId: tour.id, stops: {}, checks: {}, threads: {}, questions: {} };
   if (spec.pin === 'pinning') {
      return seeded;
   }

   await world.idle();
   if (spec.chapters === undefined) {
      return seeded;
   }

   const chapters = spec.chapters.map((chapter, index) => ({ id: `c${index + 1}`, title: chapter.title }));
   const stops = spec.chapters.flatMap((chapter, index) => chapter.stops.map((stop) => ({ stop, chapter: chapters[index]!.id })));
   const planned = (await claude.call('set_plan', {
      tourId: tour.id,
      chapters,
      stops: stops.map(({ stop, chapter }) => ({ title: stop.title, kind: stop.kind, chapter, ...(stop.anchorHint === undefined ? {} : { anchorHint: stop.anchorHint }) })),
      ...(spec.fileMarks === undefined ? {} : { fileMarks: spec.fileMarks }),
   })) as Tour;
   planned.plan!.stops.forEach((stop, index) => (seeded.stops[stops[index]!.stop.key] = stop.id));
   for (const { stop } of stops) {
      if (stop.content !== undefined) {
         await writeStop(claude, seeded, stop.key, stop.content);
      }
   }

   return seeded;
}

function cloneSeeded(seeded: SeededTour): SeededTour {
   return { ...seeded, stops: { ...seeded.stops }, checks: { ...seeded.checks }, threads: { ...seeded.threads }, questions: { ...seeded.questions } };
}

function anchorInput(anchor: SampleAnchor, seeded: SeededTour): AnchorInput {
   switch (anchor.kind) {
      case 'stopText':
         return { kind: 'stopText', stopId: seeded.stops[anchor.stop]!, field: anchor.field.replace(/^checks\[([^\]]+)\]/, (_, key: string) => `checks[${seeded.checks[key]}]`), text: anchor.text };
      case 'stop':
         return { kind: 'stop', stopId: seeded.stops[anchor.stop]! };
      default:
         return anchor;
   }
}

// Records a thread and its last question under the key.
function remember(seeded: SeededTour, key: string, thread: QuestionThread): void {
   seeded.threads[key] = thread.id;
   const question = thread.kind === 'note' ? undefined : thread.messages.findLast((message) => message.kind === 'question');
   if (question !== undefined) {
      seeded.questions[key] = question.id;
   }
}

// Takes one step as the reviewer or as the bound session, and returns the bound session after it, which only a newSession step changes.
async function take(world: World, claude: ClaudeSession, step: SampleStep, seeded: SeededTour, seed: SampleSeed): Promise<ClaudeSession> {
   const { reviewer } = world;
   const tourId = seeded.tourId;
   if ('ask' in step) {
      const stopId = step.stop === undefined ? {} : { stopId: seeded.stops[step.stop] };
      remember(seeded, step.ask, await reviewer.call('ask_question', { tourId, text: step.text, anchor: anchorInput(step.anchor, seeded), ...stopId }));
   } else if ('reply' in step) {
      await claude.call('reply', { tourId, questionId: seeded.questions[step.reply], resolution: step.resolution, ...(step.text === undefined ? {} : { text: step.text }) });
   } else if ('followUp' in step) {
      remember(seeded, step.followUp, await reviewer.call('ask_follow_up', { tourId, threadId: seeded.threads[step.followUp]!, text: step.text }));
   } else if ('resend' in step) {
      await reviewer.call('resend_question', { tourId, questionId: seeded.questions[step.resend]! });
   } else if ('cancel' in step) {
      await reviewer.call('cancel_question', { tourId, questionId: seeded.questions[step.cancel]! });
   } else if ('resolve' in step) {
      await reviewer.call('set_thread_state', { tourId, threadId: seeded.threads[step.resolve]!, state: 'resolved' });
   } else if ('startThread' in step) {
      const started = (await claude.call('start_thread', { tourId, text: step.text, anchor: anchorInput(step.anchor, seeded) })) as { thread: QuestionThread };
      remember(seeded, step.startThread, started.thread);
   } else if ('addMessage' in step) {
      await claude.call('add_thread_message', { tourId, threadId: seeded.threads[step.addMessage], text: step.text });
   } else if ('outdate' in step) {
      await claude.call('outdate_thread', { tourId, threadId: seeded.threads[step.outdate] });
   } else if ('action' in step) {
      const { request } = step;
      const action =
         request.kind === 'explain' || request.kind === 'revise'
            ? { kind: request.kind, target: { kind: 'stop' as const, stopId: seeded.stops[request.stop]! } }
            : request.kind === 'reorder'
              ? { kind: request.kind, stopIds: request.stops.map((key) => seeded.stops[key]!) }
              : { kind: request.kind };
      remember(seeded, step.action, await reviewer.call('request_tour_action', { tourId, action, ...(step.text === undefined ? {} : { text: step.text }) }));
   } else if ('familiarity' in step) {
      remember(seeded, step.familiarity, (await reviewer.call('change_familiarity', { tourId, familiarity: step.level })).thread);
   } else if ('write' in step) {
      await writeStop(claude, seeded, step.write, step.content);
   } else if ('note' in step) {
      const stopId = step.stop === undefined ? {} : { stopId: seeded.stops[step.stop] };
      remember(seeded, step.note, (await reviewer.call('save_note', { tourId, text: step.text, anchor: anchorInput(step.anchor, seeded), ...stopId })).thread);
   } else if ('answer' in step) {
      await answer(world, seeded, step);
   } else if ('sendAnswers' in step) {
      seeded.batchId = ((await reviewer.call('send_answers', { tourId })) as UpdatedBatch).batchId;
   } else if ('fetchAnswers' in step) {
      await claude.call('fetch_answers', { tourId, batchId: seeded.batchId });
   } else if ('fetchQuestion' in step) {
      await claude.call('fetch_question', { tourId, questionId: seeded.questions[step.fetchQuestion] });
   } else if ('synthesis' in step) {
      const summary = (await claude.call('read_summary', { tourId })) as { summaryFingerprint: string };
      await claude.call('write_synthesis', { tourId, text: step.synthesis, basedOn: summary.summaryFingerprint });
   } else if ('newSession' in step) {
      await claude.drop();
      const next = await world.claude();
      await next.call('attach_tour', { tourId });
      return next;
   } else if ('skip' in step) {
      await reviewer.call('set_stop_skipped', { tourId, stopId: seeded.stops[step.skip]!, skipped: true });
   } else if ('progress' in step) {
      await reviewer.call('record_reading_progress', step.progress === 'summary' ? { tourId, summary: true } : { tourId, stopId: seeded.stops[step.progress]! });
   } else if ('archive' in step) {
      await reviewer.call('set_tour_archived', { tourId, archived: true });
   } else if ('markReviewed' in step) {
      await markReviewed(world, seeded, step.markReviewed, step.atFirstHead === true ? seed : null);
   }

   return claude;
}

// Answers the check as the reviewer does: a suggestion's text picks the suggestion with its type, and any other text is a decision until the reviewer retypes it.
async function answer(world: World, seeded: SeededTour, step: Extract<SampleStep, { answer: unknown }>): Promise<void> {
   const tourId = seeded.tourId;
   const [stopKey, checkKey] = step.answer;
   const stop = (await world.reviewer.call('read_state', { tourId })).plan!.stops.find((candidate) => candidate.id === seeded.stops[stopKey]);
   const check = stop?.stage === 'written' ? stop.content.checks?.find((candidate) => candidate.id === seeded.checks[checkKey]) : undefined;
   const suggested = check?.suggestions?.some((suggestion) => suggestion.text === step.text && suggestion.type === step.type) === true;
   const target = { tourId, stopId: seeded.stops[stopKey]!, checkId: seeded.checks[checkKey]! };
   await world.reviewer.call('answer_check', { ...target, text: step.text, ...(suggested ? { fromSuggestion: true } : {}) });
   if (!suggested && step.type !== 'decision') {
      await world.reviewer.call('set_answer_type', { ...target, type: step.type });
   }
}

// Marks the file reviewed at the tour's head, or at the first head when `firstHead` holds the seed, so the mark names a version that the tour's head has replaced.
async function markReviewed(world: World, seeded: SeededTour, path: string, firstHead: SampleSeed | null): Promise<void> {
   const listed = (tourId: string) => world.reviewer.call('list_changed_files', { tourId }) as Promise<ChangedFileList>;
   const source = firstHead === null ? seeded.tourId : firstHead.tours['checkout-rework']!.tourId;
   const headObjectId = (await listed(source)).files.find((file) => file.path === path)?.headObjectId ?? null;
   await world.reviewer.call('mark_file_reviewed', { tourId: seeded.tourId, path, headObjectId });
}
