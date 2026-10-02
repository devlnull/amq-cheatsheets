#!/usr/bin/env python3
"""cs - amq-cheatsheets: a text database, an inverted index and instant search.

    cs search <query>     search titles, tags, aliases, shortcuts (typo tolerant)
    cs open <query|id>    open the best match's image (Preview)
    cs show <query|id>    print a sheet in the terminal
    cs list | tags        browse
    cs new <title>        scaffold a new sheet
    cs build              validate, regenerate db/, index, markdown, Raycast, Spotlight
    cs validate           lint every sheet.json

Source of truth: sheets/<id>/sheet.json.  Everything else is generated.
Pure standard library; works with the macOS system python3 (3.9+).
"""
from __future__ import annotations

import argparse
import bisect
import json
import os
import re
import sys
import unicodedata
from collections import defaultdict
from pathlib import Path

ROOT = Path(os.environ.get("CS_ROOT") or Path(__file__).resolve().parent)
SHEETS = ROOT / "sheets"
DB_DIR = ROOT / "db"
DB_FILE = DB_DIR / "cheatsheets.jsonl"
INDEX_FILE = DB_DIR / "index.json"
# NOTE: subprocess/shutil/plistlib/datetime are imported lazily - search startup time matters.
RAYCAST_DIR = ROOT / "raycast"
INDEX_VERSION = 4

# --------------------------------------------------------------------------
# Keys & chords
# --------------------------------------------------------------------------
MODS = ["ctrl", "opt", "shift", "cmd"]  # canonical (macOS display) order
GLYPH = {
    "cmd": "⌘", "shift": "⇧", "opt": "⌥", "ctrl": "⌃",
    "enter": "↵", "backspace": "⌫", "up": "↑", "down": "↓", "left": "←", "right": "→",
    "tab": "⇥", "esc": "⎋", "space": "Space", "plus": "+",
}
GLYPH_WORDS = {  # glyph -> word, used when parsing queries / text
    "⌘": " cmd ", "⇧": " shift ", "⌥": " opt ", "⌃": " ctrl ", "↵": " enter ", "⏎": " enter ",
    "⌫": " backspace ", "⎋": " esc ", "⇥": " tab ", "←": " left ", "→": " right ",
    "↑": " up ", "↓": " down ",
}
SYMBOL_NAMES = {
    "[": "lbracket", "]": "rbracket", ",": "comma", ".": "period", "/": "slash", "\\": "backslash",
    "`": "backtick", ";": "semicolon", "'": "quote", "-": "minus", "=": "equals", "+": "plus",
}
# index-time synonyms: a key named X is also findable as Y
SYNONYMS = {
    "cmd": ["command"], "opt": ["option", "alt"], "ctrl": ["control"], "esc": ["escape"],
    "enter": ["return"], "backspace": ["delete"],
    "up": ["arrow"], "down": ["arrow"], "left": ["arrow"], "right": ["arrow"],
    "lbracket": ["bracket"], "rbracket": ["bracket"], "backtick": ["grave"],
    "click": ["mouse"],
}
# query-time: spoken form -> canonical key word (only for chord detection)
CHORD_ALIASES = {"command": "cmd", "option": "opt", "alt": "opt", "control": "ctrl",
                 "return": "enter", "escape": "esc", "delete": "backspace"}


def parse_chord(chord):
    parts = [p for p in chord.lower().strip().split("+") if p] if chord != "+" else ["plus"]
    if chord.endswith("++"):
        parts = [p for p in chord[:-2].lower().split("+") if p] + ["plus"]
    if not parts:
        return None
    mods, key = parts[:-1], parts[-1]
    if any(m not in MODS for m in mods):
        return None
    mods.sort(key=MODS.index)
    return mods, key


def canon_chord(chord):
    p = parse_chord(chord)
    return "+".join(p[0] + [p[1]]) if p else chord


def glyph_chord(chord):
    p = parse_chord(chord)
    if not p:
        return chord
    mods, key = p
    k = GLYPH.get(key, key.upper() if len(key) <= 3 else key)
    return "".join(GLYPH[m] for m in mods) + k


def glyph_keys(keys):
    return " / ".join(glyph_chord(k) for k in keys)


# --------------------------------------------------------------------------
# Text normalisation
# --------------------------------------------------------------------------
def _fold(s):
    s = unicodedata.normalize("NFKD", s)
    return "".join(c for c in s if not unicodedata.combining(c)).lower()


