---
name: release
description: Cut a lotsen release end to end. Stamp the changelog and the plugin's npm version, push to main, wait for green CI, start the Release workflow, hand the npm approval to the maintainer, then check what the release published on GitHub, npm and the marketplace.
argument-hint: "[version, such as v0.2.0 or v0.2.0-rc.1]"
disable-model-invocation: true
---

You are cutting a lotsen release. `docs/versioning.md` describes the process and `.github/workflows/release.yml` carries it out. Follow the steps below in order.

The version to release: `$ARGUMENTS`. If it is empty, propose one in step 1.

## Hard rules

- **Release only a commit on `main` whose CI passed.** `release.yml` refuses any other, but check it yourself before you start the workflow.
- **Never approve the release yourself.** The `npm` job waits in the `npm` environment for the maintainer's approval. The token in use may belong to the maintainer, so `current_user_can_approve: true` says nothing about whether you may. The approval is the maintainer's, every time.
- **Nothing irreversible without asking.** Never delete a tag or a release, unpublish or deprecate an npm version, force-push, or re-run a failed release job without the maintainer's go-ahead. A published npm version can never be published again with other content.
- **No nib IDs in public text.** The nib store is private. `CHANGELOG.md`, the release notes and the commit messages on `main` hold no `lots-xxxx` reference.
- **Halt on any error.** When a `git`, `gh`, `npm` or workflow step fails, stop and report the cause. Don't retry blindly or work around a refusal.

## 1. Version

Use the argument if it is `vX.Y.Z` or `vX.Y.Z-<suffix>`, such as `v0.2.0-rc.1`. Otherwise read the latest tag (`git tag --sort=-v:refname | head -1`) and the `## Unreleased` section of `CHANGELOG.md`, propose the next version, and confirm it with the maintainer. A version with a `-` is a pre-release. Below 1.0, a breaking change raises the minor version (`docs/versioning.md`, "What counts as a breaking change").

Check that the tag is free: `git ls-remote --tags origin "refs/tags/<version>"` prints nothing.

## 2. Sync

```sh
git fetch origin && git -C .nibs pull
git status --short
git log --oneline origin/main..main
```

Be on `main` with a clean tree, and not behind `origin/main`. When local `main` holds commits that `origin/main` lacks, list them and confirm with the maintainer that they belong in this release. If the tree is dirty or `main` is behind, stop and ask: don't stash, rebase or merge on your own.

## 3. Documentation

Run `/decaf-quality:coherence-audit` over what a release publishes: `README.md`, `docs/versioning.md`, `docs/live-test.md` and the tour skill, `plugin/skills/tour/SKILL.md`. Fix what it finds, or record with the maintainer why a finding ships as it is.

## 4. Changelog (stable releases only)

A pre-release needs no changelog section, because its notes are the ones GitHub generates. Skip to step 5.

For a stable release, move the entries under `## Unreleased` in `CHANGELOG.md` into a new section `## <version> - <YYYY-MM-DD>` below it, and leave `## Unreleased` empty above. Read every entry as a user would:

- Each entry says what changed for the user of lotsen, not how the code changed.
- `### Added`, `### Changed` and `### Fixed` group the entries. A fix to a feature that is new in this release is no fix: fold it into the feature's entry.
- American English, and no nib IDs: `grep -nE 'lots-[a-z0-9]{4}' CHANGELOG.md` prints nothing.
- When the commits since the last tag (`git log --oneline <last tag>..main`) hold a user-facing change that no entry covers, add one.

Then print the notes the release would publish, and show them to the maintainer:

```sh
node npm/release-notes.mjs <version>
```

## 5. The plugin's npm version

`plugin/.mcp.json` must start the npm version of this release, the version without its `v`, such as `lotsen@0.2.0`. Each release candidate gets its own commit of it. Set it, then check:

```sh
node npm/check-release-version.mjs <version>
```

## 6. Commit, push and wait for CI

Commit what steps 3 to 5 changed in one commit, such as `Release lotsen 0.2.0`, then `git push origin main`. When nothing changed, push the commits from step 2, if any.

Find the CI run of the pushed commit. The run appears a few seconds after the push, so retry the lookup a few times:

```sh
SHA=$(git rev-parse HEAD)
gh run list --workflow ci.yml --commit "$SHA" --json databaseId,status,conclusion
```

When no run appears, the push changed only paths that `ci.yml` ignores. Start one with `gh workflow run ci --ref main`, and find it the same way.

