# Live test: a tour from Claude Code

How to check by hand that a live Claude Code session creates a tour, its plan and its stops, and that they appear in the
browser without a reload. Sections 7 to 11 then follow that tour through answers, revisions, a new update and the
end-of-tour summary. The session uses the tour skill in [`plugin/`](../plugin) and runs `lotsen mcp` as a development
channel.

The steps below write `<lotsen>` for the absolute path of this repository's checkout.

## Requirements

- git 2.46 or newer. lotsen passes its Azure DevOps credential to git through a credential helper, and git 2.46 added
  the helper answer that it uses. An older git makes lotsen refuse to run git.
- The .NET 11 SDK, the version that `global.json` names or a later feature band of it.
- clang and the zlib development files (`clang` and `zlib1g-dev` on Debian and Ubuntu), which the Native AOT publish
  links with.
- Node.js 24.15 or newer in the 24 line, or 26 or newer, as `ui/package.json` requires.
- The Azure CLI, signed in with `az login` to an account that can read the pull request's repository.
- Claude Code, with the lotsen plugin from [`plugin/`](../plugin).

## Shortcuts

`scripts/live-test.sh` wraps the commands below:

| Command                             | Does                                                                                                                                                               |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `check`                             | Reports whether the publish, the server, the MCP registration and the Azure CLI sign-in are ready.                                                                 |
| `build`                             | Step 1.                                                                                                                                                            |
| `serve`                             | Step 2, when you want the server in the foreground.                                                                                                                |
| `register`                          | Step 3, replacing an older entry.                                                                                                                                  |
| `plugin`                            | Prints the path of a new copy of `plugin/` without its MCP entry, for `--plugin-dir`. See step 3.                                                                  |
| `claude [dir]`                      | Step 4: an interactive session in `dir` with the development channel.                                                                                              |
| `pin <pr-url> [familiarity]`        | Creates a tour in a headless `claude -p` session and prints its pin section. It needs the registration from step 3, and starts a background server when none runs. |
| `demo <pr-url> [familiarity] [dir]` | Steps 1 to 5 in one command: tours the PR in an interactive session with the development channel. See below.                                                       |
| `demo-link`                         | Prints a new link to the UI of the demo's server, which works once. `just demo-print-link` runs it.                                                                |

`just demo <pr-url> [familiarity] [dir]` runs `demo` from any directory. It publishes lotsen first when the publish is missing or older than its sources. It refuses to start while the Azure
CLI is not signed in. The session loads this checkout's publish through `--mcp-config`, which replaces the user-scope `lotsen` from step 3 for that session, so `demo` needs no registration.
Its server keeps its tours, clones and the UI's preferences in a data directory of its own, `~/.local/share/lotsen-demo` (under `$XDG_DATA_HOME` when that is set), and never touches your other tours. Delete that
directory to start the next demo with no tours. Claude runs in `dir`, by default a new empty directory, and starts with `/lotsen:tour <pr-url> <familiarity>`, `familiar` when you give none.
Pass a checkout of the PR's repository as `dir` to let Claude read the code with git. For a new link to the UI, run `just demo-print-link`, which prints one that works once, or
`LOTSEN_DATA_DIR=~/.local/share/lotsen-demo lotsen open` to open it in the browser.

`pin` checks the MCP tools and the pinning without a person at the keyboard: compare `head` and `mergeBase` with the pull request, and run it again after `az logout` and a server restart for the failure path. It does not check the channel, because the channels documentation says that Claude Code ignores
`--dangerously-load-development-channels` with `-p` ([Unverified] on 2.1.310), and it does not check the browser. Use `claude` and step 5 for those.

## 1. Build

```sh
cd <lotsen>/ui && npm ci && npm run build
cd <lotsen> && dotnet publish src/Lotsen -c Release
```

The executable is `<lotsen>/src/Lotsen/bin/Release/net11.0/linux-x64/publish/lotsen`, written `lotsen` below. The
publish embeds `ui/dist`, so build the UI first. The adapter refuses a server of another build while another session or
a `lotsen serve` holds it, so after every publish run `lotsen restart`, which stops the server that runs and starts a background server of the new build, then
restart Claude Code or reconnect `lotsen` in `/mcp`. `lotsen stop` stops a server, foreground or background, and waits
until it has exited. A `lotsen serve` also stops on Ctrl-C. A background server stops 60 seconds after the last Claude
Code session with lotsen ends.

