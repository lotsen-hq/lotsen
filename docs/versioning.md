# Versioning

Lotsen uses [semantic versioning](https://semver.org/). The version stays below 1.0 until the public release.

## Where the version comes from

A release is a git tag, `v<version>`, and npm packages of the version without its `v`. The release workflow takes the version as an input and builds with it. It publishes the npm packages once both builds pass and the maintainer approves the release, and creates the tag last, so a failed build or a rejected release leaves no tag.

The plugin starts lotsen through npx at an exact version, which `plugin/.mcp.json` names, such as `lotsen@0.1.0`. Before a release, commit the new version there. For a release without a suffix, also move the entries under `## Unreleased` in [`CHANGELOG.md`](../CHANGELOG.md) into a section `## v0.1.0 - <date>`, which becomes the GitHub release's notes. Push both to `main`. Then start the release from `main`, once CI has passed on that commit:

```sh
gh workflow run release --ref main -f version=v0.1.0
```

A version with a suffix, such as `v0.1.0-rc.1`, publishes a pre-release, and npm's dist-tag `next` gets it in place of `latest`. A pre-release needs no changelog section, and its notes are the ones GitHub generates from the commits. Each release candidate gets its own commit of `plugin/.mcp.json`. The workflow refuses a malformed version, a run from another branch, a `plugin/.mcp.json` that names another version, a release without a suffix whose changelog section is missing or empty, a commit whose CI did not pass and a version that already has a tag. `node npm/check-release-version.mjs v0.1.0` runs the check of `plugin/.mcp.json` locally, and `node npm/release-notes.mjs v0.1.0` prints the notes that the release would publish.

## Approving a release

The `npm` job runs in the GitHub environment `npm`, whose required reviewer is the maintainer, so the run waits there until the maintainer approves it in the run's page. Nothing is public before then: the npm packages, the tag, the GitHub release and the marketplace entry all come after the approval. The `check` job's summary shows the release notes, and the run's artifacts hold both executables to try first. Rejecting the run ends the release, and starting it again needs another approval.

On npmjs.com, the trusted publisher of each of the three packages names the environment `npm`, so that npm accepts a publish only from a job in it. Keep the names in step when either changes.

## Checking a download

Every executable in a GitHub release carries a build provenance attestation: a signed statement that the release workflow of `lotsen-hq/lotsen` built those bytes at the release's commit ([GitHub docs](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)). Check one with the GitHub CLI, which exits with a status other than 0 when the file does not match:

```sh
gh attestation verify lotsen-linux-x64 --repo lotsen-hq/lotsen
```

The npm packages carry npm's own provenance, which trusted publishing adds ([npm docs](https://docs.npmjs.com/trusted-publishers#automatic-provenance-generation)), and `npm audit signatures` checks it ([npm docs](https://docs.npmjs.com/generating-provenance-statements#verifying-provenance-attestations)).

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
