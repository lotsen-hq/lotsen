import { describe, expect, it } from 'vitest';
import { summarizeStress, type SuiteRun } from '../scripts/stressReport.mjs';

// Cut from what `dotnet test --logger trx` writes for xunit.v3: one test that passed and one that failed.
const failedTrx = `\uFEFF<?xml version="1.0" encoding="utf-8"?>
<TestRun id="b60a1e15-34f9-484e-b1b0-5f9f6fc0fa51" name="@build-host 2026-11-06 14:04:34" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult executionId="e1a53f8e-51e4-43e0-9b5e-1a61c4846c39" testId="0499fb09-d4cc-def6-dabd-1a6003e339b3" testName="Lotsen.Tests.PinTests.Pins_the_head" computerName="build-host" duration="00:00:00.0000788" outcome="Passed" testListId="8c84fa94-04c1-424b-9868-57a2d4851a1d" relativeResultsDirectory="e1a53f8e-51e4-43e0-9b5e-1a61c4846c39" />
    <UnitTestResult executionId="551c66c2-b67a-4825-a75a-f4d1cef9daa8" testId="d59f09a0-81eb-7bf4-8dcc-1b1a5140c0d6" testName="Lotsen.Tests.PinTests.Label_names_the_pull_request(url: &quot;https://dev.azure.com/a&gt;b&quot;)" computerName="build-host" duration="00:00:00.0074426" outcome="Failed" testListId="8c84fa94-04c1-424b-9868-57a2d4851a1d" relativeResultsDirectory="551c66c2-b67a-4825-a75a-f4d1cef9daa8">
      <Output>
        <ErrorInfo>
          <Message>Assert.Equal() Failure: Strings differ
           ↓ (pos 0)
Expected: "expected &lt;a&gt; &amp; "b""
Actual:   "actual"</Message>
          <StackTrace>   at Lotsen.Tests.PinTests.Label_names_the_pull_request() in /src/tests/PinTests.cs:line 9</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
  </Results>
  <ResultSummary outcome="Failed">
    <Counters total="2" executed="2" passed="1" failed="1" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>`;

const passedTrx = `<?xml version="1.0" encoding="utf-8"?>
<TestRun id="b88e8939-7da0-4810-aa10-e3325f2a727a" name="@build-host 2026-11-06 14:04:09" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult executionId="1d652873-3201-4483-ad55-3c7dcdd33f1d" testName="Lotsen.Tests.PinTests.Pins_the_head" outcome="Passed" />
  </Results>
</TestRun>`;

// Cut from what Vitest's junit reporter writes: one test that passed and one that failed.
const failedJunit = `<?xml version="1.0" encoding="UTF-8" ?>
<testsuites name="vitest tests" tests="2" failures="1" errors="0" time="0.00632973">
    <testsuite name="tests/app/threads.test.ts" timestamp="2026-11-06T13:04:56.523Z" hostname="build-host" tests="2" failures="1" errors="0" skipped="0" time="0.00632973">
        <testcase classname="tests/app/threads.test.ts" name="threads &gt; asks a question" time="0.00230386">
        </testcase>
        <testcase classname="tests/app/threads.test.ts" name="threads &gt; replies &amp; resolves" time="0.002944137">
            <failure message="expected { a: &apos;&lt;b&gt;&apos; } to deeply equal { a: &apos;c&apos; }
second line" type="AssertionError">
AssertionError: expected { a: &apos;&lt;b&gt;&apos; } to deeply equal { a: &apos;c&apos; }
 ❯ tests/app/threads.test.ts:4:54
            </failure>
        </testcase>
    </testsuite>
</testsuites>`;

const passedJunit = `<?xml version="1.0" encoding="UTF-8" ?>
<testsuites name="vitest tests" tests="1" failures="0" errors="0" time="0.002">
    <testsuite name="tests/app/threads.test.ts" tests="1" failures="0" errors="0" skipped="0" time="0.002">
        <testcase classname="tests/app/threads.test.ts" name="threads &gt; asks a question" time="0.002">
        </testcase>
    </testsuite>
</testsuites>`;

