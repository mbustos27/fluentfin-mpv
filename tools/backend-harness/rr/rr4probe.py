"""Round-4 scenarios against the real bridge (fake loopback servers, fake
tokens only). Run off-screen through hidden_run.py, like rrprobe.py:

    rr4probe.py <scenario[:arg,arg]> ...
    (RR_OUT=<scratch out dir>, RR_MKV=<a small .mkv>; before_probe.py runs
    these against an older backend copy too)

Scenarios (the round-4 reviewer's, extended):

- ``stop_then:<next|stop|connect_same|connect_other|eof|shutdown>[,secs]``:
  ``stop`` and a second command in one write; ``secs``: a progress report
  in flight that long first. ``next`` must be ``no_playback``.
- ``stop_play[:pbi,reps]``, ``next_stop_play[:pbi,reps]``: ``stop`` and
  ``play Z`` in one write; Z must end up playing.
- ``exits[:name]``: every way a play session ends, one bridge each. Per
  PlaySessionId the fake server handed out: Playing and Stopped requests
  (and how the Stopped was answered) against the ``reported`` events --
  exactly one per session that sent Playing (``sent`` iff its Stopped got a
  2xx), ``none`` or nothing for one that never did, none for ``play_file``,
  and every ``reported`` of a ``shutdown`` before its answer. Prints
  ``VERDICT exits <name>: ok`` or the mismatches.
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rrprobe as R  # noqa: E402
from rrprobe import (FakeJF, TA, TB, UID, X, Y, Z, cargs, log, sess,  # noqa: E402
                     evsum, wait_state, wait_record, start, mpv_procs,
                     kill_pids, leaks)


def burst(b, *cmds):
    """Several requests in ONE write, so the reader queues them back to
    back. Returns their ids."""
    ids, data = [], b""
    for cmd, args in cmds:
        with b._cond:
            b._next_id += 1
            rid = b._next_id
        msg = {"id": rid, "cmd": cmd}
        if args is not None:
            msg["args"] = args
        data += (json.dumps(msg) + "\n").encode()
        ids.append(rid)
    b.send_raw(data)
    return ids


def answer(b, rid, timeout=20):
    found = b.wait_for(lambda m: m.get("id") == rid, timeout)
    return found[1] if found else None


def counts(server, kind):
    out = {}
    for s in sess(server):
        if s[0] == kind:
            out[s[1]] = out.get(s[1], 0) + 1
    return out


def reported(b):
    return b.events("reported")


def end(b, servers, tokens, how="shutdown"):
    """Shut the bridge down; returns (exit code, index of the shutdown
    answer in ``b.messages`` or None)."""
    t0 = time.time()
    at = None
    if how == "eof":
        b.proc.stdin.close()
        r = None
    else:
        mark = b.mark()
        r = b.request("shutdown", timeout=40)
        if r is not None:
            found = b.wait_for(lambda m: m.get("id") == r.get("id"), 1, mark)
            at = found[0] if found else None
    code = b.finish(60)
    time.sleep(1.5)
    left = mpv_procs(b.config_dir)
    log("end %s: answer %s exit %s at %.2fs, mpv left %s, leaks %s, "
        "not_json %s" % (how, (r or {}).get("ok") if r else "-", code,
                         time.time() - t0, left, leaks(b, tokens),
                         b.not_json[:1]))
    kill_pids(left)
    return code, at


# ---------------------------------------------------------------------------
# The reviewer's scenarios


def s_stop_play(pbi="0", reps="1"):
    """X playing; `stop` and `play Z` in one write. Z must end up playing;
    X Stopped once; Z not Stopped until the shutdown."""
    for rep in range(int(reps)):
        log("==== stop_play pbi=%s rep %d" % (pbi, rep))
        A = FakeJF("A", TA, R.MKV, UID)
        b = start("rr4-stop-play-%s-%d" % (pbi, rep))
        try:
            b.request("connect", cargs(A, TA), timeout=60)
            m = b.mark()
            b.request("play", {"itemIds": [X]})
            wait_state(b, "playing", m)
            time.sleep(1.5)
            A.playbackinfo_delay = float(pbi)
            m = b.mark()
            ids = burst(b, ("stop", None), ("play", {"itemIds": [Z]}))
            log("answers", [(answer(b, i) or {}).get("ok") for i in ids])
            time.sleep(6 + float(pbi))
            st = (b.request("get_state") or {}).get("result") or {}
            log("RESULT stop_play %s/%d: final %s %s; Stopped %s; Playing %s"
                % (pbi, rep, st.get("state"), (st.get("itemId") or "")[:2],
                   counts(A, "Stopped"), counts(A, "Playing")))
            log("events", evsum(b, m))
            end(b, [A], [TA])
            log("after shutdown Stopped", counts(A, "Stopped"))
        finally:
            A.playbackinfo_delay = 0
            kill_pids(mpv_procs(b.config_dir))
            b.kill()


def s_next_stop_play(pbi="2", reps="1"):
    """[X, Y] playing; a slow PlaybackInfo keeps `next` on the player's
    lock; then `stop` + `play Z` in one write, Z's PlaybackInfo fast. The
    deferred stop must not kill Z."""
    for rep in range(int(reps)):
        log("==== next_stop_play pbi=%s rep %d" % (pbi, rep))
        A = FakeJF("A", TA, R.MKV, UID)
        b = start("rr4-nsp-%s-%d" % (pbi, rep))
        try:
            b.request("connect", cargs(A, TA), timeout=60)
            m = b.mark()
            b.request("play", {"itemIds": [X, Y]})
            wait_state(b, "playing", m)
            time.sleep(1.5)
            A.playbackinfo_delay = float(pbi)
            t0 = time.time()
            b.request("next")
            wait_record(A, lambda r: r["t"] > t0
                        and "/PlaybackInfo" in r["path"], 10)
            A.playbackinfo_delay = 0
            time.sleep(0.3)
            m = b.mark()
            ids = burst(b, ("stop", None), ("play", {"itemIds": [Z]}))
            log("answers", [(answer(b, i) or {}).get("ok") for i in ids])
            time.sleep(float(pbi) + 6)
            st = (b.request("get_state") or {}).get("result") or {}
            log("RESULT next_stop_play %s/%d: final %s %s; Stopped %s; "
                "Playing %s" % (pbi, rep, st.get("state"),
                                (st.get("itemId") or "")[:2],
                                counts(A, "Stopped"), counts(A, "Playing")))
            log("events", evsum(b, m))
            end(b, [A], [TA])
            log("after shutdown Stopped", counts(A, "Stopped"))
        finally:
            A.playbackinfo_delay = 0
            kill_pids(mpv_procs(b.config_dir))
            b.kill()


def s_stop_then(then="next", progress="0"):
    """X playing ([X, Y] for next); `stop` and `then` in one write;
    ``progress``: a progress report in flight that long (report lock
    held)."""
    log("==== stop_then", then, progress)
    A = FakeJF("A", TA, R.MKV, UID)
    B = FakeJF("B", TB, R.MKV, UID)
    b = start("rr4-stop-then-%s-%s" % (then, progress))
    try:
        b.request("connect", cargs(A, TA), timeout=60)
        m = b.mark()
        b.request("play", {"itemIds": [X, Y] if then == "next" else [X]})
        wait_state(b, "playing", m)
        time.sleep(1.5)
        if float(progress):
            A.progress_delay = float(progress)
            t_hang = time.time()
            log("progress in flight", bool(wait_record(
                A, lambda r: r["t"] > t_hang and "/Progress" in r["path"],
                15)))
            time.sleep(0.3)
        m = b.mark()
        t0 = time.time()
        second = {"next": ("next", None), "stop": ("stop", None),
                  "connect_same": ("connect", cargs(A, TA, window=False)),
                  "connect_other": ("connect", cargs(B, TB, window=False)),
                  "shutdown": ("shutdown", None)}.get(then)
        if then == "eof":
            ids = burst(b, ("stop", None))
            b.proc.stdin.close()
        else:
            ids = burst(b, ("stop", None), second)
        log("answers", [((answer(b, i, 30) or {}).get("ok"),
                         (answer(b, i, 1) or {}).get("error"))
                        for i in ids], "in %.2fs" % (time.time() - t0))
        if then in ("eof", "shutdown"):
            code = b.finish(60)
            time.sleep(float(progress) + 1.5)
            log("RESULT stop_then %s %s: exit %s at %.2fs; A Stopped %s; "
                "reported %s; mpv left %s" % (
                    then, progress, code, time.time() - t0,
                    counts(A, "Stopped"),
                    [(d["itemId"][:2], d["outcome"]) for d in reported(b)],
                    mpv_procs(b.config_dir)))
            log("events", evsum(b, m))
            return
        A.progress_delay = 0
        time.sleep(float(progress) + 5)
        st = (b.request("get_state") or {}).get("result") or {}
        log("RESULT stop_then %s %s: final %s %s conn %s; A Stopped %s "
            "Playing %s; B %s; reported %s" % (
                then, progress, st.get("state"),
                (st.get("itemId") or "")[:2], st.get("connected"),
                counts(A, "Stopped"), counts(A, "Playing"),
                counts(B, "Playing"),
                [(d["itemId"][:2], d["outcome"]) for d in reported(b)]))
        log("A sess", sess(A))
        log("events", evsum(b, m))
        end(b, [A, B], [TA, TB])
        log("after end: A Stopped %s, B Stopped %s" % (
            counts(A, "Stopped"), counts(B, "Stopped")))
    finally:
        A.progress_delay = 0
        kill_pids(mpv_procs(b.config_dir))
        b.kill()


# ---------------------------------------------------------------------------
# exits: reported against the server, per play session


def _backend_pids(config_dir):
    ps = ("Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | "
          "ForEach-Object { '{0}|{1}' -f $_.ProcessId, $_.CommandLine }")
    out = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                         capture_output=True, text=True, timeout=60).stdout
    return [int(line.split("|", 1)[0]) for line in out.splitlines()
            if "|" in line and config_dir.lower() in line.lower()
            and "jellyfin_mpv_shim.bridge" in line]


def book(server):
    """Per PlaySessionId this server handed out: its item, Playing and
    Stopped requests, and the statuses the Stopped ones were answered
    with."""
    with server.lock:
        issued = list(server.issued)
        records = list(server.records)
    out = {s: {"item": None, "playing": 0, "stopped": 0, "answered": []}
           for s in issued}
    for r in records:
        body = r["body"] if isinstance(r["body"], dict) else {}
        session = body.get("PlaySessionId")
        if session not in out:
            continue
        path = r["path"].split("?")[0]
        entry = out[session]
        entry["item"] = entry["item"] or (body.get("ItemId") or "")[:2]
        if r["method"] == "POST" and path == "/Sessions/Playing":
            entry["playing"] += 1
        elif r["method"] == "POST" and path == "/Sessions/Playing/Stopped":
            entry["stopped"] += 1
        elif r["method"] == "DONE" and path == "/Sessions/Playing/Stopped":
            entry["answered"].append(r.get("status", 204))
    return out


def verdict(name, b, servers, crashed=False, answer_at=None,
            expect=None):
    """Compare each session's requests with its ``reported`` events."""
    events = reported(b)
    by_session = {}
    for i, m in enumerate(b.messages):
        if m.get("event") == "reported":
            by_session.setdefault(m["data"]["playSessionId"], []).append(
                (i, m["data"]))
    shown = {(m.get("data") or {}).get("playSessionId")
             for m in b.messages if m.get("event") == "state"}
    problems, rows = [], []
    known = set()
    for server in servers:
        for session, e in book(server).items():
            known.add(session)
            got = by_session.get(session, [])
            outcomes = [d["outcome"] for _i, d in got]
            rows.append("%s %s: Playing %d Stopped %d answered %s shown %s "
                        "-> reported %s" % (
                            server.name, session[-8:], e["playing"],
                            e["stopped"], e["answered"],
                            session in shown, outcomes))
            if crashed:
                if outcomes:
                    problems.append("%s: reported after a crash" % session)
                continue
            if len(outcomes) > 1:
                problems.append("%s: %d reported" % (session, len(outcomes)))
            if e["playing"]:
                if len(outcomes) != 1:
                    problems.append("%s: Playing sent, %d reported" % (
                        session, len(outcomes)))
                    continue
                accepted = any(200 <= s < 300 for s in e["answered"])
                want = "sent" if accepted else "failed"
                if outcomes[0] != want:
                    problems.append("%s: %s, its Stopped answered %s" % (
                        session, outcomes[0], e["answered"]))
            elif outcomes and outcomes[0] != "none":
                problems.append("%s: never sent Playing, reported %s" % (
                    session, outcomes[0]))
            elif session in shown and not outcomes:
                problems.append("%s: shown, never sent Playing, no "
                                "reported" % session)
            if answer_at is not None:
                late = [i for i, _d in got if i > answer_at]
                if late:
                    problems.append("%s: reported after the shutdown "
                                    "answer" % session)
    for session in by_session:
        if session not in known:
            problems.append("%s: reported, not a session of ours" % session)
    if expect is not None:
        outcomes = sorted(d["outcome"] for d in events)
        if outcomes != sorted(expect):
            problems.append("outcomes %s, expected %s" % (outcomes,
                                                          sorted(expect)))
    for row in rows:
        log("   ", row)
    log("RESULT exits %s: %d reported %s" % (
        name, len(events), [(d["itemId"][:2], d["outcome"]) for d in events]))
    log("VERDICT exits %s: %s" % (name, "ok" if not problems
                                  else "; ".join(problems)))