## 2. Start the server, or let Claude Code start it

Claude Code's `lotsen mcp` starts a background server when none runs, as soon as Claude Code starts it, and the
server exits 60 seconds after the last session that used it ended. A Ctrl-C in the terminal does not stop it. It
writes Warning lines and above to `server.log` in the data directory, by default `~/.local/share/lotsen/server.log`.
A start that fails or does not answer within 10 seconds fails the tool call, with a reason that names that log.

To see the clone, download and pin timings, run the server in the foreground instead, before you start Claude Code:

```sh
lotsen serve --log-level Information
```

Leave it running in its own terminal. It prints its port and stops on Ctrl-C or `lotsen stop`. It never stops for lack
of sessions.

The port is one that the OS assigns, unless `--port <n>` or the `LOTSEN_PORT` environment variable sets it. The option wins over the variable. One server runs per data directory, so a second `lotsen serve` exits 1. Once the first server has started, the message names its PID and `lotsen stop`. The UI's settings, the Files panel's choices and the workspace layout live in `preferences.json` in the data directory, so they survive a start on another port. A change in one browser tab shows in every other tab of the server at once, except the layout and the split of a side-by-side diff, which a tab reads only when it loads. A tab that cannot read the preferences as it loads shows that the server could not be reached, and a reload tries again.

The server keeps what it read from git in memory, in a git fact cache of 64 MiB by its own estimate of the sizes. `--fact-cache-mb <n>` or the `LOTSEN_FACT_CACHE_MB` environment variable sets another size, a whole number of MiB from 1 to 2147483647. The option wins over the variable, and an empty variable counts as unset. Any other value makes `lotsen serve` exit 2 for the option, and a non-empty variable that is not such a size makes it exit 1. With `--log-level Information`, `lotsen serve` prints the size at its start: "The git fact cache keeps up to 64 MiB."

`lotsen restart` stops the running server, if any, starts a background server and prints its address and PID. The
background server listens on the port that `LOTSEN_PORT` sets, or else on one that the OS assigns, and stops 60
seconds after the last session ends, as one that Claude Code starts does. `lotsen stop` with no server running says
so and exits 0. Claude Code's sessions do not start a new server after `lotsen stop` until Claude calls a lotsen tool.

A background server that `lotsen restart` or Claude Code starts takes its fact cache size from `LOTSEN_FACT_CACHE_MB`
in the environment of the command or of Claude Code.

Every server start issues new credentials, so a browser tab of the earlier server shows that it has no session once
it reaches the new one, even on the same port. Run `lotsen open` for a new tab.

Under WSL, `open_ui` opens the Windows browser through `wslview` or `cmd.exe`, with a `localhost` link that reaches the server through WSL's localhost
forwarding.

## 3. Register the MCP server, once

`server:lotsen` names an MCP server called `lotsen`. Register it in user scope, because the session runs in the PR's
repository, not in this one:

```sh
claude mcp add --scope user lotsen -- <lotsen>/src/Lotsen/bin/Release/net11.0/linux-x64/publish/lotsen mcp
```

`claude mcp remove --scope user lotsen` removes it again.

`plugin/` has its own MCP entry, which runs a released lotsen through npx, and an installed plugin uses that entry. A development session must talk only to this checkout's publish, so it loads a copy of
`plugin/` without the entry, which `<lotsen>/scripts/live-test.sh plugin` makes. The channel stays `server:lotsen`, because a development channel cannot name a plugin loaded with `--plugin-dir`:
on 2.1.319, `plugin:lotsen@inline` reports "plugin not installed".

The channels documentation shows `server:<name>` only with a server from a project `.mcp.json`. It does not say which
scopes count. In Claude Code 2.1.308 the startup check for `server:` entries collects server names from the
enterprise, managed, user, project and local scopes. A live session on 2.1.310 confirmed that a user-scope entry
matches.

If the startup screen in step 4 does not show the channel, use a project `.mcp.json` in the PR's checkout instead.
That is the form the earlier channel tests confirmed. Claude Code asks once to approve it. Add `.mcp.json` to
`.git/info/exclude` to keep it out of that repository's commits:

