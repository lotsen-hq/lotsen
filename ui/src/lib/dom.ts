/** Submits the form of a question box on Ctrl+Enter or Cmd+Enter, the reviewer's shortcut to send. */
export function sendOnModifiedEnter(event: KeyboardEvent): void {
   if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      (event.currentTarget as HTMLTextAreaElement).form?.requestSubmit();
   }
}

/** Submits the form of a one-line question box on Enter, Ctrl+Enter or Cmd+Enter. Shift+Enter adds a line, and an Enter that ends an input method's composition submits nothing. */
export function sendOnEnter(event: KeyboardEvent): void {
   if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      (event.currentTarget as HTMLTextAreaElement).form?.requestSubmit();
   }
}

/** Returns the text of an HTML fragment, without its markup or its link targets. */
export function htmlText(html: string): string {
   return new DOMParser().parseFromString(html, 'text/html').body.textContent ?? '';
}
