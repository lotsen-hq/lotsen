#!/usr/bin/env bash
# Shortcuts for the manual checks in docs/live-test.md.
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
lotsen="$root/src/Lotsen/bin/Release/net11.0/linux-x64/publish/lotsen"
# A relative XDG_DATA_HOME is ignored, as lotsen's own data directory ignores it.
[[ ${XDG_DATA_HOME:-} == /* ]] && demo_data="$XDG_DATA_HOME/lotsen-demo" || demo_data="$HOME/.local/share/lotsen-demo"

usage() {
   cat <<EOF
Usage: scripts/live-test.sh <command>

  check                       Report what is ready: publish, server, MCP registration, Azure CLI sign-in.
  build                       Build the UI, then publish lotsen.
  serve                       Run lotsen serve in this terminal.
  register                    Register the lotsen MCP server in user scope, replacing an older entry.
  plugin                      Print the path of a new copy of the plugin without its MCP entry, for --plugin-dir.
  claude [dir]                Start an interactive Claude Code session in dir (default: here) with the development channel.
  pin <pr-url> [familiarity]  Create a tour in a headless session and print its pin section. Familiarity: familiar, some or new.
  demo <pr-url> [familiarity] [dir]
                              Tour the PR in an interactive session in dir (default: a new empty directory), with a server on
                              the demo data directory ($demo_data). Publishes first when the sources changed.
  demo-link                   Print a new link to the UI of the demo's server, which works once.
EOF
}

need_publish() {
   [[ -x "$lotsen" ]] || { echo "No publish at $lotsen. Run: scripts/live-test.sh build" >&2; exit 1; }
}

# The plugin's own MCP entry runs a released lotsen through npx, and a session started here must talk only to this checkout's publish.
plugin_copy() {
   local copy
   copy=$(mktemp -d)
   cp -R "$root/plugin/." "$copy"
   rm "$copy/.mcp.json"
   echo "$copy"
}

check() {
   [[ -x "$lotsen" ]] && echo "publish:      yes" || echo "publish:      no (run build)"
   pgrep -f "$lotsen (serve|daemon)" >/dev/null && echo "server:       running" || echo "server:       not running (run serve, or let Claude Code start one)"
   local registered
   registered=$(claude mcp get lotsen 2>/dev/null | sed -n 's/^[[:space:]]*Command: //p') || true
   case "$registered" in
      "") echo "registration: no (run register)" ;;
      "$lotsen") echo "registration: yes" ;;
      *) echo "registration: $registered is not this checkout's publish (run register)" ;;
   esac
   az account show -o none 2>/dev/null && echo "az:           signed in" || echo "az:           not signed in (run az login)"
}

build() {
   (cd "$root/ui" && npm ci && npm run build)
   (cd "$root" && dotnet publish src/Lotsen -c Release)
}

pin() {
   local url=${1:?pin needs a pull request URL} familiarity=${2:-familiar}
   need_publish
   # An empty directory keeps the session out of any project, and the pin needs no checkout.
   cd "$(mktemp -d)"
   claude -p "/lotsen:tour $url $familiarity. After create_tour, call read_state until pin.state is pinned or failed. Then stop and print the pin section of read_state verbatim." \
      --plugin-dir "$(plugin_copy)" --allowedTools "mcp__lotsen__*" --output-format text
}

stale() {
   [[ ! -x "$lotsen" ]] && return 0
   local newer
   newer=$(find "$root/src" "$root/ui/src" "$root/ui/index.html" "$root/ui/package.json" "$root/ui/package-lock.json" "$root/ui/vite.config.ts" "$root/ui/svelte.config.js" \
      "$root/Directory.Build.props" "$root/Directory.Packages.props" "$root/global.json" \( -name bin -o -name obj \) -prune -o -newer "$lotsen" -print -quit)
   [[ -n "$newer" ]]
}

demo() {
   [[ $# -ge 1 && -n "$1" ]] || { usage >&2; exit 2; }
   local url=$1 familiarity=${2:-familiar} dir=${3:-}
   case "$familiarity" in
      familiar | some | new) ;;
      *) echo "Familiarity '$familiarity' is not one of familiar, some or new." >&2; exit 2 ;;
   esac
   az account show -o none 2>/dev/null || { echo "The Azure CLI is not signed in, so the server cannot pin the PR. Run: az login" >&2; exit 1; }
   if stale; then
      build
      # A demo server that still runs the earlier build refuses the new adapter.
      LOTSEN_DATA_DIR="$demo_data" "$lotsen" stop
   fi
   cd "${dir:-$(mktemp -d)}"
   # An --mcp-config server named lotsen replaces the user-scope registration for this session.
   local config
   config=$(printf '{"mcpServers":{"lotsen":{"command":"%s","args":["mcp"],"env":{"LOTSEN_DATA_DIR":"%s"}}}}' "$lotsen" "$demo_data")
   echo "Demo data directory: $demo_data. A new link to the UI: just demo-print-link"
   # The prompt comes first, because --mcp-config and the channel flag each take every word after them.
   exec claude "/lotsen:tour $url $familiarity" --plugin-dir "$(plugin_copy)" --mcp-config "$config" --dangerously-load-development-channels server:lotsen
}

case "${1:-}" in
   check) check ;;
   build) build ;;
   serve) need_publish; exec "$lotsen" serve --log-level Information ;;
   register)
      need_publish
      claude mcp remove --scope user lotsen >/dev/null 2>&1 || true
      claude mcp add --scope user lotsen -- "$lotsen" mcp
      ;;
   claude)
      cd "${2:-.}"
      # The flag must come last: it takes every word after it as a channel entry.
      exec claude --plugin-dir "$(plugin_copy)" --dangerously-load-development-channels server:lotsen
      ;;
   plugin) plugin_copy ;;
   pin) shift; pin "$@" ;;
   demo) shift; demo "$@" ;;
   demo-link) need_publish; exec env LOTSEN_DATA_DIR="$demo_data" "$lotsen" open --print-link ;;
   *) usage; exit 2 ;;
esac
