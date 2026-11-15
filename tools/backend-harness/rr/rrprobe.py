"""Re-review probes against the real bridge. Fake loopback servers, fake tokens only."""
import json, os, subprocess, sys, time, threading
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, r"C:\Users\moise\Dev\fluentfin-mpv\backend\tools")
from bridge_smoke import BridgeProcess  # noqa
from fakejf2 import FakeJF  # noqa

PY = r"C:\Users\moise\Dev\fluentfin-mpv\backend\.venv\Scripts\python.exe"
RR = os.path.dirname(HERE)
#: Where the bridges' config dirs and logs go, and the clip the fake servers
#: stream: RR_OUT / RR_MKV, so a run keeps them in its own scratch folder.
OUT = os.environ.get("RR_OUT") or sys.exit("set RR_OUT to a scratch folder outside the repo (bridge config dirs and logs go there)")
MKV = os.environ.get("RR_MKV") or sys.exit("set RR_MKV to a small .mkv the fake servers stream (e.g. one made with mpv av://lavfi:testsrc --o=...)")
UID = "0123456789abcdef0123456789abcdef"
TA = "aaaaaaaa11111111aaaaaaaa11111111"
TB = "bbbbbbbb22222222bbbbbbbb22222222"
X = "11111111111111111111111111111111"
Y = "22222222222222222222222222222222"
Z = "33333333333333333333333333333333"
os.makedirs(OUT, exist_ok=True)
T0 = time.time()


def log(*a):
    print("[%6.1f]" % (time.time() - T0), *a, flush=True)


def cargs(server, token, window=True, uid=UID):
    a = {"protocol": 1,
         "server": {"url": server.url, "id": "s" + server.name, "name": server.name},
         "user": {"id": uid, "name": "u"}, "accessToken": token,
         "device": {"id": "dev-probe", "name": "Probe"},
         "client": {"name": "FluentFin", "version": "0.0.1"}}
    if window:
        a["window"] = {"mode": "standalone"}
    return a


def mpv_procs(config_dir):
    """This run's mpv.exe for ``config_dir``: the dir in its command line and
    created after this module was loaded (T0), both read through one handle
    (ownproc.py). Pid objects: they also carry the creation time, which
    kill_pids and rr3probe._suspend require again."""
    import ownproc  # rr/, on sys.path above
    return ownproc.find("mpv.exe", config_dir, T0)


def kill_pids(pids):
    """Ends each pid only through a handle that proved it this run's: created
    after T0, with OUT (or the config dir it was found by) in its command line
    and, for a pid from mpv_procs, the very creation time found (ownproc.py)."""
    import ownproc  # rr/, on sys.path above
    ownproc.terminate(pids, OUT, T0)


def sess(server, t0=0):
    with server.lock:
        out = []
        for r in server.records:
            if r["t"] < t0:
                continue
            p = r["path"].split("?")[0]
            if r["method"] in ("POST", "DONE") and p.startswith("/Sessions/Playing"):
                k = {"/Sessions/Playing": "Playing", "/Sessions/Playing/Progress": "Progress",
                     "/Sessions/Playing/Stopped": "Stopped"}.get(p, p)
                b = r["body"] or {}
                out.append(("%s%s" % ("" if r["method"] == "POST" else "done:", k),
                            (b.get("ItemId") or "")[:2], b.get("PositionTicks"), round(r["t"] - T0, 1)))
            elif r["method"] == "DELETE":
                out.append(("DELETE", p[:30], None, round(r["t"] - T0, 1)))
        return out


def stops(server, item):
    return [s for s in sess(server) if s[0] == "Stopped" and s[1] == item[:2]]


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


def evsum(b, since=0):
    out = []
    for m in b.messages[since:]:
        e = m.get("event")
        if e is None or e == "position":
            continue
        d = m.get("data") or {}
        if e == "state":
            out.append(("state", d.get("state"), (d.get("itemId") or "")[:2]))
        elif e in ("ended", "error", "item"):
            out.append((e, (d.get("itemId") or "")[:2], d.get("reason") or d.get("code")))
    return out


def wait_state(b, st, since, timeout=40, item=None):
    return b.wait_event("state", lambda d: d.get("state") == st and (item is None or d.get("itemId") == item), timeout, since)


def start(name, conf=None):
    b = BridgeProcess(PY, OUT, name, dev=True, conf=conf)
    b.wait_event("hello", timeout=30)
    return b


