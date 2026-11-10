/**
 * Gives jsdom's ranges the client rectangles of an element, which are empty without layout, until the returned function runs.
 *
 * CodeMirror measures its text through ranges when an editor scrolls, and jsdom's ranges lack these methods.
 */
export function stubRangeLayout(): () => void {
   const prototype = Range.prototype as Partial<Pick<Range, 'getClientRects' | 'getBoundingClientRect'>>;
   if (prototype.getClientRects) {
      return () => {};
   }

   prototype.getClientRects = () => document.createElement('div').getClientRects();
   prototype.getBoundingClientRect = () => document.createElement('div').getBoundingClientRect();
   return () => {
      delete prototype.getClientRects;
      delete prototype.getBoundingClientRect;
   };
}
