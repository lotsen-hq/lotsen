# Changelog

The notable changes in each release of lotsen, newest first. Versions follow [semantic versioning](https://semver.org/), as [`docs/versioning.md`](docs/versioning.md) describes.

## Unreleased

### Added

- **Choose the size of the page's text.** Settings offers Small, Medium and Large, and the code in the diffs scales with the rest. Medium keeps the sizes of earlier versions.
- **Choose a color theme.** Settings offers Catppuccin, Visual Studio Code and Gruvbox beside Lotsen's own colors. Each has a light and a dark palette, so the Theme setting still picks light, dark or the system's choice.
- **Catppuccin and Gruvbox syntax colors** join the Syntax colors setting. It stays separate from the color theme, so you can combine any two.

### Changed

- **A diff line with thread markers is as tall as any other line**, so its code sits level with its line number.
- **Gray text in the light theme is darker**, so small labels such as a thread's "Resolved" read clearly on their gray fill.
- **Stops and diffs load faster, most of all on Windows, where each git process is slow.** lotsen keeps what git told it about a tour's commits in its git fact cache, so a stop that Claude writes again, a diff you open again and the list of changed files after pinning start no git process. The first stop that Claude writes on a pin starts fewer of them too.

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
