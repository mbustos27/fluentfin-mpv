"""Round-7 probes against the real bridge: the round-7 reviewer's scenarios
(rr7probe/fixprobe in its scratch folder), moved here with bounds for the
round-7 fixes. A held end of file -- X reaches its end while a ``play`` is
pending (its item fetch) or opening (its PlaybackInfo) -- and what ends that
play. Fake loopback servers (fakejf2), fake tokens only; run off-screen.

    RR_OUT=<scratch folder> RR_MKV=<small .mkv> rr7probe.py <scenario[:args]> ...

(``before_probe.py`` finds these too, for a run against an older backend.)
Each prints ``RESULT rr7 <name>: ok`` or the problems found: rr6probe's
per-session checks (one ``reported`` per session, none before its Stopped,
``sent`` only on a 2xx, ``none`` only for a session the server never heard
of; exactly one Stopped for every session with Playing) plus each
scenario's own. No arguments runs ``ALL``.

- ``held_stop[:pbi|item]``, ``held_play2[:pbi|item]``,
  ``held_shutdown[:pbi|item[,eof]]``, ``held_switch[:pbi|item]``: queue
  [X, Y], X seeked to its last seconds, ``play Z`` slow (PlaybackInfo or the
  item fetch, 5 s), then a stop, a second play, a shutdown, a server switch.
  Y must never start; X gets exactly one Stopped.
- ``held_load_fail``: Z's stream does not open: ``state`` ends ``error``.
- ``held_fetch_fail[:0|1]``: Z's item fetch fails (only Z's is slow): the
  held end runs after all -- Y plays (``0``), or X ends (``1``: X alone).
- ``held_last[:shutdown|eof|stop[,pbi|item[,delay]]]``: X alone (the last
  item: mpv idles at its end, ``playback_abort``, and stop() returns early),
  X ends while ``play Z`` waits, then the shutdown, stdin EOF or a stop.
  X gets its Stopped (round 7 item 2: Playing 1, Stopped 0, ``reported
  failed``); ``stop`` answers at once and X goes ``idle`` within
  ``IDLE_AFTER_STOP`` of the answer, not when Z's fetch returns.
- ``spin[:delay,ids]``: X at its end while ``play Z`` waits ``delay`` s on
  its item fetch (``ids`` 2: [X, Y], 1: [X]). Round 7 item 1: the poll
  rescue re-queued the held end on every pass -- ~40,000 "Holding the end"
  lines and ~14 s of the backend's CPU in 19 s. Bounded here: at most
  ``SPIN_HOLDS`` such lines, ``SPIN_LINES`` log lines and ``SPIN_CPU`` s of
  CPU per process (the backend's python, mpv) over the wait.
"""
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rr5probe as P  # noqa: E402  (puts backend/tools on sys.path)
import rr6probe as Q  # noqa: E402
from rr5probe import (EXTRA, EncJF, GARBAGE, evs, get_state,  # noqa: E402
                      play, playing_posts, stopped_posts)
from rrprobe import X, Y, Z, TB, cargs, log  # noqa: E402

#: Bounds for ``spin`` (the fixed backend: 1 hold, +2 lines, ~0.1 s CPU).
SPIN_HOLDS = 2
SPIN_LINES = 100
SPIN_CPU = 3.0
#: ``held_last:stop``: from the ``stop`` answer to ``idle``.
IDLE_AFTER_STOP = 2.0


def run(name, body, server_cls=EncJF, how="shutdown", conf=None):
    P.run(name, body, conf=conf, server_cls=server_cls, how=how, label="rr7",
          check=Q.sessions_check)


class ItemFailJF(EncJF):
    """The item fetch for ids in ``item_fail`` answers nothing; ids in
    ``slow_items`` wait that many seconds first (only those: a delay on
    every fetch also held up the advance to Y it is meant to show)."""

    def __init__(self, *a, **k):
        super().__init__(*a, **k)
        self.item_fail = set()
        self.slow_items = {}

    def item(self, item_id):
        delay = self.slow_items.get(item_id)
        if delay:
            time.sleep(delay)
        if item_id in self.item_fail:
            return None
        return super().item(item_id)


def _setup(b, A, ids, seek_s="118.5"):
    play(b, ids)
    time.sleep(1.5)
    m = b.mark()
    b.request("seek", {"positionTicks": int(float(seek_s) * 10000000)})
    time.sleep(0.2)
    return m


