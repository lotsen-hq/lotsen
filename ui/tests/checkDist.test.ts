import { describe, expect, it } from 'vitest';
import { findDistProblems } from '../scripts/distRules.mjs';

const page = '<!doctype html><html><head><meta name="csp-nonce" content="__LOTSEN_CSP_NONCE__"><script type="module" src="/app.js"></script><link rel="stylesheet" href="/app.css"></head></html>';
const popout = '<!doctype html><html><head><meta name="csp-nonce" content="__LOTSEN_CSP_NONCE__" /></head><body></body></html>';
// What CodeMirror's style-mod bundles: rules built in the script and a <style> element that takes a nonce.
const codeMirrorStyles =
   'class StyleSet{constructor(e,t){this.styleTag=e.createElement("style");' +
   'if(t)this.styleTag.setAttribute("nonce",t)}}' +
   'const rules=[".cm-editor {position: relative}",".ͼ1 .cm-line {padding: 0 2px}"];';
// What a component compiled with injected CSS bundles.
const svelteStyles = 'const $$css={hash:"svelte-1ha9w09",code:".tabs.svelte-1ha9w09{display:flex}"};';

function problemsOf(overrides: Record<string, string | null>): string[] {
   const files: Record<string, string | null> = { 'index.html': page, 'popout.html': popout, 'app.css': '', 'app.js': '', 'THIRD-PARTY-NOTICES.txt': '', 'favicon.svg': '', ...overrides };
   const present = Object.keys(files).filter((path) => files[path] !== null);
   return findDistProblems(present, (path) => files[path]!);
}

describe('the dist check', () => {
   it('passes CodeMirror styles in the app script and in a chunk', () => {
      expect(problemsOf({ 'app.js': codeMirrorStyles, 'chunks/lang-json.js': codeMirrorStyles })).toEqual([]);
   });

   it.each(['app.js', 'chunks/lang-json.js'])('rejects component CSS injected from %s', (path) => {
      expect(problemsOf({ [path]: svelteStyles })).toEqual([`${path} holds component CSS (.svelte-1ha9w09) to inject at run time; it must go to app.css.`]);
   });

   it('passes a font beside app.css', () => {
      expect(problemsOf({ 'seti.woff': '' })).toEqual([]);
   });

   it.each(['chunks/lang-json.css', 'logo.png', 'chunks/nested/lang.js', 'fonts/seti.woff', 'seti.woff2'])('rejects the unexpected file %s', (path) => {
      expect(problemsOf({ [path]: '' })).toEqual([expect.stringContaining(`dist holds ${path}`)]);
   });

   it('rejects a dist without the favicon', () => {
      expect(problemsOf({ 'favicon.svg': null })).toEqual(['dist lacks favicon.svg.']);
   });

   it('rejects a dist without the third-party notices', () => {
      expect(problemsOf({ 'THIRD-PARTY-NOTICES.txt': null })).toEqual(['dist lacks THIRD-PARTY-NOTICES.txt.']);
   });

   it('rejects a dist without the popout page', () => {
      expect(problemsOf({ 'popout.html': null })).toEqual(['dist lacks popout.html.']);
   });

   it('rejects a popout page without the nonce tag', () => {
      expect(problemsOf({ 'popout.html': popout.replace(/<meta name="csp-nonce"[^>]*>/, '') })).toEqual(['popout.html lost the csp-nonce meta tag with the placeholder the server replaces.']);
   });

   it('rejects a dist without the app script', () => {
      expect(problemsOf({ 'app.js': null })).toEqual(['dist lacks app.js.']);
   });

   it.each(['<meta name="csp-nonce" content="__LOTSEN_CSP_NONCE__">', '<meta name="csp-nonce" content="__LOTSEN_CSP_NONCE__" />'])('passes the nonce tag written as %s', (tag) => {
      expect(problemsOf({ 'index.html': page.replace(/<meta name="csp-nonce"[^>]*>/, tag) })).toEqual([]);
   });

   it('rejects a page without the nonce tag', () => {
      expect(problemsOf({ 'index.html': page.replace(/<meta name="csp-nonce"[^>]*>/, '') })).toEqual(['index.html lost the csp-nonce meta tag with the placeholder the server replaces.']);
   });

   it('rejects an inline script or style in the page', () => {
      const inline = page.replace('</head>', '<script>run()</script><style>p{}</style></head>');

      expect(problemsOf({ 'index.html': inline })).toEqual([
         "index.html has an inline script, which script-src 'self' blocks: <script>",
         'index.html has an inline style, which the style-src nonce does not cover.',
      ]);
   });
});
