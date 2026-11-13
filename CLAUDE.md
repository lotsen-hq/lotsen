# Lotsen

Local web UI that layers a Claude-written guided tour over an Azure DevOps PR.

## Issue tracking

The maintainer tracks work with nibs. `.nibs/` is a separate, private git repository, gitignored here, and only `.nibs.yml` is part of this one. Commit and push nib changes from within `.nibs/`, not from the project root. A git worktree finds no store, because its own `.nibs.yml` stops the upward search, so run `nibs` in a worktree with `NIBS_PATH` set to the main checkout's `.nibs/`.

## CI

`.github/workflows/ci.yml` has two jobs. The `linux` job runs every check, as a non-root user, which the permission tests need. The `windows` job runs the tests and the publish. Only a run started by hand (`gh workflow run ci --ref <branch>`) runs it in a private copy of the repository. A push or pull request that changes only `.decaf/`, `docs/`, `README.md`, `ui/README.md`, `CLAUDE.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` or `SECURITY.md` starts no run, so keep that list free of files that a test, the build or the publish reads.

A release is a manual run of `.github/workflows/release.yml` from `main`, on a commit whose CI passed: `gh workflow run release --ref main -f version=v0.1.0`. Commit the version without its `v` into `plugin/.mcp.json` first, such as `lotsen@0.1.0`, and for a release without a suffix move `## Unreleased` in `CHANGELOG.md` into a section `## v0.1.0 - <date>`, because the release refuses either one missing (`docs/versioning.md`). Its `npm` job waits for the maintainer's approval in the `npm` environment, and nothing is public before it. Every `uses:` pins a commit SHA with its release in a comment, which Dependabot (`.github/dependabot.yml`) keeps current. When the tip of `main` has no CI run because its push changed only docs, start one with `gh workflow run ci --ref main` first. The npm job publishes with no npm token, and only the job that creates the tag and the release can write to the repository, so neither runs anything from the packages that the build installs. For a release without a suffix, a last job pins the entry in the marketplace repository `lotsen-hq/plugins` to the new tag. It pushes with a deploy key of that repository, the secret `MARKETPLACE_DEPLOY_KEY` of the `release` environment, which only `main` can use.

## When Executing Plans

When working from a plan or work item:

- **Auto-fix without asking:** bug fixes, type errors, missing imports, broken references, missing null checks, obvious error handling gaps
- **Auto-add without asking:** necessary validation, missing error handling on external calls, required interface implementations
- **Ask before doing:** new dependencies/packages, schema or data model changes, architectural decisions not covered in the plan, changes to public APIs or contracts, anything that affects other projects or services
- **Never without explicit approval:** delete or restructure files not mentioned in the plan, change build/CI configuration, modify security boundaries
- **An unapproved package blocks the work; it does not justify a workaround.** When a work item would be better served by a package that is not yet approved, stop and ask before implementation starts, even in an unattended run. Do not start with a hand-rolled substitute and swap the package in later.

## Code conventions

- **The build is the gate.** `Directory.Build.props` makes every warning an error in every project, the tests included, and turns off the JSON reflection fallback everywhere, so a type missing from `LotsenJsonContext` fails in tests as it would under Native AOT. Package versions live in `Directory.Packages.props`. `src/Directory.Build.props` and `tests/Directory.Build.props` import the root one and hold the settings that their projects share.
- **Common tasks are `just` recipes** (`justfile`; `just --list`). Use `just test-filter Category=Cli`, `just check` (everything CI runs) and `just run <args>` instead of the raw commands. CI still runs the raw commands, so keep a recipe in step with `.github/workflows/ci.yml` when either changes. On Windows the recipes run under PowerShell 7 (`windows-shell`), so a recipe line must work in both `sh` and `pwsh`, or the recipe gets a `[unix]` and a `[windows]` version.
- **The CLI is `System.CommandLine` in `src/Lotsen.Core/Cli/Commands.cs`.** Parse errors go to stderr with exit 2, because the library's own `ParseErrorAction` writes to stdout. The server and the adapter register their own signal handling, so `ProcessTerminationTimeout` stays null. `--version` prints `VersionInfo.Build`, not the entry assembly's version. A new command or option gets a `Category=Cli` process test in `tests/Lotsen.Tests/CliTests.cs`.
- **No top-level statements.** An executable declares an explicit `Program` class with a `Main` method, in the project's namespace. The test projects have no `Program.cs`: xunit.v3 generates their entry points. `Lotsen.TestSupport` is a library of the fixtures that more than one test project uses.
- **No primary constructors on classes or structs**; records may use them. A class declares an explicit constructor that assigns `private readonly` `_camelCase` fields.
- **Let the design enforce a constraint, not a test that inspects the code.** Don't add tests that scan source files or reflect over types to catch a programmer's mistake. Find a design under which the mistake does not compile or cannot be expressed: a private constructor whose only factory does the real work, a nested type, a narrower interface. If no such design exists, stop and ask. A test that checks a product document, such as the tour skill, against the names the code defines is not this kind of test.
- **Platform specifics stay behind the platform abstraction.** The MVP supports only Azure DevOps, but GitHub and others will follow (design decision 1.3). Code that knows a hosting platform's URLs, API, terms or behavior lives in that platform's library (today `src/Lotsen.AzureDevOps/`) or in the composition root, the `lotsen` executable in `src/Lotsen/`. `src/Lotsen.Core/` holds pinning, operations, the git layer, the server and the UI, and references no platform library, so a platform-specific dependency there does not compile. Don't add one anywhere else. Each library's `public` types are the ones another project uses, and only the test projects, `Lotsen.TestSupport` and the test host `Lotsen.TestHost` (a test-side executable that nothing in `src/` references and that is never published) get `InternalsVisibleTo`, never another production project.
- **Indentation is 3 spaces**, 2 for JSON, YAML and Markdown (`.editorconfig`).
- **Lines aim for 200 characters** (`max_line_length`). Keep statements and argument lists within it, and don't wrap a line that fits. A long string, such as an attribute's description, may run past it.
- **Formatting is checked.** `just format` formats the C# with `dotnet format` and the UI with Prettier, which reads `.editorconfig` for indentation and line width. `just format-check`, part of `just check` and CI, fails on any difference. It runs `dotnet format` at `--severity info`, so every info-level style and analyzer rule gates it unless `.editorconfig` turns the rule off. The contract's generated files under `ui/` are in `ui/.prettierignore`, because their checks compare them with what the generators write.
- **Comments follow the decaf comment rules** (`/decaf-quality:write-comments`) with this project's additions in `.decaf/conventions/comments.md`: the scope, the rules for served text and the glossary of domain terms. When a comment needs a term the glossary lacks, add the term in the same change. `python3 .decaf/conventions/comment-lint.py <path>` flags wording to judge.

## Writing conventions

These rules govern every document that is not a user doc, nib bodies included. User docs are the `README.md` files, `CHANGELOG.md`, `CONTRIBUTING.md` and `docs/live-test.md`.

- Every claim is either checked against a source (source code, vendor docs, package registries, a run) or labeled inline **[Unverified]**, **[Inference]** or **[Speculation]**.
- Keep summaries and open-question sections in sync with the body when adding or changing a section.
- Plain, short sentences; tables for option comparisons.
