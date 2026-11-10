import type { WriteResult } from './threadActions.svelte';

/** One control's write in flight and the reason of its last refusal, for the control to show. Reads are reactive. */
export class PendingWrite {
   private running = $state(false);
   private reason = $state<string | null>(null);

   /** Whether the control's write is in flight. */
   get busy(): boolean {
      return this.running;
   }

   /** The reason of the control's last refused write, or null when its last write succeeded or none ended yet. */
   get refusal(): string | null {
      return this.reason;
   }

   /**
    * Runs the write unless one is in flight, and keeps its refusal's reason until the next run.
    *
    * @returns How the write ended, or null when a write in flight made this run do nothing.
    */
   async run(write: () => Promise<WriteResult>): Promise<WriteResult | null> {
      if (this.running) {
         return null;
      }

      this.running = true;
      this.reason = null;
      try {
         const result = await write();
         this.reason = result.ok ? null : result.refusal;
         return result;
      } finally {
         this.running = false;
      }
   }

   /** Drops the last refusal's reason. */
   clear(): void {
      this.reason = null;
   }
}
