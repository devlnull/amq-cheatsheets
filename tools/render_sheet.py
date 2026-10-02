#!/usr/bin/env python3
"""Render a sheet.json into a dark "cheat sheet" PNG (same look as the IntelliJ one).

    python3 tools/render_sheet.py git            # -> sheets/git/git.png
    python3 tools/render_sheet.py git --html     # keep/print the intermediate HTML path

Uses headless Google Chrome (or Edge). Options live in sheet.json: sections[].color/icon,
render.{subtitle,tagline,intro,logo,layout,footer_note,footer_sign}. Tips come from `tips`.
"""
import argparse
import html
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BROWSERS = ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            "/Applications/Chromium.app/Contents/MacOS/Chromium"]
W, H = 1024, 1536

LOGO_GIT = """<svg viewBox="0 0 100 100" width="104" height="104"><g transform="rotate(45 50 50)">
<rect x="14" y="14" width="72" height="72" rx="10" fill="#F05033"/></g>
<g stroke="#fff" stroke-width="5.5" stroke-linecap="round" fill="none">
<path d="M50 36 V64"/><path d="M50 50 L66 38"/></g>
<g fill="#fff"><circle cx="50" cy="35" r="7"/><circle cx="50" cy="65" r="7"/><circle cx="67" cy="37" r="7"/></g>
<g fill="#F05033"><circle cx="50" cy="35" r="2.6"/><circle cx="50" cy="65" r="2.6"/><circle cx="67" cy="37" r="2.6"/></g></svg>"""

CSS = """
*{box-sizing:border-box;margin:0;padding:0}
html,body{width:%(W)dpx;height:%(H)dpx;background:#0a1424;overflow:hidden}
body{font-family:-apple-system,"SF Pro Text","Helvetica Neue",Arial,sans-serif;color:#e8eefc;display:flex;flex-direction:column;
 background:radial-gradient(1200px 500px at 15%% 0%%,#12233f 0%%,#0a1424 60%%)}
header{height:172px;padding:24px 34px 0 36px;display:flex;align-items:flex-start;gap:22px;border-bottom:1px solid #1b2a44;position:relative}
.logo{flex:none;margin-top:-2px}
h1{font-size:58px;font-weight:800;letter-spacing:-1.5px;line-height:1.02;color:#fff}
.sub{font-size:36px;font-weight:700;letter-spacing:-.5px;line-height:1.15;
 background:linear-gradient(90deg,#5aa9ff,#7fb8ff);-webkit-background-clip:text;color:transparent}
.intro{margin-top:9px;font-size:17px;line-height:1.3;color:#6fb0ff}
.tag{position:absolute;right:40px;top:34px;text-align:right;font-family:"Snell Roundhand","Brush Script MT","Bradley Hand",cursive;
 font-size:33px;line-height:1.1;color:#4a9bff;transform:rotate(-6deg);font-weight:600}
.tag:after{content:"";display:block;margin:6px 0 0 auto;width:150px;height:3px;border-radius:3px;background:linear-gradient(90deg,transparent,#4a9bff)}
main{flex:1;padding:14px 18px 8px;display:flex;flex-direction:column;gap:12px;min-height:0}
.row{display:flex;gap:12px;min-height:0}
.card{flex:1;min-width:0;border:2px solid var(--c);border-radius:12px;background:#0d192b;display:flex;flex-direction:column;overflow:hidden;
 box-shadow:0 0 0 1px rgba(0,0,0,.4)}
.card h2{flex:none;padding:0 14px;height:42px;display:flex;align-items:center;gap:10px;font-size:19px;font-weight:700;color:#fff;
 background:linear-gradient(180deg,var(--c),color-mix(in srgb,var(--c) 82%%,#000))}
.card h2 .ic{font-size:21px;width:28px;text-align:center}
.card ul{list-style:none;flex:1;display:flex;flex-direction:column;margin:5px;background:#0b1424;border-radius:8px;padding:0 10px}
.card li{flex:1;display:flex;align-items:center;border-bottom:1px solid rgba(255,255,255,.07);gap:10px;min-height:0}
.card li:last-child{border-bottom:0}
.a{font-size:14px;color:#e9effb;line-height:1.2}
.n{color:#8ea0bd;font-size:12px}
code{font-family:Menlo,Monaco,monospace;font-variant-ligatures:none;font-feature-settings:"liga" 0,"calt" 0;font-size:11.8px;background:#1d2a41;border:1px solid #2c3d5a;color:#f1f5ff;
 padding:3px 8px;border-radius:6px;white-space:nowrap}
code i{font-style:normal;color:var(--c);filter:brightness(1.35)}
.wide li{justify-content:space-between}
.wide .a{flex:1}
.stack li{flex-direction:column;align-items:flex-start;justify-content:center;gap:2px}
.stack .a{font-size:12.6px;line-height:1.1}
.stack code{font-size:10.8px;line-height:1.25;padding:1px 6px}
footer{height:98px;margin:0 18px 12px;border-top:1px solid #1b2a44;display:flex;align-items:center;padding:6px 14px 0;gap:22px}
.pt{display:flex;align-items:center;gap:10px;color:#ffb020;font-weight:700;font-size:21px;flex:none}
.pt .e{font-size:34px}
.tips{font-size:12.4px;line-height:1.55;color:#c9d5ea;flex:1.45}
.tips li{list-style:none}.tips li:before{content:"•";color:#7d8ba3;margin-right:7px}
.vr{width:1px;align-self:stretch;margin:10px 0;background:#223452}
.rem{display:flex;gap:12px;flex:1;align-items:flex-start;padding-top:8px}
.rem .e{font-size:30px}.rem b{color:#6fb0ff;font-size:16px;display:block;margin-bottom:3px}
.rem p{font-size:12.4px;color:#c9d5ea;line-height:1.45}
.sign{font-family:"Snell Roundhand","Brush Script MT","Bradley Hand",cursive;font-size:26px;line-height:1.1;color:#4a9bff;transform:rotate(-7deg);text-align:center;flex:none;font-weight:600}
"""


