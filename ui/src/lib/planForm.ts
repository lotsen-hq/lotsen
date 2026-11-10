import type { Familiarity } from './types';

/** What the Re-plan form holds and what the tour's controls say about it. */
export interface PlanFormState {
   /** The tour's familiarity level. */
   current: Familiarity;
   /** The level that the form selects. */
   selected: Familiarity;
   /** The reviewer's words, which count as none when they hold only whitespace. */
   words: string;
   /** Whether the controls say that the tour's current level asks Claude again. */
   askAgain: boolean;
   /** Why a replan cannot go now, or null when it can. */
   replanReason: string | null;
   /** Why a change of the level cannot go now, or null when it can. */
   levelReason: string | null;
}

/** What Send does: request the replan, change the level, or send nothing, with the server's reason when a closed offer holds the send back. */
export type PlanFormSend = { send: 'replan' } | { send: 'level'; level: Familiarity } | { send: null; reason: string | null };

/** Returns what Send does in the Re-plan form: words at the current level are a replan, and another level is a change of the level that carries any words. */
export function planFormSend({ current, selected, words, askAgain, replanReason, levelReason }: PlanFormState): PlanFormSend {
   const level = (): PlanFormSend => (levelReason === null ? { send: 'level', level: selected } : { send: null, reason: levelReason });
   if (selected !== current) {
      return level();
   }

   if (words.trim() !== '') {
      return replanReason === null ? { send: 'replan' } : { send: null, reason: replanReason };
   }

   return askAgain ? level() : { send: null, reason: null };
}
