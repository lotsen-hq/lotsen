// Starts the test host for a test run or the demo, in Node: tests/Lotsen.TestHost, built here unless LOTSEN_TEST_HOST names a built Lotsen.TestHost.dll.
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { delimiter, dirname, join, resolve } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const project = resolve(repository, 'tests/Lotsen.TestHost/Lotsen.TestHost.csproj');
const built = resolve(repository, 'tests/Lotsen.TestHost/bin/Debug/net11.0/Lotsen.TestHost.dll');

/** Returns the host's assembly: the one that LOTSEN_TEST_HOST names, or the Debug build of the project, which this builds first. */
export function hostAssembly() {
   const named = process.env.LOTSEN_TEST_HOST;
   if (named !== undefined && named !== '') {
      if (!existsSync(named)) {
         throw new Error(`LOTSEN_TEST_HOST names ${named}, which does not exist.`);
      }

      return named;
   }

   const build = spawnSync('dotnet', ['build', project, '--nologo', '-v', 'quiet'], { stdio: 'inherit' });
   if (build.status !== 0) {
      throw new Error(`dotnet build ${project} failed with ${build.status}.`);
   }

   return built;
}

/**
 * Returns the directory of git's own executable on Windows, or null elsewhere or when git has none there. Git for Windows' git.exe on PATH is a launcher that starts that executable as a second
 * process, which doubles the cost of each of the many git calls of a test run.
 */
function gitExecPath() {
   if (process.platform !== 'win32') {
      return null;
   }

   const found = spawnSync('git', ['--exec-path'], { encoding: 'utf8' });
   const directory = found.status === 0 ? resolve(found.stdout.trim()) : null;
   return directory !== null && existsSync(join(directory, 'git.exe')) ? directory : null;
}

/** Returns the host's environment: this process's, with git's own executable first on PATH and the temporary directory moved to the one that LOTSEN_TEST_TEMP names, if it names one. */
function hostEnvironment() {
   const environment = { ...process.env };
   const git = gitExecPath();
   if (git !== null) {
      // Windows matches the variable's name in any case, and the name can be Path as well as PATH.
      const path = Object.keys(environment).find((name) => name.toUpperCase() === 'PATH') ?? 'PATH';
      environment[path] = `${git}${delimiter}${environment[path] ?? ''}`;
   }

   const temp = process.env.LOTSEN_TEST_TEMP;
   if (temp !== undefined && temp !== '') {
      mkdirSync(temp, { recursive: true });
      // .NET reads TMPDIR on Unix and TMP or TEMP on Windows.
      Object.assign(environment, { TMPDIR: temp, TMP: temp, TEMP: temp });
   }

   return environment;
}

/** Starts the host and returns its address once it printed it. */
export async function startHost(assembly = hostAssembly()) {
   const child = spawn('dotnet', [assembly], { stdio: ['pipe', 'pipe', 'inherit'], env: hostEnvironment() });
   const exited = new Promise((done) => child.once('exit', () => done()));
   const first = await new Promise((done, fail) => {
      const lines = createInterface({ input: child.stdout });
      lines.once('line', (line) => {
         lines.close();
         done(line);
      });
      child.once('exit', (code) => fail(new Error(`The test host exited with ${code} before it printed its address.`)));
   });
   return {
      address: JSON.parse(first),
      async stop() {
         child.stdin.end();
         // The host stops its worlds and removes its directory once its input closes. Only a host that hangs is killed, and only this one.
         const timer = setTimeout(() => child.kill(), 30_000);
         await exited;
         clearTimeout(timer);
      },
   };
}