```json
{
  "mcpServers": {
    "lotsen": { "command": "<lotsen>/src/Lotsen/bin/Release/net11.0/linux-x64/publish/lotsen", "args": ["mcp"] }
  }
}
```

## 4. Start Claude Code in the PR's repository

```sh
cd <checkout of the PR's repository>
claude --version
claude --plugin-dir "$(<lotsen>/scripts/live-test.sh plugin)" --dangerously-load-development-channels server:lotsen
```

`--dangerously-load-development-channels` takes every word after it as an entry, so put other flags before it.

On 2.1.310 with an account that has no organization, `--channels server:lotsen` starts but says that `server:` entries need `--dangerously-load-development-channels`, so the long flag stays.

Check before you go on:

- The startup screen says that messages from `server:lotsen` inject directly in this session.
- `/mcp` lists `lotsen` as connected, with the tools `find_tours`, `create_tour`, `attach_tour`, `set_plan`,
  `put_stop`, `read_state`, `fetch_question`, `reply`, `ack`, `pin_tour`, `open_ui`, `start_thread`,
  `add_thread_message`, `outdate_thread` and `delete_thread`.
- `/lotsen:tour` is in the `/` menu.
- Without a `lotsen serve`, `lotsen open` in another terminal opens the UI, so Claude Code started a background
  server. `<lotsen>/scripts/live-test.sh check` reports the server as running.
- Once you end the session with no `lotsen serve` running, `lotsen open` still opens the UI for about 60 seconds, and
  then says that no server is running. A tour that you open in the browser in that time does not keep the server.

## 5. Tour a PR

Use a real Azure DevOps PR in this repository. Claude reads it with your Azure DevOps MCP tools, the Azure CLI or git.

**With a familiarity level.** Ask `/lotsen:tour <pr-url> familiar`, or in words: "tour this PR <pr-url>, familiar".

- Claude calls `create_tour`, then `open_ui` with the new tour's ID. The browser opens with the tour selected, and its
  Plan panel shows the plan once Claude sets it. The tour list shows the PR as "Generating", with
  "Familiar with the code · no plan yet".
- Claude reads the PR and calls `set_plan`. Without a reload, the tour's stop count changes and the Plan panel shows
  the chapters and stops: an overview first, then kinds such as context, inspect, risk and question.
- Claude goes on reading and planning while the tour pins in the background, and does not wait for the pin. The
  first pin of a repository clones it into `~/.local/share/lotsen/clones`, which can take minutes.
- Ask Claude for the tour's `read_state`. Its `pin.state` reaches `pinned`. `pin.head` is the PR's latest commit, as
  the PR's Commits tab shows it, and `pin.iteration` is the number of its latest update. In a checkout with both
  branches fetched, `git merge-base <pin.head> origin/<target branch>` prints `pin.mergeBase`.
- Once the pin is `pinned`, Claude calls `put_stop` for each stop in tour order. Without a reload, each stop turns
  ready in the Plan panel. A file that the plan marks mechanical or generated opens collapsed, with its mark and
  reason.
- Open every stop. It shows its headline and prose, and its passages in the diff.
- Click every `ref:` link in the prose. Each one opens its file at the side it names, `base` or `head`, on the lines it
  names. Check the lines against the code that the sentence describes.
- Note each `put_stop` refusal that Claude reports in the terminal, and whether Claude fixed the input and wrote the
  stop on its next call.

**Tour states.** Keep the tour list open in the tour switcher while Claude writes the stops.

- The tour shows "Generating" until the last stop is written, then "Ready", without a reload.
- Open a stop. The tour turns "In progress". Open the summary page after the last stop. The tour turns "Finished",
  and stays so when you open a stop again.
- Open another stop, choose another tour in the list, then choose this tour again. It opens at the stop you opened
  last. Do the same from the summary page. The tour opens at the summary page. Both work in a new browser tab too.
- Click the archive button beside the tour. The tour leaves the list, and "Show hidden tours (1)" appears. Tick it.
  The tour shows "Archived". Click its unarchive button. The tour shows "Finished" again.
