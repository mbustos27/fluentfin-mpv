"""Round-5 probes: ``reported`` and the item left behind, against the real
bridge. Fake loopback servers (fakejf2), fake tokens only; run off-screen.

    RR_OUT=<scratch folder> RR_MKV=<small .mkv> rr5probe.py <scenario[:args]> ...

Scenarios (each prints ``RESULT rr5 <name>: ok`` or the problems found):

- ``restart_ok``, ``restart_cancel``, ``restart_cancel_queue``: a track
  change that restarts the stream (an Encode subtitle), plain, and stopped
  while it loads -- one item, and with a next item (no finished callback
  rescues it then).
- ``restart_pbi_fail``: that restart's PlaybackInfo answers 500. The old
  session must go on: no ``failed``, progress reports keep coming, and the
  stop sends its Stopped.
- ``restart_load_error[:single]``: the restart's stream does not open.
- ``restart_shutdown[:eof]``: a shutdown while the restart's PlaybackInfo
  hangs.
- ``next_load_error``, ``next_load_timeout_stop``, ``prev_load_error``,
  ``eof_advance_fail``, ``next_pbi_fail``: the target of a next, a previous
  or an auto-advance never opens (bad stream, a load timeout, a 500 from
  PlaybackInfo). ``state`` must go to ``error`` and a ``stop`` to ``idle``;
  the item before it must not stay current.
- ``play_load_error``, ``load_timeout_replace``, ``next_load_timeout``: the
  same for a ``play`` command, and the timeout without a stop.
- ``stop_play_next``, ``eof_advance``, ``switch_opening``,
  ``shutdown_opening[:eof]``, ``prev_reuse``: other ways a session ends.
- ``hung_mpv_stop[:shutdown|eof[,secs]]``: the Stopped reaches the server,
  then mpv is suspended; the ``reported`` must still come within ~2 s.
- ``hung_mpv_play[:eof]``: mpv suspended while playing, then a shutdown.

Every scenario checks, per play session the server issued: at most one
``reported``, one for every session shown/Playing/Stopped, none before its
Stopped reached the server, ``sent`` only after a 2xx, ``none`` only for a
session the server never heard of, and (with a ``shutdown``) none after the
answer.
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rrprobe as R  # noqa: E402  (puts backend/tools on sys.path)
from rrprobe import (TA, TB, UID, X, Y, Z, cargs, log, mpv_procs,  # noqa: E402
                     kill_pids, leaks, wait_record)
from rr3probe import _suspend  # noqa: E402
from rr4probe import burst, answer  # noqa: E402
from fakejf2 import FakeJF  # noqa: E402
from bridge_smoke import BridgeProcess  # noqa: E402

#: Not a media file: a stream mpv fails to open ("unrecognized file format").
GARBAGE = os.path.join(R.OUT, "rr5-garbage.bin")
if not os.path.exists(GARBAGE):
    with open(GARBAGE, "wb") as fh:
        fh.write(os.urandom(300000))

#: Problems a scenario body found itself (state checks), reported by
#: `analyse` with the rest.
EXTRA = []


class TBridge(BridgeProcess):
    """BridgeProcess that also records when each message arrived."""

    def _read(self):
        self.arrived = []
        for raw in self.proc.stdout:
            with self._cond:
                self.lines.append(raw)
                try:
                    text = raw.decode("utf-8")
                    if text.startswith("﻿") or not text.endswith("\n"):
                        raise ValueError("BOM or unterminated line")
                    msg = json.loads(text)
                    if not isinstance(msg, dict):
                        raise ValueError("not an object")
                    self.messages.append(msg)
                    self.arrived.append(time.time())
                except Exception as error:
                    self.not_json.append((raw, str(error)))
                self._cond.notify_all()
        with self._cond:
            self._cond.notify_all()


class EncJF(FakeJF):
    """Its sources carry one subtitle the server burns in (Encode): picking
    it restarts the stream (`Video.set_streams`), as a transcode track
    change does."""

    def source(self, item_id):
        s = super().source(item_id)
        s["MediaStreams"] = list(s["MediaStreams"]) + [
            {"Type": "Subtitle", "Index": 2, "Codec": "subrip",
             "Language": "eng", "IsExternal": False,
             "DeliveryMethod": "Encode", "IsDefault": False}]
        return s


def start(name, conf=None):
    b = TBridge(R.PY, R.OUT, name, dev=True, conf=conf)
    b.wait_event("hello", timeout=30)
    return b


def wait_state(b, st, since, timeout=40, pred=None):
    return b.wait_event("state", lambda d: d.get("state") == st and (
        pred is None or pred(d)), timeout, since)


def stopped_posts(server):
    out = {}
    with server.lock:
        for r in server.records:
            p = r["path"].split("?")[0]
            body = r["body"] if isinstance(r["body"], dict) else {}
            s = body.get("PlaySessionId")
            if p == "/Sessions/Playing/Stopped":
                e = out.setdefault(s, {"post": [], "done": []})
                if r["method"] == "POST":
                    e["post"].append(r["t"])
                elif r["method"] == "DONE":
                    e["done"].append((r["t"], r.get("status")))
    return out


def playing_posts(server):
    out = {}
    with server.lock:
        for r in server.records:
            p = r["path"].split("?")[0]
            body = r["body"] if isinstance(r["body"], dict) else {}
            if r["method"] == "POST" and p == "/Sessions/Playing":
                out.setdefault(body.get("PlaySessionId"), []).append(r["t"])
    return out


def progress_posts(server, session, since=0):
    with server.lock:
        return [r["t"] for r in server.records
                if r["method"] == "POST" and r["t"] >= since
                and r["path"].split("?")[0] == "/Sessions/Playing/Progress"
                and isinstance(r["body"], dict)
                and r["body"].get("PlaySessionId") == session]


def analyse(name, b, servers, answer_at=None, label="rr5"):
    """Per session: Playing, Stopped (receipt/answer), shown, reported; and
    the UI's rule (MpvBackendHost.TrackPlaySession) replayed over the
    stream: how long a crash would have meant a duplicate Stopped (open,
    Stopped already at the server) or a lost one (closed before its
    Stopped reached the server)."""
    msgs = list(b.messages)
    times = list(b.arrived)
    issued = []
    stops, plays = {}, {}
    for s in servers:
        issued += [(s.name, i) for i in s.issued]
        stops.update(stopped_posts(s))
        plays.update(playing_posts(s))
    rep = {}
    shown_first = {}
    shown_after = {}
    for i, m in enumerate(msgs):
        e = m.get("event")
        d = m.get("data") or {}
        if e == "reported":
            rep.setdefault(d.get("playSessionId"), []).append(
                (times[i], d.get("outcome"), i))
        elif e == "state" and d.get("playSessionId"):
            ps = d["playSessionId"]
            shown_first.setdefault(ps, times[i])
            if ps in rep:
                shown_after[ps] = shown_after.get(ps, 0) + 1
    problems = list(EXTRA)
    for srv, ps in issued:
        st = stops.get(ps, {"post": [], "done": []})
        r = rep.get(ps, [])
        log("   %s %s: Playing %d Stopped %d answered %s shown %s reported %s"
            " states-after-reported %d" % (
                srv, ps[-10:], len(plays.get(ps, [])), len(st["post"]),
                [x[1] for x in st["done"]], ps in shown_first,
                [(o, round(t - R.T0, 2)) for t, o, _ in r],
                shown_after.get(ps, 0)))
        if len(r) > 1:
            problems.append("%s: %d reported" % (ps, len(r)))
        if (plays.get(ps) or st["post"] or ps in shown_first) and not r:
            problems.append("%s: shown/Playing/Stopped but never reported" % ps)
        if r and st["post"] and r[0][0] < st["post"][0]:
            problems.append("%s: reported %.2fs BEFORE its Stopped reached the"
                            " server" % (ps, st["post"][0] - r[0][0]))
        if r and r[0][1] == "sent" and not any(
                200 <= (x[1] or 0) < 300 for x in st["done"]):
            problems.append("%s: sent without a 2xx" % ps)
        if r and r[0][1] == "none" and (plays.get(ps) or st["post"]):
            problems.append("%s: none, but the server got Playing/Stopped" % ps)
        if answer_at is not None and r and r[0][2] > answer_at:
            problems.append("%s: reported after the shutdown answer" % ps)
    # UI replay
    open_ps, seen = None, set()
    dup_open = {}
    for i, m in enumerate(msgs):
        t = times[i]
        e = m.get("event")
        d = m.get("data") or {}
        if open_ps is not None:
            st = stops.get(open_ps)
            if st and st["post"] and st["post"][0] <= t:
                first = dup_open.setdefault(open_ps, [st["post"][0], t])
                first[1] = t
        if e == "state":
            if d.get("state") in ("opening", "playing", "paused") and \
                    d.get("itemId") and d.get("playSessionId"):
                ps = d["playSessionId"]
                if ps not in seen:
                    seen.add(ps)
                    open_ps = ps
        elif e == "reported":
            ps = d.get("playSessionId")
            if open_ps == ps:
                open_ps = None
            seen.add(ps)
    for ps, (t_post, t_last) in dup_open.items():
        log("    UI: %s open %.2fs after its Stopped reached the server" % (
            ps[-10:], t_last - t_post))
    log("    UI: open at the end: %s" % (open_ps and open_ps[-10:]))
    log("RESULT %s %s: %s" % (label, name, "ok" if not problems else
                               "; ".join(problems)))


def finish(b, servers, tokens, how="shutdown"):
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
    time.sleep(1.0)
    left = mpv_procs(b.config_dir)
    log("end %s: answer %s exit %s at %.2fs, mpv left %s, leaks %s, not_json %s"
        % (how, (r or {}).get("ok") if r else "-", code, time.time() - t0,
           left, leaks(b, tokens), b.not_json[:1]))
    if b.not_json:
        EXTRA.append("stdout carried a line that is not a protocol message")
    if leaks(b, tokens):
        EXTRA.append("a token leaked")
    kill_pids(left)
    return code, at


def evs(b, since=0):
    out = []
    for i, m in enumerate(b.messages[since:], since):
        e = m.get("event")
        if e in (None, "position"):
            if e is None:
                out.append(("resp", m.get("id"), m.get("ok"),
                            (m.get("error") or {}).get("code")))
            continue
        d = m.get("data") or {}
        if e == "state":
            out.append(("state", d.get("state"), (d.get("itemId") or "")[:2],
                        (d.get("playSessionId") or "")[-4:]))
        elif e == "reported":
            out.append(("reported", (d.get("playSessionId") or "")[-4:],
                        d.get("outcome")))
        elif e in ("ended", "error", "item"):
            out.append((e, (d.get("itemId") or "")[:2],
                        d.get("reason") or d.get("code")))
    return out


def run(name, body, conf=None, server_cls=EncJF, how="shutdown", label="rr5",
        check=None):
    """``label`` names the round in the output (rr6probe reuses this);
    ``check(b, A, B)``, after the finish, adds its own problems to EXTRA."""
    log("==== %s" % label, name)
    del EXTRA[:]
    A = server_cls("A", TA, R.MKV, UID)
    B = server_cls("B", TB, R.MKV, UID)
    b = start("rr5-" + name, conf=conf)
    pids = []
    try:
        r = b.request("connect", cargs(A, TA), timeout=60)
        log("connect", (r or {}).get("ok"))
        how = body(b, A, B, pids) or how
        _code, at = finish(b, [A, B], [TA, TB], how)
        log("events", evs(b))
        if check is not None:
            check(b, A, B)
        analyse(name, b, [A, B], answer_at=at, label=label)
    except Exception:
        import traceback
        traceback.print_exc()
        log("RESULT %s %s: the probe itself failed" % (label, name))
    finally:
        for p in pids:
            _suspend(p, resume=True)
        A.stopped_delay = A.progress_delay = A.playbackinfo_delay = 0
        kill_pids(mpv_procs(b.config_dir))
        b.kill()


def play(b, ids, index=0):
    m = b.mark()
    args = {"itemIds": ids}
    if index:
        args["startIndex"] = index
    b.request("play", args)
    ok = wait_state(b, "playing", m, pred=lambda d: d.get("itemId") == ids[index])
    log("playing", ids[index][:2], bool(ok))
    return ok


def new_session(b, since, old, timeout=30):
    return b.wait_event("state", lambda d: d.get("playSessionId") and
                        d.get("playSessionId") != old, timeout, since)


def get_state(b):
    return (b.request("get_state") or {}).get("result") or {}


# -- scenarios ----------------------------------------------------------------

def s_restart_ok():
    def body(b, A, B, pids):
        st = play(b, [X]); time.sleep(2)
        m = b.mark()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        log("new session", new_session(b, m, st["playSessionId"]))
        time.sleep(3)
        log("stop", (b.request("stop") or {}).get("ok")); time.sleep(3)
    run("restart_ok", body)


def s_restart_cancel():
    def body(b, A, B, pids):
        st = play(b, [X]); time.sleep(2)
        A.stream_delay[X] = 10
        m = b.mark()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        log("new session", new_session(b, m, st["playSessionId"]))
        time.sleep(1.0)
        log("stop", (b.request("stop") or {}).get("ok")); time.sleep(4)
        A.stream_delay.pop(X, None)
        st = get_state(b)
        log("after stop: state %s" % st.get("state"))
        if st.get("state") != "idle":
            EXTRA.append("state %s after the stop, not idle" % st.get("state"))
    run("restart_cancel", body)


def s_restart_cancel_queue():
    """As restart_cancel, but X has a next item: mpv's abort for X then
    queues no finished callback, and nothing but the bridge ends X."""
    def body(b, A, B, pids):
        st = play(b, [X, Y]); time.sleep(2)
        A.stream_delay[X] = 10
        m = b.mark()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        log("new session", new_session(b, m, st["playSessionId"]))
        time.sleep(1.0)
        log("stop", (b.request("stop") or {}).get("ok")); time.sleep(4)
        A.stream_delay.pop(X, None)
        st = get_state(b)
        log("after stop: state %s %s" % (st.get("state"),
                                         (st.get("itemId") or "")[:2]))
        if st.get("state") != "idle":
            EXTRA.append("state %s after the stop, not idle" % st.get("state"))
    run("restart_cancel_queue", body)


def s_restart_pbi_fail():
    def body(b, A, B, pids):
        st = play(b, [X]); time.sleep(2)
        session = st and st.get("playSessionId")
        A.pbi_fail.add(X)
        t_fail = time.time()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        time.sleep(1)
        A.pbi_fail.discard(X)
        time.sleep(11)      # two progress ticks (5 s) at least
        st = get_state(b)
        log("state after", st.get("state"), st.get("playSessionId"))
        progress = progress_posts(A, session, t_fail + 0.5)
        log("progress reports for %s after the failure: %d" % (
            (session or "")[-10:], len(progress)))
        if st.get("playSessionId") != session:
            EXTRA.append("the state shows session %s, not %s" % (
                st.get("playSessionId"), session))
        if not progress:
            EXTRA.append("no progress report after the failed restart")
        if any(m.get("event") == "reported"
               and (m.get("data") or {}).get("playSessionId") == session
               for m in b.messages):
            EXTRA.append("%s reported before its stop" % session)
        log("stop", (b.request("stop") or {}).get("ok")); time.sleep(3)
        posts = stopped_posts(A).get(session, {"post": []})["post"]
        log("Stopped for %s: %d" % ((session or "")[-10:], len(posts)))
        if len(posts) != 1:
            EXTRA.append("%d Stopped for %s" % (len(posts), session))
    run("restart_pbi_fail", body)


def s_restart_load_error(variant="queue"):
    """The restart's stream does not open (a load error, not a timeout):
    the item cannot go on -- mpv has dropped its file."""
    def body(b, A, B, pids):
        ids = [X] if variant == "single" else [X, Y]
        play(b, ids); time.sleep(2)
        good = A.media_path
        A.media_path = GARBAGE
        m = b.mark()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        log("error", b.wait_event("error", None, 30, m))
        A.media_path = good
        _after_fail(b, "restart_load_error", X)
    run("restart_load_error_" + variant, body)


def s_restart_shutdown(how="shutdown"):
    def body(b, A, B, pids):
        play(b, [X]); time.sleep(2)
        A.playbackinfo_delay = 30
        t0 = time.time()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        log("PBI in flight", bool(wait_record(A, lambda r: r["t"] > t0 and
                                              "/PlaybackInfo" in r["path"], 10)))
        time.sleep(0.5)
        return how
    run("restart_shutdown_" + how, body)


def s_stop_play_next():
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(1.5)
        A.progress_delay = 3
        t0 = time.time()
        log("progress in flight", bool(wait_record(
            A, lambda r: r["t"] > t0 and "/Progress" in r["path"], 15)))
        time.sleep(0.3)
        ids = burst(b, ("stop", None), ("play", {"itemIds": [Z]}),
                    ("next", None))
        log("answers", [((answer(b, i, 30) or {}).get("ok"),
                         ((answer(b, i, 1) or {}).get("error") or {}).get("code"))
                        for i in ids])
        A.progress_delay = 0
        time.sleep(9)
        st = get_state(b)
        log("final", st.get("state"), (st.get("itemId") or "")[:2])
    run("stop_play_next", body)


def s_eof_advance():
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(1.5)
        m = b.mark()
        b.request("seek", {"positionTicks": 1170000000})
        log("item Y", bool(b.wait_event("item", lambda d: d.get("itemId") == Y,
                                        40, m)))
        time.sleep(3)
    run("eof_advance", body)


def s_load_timeout_replace():
    def body(b, A, B, pids):
        play(b, [X]); time.sleep(2)
        A.stream_delay[Y] = 12
        m = b.mark()
        b.request("play", {"itemIds": [Y]})
        log("error", b.wait_event("error", None, 30, m))
        time.sleep(3)
        A.stream_delay.pop(Y, None)
    run("load_timeout_replace", body, conf={"playback_timeout": 3})


def s_next_load_timeout():
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(2)
        A.stream_delay[Y] = 12
        m = b.mark()
        log("next", (b.request("next") or {}).get("ok"))
        log("error", b.wait_event("error", None, 30, m))
        time.sleep(3)
        A.stream_delay.pop(Y, None)
    run("next_load_timeout", body, conf={"playback_timeout": 3})


def s_switch_opening():
    def body(b, A, B, pids):
        play(b, [X]); time.sleep(2)
        A.stream_delay[Y] = 12
        m = b.mark()
        b.request("play", {"itemIds": [Y]})
        log("Y opening w/ session", bool(b.wait_event(
            "state", lambda d: d.get("state") == "opening" and
            d.get("itemId") == Y and d.get("playSessionId"), 20, m)))
        time.sleep(0.5)
        t0 = time.time()
        r = b.request("connect", cargs(B, TB, window=False), timeout=30)
        log("switch", (r or {}).get("ok"), "%.1fs" % (time.time() - t0))
        time.sleep(3)
        play(b, [Z]); time.sleep(2)
        A.stream_delay.pop(Y, None)
    run("switch_opening", body)


def s_shutdown_opening(how="shutdown"):
    def body(b, A, B, pids):
        play(b, [X]); time.sleep(2)
        A.stream_delay[Y] = 12
        m = b.mark()
        b.request("play", {"itemIds": [Y]})
        log("Y opening w/ session", bool(b.wait_event(
            "state", lambda d: d.get("state") == "opening" and
            d.get("itemId") == Y and d.get("playSessionId"), 20, m)))
        time.sleep(0.5)
        return how
    run("shutdown_opening_" + how, body)


def s_hung_mpv_stop(how="shutdown", after="3"):
    """X playing; the Stopped goes out (stop), then mpv is suspended before
    the monitor's next tick: the ``reported`` must still come within ~2 s
    of the server receiving that Stopped (BRIDGE.md section 5)."""
    def body(b, A, B, pids):
        play(b, [X]); time.sleep(2)
        pids.extend(mpv_procs(b.config_dir))
        m = b.mark()
        log("stop", (b.request("stop") or {}).get("ok"))
        t0 = time.time()
        rec = wait_record(A, lambda r: r["t"] > t0 - 1 and
                          "/Stopped" in r["path"], 5)
        log("Stopped at server", bool(rec))
        log("suspend", [_suspend(p) for p in pids])
        rep = b.wait_event("reported", None, float(after), m)
        found = b.wait_for(lambda msg: msg.get("event") == "reported", 0.1, m)
        if rec and found:
            latency = b.arrived[found[0]] - rec["t"]
            log("reported %.2fs after the server got the Stopped: %s" % (
                latency, rep))
            if latency > 2.0:
                EXTRA.append("reported %.2fs after the Stopped" % latency)
        else:
            log("reported within %ss of the suspend: %s" % (after, rep))
            EXTRA.append("no reported within %ss of the suspend" % after)
        return how
    run("hung_mpv_stop_%s_%s" % (how, after), body)


def s_hung_mpv_play(how="shutdown"):
    """X playing; mpv suspended; shutdown. The final Stopped needs mpv."""
    def body(b, A, B, pids):
        play(b, [X]); time.sleep(2)
        pids.extend(mpv_procs(b.config_dir))
        log("suspend", [_suspend(p) for p in pids])
        time.sleep(1)
        return how
    run("hung_mpv_play_" + how, body)


def s_prev_reuse():
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(1.5)
        m = b.mark()
        log("next", (b.request("next") or {}).get("ok"))
        b.wait_event("item", lambda d: d.get("itemId") == Y, 30, m)
        time.sleep(2)
        m = b.mark()
        log("previous", (b.request("previous") or {}).get("ok"))
        b.wait_event("item", lambda d: d.get("itemId") == X, 30, m)
        time.sleep(2)
    run("prev_reuse", body)


def _after_fail(b, label, target):
    """BRIDGE.md section 5: an item that never opened leaves ``state:
    "error"`` (also as the target of next/previous/advance -- the item
    before it does not stay current); a ``stop`` then goes ``idle``."""
    time.sleep(3)
    st = get_state(b)
    log("%s: state %s %s %s" % (label, st.get("state"),
                                (st.get("itemId") or "")[:2],
                                st.get("playSessionId")))
    if st.get("state") != "error":
        EXTRA.append("state %s (%s) after the failed start, not error" % (
            st.get("state"), (st.get("itemId") or "")[:2]))
    elif target and st.get("itemId") != target:
        EXTRA.append("the error names %s, not %s" % (st.get("itemId"),
                                                     target))
    m = b.mark()
    log("stop", (b.request("stop") or {}).get("ok"))
    time.sleep(3)
    st = get_state(b)
    log("%s after stop: state %s %s" % (label, st.get("state"),
                                        (st.get("itemId") or "")[:2]))
    if st.get("state") != "idle":
        EXTRA.append("state %s after the stop, not idle" % st.get("state"))
    log("events after stop", evs(b, m))


def s_next_load_error():
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(2)
        good = A.media_path
        A.media_path = GARBAGE
        m = b.mark()
        log("next", (b.request("next") or {}).get("ok"))
        log("error", b.wait_event("error", None, 30, m))
        A.media_path = good
        _after_fail(b, "next_load_error", Y)
    run("next_load_error", body)


def s_next_load_timeout_stop():
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(2)
        A.stream_delay[Y] = 12
        m = b.mark()
        log("next", (b.request("next") or {}).get("ok"))
        log("error", b.wait_event("error", None, 30, m))
        A.stream_delay.pop(Y, None)
        _after_fail(b, "next_load_timeout", Y)
    run("next_load_timeout_stop", body, conf={"playback_timeout": 3})


def s_next_pbi_fail():
    """The next item's PlaybackInfo answers 500: it never reaches mpv."""
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(2)
        A.pbi_fail.add(Y)
        m = b.mark()
        log("next", (b.request("next") or {}).get("ok"))
        log("error", b.wait_event("error", None, 30, m))
        A.pbi_fail.discard(Y)
        _after_fail(b, "next_pbi_fail", Y)
    run("next_pbi_fail", body)


