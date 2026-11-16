"""Probe the real bridge against fake loopback Jellyfin servers. No real server, fake tokens."""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import os as _os  # noqa: E402
_REPO = _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", ".."))  # the repository
sys.path.insert(0, _os.path.join(_REPO, "backend", "tools"))
from bridge_smoke import BridgeProcess, ok, error_code  # noqa: E402
from fakejf import FakeJF, Sniffer  # noqa: E402

PY = _os.path.join(_REPO, "backend", ".venv", "Scripts", "python.exe")
OUT = os.environ.get("PROBE_OUT") or sys.exit("set PROBE_OUT to a scratch folder outside the repo")
MKV = os.environ.get("PROBE_MKV") or sys.exit("set PROBE_MKV to a small test .mkv")
UID = "0123456789abcdef0123456789abcdef"
TA = "aaaaaaaa11111111aaaaaaaa11111111"
TB = "bbbbbbbb22222222bbbbbbbb22222222"
X = "11111111111111111111111111111111"
Y = "22222222222222222222222222222222"
os.makedirs(OUT, exist_ok=True)
#: when this run began: mpv_procs / kill_pids act only on processes created after it
RUN_STARTED = time.time()


def connect_args(server, token, window=True):
    a = {"protocol": 1,
         "server": {"url": server.url, "id": "s" + server.name, "name": server.name},
         "user": {"id": UID, "name": "u"},
         "accessToken": token,
         "device": {"id": "dev-probe", "name": "Probe"},
         "client": {"name": "FluentFin", "version": "0.0.1"}}
    if window:
        a["window"] = {"mode": "standalone"}
    return a


def mpv_procs(config_dir):
    """This run's mpv.exe for ``config_dir`` (rr/ownproc.py: the dir in its
    command line, created after this module was loaded, read through one
    handle)."""
    return _ownproc().find("mpv.exe", config_dir, RUN_STARTED)


def ports(pids):
    if not pids:
        return ""
    ids = ",".join(str(p) for p in pids)
    ps = ("$p=@(%s); Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $p -contains $_.OwningProcess } | "
          "ForEach-Object { 'TCP {0}:{1} -> {2}:{3} {4} pid {5}' -f $_.LocalAddress,$_.LocalPort,$_.RemoteAddress,$_.RemotePort,$_.State,$_.OwningProcess };"
          "Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $p -contains $_.OwningProcess } | "
          "ForEach-Object { 'UDP {0}:{1} pid {2}' -f $_.LocalAddress,$_.LocalPort,$_.OwningProcess }") % ids
    return subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                          capture_output=True, text=True, timeout=60).stdout


def kill_pids(pids):
    """Ends each pid only through a handle that proved it this run's (rr/ownproc.py)."""
    _ownproc().terminate(pids, OUT, RUN_STARTED)


def _ownproc():
    """rr/ownproc.py, loaded by path (rr/ also has a fakejf.py, which must not
    shadow this folder's)."""
    import importlib.util
    mod = sys.modules.get("ownproc")
    if mod is None:
        spec = importlib.util.spec_from_file_location("ownproc", os.path.join(HERE, "rr", "ownproc.py"))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        sys.modules["ownproc"] = mod
    return mod


def leaks(b, tokens):
    found = []
    for path in (b.stderr_path, b.log_file):
        try:
            data = open(path, "rb").read()
        except OSError:
            continue
        for t in tokens:
            if t.encode() in data:
                found.append((os.path.basename(path), t[:8]))
    for line in b.lines:
        for t in tokens:
            if t.encode() in line:
                found.append(("stdout", t[:8]))
    return found


def wait_state(b, st, since, timeout=40):
    return b.wait_event("state", lambda d: d.get("state") == st, timeout, since)