- With a throwaway PR that you may abandon: abandon it in Azure DevOps, open its tour, and click "Check the PR for a
  new update" in the top bar. The tour leaves the list, and "Show hidden tours (1)" appears. Tick it. The tour shows
  "PR abandoned" beside its state, and stays unarchived. Reactivate the PR in Azure DevOps and check again. The flag
  goes, and the tour is listed again.

**When the pin fails.** Run `az logout`, then run `lotsen restart`, or stop `lotsen serve` with Ctrl-C and start it
again. The server keeps
its token in memory until the token nears its expiry, so only a new server asks the Azure CLI again. Then ask Claude
to tour the PR again.

- `read_state` shows `pin.state` `failed`, and Claude tells you the reason, which says to run `az login`.
- Run `az login` and tell Claude. Claude calls `pin_tour`, and the pin reaches `pinned`.

**Without a familiarity level.** Ask "walk me through PR <pr-url>".

- Claude asks one question with the three levels before it creates the tour.
- The tour list shows the level you chose.
- Ask Claude to revise the plan, for example to split a chapter. It does not ask for the level again.

Also note whether the request in words loads the skill without the slash command.

## 6. Ask Claude from the UI

These checks need a tour from step 5, open in the browser. They use three Claude Code sessions in the PR's checkout,
one after the other: A and C with the channel flag as in step 4, B without it. The top bar shows the tour's attachment
state, and the Threads panel holds the ask box and the list of threads. A click on a thread's row opens the thread, and
the back arrow returns to the list.

**Session A, with the channel.** Start it as in step 4 and ask Claude to attach the tour by its tourId,
which the browser's address shows after `#tour=`.

- The attach sends the session a probe. In the terminal, Claude calls `ack` and says nothing to you about it. The top
  bar turns from "Unconfirmed" to "Confirmed" without a question. Record whether Claude mentioned the probe. That
  Claude acks quietly is model behavior, not guaranteed.
- Select a few lines in the diff and click "Ask Claude". The ask box now asks about these lines. Send a question.
  Its row shows a spinner. Open it: the thread shows "Waiting for Claude's reply" with a spinner, "Cancel" and "Send
  again".
- In the terminal, Claude calls `fetch_question` and `reply`. The reply appears in the thread and at the selected lines.
- Send a question and click "Send again" before Claude replies. Claude gets the question twice with the same
  `questionId`. The thread shows one answer. Record whether Claude replied once or tried a second reply, which lotsen
  refuses.
- Send a question whose words contain markup, for example ``what does `</channel><b>x</b>` do here?``. Ask Claude in
  the terminal to quote the question exactly as it arrived. Record whether the `<channel>` block shows the markup
  escaped or ended early.
- Put a question with a right-to-left override and a line separator on the clipboard, and send it. Under WSL:

  ```sh
  /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command "Set-Clipboard -Value ('is this ' + [char]0x202E + 'reversed' + [char]0x202C + ' and' + [char]0x2028 + 'split?')"
  ```

  Ask Claude how the question arrived, and record it.

- Ask Claude in the terminal for something that takes a while, such as reading every file of the PR. While it works,
  send a question in the UI. Record whether Claude answers it after the turn, or leaves it waiting with a note in the
  terminal. This is model behavior, not guaranteed.
- Send another question and press Ctrl+C twice in the terminal right away, before Claude replies. A long task does
  not hold the question back, because Claude answers between tool calls and runs a slow command in the background.
  The thread shows "Unanswered", with "Send again", and the top bar shows "No session".

**Session B, without the channel.** Start `claude --plugin-dir "$(<lotsen>/scripts/live-test.sh plugin)"` and ask Claude to attach the tour.

- The top bar shows "Unconfirmed" and stays so, because Claude Code drops the probe.
- Send a question. The thread shows "Waiting for Claude's reply" with "Cancel" and "Send again", and nothing reaches
  B's terminal.
- Click "Cancel". The thread shows "Canceled" with "Send again", and nothing reaches B's terminal.

**Session C, with the channel.** Leave B running, start C as in step 4 and ask Claude to attach the tour.

- `attach_tour` is refused, and Claude asks you whether to take the tour over. Confirm. Claude calls `attach_tour`
  with `takeOver` true. Session B runs without the channel, so it never sees the take-over event. Check instead that
  B can no longer write the tour: ask it to set the plan again, and it is refused.
