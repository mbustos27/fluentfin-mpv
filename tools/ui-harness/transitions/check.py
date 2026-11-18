"""Off-screen checks of what happens between episodes, against the real backend and the UI harness's fake server
(tools/ui-harness/fakeserver): what the server is told, judged from the requests it got. Fake server, fake token, no
password anywhere (Quick Connect, approved by the harness).

    python check.py --media <fakeserver media dir> --out <dir> [--python <backend venv python>]

Run it on a private desktop (mpv opens a window):

    backend\\.venv\\Scripts\\python.exe -I tools\\backend-bench\\hidden_run.py --log <dir>\\run.log ^
        --env APPDATA=<dir>\\a --env LOCALAPPDATA=<dir>\\l -- backend\\.venv\\Scripts\\python.exe ^
        tools\\ui-harness\\transitions\\check.py --media <media> --out <dir>

Checks (1.2.0, the review of wip/114-player):
- credits: Next 10 s into end credits that start at 80 % (where the app's countdown moves on) leaves the episode
  played on the server (Jellyfin marks played only from 90 %).
- double next: a second `next` while the first is still opening (the app's Play now and its countdown a moment
  apart) is refused, and exactly one episode is moved to.
- volume: the volume set in an episode's last seconds is the next one's.
Exit status 0 when every check passed.
"""
import argparse
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
TICKS = 10_000_000

ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument("--media", required=True, help="the fake server's media folder (media.json, test.mkv)")
ap.add_argument("--out", required=True)
ap.add_argument("--python", default=sys.executable, help="the backend's python")
a = ap.parse_args()
os.makedirs(a.out, exist_ok=True)
T0 = time.time()
results = []


def say(*parts):
    print("[%6.2f]" % (time.time() - T0), *parts, flush=True)


def check(name, ok, detail=""):
    results.append((name, bool(ok)))
    say("PASS" if ok else "FAIL", name, detail)


srv_out = os.path.join(a.out, "server")
os.makedirs(srv_out, exist_ok=True)
ready = os.path.join(srv_out, "ready.json")
if os.path.exists(ready):
    os.remove(ready)