def _report(b, A, m, want_state=None, want_item=None, y_may_start=False,
            x_stopped=True):
    """``x_stopped``: X's sessions must have their one Stopped by now (not
    before a shutdown that is to send it: `sessions_check` sees that one)."""
    s = get_state(b)
    log("after: %s %s %s" % (s.get("state"), (s.get("itemId") or "")[:2],
                             s.get("playSessionId")))
    plays = playing_posts(A)
    stops = stopped_posts(A)
    log("Playing", {k[-10:]: len(v) for k, v in plays.items()})
    log("Stopped", {k[-10:]: len(v["post"]) for k, v in stops.items()})
    log("errors", [(e.get("code"), e.get("message")) for e in Q._errors(b, m)])
    log("ended", Q._ended(b, m))
    log("events", evs(b, m))
    if not y_may_start:
        started = [ps for ps in plays if Y[:6] in ps]
        if started:
            EXTRA.append("Y started: %s" % started)
    for ps in [ps for ps in plays if X[:6] in ps] if x_stopped else []:
        n = len(stops.get(ps, {"post": []})["post"])
        if n != 1:
            EXTRA.append("X session %s: %d Stopped" % (ps, n))
    if want_state is not None and s.get("state") != want_state:
        EXTRA.append("final state %s, want %s" % (s.get("state"), want_state))
    if want_item is not None and s.get("itemId") != want_item:
        EXTRA.append("final item %s, want %s" % (s.get("itemId"), want_item))
    return s


def _slow(A, where, seconds=5.0):
    if where == "pbi":
        A.playbackinfo_delay = seconds
    else:
        A.item_delay = seconds


def s_held_stop(where="pbi"):
    def body(b, A, B, pids):
        m = _setup(b, A, [X, Y])
        _slow(A, where)
        b.request("play", {"itemIds": [Z]})
        time.sleep(2.5)         # X reaches its end meanwhile
        log("stop", (b.request("stop") or {}).get("ok"))
        time.sleep(7)
        A.playbackinfo_delay = A.item_delay = 0
        _report(b, A, m, want_state="idle")
        zs = [ps for ps in playing_posts(A) if Z[:6] in ps]
        if zs:
            EXTRA.append("Z got Playing after the stop: %s" % zs)
    run("held_stop_" + where, body)


def s_held_play2(where="pbi"):
    def body(b, A, B, pids):
        m = _setup(b, A, [X, Y])
        _slow(A, where)
        b.request("play", {"itemIds": [Z]})
        time.sleep(2.5)
        A.playbackinfo_delay = A.item_delay = 0
        log("play2", (b.request("play", {"itemIds": [Z]}) or {}).get("ok"))
        time.sleep(8)
        _report(b, A, m, want_state="playing", want_item=Z)
    run("held_play2_" + where, body)


def s_held_shutdown(where="pbi", how="shutdown"):
    def body(b, A, B, pids):
        m = _setup(b, A, [X, Y])
        _slow(A, where)
        b.request("play", {"itemIds": [Z]})
        time.sleep(2.5)
        _report(b, A, m, x_stopped=False)   # X's Stopped: the shutdown's
        return how
    run("held_shutdown_%s_%s" % (where, how), body)


def s_held_switch(where="pbi"):
    def body(b, A, B, pids):
        m = _setup(b, A, [X, Y])
        _slow(A, where)
        b.request("play", {"itemIds": [Z]})
        time.sleep(2.5)
        t0 = time.time()
        r = b.request("connect", cargs(B, TB, window=False), timeout=30)
        log("switch", (r or {}).get("ok"), "%.1fs" % (time.time() - t0))
        time.sleep(6)
        A.playbackinfo_delay = A.item_delay = 0
        _report(b, A, m, want_state="idle")
        play(b, [Z])
        time.sleep(2)
        log("B Playing", {k[-10:]: len(v) for k, v in playing_posts(B).items()})
    run("held_switch_" + where, body)


def s_held_load_fail():
    def body(b, A, B, pids):
        m = _setup(b, A, [X, Y])
        A.playbackinfo_delay = 3
        good = A.media_path
        b.request("play", {"itemIds": [Z]})
        time.sleep(1.0)
        A.media_path = GARBAGE      # Z's stream will not open
        time.sleep(8)
        A.media_path = good
        A.playbackinfo_delay = 0
        _report(b, A, m, want_state="error")
        log("stop", (b.request("stop") or {}).get("ok"))
        time.sleep(2)
        log("final", get_state(b).get("state"))
    run("held_load_fail", body)


def s_held_fetch_fail(last="0"):
    def body(b, A, B, pids):
        ids = [X] if last == "1" else [X, Y]
        m = _setup(b, A, ids)
        A.slow_items[Z] = 4
        A.item_fail.add(Z)
        b.request("play", {"itemIds": [Z]})
        time.sleep(8)
        A.slow_items.clear()
        A.item_fail.discard(Z)
        s = _report(b, A, m, y_may_start=True,
                    want_state=("idle" if last == "1" else "playing"),
                    want_item=(None if last == "1" else Y))
        log("final state", s.get("state"))
    run("held_fetch_fail_" + last, body, server_cls=ItemFailJF)


