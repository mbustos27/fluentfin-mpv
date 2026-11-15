"""How long mpv_events.wait_property takes to notice `satisfied_by` / `abort`.

    python bench_wait_property.py --shim-root <checkout> [--trials 20]

No mpv: a fake jsonipc-shaped instance whose property never satisfies the
condition (a live stream never reports `duration`), and a timer that sets the
`satisfied_by` Event (what `_on_file_loaded` does) -- or `abort` (what a load
error does) -- at a random moment. Reports the delay from the set() to
wait_property returning. Today both are seen only by the poll thread
(POLL_INTERVAL_SECS), so the expectation is uniform in [0, 0.5 s].
"""

import argparse
import random
import statistics
import sys
import threading
import time


class FakeExtMpv:
    """Shaped like python_mpv_jsonipc.MPV for wait_property's purposes."""

    def __init__(self):
        self._next = 1

    def bind_property_observer(self, name, cb):
        self._next += 1
        return self._next

    def unbind_property_observer(self, oid):
        pass

    @property
    def duration(self):
        return None


def trial(wait_property, which):
    ev = threading.Event()
    inst = FakeExtMpv()
    delay = random.uniform(0.05, 0.6)
    fired = {}

    def fire():
        time.sleep(delay)
        fired["t"] = time.perf_counter()
        ev.set()

    threading.Thread(target=fire, daemon=True).start()
    kw = {"satisfied_by": ev} if which == "satisfied_by" else {"abort": ev}
    ok = wait_property(inst, "duration", lambda x: x is not None, 30,
                       skip_initial=True, **kw)
    return (time.perf_counter() - fired["t"]) * 1000, ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shim-root", required=True)
    ap.add_argument("--trials", type=int, default=20)
    a = ap.parse_args()
    sys.argv = [sys.argv[0]]
    sys.path.insert(0, a.shim_root)
    from jellyfin_mpv_shim.mpv_events import wait_property, POLL_INTERVAL_SECS
    print("POLL_INTERVAL_SECS =", POLL_INTERVAL_SECS)
    for which in ("satisfied_by", "abort"):
        lat = []
        for _ in range(a.trials):
            ms, ok = trial(wait_property, which)
            lat.append(ms)
        lat.sort()
        print("%-12s n=%d  median %.0f ms  mean %.0f ms  p90 %.0f ms  max %.0f ms"
              "  (returned %s)" % (which, len(lat), statistics.median(lat),
                                   statistics.mean(lat),
                                   lat[int(len(lat) * 0.9) - 1], lat[-1], ok))


if __name__ == "__main__":
    main()
