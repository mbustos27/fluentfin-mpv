"""Round-6 probes against the real bridge: the round-6 reviewer's scenarios
(rr6probe/rr6b/rr6c in its scratch folder), moved here with the checks the
round-6 fixes answer to. Fake loopback servers (fakejf2), fake tokens only;
run off-screen.

    RR_OUT=<scratch folder> RR_MKV=<small .mkv> rr6probe.py <scenario[:args]> ...

(``before_probe.py`` finds these too, for a run against an older backend.)
Each prints ``RESULT rr6 <name>: ok`` or the problems found -- rr5probe's
per-session checks (one ``reported`` per session, none before its Stopped,
``sent`` only on a 2xx, ``none`` only for a session the server never heard
of) plus, here: every session the server got Playing for gets exactly one
Stopped (or, abandoned by a restart -- a later PlaybackInfo for the same item
-- none and ``reported failed``: upstream never sends that one), and no
Stopped goes out for a session that never sent Playing.

- ``play_vs_advance[:seek,pbi]``: queue [X, Y], X seeked to its last
  seconds, ``play Z`` with a slow PlaybackInfo. Y must never start (no
  Playing), Z ends up playing.
- ``play_vs_advance_fail[:seek,pbi]``: the same, Z's PlaybackInfo answering
  500 after the delay: X's end is honoured -- the queue advances to Y.
- ``play_vs_advance_last[:seek,pbi]``: queue [X] only, Z failing the same
  way: X ends instead of sitting at its end.
- ``restart_no_url``, ``restart_no_source``: a track change whose
  PlaybackInfo answers with a source nothing can play, or none at all. The
  old session goes on (progress reports, its Stopped at the stop), ``state``
  keeps the subtitle actually showing, an ``error`` (``mpv_failed``) names
  the item, no ``state`` shows the session the failed negotiation was
  handed (the UI would drop the old one for it), and the ``stop`` ends it.
- ``restart_pbi_500``, ``next_pbi_500``: a PlaybackInfo 500 on a restart and
  on a next: ``server_unreachable``, never ``internal``, never "NoneType".
- ``restart_cancel_play``: a restart mpv is loading, cancelled by ``play
  Z``: X ends ``replaced``. ``restart_cancel_connect``.
- ``advance_shutdown[:shutdown|eof]``, ``next_then_play[:delay]``,
  ``next_pbi_then_next``: the reviewer's other round-6 probes.
"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rr5probe as P  # noqa: E402  (puts backend/tools on sys.path)
from rr5probe import (EXTRA, EncJF, evs, get_state, new_session,  # noqa: E402
                      play, playing_posts, progress_posts, stopped_posts)
from rrprobe import X, Y, Z, TB, cargs, log, wait_record  # noqa: E402


class NoSrcJF(EncJF):
    """PlaybackInfo for the items in ``nosrc`` answers a fresh session and
    no MediaSources (a server whose state changed under the item)."""

    def __init__(self, *a, **k):
        super().__init__(*a, **k)
        self.nosrc = set()

    def playback_info(self, item_id):
        info = super().playback_info(item_id)
        if item_id in self.nosrc:
            info["MediaSources"] = []
        return info


class NoUrlJF(EncJF):
    """PlaybackInfo for the items in ``nourl`` answers a source nothing can
    play (no direct play, no direct stream, no transcoding)."""

    def __init__(self, *a, **k):
        super().__init__(*a, **k)
        self.nourl = set()

    def playback_info(self, item_id):
        info = super().playback_info(item_id)
        if item_id in self.nourl:
            for src in info["MediaSources"]:
                src["SupportsDirectPlay"] = False
                src["SupportsDirectStream"] = False
                src["SupportsTranscoding"] = False
        return info


def _errors(b, since=0):
    return [(m.get("data") or {}) for m in b.messages[since:]
            if m.get("event") == "error"]


def _ended(b, since=0):
    return [(m.get("data") or {}) for m in b.messages[since:]
            if m.get("event") == "ended"]


def _item_of(session):
    """fakejf2's ids are ps-<server>-<item[:6]>-<n>."""
    parts = (session or "").split("-")
    return parts[2] if len(parts) > 3 else None


