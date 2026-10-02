"""Run: python3 -m unittest discover -s tests -v"""
import importlib.machinery
import importlib.util
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
_loader = importlib.machinery.SourceFileLoader("cs", str(ROOT / "cs"))
cs = importlib.util.module_from_spec(importlib.util.spec_from_loader("cs", _loader))
_loader.exec_module(cs)


def sheet(sid, title, tags, entries, **kw):
    s = {"id": sid, "title": title, "category": kw.get("category", "IDE"), "tags": tags,
         "aliases": kw.get("aliases", []), "description": kw.get("description", ""),
         "sections": [{"name": "Main", "entries": entries}], "_dir": ROOT / "sheets" / sid}
    return s


FIXTURE = [
    sheet("idea", "IntelliJ IDEA Shortcuts", ["intellij", "jetbrains", "version-control"],
          [{"action": "Rename", "keys": ["shift+f6"]},
           {"action": "Go to file", "keys": ["cmd+shift+o"]},
           {"action": "Search everywhere", "keys": ["shift+shift"]},
           {"action": "Open settings", "keys": ["cmd+,"]}], aliases=["intelij idea"]),
    sheet("git", "Git Commands", ["git", "vcs", "cli"], [{"action": "Rebase interactive", "keys": []}],
          category="VCS"),
]
IDX = cs.build_index(FIXTURE)


def top(q, **kw):
    r = cs.search(IDX, q, **kw)
    return r[0]["sheet"]["id"] if r else None


class Chords(unittest.TestCase):
    def test_canonical_order(self):
        self.assertEqual(cs.canon_chord("cmd+shift+o"), "shift+cmd+o")
        self.assertEqual(cs.glyph_chord("cmd+shift+o"), "⇧⌘O")
        self.assertEqual(cs.glyph_chord("opt+f7"), "⌥F7")

    def test_bad_chord(self):
        self.assertIsNone(cs.parse_chord("hyper+x"))


class Search(unittest.TestCase):
    def test_title_tag_alias(self):
        self.assertEqual(top("intellij"), "idea")
        self.assertEqual(top("jetbrains"), "idea")
        self.assertEqual(top("vcs"), "git")

    def test_prefix_and_typo(self):
        self.assertEqual(top("intell"), "idea")
        self.assertEqual(top("jetbrian"), "idea")
        self.assertEqual(top("rebse"), "git")

    def test_hyphenated_tag(self):
        self.assertEqual(top("versioncontrol"), "idea")
        self.assertEqual(top("version control"), "idea")

    def test_shortcut_lookup_all_spellings(self):
        for q in ("cmd shift o", "⇧⌘O", "command+shift+o", "Cmd-Shift-O"):
            r = cs.search(IDX, q)
            self.assertEqual(r[0]["entries"][0]["action"], "Go to file", q)

    def test_symbol_key(self):
        r = cs.search(IDX, "cmd ,")
        self.assertEqual(r[0]["entries"][0]["action"], "Open settings")

    def test_double_shift(self):
        r = cs.search(IDX, "shift shift")
        self.assertEqual(r[0]["entries"][0]["action"], "Search everywhere")

    def test_sheet_plus_entry_terms(self):
        r = cs.search(IDX, "intellij rename")
        self.assertEqual(r[0]["entries"][0]["action"], "Rename")

    def test_no_match_and_filters(self):
        self.assertEqual(cs.search(IDX, "zzzzqq"), [])
        self.assertEqual(cs.search(IDX, "git", tag="vcs")[0]["sheet"]["id"], "git")
        self.assertEqual(cs.search(IDX, "rename", category="VCS"), [])

    def test_empty_query_lists_all(self):
        self.assertEqual(len(cs.search(IDX, "")), 2)


class RealRepo(unittest.TestCase):
    def test_all_sheets_valid(self):
        errors, _ = cs.validate(cs.load_sheets())
        self.assertEqual(errors, [])

    def test_db_matches_sheets(self):
        lines = (ROOT / "db" / "cheatsheets.jsonl").read_text(encoding="utf-8").splitlines()
        self.assertEqual([json.loads(l)["id"] for l in lines], [s["id"] for s in cs.load_sheets()])


if __name__ == "__main__":
    unittest.main()
