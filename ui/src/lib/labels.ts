import type {
   AnchorInput,
   AttachmentLevel,
   CodeReference,
   Familiarity,
   FileChangeKind,
   FileMarkKind,
   Plan,
   PassageRole,
   RepositoryEntryType,
   StopKind,
   StopStatus,
   ThreadAnchor,
   TourBinding,
} from './types';
import type { DoneClick } from './doneClick';
import type { DoneState } from './tourRecord.svelte';

/** Whether a session is bound to a tour, and if so, its attachment level. */
export type Attachment = 'none' | AttachmentLevel;

/** What the top bar shows of an attachment: a short label and one line on what works. */
export interface AttachmentText {
   label: string;
   explanation: string;
}

export const attachmentTexts: Record<Attachment, AttachmentText> = {
   none: { label: 'No session', explanation: 'No Claude session is attached to this tour. Browsing and notes work, but sending a question needs a session that attaches it.' },
   unconfirmed: { label: 'Unconfirmed', explanation: 'Claude has not confirmed yet that questions reach it. A question waits until Claude answers, and you can cancel it or send it again.' },
   confirmed: { label: 'Confirmed', explanation: 'Questions reach Claude.' },
};

export function attachmentOf(binding: TourBinding | null | undefined): Attachment {
   return binding?.level ?? 'none';
}

export const familiarityLabels: Record<Familiarity, string> = {
   familiar: 'Familiar with the code',
   some: 'Knows some of the code',
   new: 'New to the code',
};

/** The familiarity levels, from the most familiar to the least. */
export const familiarityLevels: readonly Familiarity[] = ['familiar', 'some', 'new'];

/** The short name of each familiarity level, as the level picker's buttons show it. */
export const familiarityShortLabels: Record<Familiarity, string> = { familiar: 'Familiar', some: 'Some', new: 'New' };

export const stopKindLabels: Record<StopKind, string> = {
   overview: 'Overview',
   context: 'Context',
   inspect: 'Inspect',
   risk: 'Risk',
   question: 'Question',
};

/** The status that the plan shows after a stop's title, or null for a ready stop, which shows none. */
export const stopStatusLabels: Record<StopStatus, string | null> = {
   pending: 'Writing',
   ready: null,
   revised: 'Revised',
};

/** A pending stop's status in the plan while no session is attached, so no one is writing it. */
export const unwrittenStopLabel = 'Not written yet';

/** The tooltip of the plan's Revised marker. */
export const revisedTooltip = 'Changed since you last opened it';

/** Returns the tooltip of a skip control, which says what a skip does, or for a skipped stop what taking the skip back does. */
export function skipTooltip(skipped: boolean): string {
   return skipped
      ? 'Take the skip back, so Next and Back stop here again.'
      : 'Next and Back pass over this stop, and the summary lists it as skipped. Claude leaves it out of rewrites for a new familiarity level, and leaves its risk out of PR comments. You can take this back at any time.';
}

/** Returns what a done state says of a stop that a revision changed after its done mark, or null for another state. */
function earlierDoneText(done: DoneState): string | null {
   return done.state === 'earlier' ? `Done on version ${done.version}. Claude revised it since.` : null;
}

/** Returns the tooltip of the plan's done checkbox, which says the stop's done state against its current version, then what the click does. */
export function doneTooltip(done: DoneState, click: DoneClick): string {
   const earlier = earlierDoneText(done);
   const state = earlier ?? (done.state === 'done' ? 'Done.' : null);
   const action = click.clears
      ? 'Take the mark back.'
      : click.earlier
        ? `Mark version ${click.version} done.${earlier === null ? ' Claude revised it since.' : ''}`
        : earlier !== null
          ? 'Mark the current version done.'
          : 'Mark this stop done. The plan counts the stops done at their current version.';
   return state === null ? action : `${state} ${action}`;
}

/**
 * Returns the accessible label of the plan's done checkbox for the stop with this number. Its checked state tells whether the current version is done, so the label adds the tooltip only when
 * the mark is on an earlier version or the click targets one.
 */
export function doneCheckboxLabel(number: number, done: DoneState, click: DoneClick): string {
   return done.state !== 'earlier' && !click.earlier ? `Stop ${number} done` : `Stop ${number} done. ${doneTooltip(done, click)}`;
}

/** Returns what a plan row's label adds for a done state, or null when the stop is not done. */
export function doneRowLabel(done: DoneState): string | null {
   return done.state === 'done' ? 'done' : done.state === 'earlier' ? `done on version ${done.version}` : null;
}

/** Returns the tooltip of the stop header's Mark done button, which says what a click does. */
export function markDoneTooltip(done: DoneState): string {
   const earlier = earlierDoneText(done);
   if (earlier !== null) {
      return `${earlier} Mark this version done.`;
   }

   return done.state === 'done' ? 'Take the done mark back.' : 'Mark this version of the stop done. If Claude revises the stop, the plan shows that you read an earlier version.';
}

