"""Round-3 scenarios against the real bridge (fake loopback servers, fake
tokens only). Run off-screen through hidden_run.py, like rrprobe.py:

    rr3probe.py <scenario[:arg,arg]> ...
    (RR_OUT=<scratch out dir>, RR_MKV=<a small .mkv>; before_probe.py runs
    these against an older backend copy too)

Scenarios: next_window:<stop|play|connect>, next_cancel:<stop|play|connect>,
stale_error:<stop|connect>, replay[:fixed], hung_eof:<endpoint>,
verify_shutdown, stop_hung, stop_then_shutdown, hung_mpv:<shutdown|eof>[,secs].
"""
import json
import sys
import time

import rrprobe as R
from rrprobe import (FakeJF, TA, TB, UID, X, Y, Z, cargs, log, sess, evsum,
                     wait_state, wait_record, finish, start, mpv_procs,
                     kill_pids)


def s_next_window(then="stop"):
    """A playing in an [X, Y] queue; a progress report in flight on a slow
    server holds the report lock; `next` (play_next reports X, waits on the
    lock); then `then` while play_next waits."""
    log("==== next_window", then)
    A = FakeJF("A", TA, R.MKV, UID); B = FakeJF("B", TB, R.MKV, UID)
    b = start("rr3-next-window-" + then)
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(1)
        A.progress_delay = 4.0
        t_hang = time.time()
        r = wait_record(A, lambda r: r["t"] > t_hang and r["method"] == "POST"
                        and r["path"].startswith("/Sessions/Playing/Progress"), 15)
        log("progress in flight", bool(r))
        m = b.mark()
        log("next ->", (b.request("next") or {}).get("ok"))
        time.sleep(0.8)
        if then == "stop":
            log("stop ->", (b.request("stop") or {}).get("ok"))
        elif then == "play":
            log("play Z ->", (b.request("play", {"itemIds": [Z]}) or {}).get("ok"))
        elif then == "connect":
            t0 = time.time()
            r = b.request("connect", cargs(B, TB, window=False), timeout=60)
            log("connectB ->", (r or {}).get("ok"), round(time.time() - t0, 1))
        A.progress_delay = 0
        time.sleep(8)
        st = (b.request("get_state") or {}).get("result") or {}
        log("FINAL state", st.get("state"), (st.get("itemId") or "")[:2])
        log("events", evsum(b, m))
        log("A Stopped", [s for s in sess(A) if s[0] == "Stopped"])
        log("A DELETE", [s for s in sess(A) if s[0] == "DELETE"])
        log("A Playing", [s for s in sess(A) if s[0] == "Playing"])
        log("RESULT next_window %s: X Stopped x%d" % (
            then, len([s for s in sess(A) if s[0] == "Stopped" and s[1] == X[:2]])))
        finish(b, [A, B], [TA, TB])
    finally:
        A.progress_delay = 0
        b.kill()


def s_stale_error(then="stop"):
    """An item fetch hangs on A; `then` supersedes the play; the fetch is let
    go later (a switch retires A, so it fails 2 s after). Does a stale error
    land after the command?"""
    log("==== stale_error", then)
    A = FakeJF("A", TA, R.MKV, UID); B = FakeJF("B", TB, R.MKV, UID)
    b = start("rr3-stale-error-" + then)
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        A.item_delay = 6.0
        m = b.mark()
        log("play Z ->", (b.request("play", {"itemIds": [Z]}) or {}).get("ok"))
        time.sleep(1.0)
        if then == "stop":
            log("stop ->", (b.request("stop") or {}).get("ok"))
        else:
            t0 = time.time()
            r = b.request("connect", cargs(B, TB, window=False), timeout=60)
            log("connectB ->", (r or {}).get("ok"), round(time.time() - t0, 1))
        st = (b.request("get_state") or {}).get("result") or {}
        log("state right after", st.get("state"), (st.get("itemId") or "")[:2])
        time.sleep(9)
        A.item_delay = 0
        st = (b.request("get_state") or {}).get("result") or {}
        ev = evsum(b, m)
        log("FINAL state", st.get("state"), (st.get("itemId") or "")[:2])
        log("events", ev)
        log("RESULT stale_error %s: final %s, error events %d" % (
            then, st.get("state"), len([e for e in ev if e[0] == "error"])))
        log("A Playing", [s for s in sess(A) if s[0] == "Playing"])
        finish(b, [A, B], [TA, TB])
    finally:
        A.item_delay = 0
        b.kill()


