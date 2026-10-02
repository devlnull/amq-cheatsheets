#!/bin/bash
# One-line installer:  curl -fsSL https://raw.githubusercontent.com/devlnull/amq-cheatsheets/main/get.sh | bash
set -euo pipefail
URL="https://github.com/devlnull/amq-cheatsheets/releases/latest/download/amq-cheatsheets.tar.gz"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
echo "Downloading latest release..."
curl -fsSL "$URL" | tar -xz -C "$TMP" --strip-components=1
"$TMP/install.sh" "$@"