/** What the plan approval form says about a stop that the reviewer skips before the approval. */
export const skipBeforeApprovalNote = 'Claude still writes a stop you skip, so you can take the skip back later.';

export const passageRoleLabels: Record<PassageRole, string> = {
   primary: 'Primary passage',
   related: 'Related passage',
};

export const changeKindLabels: Record<FileChangeKind, string> = {
   added: 'Added',
   modified: 'Modified',
   deleted: 'Deleted',
   renamed: 'Renamed',
};

/** The tag of a repository entry that is not a plain file or a folder. */
export const repositoryEntryTags: Partial<Record<RepositoryEntryType, string>> = {
   symlink: 'symbolic link',
   submodule: 'submodule',
};

/** The tag of a changed file whose only change is its line endings. */
export const lineEndingsOnlyLabel = 'line endings only';

/** The tag of a changed file that Claude marked. */
export const fileMarkLabels: Record<FileMarkKind, string> = {
   mechanical: 'mechanical',
   generated: 'generated',
};

/** Returns a size in bytes as the reader would say it, in bytes, KiB or MiB with one decimal. */
export function formatSize(bytes: number): string {
   if (bytes < 1024) {
      return `${bytes} bytes`;
   }

   return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KiB` : `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
}

/** Returns where lines at a side are, as "line 20" or "lines 5–7", with "at the base" after lines at the base. */
export function linesLabel(reference: CodeReference): string {
   const lines = reference.startLine === reference.endLine ? `line ${reference.startLine}` : `lines ${reference.startLine}–${reference.endLine}`;
   return reference.side === 'base' ? `${lines} at the base` : lines;
}

/** Returns a stop's position in the plan, as "Stop 2", or "A stop no longer in the plan" for a stop that the plan lacks. */
export function stopNumberLabel(stopId: string, plan: Plan | null): string {
   const index = plan?.stops.findIndex((stop) => stop.id === stopId) ?? -1;
   return index < 0 ? 'A stop no longer in the plan' : `Stop ${index + 1}`;
}

/** Returns where a thread was asked, as the Threads panel names it, with the position and title of an anchor's stop in the plan. */
export function anchorLabel(anchor: ThreadAnchor | AnchorInput, plan: Plan | null): string {
   const stopLabel = (stopId: string) => {
      const index = plan?.stops.findIndex((stop) => stop.id === stopId) ?? -1;
      return index < 0 ? 'A stop no longer in the plan' : `Stop ${index + 1} · ${plan!.stops[index]!.title}`;
   };
   switch (anchor.kind) {
      case 'tour':
         return 'The tour';
      case 'stop':
         return stopLabel(anchor.stopId);
      case 'stopText':
         return `${stopLabel(anchor.stopId)} · “${anchor.text}”`;
      case 'code':
         return `${anchor.path}, ${linesLabel(anchor)}`;
      case 'file':
         return anchor.side === 'base' ? `${anchor.path} at the base` : anchor.path;
   }
}

/** Returns a text on one line, with its runs of whitespace as single spaces, shortened with an ellipsis to at most `length` code points. */
export function shortened(text: string, length: number): string {
   const characters = [...text.replace(/\s+/g, ' ').trim()];
   return characters.length > length
      ? `${characters
           .slice(0, length - 1)
           .join('')
           .trimEnd()}…`
      : characters.join('');
}

const clockTime = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' });
const weekday = new Intl.DateTimeFormat(undefined, { weekday: 'short' });
const monthDay = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' });
const monthDayTime = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

/** Returns how many calendar days of the browser's time zone lie between the day of `time` and the day of `now`, 0 for the same day. */
function daysBefore(time: Date, now: Date): number {
   const day = (date: Date) => Date.UTC(date.getFullYear(), date.getMonth(), date.getDate());
   return Math.round((day(now) - day(time)) / 86_400_000);
}

/**
 * Returns when a thread last changed, as its row in the thread list shows it: the clock time on the day of `now`, then "Yesterday", the weekday within the week, and the date before
 * that. A timestamp that does not parse shows as it is.
 */
export function rowTime(timestamp: string, now: Date): string {
   const time = new Date(timestamp);
   if (Number.isNaN(time.getTime())) {
      return timestamp;
   }

   const days = daysBefore(time, now);
   return days <= 0 ? clockTime.format(time) : days === 1 ? 'Yesterday' : days < 7 ? weekday.format(time) : monthDay.format(time);
}

/** Returns when a message was written, as the line under it shows it: the clock time on the day of `now`, and the date with the time before that. A timestamp that does not parse shows as it is. */
export function messageTime(timestamp: string, now: Date): string {
   const time = new Date(timestamp);
   if (Number.isNaN(time.getTime())) {
      return timestamp;
   }

   return daysBefore(time, now) <= 0 ? clockTime.format(time) : monthDayTime.format(time);
}
