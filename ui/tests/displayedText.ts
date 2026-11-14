/** Returns the element's text without the elements that the `hidden` class takes out of the page and the accessibility tree, which jsdom keeps because it applies no stylesheet. */
export function displayedText(element: Element): string {
   const copy = element.cloneNode(true) as Element;
   copy.querySelectorAll('.hidden').forEach((hidden) => hidden.remove());
   return copy.textContent.replace(/\s+/g, ' ').trim();
}
