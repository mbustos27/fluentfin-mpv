"""End-to-end crash-rule scenarios (docs/BRIDGE.md §3, §6): a fake Jellyfin (tools/backend-harness/rr/fakejf2.py, a fresh
fake token per run) + the C# harness (e2e.exe: the REAL backend, FluentFin.Core's host/service/close sequence), one
process per scenario run. Counts the Stopped requests each fake server got, by sender (User-Agent "ui-harness-e2e" =
FluentFin's crash report, else the backend) and play session, and judges each run against the rule.

Run it through run.ps1 (builds e2e.exe, makes a test video, runs this off-screen on a private desktop inside a
kill-on-close job with a sandboxed APPDATA/LOCALAPPDATA). Directly:

    drive.py --exe <e2e.exe> --python <backend venv python.exe> --out <dir> --mkv <small .mkv> [--burn N] scenario[:reps] ...

--burn N keeps N busy-looping processes running meanwhile (a loaded machine). Everything it starts (fake servers,
burners, e2e.exe and through it the backend) is closed in a `finally`. Writes <out>/summary.json and prints a table.
Every run also checks that no token is in the environment of the backend or anything it started (e2e.exe's ENVCHECK).
"""
import argparse
import json
import os
import secrets
import subprocess
import sys
import time

# importing fakejf2 from the repo must not leave a __pycache__ there (run.ps1 also passes -B)
sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "backend-harness", "rr"))
from fakejf2 import FakeJF  # noqa: E402

UID = "0123456789abcdef0123456789abcdef"
UID2 = "fedcba9876543210fedcba9876543210"   # a second user of server A (user_switch_then_kill)
X = "1" * 32
Y = "2" * 32
UA = "ui-harness-e2e"

SCENARIOS = {
    # name: what it checks (docs/BRIDGE.md §6 unless noted)
    "reported_then_kill": "stop, its `reported`, then a kill: the session is closed, FluentFin sends no Stopped",
    "kill_playing": "a kill while playing: exactly one Stopped, FluentFin's, with the position",
    "replace_then_kill": "play Y over X, X reported, then a kill: X the backend's, Y one from FluentFin",
    "next_then_kill": "next (X -> Y), X reported, then a kill: X the backend's, Y one from FluentFin",
    "kill_then_close": "a kill and the close at once: one Stopped from FluentFin, asked for before the close returns",
    "held_kill_close": "a watchdog kill decided and held in its log line, the close finishes it: one Stopped from FluentFin before the close returns (round 5 item 1; run with --burn)",
    "crash_interp": "the real interpreter dies (not a kill): one Stopped from FluentFin; the message does not name the libmpv traceback (item 4)",
    "kill_opening": "a kill while opening: one Stopped from FluentFin, Failed=true and no position",
    "replay_opening_kill": "X played and reported, X again, a kill while the replay opens: Failed=true and no position, not the first session's (item 2)",
    "switch_then_kill": "FluentFin switches to server B, the backend connects to B while A's Stopped hangs, then a kill: no Stopped from FluentFin to A or B (item 3)",
    "user_switch_then_kill": "FluentFin switches to another user of the same server, the backend connects as that user while the first user's Stopped hangs, then a kill: no Stopped from FluentFin as either user (round 5 follow-up)",
    "close_playing": "the close while playing: the backend's Stopped, none from FluentFin",
    "close_hung_stopped": "the close with the server's Stopped hung: the backend's attempt, none from FluentFin, closed within ~10 s",
    "close_suspended": "the close with the backend suspended: killed 10 s after shutdown, no Stopped from anyone",
    "stop_then_close": "stop, then the close: the backend's Stopped only",
    "stop_then_kill": "stop, then a kill at once: at least one Stopped, at most one from FluentFin (a second is the accepted cost)",
    "signout_playing": "Log out / Switch user while playing (round 6 item 1): the playback stops and the backend's Stopped reaches the server, accepted, before the token is revoked; X's `reported` before the logout; within ~2 s; nothing answered 401",
    "signout_hung_stopped": "the same with the server's Stopped hung: the sign-out goes on after ~2 s, the backend's Stopped request arrived before the logout, none from FluentFin",
    # fluentfin-mpv (startup-perf): connect.prewarm (docs/BRIDGE.md §3, §4)
    "prewarm_idle": "pre-warmed after sign-in, idle: one GET /Users/Me and nothing else from the backend (nothing reported), no window of mpv's inside ours, the close shuts it down",
    "prewarm_play": "pre-warmed, then a play: it connects nothing more (one /Users/Me), mpv's window appears, the backend's Stopped only; PLAYTIME against cold_play",
    "prewarm_kill_idle": "pre-warmed, killed while idle: nothing reported by anyone, no interrupted playback; the next play starts it again and plays",
    "prewarm_signout": "pre-warmed, then a sign-out: the backend is shut down, one /Users/Me and nothing else",
    "cold_play": "a play with nothing pre-warmed (the backend started and connected by the play): PLAYTIME for comparison",
    "prewarm_play_wan": "prewarm_play with 40 ms on each API request and on the stream's first byte (a remote server)",
    "cold_play_wan": "cold_play with 40 ms on each API request and on the stream's first byte (a remote server)",
}