def sessions_check(b, A, B):
    """Every session the server got Playing for: exactly one Stopped -- or,
    abandoned by a restart (a later PlaybackInfo for the same item; upstream
    never sends that session's Stopped, BRIDGE.md section 5), none and
    ``reported failed``. No Stopped for a session that never sent
    Playing."""
    outcomes = {(m.get("data") or {}).get("playSessionId"):
                (m.get("data") or {}).get("outcome")
                for m in b.messages if m.get("event") == "reported"}
    for server in (A, B):
        plays = playing_posts(server)
        stops = stopped_posts(server)
        with server.lock:
            issued = list(server.issued)
        for ps in set(plays) | set(stops):
            n_play = len(plays.get(ps, []))
            n_stop = len(stops.get(ps, {"post": []})["post"])
            if n_stop > 1 or (n_play and n_stop == 0):
                later = issued[issued.index(ps) + 1:] if ps in issued else []
                restarted = any(_item_of(s) == _item_of(ps) for s in later)
                if not (n_stop == 0 and restarted
                        and outcomes.get(ps) == "failed"):
                    EXTRA.append("%s: Playing %d, Stopped %d" % (
                        ps, n_play, n_stop))
            if n_stop and not n_play:
                EXTRA.append("%s: Stopped %d without a Playing" % (ps,
                                                                   n_stop))


def run(name, body, server_cls=EncJF, how="shutdown", conf=None):
    P.run(name, body, conf=conf, server_cls=server_cls, how=how, label="rr6",
          check=sessions_check)


# -- item 1: a play racing the queue's own advance ---------------------------

def _play_vs_advance(name, ids, seek_s, pbi, fail_z, expect):
    def body(b, A, B, pids):
        play(b, ids); time.sleep(1.5)
        m = b.mark()
        b.request("seek", {"positionTicks": int(float(seek_s) * 10000000)})
        time.sleep(0.2)
        A.playbackinfo_delay = float(pbi)
        if fail_z:
            A.pbi_fail.add(Z)
        t0 = time.time()
        r = b.request("play", {"itemIds": [Z]})
        log("play answered", (r or {}).get("ok"))
        rec = wait_record(A, lambda r: r["t"] > t0
                          and "/Sessions/Playing/Stopped" in r["path"], 5)
        log("X Stopped at %.2fs" % ((rec["t"] - t0) if rec else -1))
        time.sleep(float(pbi) * 2 + 5)
        A.playbackinfo_delay = 0
        A.pbi_fail.discard(Z)
        s = get_state(b)
        log("after: %s %s %s" % (s.get("state"), (s.get("itemId") or "")[:2],
                                 s.get("playSessionId")))
        plays = playing_posts(A)
        log("Playing", {k[-8:]: len(v) for k, v in plays.items()})
        log("Stopped", {k[-8:]: len(v["post"])
                        for k, v in stopped_posts(A).items()})
        log("errors", [(e.get("code"), e.get("message")) for e in _errors(b, m)])
        log("events", evs(b, m))
        if expect == "Z":
            if s.get("itemId") != Z or s.get("state") != "playing":
                EXTRA.append("final %s %s, not Z playing" % (
                    s.get("state"), s.get("itemId")))
            started = [ps for ps in plays if Y[:6] in ps]
            if started:
                EXTRA.append("Y started (Playing for %s) though play Z owned "
                             "what came next" % started)
        elif expect == "Y":
            if s.get("itemId") != Y or s.get("state") != "playing":
                EXTRA.append("final %s %s: X's end was not honoured (Y not "
                             "playing)" % (s.get("state"), s.get("itemId")))
        else:
            if s.get("state") in ("playing", "paused"):
                EXTRA.append("final %s %s: X did not end" % (
                    s.get("state"), s.get("itemId")))
        if fail_z:
            codes = [e.get("code") for e in _errors(b, m)]
            if "server_unreachable" not in codes or "internal" in codes:
                EXTRA.append("Z's failure reported as %s" % codes)
    run(name, body)


def s_play_vs_advance(seek_s="118.5", pbi="4"):
    _play_vs_advance("play_vs_advance_%s_%s" % (seek_s, pbi), [X, Y], seek_s,
                     pbi, False, "Z")


def s_play_vs_advance_fail(seek_s="118.5", pbi="4"):
    _play_vs_advance("play_vs_advance_fail_%s_%s" % (seek_s, pbi), [X, Y],
                     seek_s, pbi, True, "Y")


def s_play_vs_advance_last(seek_s="118.5", pbi="4"):
    _play_vs_advance("play_vs_advance_last_%s_%s" % (seek_s, pbi), [X],
                     seek_s, pbi, True, "end")


# -- item 2: a restart that never got a usable stream ------------------------

