// The demo's server side, in Vite's dev server and preview: one test host, a template of the sample tours, and a fresh world from that template for each load of the page.
import { request } from 'node:http';
import { connectBackend } from '../tests/backend/backend.ts';
import { startHost } from '../tests/backend/host.mjs';
import { worldHeader, worldPath } from './pageWorld.ts';
import { sampleTemplateName, seedSampleTours } from './seed.ts';

// A world without an open event stream and without a request for this long belongs to a closed page.
const idleWorldMs = 60_000;

/**
 * One page's world, with when the page last reached it, its open event streams, and the real time that the world's clock shows, which only moves when the demo advances it.
 *
 * @typedef {{ world: import('../tests/backend/backend.ts').World; lastSeen: number; streams: number; clockAt: number }} Tracked
 */

/** Returns the plugin that serves the demo's worlds: `POST /__demo/world`, the page's `/api/*` requests to its world, and `/control/*` to the host with the host's token. */
export function demoWorlds() {
   /** @type {Demo | undefined} */
   let demo;
   const install = (middlewares, httpServer) => {
      demo ??= new Demo();
      middlewares.use((request, response, next) => void demo.handle(request, response, next));
      httpServer?.once('close', () => void demo?.stop());
   };

   return {
      name: 'lotsen-demo-worlds',
      configureServer: (server) => install(server.middlewares, server.httpServer),
      configurePreviewServer: (server) => install(server.middlewares, server.httpServer),
   };
}

const loopbackNames = new Set(['localhost', '127.0.0.1', '[::1]']);

/**
 * Returns why the demo refuses a request for a world or for the host's control API, or null when it takes it.
 *
 * The demo adds the host's token to a control request, so it takes one only from its own page: a request to a loopback name, from no page or from a page of that same origin.
 *
 * @param {{ host?: string; origin?: string }} headers - The request's headers.
 */
export function foreignPageRefusal(headers) {
   const host = headers.host ?? '';
   const hostname = host.startsWith('[') ? host.slice(0, host.indexOf(']') + 1) : host.split(':')[0];
   if (!loopbackNames.has(hostname)) {
      return `The demo serves its worlds only at localhost, not at ${host}.`;
   }

   if (headers.origin !== undefined && headers.origin !== `http://${host}` && headers.origin !== `https://${host}`) {
      return `The demo serves its worlds only to its own page, not to a page of ${headers.origin}.`;
   }

   return null;
}

/**
 * Holds the world that the next load of the page takes, which starts before that load comes, so the load need not wait for the host's build and the template's seed.
 *
 * @template T
 */
export class SpareWorld {
   /** @type {() => Promise<T>} */
   #start;
   /** @type {(error: unknown) => void} */
   #report;
   /** @type {Promise<T> | null} */
   #next;

   /**
    * @param {() => Promise<T>} start - Starts a world.
    * @param {(error: unknown) => void} [report] - Reports the failure of a spare that no load took.
    */
   constructor(start, report = (error) => console.error(`The demo could not start a world: ${String(error)}`)) {
      this.#start = start;
      this.#report = report;
      this.#next = this.#begin();
   }

   /** Returns the spare world, and starts the next spare in the same step, so loads that come together never share a world. */
   take() {
      const taken = this.#next ?? this.#start();
      this.#next = this.#begin();
      return taken;
   }

   #begin() {
      const started = this.#start();
      // A spare that fails before a load takes it leaves no spare, so the next load starts its own world. A load that took it gets the failure.
      started.catch((error) => {
         if (this.#next === started) {
            this.#next = null;
            this.#report(error);
         }
      });
      return started;
   }
}

class Demo {
   /** @type {Map<string, Tracked>} */
   worlds = new Map();

   constructor() {
      this.host = startHost();
      this.backend = this.host.then((host) => connectBackend(host.address));
      this.template = this.backend.then((backend) => backend.template(sampleTemplateName, seedSampleTours));
      this.spare = new SpareWorld(() => this.start());
   }

   async handle(request, response, next) {
      const path = request.url ?? '';
      const refused = path === worldPath || path.startsWith('/control/') ? foreignPageRefusal(request.headers) : null;
      if (refused !== null) {
         response.writeHead(403, { 'Content-Type': 'text/plain; charset=utf-8' });
         response.end(refused);
         return;
      }

      try {
         if (path === worldPath && request.method === 'POST') {
            await this.newWorld(response);
         } else if (path.startsWith('/control/')) {
            await this.control(request, response);
         } else if (path.startsWith('/api/')) {
            await this.api(request, response);
         } else {
            next();
         }
      } catch (error) {
         if (!response.headersSent) {
            response.writeHead(502, { 'Content-Type': 'text/plain; charset=utf-8' });
         }

         response.end(`The demo could not serve ${path}: ${String(error)}`);
      }
   }