PREWARM = ("prewarm_idle", "prewarm_play", "prewarm_kill_idle", "prewarm_signout", "prewarm_play_wan")


class RevokingFakeJF(FakeJF):
    """fakejf2.FakeJF whose POST /Sessions/Logout revokes the token, as Jellyfin does: every later request with it is
    answered 401. Records when the logout came (`revoked_at`) and the status of every Stopped (`stopped_answers`);
    fakejf2.py is not changed."""

    def __init__(self, name, token, media_path, user_id):
        super().__init__(name, token, media_path, user_id)
        outer = self
        self.revoked_at = None
        self.stopped_answers = []
        base = self.httpd.RequestHandlerClass

        class Revoking(base):
            def _json(self, code, obj):
                if self.command == "POST" and self.path.split("?")[0].startswith("/Sessions/Playing/Stopped"):
                    with outer.lock:
                        outer.stopped_answers.append((time.time(), code))
                return base._json(self, code, obj)

            def do_POST(self):
                if self.path.split("?")[0] != "/Sessions/Logout":
                    return base.do_POST(self)
                self._rec(self._body())
                valid = ('Token="%s"' % outer.token) in (self.headers.get("Authorization") or "")
                if valid:
                    with outer.lock:
                        outer.revoked_at = time.time()
                        outer.token = "revoked-" + secrets.token_hex(8)
                return self._json(204 if valid else 401, None)

        self.httpd.RequestHandlerClass = Revoking

T0 = time.time()


class TwoUserFakeJF(FakeJF):
    """fakejf2.FakeJF with a second user: requests carrying token2 are served as user uid2 (its own /Users/Me), everything
    else as fakejf2 serves user one. `users` records (method, path, user, sender) per request; fakejf2.py is not changed."""

    def __init__(self, name, token, media_path, user_id, token2, user_id2):
        super().__init__(name, token, media_path, user_id)
        outer = self
        self.users = []
        base = self.httpd.RequestHandlerClass

        class TwoUsers(base):
            def _as_user(self):
                auth = self.headers.get("Authorization") or ""
                second = 'Token="%s"' % token2 in auth
                with outer.lock:
                    outer.users.append((self.command, self.path.split("?")[0], "user2" if second else "user1",
                                        "UI" if (self.headers.get("User-Agent") or "") == UA else "backend"))
                if second:
                    # served by fakejf2's own code as user one, which knows only its token
                    del self.headers["Authorization"]
                    self.headers["Authorization"] = auth.replace('Token="%s"' % token2, 'Token="%s"' % outer.token)
                return second

            def do_GET(self):
                if self._as_user() and self.path.split("?")[0] == "/Users/Me":
                    self._rec()
                    return self._json(200, {"Id": user_id2, "Name": "u2", "Policy": {}, "Configuration": {}})
                return base.do_GET(self)

            def do_POST(self):
                self._as_user()
                return base.do_POST(self)

            def do_HEAD(self):
                self._as_user()
                return base.do_HEAD(self)

            def do_DELETE(self):
                self._as_user()
                return base.do_DELETE(self)

        self.httpd.RequestHandlerClass = TwoUsers

    def stopped_by_user(self):
        with self.lock:
            rows = [(user, sender) for method, path, user, sender in self.users if method == "POST" and path == "/Sessions/Playing/Stopped"]
        return {k: rows.count(k) for k in sorted(set(rows))}


def log(*a):
    print("[%7.1f]" % (time.time() - T0), *a, flush=True)