srv = subprocess.Popen([a.python, "-B", os.path.join(REPO, "tools", "ui-harness", "fakeserver", "server.py"),
                        "--out", srv_out, "--media", a.media, "--ready-file", ready],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
procs = [srv]
try:
    for _ in range(300):
        if os.path.exists(ready):
            break
        time.sleep(0.1)
    with open(ready, encoding="utf-8") as fh:
        url = json.load(fh)["url"]
    AUTH = 'MediaBrowser Client="TransitionCheck", Device="check", DeviceId="transition-check", Version="1.2.0"'

    def call(method, path, body=None, token=None):
        req = urllib.request.Request(url + path, method=method,
                                     data=None if body is None else json.dumps(body).encode())
        req.add_header("Content-Type", "application/json")
        req.add_header("Authorization", AUTH + (', Token="%s"' % token if token else ""))
        with urllib.request.urlopen(req, timeout=20) as r:
            raw = r.read()
            return json.loads(raw) if raw else None

    q = call("POST", "/QuickConnect/Initiate")
    call("POST", "/__harness/quickconnect/approve", {"code": q["Code"]})
    call("GET", "/QuickConnect/Connect?secret=" + q["Secret"])
    signed = call("POST", "/Users/AuthenticateWithQuickConnect", {"Secret": q["Secret"]})
    token, user = signed["AccessToken"], signed["User"]
    fixture = call("GET", "/__harness/fixture")
    eps = call("GET", "/Shows/%s/Episodes?SeasonId=%s&UserId=%s" % (
        fixture["series"]["id"], fixture["season"]["id"], user["Id"]), token=token)["Items"]
    ids = [e["Id"] for e in eps[:4]]
    call("POST", "/__harness/segments", {"outro_seconds": 120})       # credits from 480 s of 600 (80 %)

    def played(item_id):
        return bool(call("GET", "/UserItems/%s/UserData" % item_id, token=token).get("Played"))

    for item_id in ids:                     # the library seeds some as watched: start from unwatched
        call("DELETE", "/UserPlayedItems/%s" % item_id, token=token)
    if any(played(i) for i in ids):
        raise SystemExit("could not mark the episodes unwatched first")

    def playing_reports():
        return [r["item"] for r in call("GET", "/__harness/summary")["reports"] if r["kind"] == "Playing"]

    # -- the backend, from this checkout, as FluentFin starts it ----------------------------------------------------
    conf_dir = os.path.join(a.out, "config")
    os.makedirs(conf_dir, exist_ok=True)
    with open(os.path.join(conf_dir, "mpv.conf"), "w", encoding="utf-8") as fh:
        fh.write("ao=null\n")
    bridge = subprocess.Popen([a.python, "-E", "-s", "-m", "jellyfin_mpv_shim.bridge", "--config-dir", conf_dir,
                               "--log-file", os.path.join(a.out, "bridge.log")],
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              stderr=open(os.path.join(a.out, "bridge-stderr.txt"), "wb"),
                              cwd=os.path.join(REPO, "backend"))
    procs.append(bridge)
    msgs, cond = [], threading.Condition()

    def reader():
        for raw in bridge.stdout:
            try:
                m = json.loads(raw)
            except ValueError:
                m = {"not_json": True}
            with cond:
                msgs.append(m)
                cond.notify_all()
    threading.Thread(target=reader, daemon=True).start()
    nid = [0]

    def wait(pred, timeout=30, since=0):
        end = time.time() + timeout
        with cond:
            while True:
                for m in msgs[since:]:
                    if pred(m):
                        return m
                if time.time() > end:
                    return None
                cond.wait(0.2)

    def req(cmd, args=None, timeout=30):
        nid[0] += 1
        i = nid[0]
        m = {"id": i, "cmd": cmd}
        if args is not None:
            m["args"] = args
        bridge.stdin.write((json.dumps(m) + "\n").encode())
        bridge.stdin.flush()
        return wait(lambda x: x.get("id") == i, timeout) or {}

    def playing(item_id, since, timeout=60):
        return wait(lambda m: m.get("event") == "state" and (m.get("data") or {}).get("state") == "playing"
                    and (m.get("data") or {}).get("itemId") == item_id, timeout, since)

    def mark():
        with cond:
            return len(msgs)

    wait(lambda m: m.get("event") == "hello", 30)
    req("connect", {"protocol": 1, "server": {"url": url, "id": signed.get("ServerId") or "s"},
                    "user": {"id": user["Id"], "name": user["Name"]}, "accessToken": token,
                    "device": {"id": "transition-check", "name": "check"},
                    "client": {"name": "TransitionCheck", "version": "1.2.0"}, "window": {"mode": "standalone"}},
        timeout=60)
    req("set_auto_play", {"autoPlay": True})
    req("set_volume", {"volume": 63})

    # credits: E1 from 482 s, Next at about 490 s (81.7 %, 10 s into the credits)
    at = mark()
    req("play", {"itemIds": ids, "startIndex": 0, "startPositionTicks": 482 * TICKS}, timeout=60)
    say("E1 playing", bool(playing(ids[0], at)))
    time.sleep(8)
    req("set_volume", {"volume": 41})
    at = mark()
    req("next")
    say("E2 playing", bool(playing(ids[1], at)))
    time.sleep(2)
    check("credits: Next 10 s into credits at 80 % leaves the episode played", played(ids[0]))
    state = req("get_state").get("result") or {}
    check("volume: set in the last seconds, carried to the next episode", state.get("volume") == 41,
          "volume %r" % state.get("volume"))

    # double next: PlaybackInfo slow (a remote server), two nexts 0.3 s apart
    call("POST", "/__harness/latency", {"rules": [["playbackinfo", 1500]]})
    before = len(playing_reports())
    at = mark()
    first = req("next")
    time.sleep(0.3)
    second = req("next")
    third_ok = playing(ids[2], at, 60)
    time.sleep(4)
    call("POST", "/__harness/latency", {"rules": []})
    now = (req("get_state").get("result") or {}).get("itemId")
    new_reports = playing_reports()[before:]
    check("double next: the second is refused while the first opens",
          first.get("ok") is True and (second.get("error") or {}).get("code") == "no_playback",
          "answers %r %r" % (first.get("ok"), second.get("error")))
    check("double next: one episode moved to", bool(third_ok) and (now or "").replace("-", "") == ids[2]
          and new_reports == [ids[2]], "now %r, Playing reports %r" % (now, new_reports))
    req("stop")
    time.sleep(1)
    req("shutdown", timeout=15)
    bridge.wait(20)
finally:
    for p in procs[::-1]:
        if p.poll() is None:
            p.kill()
            try:
                p.wait(10)
            except Exception:
                pass

failed = [n for n, ok in results if not ok]
say("%d checks, %d failed%s" % (len(results), len(failed), (": " + "; ".join(failed)) if failed else ""))
sys.exit(1 if failed or not results else 0)