def s_replay(mode="fresh"):
    """X, Y, X again, stop. ``fixed``: the fake server hands the same
    PlaySessionId to every play of an item; the default is Jellyfin's, a new
    one per PlaybackInfo. Is the second X reported Stopped?"""
    log("==== replay", mode)
    A = FakeJF("A", TA, R.MKV, UID)
    A.fixed_sessions = mode == "fixed"
    b = start("rr3-replay-" + mode)
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        for item in (X, Y, X):
            m = b.mark(); b.request("play", {"itemIds": [item]})
            log("playing", item[:2], bool(b.wait_event("item", lambda d: d.get("itemId") == item, 40, m)))
            time.sleep(2.5)
        b.request("stop"); time.sleep(3)
        playing = [s[:2] for s in sess(A) if s[0] == "Playing"]
        stopped = [s[:2] for s in sess(A) if s[0] == "Stopped"]
        log("A Playing", playing)
        log("A Stopped", stopped)
        log("RESULT replay %s: %d Playing, %d Stopped" % (mode, len(playing), len(stopped)))
        finish(b, [A], [TA])
    finally:
        b.kill()


def s_next_cancel(then="stop", delay="4"):
    """[X, Y] playing; PlaybackInfo slow (not hung); `next` (play_next
    reports X, Y's start in flight); `then` while Y negotiates: the start
    is cancelled, X stays loaded. How many Stopped does X get?"""
    log("==== next_cancel", then, delay)
    A = FakeJF("A", TA, R.MKV, UID); B = FakeJF("B", TB, R.MKV, UID)
    b = start("rr3-next-cancel-" + then)
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.playbackinfo_delay = float(delay)
        m = b.mark(); t0 = time.time()
        log("next ->", (b.request("next") or {}).get("ok"))
        log("PBI in flight", bool(wait_record(A, lambda r: r["t"] > t0 and "/PlaybackInfo" in r["path"], 10)))
        time.sleep(0.5)
        if then == "stop":
            log("stop ->", (b.request("stop") or {}).get("ok"))
        elif then == "play":
            log("play Z ->", (b.request("play", {"itemIds": [Z]}) or {}).get("ok"))
        elif then == "connect":
            r = b.request("connect", cargs(B, TB, window=False), timeout=60)
            log("connectB ->", (r or {}).get("ok"), round(time.time() - t0, 1))
        time.sleep(float(delay) + 4)
        A.playbackinfo_delay = 0
        st = (b.request("get_state") or {}).get("result") or {}
        ev = evsum(b, m)
        log("FINAL state", st.get("state"), (st.get("itemId") or "")[:2])
        log("events", ev)
        log("A Stopped", [s for s in sess(A) if s[0] == "Stopped"])
        log("A Playing", [s for s in sess(A) if s[0] == "Playing"])
        log("RESULT next_cancel %s: X Stopped x%d, error events %d" % (
            then, len([s for s in sess(A) if s[0] == "Stopped" and s[1] == X[:2]]),
            len([e for e in ev if e[0] == "error"])))
        finish(b, [A, B], [TA, TB])
    finally:
        A.playbackinfo_delay = 0
        b.kill()


