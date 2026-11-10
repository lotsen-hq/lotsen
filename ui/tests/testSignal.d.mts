// The types of testSignal.mjs, which runs in Node only, for the TypeScript that imports it.

/** Runs the test, and lets its code read the signal through {@link testSignal}, also after the test timed out. */
export function runWithSignal(signal: AbortSignal, runTest: () => Promise<void>): Promise<void>;

/** Returns the signal of the test whose code calls this, or undefined outside a test that {@link runWithSignal} runs. */
export function testSignal(): AbortSignal | undefined;
