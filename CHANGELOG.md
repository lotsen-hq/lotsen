# Changelog

The notable changes in each release of lotsen, newest first. Versions follow [semantic versioning](https://semver.org/), as [`docs/versioning.md`](docs/versioning.md) describes.

## Unreleased

## v0.2.0 - 2026-11-13

### Added

- **Dockable panels.** Drag a panel to any side of another, or into a group of tabs. The layout is saved and comes back on the next load. The Panels menu applies a ready-made arrangement (Default, Columns, or Explanation above the diff), saves and applies your own named layouts, and resets the layout.
- **Pop a panel out into its own browser window.** Closing the window returns the panel to its place among its group's tabs. lotsen tells you when the browser blocks the window.
- **Preferences apply in every open tab.** The page's preferences, your named layouts among them, live in lotsen's data directory, so a change applies at once in each tab and survives a change of port.
- **Mark a stop as done** from the Plan panel or the Stop panel. Once Claude revises the stop, the plan shows it as done on an earlier version.
- **Parts fold a large tour's chapters.** Claude can group the chapters of a large tour into parts, and the Plan panel folds each part to its heading.
- **Mentions of work items and pull requests in a tour link to Azure DevOps.**
- **The tour's label in the header links to its pull request.**

### Changed

- **The Stop panel's controls sit in one toolbar under the title:** an Ask Claude menu, the done checkbox, the skip toggle and a menu with the placement and the version choice.
- **Panels no longer fold.** Each group's header maximizes or hides its panel, and the Panels menu in the top bar hides and shows each panel. Files and Plan are separate panels.
- **`lotsen open` says why the server refused it**, and names both builds when the server runs another build of lotsen.

## v0.1.0 - 2026-11-10

The first public release.