NOSTEM = {"minus", "plus", "equals"}


def stem(t):
    if t in NOSTEM:
        return t
    return t[:-1] if len(t) > 3 and t.endswith("s") and not t.endswith("ss") else t


def text_tokens(s):
    s = _fold(s)
    for g, w in GLYPH_WORDS.items():
        s = s.replace(g, w)
    return [stem(t) for t in re.findall(r"[a-z0-9]+", s)]


def key_terms(chord):
    p = parse_chord(chord)
    if not p:
        return text_tokens(chord)
    mods, key = p
    names = list(mods) + [SYMBOL_NAMES.get(key, key)]
    out = []
    for n in names:
        out.append(n)
        out.extend(SYNONYMS.get(n, []))
    return [stem(t) for t in out]


def query_tokens(q):
    """-> (stemmed tokens, chord-canonical-or-None)"""
    s = _fold(q)
    for g, w in GLYPH_WORDS.items():
        s = s.replace(g, w)
    raw = re.findall(r"[a-z0-9]+|[^\sa-z0-9]", s)
    has_mod = any(CHORD_ALIASES.get(t, t) in MODS for t in raw)
    words = []
    for i, t in enumerate(raw):
        if re.match(r"[a-z0-9]", t):
            words.append(t)
        elif has_mod and t in SYMBOL_NAMES:
            # "-" / "+" are separators ("cmd-shift-o") unless they are the key itself ("cmd -")
            if t in "-+" and not (i == len(raw) - 1 and words and words[-1] in MODS):
                continue
            words.append(SYMBOL_NAMES[t])
    chord = None
    canon = [CHORD_ALIASES.get(w, w) for w in words]
    if len(canon) >= 2 and all(c in MODS for c in canon[:-1]):
        chord = "+".join(sorted(canon[:-1], key=MODS.index) + [canon[-1]])
    return [stem(w) for w in words], chord


# --------------------------------------------------------------------------
# Loading & validation
# --------------------------------------------------------------------------
KEBAB = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")


def load_sheets():
    out = []
    for f in sorted(SHEETS.glob("*/sheet.json")):
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            raise SystemExit("%s: invalid JSON: %s" % (f.relative_to(ROOT), e))
        data["_dir"] = f.parent
        out.append(data)
    return out


def validate(sheets):
    errors, warnings = [], []
    seen = set()
    for s in sheets:
        d = s["_dir"]
        w = d.name

        def err(m):
            errors.append("%s: %s" % (w, m))

        def warn(m):
            warnings.append("%s: %s" % (w, m))

        for field in ("id", "title", "category", "tags", "sections"):
            if not s.get(field):
                err("missing '%s'" % field)
        if s.get("id") != w:
            err("id %r must equal folder name %r" % (s.get("id"), w))
        if s.get("id") in seen:
            err("duplicate id")
        seen.add(s.get("id"))
        tags = s.get("tags") or []
        for t in tags:
            if not KEBAB.match(t):
                err("tag %r must be lowercase kebab-case" % t)
        if len(set(tags)) != len(tags):
            err("duplicate tags")
        if len(tags) < 3:
            warn("fewer than 3 tags - harder to find")
        if not s.get("description"):
            warn("no description")
        img = s.get("image")
        if img and not (d / img).is_file():
            err("image %r not found" % img)
        if not img:
            warn("no image (text-only sheet)")
        n = 0
        for sec in s.get("sections") or []:
            if not sec.get("name"):
                err("section without name")
            for e in sec.get("entries") or []:
                n += 1
                if not e.get("action"):
                    err("entry without action in section %r" % sec.get("name"))
                for k in e.get("keys") or []:
                    if not parse_chord(k):
                        err("bad chord %r (%s)" % (k, e.get("action")))
        if n == 0:
            warn("no entries - only title/tags are searchable")
    return errors, warnings


# --------------------------------------------------------------------------
# Index
# --------------------------------------------------------------------------
W_TITLE, W_TAG, W_ALIAS, W_CAT, W_DESC, W_SECTION = 10, 7, 6, 5, 2, 3
W_E_ACTION, W_E_KEYS, W_E_SECTION, W_E_NOTE, W_CHORD = 5, 6, 2, 2, 8


