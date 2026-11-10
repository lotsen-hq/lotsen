import { describe, expect, it } from 'vitest';
import { connectBackend, joinWorld, Refusal, type World } from './backend/backend';

const host = { url: 'http://host.test', token: 'token' };
const info = { world: 'w1', url: 'http://server.test', uiCredential: 'ui' };

/** A fetch that answers each request with the response that `answer` returns for its path and body, and keeps the paths in order. */
function fakeFetch(answer: (path: string, body: Record<string, unknown>) => Response | Promise<Response>): { fetch: typeof fetch; paths: string[] } {
   const paths: string[] = [];
   const fetchFn: typeof fetch = async (input, init) => {
      const path = new URL(String(input)).pathname;
      paths.push(path);
      return answer(path, JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>);
   };
   return { fetch: fetchFn, paths };
}

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

describe("the test host's client", () => {
   it('names the verb, the status and the empty body of a control answer that is not JSON', async () => {
      const { fetch } = fakeFetch(() => new Response(null, { status: 500 }));
      const world = joinWorld(host, info, undefined, fetch);

      await expect(world.claude()).rejects.toThrow('claude.open answered 500 with an empty body.');
   });

   it("names the operation and the status of a server answer that is not JSON, and keeps a refusal's status", async () => {
      const { fetch } = fakeFetch((path) => (path === '/api/ops/read_state' ? new Response('', { status: 200 }) : new Response(null, { status: 401 })));
      const world = joinWorld(host, info, undefined, fetch);

      await expect(world.reviewer.call('read_state', { tourId: 't' })).rejects.toThrow('read_state answered 200 with an empty body.');
      const refused = await world.reviewer.call('list_tours', {}).catch((error: unknown) => error);
      expect(refused).toBeInstanceOf(Refusal);
      expect((refused as Refusal).status).toBe(401);
   });

   it("keeps a template's build world running through the teardown of the test that started the build, and stops it when the build fails", async () => {
      const { fetch, paths } = fakeFetch((path) => (path === '/control/template.claim' ? json({ has: false, build: info }) : json({})));
      const backend = connectBackend(host, fetch);
      let fail!: (error: Error) => void;
      const building = new Promise<never>((_, reject) => (fail = reject));
      let builds: World | undefined;
      const template = backend.template('held', async (world: World) => {
         builds = world;
         return building;
      });

      const started = backend.world({ template });
      await expect.poll(() => builds?.id).toBe(info.world);
      await backend.disposeAll();
      expect(paths).not.toContain('/control/world.stop');

      fail(new Error('The build failed.'));
      await expect(started).rejects.toThrow('The build failed.');
      expect(paths.filter((path) => path === '/control/world.stop')).toHaveLength(1);
   });

   it("stops a template's build world when the host refuses its seal", async () => {
      const { fetch, paths } = fakeFetch((path) => {
         switch (path) {
            case '/control/template.claim':
               return json({ has: false, build: info });
            case '/control/world.seal':
               return json({ error: 'world.seal: The seal failed.' }, 500);
            default:
               return json({});
         }
      });
      const backend = connectBackend(host, fetch);
      const template = backend.template('refused', async () => 'seed');

      await expect(backend.world({ template })).rejects.toThrow('world.seal: The seal failed.');

      expect(paths.filter((path) => path === '/control/world.stop')).toHaveLength(1);
   });
});