def finish(b, servers, tokens):
    try:
        t0 = time.time()
        r = b.request("shutdown", timeout=40)
        code = b.finish(60)
        log("shutdown resp", (r or {}).get("ok"), "exit", code, "secs", round(time.time() - t0, 1))
        time.sleep(1)
        log("mpv left", mpv_procs(b.config_dir))
        log("leaks", leaks(b, tokens), "not_json", b.not_json[:2])
    finally:
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def wait_record(server, pred, timeout):
    end = time.time() + timeout
    while time.time() < end:
        with server.lock:
            for r in server.records:
                if pred(r):
                    return r
        time.sleep(0.02)
    return None


# --- 1. timeline thread holds _tl_lock in a hung progress report; server switch
def s_tl_switch():
    log("==== tl_switch")
    A = FakeJF("A", TA, MKV, UID); B = FakeJF("B", TB, MKV, UID)
    b = start("rr-tl-switch")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.progress_delay = 300; A.stopped_delay = 300; A.delete_delay = 300
        t_hang = time.time()
        r = wait_record(A, lambda r: r["t"] > t_hang and "/Progress" in r["path"], 15)
        log("A progress request now hanging:", bool(r))
        time.sleep(1)
        t0 = time.time()
        r = b.request("connect", cargs(B, TB, window=False), timeout=90)
        log("connectB", (r or {}).get("ok"), (r or {}).get("error"), "after", round(time.time() - t0, 1), "s")
        m = b.mark(); t0 = time.time()
        b.request("play", {"itemIds": [Z]})
        it = b.wait_event("item", lambda d: d.get("itemId") == Z, 60, m)
        log("Z item on B", bool(it), round(time.time() - t0, 1))
        time.sleep(8)
        log("B sessions", sess(B))
        log("A sessions", sess(A))
        log("events", evsum(b))
        finish(b, [A, B], [TA, TB])
    finally:
        A.progress_delay = A.stopped_delay = A.delete_delay = 0
        b.kill()


# --- 2. hung progress, shutdown
def s_tl_shutdown():
    log("==== tl_shutdown")
    A = FakeJF("A", TA, MKV, UID)
    b = start("rr-tl-shutdown")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.progress_delay = 300
        t_hang = time.time()
        r = wait_record(A, lambda r: r["t"] > t_hang and "/Progress" in r["path"], 15)
        log("A progress hanging:", bool(r))
        time.sleep(1)
        finish(b, [A], [TA])
        log("A sessions", sess(A))
    finally:
        A.progress_delay = 0
        b.kill()


# --- 3. stop lands while _report_outgoing waits for _tl_lock (slow progress)
def s_stop_window(cmd="stop"):
    log("==== stop_window", cmd)
    A = FakeJF("A", TA, MKV, UID); B = FakeJF("B", TB, MKV, UID)
    b = start("rr-window-" + cmd)
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(1)
        A.progress_delay = 3.0
        t_hang = time.time()
        r = wait_record(A, lambda r: r["t"] > t_hang and r["method"] == "POST" and r["path"].startswith("/Sessions/Playing/Progress"), 15)
        log("A progress in flight:", bool(r))
        m = b.mark()
        log("play Y ->", (b.request("play", {"itemIds": [Y]}) or {}).get("ok"))
        time.sleep(0.8)
        if cmd == "stop":
            log("stop ->", (b.request("stop") or {}).get("ok"))
        elif cmd == "play":
            log("play Z ->", (b.request("play", {"itemIds": [Z]}) or {}).get("ok"))
        else:
            t0 = time.time()
            r = b.request("connect", cargs(B, TB, window=False), timeout=60)
            log("connectB ->", (r or {}).get("ok"), round(time.time() - t0, 1))
        A.progress_delay = 0
        time.sleep(10)
        st = (b.request("get_state") or {}).get("result") or {}
        log("FINAL state", st.get("state"), st.get("itemId"))
        log("events", evsum(b, m))
        log("A sessions", sess(A))
        if cmd == "connect":
            log("B sessions", sess(B))
        finish(b, [A, B], [TA, TB])
    finally:
        A.progress_delay = 0
        b.kill()