A CI run takes from about 25 minutes to well over an hour. Wait for it with `gh run watch <id> --exit-status` in a background Bash call, so the harness tells you when it ends. Don't poll in a loop. Then read the result itself:

```sh
gh run view <id> --json status,conclusion,jobs --jq '{status, conclusion, jobs: [.jobs[] | {name, conclusion}]}'
```

Anything but `success` stops the release. Report the failed jobs with `gh run view <id> --log-failed | tail -60`.

## 7. Start the release and hand over the approval

```sh
gh workflow run release --ref main -f version=<version>
gh run list --workflow release.yml --limit 1 --json databaseId,status,url
```

The `check` and `build` jobs run first, for a few minutes. Wait in a background Bash loop until the run's status is `waiting` or the run ends:

```sh
until s=$(gh run view <id> --json status --jq .status) && { [ "$s" = waiting ] || [ "$s" = completed ]; }; do sleep 20; done; echo "$s"
```

A completed run at this point failed in `check` or `build`: report the failed step and stop. A waiting run waits for the `npm` environment. Confirm that:

```sh
gh api repos/lotsen-hq/lotsen/actions/runs/<id>/pending_deployments \
  --jq '.[] | {environment: .environment.name, reviewers: [.reviewers[]? | .reviewer.login]}'
```

Then give the maintainer the run's URL and what to check before approving:

- The `check` job's summary shows the release notes, for a stable release.
- The run's artifacts hold `lotsen-linux-x64` and `lotsen-win-x64.exe` to try.
- Everything public comes after the approval: the npm packages, the tag, the GitHub release and, for a stable release, the marketplace entry.

Rejecting the run ends the release with nothing published. Don't approve it yourself.

## 8. Wait for the run to end

Once the maintainer approves, wait for the run with `gh run watch <id> --exit-status` in a background Bash call. When it fails, report the failed job and its log, and ask before you re-run anything. `gh run rerun <id> --failed` is safe to suggest: the npm job skips each package that npm already holds at the version, and the marketplace job finds an entry that is already pinned.

## 9. Check what the release published

Run each check and report the result. Stop at the first wrong one and report it.

```sh
VERSION=<version without its v>
gh release view <version> -R lotsen-hq/lotsen --json isDraft,isPrerelease,body,assets \
  --jq '{isDraft, isPrerelease, assets: [.assets[].name] | sort, body}'
npm view lotsen@"$VERSION" version
npm view @lotsen-hq/lotsen-linux-x64@"$VERSION" version
npm view @lotsen-hq/lotsen-win32-x64@"$VERSION" version
npm view lotsen dist-tags --json
```

- The release is no draft, `isPrerelease` matches the version's suffix, and its assets are `checksums.txt`, `lotsen-linux-x64` and `lotsen-win-x64.exe`.
- A stable release's body equals the output of `node npm/release-notes.mjs <version>`.
- npm holds all three packages at the version. npm's `latest` names a stable release, and `next` names a pre-release.

Each executable carries a provenance attestation. Download both into a new directory in the scratchpad and verify each by its exit status. `gh attestation verify` prints nothing when it succeeds outside a terminal.

```sh
gh release download <version> -R lotsen-hq/lotsen -p 'lotsen-*' -D <dir>
gh attestation verify <dir>/lotsen-linux-x64 --repo lotsen-hq/lotsen && echo verified
gh attestation verify <dir>/lotsen-win-x64.exe --repo lotsen-hq/lotsen && echo verified
```

The marketplace entry names the release's tag for a stable release, and stays as it was for a pre-release:

```sh
gh api repos/lotsen-hq/plugins/contents/.claude-plugin/marketplace.json --jq .content | base64 -d \
  | jq -r '.plugins[] | select(.name == "lotsen") | .source.ref'
```

Last, check the published executable through npm. Its version starts with the version and a `+`:

```sh
npx -y lotsen@"$VERSION" --version
```

## 10. Report and tidy up

Report the release's URL, the version, and the result of each check in step 9.

When the notes need a correction after the release, fix the section in `CHANGELOG.md` first, then ask the maintainer before you change the published notes with `gh release edit <version> -R lotsen-hq/lotsen --notes-file <file>`.

For a stable release, find the milestone nib named after the version (`nibs list -t milestone`). When all its children are closed, offer to close it with a summary that names the release, and commit and push the nib store from within `.nibs/`.
