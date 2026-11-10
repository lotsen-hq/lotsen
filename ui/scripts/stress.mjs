// Runs the test suites round after round while busy loops load every core, and reports each failed test. `just stress --help` lists the options.
import { spawn, spawnSync } from 'node:child_process';
import { closeSync, existsSync, mkdirSync, openSync, readdirSync, readFileSync } from 'node:fs';
import { availableParallelism } from 'node:os';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { Worker } from 'node:worker_threads';
import { summarizeStress } from './stressReport.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
const ui = join(root, 'ui');
const testHost = join(root, 'tests/Lotsen.TestHost/bin/Debug/net11.0/Lotsen.TestHost.dll');
const windows = process.platform === 'win32';

const usage = `Usage: just stress [--rounds N] [--hogs N] [--suites dotnet,ui,e2e]

  --rounds N    How many rounds to run, 5 by default.
  --hogs N      How many busy loops load the CPU during the rounds, one per core (${availableParallelism()}) by default.
  --suites S    The suites that each round runs side by side, dotnet,ui by default:
                dotnet (the .NET tests), ui (the UI's unit and flow tests), e2e (the layout suite).

Each round writes its reports and output under .stress/<run>/round-<n>/. The run exits 1 when any test failed.`;

/** How each suite runs with its results in the directory, and which reports it leaves there. */
const suites = {
   dotnet: (dir) => ({
      command: 'dotnet',
      args: ['test', 'Lotsen.slnx', '--no-build', '--logger', 'trx', '--results-directory', dir],
      cwd: root,
      reports: () =>
         readdirSync(dir)
            .filter((name) => name.endsWith('.trx'))
            .map((name) => ({ format: 'trx', xml: readFileSync(join(dir, name), 'utf8') })),
   }),
   // The report's path is relative to the UI's directory, the root that Vitest resolves it against, so it holds no space for cmd.exe to split.
   // See https://github.com/vitest-dev/vitest/blob/v5.0.5/packages/vitest/src/node/reporters/junit.ts#L256.
   ui: (dir) => ({
      command: 'npm',
      args: ['test', '--', '--reporter=default', '--reporter=junit', `--outputFile.junit=${relative(ui, join(dir, 'junit.xml')).replaceAll('\\', '/')}`],
      cwd: ui,
      reports: () => junitReport(join(dir, 'junit.xml')),
   }),
   e2e: (dir) => ({
      command: 'npm',
      args: ['run', 'e2e', '--', '--reporter=list,junit'],
      env: { PLAYWRIGHT_JUNIT_OUTPUT_FILE: join(dir, 'junit.xml') },
      cwd: ui,
      reports: () => junitReport(join(dir, 'junit.xml')),
   }),
};

const children = new Set();
let interrupted = false;

function junitReport(path) {
   return existsSync(path) ? [{ format: 'junit', xml: readFileSync(path, 'utf8') }] : [];
}

function parseOptions() {
   const { values } = parseArgs({
      options: {
         rounds: { type: 'string', default: '5' },
         hogs: { type: 'string', default: String(availableParallelism()) },
         suites: { type: 'string', default: 'dotnet,ui' },
         help: { type: 'boolean' },
      },
   });
   if (values.help) {
      console.log(usage);
      process.exit(0);
   }

   const rounds = Number(values.rounds);
   const hogs = Number(values.hogs);
   const chosen = values.suites.split(',').map((suite) => suite.trim());
   const problems = [
      ...(Number.isInteger(rounds) && rounds >= 1 ? [] : [`--rounds is ${values.rounds}, which is not a whole number from 1.`]),
      ...(Number.isInteger(hogs) && hogs >= 0 ? [] : [`--hogs is ${values.hogs}, which is not a whole number from 0.`]),
      ...chosen.filter((suite) => !Object.hasOwn(suites, suite)).map((suite) => `--suites names ${suite || 'an empty suite'}, which is not dotnet, ui or e2e.`),
      ...(new Set(chosen).size === chosen.length ? [] : ['--suites names a suite twice.']),
   ];
   if (problems.length > 0) {
      throw new Error(problems.join('\n'));
   }

   return { rounds, hogs, suites: chosen };
}

/** Starts the command with the options, as the leader of its own process group outside Windows, so {@link stopTree} reaches the processes that it starts. */
function start(command, args, options) {
   // Windows runs npm.cmd only through cmd.exe (https://nodejs.org/api/child_process.html#spawning-bat-and-cmd-files-on-windows). The arguments hold no space or quote, so they need no quoting.
   if (windows) {
      return command === 'npm' ? spawn('cmd.exe', ['/d', '/c', 'npm', ...args], options) : spawn(command, args, options);
   }

   // A detached child leads a new process group (https://nodejs.org/api/child_process.html#optionsdetached).
   return spawn(command, args, { ...options, detached: true });
}

/** Stops the child and every process that it started. */
function stopTree(child, signal) {
   if (windows) {
      spawnSync('taskkill', ['/pid', String(child.pid), '/t', '/f'], { stdio: 'ignore' });
      return;
   }

   try {
      // A negative ID signals the whole process group (https://man7.org/linux/man-pages/man2/kill.2.html).
      process.kill(-child.pid, signal);
   } catch {
      // The group has no process left.
   }
}