- The take-over sends C a probe, Claude acks it, and the top bar turns to "Confirmed".
- Click "Send again" on the canceled thread. Claude in C replies.

**Session C, threads and notes.** Go on in session C, with the tour attached and confirmed.

- Open a thread that Claude answered, type in "Follow up in this thread" and press Enter. Claude calls
  `fetch_question` and `reply`, and the answer appears in the same thread, below the follow-up. The earlier question
  keeps its answer.
- Select a few words in a stop's prose and click "Ask Claude". Send a question. Ask Claude in the terminal for
  the anchor that `fetch_question` returned. Its kind is `stopText`, with the stop and the words you selected.
- Write two questions at two places and save each with "Save as note". Each note shows "Draft" once you open it. Ask
  Claude for the tour's `read_state`: it has neither note.
- Open each note and tick "Send with other notes". Back in the list, click "Send chosen notes" in the bar above the ask
  box, which says "2 notes chosen.". Each thread shows "Sent", then "Received" once Claude calls `fetch_question` for
  it. Claude answers each in its own thread.
- Ask Claude in the terminal to start a thread at some lines of a changed file, with a short remark. The thread
  appears without a reload, at those lines, with "Claude" at the top of the message, and its row says "Your turn". Open
  it and follow up with a question. Claude answers in that thread.
- Ask Claude to add a message to one of its threads. The message appears without a reload.
- Ask Claude to mark one of its threads outdated. The thread leaves the list without a reload, and comes back marked
  "Outdated" when you add "Outdated" to the panel's state filter.
- Open another thread and click "Resolve", and ask Claude to mark it outdated. The call succeeds and changes nothing:
  the thread stays resolved. Ask Claude to add a message to it. The thread still stays resolved.
- Ask Claude to delete one of its threads. The thread and its marker go away without a reload.

## 7. One tour's life: the server's start

Sections 7 to 11 follow one tour of one real PR, in order, because each leaves the state that the next one needs.
Use a PR whose source branch you can push to, because section 10 pushes a commit to it. Pick a PR that changes a few
files, so a tour has several stops. Start from no tours: delete `~/.local/share/lotsen-demo` first.

These sections run on `just demo`, whose server keeps its tours in `~/.local/share/lotsen-demo`. Run every `lotsen`
command in them with that data directory, for example `LOTSEN_DATA_DIR=~/.local/share/lotsen-demo lotsen open`.
Keep the server's log open in a second terminal:
`tail -F ~/.local/share/lotsen-demo/server.log`.

- With no demo running, `lotsen open` says that no server is running.
- Run `claude --version` and `lotsen --version`, and keep both for the record.
- Run `just demo <pr-url> familiar <checkout of the PR's repository>`. Claude Code starts, and the tour skill runs.
  Claude creates the tour and opens the UI, so Claude Code started the server on demand. `lotsen open` now opens the
  UI.
- Let Claude write the plan and every stop, as in step 5. This session is the tour's session.

**A spare session offers the saved tour.** In another terminal, run the same `just demo <pr-url> familiar <checkout>`.

- Claude calls `find_tours` and asks whether to reopen the saved tour or create a new one. The tour shows its state,
  "familiar", today's date and that another session holds it. Claude does not call `create_tour` before you answer.
- Choose to reopen it. Claude calls `attach_tour`, is told that another session holds the tour, and asks whether to
  take it over. Decline. Claude leaves the tour alone, and the tour list still holds one tour of the PR.
- Keep this spare session open until section 11. It holds no tour, but it keeps the server running whenever a check
  ends the tour's session, so the server never reaches its 60-second exit before section 11.

Several checks below end the tour's session with `/exit` and later **reattach** the tour: run
`just demo <pr-url> familiar <checkout>` in a free terminal and choose to reopen the saved tour. Claude attaches it
with no take-over, because no session holds it, and this session is the tour's session from then on.

## 8. Checks and answers

The tour from section 7 is open in the browser, attached to the tour's session.

**Checks on a stop.** Ask Claude in the tour's session to add two checks to one stop, each with a recommendation and
suggested answers, and one check to another stop.

