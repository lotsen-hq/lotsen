import { afterEach, describe, expect, it } from 'vitest';
import { isElement, isHTMLElement } from '../src/lib/dom';

afterEach(() => document.body.replaceChildren());

/** Returns the document of a new iframe, whose nodes belong to the iframe's realm, as a popout window's can. */
function otherRealm(): Document {
   return document.body.appendChild(document.createElement('iframe')).contentDocument!;
}

describe('the realm-independent node checks', () => {
   it("tell an element of another window's realm, which fails the main page's instanceof", () => {
      const other = otherRealm();
      const div = other.createElement('div');
      const circle = other.createElementNS('http://www.w3.org/2000/svg', 'circle');
      expect(div instanceof Element).toBe(false);

      expect([isElement(div), isHTMLElement(div)]).toEqual([true, true]);
      expect([isElement(circle), isHTMLElement(circle)]).toEqual([true, false]);
      expect([isElement(other.createTextNode('text')), isHTMLElement(other.createTextNode('text'))]).toEqual([false, false]);
   });

   it("tell an element of the main page's realm", () => {
      expect([isElement(document.createElement('span')), isHTMLElement(document.createElement('span'))]).toEqual([true, true]);
   });

   it('refuse a value that is no node', () => {
      for (const value of [null, undefined, window, 1]) {
         expect([isElement(value), isHTMLElement(value)]).toEqual([false, false]);
      }
   });
});