/** Builds the solution, the test host included, before the busy loops start. */
function build() {
   return new Promise((resolve) => {
      const child = spawn('dotnet', ['build', 'Lotsen.slnx'], { cwd: root, stdio: 'inherit' });
      child.once('error', (error) => {
         console.error(`stress: dotnet build did not start: ${error.message}`);
         resolve(1);
      });
      child.once('exit', (code) => resolve(code ?? 1));
   });
}

/** Runs one suite in one round, with its output in output.log beside its reports, and returns the run for the summary. */
function runSuite(name, round, dir) {
   mkdirSync(dir, { recursive: true });
   const suite = suites[name](dir);
   const log = openSync(join(dir, 'output.log'), 'w');
   const started = Date.now();
   const child = start(suite.command, suite.args, { cwd: suite.cwd, env: { ...process.env, LOTSEN_TEST_HOST: testHost, ...suite.env }, stdio: ['ignore', log, log] });
   closeSync(log);
   children.add(child);

   return new Promise((resolve) => {
      let settled = false;
      const settle = (exitCode, cause) => {
         if (settled) {
            return;
         }

         settled = true;
         children.delete(child);
         if (!interrupted) {
            const seconds = Math.round((Date.now() - started) / 1000);
            console.log(`   ${name}: ${exitCode === 0 ? 'passed' : `failed, ${cause}`}, in ${seconds} s.`);
         }
         resolve({ suite: name, round, exitCode, reports: suite.reports() });
      };
      child.once('error', (error) => settle(1, `it did not start: ${error.message}`));
      child.once('exit', (code, signal) => settle(code ?? 1, code === null ? `killed by ${signal}` : `exit code ${code}`));
   });
}

/** Stops every suite in progress: a request to end first, then a kill of what remains after a grace period. */
async function stopChildren() {
   const running = [...children];
   for (const child of running) {
      stopTree(child, 'SIGTERM');
   }

   const exited = Promise.all(running.map((child) => new Promise((resolve) => (child.exitCode !== null || child.signalCode !== null ? resolve() : child.once('exit', resolve)))));
   // An unref'd timer lets the run end as soon as the suites have exited.
   await Promise.race([exited, new Promise((resolve) => setTimeout(resolve, 5000).unref())]);
   for (const child of running) {
      stopTree(child, 'SIGKILL');
   }
}

function plural(count, noun) {
   return `${count} ${noun}${count === 1 ? '' : 's'}`;
}

function duration(milliseconds) {
   const seconds = Math.round(milliseconds / 1000);
   return `${Math.floor(seconds / 60)} min ${seconds % 60} s`;
}

async function main() {
   let options;
   try {
      options = parseOptions();
   } catch (error) {
      console.error(`stress: ${error.message}\n\n${usage}`);
      return 2;
   }

   const started = Date.now();
   const built = await build();
   if (built !== 0) {
      console.error(`stress: dotnet build failed with exit code ${built}.`);
      return built;
   }

   if (!existsSync(testHost)) {
      console.error(`stress: the build left no test host at ${testHost}.`);
      return 1;
   }

   const run = new Date().toISOString().slice(0, 19).replaceAll(':', '-');
   const runDir = join(root, '.stress', run);
   const onSignal = () => {
      if (!interrupted) {
         interrupted = true;
         console.log('stress: stopping the suites in progress.');
         void stopChildren();
      }
   };
   // Outside Windows the suites run in sessions of their own, which the terminal's hangup and Ctrl+\ do not reach.
   // Windows sends SIGBREAK for Ctrl+Break and SIGHUP when the console closes (https://nodejs.org/api/process.html#signal-events).
   for (const signal of windows ? ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK'] : ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGQUIT']) {
      process.on(signal, onSignal);
   }
   process.on('exit', () => children.forEach((child) => stopTree(child, 'SIGKILL')));

   // Each worker spins on one core until the run ends.
   const hogs = Array.from({ length: options.hogs }, () => new Worker('for (;;) {}', { eval: true }));
   const runs = [];
   console.log(`stress: ${plural(options.rounds, 'round')} of ${options.suites.join(', ')} with ${plural(options.hogs, 'busy loop')}, into ${relative(root, runDir)}.`);
   for (let round = 1; round <= options.rounds && !interrupted; round++) {
      console.log(`Round ${round} of ${options.rounds}:`);
      const results = await Promise.all(options.suites.map((suite) => runSuite(suite, round, join(runDir, `round-${round}`, suite))));
      if (!interrupted) {
         runs.push(...results);
      }
   }

   await Promise.all(hogs.map((hog) => hog.terminate()));
   const { lines, failed } = summarizeStress(runs);
   const completed = interrupted ? `${runs.length / options.suites.length} of ${plural(options.rounds, 'round')}, interrupted` : plural(options.rounds, 'round');
   console.log(`\nStress summary: ${completed}, ${plural(options.hogs, 'busy loop')}, ${duration(Date.now() - started)}, results in ${relative(root, runDir)}.`);
   console.log(lines.join('\n'));
   if (interrupted) {
      return 130;
   }

   return failed ? 1 : 0;
}

process.exitCode = await main();