def build_index(sheets):
    metas, entries = [], []
    st = defaultdict(dict)  # term -> {sheet_idx: weight}
    et = defaultdict(dict)  # term -> {entry_idx: weight}

    def put(d, term, key, w):
        if term and w > d[term].get(key, 0):
            d[term][key] = w

    for si, s in enumerate(sheets):
        metas.append({
            "id": s["id"], "title": s["title"], "category": s.get("category", ""),
            "tags": s.get("tags", []), "description": s.get("description", ""),
            "image": (str(Path("sheets") / s["id"] / s["image"]) if s.get("image")
                      else str(Path("sheets") / s["id"] / (s["id"] + ".md"))),
            "md": str(Path("sheets") / s["id"] / (s["id"] + ".md")),
        })
        fields = [(s["title"], W_TITLE), (s.get("category", ""), W_CAT),
                  (s.get("description", ""), W_DESC), (s.get("platform", ""), W_DESC)]
        fields += [(t, W_TAG) for t in s.get("tags", [])]
        fields += [(a, W_ALIAS) for a in s.get("aliases", [])]
        fields += [(sec["name"], W_SECTION) for sec in s.get("sections", [])]
        for text, w in fields:
            for tok in text_tokens(text):
                put(st, tok, si, w)
        for t in s.get("tags", []):  # "version-control" also findable as "versioncontrol"
            if "-" in t:
                put(st, t.replace("-", ""), si, W_TAG)
        for sec in s.get("sections", []):
            for e in sec.get("entries", []):
                ei = len(entries)
                keys = [canon_chord(k) for k in e.get("keys", [])]
                entries.append([si, sec["name"], e["action"], keys, e.get("note", ""), e.get("check", ""),
                                e.get("command", "")])
                for tok in text_tokens(e["action"]):
                    put(et, tok, ei, W_E_ACTION)
                for tok in text_tokens(sec["name"]):
                    put(et, tok, ei, W_E_SECTION)
                for tok in text_tokens(e.get("note", "")):
                    put(et, tok, ei, W_E_NOTE)
                for tok in text_tokens(e.get("command", "")):
                    put(et, tok, ei, W_E_KEYS)
                for k in keys:
                    for tok in key_terms(k):
                        put(et, tok, ei, W_E_KEYS)
                    put(et, "chord:" + k, ei, W_CHORD)
    vocab = sorted({t for t in list(st) + list(et) if not t.startswith("chord:")})
    return {
        "v": INDEX_VERSION, "sheets": metas, "entries": entries, "vocab": vocab,
        "st": {t: sorted(d.items()) for t, d in st.items()},
        "et": {t: sorted(d.items()) for t, d in et.items()},
    }


def _edit_within(a, b, limit):
    """True if Damerau-Levenshtein(a, b) <= limit (cheap, early exit)."""
    if abs(len(a) - len(b)) > limit:
        return False
    prev2, prev = None, list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        cur = [i] + [0] * len(b)
        for j in range(1, len(b) + 1):
            c = 0 if a[i - 1] == b[j - 1] else 1
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + c)
            if i > 1 and j > 1 and a[i - 1] == b[j - 2] and a[i - 2] == b[j - 1]:
                cur[j] = min(cur[j], prev2[j - 2] + 1)
        if min(cur) > limit:
            return False
        prev2, prev = prev, cur
    return prev[-1] <= limit


def _expand(term, vocab):
    """term -> [(vocab_term, quality)]: exact 1.0, prefix 0.6, fuzzy 0.35"""
    out = []
    i = bisect.bisect_left(vocab, term)
    if i < len(vocab) and vocab[i] == term:
        out.append((term, 1.0))
    if len(term) >= 2:
        j, n = i, 0
        while j < len(vocab) and vocab[j].startswith(term) and n < 300:
            if vocab[j] != term:
                out.append((vocab[j], 0.6))
            j += 1
            n += 1
    if not out and len(term) >= 4:
        lim = 1 if len(term) < 8 else 2
        out = [(v, 0.35) for v in vocab if v[0] == term[0] and _edit_within(term, v, lim)]
        if not out:  # first-letter typo
            out = [(v, 0.3) for v in vocab if _edit_within(term, v, lim)]
    return out