def s_held_last(end="shutdown", where="item", delay="5"):
    def body(b, A, B, pids):
        m = _setup(b, A, [X])
        if where == "pbi":
            A.playbackinfo_delay = float(delay)
        else:
            A.item_delay = float(delay)
        t0 = time.time()
        b.request("play", {"itemIds": [Z]})
        time.sleep(2.5)
        s = get_state(b)
        log("held: %s %s" % (s.get("state"), (s.get("itemId") or "")[:2]))
        if end == "stop":
            m2 = b.mark()
            asked = time.time()
            ok = (b.request("stop") or {}).get("ok")
            answered = time.time()
            log("stop", ok, "answered in %.2fs" % (answered - asked))
            if answered - asked > 1.0:
                EXTRA.append("stop answered in %.2fs" % (answered - asked))
            idle = b.wait_event("state", lambda d: d.get("state") == "idle",
                                30, m2)
            took = time.time() - answered
            log("idle after the stop: %s, %.1fs after its answer (%.1fs "
                "after the play)" % (bool(idle), took, time.time() - t0))
            if not idle or took > IDLE_AFTER_STOP:
                EXTRA.append("idle %.1fs after the stop's answer" % took)
            time.sleep(1)
            A.playbackinfo_delay = A.item_delay = 0
            stops = stopped_posts(A)
            log("Stopped", {k[-10:]: len(v["post"]) for k, v in stops.items()})
            log("events", evs(b, m))
            _report(b, A, m, want_state="idle")
            return "shutdown"
        return end
    run("held_last_%s_%s_%s" % (end, where, delay), body)


def _cpu(config_dir):
    """CPU seconds so far of each process whose command line names this
    run's own config dir (the backend's interpreters, its mpv), by
    ``name:pid``. Read-only; the query's own powershell is left out."""
    ps = ("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and "
          "$_.CommandLine.ToLower().Contains('%s') } | ForEach-Object { "
          "'{0}|{1}|{2}|{3}' -f $_.ProcessId, $_.Name, $_.UserModeTime, "
          "$_.KernelModeTime }" % config_dir.lower().replace("'", "''"))
    out = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                         capture_output=True, text=True, timeout=60).stdout
    rows = {}
    for line in out.splitlines():
        parts = line.split("|")
        if len(parts) == 4 and parts[1].lower() != "powershell.exe":
            rows["%s:%s" % (parts[1], parts[0])] = (
                int(parts[2] or 0) + int(parts[3] or 0)) / 1e7
    return rows


def _lines(path):
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.readlines()


def s_spin(delay="20", ids="2"):
    def body(b, A, B, pids):
        _setup(b, A, [X, Y] if ids == "2" else [X])
        A.item_delay = float(delay)
        c0 = _cpu(b.config_dir)
        n0 = len(_lines(b.log_file))
        t0 = time.time()
        b.request("play", {"itemIds": [Z]})
        time.sleep(float(delay) - 1)
        A.item_delay = 0      # Z's fetch already slept; spare trickplay
        c1 = _cpu(b.config_dir)
        lines = _lines(b.log_file)
        held = sum(1 for line in lines if "Holding the end" in line)
        log("over %.1fs: log +%d lines, 'Holding' %d" % (
            time.time() - t0, len(lines) - n0, held))
        if held > SPIN_HOLDS:
            EXTRA.append("held %d times" % held)
        if len(lines) - n0 > SPIN_LINES:
            EXTRA.append("log grew %d lines" % (len(lines) - n0))
        for k in sorted(c1):
            used = c1[k] - c0.get(k, 0.0)
            log("cpu %s: %.2fs" % (k, used))
            if used > SPIN_CPU:
                EXTRA.append("%s used %.1fs of CPU" % (k, used))
        time.sleep(3)
        s = get_state(b)
        log("after: %s %s" % (s.get("state"), (s.get("itemId") or "")[:2]))
    run("spin_%s_%s" % (delay, ids), body)


#: Every scenario once, in the order the before/after runs use.
ALL = ["spin:20,2", "spin:20,1", "held_last:shutdown,item,5",
       "held_last:eof,item,5", "held_last:stop,item,5",
       "held_last:shutdown,pbi,5", "held_stop:pbi", "held_stop:item",
       "held_play2:pbi", "held_play2:item", "held_shutdown:pbi",
       "held_shutdown:item", "held_shutdown:item,eof", "held_switch:pbi",
       "held_switch:item", "held_load_fail", "held_fetch_fail:0",
       "held_fetch_fail:1"]


if __name__ == "__main__":
    for w in sys.argv[1:] or ALL:
        name, _, arg = w.partition(":")
        try:
            globals()["s_" + name](*(arg.split(",") if arg else []))
        except Exception:
            import traceback
            traceback.print_exc()
