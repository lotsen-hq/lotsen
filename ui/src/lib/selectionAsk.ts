import type { PickedAnchor } from './viewState';
import { anchorLabel, shortened } from './labels';
import type { FilePaths } from './pin';
import type { EditorSurface } from './editor/surface';
import type { LinesAtSide } from './passageMarks';
import type { ChangedFile } from './types';

/** What a selection of a stop's text offers beside the ask: the Explain tour action about the selected text, or the reason it is disabled. */
export interface SelectionExplain {
   /** Requests the action, and returns null once the server stored it, or the server's reason when it refused it. */
   send: (anchor: Extract<PickedAnchor, { kind: 'stopText' }>) => Promise<string | null>;
   /** Why the action is disabled, or null when it is not. */
   unavailable: string | null;
}

/** What a selection lets the reviewer ask about, with the label that names it and the first line of its text, which the ask box quotes, or why it offers no ask. */
export type SelectionTarget = { anchor: PickedAnchor; label: string; quote: string | null } | { refusal: string };

/** The refusal of a selection that spans lines of both sides, whose question would have no one side to anchor to. */
export const bothSidesRefusal = 'Select lines of one side to ask about them.';

/** The refusal of a selection that spans more than one prose field of a stop, since a stop text anchor lies in one field. */
export const fieldsRefusal = 'Select text of one section to ask about it.';

/** Returns what a selection of a file's editors asks about: the lines it covers at one side, or null when it covers none of a file that exists at that side. */
export function codeTarget(lines: LinesAtSide | 'bothSides' | null, paths: FilePaths, editor: Pick<EditorSurface, 'lineText'>): SelectionTarget | null {
   if (lines === 'bothSides') {
      return { refusal: bothSidesRefusal };
   }

   const path = lines === null ? null : paths[lines.side];
   if (lines === null || path === null) {
      return null;
   }

   const anchor: PickedAnchor = { kind: 'code', side: lines.side, path, startLine: lines.startLine, endLine: lines.endLine };
   return { anchor, label: anchorLabel(anchor, null), quote: editor.lineText({ side: lines.side, line: lines.startLine }) };
}

/** Returns the anchor of a changed file as a whole: the file at the head, or at the base when the pull request deletes it. */
export function changedFileAnchor(file: ChangedFile): Extract<PickedAnchor, { kind: 'file' }> {
   return { kind: 'file', side: file.kind === 'deleted' ? 'base' : 'head', path: file.path };
}

/** The longest selected text that an ask's label quotes, in code points, with the ellipsis. */
const selectionLabelLength = 60;

/**
 * Returns what a selection of a stop's text asks about: the text it covers in the one prose field it touches, each field an element with a `data-stop-field` attribute inside the container.
 *
 * @param version The number of the stop's version that the container shows, which the server refuses once a newer version replaced it, or undefined to let the server take the current one.
 * @returns The selected text, a refusal for text in more than one field, or null when it covers no text of a field.
 */
export function stopTextTarget(range: Range, container: Element, stopId: string, version?: number): SelectionTarget | null {
   const parts = [...container.querySelectorAll<HTMLElement>('[data-stop-field]')].flatMap((element) => {
      if (!range.intersectsNode(element)) {
         return [];
      }

      const part = container.ownerDocument.createRange();
      part.selectNodeContents(element);
      if (range.compareBoundaryPoints(Range.START_TO_START, part) > 0) {
         part.setStart(range.startContainer, range.startOffset);
      }

      if (range.compareBoundaryPoints(Range.END_TO_END, part) < 0) {
         part.setEnd(range.endContainer, range.endOffset);
      }

      const text = part.toString().trim();
      return text === '' ? [] : [{ field: element.dataset.stopField!, text }];
   });
   if (parts.length > 1) {
      return { refusal: fieldsRefusal };
   }

   const [selected] = parts;
   return selected === undefined
      ? null
      : {
           anchor: { kind: 'stopText', stopId, ...selected, ...(version === undefined ? {} : { version }) },
           label: `“${shortened(selected.text, selectionLabelLength)}”`,
           quote: selected.text.split('\n', 1)[0]!,
        };
}
