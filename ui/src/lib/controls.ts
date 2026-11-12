import type { StopActionKind } from './tourActions';
import type { Offer, PendingWork, QuestionOutcome, QuestionThread, StopOffers, ThreadOffers, TourControls } from './types';

/** Why the page cannot use a control before the tour's controls arrive with its load. */
export const controlsNotLoaded = 'The tour is not loaded yet.';

/**
 * Returns why a control is disabled, or null when it is enabled: the server's reason for a closed offer, or else the page's own reason, such as a write of the page still in flight.
 *
 * Only the server decides whether a control can act, so no component writes its own reason why one cannot.
 *
 * @param offer - The control's offer from the tour's controls, or undefined while the page has none.
 * @param busy - Why the page holds the control back for a write of its own in flight, or null for none.
 */
export function disabledReason(offer: Offer | undefined, busy: string | null = null): string | null {
   if (offer === undefined) {
      return controlsNotLoaded;
   }

   return offer.state === 'closed' ? sentence(offer.reason) : busy;
}

/** Returns whether the server says that a send of the answers holds one, open or closed for another reason, so the page shows the send control. */
export function hasAnswersToSend(offer: Offer | undefined): boolean {
   return offer !== undefined && !(offer.state === 'closed' && offer.code === 'noAnswersToSend');
}

/** Returns whether the offer is open. */
export function isOpen(offer: Offer | undefined): boolean {
   return offer?.state === 'open';
}

/** Returns the controls of the stop's tour actions, or undefined while the page has none for the stop. */
export function stopOffers(controls: TourControls | null, stopId: string): StopOffers | undefined {
   return controls?.offers.stops[stopId];
}

/** Returns why each tour action of the stop is disabled, as `disabledReason` says, or null for an action that is enabled. */
export function stopActionReasons(controls: TourControls | null, stopId: string): Record<StopActionKind, string | null> {
   const offers = stopOffers(controls, stopId);
   return { explain: disabledReason(offers?.explain), revise: disabledReason(offers?.revise) };
}

/** Returns the controls of the thread, or undefined while the page has none for it. */
export function threadOffers(controls: TourControls | null, threadId: string): ThreadOffers | undefined {
   return controls?.offers.threads[threadId];
}

/** Returns the IDs of the stops that a waiting explain or revise tour action holds back, as the controls' waiting work names them. */
export function stopsWithWaitingActions(pending: PendingWork | undefined): ReadonlySet<string> {
   return new Set(Object.keys(pending?.stops ?? {}));
}

/** Returns the threads that the IDs name, in the order of the IDs, without an ID that names no thread the page knows. */
export function threadsOf(threads: readonly QuestionThread[], ids: readonly string[] | undefined): QuestionThread[] {
   return (ids ?? []).flatMap((id) => threads.find((thread) => thread.id === id) ?? []);
}

/** Returns the outcome that the server derived for the question of the thread, or null when the thread's status names no such question. */
export function outcomeOf(thread: QuestionThread, questionId: string): QuestionOutcome | null {
   return thread.status.questions.find((question) => question.questionId === questionId)?.outcome ?? null;
}

// The server's reasons start with a lowercase word after the field they name, as in "tourId: tour x is ...", and the page shows one without its field.
function sentence(reason: string): string {
   return reason.length === 0 ? reason : reason[0]!.toUpperCase() + reason.slice(1);
}
