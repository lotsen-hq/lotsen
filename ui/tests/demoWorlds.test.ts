import { describe, expect, it } from 'vitest';
import { SpareWorld, foreignPageRefusal } from '../demo/worlds.mjs';

describe("the demo server's worlds and control API", () => {
   it.each([
      [{ host: 'localhost:5173' }],
      [{ host: 'localhost:5173', origin: 'http://localhost:5173' }],
      [{ host: '127.0.0.1:4173', origin: 'http://127.0.0.1:4173' }],
      [{ host: '[::1]:5173', origin: 'http://[::1]:5173' }],
   ])("serve the demo's own page and a request from no page: %o", (headers) => {
      expect(foreignPageRefusal(headers)).toBeNull();
   });

   it.each([
      [{ host: 'localhost:5173', origin: 'https://example.com' }, /page of https:\/\/example.com/],
      [{ host: 'localhost:5173', origin: 'http://localhost:3000' }, /page of http:\/\/localhost:3000/],
      [{ host: 'localhost:5173', origin: 'null' }, /page of null/],
      [{ host: 'evil.example:5173', origin: 'http://evil.example:5173' }, /only at localhost, not at evil.example:5173/],
      [{ host: 'localhost.evil.example:5173' }, /only at localhost/],
      [{}, /only at localhost/],
   ])('refuse a page of another origin and another host name: %o', (headers, reason) => {
      expect(foreignPageRefusal(headers)).toMatch(reason);
   });
});

describe("the demo's spare world", () => {
   // Each start waits until the test resolves it, as a world does while the host copies the template.
   interface HeldStart {
      resolve(world: number): void;
      reject(error: Error): void;
   }

   function startsOnHold(): { starts: HeldStart[]; start: () => Promise<number> } {
      const starts: HeldStart[] = [];
      const start = () => new Promise<number>((resolve, reject) => void starts.push({ resolve, reject }));
      return { starts, start };
   }

   it('gives three loads that come while the spare starts a world each, and starts one spare beyond them', async () => {
      const { starts, start } = startsOnHold();
      const spare = new SpareWorld(start);

      const taken = [spare.take(), spare.take(), spare.take()];
      starts.forEach((started, index) => started.resolve(index));

      expect(await Promise.all(taken)).toEqual([0, 1, 2]);
      expect(starts).toHaveLength(4);
   });

   it('starts a world for the next load when the spare failed before a load took it', async () => {
      const { starts, start } = startsOnHold();
      const spare = new SpareWorld(start, () => {});
      starts[0]!.reject(new Error('The host stopped.'));
      await Promise.resolve();

      const taken = spare.take();
      starts[1]!.resolve(1);

      await expect(taken).resolves.toBe(1);
   });
});