- Claude rewrites each stop with `put_stop`. The stop shows a "Checks" section with each question, its
  recommendation and its suggested answers.
- Ask Claude for the tour's `read_state`. Each check has an ID.
- Ask Claude to rewrite one of these stops with a changed recommendation, keeping its checks. `read_state` shows the
  same check IDs as before.

**Answers with no session.** End the tour's session with `/exit`. The top bar shows "No session".

- Answer a check on the first stop by picking a suggested answer, and a check on the second stop by writing your own.
  Change one answer's type, from "Decision" to "Concern". Each answer shows "Draft".
- The top bar shows "2 unsent answers" and "Send answers". The button is disabled, and beside it a reason says that
  the tour is attached to no Claude Code session.

**Send with a session.** Reattach the tour, as section 7 says.

- Right after the attach, while the top bar still shows "Unconfirmed", click "Send answers". The `<channel>` block in
  the terminal carries a `nonce`, and Claude calls `ack` with it before it fetches the answers. When the top bar turns
  "Confirmed" before you can click, record that this check was not reached.
- Otherwise, once the top bar shows "Confirmed", click "Send answers". The terminal shows one `<channel>` block with
  `event="answers"`, a `tourId` and a `batchId`, and no answer text. Claude calls `fetch_answers` before it acts.
- Without a reload, each answer turns from "Sent" to "Received", and the unsent count goes away.
- Claude reads each answer's type. For the concern, it starts a thread with `start_thread`, which appears in the
  Threads panel. It does not revise any stop: record any `put_stop` call it makes for the answers.

**A changed answer.** Change a received answer.

- It shows "Draft" again, the top bar counts it, and "Send answers" sends it in a new batch with a new `batchId`.

**An answer lost on the way.** Answer another check, so it shows "Draft".

- Click "Send answers" and press Ctrl+C twice in the tour's session right away, before Claude calls
  `fetch_answers`. The answer shows "Not received", and the top bar counts it again.
- Reattach the tour and click "Send answers". The new session receives the answer, and it shows "Received".

## 9. Improving the tour

Keep the tour attached and open in the browser.

**Explain and revise a stop.** Open a stop. The toolbar under its title has an "Ask Claude" menu with "Explain
deeper…" and "Revise…".

- Choose "Explain deeper…", leave the box empty and click "Send to Claude". The Stop panel says "Claude is explaining
  this stop deeper." In the terminal the request arrives as a tour action. Claude rewrites the stop with `put_stop`
  and resolves the request with a short reply. The deeper explanation is in the stop, not only in the reply.
- Select a few words in the stop's text and ask to explain them from the selection. The request names the selected
  words, and Claude does the same.
- Choose "Revise…", write what should change and send it. Claude rewrites the stop the same way.

**The version banner.** A revision that you asked for from the UI replaces the stop at once. The banner is for one
that you did not ask for there.

- Stay on a stop and ask Claude in the terminal to revise it. When the revision arrives, a banner says "Claude revised
  this stop while you read it", and the stop does not change. Click "Show the new version". The stop shows the
  revision.
- Ask Claude in the terminal to revise a stop that you are not viewing. Open that stop later. It shows the revision,
  with no banner.

**Answers and threads across a revision.** Pick a stop with two checks, answer both, and send the answers.

- Ask Claude to revise the stop, keeping one check's ID and dropping the other check. The kept check keeps its answer.
  The dropped check's answer shows "Outdated" on the earlier version, and "Send answers" leaves it out.
- On another stop, select words in two fields of its text, for example the headline and the prose, and send a question
  from each selection. Ask Claude to revise only the prose. The prose's thread turns "Outdated". The headline's thread
  stays open.

**Re-plan and reorder.** The Plan panel shows the familiarity level on one quiet line, with a "⋯" menu at its end. The
menu offers "Re-plan…" and "Reorder…".

- Choose "Re-plan…", write how the plan should change, for example to split a chapter, and send it. Claude revises the
  plan with `set_plan`, keeps the IDs of the stops that stay, and says in the reply what changed.
- Choose "Reorder…", drag a stop to a new place or move it with the arrows, and send the order. Claude applies it, and
  says in the reply where its order differs from yours.

**The familiarity level.** Open the second stop, then choose "Re-plan…" and pick another level. Nothing goes to Claude
until you press "Send to Claude".

