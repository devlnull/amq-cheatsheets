#!/bin/bash
# Build the distributable files into dist/ :
#   amq-cheatsheets-<ver>.pkg     double-click installer (unsigned unless SIGN_ID is set)
#   amq-cheatsheets-<ver>.tar.gz  for install.sh / Homebrew / curl
#   SHA256SUMS
# Optional env: SIGN_ID="Developer ID Installer: Name (TEAMID)"
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
VER="$(tr -d '[:space:]' < VERSION)"
OUT="$ROOT/dist"; STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
rm -rf "$OUT"; mkdir -p "$OUT"

echo "==> testing"
python3 -m unittest discover -s tests >/dev/null
./cs build --no-spotlight >/dev/null     # make sure db/ + md + raycast are current

echo "==> staging $VER"
PKGROOT="$STAGE/root/usr/local/share/amq-cheatsheets"
mkdir -p "$PKGROOT"
rsync -a --exclude='.git' --exclude='__pycache__' --exclude='.DS_Store' --exclude='dist' --exclude='tests' \
      --exclude='db/index.json' --exclude='sheets/*/*.render.html' --exclude='.gitignore' \
      ./cs ./install.sh ./VERSION ./README.md ./schema ./sheets ./tools ./raycast ./db "$PKGROOT/"
mkdir -p "$PKGROOT/tools" && cp packaging/Cheatsheets.applescript "$PKGROOT/tools/"   # launcher template ships with the payload

xattr -cr "$STAGE"            # no ._* AppleDouble files in the archives (install.sh re-applies Finder tags)
export COPYFILE_DISABLE=1

echo "==> tarball"
TARDIR="$STAGE/amq-cheatsheets-$VER"; cp -R "$PKGROOT" "$TARDIR"
tar -C "$STAGE" -czf "$OUT/amq-cheatsheets-$VER.tar.gz" "amq-cheatsheets-$VER"

echo "==> pkg"
SIGN=(); [ -n "${SIGN_ID:-}" ] && SIGN=(--sign "$SIGN_ID")
pkgbuild --root "$STAGE/root" --identifier com.amq.cheatsheets --version "$VER" \
         --scripts packaging/pkg/scripts --install-location / ${SIGN[@]+"${SIGN[@]}"} \
         "$OUT/amq-cheatsheets-$VER.pkg" >/dev/null

( cd "$OUT" && shasum -a 256 amq-cheatsheets-"$VER".* > SHA256SUMS )
echo; ls -lh "$OUT"; echo; cat "$OUT/SHA256SUMS"
[ -n "${SIGN_ID:-}" ] || echo "(pkg is unsigned: first open needs right-click → Open, see README)"