def stops(srv):
    rows = []
    with srv.lock:
        for r in srv.records:
            if r["method"] == "POST" and r["path"].split("?")[0] == "/Sessions/Playing/Stopped":
                b = r["body"] if isinstance(r["body"], dict) else {}
                who = "UI" if (r.get("ua") or "") == UA else "backend"
                rows.append({"who": who, "item": (b.get("ItemId") or "")[:4], "ps": b.get("PlaySessionId"),
                             "pos": b.get("PositionTicks"), "failed": b.get("Failed"), "t": round(r["t"] - T0, 2)})
    return rows


def backend_requests(srv):
    """(method, path) of every request the backend made (not FluentFin's own, User-Agent UA), in order."""
    with srv.lock:
        return [(r["method"], r["path"].split("?")[0]) for r in srv.records
                if r.get("method") != "DONE" and (r.get("ua") or "") != UA]


def playtime(text):
    """The PLAYTIME line's three numbers (ms after PlayAsync): play sent, playing, first position."""
    for line in text.splitlines():
        if "PLAYTIME" in line:
            nums = [int(w) for w in line.replace(",", " ").split() if w.isdigit()]
            if len(nums) >= 3:
                return {"sent": nums[-3], "playing": nums[-2], "position": nums[-1]}
    return None


def parse(text):
    close = None
    result = None
    for line in text.splitlines():
        if "CLOSE returned after" in line:
            seg = line.split("CLOSE returned after", 1)[1]
            secs = float(seg.split("s:", 1)[0])
            kv = dict(x.split("=", 1) for x in seg.split("s:", 1)[1].split())
            close = {"secs": secs, "interrupts": int(kv["interrupts"]), "calls": int(kv["uiStopCalls"]), "done": int(kv["uiStopDone"])}
        if "] RESULT " in line:
            result = line.split("] RESULT ", 1)[1]
    return close, result