# --- 4. play X; play Y (Y's stream slow) ; then play Z / stop while Y loads
def s_ppp(cmd="play"):
    log("==== ppp", cmd)
    A = FakeJF("A", TA, MKV, UID)
    A.stream_delay[Y] = 6
    b = start("rr-ppp-" + cmd)
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(3)
        m = b.mark()
        log("play Y ->", (b.request("play", {"itemIds": [Y]}) or {}).get("ok"))
        time.sleep(1.5)
        st = (b.request("get_state") or {}).get("result") or {}
        log("state while Y loads", st.get("state"), st.get("itemId"))
        if cmd == "play":
            log("play Z ->", (b.request("play", {"itemIds": [Z]}) or {}).get("ok"))
        else:
            log("stop ->", (b.request("stop") or {}).get("ok"))
        time.sleep(8)
        st = (b.request("get_state") or {}).get("result") or {}
        log("FINAL state", st.get("state"), st.get("itemId"))
        log("events", evsum(b, m))
        log("A sessions", sess(A))
        log("X stopped reports", stops(A, X))
        finish(b, [A], [TA])
    finally:
        b.kill()


# --- 5. plain replace: reason + one report each
def s_replace():
    log("==== replace")
    A = FakeJF("A", TA, MKV, UID)
    b = start("rr-replace")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(3)
        m = b.mark(); b.request("play", {"itemIds": [Y]})
        log("item Y", bool(b.wait_event("item", lambda d: d.get("itemId") == Y, 40, m)))
        time.sleep(3)
        b.request("stop"); time.sleep(3)
        log("events", evsum(b, m))
        log("A sessions", sess(A))
        finish(b, [A], [TA])
    finally:
        b.kill()


# --- 6. /Users/Me answers 404 (old server) / another user
def s_me():
    for mode in ("404", "other"):
        log("==== me", mode)
        A = FakeJF("A", TA, MKV, UID); A.me_mode = mode
        b = start("rr-me-" + mode)
        try:
            r = b.request("connect", cargs(A, TA), timeout=60)
            log("connect", (r or {}).get("ok"), (r or {}).get("error"))
            finish(b, [A], [TA])
        finally:
            b.kill()


# --- 7. queued pause during opening, then next
def s_pause_next():
    log("==== pause_next")
    A = FakeJF("A", TA, MKV, UID); A.playbackinfo_delay = 2
    b = start("rr-pause-next")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        time.sleep(0.5)
        log("pause ->", (b.request("pause") or {}).get("ok"))
        log("seek ->", (b.request("seek", {"positionTicks": 300000000}) or {}).get("ok"))
        log("next ->", b.request("next"))
        time.sleep(10)
        st = (b.request("get_state") or {}).get("result") or {}
        log("FINAL state", st.get("state"), st.get("itemId"), st.get("positionTicks"))
        log("events", evsum(b, m))
        finish(b, [A], [TA])
    finally:
        b.kill()


# --- 8. play_file after a server item: no auth header to a third-party host
def s_sniff():
    from fakejf2 import Sniffer
    log("==== sniff")
    A = FakeJF("A", TA, MKV, UID); sn = Sniffer()
    b = start("rr-sniff")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        st = [(r["path"][:30], r["token"]) for r in A.records if "/stream" in r["path"]][:2]
        log("stream requests carried token (header path)", [(p, (t or "")[:6]) for p, t in st])
        time.sleep(2)
        b.request("play_file", {"url": sn.url + "/clip.mkv", "title": "x"})
        time.sleep(5)
        log("sniffer saw", [(x["path"], {k: v[:30] for k, v in x["headers"].items() if k.lower() in ("authorization", "x-emby-token", "x-emby-authorization")}) for x in sn.records])
        log("A stopped", stops(A, X))
        finish(b, [A], [TA])
    finally:
        b.kill()


# --- 9. shutdown lands while _report_outgoing waits for _tl_lock
def s_shutdown_window():
    log("==== shutdown_window")
    A = FakeJF("A", TA, MKV, UID)
    b = start("rr-window-shutdown")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(1)
        A.progress_delay = 3.0
        t_hang = time.time()
        r = wait_record(A, lambda r: r["t"] > t_hang and r["method"] == "POST" and r["path"].startswith("/Sessions/Playing/Progress"), 15)
        log("A progress in flight:", bool(r))
        b.request("play", {"itemIds": [Y]})
        time.sleep(0.6)
        A.progress_delay = 0
        finish(b, [A], [TA])
        time.sleep(4)
        log("mpv after 4s more", mpv_procs(b.config_dir))
        log("A sessions", sess(A))
    finally:
        kill_pids(mpv_procs(b.config_dir))
        b.kill()




