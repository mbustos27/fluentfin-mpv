"""Round-8 probes against the real bridge: the round-8 reviewer's scenario
(rr8probe in its scratch folder), moved here with its bounds. An end of file
held for a ``play`` (rr7) when mpv stops answering, then the shutdown. Fake
loopback servers (fakejf2), fake tokens only; run off-screen.

    RR_OUT=<scratch folder> RR_MKV=<small .mkv> rr8probe.py <scenario[:args]> ...

(``before_probe.py`` finds these too, for a run against an older backend.)
Each prints ``RESULT rr8 <name>: ok`` or the problems found: rr6probe's
per-session checks (one ``reported`` per session, none before its Stopped,
``sent`` only on a 2xx, ``none`` only for a session the server never heard
of; exactly one Stopped for every session with Playing) plus the scenario's
own. No arguments runs ``ALL``.

- ``held_hung[:shutdown|eof[,1|0]]``: X alone, seeked to its last seconds;
  ``play Z`` waits on a slow item fetch (``1``), so X's end is held; then
  the bridge's own mpv is suspended (only through ownproc: this run's mpv,
  by its config dir and creation time) and the shutdown asked for
  (``shutdown``) or stdin closed (``eof``). ``0``, the control: no
  ``play``, mpv suspended in X's last seconds, nothing held. Round 8: the
  shutdown's held end ran before its stop(), and each waited out one mpv
  reply (5 s): no answer, exit 1 at ~9.7 s, X ``reported`` never. Bounded
  here: exit 0 within `EXIT_BY` of the request, the ``shutdown`` answered,
  and X's one Stopped and ``reported``.
"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rr5probe as P  # noqa: E402  (puts backend/tools on sys.path)
import rr6probe as Q  # noqa: E402
import rr7probe as R7  # noqa: E402
from rr5probe import EXTRA, get_state  # noqa: E402
from rrprobe import X, Z, log, mpv_procs  # noqa: E402
from rr3probe import _suspend  # noqa: E402

#: From the shutdown request (or stdin's close) to the backend's exit: the
#: bridge's `SHUTDOWN_BUDGET` (8 s) and `EXIT_SLACK` (1 s). Its exit
#: watchdog fires at 9.5 s, with exit status 1 and no answer.
EXIT_BY = 9.0


def run(name, body, how="shutdown"):
    """`rr5probe.run` with the shutdown timed: exit status 0, the answer
    (``shutdown``), and the exit within `EXIT_BY` of the request. Patched
    for this run only, so nothing else run in the process is affected."""
    real = P.finish

    def finish(b, servers, tokens, how="shutdown"):
        exited = {}
        real_exit = b.finish

        def timed_exit(timeout=30):
            code = real_exit(timeout)
            exited["at"] = time.time()
            return code

        b.finish = timed_exit
        asked = time.time()
        code, at = real(b, servers, tokens, how)
        took = exited.get("at", time.time()) - asked
        log("exit %s %.2fs after the %s" % (code, took, how))
        if code != 0:
            EXTRA.append("exit status %s" % code)
        if took > EXIT_BY:
            EXTRA.append("exited %.2fs after the %s (bound %.1fs)" % (
                took, how, EXIT_BY))
        if how == "shutdown" and at is None:
            EXTRA.append("the shutdown was not answered")
        return code, at

    P.finish = finish
    try:
        P.run(name, body, how=how, label="rr8", check=Q.sessions_check)
    finally:
        P.finish = real


def _held_lines(b):
    with open(b.log_file, encoding="utf-8", errors="replace") as fh:
        return sum(1 for line in fh if "Holding the end" in line)


def s_held_hung(how="shutdown", hold="1"):
    def body(b, A, B, pids):
        R7._setup(b, A, [X])
        if hold == "1":
            A.item_delay = 15.0
            b.request("play", {"itemIds": [Z]})
            time.sleep(2.5)         # X reaches its end meanwhile: held
            if not _held_lines(b):
                EXTRA.append("X's end was not held before the suspend")
        s = get_state(b)
        log("before suspend: %s %s" % (s.get("state"),
                                       (s.get("itemId") or "")[:2]))
        found = mpv_procs(b.config_dir)
        if not found:
            EXTRA.append("no mpv of this run's to suspend")
        pids.extend(found)          # resumed by rr5probe.run's finally
        suspended = [_suspend(p) for p in found]
        log("suspend", suspended)
        if not all(suspended):
            EXTRA.append("mpv not suspended: %s" % suspended)
        time.sleep(1)
        return how
    run("held_hung_%s_%s" % (how, hold), body, how=how)


#: Every scenario once, in the order the before/after runs use.
ALL = ["held_hung:shutdown,1", "held_hung:eof,1", "held_hung:shutdown,0"]


if __name__ == "__main__":
    for w in sys.argv[1:] or ALL:
        name, _, arg = w.partition(":")
        try:
            globals()["s_" + name](*(arg.split(",") if arg else []))
        except Exception:
            import traceback
            traceback.print_exc()