def judge(sc, c):
    """Problems with one run (empty = PASS)."""
    p = []
    st, stb, issued, close = c["stops"], c["stopsB"], c["issued"], c["close"]

    def rows(ps, who=None):
        return [r for r in st if r["ps"] == ps and (who is None or r["who"] == who)]

    def one_ui(ps, failed=None):
        ui, be = rows(ps, "UI"), rows(ps, "backend")
        if len(ui) != 1:
            p.append("%s: %d Stopped from FluentFin, want 1" % (ps, len(ui)))
        if be:
            p.append("%s: %d Stopped from the backend, want 0" % (ps, len(be)))
        for r in ui:
            if failed and (r["failed"] is not True or r["pos"] is not None):
                p.append("%s: FluentFin's Stopped has Failed=%s pos=%s, want Failed=true and no position" % (ps, r["failed"], r["pos"]))
            if not failed and (r["failed"] is not None or r["pos"] is None):
                p.append("%s: FluentFin's Stopped has Failed=%s pos=%s, want a position" % (ps, r["failed"], r["pos"]))

    def backend_only(ps, at_least=1):
        if len(rows(ps, "UI")):
            p.append("%s: %d Stopped from FluentFin, want 0" % (ps, len(rows(ps, "UI"))))
        if len(rows(ps, "backend")) < at_least:
            p.append("%s: %d Stopped from the backend, want %d" % (ps, len(rows(ps, "backend")), at_least))

    def asked_before_close(n=1):
        if close is None:
            p.append("the close never returned")
        elif close["calls"] < n:
            p.append("only %d Stopped from FluentFin asked for when the close returned (after %.3f s), want %d" % (close["calls"], close["secs"], n))

    if not (c["result"] or "").startswith("%s: ok" % sc):
        p.append("harness: %s" % c["result"])
    if c["leaks"]:
        p.append("fake token in %s" % c["leaks"])
    envcheck = [line.split("ENVCHECK", 1)[1].strip() for line in c["text"].splitlines() if "ENVCHECK" in line]
    if not envcheck:
        p.append("no ENVCHECK: the backend's environment was not looked at")
    for line in envcheck:
        if not line.startswith("ok"):
            p.append("ENVCHECK %s" % line)
    if c["left"]:
        p.append("processes left: %s" % c["left"])
    if [r for r in stb if r["who"] == "UI"]:
        p.append("FluentFin sent Stopped to server B: %s" % stb)
    ps = issued + [None, None]
    x1, second = ps[0], ps[1]
    if sc in ("reported_then_kill", "close_playing", "stop_then_close"):
        backend_only(x1)
    elif sc in ("kill_playing", "kill_then_close", "held_kill_close", "crash_interp"):
        one_ui(x1)
        asked_before_close()
        if sc == "crash_interp" and "mpv-1.dll" in c["text"].split("INTERRUPTED", 1)[-1].split("\n", 1)[0]:
            p.append("the crash message names the harmless libmpv traceback")
    elif sc in ("replace_then_kill", "next_then_kill"):
        backend_only(x1)
        one_ui(second)
        asked_before_close()
    elif sc == "kill_opening":
        one_ui(x1, failed=True)
        asked_before_close()
    elif sc == "replay_opening_kill":
        backend_only(x1)
        one_ui(second, failed=True)
        asked_before_close()
    elif sc in ("switch_then_kill", "user_switch_then_kill"):
        backend_only(x1, at_least=0)
        if rows(x1, "UI"):
            p.append("FluentFin sent the switched-away session's Stopped to A%s" % (" as the new user" if sc.startswith("user") else ""))
        if "not reporting its stop" not in c["svclog"]:
            p.append("the service did not log the skipped report")
    elif sc == "close_hung_stopped":
        backend_only(x1)
        if close and close["secs"] > 10.6:
            p.append("close took %.3f s" % close["secs"])
    elif sc == "close_suspended":
        if st:
            p.append("Stopped requests: %s" % st)
        if close and not 9.5 <= close["secs"] <= 10.6:
            p.append("close took %.3f s, want ~10 s" % close["secs"])
    elif sc == "stop_then_kill":
        if len(rows(x1)) < 1:
            p.append("no Stopped for %s" % x1)
        if len(rows(x1, "UI")) > 1:
            p.append("%d Stopped from FluentFin" % len(rows(x1, "UI")))
    elif sc in ("signout_playing", "signout_hung_stopped"):
        backend_only(x1)
        so = c["signout"]
        if so["revoked_at"] is None:
            p.append("the token was never revoked")
        late = [r for r in rows(x1) if so["revoked_at"] is not None and r["t"] > round(so["revoked_at"] - T0, 2)]
        if late:
            p.append("%d Stopped for %s arrived after the token was revoked" % (len(late), x1))
        refused = [code for _, code in so["stopped_answers"] if code == 401]
        if refused:
            p.append("%d Stopped answered 401 (sent with the revoked token)" % len(refused))
        if so["secs"] is None:
            p.append("no SIGNOUT timing")
        elif so["secs"] > 2.3:
            p.append("the sign-out waited %.3f s, want at most ~2 s" % so["secs"])
        if sc == "signout_playing":
            if not so["reported_first"]:
                p.append("X's `reported` had not come when the token was revoked")
            if [code for _, code in so["stopped_answers"] if not 200 <= code < 300]:
                p.append("Stopped answers %s, want 2xx" % [code for _, code in so["stopped_answers"]])
    elif sc in PREWARM or sc.startswith("cold_play"):
        reqs = c["backendRequests"]
        me = [r for r in reqs if r == ("GET", "/Users/Me")]
        if sc in ("prewarm_idle", "prewarm_signout"):
            if reqs != [("GET", "/Users/Me")]:
                p.append("backend requests %s, want only one GET /Users/Me" % reqs)
            if st:
                p.append("Stopped requests: %s" % st)
        else:
            backend_only(x1)
            if sc.startswith("prewarm_play") and len(me) != 1:
                p.append("%d GET /Users/Me, want 1 (the play connected again)" % len(me))
            if sc == "prewarm_kill_idle":
                if len(me) != 2:
                    p.append("%d GET /Users/Me, want 2 (the pre-warm, the play after the crash)" % len(me))
                if close and close["interrupts"]:
                    p.append("%d interrupted playbacks, want 0 (it crashed while idle)" % close["interrupts"])
            if c["playtime"] is None:
                p.append("no PLAYTIME")
        if sc in PREWARM:
            before = reqs[:reqs.index(("POST", "/Sessions/Playing"))] if ("POST", "/Sessions/Playing") in reqs else reqs
            reported = [r for r in before if r[1].startswith("/Sessions")]
            if reported:
                p.append("reported while idle: %s" % reported)
            if "IDLE mpv window inside ours: none" not in c["text"] and os.environ.get("E2E_ALLOW_IDLE_WINDOW") != "1":
                p.append("no proof that mpv had no window inside ours while idle")
    if sc not in ("close_suspended", "close_hung_stopped") and close and close["secs"] > 10.6:
        p.append("close took %.3f s" % close["secs"])
    return p


