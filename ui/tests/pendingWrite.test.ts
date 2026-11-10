import { describe, expect, it } from 'vitest';
import { PendingWrite } from '../src/lib/pendingWrite.svelte';
import type { WriteResult } from '../src/lib/threadActions.svelte';

/** A write that ends when the test settles it. */
function deferred(): { write: () => Promise<WriteResult>; settle: (result: WriteResult) => void; calls: () => number } {
   let settle: (result: WriteResult) => void = () => {};
   let calls = 0;
   return {
      write: () => {
         calls++;
         return new Promise<WriteResult>((resolve) => (settle = resolve));
      },
      settle: (result) => settle(result),
      calls: () => calls,
   };
}

describe('a pending write', () => {
   it('is busy while its write runs, and not before or after', async () => {
      const pending = new PendingWrite();
      const { write, settle } = deferred();
      expect(pending.busy).toBe(false);

      const run = pending.run(write);
      expect(pending.busy).toBe(true);
      settle({ ok: true });

      await expect(run).resolves.toEqual({ ok: true });
      expect(pending.busy).toBe(false);
   });

   it('shows the reason of a refusal, and clears it once the next write succeeds', async () => {
      const pending = new PendingWrite();

      await pending.run(async () => ({ ok: false, refusal: 'The thread is gone.' }));
      expect(pending.refusal).toBe('The thread is gone.');

      const { write, settle } = deferred();
      const run = pending.run(write);
      expect(pending.refusal).toBeNull();
      settle({ ok: true });
      await run;
      expect(pending.refusal).toBeNull();
   });

   it('ignores a second run while the first runs', async () => {
      const pending = new PendingWrite();
      const first = deferred();
      const second = deferred();

      const run = pending.run(first.write);
      await expect(pending.run(second.write)).resolves.toBeNull();
      first.settle({ ok: false, refusal: 'Refused.' });
      await run;

      expect(second.calls()).toBe(0);
      expect(pending.refusal).toBe('Refused.');
   });

   it('drops a refusal that the reviewer dismissed', async () => {
      const pending = new PendingWrite();
      await pending.run(async () => ({ ok: false, refusal: 'Refused.' }));

      pending.clear();

      expect(pending.refusal).toBeNull();
   });
});
