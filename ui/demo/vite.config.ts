import { mergeConfig, type Plugin } from 'vite';
import base from '../vite.config.ts';
import { popoutNonce } from './popoutNonce.mjs';
import { demoWorlds } from './worlds.mjs';

// A fixed nonce, so the built demo can be served with the server's policy without a server to make a nonce per load.
const nonce = 'lotsen-demo';

// The policy of src/Lotsen.Core/Server/ContentSecurityPolicy.cs, with the fixed nonce.
const contentSecurityPolicy = [
   "default-src 'self'",
   "script-src 'self'",
   `style-src 'self' 'nonce-${nonce}'`,
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

const demo: Plugin = {
   name: 'lotsen-demo',
   configureServer: (server) => void server.middlewares.use(demoAtRoot),
   configurePreviewServer: (server) => void server.middlewares.use(demoAtRoot),
   transformIndexHtml: { order: 'post', handler: (html, context) => (context.server ? html : html.replace('__LOTSEN_CSP_NONCE__', nonce)) },
};

// The demo builds to demo-dist, apart from the dist that the server embeds.
export default mergeConfig(base, {
   plugins: [demo, demoWorlds(), popoutNonce(nonce)],
   build: { outDir: 'demo-dist', rolldownOptions: { input: 'demo/index.html' } },
   preview: { headers: { 'Content-Security-Policy': contentSecurityPolicy } },
});