def left_behind(cfg):
    ps = ("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine } | "
          "ForEach-Object { '{0}|{1}|{2}' -f $_.ProcessId, $_.Name, $_.CommandLine }")
    exe = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    out = subprocess.run([exe, "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=120).stdout.splitlines()
    key = cfg.replace("/", "\\").lower()
    return [r.split("|")[:2] for r in out if key in r.replace("/", "\\").lower() and "powershell" not in r.lower()]


def run(a, sc, rep, token, token2):
    srv = FakeJF("A", token, a.mkv, UID)
    srvB = None
    if sc == "kill_opening":
        srv.stream_delay[X] = 8.0
    if sc.endswith("_wan"):
        # a remote server: every API request and the stream's first byte 40 ms later
        srv.me_delay = srv.item_delay = srv.playbackinfo_delay = 0.04
        srv.stream_delay[X] = 0.04
    if sc == "replay_opening_kill":
        class LaterDelay(dict):
            # the stream is slow only once a Stopped came in: the replay opens slowly, the first play does not
            def _on(self):
                with srv.lock:
                    return any(r["method"] == "POST" and "/Stopped" in r["path"] for r in srv.records)

            def get(self, k, d=None):
                return 8.0 if self._on() else d

            def __getitem__(self, k):
                return 8.0
        srv.stream_delay = LaterDelay()
    if sc == "close_hung_stopped":
        srv.stopped_delay = 30.0
    if sc == "switch_then_kill":
        srv.stopped_delay = 6.0
        srvB = FakeJF("B", token, a.mkv, UID)
    if sc == "user_switch_then_kill":
        srv.close()
        srv = TwoUserFakeJF("A", token, a.mkv, UID, token2, UID2)
        srv.stopped_delay = 6.0
    if sc.startswith("signout"):
        srv.close()
        srv = RevokingFakeJF("A", token, a.mkv, UID)
        if sc == "signout_hung_stopped":
            srv.stopped_delay = 6.0
    label = "%s-%d" % (sc, rep)
    try:
        log("==== %s (server A %s%s)" % (label, srv.url, "" if srvB is None else ", B " + srvB.url))
        args = [a.exe, a.python, a.out, label, sc, srv.url, UID] + ([srvB.url] if srvB else [])
        env = dict(os.environ, E2E_FAKE_TOKEN=token, E2E_FAKE_TOKEN_2=token2, E2E_USER_2=UID2)
        try:
            p = subprocess.run(args, capture_output=True, text=True, timeout=240, cwd=a.out, env=env)
            text = p.stdout + p.stderr
        except subprocess.TimeoutExpired as e:
            text = (e.stdout or "") + (e.stderr or "") + "\nharness: e2e.exe timed out (killed)"
        with open(os.path.join(a.out, label + ".txt"), "w", encoding="utf-8") as fh:
            fh.write(text)
        for line in text.splitlines():
            if any(k in line for k in ("RESULT", "CLOSE returned", "INTERRUPTED", "UI Stopped", "event reported", "harness error",
                                       "reported events", "Error:", "Warning:", "Stopping the mpv", "exited with code", "connect to B",
                                       "ENVCHECK", "SIGNOUT", "UI Logout", "PREWARM", "IDLE", "PLAYTIME", "PLAYING", "KILLED", "SIGNEDOUT")):
                log("   ", line.strip()[:300])
        time.sleep(1.0)
        close, result = parse(text)
        svclog = ""
        try:
            svclog = open(os.path.join(a.out, label + "-service.log"), encoding="utf-8", errors="replace").read()
        except OSError:
            pass
        leaks = []
        for f in (label + ".txt", label + "-host.log", label + "-service.log", label + "-backend.log"):
            try:
                text_f = open(os.path.join(a.out, f), encoding="utf-8", errors="replace").read()
                if token in text_f or token2 in text_f:
                    leaks.append(f)
            except OSError:
                pass
        signout = {"secs": None, "reported_first": False, "revoked_at": getattr(srv, "revoked_at", None),
                   "stopped_answers": list(getattr(srv, "stopped_answers", []))}
        for line in text.splitlines():
            if "SIGNOUT stop and report took" in line:
                seg = line.split("took", 1)[1]
                signout["secs"] = float(seg.split("s;", 1)[0])
                signout["reported_first"] = seg.rstrip().endswith("True")
        c = {"stops": stops(srv), "stopsB": stops(srvB) if srvB else [], "issued": list(srv.issued), "close": close,
             "backendRequests": backend_requests(srv), "playtime": playtime(text),
             "result": result, "text": text, "svclog": svclog, "leaks": leaks, "signout": signout,
             "left": left_behind(os.path.join(a.out, label + "-config"))}
        problems = judge(sc, c)
        per = {}
        for r in c["stops"]:
            per.setdefault(r["ps"], []).append("%s%s" % (r["who"], "" if r["who"] == "backend" else "(pos=%s,failed=%s)" % (r["pos"], r["failed"])))
        log("  server A Stopped per session:", per or "none", "| issued:", c["issued"])
        if srvB is not None:
            log("  server B Stopped:", c["stopsB"] or "none")
        by_user = srv.stopped_by_user() if isinstance(srv, TwoUserFakeJF) else None
        if by_user is not None:
            log("  server A Stopped by (user, sender):", by_user or "none")
        if sc.startswith("signout"):
            log("  sign-out: %.3f s, reported first: %s, Stopped answers: %s, token revoked at %s" % (
                -1 if signout["secs"] is None else signout["secs"], signout["reported_first"], [code for _, code in signout["stopped_answers"]],
                "-" if signout["revoked_at"] is None else round(signout["revoked_at"] - T0, 2)))
        log("  close: %s" % close)
        log("  VERDICT %s: %s" % (label, "PASS" if not problems else "FAIL " + "; ".join(problems)))
        if sc in PREWARM or sc.startswith("cold_play"):
            log("  backend requests: %s | playtime: %s" % (c["backendRequests"], c["playtime"]))
        return {"run": label, "scenario": sc, "pass": not problems, "problems": problems, "perSession": per,
                "playtime": c["playtime"], "backendRequests": c["backendRequests"] if (sc in PREWARM or sc.startswith("cold_play")) else None,
                "serverB": c["stopsB"], "stoppedByUser": {"%s/%s" % k: v for k, v in (by_user or {}).items()},
                "issued": c["issued"], "close": close,
                "signout": {k: v for k, v in signout.items() if k != "revoked_at"} if sc.startswith("signout") else None}
    finally:
        srv.close()
        if srvB is not None:
            srvB.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--exe", required=True)
    ap.add_argument("--python", required=True, help="the backend venv's python.exe")
    ap.add_argument("--out", required=True)
    ap.add_argument("--mkv", required=True)
    ap.add_argument("--burn", type=int, default=0, help="busy-looping processes to keep running meanwhile")
    ap.add_argument("scenarios", nargs="+", help="name[:reps]; 'all' = every scenario once. Known: " + ", ".join(SCENARIOS))
    a = ap.parse_args()
    a.out = os.path.abspath(a.out)
    os.makedirs(a.out, exist_ok=True)
    todo = []
    seen = {}
    for arg in a.scenarios:
        name, _, reps = arg.partition(":")
        for sc in (SCENARIOS if name == "all" else [name]):
            if sc not in SCENARIOS:
                sys.exit("unknown scenario %s" % sc)
            for _ in range(int(reps or 1)):
                # numbered across the whole list, so a scenario named twice never overwrites a run's files
                todo.append((sc, seen.get(sc, 0)))
                seen[sc] = seen.get(sc, 0) + 1
    token = secrets.token_hex(16)   # fake: only the fake servers know it
    token2 = secrets.token_hex(16)  # the second user's, also fake
    burners = []
    results = []
    try:
        for _ in range(a.burn):
            burners.append(subprocess.Popen([sys.executable, "-I", "-c", "while True: pass"]))
        if burners:
            log("burning %d processes" % len(burners))
        for sc, i in todo:
            results.append(run(a, sc, i, token, token2))
    finally:
        for b in burners:
            b.kill()
        for b in burners:
            b.wait(timeout=30)
    with open(os.path.join(a.out, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump(results, fh, indent=1)
    log("summary (%d runs, %d pass):" % (len(results), sum(r["pass"] for r in results)))
    for r in results:
        close = r["close"] or {}
        log("  %-26s %-4s close %6s s, FluentFin Stopped asked for by then: %s | per session %s%s%s" % (
            r["run"], "PASS" if r["pass"] else "FAIL", close.get("secs", "-"), close.get("calls", "-"), r["perSession"] or "none",
            " | by user " + str(r["stoppedByUser"]) if r["stoppedByUser"] else "",
            "" if r["pass"] else " | " + "; ".join(r["problems"])))
    return 0 if all(r["pass"] for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