def s_play_load_error():
    def body(b, A, B, pids):
        play(b, [X]); time.sleep(2)
        good = A.media_path
        A.media_path = GARBAGE
        m = b.mark()
        b.request("play", {"itemIds": [Y]})
        log("error", b.wait_event("error", None, 30, m))
        A.media_path = good
        _after_fail(b, "play_load_error", Y)
    run("play_load_error", body)


def s_eof_advance_fail():
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(2)
        good = A.media_path
        m = b.mark()
        b.request("seek", {"positionTicks": 1170000000})
        # the next item's stream is garbage once X has been read to its end
        time.sleep(0.8)
        A.media_path = GARBAGE
        log("error", b.wait_event("error", None, 30, m))
        A.media_path = good
        _after_fail(b, "eof_advance_fail", Y)
    run("eof_advance_fail", body)


def s_prev_load_error():
    def body(b, A, B, pids):
        play(b, [X, Y], 1); time.sleep(2)
        good = A.media_path
        A.media_path = GARBAGE
        m = b.mark()
        log("previous", (b.request("previous") or {}).get("ok"))
        log("error", b.wait_event("error", None, 30, m))
        A.media_path = good
        _after_fail(b, "prev_load_error", X)
    run("prev_load_error", body)


if __name__ == "__main__":
    for w in sys.argv[1:]:
        name, _, arg = w.partition(":")
        try:
            globals()["s_" + name](*(arg.split(",") if arg else []))
        except Exception:
            import traceback
            traceback.print_exc()