def summary(name, b, servers, extra=None):
    print("---- %s" % name)
    for s in servers:
        print("  %s sessions: %s" % (s.name, s.sessions()))
    evs = [(m.get("event"), m.get("data") if m.get("event") in ("ended", "error", "item", "fullscreen_request") else (m.get("data") or {}).get("state"))
           for m in b.messages if "event" in m and m.get("event") not in ("position",)]
    print("  events:", evs)
    if extra:
        for k, v in extra.items():
            print("  %s: %s" % (k, v))


def s_switch():
    name = "switch"
    A = FakeJF("A", TA, MKV, UID); B = FakeJF("B", TB, MKV, UID); sn = Sniffer()
    b = BridgeProcess(PY, OUT, name, dev=True, conf={"mpv_log_level": "debug"})
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        r = b.request("connect", connect_args(A, TA), timeout=60); extra["connectA"] = r
        m = b.mark(); r = b.request("play", {"itemIds": [X]}); extra["playX"] = r
        extra["playingX"] = bool(wait_state(b, "playing", m))
        time.sleep(4)
        extra["mpv_pids"] = mpv_procs(b.config_dir)
        extra["ports"] = ports(extra["mpv_pids"] + [b.proc.pid])
        extra["A_before_switch"] = A.sessions()
        m = b.mark(); r = b.request("connect", connect_args(B, TB, window=False), timeout=60); extra["connectB"] = r
        time.sleep(3)
        extra["A_after_switch"] = A.sessions()
        m = b.mark(); r = b.request("play", {"itemIds": [Y]}); extra["playY"] = r
        extra["playingY"] = bool(wait_state(b, "playing", m))
        time.sleep(3)
        stream_auth = [(r["path"][:40], r["token"]) for r in B.records if "/stream" in r["path"]][:3]
        extra["B_stream_requests(path,token)"] = stream_auth
        m = b.mark(); r = b.request("play_file", {"url": sn.url + "/clip.mkv", "title": "x"}); extra["play_file"] = r
        time.sleep(5)
        extra["sniffer_saw"] = [(x["path"], {k: v for k, v in x["headers"].items() if k.lower() in ("authorization", "x-emby-token")}) for x in sn.records]
        t0 = time.time(); b.proc.stdin.close(); code = b.finish(60)
        extra["eof_exit"] = (code, round(time.time() - t0, 1))
        time.sleep(1)
        extra["mpv_left"] = mpv_procs(b.config_dir)
        extra["leaks"] = leaks(b, [TA, TB])
        extra["not_json"] = b.not_json[:3]
    finally:
        summary(name, b, [A, B], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_eof_opening():
    name = "eof-opening"
    A = FakeJF("A", TA, MKV, UID); A.playbackinfo_delay = 4
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        extra["connect"] = b.request("connect", connect_args(A, TA), timeout=60)
        m = b.mark(); extra["play"] = b.request("play", {"itemIds": [X]})
        time.sleep(1.5)
        extra["state_at_eof"] = (b.request("get_state") or {}).get("result", {}).get("state")
        t0 = time.time(); b.proc.stdin.close(); code = b.finish(60)
        extra["exit"] = (code, round(time.time() - t0, 1))
        time.sleep(5)  # let the PlaybackInfo delay elapse
        extra["mpv_left_after_5s"] = mpv_procs(b.config_dir)
        extra["leaks"] = leaks(b, [TA])
    finally:
        summary(name, b, [A], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_eof_loading():
    """EOF while mpv is loading (stream slow): PlaybackInfo fast, item ok."""
    name = "eof-item-fetch"
    A = FakeJF("A", TA, MKV, UID); A.item_delay = 3
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        extra["connect"] = b.request("connect", connect_args(A, TA), timeout=60)
        extra["play"] = b.request("play", {"itemIds": [X]})
        time.sleep(1.0)
        t0 = time.time(); b.proc.stdin.close(); code = b.finish(60)
        extra["exit"] = (code, round(time.time() - t0, 1))
        time.sleep(5)
        extra["mpv_left_after_5s"] = mpv_procs(b.config_dir)
    finally:
        summary(name, b, [A], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_shutdown_playing():
    name = "shutdown-playing"
    A = FakeJF("A", TA, MKV, UID)
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        extra["connect"] = b.request("connect", connect_args(A, TA), timeout=60)
        m = b.mark(); extra["play"] = b.request("play", {"itemIds": [X]})
        extra["playing"] = bool(wait_state(b, "playing", m))
        time.sleep(3)
        t0 = time.time(); extra["shutdown"] = b.request("shutdown", timeout=40); code = b.finish(60)
        extra["exit"] = (code, round(time.time() - t0, 1))
        time.sleep(1)
        extra["mpv_left"] = mpv_procs(b.config_dir)
    finally:
        summary(name, b, [A], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_replace():
    name = "replace"
    A = FakeJF("A", TA, MKV, UID)
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        extra["connect"] = b.request("connect", connect_args(A, TA), timeout=60)
        m = b.mark(); b.request("play", {"itemIds": [X]})
        extra["playingX"] = bool(wait_state(b, "playing", m))
        time.sleep(2)
        m = b.mark(); b.request("play", {"itemIds": [Y]})
        extra["itemY"] = b.wait_event("item", lambda d: d.get("itemId") == Y, 40, m)
        time.sleep(2)
        m = b.mark(); extra["stop"] = b.request("stop")
        wait_state(b, "idle", m, 10)
        time.sleep(2)
        extra["shutdown"] = b.request("shutdown", timeout=40); b.finish(60)
    finally:
        summary(name, b, [A], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_connect_opening():
    name = "connect-during-opening"
    A = FakeJF("A", TA, MKV, UID); A.playbackinfo_delay = 3
    B = FakeJF("B", TB, MKV, UID)
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        extra["connectA"] = b.request("connect", connect_args(A, TA), timeout=60)
        b.request("play", {"itemIds": [X]})
        time.sleep(1.0)
        extra["connectB"] = b.request("connect", connect_args(B, TB, window=False), timeout=60)
        time.sleep(6)
        extra["state"] = (b.request("get_state") or {}).get("result")
        extra["shutdown"] = b.request("shutdown", timeout=40); b.finish(60)
    finally:
        summary(name, b, [A, B], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


if __name__ == "__main__":
    which = sys.argv[1:] or ["switch", "eof_opening", "eof_loading", "shutdown_playing", "replace", "connect_opening"]
    for w in which:
        globals()["s_" + w]()


def s_next():
    name = "next"
    A = FakeJF("A", TA, MKV, UID)
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        extra["connect"] = b.request("connect", connect_args(A, TA), timeout=60).get("ok")
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        extra["playingX"] = bool(wait_state(b, "playing", m))
        time.sleep(2)
        m = b.mark(); extra["next"] = b.request("next")
        extra["itemY"] = bool(b.wait_event("item", lambda d: d.get("itemId") == Y, 40, m))
        time.sleep(2)
        extra["A_after_next"] = A.sessions()
        extra["shutdown"] = b.request("shutdown", timeout=40); b.finish(60)
    finally:
        summary(name, b, [A], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_pause_opening():
    name = "pause-opening"
    A = FakeJF("A", TA, MKV, UID); A.playbackinfo_delay = 2
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        b.request("connect", connect_args(A, TA), timeout=60)
        m = b.mark(); b.request("play", {"itemIds": [X]})
        time.sleep(0.8)
        extra["state_when_paused"] = (b.request("get_state") or {}).get("result", {}).get("state")
        extra["pause"] = b.request("pause")
        extra["seek"] = b.request("seek", {"positionTicks": 600000000})
        first = b.wait_event("state", lambda d: d.get("state") in ("playing", "paused"), 40, m)
        extra["first_active_state"] = first and first.get("state")
        time.sleep(2.5)
        st = (b.request("get_state") or {}).get("result", {})
        extra["after_2.5s"] = (st.get("state"), st.get("positionTicks"))
        b.request("shutdown", timeout=40); b.finish(60)
    finally:
        summary(name, b, [A], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()