def after_shutdown(b, sid):
    idx = [i for i, m in enumerate(b.messages) if m.get("id") == sid]
    if not idx:
        return "no shutdown response"
    return [m.get("event") or ("resp", m.get("id")) for m in b.messages[idx[0] + 1:]]


# --- 10. next on a slow/hung PlaybackInfo, then shutdown (probe6 shape)
def s_next_shutdown(delay="6"):
    delay = float(delay)
    log("==== next_shutdown", delay)
    A = FakeJF("A", TA, MKV, UID)
    b = start("rr-next-shutdown-%d" % delay)
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.playbackinfo_delay = delay
        log("next", (b.request("next") or {}).get("ok"))
        time.sleep(1.0)
        t0 = time.time()
        sid = b._next_id + 1
        r = b.request("shutdown", timeout=40)
        code = b.finish(60)
        log("shutdown(ok, exit, secs)", (r or {}).get("ok"), code, round(time.time() - t0, 1))
        log("protocol lines after the shutdown response", after_shutdown(b, sid))
        time.sleep(1)
        log("mpv left", mpv_procs(b.config_dir))
        log("A sessions", sess(A))
        log("leaks", leaks(b, [TA]), "not_json", b.not_json[:1])
    finally:
        kill_pids(mpv_procs(b.config_dir)); b.kill()
        A.playbackinfo_delay = 0


# --- 11. next hung on PlaybackInfo; switch to B; play on B; (probe6 hang_switch)
def s_hang_switch():
    log("==== hang_switch")
    A = FakeJF("A", TA, MKV, UID); B = FakeJF("B", TB, MKV, UID)
    b = start("rr-hang-switch")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.playbackinfo_delay = 120
        log("next", (b.request("next") or {}).get("ok"))
        time.sleep(1.5)
        t0 = time.time()
        r = b.request("connect", cargs(B, TB, window=False), timeout=90)
        log("connectB", (r or {}).get("ok"), (r or {}).get("error"), round(time.time() - t0, 1))
        m = b.mark(); t0 = time.time()
        b.request("play", {"itemIds": [Z]})
        it = b.wait_event("item", lambda d: d.get("itemId") == Z, 60, m)
        log("Z item on B", bool(it), round(time.time() - t0, 1))
        time.sleep(3)
        log("B sessions", sess(B))
        log("A sessions", sess(A))
        finish(b, [A, B], [TA, TB])
    finally:
        A.playbackinfo_delay = 0
        b.kill()





# --- 12. a uniformly slow server (every request takes N s), playing, then shutdown
def s_slow_all(delay="4", with_next="0"):
    delay = float(delay)
    log("==== slow_all", delay, "next" if with_next == "1" else "")
    A = FakeJF("A", TA, MKV, UID)
    b = start("rr-slow-all-%d-%s" % (delay, with_next))
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X, Y]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.progress_delay = A.stopped_delay = A.playing_delay = A.delete_delay = A.playbackinfo_delay = delay
        time.sleep(6)
        if with_next == "1":
            log("next", (b.request("next") or {}).get("ok"))
            time.sleep(1.0)
        t0 = time.time()
        sid = b._next_id + 1
        r = b.request("shutdown", timeout=40)
        t_resp = round(time.time() - t0, 1)
        code = b.finish(60)
        log("shutdown resp", (r or {}).get("ok"), "at", t_resp, "s; exit", code, "at", round(time.time() - t0, 1), "s")
        time.sleep(delay + 1)
        log("mpv left", mpv_procs(b.config_dir))
        log("A Stopped after shutdown", [s for s in sess(A, t0) if "Stopped" in s[0]])
    finally:
        kill_pids(mpv_procs(b.config_dir)); b.kill()
        A.progress_delay = A.stopped_delay = A.playing_delay = A.delete_delay = A.playbackinfo_delay = 0





def all_procs(config_dir):
    ps = ("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*%s*' } | "
          "ForEach-Object { '{0}|{1}' -f $_.ProcessId, $_.Name }") % config_dir.replace("'", "''")
    out = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=60).stdout
    return [(int(l.split("|")[0]), l.split("|")[1]) for l in out.splitlines() if "|" in l]


