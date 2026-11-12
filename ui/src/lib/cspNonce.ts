import { mainPage } from './panelDocument.svelte';

const placeholder = '__LOTSEN_CSP_NONCE__';

/**
 * Returns the style nonce of this page load, which the server writes into the `csp-nonce` meta tag and the Content-Security-Policy header.
 *
 * Code that creates `<style>` elements at run time must give them this nonce, or the policy blocks them.
 *
 * @returns The nonce, or undefined when the page was not served by lotsen and so has no nonce.
 */
export function cspNonce(document: Document = mainPage.document): string | undefined {
   const content = document.querySelector('meta[name="csp-nonce"]')?.getAttribute('content');
   return content && content !== placeholder ? content : undefined;
}
