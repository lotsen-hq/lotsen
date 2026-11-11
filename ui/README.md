# Lotsen UI

The Svelte 5 page that `lotsen serve` embeds and serves. Node 24.15 or later (see `engines` in `package.json`).

```sh
npm ci
npm test        # Vitest with jsdom, once
npm run check   # svelte-check
npm run lint    # ESLint, including the page's CSP and HTML rules
npm run build   # writes dist/ and checks it against the server's content security policy
```

## Demo mode

`just ui-demo` (`npm run demo`) runs the page in Vite's dev server with hot reload, on real lotsen servers, without
`lotsen serve`, Claude or a pull request. It needs the .NET SDK. Open the address that Vite prints.

- `demo/worlds.mjs` is a Vite plugin. It starts the test host of `tests/Lotsen.TestHost` once, as the UI's flow tests do
  (`tests/backend/host.mjs`, which builds the host unless `LOTSEN_TEST_HOST` names a built `Lotsen.TestHost.dll`). It seeds
  the sample tours into a template world once, through the server's real operations, and gives each load of the page a
  fresh world copied from that template. The host stops when Vite stops, and with it every world. A world whose page
  closed stops at a later load of the page.
- Each load posts to `/__demo/world` for its world, and every request of the page names that world in the
  `Lotsen-Demo-World` header. The plugin forwards the page's `/api/*` requests to the world's server, with the event
  stream unbuffered, under the server's own `Host` and without the `Origin` header, which the server's request guards
  would refuse. The page signs in with a one-time code of its world, as a link from `lotsen open` does, and keeps the
  address's fragment and the history entry's state, so back, forward and a reload restore the tour, the stop, the file and
  the stop-files stack. A reload starts from a new world.
- A new world's server holds no preferences, so the page copies up the settings that the browser keeps for the demo's
  origin, as a page does from an older lotsen. A change of a setting reaches the world's server and the browser alike, so
  the next load starts with it.
- The plugin also forwards `/control/*` to the host and adds the host's token, so the page holds none. The page binds
  the sample tours to two real agent sessions through it: a confirmed one and an unconfirmed one. The plugin answers
  `/control/*` and `/__demo/world` only at `localhost`, `127.0.0.1` or `[::1]`, and only to the demo's own page or to a
  request from no page, and it refuses a request from a page of another origin.
- The **Demo: act as Claude** panel acts as those sessions. Each pending stop of the open tour gets a button that writes
  its sample content with `put_stop`, and each question in the sessions' inbox gets a reply box that calls `reply`, with
  the resolution picked beside it. A question that the unconfirmed session receives carries its nonce, which the panel's
  Ack sends with `ack`. A refusal shows the server's reason.
- `demo/sampleTours.ts` holds the sample tours as seed specs, and `demo/seed.ts` takes them through the operations: a
  failed pin, a pinned tour without a plan, a plan-first tour whose plan waits for approval, a plan with every stop kind,
  and tours in every tour state, two of them on a completed and an abandoned pull request. Every tour pins one storefront
  change with files of every kind and mark: added, modified, deleted, renamed, binary, large, line-endings-only and
  re-indented. The checkout tour has pending, ready and revised stops, every section of a stop on some and not on others,
  passages with labels, file marks, a synthesis that predates the summary, a skipped stop, answers in each state and an
  outdated one, notes, threads at every kind of anchor with a reply of each resolution, Claude's own threads, resolved
  tour actions of each kind and a familiarity change, and a newer iteration whose Update makes the payment and gateway
  stops stale. Each world adds what no template keeps: a question that Claude received, a follow-up and an explain that
  wait, and a question to the unconfirmed session.
- The sample tours lack what the real operations or the host cannot make: a pin in progress, because a world's platform
  answers at once, a symbolic link and a submodule, because the host commits files only, a pull request that changes no
  files, and clones for the settings panel's prune. A world's platform reports one pull request for every URL, so every
  tour but the checkout tour pins its newer iteration, and opening the completed or abandoned tour finds its pull request
  active again.
- A new UI state gets a sample in `demo/sampleTours.ts`. `tests/app/sampleTours.test.ts` seeds the samples into real
  worlds, checks that they hold each state, and opens every sample tour, stop, passage, diff, folder and file.

The dev server injects styles as `<style>` elements without a nonce, so it runs without the server's content security
policy. `just ui-demo-csp` (`npm run demo:csp`) builds the demo into `demo-dist/` and serves it with Vite's preview, on
the same worlds, with that policy and a fixed nonce, to check a change against the policy without the server.
`demo/vite.config.ts` holds a copy of the policy, so keep it in step with `src/Lotsen.Core/Server/ContentSecurityPolicy.cs`.
Neither recipe writes to `dist/`, and `npm run build` never builds the demo.