def s_ports():
    log("==== ports")
    A = FakeJF("A", TA, MKV, UID)
    b = start("rr-ports")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        procs = all_procs(b.config_dir) + [(b.proc.pid, "launcher")]
        log("procs", procs)
        ids = ",".join(str(p) for p, _ in procs)
        ps = ("$p=@(%s); Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $p -contains $_.OwningProcess } | "
              "ForEach-Object { 'LISTEN {0}:{1} pid {2}' -f $_.LocalAddress,$_.LocalPort,$_.OwningProcess };"
              "Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $p -contains $_.OwningProcess } | "
              "ForEach-Object { 'UDP {0}:{1} pid {2}' -f $_.LocalAddress,$_.LocalPort,$_.OwningProcess }") % ids
        log("listeners", subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=60).stdout.strip() or "none")
        finish(b, [A], [TA])
    finally:
        b.kill()





# --- 13. a seek queued for an item whose start then fails lands on the outgoing item
def s_seek_failed_replace():
    log("==== seek_failed_replace")
    A = FakeJF("A", TA, MKV, UID)
    b = start("rr-seek-failed")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.pbi_fail.add(Y); A.playbackinfo_delay = 2
        m = b.mark()
        log("play Y ->", (b.request("play", {"itemIds": [Y]}) or {}).get("ok"))
        time.sleep(0.5)
        st = (b.request("get_state") or {}).get("result") or {}
        log("state when seeking", st.get("state"), (st.get("itemId") or "")[:2])
        log("seek 100s ->", (b.request("seek", {"positionTicks": 1000000000}) or {}).get("ok"))
        time.sleep(5)
        st = (b.request("get_state") or {}).get("result") or {}
        log("FINAL state", st.get("state"), (st.get("itemId") or "")[:2], "pos s", (st.get("positionTicks") or 0) / 1e7)
        log("events", evsum(b, m))
        log("A sessions after", [s for s in sess(A) if s[0] in ("Stopped", "Progress", "Playing")][-4:])
        A.playbackinfo_delay = 0
        finish(b, [A], [TA])
    finally:
        b.kill()





# --- 14. after switching away from a hung server, when does the new server hear Playing?
def s_tl_switch_long():
    log("==== tl_switch_long")
    A = FakeJF("A", TA, MKV, UID); B = FakeJF("B", TB, MKV, UID)
    b = start("rr-tl-switch-long")
    try:
        log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
        m = b.mark(); b.request("play", {"itemIds": [X]})
        log("playing X", bool(wait_state(b, "playing", m)))
        time.sleep(2)
        A.progress_delay = A.stopped_delay = A.delete_delay = A.playing_delay = 600
        t_hang = time.time()
        log("A progress hanging:", bool(wait_record(A, lambda r: r["t"] > t_hang and "/Progress" in r["path"], 15)))
        time.sleep(1)
        t0 = time.time()
        r = b.request("connect", cargs(B, TB, window=False), timeout=90)
        log("connectB", (r or {}).get("ok"), "after", round(time.time() - t0, 1), "s")
        m = b.mark(); t0 = time.time()
        b.request("play", {"itemIds": [Z]})
        log("Z item on B", bool(b.wait_event("item", lambda d: d.get("itemId") == Z, 60, m)), round(time.time() - t0, 1))
        r = wait_record(B, lambda r: r["path"].startswith("/Sessions/Playing") and r["method"] == "POST", 400)
        log("B first session report:", r and r["path"], "after", r and round(r["t"] - t0, 1), "s from play Z")
        log("A requests", [(x["method"], x["path"].split("?")[0][:28], round(x["t"] - T0, 1)) for x in A.records if x["t"] > t_hang])
        finish(b, [A, B], [TA, TB])
    finally:
        b.kill()



# --- round 2: each hung endpoint, then shutdown or a switch -------------------
HANG = 300


