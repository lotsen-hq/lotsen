// The types of stressReport.mjs, for the TypeScript that imports it.

/** One test report that a suite wrote: a trx from `dotnet test`, or a JUnit XML report from Vitest or Playwright. */
export interface TestReport {
   format: 'trx' | 'junit';
   xml: string;
}

/** One suite's run in one round of a stress run. */
export interface SuiteRun {
   /** The suite's name, such as `dotnet` or `ui`. */
   suite: string;
   /** The round's number, from 1. */
   round: number;
   /** The exit code of the suite's process. */
   exitCode: number;
   /** The reports that the run wrote, none when it crashed before it wrote one. */
   reports: readonly TestReport[];
}

/**
 * Returns the summary of a stress run: the rounds that passed and failed per suite, then each failed test.
 *
 * @param runs Each suite's run in each round, in the order the summary lists the suites.
 * @returns The summary's lines, and whether any run failed.
 */
export function summarizeStress(runs: readonly SuiteRun[]): { lines: string[]; failed: boolean };