## Flow tests on Windows

The flow tests write their worlds' repositories and data under the temporary directory, and Microsoft Defender's
real-time scan of those files slows them until many time out. Exclude a folder from the scan and name it in
`LOTSEN_TEST_TEMP`, and the test host keeps its directories there:

```powershell
Add-MpPreference -ExclusionPath "$env:LOCALAPPDATA\lotsen-test-temp"   # in an elevated shell, once
$env:LOTSEN_TEST_TEMP = "$env:LOCALAPPDATA\lotsen-test-temp"           # or set it in your user environment
just ui-test
```

## The layout suite

`just ui-e2e` (`npm run e2e`) runs `e2e/` in Playwright's Chromium against the demo, for what jsdom cannot lay out: the
scroll to a focused passage, the inline explanation below its anchor, the spacer that aligns the side-by-side panes,
collapsed rows, the pane splitter and the file that the stop-files stack reports. It also loads pages of the demo
together, each of which must get a world of its own. Playwright starts the demo on a free port that the suite picks at
run time, so two checkouts can run the suite at once, and `LOTSEN_E2E_PORT` pins the port. The `csp` project runs
`e2e/csp.spec.ts` against a second server, the built demo that `npm run demo:csp` serves with the server's policy, on the
port that `LOTSEN_E2E_CSP_PORT` pins. Playwright stops both servers once the suite ends, and their test hosts stop with them. `just ui-browser` downloads the Chromium build that the suite
needs.

The components reach the editors through `EditorSurface` (`src/lib/editor/surface.ts`), and within `src/` only
`src/lib/editor/` imports CodeMirror, which the lint rule enforces. `tests/editorSurfaceCalls.test.ts` checks the
components' calls on recording surfaces, and the flow tests run the real editors.

## Stress runs

`just stress` (`scripts/stress.mjs`) hunts for flaky tests. It builds once, starts one busy loop per core, and runs the
.NET suite and the UI's tests side by side for 5 rounds. It then prints how many rounds each suite passed, with the
round and first message line of each failed test, and exits 1 when any test failed. `--rounds`, `--hogs` and `--suites`
(`dotnet`, `ui`, `e2e`) change the defaults, and `just stress --help` lists them. Each round's reports and output go to
`.stress/<run>/round-<n>/` at the repository root.

## The contract with the server

The JSON shapes that the UI sends and receives come from the server's C# types, so no UI code declares them by hand:

- `src/lib/contract.schema.json` is the schema document that the .NET test `ContractTests` (`Category=Contract`) writes from the operations that the UI calls and the event kinds.
- `src/lib/contract.generated.ts` holds the TypeScript types that `json-schema-to-typescript` writes from that document: each input, output and event payload, `EventPayloads` with
  `EventName`, and `Operations` with `OperationName`. `src/lib/types.ts` exports them with the UI's own types.
- `tests/contract/fixtures.generated.ts` holds one event of each kind and the output of each operation as the server's serializer writes them. The type check checks them against the
  types, and `tests/contract/fixtures.test.ts` feeds them through the UI's handlers and API.

Change a C# type, then run `just contract` from the repository root, which writes all three. `ContractTests` fail while the schema or the fixtures differ from the code, and
`tests/contract.test.ts` fails while the TypeScript differs from the schema. Never edit the generated files.

## How the build reaches the executable

`src/Lotsen.Core/Lotsen.Core.csproj` embeds every file in `ui/dist/` as a resource named `ui/<path>`, so build the UI
before the .NET build:

- A Release build or a publish fails when `ui/dist/index.html` is missing, so a placeholder page cannot ship.
- A Debug build, and with it `dotnet test`, embeds `src/Lotsen.Core/placeholder-ui/` when `ui/dist/` is absent, so the .NET
  tests need no Node. Once `ui/dist/` exists, Debug builds embed it too, so delete it or rebuild it when it is stale.
- `-p:LotsenUiDirectory=<dir>/` embeds another directory.

The build emits fixed names, `index.html`, `app.js` and `app.css`, plus the code split from `app.js` as
`chunks/<package>.js`, such as the diff view's languages, and the fonts that `app.css` loads, such as `seti.woff`; the server revalidates every file, so hashed names buy nothing.
`scripts/check-dist.mjs` fails the build when `dist/` holds other files, when `index.html` has an inline script or style
or lacks the `__LOTSEN_CSP_NONCE__` placeholder that the server replaces with each load's style nonce, or when a script
carries scoped component CSS (`.svelte-<hash>` selectors), as a component compiled with injected CSS does. CodeMirror
builds its styles in the script too, but without Svelte's scope, and its `<style>` element takes the nonce.