def s_exits(which="all"):
    """One ``reported`` per play session, matching what the server got,
    whatever ends it."""
    plans = {
        "stop": ([("play", [X]), ("stop",)], ["sent"]),
        "replace": ([("play", [X]), ("play", [Y]), ("end", "shutdown")],
                    ["sent", "sent"]),
        "next": ([("play", [X, Y]), ("next",), ("end", "eof")],
                 ["sent", "sent"]),
        "previous": ([("play", [X, Y], 1), ("previous",),
                      ("end", "shutdown")], ["sent", "sent"]),
        "eof": ([("play", [X]), ("seek_end",), ("end", "shutdown")],
                ["sent"]),
        "switch": ([("play", [X]), ("switch",), ("playB", [Z]),
                    ("end", "shutdown")], ["sent", "sent"]),
        "switch_hung": ([("play", [X]), ("hang_stopped",), ("switch",),
                         ("playB", [Z]), ("end", "shutdown")],
                        ["failed", "sent"]),
        "replay": ([("play", [X]), ("stop",), ("play", [X]),
                    ("end", "shutdown")], ["sent", "sent"]),
        "replace_eof": ([("play", [X]), ("play", [Y]), ("end", "eof")],
                        ["sent", "sent"]),
        "shutdown": ([("play", [X]), ("end", "shutdown")], ["sent"]),
        "shutdown_hung": ([("play", [X]), ("hang_stopped",),
                           ("end", "shutdown")], ["failed"]),
        "status500": ([("status", 500), ("play", [X]), ("stop",)],
                      ["failed"]),
        "never_opened": ([("play", [X]), ("slow_stream", Y),
                          ("play_opening", [Y]), ("stop",)],
                         ["sent", "none"]),
        "play_file": ([("play_file",), ("stop",), ("end", "shutdown")], []),
        "mpvkill": ([("play", [X]), ("kill_mpv",), ("end", "shutdown")],
                    ["sent"]),
        "crash": ([("play", [X]), ("crash",)], None),
    }
    names = list(plans) if which == "all" else which.split("+")
    for name in names:
        steps, expect = plans[name]
        log("==== exits", name)
        A = FakeJF("A", TA, R.MKV, UID)
        B = FakeJF("B", TB, R.MKV, UID)
        b = start("rr4-exits-" + name)
        ended = crashed = False
        answer_at = None
        try:
            b.request("connect", cargs(A, TA), timeout=60)
            for step in steps:
                kind = step[0]
                if kind in ("play", "playB"):
                    args = {"itemIds": step[1]}
                    index = step[2] if len(step) > 2 else 0
                    if index:
                        args["startIndex"] = index
                    m = b.mark()
                    b.request("play", args)
                    b.wait_event("item", lambda d, i=step[1][index]:
                                 d.get("itemId") == i, 40, m)
                    time.sleep(2.5)
                elif kind == "play_opening":
                    m = b.mark()
                    b.request("play", {"itemIds": step[1]})
                    b.wait_event("state", lambda d: d.get("state")
                                 == "opening" and d.get("playSessionId"),
                                 20, m)
                    time.sleep(0.5)
                elif kind in ("next", "previous"):
                    m = b.mark()
                    log(kind, (b.request(kind) or {}).get("ok"))
                    b.wait_event("item", None, 40, m)
                    time.sleep(2.5)
                elif kind == "seek_end":
                    m = b.mark()
                    b.request("seek", {"positionTicks": 1160000000})
                    log("ended", b.wait_event("ended", None, 30, m))
                    time.sleep(1.5)
                elif kind == "stop":
                    log("stop", (b.request("stop") or {}).get("ok"))
                    time.sleep(2.5)
                elif kind == "switch":
                    t0 = time.time()
                    r = b.request("connect", cargs(B, TB, window=False),
                                  timeout=30)
                    log("switch", (r or {}).get("ok"),
                        "in %.1fs" % (time.time() - t0))
                    time.sleep(3.0)     # the old server's grace
                elif kind == "hang_stopped":
                    A.stopped_delay = 30.0
                elif kind == "status":
                    A.stopped_status = step[1]
                elif kind == "slow_stream":
                    A.stream_delay[step[1]] = 8.0
                elif kind == "play_file":
                    m = b.mark()
                    b.request("play_file", {"url": "av://lavfi:testsrc"})
                    wait_state(b, "playing", m, timeout=20)
                    time.sleep(1.5)
                elif kind == "kill_mpv":
                    pids = mpv_procs(b.config_dir)
                    log("killing mpv", pids)
                    kill_pids(pids)
                    time.sleep(4)
                elif kind == "end":
                    _code, answer_at = end(b, [A, B], [TA, TB], step[1])
                    ended = True
                elif kind == "crash":
                    pids = _backend_pids(b.config_dir)
                    log("killing the backend interpreter", pids)
                    kill_pids(pids)
                    b.finish(20)
                    time.sleep(1.5)
                    ended = crashed = True
                    log("mpv left after the crash",
                        mpv_procs(b.config_dir))
            if not ended:
                _code, answer_at = end(b, [A, B], [TA, TB])
            time.sleep(1)
            verdict(name, b, [A, B], crashed=crashed, answer_at=answer_at,
                    expect=expect)
        finally:
            A.stopped_delay = 0
            kill_pids(mpv_procs(b.config_dir))
            b.kill()


if __name__ == "__main__":
    for w in sys.argv[1:]:
        name, _, arg = w.partition(":")
        try:
            globals()["s_" + name](*(arg.split(",") if arg else []))
        except Exception:
            import traceback
            traceback.print_exc()
