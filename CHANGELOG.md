# Changelog

The notable changes in each release of lotsen, newest first. Versions follow [semantic versioning](https://semver.org/), as [`docs/versioning.md`](docs/versioning.md) describes.

## Unreleased

### Added

- **Give a note a type.** The ask box and a note's thread view offer a note type: decision, concern or open question. The summary page lists a typed note under Decisions, Concerns or Open questions, marked as your note and linked to its thread, whether the thread is open, resolved or outdated. Claude still sees no note until you send it. A sent note keeps its type, and the summary page then marks it as your finding. Once sent, Claude sees the type when it fetches the question and in the review summary, so it reads the note as the decision, concern or open question you filed. The view of a sent question offers its type too, and you can change it at any time without sending Claude anything new.
- **The summary page's follow-ups take your typed threads.** Draft work items takes the sent threads of type decision, the open questions follow-up those of type open question, and Draft PR comments those of type concern, beside the sent answers. A follow-up is available when its group holds only such a thread. For an open question thread, Claude adds to that thread instead of starting a new one. An outdated thread never goes, and neither does an open question that you resolved, so a follow-up that you send again after such a change, or after you delete one of its threads, is refused. Claude reads each as your own finding, and a synthesis lists it as yours. A note that you have not sent never goes to Claude.
- **lotsen 0.3.0 and earlier cannot read a tour that holds a typed note, a typed sent thread, or a follow-up that handed Claude a typed thread.** They leave the tour out of the tour list and refuse to prune clones while it exists. No data is lost, and this version reads the tour.

## v0.3.0 - 2026-11-15

### Added

- **Tours of GitHub pull requests.** Start a tour from any github.com URL of a pull request, such as its changed files. lotsen reads the pull request with your GitHub CLI sign-in, which `gh auth login` sets, also for a public repository. When the pull request's head changes, lotsen offers the new iteration, numbering the heads it sees from 1. lotsen shows a merged pull request as completed and a closed one as abandoned, and links mentions of issues and pull requests to GitHub. GitHub Enterprise Server and GHE.com are not supported.
- **Choose the size of the page's text.** Settings offers Small, Medium and Large, and the code in the diffs scales with the rest. Medium keeps the sizes of earlier versions.
- **Choose a color theme.** Settings offers Catppuccin, Visual Studio Code and Gruvbox beside Lotsen's own colors. Each has a light and a dark palette, so the Theme setting still picks light, dark or the system's choice.
- **Catppuccin and Gruvbox syntax colors** join the Syntax colors setting. It stays separate from the color theme, so you can combine any two.

### Changed

- **A tidier Settings panel.** The settings sit under Appearance, Tour, Threads and Storage. Color theme and Syntax colors are dropdowns that show each family's colors, Placement and Thread messages show a small picture of each choice, and a preview shows the chosen colors and font size on a few diff lines. Mixed threshold shows only while Placement is Mixed, and the panel's footer shows the lotsen version.
- **A diff line with thread markers is as tall as any other line**, so its code sits level with its line number.
- **A stop's checks are easier to read.** Each check sits in its own card under its question in bold, with a pill that says whether it needs an answer or how far its answer has come, and the Checks heading counts the answered checks. Claude's recommendation shows in Claude's colors, and folds once you answer the check. Fewer labels stand between the question and the suggestions, and the answer type menu is as large as the buttons beside it.
- **The top bar's controls share one height and one look for each kind.** The tour switcher's chevron has no border, the Claude session state is a pill, and the pull request label shows its link with an underline and an external-link mark. Thin dividers split the bar into the tour, the stop, and Claude and the workspace. The tour takes only the width it needs, so the stop line shows more of the stop's title, and the update check sits beside the pull request label. When the stop line runs short of room, the chapter gives way before the stop's title, and "Skipped" stays. "Send answers" shows the unsent count inside the button, and a refused send shows its reason in a strip under the bar until you dismiss it. One status chip shows the Claude session, turns amber while the page connects or reconnects, and lists the session, the connection and Claude's current work on a click, so the bar no longer shows "Live" or "No Claude session is writing this tour". While a plan waits for your approval, the bar shows a "Review the plan" button that opens the Plan panel, brings a popped-out Plan panel's window forward where the browser allows it, and moves the focus to "Approve plan". In a narrow window the bar gives way in a fixed order, down to 720px at every font size, so its controls stay in the window.
- **The keyboard focus is easier to see in the light theme.** A focused button's edge takes the primary color, so it stands out against every surface a button sits on, in every color theme.
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