def s_hung_eof(endpoint="progress"):
    """stdin EOF (not the shutdown command) with an endpoint hung."""
    log("==== hung_eof", endpoint)
    A = FakeJF("A", TA, R.MKV, UID)
    b = start("rr3-heof-" + endpoint)
    try:
        R._hang_setup(b, A, endpoint)
        t0 = time.time()
        b.proc.stdin.close()
        code = b.finish(60)
        log("RESULT hung_eof %s: exit %s at %.2fs" % (endpoint, code, time.time() - t0))
        time.sleep(1)
        log("mpv left", mpv_procs(b.config_dir))
        log("A Stopped after EOF", [s for s in sess(A, t0) if s[0] == "Stopped"])
    finally:
        R._unhang(A)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_verify_shutdown(_=None):
    """A playing on A; connect B whose /Users/Me hangs; shutdown during the
    verify. Each request answered once? How long to exit?"""
    log("==== verify_shutdown")
    A = FakeJF("A", TA, R.MKV, UID); B = FakeJF("B", TB, R.MKV, UID)
    B.me_delay = 300
    b = start("rr3-verify-shutdown")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(1.5)
        with b._cond:
            b._next_id += 1
            cid = b._next_id
        b.send_raw((json.dumps({"id": cid, "cmd": "connect",
                                "args": cargs(B, TB, window=False)}) + "\n").encode())
        log("Users/Me in flight", bool(wait_record(B, lambda r: r["path"].startswith("/Users/Me"), 10)))
        time.sleep(1.0)
        t0 = time.time()
        r = b.request("shutdown", timeout=40)
        t_resp = time.time() - t0
        code = b.finish(60)
        log("RESULT verify_shutdown: shutdown ok=%s at %.2fs, exit %s at %.2fs" % (
            (r or {}).get("ok"), t_resp, code, time.time() - t0))
        answers = [m for m in b.messages if m.get("id") == cid]
        log("connect B answers", [(a.get("ok"), (a.get("error") or {}).get("code")) for a in answers])
        log("A Stopped", [s for s in sess(A) if s[0] == "Stopped"])
        time.sleep(1)
        log("mpv left", mpv_procs(b.config_dir))
        log("leaks", R.leaks(b, [TA, TB]), "not_json", b.not_json[:1])
    finally:
        B.me_delay = 0
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_stop_hung(_=None):
    """A playing on A; A's progress endpoint hangs (a report in flight holds
    the report lock); `stop`, then `connect B`. Are they answered?"""
    log("==== stop_hung")
    A = FakeJF("A", TA, R.MKV, UID); B = FakeJF("B", TB, R.MKV, UID)
    b = start("rr3-stop-hung")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(1.5)
        A.progress_delay = 300
        t_hang = time.time()
        log("progress in flight", bool(wait_record(A, lambda r: r["t"] > t_hang and "/Progress" in r["path"], 15)))
        time.sleep(0.5)
        t0 = time.time()
        r = b.request("stop", timeout=20)
        log("RESULT stop answered:", r is not None, "after %.1fs" % (time.time() - t0))
        t1 = time.time()
        r = b.request("connect", cargs(B, TB, window=False), timeout=20)
        log("RESULT connect B answered:", r is not None, (r or {}).get("ok"), "after %.1fs" % (time.time() - t1))
        t2 = time.time()
        r = b.request("ping", timeout=5)
        log("ping answered:", r is not None, "after %.2fs" % (time.time() - t2))
        st = (b.request("get_state") or {}).get("result") or {}
        log("state", st.get("state"), (st.get("itemId") or "")[:2], "connected", st.get("connected"))
        log("B requests", [(x["method"], x["path"].split("?")[0][:28]) for x in B.records])
        t3 = time.time()
        r = b.request("shutdown", timeout=40)
        t_resp = time.time() - t3
        code = b.finish(60)
        log("shutdown ok=%s at %.2fs exit %s at %.2fs" % ((r or {}).get("ok"), t_resp, code, time.time() - t3))
        time.sleep(1)
        log("mpv left", mpv_procs(b.config_dir))
        log("A Stopped", [s for s in sess(A) if s[0] == "Stopped"])
    finally:
        A.progress_delay = 0
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_stop_then_shutdown(slow="0"):
    """X playing; `stop` answered at once, then `shutdown` straight after,
    before the deferred stop has run (``slow``: A's Stopped endpoint takes
    that many seconds). X is reported Stopped exactly once, within the
    shutdown's deadline."""
    log("==== stop_then_shutdown", slow)
    A = FakeJF("A", TA, R.MKV, UID)
    b = start("rr3-stop-shutdown-" + slow)
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.stopped_delay = float(slow)
        t0 = time.time()
        r1 = b.request("stop", timeout=20)
        r2 = b.request("shutdown", timeout=40)
        t_resp = time.time() - t0
        code = b.finish(60)
        time.sleep(float(slow) + 1)
        stopped = [s for s in sess(A) if s[0] == "Stopped"]
        log("RESULT stop_then_shutdown %s: stop ok=%s, shutdown ok=%s at %.2fs, exit %s, X Stopped x%d" % (
            slow, (r1 or {}).get("ok"), (r2 or {}).get("ok"), t_resp, code,
            len([s for s in stopped if s[1] == X[:2]])))
        log("A Stopped", stopped)
        log("mpv left", mpv_procs(b.config_dir))
    finally:
        A.stopped_delay = 0
        kill_pids(mpv_procs(b.config_dir)); b.kill()