def _restart_fail(name, server_cls, flag):
    def body(b, A, B, pids):
        st = play(b, [X]); time.sleep(2)
        old = st["playSessionId"]
        sub_before = st.get("subtitleStreamIndex")
        getattr(A, flag).add(X)
        m = b.mark()
        t_fail = time.time()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        time.sleep(1.5)
        getattr(A, flag).discard(X)
        time.sleep(11)                  # two progress ticks (5 s) at least
        s = get_state(b)
        new = s.get("playSessionId")
        log("state after: %s %s session %s sub %s (before %s)" % (
            s.get("state"), (s.get("itemId") or "")[:2], new,
            s.get("subtitleStreamIndex"), sub_before))
        progress = len(progress_posts(A, old, t_fail + 0.5))
        log("progress old after: %d, progress new: %d" % (
            progress, len(progress_posts(A, new or "-", 0))))
        errors = _errors(b, m)
        log("errors", [(e.get("code"), e.get("message")) for e in errors])
        log("events", evs(b, m))
        if s.get("state") != "playing" or new != old:
            EXTRA.append("after the failed change: %s on %s, not playing on "
                         "%s" % (s.get("state"), new, old))
        if s.get("subtitleStreamIndex") == 2:
            EXTRA.append("state shows subtitle 2, which is not on screen")
        if not progress:
            EXTRA.append("no progress report for %s after the failed change"
                         % old)
        # fluentfin-mpv: the message is for the user -- X by its title
        # (fakejf2 names it "Fake <first 4>"), never its id.
        if [e.get("code") for e in errors] != ["mpv_failed"] or not any(
                ("Fake %s" % X[:4]) in (e.get("message") or "")
                and X not in (e.get("message") or "") for e in errors):
            EXTRA.append("error events %s: want one mpv_failed naming X by "
                         "its title" % (
                             [(e.get("code"), e.get("message"))
                              for e in errors]))
        shown = sorted({(msg.get("data") or {}).get("playSessionId")
                        for msg in b.messages[m:] if msg.get("event") == "state"}
                       - {old, None})
        if shown:
            # The UI tracks the latest session shown and never reopens one
            # it has seen: the old one, which played on, lost its crash rule.
            EXTRA.append("state showed %s while mpv still played %s" % (
                shown, old))
        m2 = b.mark()
        log("stop", (b.request("stop") or {}).get("ok")); time.sleep(3)
        s = get_state(b)
        log("after stop: %s" % s.get("state"))
        if s.get("state") != "idle":
            EXTRA.append("state %s after the stop" % s.get("state"))
        stops = stopped_posts(A)
        log("Stopped posts", {k[-6:]: len(v["post"]) for k, v in stops.items()})
        log("Playing posts", {k[-6:]: len(v)
                              for k, v in playing_posts(A).items()})
        if len(stops.get(old, {"post": []})["post"]) != 1:
            EXTRA.append("old session %s: %d Stopped" % (
                old, len(stops.get(old, {"post": []})["post"])))
        log("events after stop", evs(b, m2))
    run(name, body, server_cls=server_cls)


def s_restart_no_url():
    _restart_fail("restart_no_url", NoUrlJF, "nourl")


def s_restart_no_source():
    _restart_fail("restart_no_source", NoSrcJF, "nosrc")


# -- item 7: a PlaybackInfo 500 is server_unreachable ------------------------

def _pbi_500(name, how):
    def body(b, A, B, pids):
        st = play(b, [X, Y]); time.sleep(2)
        old = st["playSessionId"]
        m = b.mark()
        A.pbi_fail.add(X if how == "restart" else Y)
        if how == "restart":
            log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        else:
            log("next", b.request("next"))
        time.sleep(3)
        A.pbi_fail.clear()
        errors = _errors(b, m)
        log("errors", [(e.get("code"), e.get("message")) for e in errors])
        s = get_state(b)
        log("state after: %s %s %s" % (s.get("state"),
                                       (s.get("itemId") or "")[:2],
                                       s.get("playSessionId")))
        log("events", evs(b, m))
        codes = [e.get("code") for e in errors]
        if codes != ["server_unreachable"]:
            EXTRA.append("error codes %s, want [server_unreachable]" % codes)
        if any("NoneType" in (e.get("message") or "") for e in errors):
            EXTRA.append("the message is a TypeError's")
        if how == "restart" and (s.get("state") != "playing"
                                 or s.get("playSessionId") != old):
            EXTRA.append("the restart left %s on %s" % (
                s.get("state"), s.get("playSessionId")))
        if how == "next" and s.get("state") != "error":
            EXTRA.append("a next that never opened left state %s" %
                         s.get("state"))
    run(name, body)


def s_restart_pbi_500():
    _pbi_500("restart_pbi_500", "restart")


def s_next_pbi_500():
    _pbi_500("next_pbi_500", "next")


# -- item 3 and the reviewer's other probes ----------------------------------