- Claude rewrites each stop after the one you view at the new level, each as a new version. The stops you read keep
  their version. Words that you write in the optional box reach Claude with the change.

**Skips and done marks.** The right end of a stop's toolbar has a check, a skip button with a forward arrow, and a
"⋯" menu. Click the skip button.

- The stop shows "Skipped" in the plan and in the stop navigation. End the tour's session, skip another stop, and
  reload the page. Both skips remain. Reattach the tour.
- On a revised stop, click the check. It turns green, and the Plan panel counts the stop as done. Open "Version 1"
  from the "⋯" menu. The check stays in its place but is disabled.

**A busy session.** Ask Claude in the terminal for a task that takes a while, such as reading every file of the PR.
While it works, choose "Explain deeper…" from a stop's "Ask Claude" menu.

- The Stop panel shows the request as pending. Claude declines it with a reply that says it is busy, and tells you in
  the terminal. Record whether it declined at once or finished its task first. This is model behavior, not
  guaranteed.

**A plan-first tour.** In the tour's session, ask `/lotsen:tour <pr-url> some plan-first`, and choose a new tour when
Claude offers the saved one.

- Claude creates the tour, writes the plan with `set_plan`, and stops. The Plan panel shows "Claude proposes this plan
  and writes no stop until you approve it."
- Ask Claude to write a stop anyway. `put_stop` is refused while the plan waits.
- Reorder two stops, skip one, change the level, write a note and click "Approve plan". Claude writes the stops in
  your order, at the new level, and leaves the skipped stop as skipped.

Go back to the first tour for section 10. When it shows "No session", reattach it.

## 10. New updates

Section 9's revisions left versions and outdated answers on the tour. Keep it attached, and open a stop whose lines
your push will change.

**The notice.** Push a commit to the PR's source branch that changes lines of two stops and leaves the other stops
alone.

- Within a moment, or after a click on "Check the PR for a new update" in the top bar, a notice says "Update N is
  available: 2 stops affected. This tour shows update M until you move it to update N."
- The notice also appears when you open the tour, when a session attaches it, before you send a question, and when
  you reload. Record each.
- Push a commit to the PR's target branch alone. No notice appears for it. Record whether Azure DevOps lists a new
  update for it.

**Update with no session.** End the tour's session.

- "Move to update N" in the notice is disabled, with its reason beside it. The notice stays.
- Reattach the tour.

**Update.** Click "Move to update N".

- The diff shows the new update. Unchanged passages and code threads sit on their moved lines. The two stops whose
  lines changed show "Stale since update N" in the plan, naming the update that made them stale.
- Claude receives a "Move to update N" decision, rewrites each stale stop with `put_stop` against the new pin,
  and resolves it with a short reply. Each rewritten stop loses its stale mark in the plan as soon as Claude writes it.
- The stale stop you view shows a banner, "Claude revised this stop for update N while you read it", and keeps its
  text. Click "Show the new version". The stop shows the revision, with no stale mark.
- A code thread on lines that the new update changed turns "Outdated". It leaves the Threads list, and comes back
  with its original commit when you add "Outdated" to the state filter.

**Edge cases.** Check each of these when your PR allows it.

- Merge the target branch into the source branch and push. The check for a new update finishes without a timeout,
  and the server's log shows no full fetch.
- After an update that moves an unchanged stop's passage to other lines, open the stop. Its focus and its decoration
  land on the new lines.
- A stale stop whose passages are all on the base side, while the target branch did not move: its base passages stay
  decorated, and its head `ref:` links say that they point at an earlier commit.
- Narrow the Plan panel. The plan row's "Stale since update N" wraps cleanly.

## 11. The end-of-tour summary

By now the tour holds answers, threads, skipped and stale stops.

**The summary page.** End the tour's session, and open the summary page after the last stop.

- With no session attached, the page shows the decisions, concerns and open questions from your answers, the risk
  stops, the threads by state, and the skipped and stale stops. "Summarize" is disabled, with its reason.

**Summarize.** Reattach the tour. Click "Summarize" and send it.

- The page says "Claude is summarizing the review." Claude reads the summary with `read_summary`, writes its
  synthesis with `write_synthesis`, and resolves the action with a short reply. "Claude's synthesis" appears on the
  page.