   /** Stops the host, which stops every world it started. */
   async stop() {
      await (await this.host).stop();
   }

   async start() {
      const world = await (await this.backend).world({ template: await this.template, clockStart: new Date().toISOString() });
      return { world, lastSeen: Date.now(), streams: 0, clockAt: Date.now() };
   }

   async newWorld(response) {
      await this.stopIdleWorlds();
      const tracked = await this.spare.take();
      this.worlds.set(tracked.world.id, tracked);
      await this.syncClock(tracked);
      /** @type {import('./pageWorld.ts').DemoWorld} */
      const answer = { world: tracked.world.id, uiCredential: tracked.world.uiCredential, code: await tracked.world.uiCode(), seed: tracked.world.seed };
      response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      response.end(JSON.stringify(answer));
   }

   async stopIdleWorlds() {
      const now = Date.now();
      for (const [id, tracked] of this.worlds) {
         if (tracked.streams === 0 && now - tracked.lastSeen > idleWorldMs) {
            this.worlds.delete(id);
            await tracked.world.dispose();
         }
      }
   }

   // Moves the world's manual clock to the real time, so what the page and the panel write shows the time it was written.
   async syncClock(tracked) {
      const now = Date.now();
      if (now - tracked.clockAt >= 1000) {
         const elapsed = now - tracked.clockAt;
         tracked.clockAt = now;
         await tracked.world.clock.advance(elapsed);
      }
   }

   async api(request, response) {
      const id = request.headers[worldHeader.toLowerCase()];
      const tracked = typeof id === 'string' ? this.worlds.get(id) : undefined;
      if (tracked === undefined) {
         response.writeHead(410, { 'Content-Type': 'text/plain; charset=utf-8' });
         response.end('This page has no world on the demo server any more. Reload the page for a new one.');
         return;
      }

      tracked.lastSeen = Date.now();
      await this.syncClock(tracked);
      const target = new URL(tracked.world.url);
      // The server refuses another Host and another Origin than its own, as a DNS rebinding page would send.
      const headers = { ...request.headers, host: target.host };
      delete headers.origin;
      delete headers[worldHeader.toLowerCase()];
      const stream = request.url.startsWith('/api/events');
      if (stream) {
         tracked.streams++;
         response.once('close', () => {
            tracked.streams--;
            tracked.lastSeen = Date.now();
         });
      }

      forward(request, response, target, headers);
   }

   async control(request, response) {
      const { address } = await this.host;
      const target = new URL(address.url);
      const body = await read(request);
      const world = JSON.parse(body.length === 0 ? '{}' : body.toString('utf8')).world;
      const tracked = typeof world === 'string' ? this.worlds.get(world) : undefined;
      if (tracked !== undefined) {
         tracked.lastSeen = Date.now();
         await this.syncClock(tracked);
      }

      const headers = { ...request.headers, host: target.host, authorization: `Bearer ${address.token}`, 'content-length': String(body.length) };
      delete headers.origin;
      forward(request, response, target, headers, body);
   }
}

// Forwards the request to the target and streams the answer back as it arrives, so the event stream reaches the page unbuffered.
function forward(incoming, response, target, headers, body) {
   const outgoing = request({ hostname: target.hostname, port: target.port, method: incoming.method, path: incoming.url, headers }, (answer) => {
      response.writeHead(answer.statusCode ?? 502, answer.headers);
      response.flushHeaders();
      answer.pipe(response);
   });
   outgoing.on('error', (error) => {
      if (!response.headersSent) {
         response.writeHead(502, { 'Content-Type': 'text/plain; charset=utf-8' });
      }

      response.end(`The demo could not reach ${target.host}: ${error.message}`);
   });
   response.once('close', () => outgoing.destroy());
   if (body === undefined) {
      incoming.pipe(outgoing);
   } else {
      outgoing.end(body);
   }
}

function read(incoming) {
   return new Promise((resolve, reject) => {
      const chunks = [];
      incoming.on('data', (chunk) => chunks.push(chunk));
      incoming.once('end', () => resolve(Buffer.concat(chunks)));
      incoming.once('error', reject);
   });
}
