"""rrprobe / rr3probe / rr4probe / rr5probe / rr6probe / rr7probe / rr8probe
scenarios against another copy of the backend.

    BEFORE_DIR=<folder holding jellyfin_mpv_shim> before_probe.py <scenario[:args]> ...

The bridge child is started from BEFORE_DIR instead of the installed package,
so the same scenario gives before/after numbers. A scenario is looked up in
rrprobe first, then rr3probe, then rr4probe, then rr5probe, then rr6probe,
then rr7probe, then rr8probe."""
import os, subprocess, sys
HERE = os.path.dirname(os.path.abspath(__file__))
BEFORE = os.environ.get("BEFORE_DIR") or sys.exit("set BEFORE_DIR to a folder holding a pre-fix copy of backend/ (with jellyfin_mpv_shim inside)")
sys.path.insert(0, HERE)
import os as _os  # noqa: E402
_REPO = _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))  # the repository
sys.path.insert(0, _os.path.join(_REPO, "backend", "tools"))
import bridge_smoke  # noqa: E402

_Popen = subprocess.Popen


def popen(argv, *a, **kw):
    if "-m" in argv and "jellyfin_mpv_shim.bridge" in argv:
        i = argv.index("-m")
        code = ("import sys; sys.path.insert(0, %r); "
                "from jellyfin_mpv_shim.bridge import main; main()" % BEFORE)
        argv = argv[:i] + ["-c", code] + argv[i + 2:]
    return _Popen(argv, *a, **kw)


bridge_smoke.subprocess.Popen = popen
import rrprobe  # noqa: E402
import rr3probe  # noqa: E402
import rr4probe  # noqa: E402
import rr5probe  # noqa: E402
import rr6probe  # noqa: E402
import rr7probe  # noqa: E402
import rr8probe  # noqa: E402

for w in sys.argv[1:]:
    name, _, arg = w.partition(":")
    fn = (getattr(rrprobe, "s_" + name, None)
          or getattr(rr3probe, "s_" + name, None)
          or getattr(rr4probe, "s_" + name, None)
          or getattr(rr5probe, "s_" + name, None)
          or getattr(rr6probe, "s_" + name, None)
          or getattr(rr7probe, "s_" + name, None)
          or getattr(rr8probe, "s_" + name))
    try:
        fn(*(arg.split(",") if arg else []))
    except Exception:
        import traceback; traceback.print_exc()