- Answer another check and send it. The page says "This synthesis predates changes to the summary below". Click
  "Summarize" again. The new synthesis replaces the old one.

**Follow-up actions.**

- "Draft work items": Claude proposes a work item for each decision that needs follow-up work, in the terminal. It
  creates none until you approve it there. Approve one, and decline the rest.
- "Walk through open questions": Claude starts one thread for each open question, each with a recommendation.
- "Draft PR comments": Claude returns the drafts as text in the terminal, and posts nothing until you approve it
  there. Decline them all, or approve one on a throwaway PR.

**The server's exit.** End the tour's session, the spare session, and any other Claude Code session of the demo.

- For about 60 seconds, `lotsen open` still opens the UI. Then it says that no server is running, even with the tour
  open in the browser.

## 12. Delete tours and prune clones

These checks delete the tour from step 5, so run them last. Keep the UI open in a second browser tab as well.

- Open the tour in the second tab. In the first tab, click the delete button beside the tour in the tour switcher. A
  dialog asks you to confirm and names the tour. Click "Cancel". Nothing changes.
- Click the delete button again, then "Delete tour". The tour leaves the list in both tabs without a reload, and the
  second tab shows no tour. `~/.local/share/lotsen/tours` no longer holds the tour's file.
- Ask Claude to read the tour's state. The call is refused with `tourId: no tour has the ID …`, and Claude tells you
  and stops working on the tour.
- When no other tour uses the PR's repository, its clone in `~/.local/share/lotsen/clones` is gone. A tour of
  another PR of the same repository keeps the clone.
- Open the settings panel and click "Prune unused clones". It says how many clones it removed, or "No clone needed
  pruning." Click it again. It says "No clone needed pruning."
- Create a tour of a large repository and click "Prune unused clones" while the tour pins. The pin still reaches
  `pinned`, and the clone stays.

## 13. Record

Record the date, the Claude Code version (`claude --version`), the lotsen build (`lotsen --version`), the MCP
registration that worked (user scope, project `.mcp.json` or `just demo`), and the result of each check above. For the stops, record any stop that did not appear and any `ref:` link that landed on the wrong
lines. For the pin, also record whether the Azure DevOps organization served the blob-less clone, and
whether a pin of a PR whose source branch was deleted reached `pinned`. For the background server, record whether
Claude Code started it and whether it exited about 60 seconds after the session ended.

## When something fails

- "The lotsen server stopped or could not be reached": ask Claude to continue. The next tool call starts a server.
- "The lotsen server could not start" or "did not answer within 10 seconds": read the log that the reason names, or run
  `lotsen serve` to see the error in the terminal.
- `lotsen open` says that no server is running: start `lotsen serve`, or a Claude Code session with the lotsen channel.
- "another build than this adapter": the server and the adapter run different publishes, and another session uses the
  server or it is a `lotsen serve`. Run `lotsen restart`, and
  restart Claude Code or reconnect `lotsen` in `/mcp`, so both run the binary you published last.
- "refused this adapter's credentials" or "could not open its session": run `lotsen restart`, then ask Claude to
  continue.
- `lotsen stop` says that the server "did not exit within 30 seconds": end the process with the PID it names.
- `lotsen restart` says that the server that answers runs another build: a Claude Code session whose lotsen is another
  install, or a `lotsen serve` of another build, started it first. Close that session or stop that server, then run
  `lotsen restart` again.
- No browser opened: Claude gives a link that works once, for a few minutes. `lotsen open` opens a fresh one, and `lotsen open --print-link` prints a fresh link to open by hand.
- The lotsen tools are missing: check `/mcp`, and the registration in step 3.
- The pin stays `failed`: its reason says what failed. `lotsen serve` also prints it as a warning.
- "Another lotsen server (PID …) is running": stop that server first with `lotsen stop`, or set `LOTSEN_DATA_DIR` to another data directory.
- "Port … of 127.0.0.1 is in use": choose another port with `--port` or `LOTSEN_PORT`, or set neither.
- "LOTSEN_FACT_CACHE_MB is '…', which is not a size in MiB": set it to a whole number from 1 to 2147483647, or unset it for 64 MiB.