def esc(s):
    return html.escape(s, quote=False)


def code_html(cmd):
    return "<code>%s</code>" % re.sub(r"(&lt;.+?&gt;)", r"<i>\1</i>", esc(cmd))


def render_html(sheet):
    r = sheet.get("render", {})
    secs = sheet["sections"]
    layout = r.get("layout") or [len(secs)]
    rows, i = [], 0
    for n in layout:
        rows.append(secs[i:i + n])
        i += n
    if i < len(secs):
        rows.append(secs[i:])
    out = []
    for row in rows:
        stacked = len(row) >= 3
        weight = max(len(s["entries"]) for s in row) * (35 if stacked else 28) + 52
        cards = []
        for s in row:
            lis = []
            for e in s["entries"]:
                note = ' <span class="n">(%s)</span>' % esc(e["note"]) if e.get("note") else ""
                if e.get("command"):
                    right = code_html(e["command"])
                else:
                    right = "".join("<code>%s</code>" % esc(k) for k in e.get("keys", []))
                lis.append('<li><span class="a">%s%s</span>%s</li>' % (esc(e["action"]), note, right))
            cards.append('<section class="card %s" style="--c:%s"><h2><span class="ic">%s</span>%s</h2><ul>%s</ul></section>'
                         % ("stack" if stacked else "wide", s.get("color", "#1e7bf0"), s.get("icon", "•"),
                            esc(s["name"]), "".join(lis)))
        out.append('<div class="row" style="flex:%d 1 0">%s</div>' % (weight, "".join(cards)))
    tips = "".join("<li>%s</li>" % esc(t) for t in (r.get("footer_tips") or sheet.get("tips", []))[:3])
    br = lambda t: "<br>".join(esc(x) for x in t.split("|"))
    logo = LOGO_GIT if r.get("logo") == "git" else ""
    title_main = sheet["title"].split(" ")[0]
    return """<!doctype html><meta charset="utf-8"><style>%s</style>
<header><div class="logo">%s</div><div><h1>%s</h1><div class="sub">%s</div><div class="intro">%s</div></div>
<div class="tag">%s</div></header>
<main>%s</main>
<footer><div class="pt"><span class="e">💡</span>Pro Tips</div><ul class="tips">%s</ul><div class="vr"></div>
<div class="rem"><span class="e">🎯</span><div><b>Remember:</b><p>%s</p></div></div><div class="sign">%s</div></footer>""" % (
        CSS % {"W": W, "H": H}, logo, esc(title_main), esc(r.get("subtitle", "Cheat Sheet")), br(r.get("intro", "")),
        br(r.get("tagline", "")), "".join(out), tips, br(r.get("footer_note", "")), br(r.get("footer_sign", "")))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("id")
    ap.add_argument("--html", action="store_true", help="also keep the HTML next to the PNG as <id>.render.html")
    ap.add_argument("--scale", type=float, default=2.0)
    a = ap.parse_args()
    d = ROOT / "sheets" / a.id
    sheet = json.loads((d / "sheet.json").read_text(encoding="utf-8"))
    browser = next((b for b in BROWSERS if Path(b).exists()), None)
    if not browser:
        sys.exit("Chrome/Edge not found")
    png = d / (sheet.get("image") or a.id + ".png")
    with tempfile.TemporaryDirectory() as tmp:
        f = Path(tmp) / "sheet.html"
        f.write_text(render_html(sheet), encoding="utf-8")
        if a.html:
            (d / (a.id + ".render.html")).write_text(f.read_text(encoding="utf-8"), encoding="utf-8")
        subprocess.run([browser, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-sandbox",
                        "--force-device-scale-factor=%s" % a.scale, "--window-size=%d,%d" % (W, H),
                        "--virtual-time-budget=2000", "--screenshot=%s" % png, f.as_uri()],
                       check=True, capture_output=True)
    print("wrote", png.relative_to(ROOT))


if __name__ == "__main__":
    main()
