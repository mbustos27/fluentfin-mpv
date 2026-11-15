"""Import cost of what bridge mode loads, from `python -X importtime`.

Run the WHOLE script off-screen (targets that reach player.py start mpv):

    python hidden_run.py --log out.txt --env PATH=<mpv dir>;%PATH% -- ^
        python bench_import.py --shim-root <checkout> --runs 5 --json imp.json

Each target is imported in a fresh interpreter with argv neutralised and
`--config <scratch>` (STARTHERE: importing reaches args.get_args()). A child
that imported player.py kills the mpv it started before exiting.

Reports, per target, the median over runs (first run discarded: .pyc warm-up):
  * wall ms of the import statement (perf_counter around it)
  * importtime cumulative ms of the target
  * the self ms of jellyfin_mpv_shim.player (= module body, which ENDS by
    constructing PlayerManager, i.e. spawning mpv) when it was imported
  * cumulative ms of notable packages pulled in (first appearance)
"""

import argparse
import json
import os
import re
import statistics
import subprocess
import sys
import tempfile

TARGETS = {
    "conf": "import jellyfin_mpv_shim.conf",
    "media": "import jellyfin_mpv_shim.media",
    "clients": "import jellyfin_mpv_shim.clients",
    "player (constructs mpv)": "import jellyfin_mpv_shim.player",
    "bridge set: player+event_handler+timeline+action_thread": (
        "import jellyfin_mpv_shim.event_handler, jellyfin_mpv_shim.timeline, "
        "jellyfin_mpv_shim.action_thread"),
    "sync.manager (lazy, first progress tick)": "import jellyfin_mpv_shim.sync.manager",
    "full app: mpv_shim module": "import jellyfin_mpv_shim.mpv_shim",
    "NOT needed: mpvtk_browser.ui": "import jellyfin_mpv_shim.mpvtk_browser.ui",
    "NOT needed: tray": "import jellyfin_mpv_shim.tray",
}

NOTABLE = ["PIL", "PIL.Image", "PIL.ImageFont", "requests", "urllib3",
           "jellyfin_apiclient_python", "python_mpv_jsonipc", "mpv",
           "websocket", "sqlite3", "ssl", "pystray", "pypresence",
           "webbrowser", "jellyfin_mpv_shim.menu",
           "jellyfin_mpv_shim.video_profile", "jellyfin_mpv_shim.syncplay",
           "jellyfin_mpv_shim.clients", "jellyfin_mpv_shim.osc_bridge",
           "jellyfin_mpv_shim.update_check", "jellyfin_mpv_shim.bulk_subtitle",
           "jellyfin_mpv_shim.svp_integration", "jellyfin_mpv_shim.trickplay",
           "jellyfin_mpv_shim.bifdecode", "jellyfin_mpv_shim.mpvtk",
           "jellyfin_mpv_shim.mpvtk_browser", "jellyfin_mpv_shim.sync",
           "jellyfin_mpv_shim.users", "jellyfin_mpv_shim.i18n",
           "jellyfin_mpv_shim.conf", "jellyfin_mpv_shim.media",
           "jellyfin_mpv_shim.utils", "gettext", "uuid", "multiprocessing"]

CHILD = r"""
import sys, time, os
sys.argv = [sys.argv[0], "--config", CFG]
sys.path.insert(0, ROOT)
t = time.perf_counter()
STMT
w = (time.perf_counter() - t) * 1000
pm = sys.modules.get("jellyfin_mpv_shim.player")
loaded = sorted(m for m in sys.modules)
print("JMSWALL %.2f" % w)
print("JMSMODS %d" % len(loaded))
print("JMSHAS " + " ".join(m for m in ("PIL", "PIL.ImageFont", "pystray",
      "jellyfin_mpv_shim.mpvtk", "jellyfin_mpv_shim.mpvtk_browser",
      "sqlite3", "pypresence", "websocket", "mpv", "python_mpv_jsonipc",
      "jellyfin_mpv_shim.sync.manager", "tkinter") if m in sys.modules))
sys.stdout.flush()
if pm is not None:
    try:
        pm.playerManager._player.mpv_process.process.kill()
    except Exception:
        pass
os._exit(0)
"""

LINE = re.compile(r"import time:\s+(\d+)\s+\|\s+(\d+)\s+\|(\s*)(\S+)")


def run_one(shim_root, cfg, stmt):
    code = (CHILD.replace("CFG", repr(cfg)).replace("ROOT", repr(shim_root))
            .replace("STMT", stmt))
    p = subprocess.run([sys.executable, "-X", "importtime", "-c", code],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=tempfile.gettempdir())
    cum, self_ = {}, {}
    for line in p.stderr.splitlines():
        m = LINE.match(line)
        if m:
            name = m.group(4)
            if name not in cum:
                cum[name] = int(m.group(2)) / 1000
                self_[name] = int(m.group(1)) / 1000
    wall = mods = None
    has = ""
    for line in p.stdout.splitlines():
        if line.startswith("JMSWALL"):
            wall = float(line.split()[1])
        elif line.startswith("JMSMODS"):
            mods = int(line.split()[1])
        elif line.startswith("JMSHAS"):
            has = line[7:].strip()
    return wall, mods, has, cum, self_


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shim-root", required=True)
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--json")
    a = ap.parse_args()
    cfg = tempfile.mkdtemp(prefix="jmsbench-imp-")
    out = {}
    for label, stmt in TARGETS.items():
        rows = [run_one(a.shim_root, cfg, stmt) for _ in range(a.runs + 1)][1:]
        walls = [r[0] for r in rows if r[0] is not None]
        target_mod = stmt.split()[1].rstrip(",")
        cums = [r[3].get(target_mod) for r in rows if r[3].get(target_mod)]
        pself = [r[4].get("jellyfin_mpv_shim.player") for r in rows
                 if r[4].get("jellyfin_mpv_shim.player") is not None]
        notable = {}
        for n in NOTABLE:
            v = [r[3][n] for r in rows if n in r[3]]
            if v:
                notable[n] = round(statistics.median(v), 1)
        out[label] = {
            "wall_ms": round(statistics.median(walls), 1) if walls else None,
            "importtime_cum_ms": round(statistics.median(cums), 1)
            if cums else None,
            "player_self_ms (mpv construct)": round(statistics.median(pself), 1)
            if pself else None,
            "modules_loaded": rows[-1][1],
            "heavy_present": rows[-1][2],
            "notable_cum_ms": notable,
        }
        print(label, json.dumps(out[label]), flush=True)
    if a.json:
        with open(a.json, "w") as f:
            json.dump(out, f, indent=2)


if __name__ == "__main__":
    main()
