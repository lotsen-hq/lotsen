/**
 * Returns the offset where each line of the text starts in an editor's document of the text, in order.
 *
 * The lines are counted as git counts them, which end at each LF only, so a lone CR does not start a line here as it does in the editor. Each CRLF before a line makes the document one code unit shorter.
 */
export function gitLineStarts(text: string): number[] {
   const starts = [0];
   let pairs = 0;
   for (let index = text.indexOf('\n'); index >= 0 && index < text.length - 1; index = text.indexOf('\n', index + 1)) {
      if (text[index - 1] === '\r') {
         pairs++;
      }

      starts.push(index + 1 - pairs);
   }

   return starts;
}

/** Returns the text of a 1-based git line, without its LF or CRLF, or null when the text has no such line. */
export function gitLineText(text: string, line: number): string | null {
   let start = 0;
   for (let current = 1; current < line; current++) {
      const end = text.indexOf('\n', start);
      if (end < 0 || end === text.length - 1) {
         return null;
      }

      start = end + 1;
   }

   const end = text.indexOf('\n', start);
   const found = end < 0 ? text.slice(start) : text.slice(start, end);
   return found.endsWith('\r') ? found.slice(0, -1) : found;
}

/** Returns the 1-based git line that holds an offset of the editor's document, from the line starts that {@link gitLineStarts} returns. */
export function gitLineAt(starts: readonly number[], offset: number): number {
   let low = 0;
   let high = starts.length;
   while (low < high) {
      const middle = (low + high) >> 1;
      if (starts[middle]! <= offset) {
         low = middle + 1;
      } else {
         high = middle;
      }
   }

   return Math.max(low, 1);
}
