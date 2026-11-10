import type { DiffLayout } from './diffLayout';
import { numberPreference, preference } from './preference';
import { changedFilesAt } from './repositoryTree';
import { contentOf } from './stops';
import type { ChangedFile, Stop, StopContent, StopPlacement, StopPassage } from './types';
import type { KnownDiff, KnownText } from './viewState';

/** Where the page shows stops' explanations: all in the Stop panel, all inline below their primary passage, or each where the mixed rule puts it. */
export type PlacementMode = 'side' | 'inline' | 'mixed';

/** The placement modes in the order that the settings offer them, with their labels. */
export const placementModes: readonly { value: PlacementMode; label: string }[] = [
   { value: 'side', label: 'Side panel' },
   { value: 'inline', label: 'Inline' },
   { value: 'mixed', label: 'Mixed' },
];

export const placementModePreference = preference<PlacementMode>(
   'lotsen.placement',
   placementModes.map((mode) => mode.value),
   'mixed',
);

/** The values that the mixed threshold allows, in characters. */
export const mixedThresholdBounds = { min: 0, max: 100_000 };

/** The body length in characters above which mixed mode shows a stop in the Stop panel, 600 when none is stored. */
export const mixedThresholdPreference = numberPreference('lotsen.mixedThreshold', mixedThresholdBounds, 600);

export interface PlacementOptions {
   mode: PlacementMode;
   /** The body length in characters above which the mixed rule picks the side panel. */
   threshold: number;
   /** The diff layout that the settings choose. */
   layout: DiffLayout;
   /** The pull request's changed files, or null or undefined while they are unknown, which counts each path as a file and each passage as shown in a diff. */
   files?: readonly ChangedFile[] | null;
   /** Where the reviewer moved the stop, which wins in mixed mode, or null or undefined when the reviewer did not move it. */
   override?: StopPlacement | null;
}

/**
 * Returns where a stop's explanation shows.
 *
 * A stop without a primary passage shows in the side panel in every mode, because inline it would have no line to sit below. In mixed mode the reviewer's toggle wins, then Claude's hint, then the
 * rule: the side panel for an overview or a risk, a stop with checks, passages in more than one changed file or path, a body over the threshold as {@link bodyLength} counts it, or a primary passage
 * shown in a side-by-side diff, whose panes are each too narrow for prose.
 */
export function resolvePlacement(stop: Stop | null, options: PlacementOptions): StopPlacement {
   const content = contentOf(stop);
   const primary = primaryPassage(stop);
   if (stop === null || !content || primary === null || options.mode === 'side') {
      return 'side';
   }

   if (options.mode === 'inline') {
      return 'inline';
   }

   return options.override ?? content.placement ?? mixedRule(stop.kind, content, primary, options);
}

function mixedRule(kind: Stop['kind'], content: StopContent, primary: StopPassage, { threshold, layout, files }: PlacementOptions): StopPlacement {
   const fileOf = changedFileOf(files ?? null);
   const passageFiles = new Set(content.passages.map((passage) => fileOf(passage)?.path ?? passage.path));
   // A file that the pull request does not change shows as its text, in one pane.
   const sideBySide = layout === 'sideBySide' && (!files || fileOf(primary) !== undefined);
   const side = kind === 'overview' || kind === 'risk' || (content.checks?.length ?? 0) > 0 || passageFiles.size > 1 || bodyLength(content) > threshold || sideBySide;
   return side ? 'side' : 'inline';
}

/** Returns the function that finds the changed file a passage is in, by its path at the passage's side, so a renamed file's old path at the base finds it. */
function changedFileOf(files: readonly ChangedFile[] | null): (passage: StopPassage) => ChangedFile | undefined {
   const sides = { base: changedFilesAt(files ?? [], 'base'), head: changedFilesAt(files ?? [], 'head') };
   return (passage) => sides[passage.side].get(passage.path);
}

/** Returns the stop's primary passage, or null when it has none or no content. */
export function primaryPassage(stop: Stop | null): StopPassage | null {
   return contentOf(stop)?.passages.find((passage) => passage.role === 'primary') ?? null;
}

const bodyFields = ['how', 'why', 'designChoices', 'pinnedBy', 'scenario'] as const;

/** Returns the length of a stop's body in Unicode code points: its `how`, `why`, `designChoices`, `pinnedBy` and `scenario` Markdown, each {@link withoutReferenceTargets}. */
export function bodyLength(content: StopContent): number {
   return bodyFields.reduce((total, field) => total + [...withoutReferenceTargets(content[field] ?? '')].length, 0);
}