def s_restart_cancel_play():
    """Restart reaches mpv (slow stream), then a play Z cancels it."""
    def body(b, A, B, pids):
        st = play(b, [X, Y]); time.sleep(2)
        A.stream_delay[X] = 10
        m = b.mark()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        log("new session", bool(new_session(b, m, st["playSessionId"])))
        time.sleep(1.0)
        play(b, [Z])
        A.stream_delay.pop(X, None)
        time.sleep(3)
        s = get_state(b)
        log("after: state %s %s" % (s.get("state"), (s.get("itemId") or "")[:2]))
        if s.get("state") != "playing" or s.get("itemId") != Z:
            EXTRA.append("state %s %s, not playing Z" % (s.get("state"),
                                                         s.get("itemId")))
        reasons = [e.get("reason") for e in _ended(b, m)
                   if e.get("itemId") == X]
        log("X ended", reasons)
        if reasons != ["replaced"]:
            EXTRA.append("X ended %s, not replaced" % reasons)
        log("events", evs(b, m))
    run("restart_cancel_play", body)


def s_restart_cancel_connect():
    """Restart reaches mpv (slow stream), then a connect to B cancels it."""
    def body(b, A, B, pids):
        st = play(b, [X, Y]); time.sleep(2)
        A.stream_delay[X] = 10
        m = b.mark()
        log("set_subtitle", b.request("set_subtitle", {"index": 2}))
        log("new session", bool(new_session(b, m, st["playSessionId"])))
        time.sleep(1.0)
        t0 = time.time()
        r = b.request("connect", cargs(B, TB, window=False), timeout=30)
        log("switch", (r or {}).get("ok"), "%.1fs" % (time.time() - t0))
        A.stream_delay.pop(X, None)
        time.sleep(2)
        play(b, [Z]); time.sleep(2)
        s = get_state(b)
        log("after: state %s %s" % (s.get("state"), (s.get("itemId") or "")[:2]))
        log("events", evs(b, m))
    run("restart_cancel_connect", body)


def s_advance_shutdown(how="shutdown"):
    """X at its end advances to Y, whose stream is slow; shutdown while Y
    loads."""
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(1.5)
        A.stream_delay[Y] = 12
        t0 = time.time()
        b.request("seek", {"positionTicks": 1170000000})
        rec = wait_record(A, lambda r: r["t"] > t0
                          and "/Items/" + Y + "/PlaybackInfo" in r["path"], 20)
        log("Y PlaybackInfo", bool(rec))
        time.sleep(1.0)
        log("state", (get_state(b) or {}).get("state"))
        return how
    run("advance_shutdown_" + how, body)


def s_next_then_play(delay="1.0"):
    """next to a slow Y, then play Z while Y loads."""
    def body(b, A, B, pids):
        play(b, [X, Y]); time.sleep(2)
        A.stream_delay[Y] = 10
        m = b.mark()
        log("next", (b.request("next") or {}).get("ok"))
        time.sleep(float(delay))
        play(b, [Z])
        A.stream_delay.pop(Y, None)
        time.sleep(3)
        s = get_state(b)
        log("after: %s %s" % (s.get("state"), (s.get("itemId") or "")[:2]))
        if s.get("state") != "playing" or s.get("itemId") != Z:
            EXTRA.append("state %s %s" % (s.get("state"), s.get("itemId")))
        log("errors", [e.get("code") for e in _errors(b, m)])
        log("events", evs(b, m))
    run("next_then_play_" + delay, body)


def s_next_pbi_then_next():
    """X, Y, Z: next to Y fails (500), a second next sent right behind it."""
    def body(b, A, B, pids):
        play(b, [X, Y, Z]); time.sleep(2)
        A.pbi_fail.add(Y)
        A.playbackinfo_delay = 1.0
        m = b.mark()
        r1 = b.request("next")
        r2 = b.request("next")
        log("answers", (r1 or {}).get("ok"), (r2 or {}).get("ok"),
            ((r2 or {}).get("error") or {}).get("code"))
        time.sleep(4)
        A.pbi_fail.discard(Y)
        A.playbackinfo_delay = 0
        s = get_state(b)
        log("after: %s %s" % (s.get("state"), (s.get("itemId") or "")[:2]))
        log("events", evs(b, m))
        log("stop", (b.request("stop") or {}).get("ok")); time.sleep(2)
        log("final", get_state(b).get("state"))
    run("next_pbi_then_next", body)


#: Every scenario once, in the order the before/after runs use.
ALL = ["play_vs_advance", "play_vs_advance:118.8,3", "play_vs_advance_fail",
       "play_vs_advance_last", "restart_no_url", "restart_no_source",
       "restart_pbi_500", "next_pbi_500", "restart_cancel_play",
       "restart_cancel_connect", "advance_shutdown:shutdown",
       "advance_shutdown:eof", "next_then_play:1.0", "next_then_play:0.2",
       "next_pbi_then_next"]


if __name__ == "__main__":
    for w in sys.argv[1:] or ALL:
        name, _, arg = w.partition(":")
        try:
            globals()["s_" + name](*(arg.split(",") if arg else []))
        except Exception:
            import traceback
            traceback.print_exc()
