// The signal of the test whose code is running, in Node, where AsyncLocalStorage carries it through the test's awaits and timers.
import { AsyncLocalStorage } from 'node:async_hooks';

const store = new AsyncLocalStorage();

/** Runs the test, and lets its code read the signal through {@link testSignal}, also after the test timed out. */
export function runWithSignal(signal, runTest) {
   return store.run(signal, runTest);
}

/** Returns the signal of the test whose code calls this, or undefined outside a test that {@link runWithSignal} runs. */
export function testSignal() {
   return store.getStore();
}
