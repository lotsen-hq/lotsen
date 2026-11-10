# Versioning

Lotsen uses [semantic versioning](https://semver.org/). The version stays below 1.0 until the public release.

## Where the version comes from

A release is a git tag, `v<version>`, and npm packages of the version without its `v`. The release workflow takes the version as an input and builds with it. It publishes the npm packages once both builds pass, and creates the tag last, so a failed build leaves no tag.

The plugin starts lotsen through npx at an exact version, which `plugin/.mcp.json` names, such as `lotsen@0.1.0`. Before a release, commit the new version there and push it to `main`. Then start the release from `main`, once CI has passed on that commit:

```sh
gh workflow run release --ref main -f version=v0.1.0
```

A version with a suffix, such as `v0.1.0-rc.1`, publishes a pre-release, and npm's dist-tag `next` gets it in place of `latest`. Each release candidate gets its own commit of `plugin/.mcp.json`. The workflow refuses a malformed version, a run from another branch, a `plugin/.mcp.json` that names another version, a commit whose CI did not pass and a version that already has a tag. `node npm/check-release-version.mjs v0.1.0` runs the check of `plugin/.mcp.json` locally.

A release publishes three npm packages: `@lotsen-hq/lotsen-linux-x64` and `@lotsen-hq/lotsen-win32-x64`, which each hold one executable, then `lotsen`, whose starter runs the executable of the system it is installed on. A rerun after a failure skips the packages that npm already holds at the version. The GitHub release also holds both executables and their checksums.

| Build | `lotsen --version` starts with |
|---|---|
| Release `v0.1.0` | `0.1.0+<commit>` |
| Any other build | `0.0.0-dev+<commit>` |

After the version, `--version` prints a digest of the executable's assemblies. The adapter refuses a server whose build differs from its own, so two builds of one version still count as different builds.

The plugin has no version of its own, in its manifest or in its marketplace entry, so Claude Code uses the commit to detect an update. The marketplace entry is pinned to the release tag, and the plugin at that tag starts the npm version of the same release, so the skill matches the executable. The release workflow moves the entry in `lotsen-hq/plugins` to the new tag of each release without a suffix. A pre-release leaves the entry as it is, so the marketplace, like npm's `latest`, offers no release candidate.

## What counts as a breaking change

A change is breaking if it changes one of these in a way that existing users or stored data can notice:

- The HTTP API and the protocol between the adapter and the server
- The stored tour format
- The command line
- The agent tools

Before the MVP release, nothing is compatible by promise: stored tours, the state file and the HTTP API can change freely, with no migration.

From then on, a breaking change raises the minor version while the version is below 1.0, and the major version after that.