# --- mpv itself not answering -------------------------------------------------

def _suspend(pid, resume=False):
    """NtSuspendProcess / NtResumeProcess on ``pid`` (from mpv_procs: this
    probe's own bridge's mpv, found by its config dir), only through a handle
    that proved it this run's first: created after the run began, the config
    dir (or RR_OUT) in its command line, the very creation time mpv_procs
    found (ownproc.py). A pid reused since is refused, so a resume in a
    finally never reaches another process."""
    import ownproc  # rr/, next to this file
    return ownproc.suspend(pid, R.OUT, R.T0, resume=resume)


def s_hung_mpv(how="shutdown", after="1"):
    """X playing; the bridge's own mpv is suspended (its IPC never answers
    again); ``after`` seconds later the bridge is asked to shut down
    (``shutdown``) or its stdin is closed (``eof``). Answer and exit times,
    and whether that mpv outlives the backend outside a job."""
    log("==== hung_mpv", how, after)
    A = FakeJF("A", TA, R.MKV, UID)
    b = start("rr3-hung-mpv-%s-%s" % (how, after))
    pids = []
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        pids = mpv_procs(b.config_dir)
        log("mpv", pids, "suspended", [_suspend(p) for p in pids])
        time.sleep(float(after))
        t0 = time.time()
        if how == "eof":
            b.proc.stdin.close()
            r = None
            t_resp = None
        else:
            r = b.request("shutdown", timeout=40)
            t_resp = time.time() - t0
        code = b.finish(60)
        t_exit = time.time() - t0
        time.sleep(1)
        left = mpv_procs(b.config_dir)
        log("RESULT hung_mpv %s %s: answer %s at %s, exit %s at %.2fs, mpv left %s" % (
            how, after, (r or {}).get("ok") if how != "eof" else "-",
            "%.2fs" % t_resp if t_resp is not None else "-", code, t_exit, left))
        log("A Stopped after", [s for s in sess(A, t0) if s[0] == "Stopped"])
    finally:
        for p in pids:
            _suspend(p, resume=True)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_hung_mpv_next(pbi="6.5", how="shutdown"):
    """The double fault: X,Y playing; a `next` holds the player's lock across
    a slow PlaybackInfo (``pbi`` s); the bridge's mpv is suspended, then the
    shutdown (or stdin EOF) comes. The start gives the lock back mid-budget,
    the orderly stop then waits on mpv's IPC (5 s), and the exit watchdog
    (9.5 s) may end the backend first. Does that mpv outlive it?"""
    log("==== hung_mpv_next", pbi, how)
    A = FakeJF("A", TA, R.MKV, UID)
    b = start("rr3-hung-mpv-next-%s-%s" % (pbi, how))
    pids = []
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.playbackinfo_delay = float(pbi)
        t_next = time.time()
        log("next", (b.request("next") or {}).get("ok"))
        log("PBI in flight", bool(wait_record(A, lambda r: r["t"] > t_next and "/PlaybackInfo" in r["path"], 10)))
        time.sleep(0.3)
        pids = mpv_procs(b.config_dir)
        log("mpv", pids, "suspended", [_suspend(p) for p in pids])
        time.sleep(0.5)
        t0 = time.time()
        if how == "eof":
            b.proc.stdin.close()
            r, t_resp = None, None
        else:
            r = b.request("shutdown", timeout=40)
            t_resp = time.time() - t0
        code = b.finish(60)
        t_exit = time.time() - t0
        time.sleep(1)
        left = mpv_procs(b.config_dir)
        log("RESULT hung_mpv_next %s %s: answer %s at %s, exit %s at %.2fs, mpv left %s" % (
            pbi, how, (r or {}).get("ok") if how != "eof" else "-",
            "%.2fs" % t_resp if t_resp is not None else "-", code, t_exit, left))
    finally:
        A.playbackinfo_delay = 0
        for p in pids:
            _suspend(p, resume=True)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


if __name__ == "__main__":
    for w in sys.argv[1:]:
        name, _, arg = w.partition(":")
        try:
            globals()["s_" + name](*(arg.split(",") if arg else []))
        except Exception:
            import traceback; traceback.print_exc()