def search(idx, query, limit=10, tag=None, category=None, entries_per_sheet=5):
    sheets, entries, vocab = idx["sheets"], idx["entries"], idx["vocab"]
    st, et = idx["st"], idx["et"]
    allowed = {i for i, m in enumerate(sheets)
               if (not tag or tag in m["tags"])
               and (not category or m["category"].lower() == category.lower())}
    toks, chord = query_tokens(query)
    if not toks:
        order = sorted(allowed, key=lambda i: sheets[i]["title"].lower())
        return [{"sheet": sheets[i], "score": 0.0, "entries": []} for i in order[:limit]]

    smaps, emaps = [], []
    for t in toks:
        sm, em = {}, {}
        for mt, q in _expand(t, vocab):
            for si, w in st.get(mt, ()):
                if w * q > sm.get(si, 0):
                    sm[si] = w * q
            for ei, w in et.get(mt, ()):
                if w * q > em.get(ei, 0):
                    em[ei] = w * q
        smaps.append(sm)
        emaps.append(em)
    chord_hits = {ei for ei, _ in et.get("chord:" + chord, ())} if chord else set()

    # sheet-level: every token must match the sheet itself or one of its entries
    per_tok = []
    for sm, em in zip(smaps, emaps):
        best = dict(sm)
        for ei, v in em.items():
            si = entries[ei][0]
            if v * 0.8 > best.get(si, 0):
                best[si] = v * 0.8
        per_tok.append(best)
    cand = set(per_tok[0])
    for b in per_tok[1:]:
        cand &= set(b)
    cand &= allowed
    if not cand:
        return []

    # entry-level: every token must match the entry (or its sheet at half weight)
    hits = defaultdict(list)
    ecand = set()
    for em in emaps:
        ecand |= set(em)
    ecand |= chord_hits
    for ei in ecand:
        si = entries[ei][0]
        if si not in cand:
            continue
        score, direct = 0.0, False
        for sm, em in zip(smaps, emaps):
            v = em.get(ei, 0)
            if v:
                direct = True
            v = max(v, sm.get(si, 0) * 0.5)
            if not v:
                score = 0
                break
            score += v
        if score and direct:
            if ei in chord_hits:
                score += 12
            hits[si].append((score, ei))

    results = []
    for si in cand:
        sc = sum(b[si] for b in per_tok)
        hs = sorted(hits.get(si, ()), reverse=True)
        if hs:
            sc += 0.25 * hs[0][0]
        results.append({
            "sheet": sheets[si], "score": round(sc, 2),
            "entries": [{"section": entries[ei][1], "action": entries[ei][2], "keys": entries[ei][3],
                         "keys_display": glyph_keys(entries[ei][3]) or entries[ei][6], "command": entries[ei][6], "note": entries[ei][4], "check": entries[ei][5],
                         "score": round(s, 2)}
                        for s, ei in hs[:entries_per_sheet]],
        })
    results.sort(key=lambda r: (-r["score"], r["sheet"]["title"].lower()))
    return results[:limit]


