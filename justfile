# lotsen task runner. Run `just` (or `just --list`) to see the recipes.

set windows-shell := ["pwsh.exe", "-NoLogo", "-NoProfile", "-Command"]

solution := "Lotsen.slnx"
executable := "src/Lotsen"

# A publish without a runtime identifier builds for the host's.
runtime := (if os() == "windows" { "win" } else { "linux" }) + "-" + (if arch() == "aarch64" { "arm64" } else { "x64" })
binary := if os() == "windows" { "lotsen.exe" } else { "lotsen" }
publish_dir := "src/Lotsen/bin/Release/net11.0/" + runtime + "/publish"

# The assembly that the test-host recipe builds.
test_host := justfile_directory() / "tests/Lotsen.TestHost/bin/Debug/net11.0/Lotsen.TestHost.dll"

# List the available recipes.
default:
   @just --list

# --- Build ---------------------------------------------------------------

# Restore NuGet packages for the solution.
restore:
   dotnet restore {{solution}}

# Build the whole solution. Warnings are errors.
build:
   dotnet build {{solution}}

# Remove build outputs.
clean:
   dotnet clean {{solution}}

# Run lotsen from source; arguments pass through, e.g. `just run serve`.
run *ARGS:
   dotnet run --project {{executable}} -- {{ARGS}}

# --- Tests ---------------------------------------------------------------

# Run the whole .NET suite. Each test project writes a trx log to its TestResults directory.
test:
   dotnet test {{solution}} --logger trx

# Run the tests matching a filter, e.g. `just test-filter Category=Cli`, with a trx log as `test` writes.
test-filter FILTER:
   dotnet test {{solution}} --filter "{{FILTER}}" --logger trx

# Run the test suites round after round while busy loops load every core, and summarize each failed test, e.g. `just stress --rounds 1 --suites dotnet`. `just stress --help` lists the options.
[working-directory: 'ui']
stress *ARGS:
   node scripts/stress.mjs {{ARGS}}

# --- UI ------------------------------------------------------------------

# Install the UI's packages from the lockfile.
[working-directory: 'ui']
ui-install:
   npm ci

# Download the Chromium build of the UI's Playwright version, for the layout suite. CI also installs its system libraries, which needs sudo.
[working-directory: 'ui']
ui-browser:
   npx playwright install chromium

# Build the UI into ui/dist, which a Release build and a publish embed.
[working-directory: 'ui']
ui-build:
   npm run build

# Run the UI's tests: the unit project, and the flow tests on real servers in the test host, which this builds with the .NET SDK unless LOTSEN_TEST_HOST names a built Lotsen.TestHost.dll.
# It takes no parameter, as `ui-e2e` takes none, so the two can run in one `just` call.
[working-directory: 'ui']
ui-test:
   npm test

# Run the UI's tests on the test host that LOTSEN_TEST_HOST names. `check` passes its one build.
[private]
[working-directory: 'ui']
ui-test-on $LOTSEN_TEST_HOST:
   npm test

# Build the test host, the test-side executable that runs the UI's flow tests and the demo on real servers. It is never published.
test-host:
   dotnet build tests/Lotsen.TestHost

# Run the UI's layout suite in Chromium against the demo, which starts the test host and stops it once the suite ends. It builds the host unless LOTSEN_TEST_HOST names a built one.
[working-directory: 'ui']
ui-e2e:
   npm run e2e

# Run the layout suite on the test host that LOTSEN_TEST_HOST names, as `ui-test-on` does for the UI's tests.
[private]
[working-directory: 'ui']
ui-e2e-on $LOTSEN_TEST_HOST:
   npm run e2e

# Type-check the UI.
[working-directory: 'ui']
ui-check:
   npm run check

# Lint the UI.
[working-directory: 'ui']
ui-lint:
   npm run lint

# Format the UI in place with Prettier.
[working-directory: 'ui']
ui-format:
   npm run format

# Fail if Prettier would change any UI file.
[working-directory: 'ui']
ui-format-check:
   npm run format:check

# Write the UI's contract from the C# types: the schema and the golden fixtures, then ui/src/lib/contract.generated.ts. `just check` fails while they differ from the code.
contract:
   dotnet test {{solution}} --filter "Category=Contract" -e LOTSEN_UPDATE_CONTRACT=1
   npm --prefix ui run contract

# Run the UI demo on real servers in the test host, with sample tours and hot reload. See ui/README.md.
[working-directory: 'ui']
ui-demo:
   npm run demo

# Build the demo and serve it under the server's content security policy, without hot reload.
[working-directory: 'ui']
ui-demo-csp:
   npm run demo:csp

# --- Formatting ----------------------------------------------------------

# Format the C# and the UI in place.
format: ui-format
   dotnet format {{solution}} --severity info

# Fail if anything is unformatted, changing nothing. --severity info gates every info-level style and analyzer rule that .editorconfig doesn't turn off.
format-check: ui-format-check
   dotnet format {{solution}} --verify-no-changes --severity info

# --- Publish -------------------------------------------------------------

# Build the UI, then publish the Native AOT executable; needs clang and zlib, or on Windows the Visual Studio C++ build tools. Warnings are errors, so any AOT or trim warning fails it.
publish: ui-build
   dotnet publish -c Release -p:PublishAot=true -p:TreatWarningsAsErrors=true

# Fail unless the publish holds the executable alone.
[unix]
publish-check:
   #!/bin/sh
   set -eu
   files=$(find "{{publish_dir}}" -mindepth 1)
   if [ "$files" != "{{publish_dir}}/lotsen" ] || [ ! -x "{{publish_dir}}/lotsen" ]; then
      echo "Expected only the lotsen executable in {{publish_dir}}." >&2
      exit 1
   fi

# Fail unless the publish holds the executable alone.
[windows]
publish-check:
   @if ((Get-ChildItem -Recurse -Force -Name "{{publish_dir}}") -join "," -ne "{{binary}}") { [Console]::Error.WriteLine("Expected only {{binary}} in {{publish_dir}}."); exit 1 }

# Pack the npm packages for this platform from the publish, install them into an empty directory, and run lotsen through the Node starter, as npx does.
npm-check:
   node npm/check.mjs "{{publish_dir}}/{{binary}}"

# --- Live test -----------------------------------------------------------

# Run a shortcut of scripts/live-test.sh, e.g. `just live pin <pr-url>`. See docs/live-test.md.
[unix]
live *ARGS:
   scripts/live-test.sh {{ARGS}}

# Tour a real Azure DevOps PR with a live Claude Code session and a server of this checkout, e.g. `just demo <pr-url> new`. See docs/live-test.md.
[unix]
[no-cd]
demo url familiarity='familiar' dir='':
   "{{justfile_directory()}}/scripts/live-test.sh" demo "{{url}}" "{{familiarity}}" "{{dir}}"

# --- Gate ----------------------------------------------------------------

# Everything CI runs: the static checks at once, then one after another the UI's tests, the layout suite, the suite, the publish and the npm packages. Both UI suites run on the one test host that this builds.
check: ui-install static-checks ui-browser test-host (ui-test-on test_host) ui-build (ui-e2e-on test_host) test publish publish-check npm-check

# Run the formatting check, the UI's type check and its lint at the same time.
[parallel]
static-checks: format-check ui-check ui-lint
