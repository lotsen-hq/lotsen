import { mergeConfig, type Plugin } from 'vite';
import base from '../vite.config.ts';
import { popoutNonce } from './popoutNonce.mjs';
import { demoWorlds } from './worlds.mjs';

// Fixed nonces, so the built demo can be served with the server's policy without a server to make a nonce per load. The popout page has its own, as each page the server serves does.
const nonce = 'lotsen-demo';
const popoutPageNonce = 'lotsen-demo-popout';

// The policy of src/Lotsen.Core/Server/ContentSecurityPolicy.cs, with a fixed nonce.
const contentSecurityPolicy = (styleNonce: string) =>
   [
      "default-src 'self'",
      "script-src 'self'",
      `style-src 'self' 'nonce-${styleNonce}'`,
      "connect-src 'self'",
      "img-src 'self' data:",
      "object-src 'none'",
      "base-uri 'none'",
      "frame-ancestors 'none'",
      "form-action 'none'",
   ].join('; ');

// Serves the demo page at the root of the dev server and the preview, which would otherwise serve the real page there.
const demoAtRoot = (request: { url?: string }, _response: unknown, next: () => void) => {
   if (request.url === '/' || request.url === '/index.html') {
      request.url = '/demo/index.html';
   }

   next();
};

// Gives each response of the preview the policy with the nonce of its page.
// The demo has no Node types, so the response's type names only the method that the middleware calls.
const servePolicy = (request: { url?: string }, response: unknown, next: () => void) => {
   (response as { setHeader: (name: string, value: string) => void }).setHeader(
      'Content-Security-Policy',
      contentSecurityPolicy(request.url?.split('?')[0] === '/popout.html' ? popoutPageNonce : nonce),
   );
   next();
};

const demo: Plugin = {
   name: 'lotsen-demo',
   configureServer: (server) => void server.middlewares.use(demoAtRoot),
   configurePreviewServer: (server) => {
      server.middlewares.use(servePolicy);
      server.middlewares.use(demoAtRoot);
   },
   transformIndexHtml: { order: 'post', handler: (html, context) => (context.server ? html : html.replace('__LOTSEN_CSP_NONCE__', nonce)) },
};

// The demo builds to demo-dist, apart from the dist that the server embeds.
export default mergeConfig(base, {
   plugins: [demo, demoWorlds(), popoutNonce(popoutPageNonce)],
   build: { outDir: 'demo-dist', rolldownOptions: { input: 'demo/index.html' } },
});
