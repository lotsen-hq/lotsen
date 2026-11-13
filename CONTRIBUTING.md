# Contributing to Lotsen

Bug reports, ideas and pull requests are welcome. For anything larger than a small fix, open an issue first, so we can agree on the approach before you write the code.

Everyone who takes part follows the [code of conduct](CODE_OF_CONDUCT.md).

## Report a bug

Open a [bug report](https://github.com/lotsen-hq/lotsen/issues/new?template=bug_report.yml). The form asks for your versions, your system and the end of the server's log.

## Set up

You need the .NET SDK that `global.json` names, Node.js (see `engines` in `ui/package.json`), [just](https://just.systems/), and for the Native AOT publish either clang and the zlib development files on Linux or the Visual Studio C++ build tools on Windows.

```sh
just ui-install
just check      # everything CI runs: formatting, lint, the UI's tests, the layout suite, the .NET tests, the publish and the npm packages
just --list     # every recipe
```

[`ui/README.md`](ui/README.md) covers the web UI and its demo, which runs sample tours with no Claude Code session. [`docs/live-test.md`](docs/live-test.md) shows how to tour a real pull request with your own build, which needs access to an Azure DevOps or GitHub repository.

## Make a change

- **`just check` passes.** Every warning is an error, and the formatting is checked: `just format` formats the C# and the UI.
- **Behavior comes with tests.** A fix gets a test that fails without it. A new command or option of the CLI gets a test in `tests/Lotsen.Tests/CliTests.cs`.
- **Hosting services stay behind their platform.** Code that knows Azure DevOps lives in `src/Lotsen.AzureDevOps/`, code that knows GitHub in `src/Lotsen.GitHub/`, and `src/Lotsen.Core/` references no platform library.
- **Comments explain why.** A comment states the reason for the code as it is now, not how it got there. That history belongs in the commit message.
- **One change per pull request.** Its description says what changed and why, and links the issue it settles.

CI runs these checks for every pull request on Linux, and the tests and the publish on Windows too.

## License

Lotsen is licensed under the [Apache License, Version 2.0](LICENSE). By contributing, you agree that your contribution is licensed under it, as its section 5 says.
