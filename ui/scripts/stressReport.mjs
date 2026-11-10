/** The outcomes in a trx that count as a failed test. */
const failedTrxOutcomes = ['Failed', 'Error', 'Timeout', 'Aborted'];

/**
 * Returns the summary of a stress run: the rounds that passed and failed per suite, then each failed test.
 *
 * @param {readonly import('./stressReport.d.mts').SuiteRun[]} runs Each suite's run in each round, in the order the summary lists the suites.
 * @returns {{ lines: string[], failed: boolean }} The summary's lines, and whether any run failed.
 */
export function summarizeStress(runs) {
   const suites = [...new Set(runs.map((run) => run.suite))];
   const failures = runs.flatMap((run) => failuresOf(run).map((failure) => ({ ...failure, run })));
   const failedRuns = new Set(failures.map((failure) => failure.run));

   const lines = suites.map((suite) => {
      const ofSuite = runs.filter((run) => run.suite === suite);
      const failed = ofSuite.filter((run) => failedRuns.has(run)).length;
      return `${suite}: ${ofSuite.length - failed} of ${ofSuite.length} rounds passed, ${failed} failed.`;
   });

   if (failures.length > 0) {
      lines.push('', 'Failed tests:');
      const ordered = suites.flatMap((suite) => failures.filter((failure) => failure.run.suite === suite).sort((a, b) => a.run.round - b.run.round));
      for (const { run, name, message } of ordered) {
         lines.push(`${run.suite}, round ${run.round}: ${name}`, `   ${message}`);
      }
   }

   return { lines, failed: failures.length > 0 };
}

/** Returns the failed tests of one run, or one entry for the run itself when it exited with an error and its reports name no failed test, as a crash before the reports leaves them. */
function failuresOf(run) {
   const failures = run.reports.flatMap((report) => (report.format === 'trx' ? trxFailures : junitFailures)(escapeCdata(report.xml)));
   if (failures.length === 0 && run.exitCode !== 0) {
      return [{ name: 'no failed test in the reports', message: `The suite exited with code ${run.exitCode}.` }];
   }

   return failures;
}

function trxFailures(xml) {
   return elements(xml, 'UnitTestResult')
      .filter(({ attributes }) => failedTrxOutcomes.includes(attributes.outcome))
      .map(({ attributes, body }) => ({ name: attributes.testName ?? '(unnamed test)', message: firstLine(decode(/<Message>([\s\S]*?)<\/Message>/.exec(body)?.[1] ?? '')) }));
}

function junitFailures(xml) {
   return elements(xml, 'testcase').flatMap(({ attributes, body }) => {
      const failure = [...elements(body, 'failure'), ...elements(body, 'error')][0];
      if (failure === undefined) {
         return [];
      }

      const name = [attributes.classname, attributes.name].filter((part) => part !== undefined).join(' > ');
      return [{ name, message: firstLine(failure.attributes.message ?? decode(failure.body)) }];
   });
}

/** Returns each element with the tag, its attributes decoded and its body as raw XML, empty for a self-closing element. The tag's elements must not nest. */
function elements(xml, tag) {
   const pattern = new RegExp(`<${tag}((?:\\s+[\\w:.-]+\\s*=\\s*(?:"[^"]*"|'[^']*'))*)\\s*(?:/>|>([\\s\\S]*?)</${tag}\\s*>)`, 'g');
   return [...xml.matchAll(pattern)].map((match) => ({ attributes: attributesOf(match[1]), body: match[2] ?? '' }));
}

function attributesOf(text) {
   return Object.fromEntries([...text.matchAll(/([\w:.-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g)].map((match) => [match[1], decode(match[2] ?? match[3])]));
}

/** Returns the XML with each CDATA section written as escaped character data, so markup in a test's output, such as `</testcase>`, cannot end an element. */
function escapeCdata(xml) {
   return xml.replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, (_, text) => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;'));
}

const namedEntities = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };

/** Returns the text of XML character data or an attribute value, with the entities replaced. */
function decode(text) {
   return text.replace(/&(lt|gt|amp|quot|apos|#\d+|#x[\da-fA-F]+);/g, (_, entity) => {
      if (entity.startsWith('#x')) {
         return String.fromCodePoint(parseInt(entity.slice(2), 16));
      }

      return entity.startsWith('#') ? String.fromCodePoint(Number(entity.slice(1))) : namedEntities[entity];
   });
}

/** Returns the first line of the text that is not blank, trimmed, or an empty string when every line is blank. */
function firstLine(text) {
   return (
      text
         .split(/\r?\n|\r/)
         .map((line) => line.trim())
         .find((line) => line !== '') ?? ''
   );
}