# --------------------------------------------------------------------------
# Build (db, index, markdown, README, Raycast, Spotlight)
# --------------------------------------------------------------------------
def write_if_changed(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and path.read_text(encoding="utf-8") == text:
        return False
    path.write_text(text, encoding="utf-8")
    return True


def sheet_markdown(s):
    L = ["---", "title: %s" % json.dumps(s["title"], ensure_ascii=False),
         "category: %s" % s.get("category", ""),
         "tags: [%s]" % ", ".join(s.get("tags", [])), "---", "", "# %s" % s["title"], ""]
    if s.get("description"):
        L += ["> %s" % s["description"], ""]
    L += ["**Category:** %s  " % s.get("category", ""), "**Platform:** %s  " % s.get("platform", "-"),
          "**Tags:** %s" % " ".join("`%s`" % t for t in s.get("tags", [])), ""]
    if s.get("image"):
        L += ["![%s](%s)" % (s["title"], s["image"].replace(" ", "%20")), ""]
    for sec in s.get("sections", []):
        L += ["## %s" % sec["name"], "", "| Action | Shortcut / Command | Note |", "|---|---|---|"]
        for e in sec.get("entries", []):
            keys = " / ".join("`%s`" % glyph_chord(k) for k in e.get("keys", []))
            if e.get("command"):
                keys = "`%s`" % e["command"].replace("|", "\\|")
            note = e.get("note", "")
            if e.get("check"):
                note = (note + " " if note else "") + "⚠️ " + e["check"]
            L.append("| %s | %s | %s |" % (e["action"], keys, note.replace("|", "\\|")))
        L.append("")
    if s.get("tips"):
        L += ["## Tips", ""] + ["- %s" % t for t in s["tips"]] + [""]
    if s.get("source"):
        L += ["_Source: %s_" % s["source"], ""]
    L.append("<!-- generated by `cs build` from sheet.json - do not edit -->")
    return "\n".join(L) + "\n"


def readme_table(sheets):
    rows = ["| Cheatsheet | Category | Tags | Entries |", "|---|---|---|---|"]
    for s in sorted(sheets, key=lambda x: x["title"].lower()):
        n = sum(len(sec.get("entries", [])) for sec in s.get("sections", []))
        link = "sheets/%s/%s" % (s["id"], s["image"]) if s.get("image") else "sheets/%s/%s.md" % (s["id"], s["id"])
        rows.append("| [%s](%s) | %s | %s | %d |" % (
            s["title"], link.replace(" ", "%20"), s.get("category", ""),
            " ".join("`%s`" % t for t in s.get("tags", [])[:8]), n))
    return "\n".join(rows)


RAYCAST_HEADER = """#!/bin/bash
# Generated by `cs build` - do not edit.
# @raycast.schemaVersion 1
# @raycast.title {title}
# @raycast.mode {mode}
# @raycast.packageName Cheatsheets
# @raycast.icon {icon}
{extra}
export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:$PATH"
DIR="$(cd "$(dirname "${{BASH_SOURCE[0]}}")/{up}" && pwd)"
"""


def write_raycast(sheets):
    changed = False
    search = RAYCAST_HEADER.format(
        title="Search Cheatsheets", mode="fullOutput", icon="🔎", up="..",
        extra='# @raycast.description Search cheatsheets by title, tag, or shortcut (e.g. "rename", "cmd shift o")\n'
              '# @raycast.argument1 { "type": "text", "placeholder": "title, tag or shortcut" }') + \
        '"$DIR/cs" search --plain "$1"\n'
    open_ = RAYCAST_HEADER.format(
        title="Open Cheatsheet", mode="silent", icon="📒", up="..",
        extra='# @raycast.description Open the best-matching cheatsheet image\n'
              '# @raycast.argument1 { "type": "text", "placeholder": "title or tag" }') + \
        '"$DIR/cs" open "$1"\n'
    for name, body in (("search-cheatsheets.sh", search), ("open-cheatsheet.sh", open_)):
        changed |= write_if_changed(RAYCAST_DIR / name, body)
        os.chmod(RAYCAST_DIR / name, 0o755)
    keep = set()
    for s in sheets:
        name = "%s.sh" % s["id"]
        keep.add(name)
        desc = "Open the %s cheatsheet. Tags: %s" % (s["title"], ", ".join(s.get("tags", [])[:6]))
        body = RAYCAST_HEADER.format(
            title="%s Cheatsheet" % s["title"], mode="silent", icon="📒", up="../..",
            extra="# @raycast.description %s" % desc) + '"$DIR/cs" open --id %s\n' % s["id"]
        p = RAYCAST_DIR / "sheets" / name
        changed |= write_if_changed(p, body)
        os.chmod(p, 0o755)
    d = RAYCAST_DIR / "sheets"
    if d.is_dir():
        for f in d.glob("*.sh"):
            if f.name not in keep:
                f.unlink()
                changed = True
    return changed


def set_finder_metadata(sheet):
    """Finder tags + comment (searchable in Spotlight: `tag:intellij`, or just the words)."""
    import plistlib
    import shutil
    import subprocess
    if sys.platform != "darwin" or not shutil.which("xattr"):
        return
    d = sheet["_dir"]
    tags = [t for t in sheet.get("tags", [])]
    comment = "%s - %s" % (sheet["title"], " ".join(tags))
    tag_hex = plistlib.dumps(tags, fmt=plistlib.FMT_BINARY).hex()
    com_hex = plistlib.dumps(comment, fmt=plistlib.FMT_BINARY).hex()
    files = [d / (sheet["id"] + ".md")]
    if sheet.get("image"):
        files.append(d / sheet["image"])
    for f in files:
        if not f.exists():
            continue
        for attr, hx in (("com.apple.metadata:_kMDItemUserTags", tag_hex),
                         ("com.apple.metadata:kMDItemFinderComment", com_hex)):
            subprocess.run(["xattr", "-wx", attr, hx, str(f)], capture_output=True)
    if shutil.which("mdimport"):
        subprocess.run(["mdimport", str(d)], capture_output=True)


def build(quiet=False, spotlight=True):
    sheets = load_sheets()
    errors, warnings = validate(sheets)
    if errors:
        for e in errors:
            print("error:", e, file=sys.stderr)
        raise SystemExit(1)
    changed = []
    lines = []
    for s in sheets:
        rec = {k: v for k, v in s.items() if k != "_dir"}
        rec.pop("$schema", None)
        rec["image_path"] = "sheets/%s/%s" % (s["id"], s["image"]) if s.get("image") else None
        rec["entry_count"] = sum(len(sec.get("entries", [])) for sec in s.get("sections", []))
        lines.append(json.dumps(rec, ensure_ascii=False, sort_keys=True))
        if write_if_changed(s["_dir"] / (s["id"] + ".md"), sheet_markdown(s)):
            changed.append("%s.md" % s["id"])
    if write_if_changed(DB_FILE, "\n".join(lines) + ("\n" if lines else "")):
        changed.append("db/cheatsheets.jsonl")
    idx = build_index(sheets)
    if write_if_changed(INDEX_FILE, json.dumps(idx, ensure_ascii=False, separators=(",", ":"))):
        changed.append("db/index.json")
    if write_raycast(sheets):
        changed.append("raycast/*")
    readme = ROOT / "README.md"
    if readme.exists():
        txt = readme.read_text(encoding="utf-8")
        new = re.sub(r"(<!-- sheets:start -->\n).*?(<!-- sheets:end -->)",
                     lambda m: m.group(1) + readme_table(sheets) + "\n" + m.group(2), txt, flags=re.S)
        if new != txt:
            readme.write_text(new, encoding="utf-8")
            changed.append("README.md")
    if spotlight:
        for s in sheets:
            set_finder_metadata(s)
    if not quiet:
        n_e = sum(r["entry_count"] for r in map(json.loads, lines))
        print("built %d sheet(s), %d entries, %d index term(s)" % (len(sheets), n_e, len(idx["vocab"])))
        for w in warnings:
            print("warning:", w)
        if changed:
            print("updated:", ", ".join(changed) if len(changed) <= 6 else "%d files" % len(changed))
    return idx


def index_is_stale():
    if not INDEX_FILE.exists():
        return True
    m = INDEX_FILE.stat().st_mtime
    deps = list(SHEETS.glob("*/sheet.json")) + [Path(__file__).resolve()]
    return any(p.stat().st_mtime > m for p in deps) or \
        len(list(SHEETS.glob("*/sheet.json"))) != len(json.loads(INDEX_FILE.read_text())["sheets"])


def load_index():
    try:
        if not index_is_stale():
            idx = json.loads(INDEX_FILE.read_text(encoding="utf-8"))
            if idx.get("v") == INDEX_VERSION:
                return idx
    except (OSError, ValueError):
        pass
    return build(quiet=True, spotlight=False)


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------
class Style:
    def __init__(self, on):
        self.on = on

    def __call__(self, code, s):
        return "\033[%sm%s\033[0m" % (code, s) if self.on else s


def print_results(res, style, plain=False):
    if not res:
        print("no matches")
        return
    for i, r in enumerate(res, 1):
        m = r["sheet"]
        print("%s %s  %s  %s" % (style("1", "%d." % i), style("1;36", m["title"]),
                                 style("33", "[%s]" % m["category"]),
                                 style("2", " ".join("#" + t for t in m["tags"][:6]))))
        for e in r["entries"]:
            note = " (%s)" % e["note"] if e["note"] else ""
            print("     %s  %s%s  %s" % (style("1;32", e["keys_display"].ljust(12)), e["action"], note,
                                         style("2", "· " + e["section"])))
            if e["check"]:
                print("     %s  %s" % (" " * 12, style("33", "⚠ verify: " + e["check"])))
        print("     %s" % style("2", str(ROOT / m["image"])))
    if plain:
        print("\nTip: `Open Cheatsheet` in Raycast opens the top result.")


def resolve(idx, q, by_id=False):
    for m in idx["sheets"]:
        if m["id"] == q:
            return m
    if by_id:
        return None
    res = search(idx, q, limit=1)
    return res[0]["sheet"] if res else None


def cmd_search(a):
    q = " ".join(a.query)
    res = search(load_index(), q, limit=a.limit, tag=a.tag, category=a.category)
    if a.lines:  # id<TAB>label, consumed by the Spotlight launcher app
        for r in res:
            m = r["sheet"]
            print("%s\t%s  [%s]" % (m["id"], m["title"], m["category"]))
            for e in r["entries"]:
                print("%s\t      %s   %s" % (m["id"], e["keys_display"], e["action"]))
    elif a.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
    else:
        print_results(res, Style(sys.stdout.isatty() and not a.plain), plain=a.plain)
    return 0 if res else 1


def cmd_open(a):
    import subprocess
    idx = load_index()
    m = resolve(idx, " ".join(a.query) if a.query else (a.id or ""), by_id=bool(a.id))
    if not m:
        print("no matching cheatsheet", file=sys.stderr)
        return 1
    path = ROOT / (m["md"] if a.md else m["image"])
    if a.print:
        print(path)
    elif sys.platform == "darwin":
        subprocess.run(["open", "-R", str(path)] if a.reveal else ["open", str(path)])
    else:
        subprocess.run(["xdg-open", str(path)])
    return 0


def cmd_show(a):
    idx = load_index()
    m = resolve(idx, " ".join(a.query))
    if not m:
        print("no matching cheatsheet", file=sys.stderr)
        return 1
    s = next(x for x in load_sheets() if x["id"] == m["id"])
    st = Style(sys.stdout.isatty())
    print(st("1;36", s["title"]), st("33", "[%s]" % s["category"]))
    print(st("2", " ".join("#" + t for t in s["tags"])))
    for sec in s["sections"]:
        print("\n" + st("1", sec["name"]))
        for e in sec["entries"]:
            note = " (%s)" % e["note"] if e.get("note") else ""
            print("  %s  %s%s" % (st("1;32", (glyph_keys(e.get("keys", [])) or e.get("command", "")).ljust(14)), e["action"], note))
    for t in s.get("tips", []):
        print("\n" + st("2", "• " + t), end="")
    print()
    return 0


def cmd_list(a):
    for s in sorted(load_sheets(), key=lambda x: x["title"].lower()):
        n = sum(len(sec.get("entries", [])) for sec in s.get("sections", []))
        print("%-24s %-10s %3d  %s" % (s["id"], s.get("category", ""), n, s["title"]))
    return 0


def cmd_tags(a):
    c = defaultdict(int)
    for s in load_sheets():
        for t in s.get("tags", []):
            c[t] += 1
    for t, n in sorted(c.items(), key=lambda kv: (-kv[1], kv[0])):
        print("%3d  %s" % (n, t))
    return 0


def cmd_validate(a):
    errors, warnings = validate(load_sheets())
    for w in warnings:
        print("warning:", w)
    for e in errors:
        print("error:", e)
    print("%d error(s), %d warning(s)" % (len(errors), len(warnings)))
    return 1 if errors else 0


def cmd_build(a):
    build(spotlight=not a.no_spotlight)
    return 0


def cmd_app(a):
    """Build ~/Applications/Cheatsheets.app so Spotlight can launch a search dialog."""
    import subprocess
    import tempfile
    tpl = ROOT / "packaging" / "Cheatsheets.applescript"
    if not tpl.exists():
        tpl = ROOT / "tools" / "Cheatsheets.applescript"
    if sys.platform != "darwin" or not tpl.exists():
        print("launcher app: macOS only / template missing", file=sys.stderr)
        return 1
    dest = Path(a.dest).expanduser() if a.dest else Path.home() / "Applications"
    dest.mkdir(parents=True, exist_ok=True)
    app = dest / "Cheatsheets.app"
    if app.exists():
        import shutil
        shutil.rmtree(str(app))
    with tempfile.TemporaryDirectory() as tmp:
        src = Path(tmp) / "Cheatsheets.applescript"
        src.write_text(tpl.read_text(encoding="utf-8").replace("__CS__", str(Path(__file__).resolve())), encoding="utf-8")
        r = subprocess.run(["osacompile", "-o", str(app), str(src)], capture_output=True, text=True)
    if r.returncode:
        print(r.stderr, file=sys.stderr)
        return 1
    print("created %s" % app)
    return 0


def cmd_new(a):
    import shutil
    from datetime import date
    title = " ".join(a.title)
    sid = a.id or re.sub(r"[^a-z0-9]+", "-", _fold(title)).strip("-")
    d = SHEETS / sid
    if d.exists():
        print("sheet %r already exists" % sid, file=sys.stderr)
        return 1
    d.mkdir(parents=True)
    image = None
    if a.image:
        src = Path(a.image).expanduser()
        if not src.is_file():
            print("image not found: %s" % src, file=sys.stderr)
            shutil.rmtree(d)
            return 1
        image = "%s%s" % (sid, src.suffix.lower())
        (shutil.move if a.move else shutil.copy2)(str(src), str(d / image))
    tags = [t.strip().lower().replace(" ", "-") for t in (a.tags or "").split(",") if t.strip()]
    today = date.today().isoformat()
    sheet = {
        "$schema": "../../schema/sheet.schema.json", "id": sid, "title": title,
        "category": a.category, "tags": tags or ["todo"], "aliases": [],
        "description": "", "platform": a.platform,
    }
    if image:
        sheet["image"] = image
    sheet.update({"source": "", "created": today, "updated": today,
                  "sections": [{"name": "General", "entries": [{"action": "TODO", "keys": []}]}], "tips": []})
    (d / "sheet.json").write_text(json.dumps(sheet, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("created sheets/%s/sheet.json" % sid)
    print("next: fill in sections/entries (ask Claude Code to transcribe the image), then run `./cs build`")
    return 0


def _version():
    try:
        return (ROOT / "VERSION").read_text().strip()
    except OSError:
        return "dev"


def main(argv=None):
    p = argparse.ArgumentParser(prog="cs", description=__doc__.split("\n")[0])
    p.add_argument("--version", action="version", version="cs " + _version())
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("search", aliases=["s"], help="search sheets and shortcuts")
    s.add_argument("query", nargs="*")
    s.add_argument("-n", "--limit", type=int, default=10)
    s.add_argument("-t", "--tag")
    s.add_argument("-c", "--category")
    s.add_argument("--json", action="store_true")
    s.add_argument("--lines", action="store_true", help="id<TAB>label lines (for the launcher app)")
    s.add_argument("--plain", action="store_true", help="no colors (Raycast)")
    s.set_defaults(fn=cmd_search)

    o = sub.add_parser("open", aliases=["o"], help="open the best match")
    o.add_argument("query", nargs="*")
    o.add_argument("--id")
    o.add_argument("--md", action="store_true", help="open the Markdown version")
    o.add_argument("--reveal", action="store_true", help="reveal in Finder")
    o.add_argument("--print", action="store_true", help="print the path only")
    o.set_defaults(fn=cmd_open)

    sh = sub.add_parser("show", help="print a sheet in the terminal")
    sh.add_argument("query", nargs="+")
    sh.set_defaults(fn=cmd_show)

    sub.add_parser("list", aliases=["ls"], help="list sheets").set_defaults(fn=cmd_list)
    sub.add_parser("tags", help="tag counts").set_defaults(fn=cmd_tags)
    sub.add_parser("validate", help="lint all sheets").set_defaults(fn=cmd_validate)

    b = sub.add_parser("build", help="regenerate DB, index, markdown, Raycast, Spotlight metadata")
    b.add_argument("--no-spotlight", action="store_true")
    b.set_defaults(fn=cmd_build)

    ap_ = sub.add_parser("app", help="build the Spotlight launcher app (~/Applications/Cheatsheets.app)")
    ap_.add_argument("--dest", help="directory to create the app in")
    ap_.set_defaults(fn=cmd_app)

    n = sub.add_parser("new", help="scaffold a sheet")
    n.add_argument("title", nargs="+")
    n.add_argument("--id")
    n.add_argument("--image")
    n.add_argument("--move", action="store_true", help="move instead of copy the image")
    n.add_argument("--tags", help="comma separated")
    n.add_argument("--category", default="General")
    n.add_argument("--platform", default="macOS")
    n.set_defaults(fn=cmd_new)

    a = p.parse_args(argv)
    return a.fn(a)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except BrokenPipeError:
        sys.exit(0)
