"""Finding 6: a server-switch connect / a shutdown behind next on a slow or
hung server. Fake loopback servers and fake 32-hex tokens only."""
import os
import sys
import time

import os as _os  # noqa: E402
_REPO = _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", ".."))  # the repository
REV = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, REV)
sys.path.insert(0, _os.path.join(_REPO, "backend", "tools"))
from bridge_smoke import BridgeProcess  # noqa: E402
from fakejf import FakeJF  # noqa: E402
import probe  # noqa: E402
from probe import (PY, MKV, UID, TA, TB, X, Y, connect_args, mpv_procs,  # noqa: E402
                   kill_pids, wait_state, summary, leaks)

OUT = os.environ.get("PROBE_OUT") or sys.exit("set PROBE_OUT to a scratch folder outside the repo")
os.makedirs(OUT, exist_ok=True)
Z = "33333333333333333333333333333333"


def stopped_after(server, t0):
    with server.lock:
        return [(r["path"].split("?")[0], (r["body"] or {}).get("ItemId"), round(r["t"] - t0, 1))
                for r in server.records
                if r["method"] == "POST" and r["path"].startswith("/Sessions/Playing/Stopped")
                and r["t"] >= t0]


def s_hang_switch():
    name = "p6-hang-switch"
    A = FakeJF("A", TA, MKV, UID)
    B = FakeJF("B", TB, MKV, UID)
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        extra["connectA"] = (b.request("connect", connect_args(A, TA), timeout=60) or {}).get("ok")
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        extra["playingX"] = bool(wait_state(b, "playing", m))
        time.sleep(2)
        A.playbackinfo_delay = 120          # A now hangs on PlaybackInfo
        extra["next"] = (b.request("next") or {}).get("ok")
        time.sleep(1.5)
        t0 = time.time()
        r = b.request("connect", connect_args(B, TB, window=False), timeout=90)
        extra["connectB"] = ((r or {}).get("ok"), (r or {}).get("error"), round(time.time() - t0, 1))
        m = b.mark(); t0 = time.time()
        b.request("play", {"itemIds": [Z]})
        extra["playingZ_on_B"] = (bool(b.wait_event("item", lambda d: d.get("itemId") == Z, 60, m)),
                                  round(time.time() - t0, 1))
        time.sleep(2)
        st = (b.request("get_state") or {}).get("result") or {}
        extra["state"] = (st.get("state"), st.get("itemId"))
        t0 = time.time()
        r = b.request("shutdown", timeout=40)
        code = b.finish(60)
        extra["shutdown"] = ((r or {}).get("ok"), code, round(time.time() - t0, 1))
        extra["B_stopped"] = stopped_after(B, 0)
        time.sleep(1)
        extra["mpv_left"] = mpv_procs(b.config_dir)
        extra["leaks"] = leaks(b, [TA, TB])
    finally:
        summary(name, b, [A, B], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()
        A.playbackinfo_delay = 0


def s_slow_shutdown(delay=6.0, name="p6-slow-shutdown"):
    A = FakeJF("A", TA, MKV, UID)
    b = BridgeProcess(PY, OUT, name, dev=True)
    extra = {}
    try:
        b.wait_event("hello", timeout=30)
        extra["connect"] = (b.request("connect", connect_args(A, TA), timeout=60) or {}).get("ok")
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        extra["playingX"] = bool(wait_state(b, "playing", m))
        time.sleep(2)
        A.playbackinfo_delay = delay
        extra["next"] = (b.request("next") or {}).get("ok")
        time.sleep(1.0)
        t0 = time.time()
        r = b.request("shutdown", timeout=40)
        code = b.finish(60)
        extra["shutdown(ok, exit, secs)"] = ((r or {}).get("ok"), code, round(time.time() - t0, 1))
        extra["A_stopped_reports_after_shutdown"] = stopped_after(A, t0)
        time.sleep(1)
        extra["mpv_left"] = mpv_procs(b.config_dir)
    finally:
        summary(name, b, [A], extra)
        kill_pids(mpv_procs(b.config_dir)); b.kill()
        A.playbackinfo_delay = 0


def s_hang_shutdown():
    s_slow_shutdown(120.0, "p6-hang-shutdown")


if __name__ == "__main__":
    for w in sys.argv[1:] or ["slow_shutdown", "hang_switch"]:
        globals()["s_" + w]()
