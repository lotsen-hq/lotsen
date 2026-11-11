import { beforeEach } from 'vitest';
import { detachPreferences } from '../src/lib/preference';

// jsdom has no ResizeObserver, and an open tooltip measures its arrow with one.
if (typeof globalThis.ResizeObserver !== 'function') {
   globalThis.ResizeObserver = class {
      // A browser refuses anything but an element.
      observe(target: unknown) {
         if (!(target instanceof Element)) {
            throw new TypeError('ResizeObserver.observe: the target is not an element.');
         }
      }
      unobserve() {}
      disconnect() {}
   };
}

// jsdom gives every element no client rects, and bits-ui hides the content of a popover whose trigger has none. Role queries then find nothing in the popover.
// https://github.com/huntabyte/bits-ui/blob/bits-ui%402.20.2/packages/bits-ui/src/lib/internal/floating-svelte/use-floating.svelte.ts#L220-L224
Element.prototype.getClientRects = () => [new DOMRect()] as unknown as DOMRectList;

// jsdom has no IntersectionObserver, and the Stop panel watches its header with one.
if (typeof globalThis.IntersectionObserver !== 'function') {
   globalThis.IntersectionObserver = class {
      readonly root = null;
      readonly rootMargin = '';
      readonly scrollMargin = '';
      readonly thresholds = [];
      observe() {}
      unobserve() {}
      disconnect() {}
      takeRecords() {
         return [];
      }
   };
}

// The page's preferences live in one module for the whole test file, so each test starts from browser storage alone, as a page that has not read the server's preferences does.
beforeEach(() => detachPreferences());
