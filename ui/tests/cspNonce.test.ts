import { afterEach, describe, expect, it } from 'vitest';
import { cspNonce } from '../src/lib/cspNonce';

function setMeta(content: string | null) {
   document.head.querySelector('meta[name="csp-nonce"]')?.remove();
   if (content !== null) {
      const meta = document.createElement('meta');
      meta.name = 'csp-nonce';
      meta.content = content;
      document.head.append(meta);
   }
}

afterEach(() => setMeta(null));

describe('cspNonce', () => {
   it('returns the nonce the server wrote into the page', () => {
      setMeta('r4nd0m-n0nce');
      expect(cspNonce()).toBe('r4nd0m-n0nce');
   });

   it('returns undefined when the page was not served by lotsen', () => {
      setMeta('__LOTSEN_CSP_NONCE__');
      expect(cspNonce()).toBeUndefined();
      setMeta(null);
      expect(cspNonce()).toBeUndefined();
   });
});
