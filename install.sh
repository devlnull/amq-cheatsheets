#!/bin/bash
# amq-cheatsheets installer for macOS (idempotent; safe to re-run to upgrade).
#
#   ./install.sh              install / upgrade
#   ./install.sh --uninstall  remove launcher + `cs` link (keeps your ~/Cheatsheets)
#   ./install.sh --purge      with --uninstall: also delete ~/Cheatsheets
#   Options: --quiet  --no-app  --no-link
#   Env:     CS_HOME (default ~/Cheatsheets)  CS_BIN_DIR  CS_APP_DIR (default ~/Applications)
#
# What it does:
#   1. copies the library to $CS_HOME (a normal folder, so Spotlight indexes it; your own sheets are never overwritten)
#   2. builds the text DB + index, Finder tags and the Raycast scripts
#   3. links `cs` onto your PATH
#   4. creates "Cheatsheets.app" so ⌘Space → "Cheatsheets" opens a search dialog
#   5. tells you how to enable Raycast (one click, if Raycast is installed)
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CS_HOME="${CS_HOME:-$HOME/Cheatsheets}"
CS_APP_DIR="${CS_APP_DIR:-$HOME/Applications}"
UNINSTALL=0; PURGE=0; QUIET=0; APP=1; LINK=1
for a in "$@"; do
  case "$a" in
    --uninstall) UNINSTALL=1 ;; --purge) PURGE=1 ;; --quiet) QUIET=1 ;;
    --no-app) APP=0 ;; --no-link) LINK=0 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "unknown option: $a" >&2; exit 2 ;;
  esac
done

say()  { [ "$QUIET" = 1 ] || printf '%s\n' "$*"; }
step() { [ "$QUIET" = 1 ] || printf '\033[1;34m==>\033[0m \033[1m%s\033[0m\n' "$*"; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

[ "$(uname -s)" = "Darwin" ] || die "macOS only"

# Where to put the `cs` command: first writable dir already on PATH, else ~/.local/bin
pick_bin_dir() {
  [ -n "${CS_BIN_DIR:-}" ] && { echo "$CS_BIN_DIR"; return; }
  for d in /opt/homebrew/bin /usr/local/bin; do
    [ -d "$d" ] && [ -w "$d" ] && { echo "$d"; return; }
  done
  echo "$HOME/.local/bin"
}
BIN_DIR="$(pick_bin_dir)"

# ---------------------------------------------------------------- uninstall
if [ "$UNINSTALL" = 1 ]; then
  step "Uninstalling"
  rm -rf "$CS_APP_DIR/Cheatsheets.app" && say "removed $CS_APP_DIR/Cheatsheets.app"
  for d in "$BIN_DIR" /opt/homebrew/bin /usr/local/bin "$HOME/.local/bin"; do
    if [ -L "$d/cs" ] && [[ "$(readlink "$d/cs")" == "$CS_HOME/cs" ]]; then rm -f "$d/cs"; say "removed $d/cs"; fi
  done
  if [ "$PURGE" = 1 ]; then rm -rf "$CS_HOME"; say "deleted $CS_HOME"; else say "kept $CS_HOME (use --purge to delete)"; fi
  say "Raycast: remove the script directory in Settings → Extensions → Script Commands if you added it."
  exit 0
fi

# ---------------------------------------------------------------- install
step "Checking Python 3"
PY="$(command -v python3 || true)"
if [ -z "$PY" ] || ! "$PY" -c 'import sys; sys.exit(sys.version_info < (3, 9))' 2>/dev/null; then
  die "python3 (3.9+) not found. Run:  xcode-select --install   (or: brew install python)"
fi
say "using $PY ($("$PY" -V 2>&1))"

step "Installing library to $CS_HOME"
mkdir -p "$CS_HOME"
if [ "$SRC" != "$CS_HOME" ]; then
  # no --delete: sheets you added yourself are kept. Bundled sheets are refreshed.
  rsync -a --exclude='.git' --exclude='__pycache__' --exclude='db/index.json' --exclude='dist' \
        --exclude='tests' --exclude='packaging' --exclude='.DS_Store' "$SRC/" "$CS_HOME/"
fi
chmod +x "$CS_HOME/cs" "$CS_HOME/install.sh"
xattr -dr com.apple.quarantine "$CS_HOME" 2>/dev/null || true

step "Building database, index, Spotlight tags and Raycast scripts"
"$CS_HOME/cs" build

if [ "$LINK" = 1 ]; then
  step "Linking the \`cs\` command"
  mkdir -p "$BIN_DIR"
  ln -sf "$CS_HOME/cs" "$BIN_DIR/cs"
  say "$BIN_DIR/cs → $CS_HOME/cs"
  case ":$PATH:" in *":$BIN_DIR:"*) ;; *) say "note: $BIN_DIR is not on your PATH. Add to ~/.zshrc:  export PATH=\"$BIN_DIR:\$PATH\"" ;; esac
fi

if [ "$APP" = 1 ]; then
  step "Creating the Spotlight launcher"
  "$CS_HOME/cs" app --dest "$CS_APP_DIR" >/dev/null || say "warning: could not build Cheatsheets.app"
  [ -d "$CS_APP_DIR/Cheatsheets.app" ] && say "$CS_APP_DIR/Cheatsheets.app"
  command -v mdimport >/dev/null && mdimport "$CS_APP_DIR/Cheatsheets.app" "$CS_HOME" 2>/dev/null || true
fi

RAYCAST=""
for d in /Applications "$HOME/Applications"; do [ -d "$d/Raycast.app" ] && RAYCAST="$d/Raycast.app"; done

if [ "$QUIET" != 1 ]; then
  printf '\n\033[1;32m✔ Installed amq-cheatsheets %s\033[0m\n\n' "$(cat "$CS_HOME/VERSION" 2>/dev/null || echo dev)"
  cat <<EOF
  Spotlight   ⌘Space → type "Cheatsheets" → search dialog.
              Spotlight also finds the sheets directly: by title, by tag (tag:git) and by shortcut/command text.
  Terminal    cs search "cmd shift o"   |   cs open git   |   cs show intellij
  Add sheets  put them in $CS_HOME/sheets/ (cs new "Docker" --image docker.png), then run: cs build
EOF
  if [ -n "$RAYCAST" ]; then
    if [ -t 1 ] && command -v pbcopy >/dev/null; then printf '%s' "$CS_HOME/raycast" | pbcopy; COPIED=" (path copied to clipboard)"; else COPIED=""; fi
    cat <<EOF

  Raycast     one-time step (Raycast has no API to do this for you):
                Raycast → Settings → Extensions → Script Commands → "Add Directories"
                → choose: $CS_HOME/raycast$COPIED
              Then type "cheatsheet", or a sheet name (e.g. "git"), in Raycast.
EOF
  else
    say "
  Raycast     not found. If you install it later, add $CS_HOME/raycast as a Script Commands directory."
  fi
fi
