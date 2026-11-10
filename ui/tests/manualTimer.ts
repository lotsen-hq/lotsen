import type { Schedule } from '../src/lib/eventStream';

/** A timer for the event stream's silence limit that fires only when the test advances it. */
export interface ManualTimer {
   readonly schedule: Schedule;
   /** Returns the time left on each armed callback, in milliseconds, soonest first. */
   armed(): number[];
   /** Moves the time on, and runs each callback that comes due, in due order. */
   advance(ms: number): void;
}

export function manualTimer(): ManualTimer {
   let now = 0;
   const timers: { due: number; run: () => void }[] = [];
   return {
      schedule(run, ms) {
         const timer = { due: now + ms, run };
         timers.push(timer);
         return () => {
            const index = timers.indexOf(timer);
            if (index >= 0) {
               timers.splice(index, 1);
            }
         };
      },
      armed: () => timers.map((timer) => timer.due - now).sort((a, b) => a - b),
      advance(ms) {
         now += ms;
         for (;;) {
            const next = timers.filter((timer) => timer.due <= now).sort((a, b) => a.due - b.due)[0];
            if (next === undefined) {
               return;
            }

            timers.splice(timers.indexOf(next), 1);
            next.run();
         }
      },
   };
}