describe('the stress summary', () => {
   const runs: SuiteRun[] = [
      { suite: 'dotnet', round: 1, exitCode: 0, reports: [{ format: 'trx', xml: passedTrx }] },
      { suite: 'ui', round: 1, exitCode: 1, reports: [{ format: 'junit', xml: failedJunit }] },
      {
         suite: 'dotnet',
         round: 2,
         exitCode: 1,
         reports: [
            { format: 'trx', xml: passedTrx },
            { format: 'trx', xml: failedTrx },
         ],
      },
      { suite: 'ui', round: 2, exitCode: 0, reports: [{ format: 'junit', xml: passedJunit }] },
      { suite: 'ui', round: 3, exitCode: 0, reports: [{ format: 'junit', xml: passedJunit }] },
   ];

   it('counts the rounds that passed and failed per suite', () => {
      const { lines } = summarizeStress(runs);
      expect(lines.slice(0, 2)).toEqual(['dotnet: 1 of 2 rounds passed, 1 failed.', 'ui: 2 of 3 rounds passed, 1 failed.']);
   });

   it('names each failed test with its round and the first line of its message', () => {
      const { lines, failed } = summarizeStress(runs);
      expect(failed).toBe(true);
      expect(lines.slice(2)).toEqual([
         '',
         'Failed tests:',
         'dotnet, round 2: Lotsen.Tests.PinTests.Label_names_the_pull_request(url: "https://dev.azure.com/a>b")',
         '   Assert.Equal() Failure: Strings differ',
         'ui, round 1: tests/app/threads.test.ts > threads > replies & resolves',
         "   expected { a: '<b>' } to deeply equal { a: 'c' }",
      ]);
   });

   it('reports a run that failed with no failed test in its reports', () => {
      const { lines, failed } = summarizeStress([{ suite: 'dotnet', round: 1, exitCode: 134, reports: [] }]);
      expect(failed).toBe(true);
      expect(lines).toEqual(['dotnet: 0 of 1 rounds passed, 1 failed.', '', 'Failed tests:', 'dotnet, round 1: no failed test in the reports', '   The suite exited with code 134.']);
   });

   it('reads past XML markup in the console output that Playwright writes as CDATA', () => {
      const playwrightJunit = `<?xml version="1.0" encoding="UTF-8"?>
<testsuites id="" name="" tests="2" failures="1" skipped="0" errors="0" time="1.2">
<testsuite name="layout.spec.ts" timestamp="2026-11-06T13:50:00.000Z" hostname="chromium" tests="2" failures="1" skipped="0" time="1.1" errors="0">
<testcase name="the page › logs a closing tag" classname="layout.spec.ts" time="0.5">
<system-out>
<![CDATA[out </testcase> text
]]>
</system-out>
<failure message="layout.spec.ts:10:5 the page › logs a closing tag" type="FAILURE">
<![CDATA[  1) layout.spec.ts:10:5 › the page › logs a closing tag
    Error: expect(received).toEqual(expected)
]]>
</failure>
</testcase>
<testcase name="the page › logs a failure tag" classname="layout.spec.ts" time="0.6">
<system-out>
<![CDATA[<failure message="not a failure">
]]>
</system-out>
</testcase>
</testsuite>
</testsuites>`;
      expect(summarizeStress([{ suite: 'e2e', round: 1, exitCode: 1, reports: [{ format: 'junit', xml: playwrightJunit }] }]).lines).toEqual([
         'e2e: 0 of 1 rounds passed, 1 failed.',
         '',
         'Failed tests:',
         'e2e, round 1: layout.spec.ts > the page › logs a closing tag',
         '   layout.spec.ts:10:5 the page › logs a closing tag',
      ]);
   });

   it('passes when every round passed', () => {
      expect(summarizeStress([{ suite: 'dotnet', round: 1, exitCode: 0, reports: [{ format: 'trx', xml: passedTrx }] }])).toEqual({
         lines: ['dotnet: 1 of 1 rounds passed, 0 failed.'],
         failed: false,
      });
   });
});
