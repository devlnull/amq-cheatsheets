# amq-cheatsheets

Every cheatsheet I need for work, in one repo: **searchable by title, tag, alias or the shortcut itself**, from the terminal, Raycast and Spotlight.

## Cheatsheets

<!-- sheets:start -->
| Cheatsheet | Category | Tags | Entries |
|---|---|---|---|
| [Git Essential Commands](sheets/git/git.png) | VCS | `git` `vcs` `version-control` `source-control` `cli` `terminal` `commands` `commit` | 76 |
| [IntelliJ IDEA macOS Shortcuts](sheets/intellij-idea/intellij-idea.png) | IDE | `intellij` `idea` `jetbrains` `ide` `shortcuts` `keyboard` `hotkeys` `keymap` | 81 |
<!-- sheets:end -->

## How it works

```
sheets/<id>/sheet.json     ← the ONLY thing you edit (title, tags, aliases, every shortcut)
sheets/<id>/<id>.png       ← the original image
        │
        │  ./cs build        (auto-runs when search sees a stale index)
        ▼
db/cheatsheets.jsonl       text database, one record per line (diff-friendly, committed)
db/index.json              inverted index: terms → sheets/entries (generated, git-ignored)
sheets/<id>/<id>.md        generated Markdown (readable on GitHub, indexed by Spotlight)
raycast/…                  generated Raycast Script Commands
Finder tags + comment      written to the png/md so Spotlight can find them by tag
```

Search is not a scan of files. `cs` loads one small index and does exact → prefix → typo-tolerant term lookup,
ranked by field weight (title > tag > alias > category > description; for shortcuts: key > action > section).
The lookup itself takes well under a millisecond; the rest of the ~35 ms is Python starting up.

## Install (macOS)

Download `amq-cheatsheets-<version>.pkg` from the Releases page and double-click it
(it is unsigned: if macOS blocks it, **right-click → Open**, or System Settings → Privacy & Security → *Open Anyway*).
Or, from the `.tar.gz` / a clone: `./install.sh`. Re-running upgrades; your own sheets are never overwritten.

What you get:

| | |
|---|---|
| `~/Cheatsheets/` | the library (a normal folder, so **Spotlight** indexes it: search by title, `tag:git`, or shortcut/command text) |
| **Cheatsheets.app** in `~/Applications` | ⌘Space → "Cheatsheets" → type a query → pick a result → opens the sheet |
| `cs` on your PATH | `cs search "cmd shift o"`, `cs open git`, `cs new …` |
| `~/Cheatsheets/raycast/` | Raycast Script Commands. **One manual step** (Raycast has no API for it): Settings → Extensions → Script Commands → *Add Directories* → that folder. The installer copies the path to your clipboard. |

Uninstall: `~/Cheatsheets/install.sh --uninstall` (add `--purge` to delete the library too).

## Release (maintainers)

```sh
echo 0.2.0 > VERSION
./packaging/build_release.sh      # tests, then dist/{*.pkg,*.tar.gz,SHA256SUMS}
```
Attach the three files to a GitHub release (`gh release create v0.2.0 dist/*`). To avoid the Gatekeeper prompt, sign with
an Apple *Developer ID Installer* certificate: `SIGN_ID="Developer ID Installer: Name (TEAMID)" ./packaging/build_release.sh`
(then notarize with `xcrun notarytool`).

## Search

```sh
./cs search rename            # shortcut by what it does
./cs search "cmd shift o"     # …or by the keys: "⇧⌘O", "command+shift+o", "Cmd-Shift-O" all work
./cs search intellij debug    # sheet + topic
./cs search intelij           # typos are fine
./cs search -t vcs            # filter by tag (-c for category), --json for scripts
./cs open intellij            # open the best match's image in Preview
./cs show intellij            # print a whole sheet in the terminal
./cs list | ./cs tags
```

Tip: `ln -s "$PWD/cs" ~/.local/bin/cs` to use `cs` from anywhere (it resolves its own location).

## Add a cheatsheet

```sh
./cs new "Git Cheatsheet" --image ~/Downloads/git.png --tags git,vcs,cli,branch,rebase --category VCS
```

Then fill in `sheets/git-cheatsheet/sheet.json` (the easy way: ask Claude Code to transcribe the image into
`sections`/`entries`), and run `./cs build`. Rules (checked by `./cs validate`, schema in `schema/sheet.schema.json`):

- `tags`: lowercase kebab-case, at least 3 – this is what makes it findable. Add `aliases` for nicknames/misspellings.
- `keys` (shortcuts) or `command` (CLI sheets like git): chords like `"cmd+shift+o"`; modifiers `ctrl opt shift cmd`; extra alternatives as more array items.
- Unsure a shortcut is the official default? Put the doubt in `"check"` – it shows up as ⚠ in search results.
- Text-only sheets (no image) are fine; they get a generated `.md` instead.

## Generate the image from the data

For sheets without an original image (like `git`), `tools/render_sheet.py` draws the dark card-style PNG straight from
`sheet.json` (needs Chrome): `python3 tools/render_sheet.py git`. Give sections a `color` and `icon`, and set
`render.layout` (e.g. `[2,3,3]` cards per row). Edit the JSON, re-render, `./cs build` – image and search data never drift apart.

## Raycast

1. Raycast → Settings → Extensions → **Script Commands** → *Add Directories* → choose `raycast/` in this repo.
2. You now have: **Search Cheatsheets** (type `rename` or `cmd shift o`), **Open Cheatsheet**, and one
   “<Title> Cheatsheet” command per sheet (type `intellij` in Raycast and press ↵).

The scripts are regenerated by `./cs build`, so new sheets show up automatically (Raycast rescans the directory).

## Spotlight

`./cs build` writes Finder tags + a comment onto each image and `.md`, and the Markdown contains every shortcut.
So Spotlight (⌘Space) finds a sheet by tag (`tag:jetbrains`), title, or by shortcut text (e.g. “Go to implementation”).

## Tests

```sh
python3 -m unittest discover -s tests -v
```