## Styling

The page uses Tailwind CSS 4, through its Vite plugin, and shadcn-svelte components built on bits-ui. `src/app.css` is the main stylesheet: it imports Tailwind and
holds the theme, the shared rules of the two file trees and the `markdown` class for server-rendered Markdown. It imports `src/syntax-colors.css`, which holds the diff
editors' syntax colors for each syntax color family and theme. The build writes both to `app.css`.

- The shadcn-svelte components live in `src/lib/components/ui/`. Add one with `npx shadcn-svelte@<version in package.json> add <name>`, which reads
  `components.json`. When an item ships theme variables, the CLI writes them into `src/app.css` under `:root` and `.dark`, where they work as they
  are. Review that diff, and check the component against the rules for the page below.
- Icons come from Lucide, `@lucide/svelte`, which renders an inline `<svg>` with attributes and no `style`. Import each icon from its own module, such as
  `@lucide/svelte/icons/panel-left`, so the bundle carries only the icons the page uses. A toolbar's icon toggle is `src/components/IconToggle.svelte`,
  whose label names the toggle and fills its tooltip.
- The file rows of the file trees show VS Code's default file icon theme, Seti: `src/components/FileIcon.svelte` draws the glyph from its font in its colors,
  and `src/lib/fileIcons.ts` picks the icon as VS Code does. `src/seti/` holds the font, the theme's data and the license notices, from microsoft/vscode at the commit
  that `scripts/generate-seti.mjs` names. Run `node scripts/generate-seti.mjs` to write them again; it needs network access to GitHub. Folders, symbolic links and
  submodules keep their Lucide icons.
- Tooltips are shadcn-svelte's Tooltip, shown through `src/components/WithTooltip.svelte`, or `TooltipButton.svelte` for a button, never a `title` attribute.
  The rows of a long list sit in a `TooltipGroup.svelte`, so they share one tooltip. A tooltip does not name its trigger, so an icon button keeps its `aria-label`.
- Components build from these primitives and from the theme variables in `src/app.css`, such as `bg-card` and `text-muted-foreground`. A value the
  theme lacks goes into the theme first.
  - The theme has no Tailwind palette, so a class such as `text-red-500` or `bg-white` generates nothing, and `npm run lint` reports it as unknown.
  - Arbitrary values and variants, such as `p-[3px]`, `p-(--x)` and `min-[600px]:`, are not allowed. `npm run lint` rejects them in string and array
    `class` values and in `cn` and `tv` calls. It does not check the object form `class={{ … }}` or other variables. The generated components keep
    the arbitrary values they come with.
  - The lint uses `eslint-plugin-better-tailwindcss`, which also rejects conflicting, duplicate and deprecated classes.
- The theme has a light and a dark palette. `src/lib/colorScheme.ts` sets the `dark` class on `<html>` from the reviewer's theme setting: light, dark, or
  the system's color scheme, which it follows as it changes, with dark when the system does not ask for light. The dark palette, Tailwind's `dark:`
  variant and the diff editors all follow that class. Until the script runs, a `prefers-color-scheme` rule in `src/app.css` gives such a system a dark
  background, so the page does not flash white.

## Rules for the page

- The page runs under `style-src 'self' 'nonce-…'` with no `'unsafe-inline'`: no `style="…"` attributes, no
  `style:` directives in markup, and any `<style>` created at run time takes the nonce from `src/lib/cspNonce.ts`.
  CodeMirror gets it through `EditorView.cspNonce`.
- Dockview lays out the workspace's panels. It sizes and places its elements through the CSSOM (`element.style`), which the policy allows, and the `<style>`
  elements that it copies into a pop-out window take the nonce from `src/lib/cspNonce.ts`. `e2e/csp.spec.ts` loads the page and drags a panel under the policy, and fails on any violation.
- No HTML insertion, such as `{@html}`, `innerHTML`, `outerHTML`, `insertAdjacentHTML` or `srcdoc`: code and text go in as text nodes. The one exception is
  `src/components/Prose.svelte`, which inserts its `html` prop, the prose of a stop or a reply as the server rendered it. `npm run lint` checks both rules in every component and
  module, the generated ones included.
- `npm run lint` also rejects bits-ui's Command and PinInput, which break the policy.
- The event stream is read with `fetch`, because `EventSource` cannot send the UI credential in the `Authorization` header.