// The forms of a link destination that Markdown allows, with the `ref:` scheme in any letter case: an inline link's after its label, with balanced parentheses and an optional title, a reference
// definition's whole line, and an autolink. A backslash before the bracket escapes it, so that text is no link.
const linkTarget = /(?<!\\)\]\(\s*(?:<ref:[^>\n]*>|ref:(?:[^\s()\\]|\\.|\((?:[^\s()\\]|\\.)*\))*)(?:\s+(?:"[^"]*"|'[^']*'|\([^)]*\)))?\s*\)/gi;
const definition = /^ {0,3}\[(?:[^\]\\\n]|\\.)+\]:[ \t]*\n?[ \t]*(?:<ref:[^>\n]*>|ref:\S*)(?:[ \t]+(?:"[^"]*"|'[^']*'|\([^)]*\)))?[ \t]*(?:\n|$)/gim;
const autolink = /(?<!\\)<ref:[^>\s]*>/gi;
const fenceOpening = /^ {0,3}(`{3,}|~{3,})/;

/**
 * Returns Markdown with the target of each `ref:` link removed: an inline link keeps its bracketed label, as in `[the call]`, and a reference definition and an autolink go whole.
 *
 * Fenced code blocks and code spans keep their text, because the server shows it as it is.
 */
export function withoutReferenceTargets(markdown: string): string {
   const parts: string[] = [];
   let prose = '';
   let fence: string | null = null;
   for (const line of markdown.split(/(?<=\n)/)) {
      if (fence !== null) {
         parts.push(line);
         if (new RegExp(`^ {0,3}${fence[0]}{${fence.length},}[ \\t]*\\r?\\n?$`).test(line)) {
            fence = null;
         }

         continue;
      }

      // A backtick fence's info string holds no backtick.
      const opening = fenceOpening.exec(line);
      if (opening !== null && !(opening[1]!.startsWith('`') && line.slice(opening[0].length).includes('`'))) {
         parts.push(withoutProseTargets(prose), line);
         prose = '';
         fence = opening[1]!;
      } else {
         prose += line;
      }
   }

   return parts.join('') + withoutProseTargets(prose);
}

/** Removes the targets from Markdown outside fenced code blocks, keeping the text of its code spans, which never cross a blank line. */
function withoutProseTargets(prose: string): string {
   return prose
      .replace(definition, '')
      .split(/(\r?\n[ \t]*\r?\n)/)
      .map((paragraph, index) => (index % 2 === 1 ? paragraph : withoutParagraphTargets(paragraph)))
      .join('');
}

/** Removes the targets from one paragraph, keeping the text of its code spans. */
function withoutParagraphTargets(text: string): string {
   const parts: string[] = [];
   let rest = 0;
   const runs = /`+/g;
   for (let run = runs.exec(text); run !== null; run = runs.exec(text)) {
      // A backslash before a run escapes its first backtick, unless a backslash escapes that backslash.
      const escaped = /\\*$/.exec(text.slice(rest, run.index))![0].length % 2 === 1;
      const opener = escaped ? run[0].slice(1) : run[0];
      const start = run.index + run[0].length - opener.length;
      if (opener === '') {
         continue;
      }

      // A span closes at the next run of exactly as many backticks. A run with no such match is plain text.
      const closing = new RegExp(`(?<!\`)${opener}(?!\`)`, 'g');
      closing.lastIndex = start + opener.length;
      const close = closing.exec(text);
      if (close !== null) {
         const end = close.index + opener.length;
         parts.push(withoutInlineTargets(text.slice(rest, start)), text.slice(start, end));
         rest = runs.lastIndex = end;
      }
   }

   return parts.join('') + withoutInlineTargets(text.slice(rest));
}

function withoutInlineTargets(text: string): string {
   return text.replace(linkTarget, ']').replace(autolink, '');
}

/** Returns whether a file's view has settled without an editor, so it shows no inline block: its load failed or found no pin, or it holds a binary file or a large one that waits for a load. */
export function showsNoEditor(known: KnownDiff | KnownText | null): boolean {
   if (known?.state === 'failed' || known?.state === 'notPinned') {
      return true;
   }

   return known?.state === 'loaded' && ('diff' in known ? known.diff.content : known.text.content) !== 'text';
}