def _hang_setup(b, A, endpoint):
    """Play X (X,Y queue), make `endpoint` hang, and get a request to it in
    flight (or about to be). Returns a label."""
    log("connectA", (b.request("connect", cargs(A, TA), timeout=60) or {}).get("ok"))
    m = b.mark(); b.request("play", {"itemIds": [X, Y]})
    log("playing X", bool(wait_state(b, "playing", m)))
    time.sleep(1.5)
    t_hang = time.time()
    if endpoint == "PlaybackInfo":
        A.playbackinfo_delay = HANG
        log("next", (b.request("next") or {}).get("ok"))
        log("PBI in flight", bool(wait_record(A, lambda r: r["t"] > t_hang and "/PlaybackInfo" in r["path"], 10)))
    elif endpoint == "progress":
        A.progress_delay = HANG
        log("progress in flight", bool(wait_record(A, lambda r: r["t"] > t_hang and "/Progress" in r["path"], 15)))
    elif endpoint == "Stopped":
        A.stopped_delay = HANG
    elif endpoint == "item":
        A.item_delay = HANG
        log("play Z", (b.request("play", {"itemIds": [Z]}) or {}).get("ok"))
        log("item fetch in flight", bool(wait_record(A, lambda r: r["t"] > t_hang and ("/Items/" + Z) in r["path"], 10)))
    elif endpoint == "Playing":
        A.playing_delay = HANG
        m = b.mark()
        log("play Z", (b.request("play", {"itemIds": [Z]}) or {}).get("ok"))
        log("Playing in flight", bool(wait_record(A, lambda r: r["t"] > t_hang and r["path"].split("?")[0] == "/Sessions/Playing" and r["method"] == "POST", 15)))
    time.sleep(0.5)


def _unhang(A):
    A.playbackinfo_delay = A.progress_delay = A.stopped_delay = 0
    A.item_delay = A.playing_delay = A.delete_delay = 0


def s_hung_shutdown(endpoint):
    log("==== hung_shutdown", endpoint)
    A = FakeJF("A", TA, MKV, UID)
    b = start("r2-hs-" + endpoint)
    try:
        _hang_setup(b, A, endpoint)
        t0 = time.time()
        r = b.request("shutdown", timeout=40)
        t_resp = time.time() - t0
        code = b.finish(60)
        t_exit = time.time() - t0
        log("RESULT hung_shutdown %s: answer ok=%s at %.2fs, exit %s at %.2fs"
            % (endpoint, (r or {}).get("ok"), t_resp, code, t_exit))
        time.sleep(1)
        log("mpv left", mpv_procs(b.config_dir))
        log("A Stopped (POST) after shutdown", [s for s in sess(A, t0) if s[0] == "Stopped"])
        log("leaks", leaks(b, [TA]), "not_json", b.not_json[:1])
    finally:
        _unhang(A)
        kill_pids(mpv_procs(b.config_dir)); b.kill()


def s_hung_switch(endpoint):
    log("==== hung_switch", endpoint)
    A = FakeJF("A", TA, MKV, UID); B = FakeJF("B", TB, MKV, UID)
    b = start("r2-hw-" + endpoint)
    try:
        _hang_setup(b, A, endpoint)
        t0 = time.time()
        r = b.request("connect", cargs(B, TB, window=False), timeout=90)
        t_conn = time.time() - t0
        m = b.mark(); t1 = time.time()
        b.request("play", {"itemIds": [Z]})
        it = b.wait_event("item", lambda d: d.get("itemId") == Z, 90, m)
        t_item = time.time() - t1
        rr = wait_record(B, lambda r: r["path"].split("?")[0] == "/Sessions/Playing" and r["method"] == "POST", 90)
        t_play = (rr["t"] - t1) if rr else None
        log("RESULT hung_switch %s: connect ok=%s in %.2fs; Z item on B %s in %.2fs; B first Playing %s"
            % (endpoint, (r or {}).get("ok"), t_conn, bool(it), t_item,
               ("in %.2fs" % t_play) if t_play is not None else "never"))
        time.sleep(2)
        st = (b.request("get_state") or {}).get("result") or {}
        log("FINAL state", st.get("state"), (st.get("itemId") or "")[:2])
        log("A requests after the switch", [(x["method"], x["path"].split("?")[0][:30], round(x["t"] - t0, 1)) for x in A.records if x["t"] >= t0])
        log("B sessions", [s for s in sess(B) if not s[0].startswith("done")])
        log("events", evsum(b, m))
        finish(b, [A, B], [TA, TB])
    finally:
        _unhang(A)
        b.kill()


if __name__ == "__main__":
    for w in sys.argv[1:]:
        name, _, arg = w.partition(":")
        try:
            globals()["s_" + name](*(arg.split(",") if arg else []))
        except Exception as e:
            import traceback; traceback.print_exc()


