"""Run the shim's unit suite on Windows, off-screen, in a sandboxed environment.

    python run_suite.py --root <shim checkout or backend/> --log <file> [--serial] [-- extra args]

* Off-screen: through hidden_run.py (a private desktop + kill-on-close job), so
  the real mpv windows that importing player.py opens never reach the user's
  desktop, and any mpv a test leaves behind dies with the run.
* mpv on PATH: the winget mpv-CI dir is prepended (this session's PATH predates
  that install). Override with --mpv-dir.
* APPDATA / LOCALAPPDATA point at a scratch dir, so neither the shim's config
  (%APPDATA%\\jellyfin-mpv-shim) nor mpv's (%APPDATA%\\mpv) on this machine is
  read or written.

Default: tools/run_tests_parallel.py (one process per module). --serial runs
`python -m unittest discover tests` instead (the cross-module leg).
"""

import argparse
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from hidden_run import run_hidden  # noqa: E402

DEFAULT_MPV = os.path.expandvars(
    r"%LOCALAPPDATA%\Microsoft\WinGet\Packages"
    r"\mpv-player.mpv-CI.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--log", required=True)
    ap.add_argument("--python", default=sys.executable)
    ap.add_argument("--mpv-dir", default=DEFAULT_MPV)
    ap.add_argument("--sandbox", default=os.path.join(os.path.dirname(HERE), "env"))
    ap.add_argument("--serial", action="store_true")
    ap.add_argument("--timeout", type=float, default=3600)
    ap.add_argument("extra", nargs=argparse.REMAINDER)
    a = ap.parse_args()
    extra = a.extra[1:] if a.extra[:1] == ["--"] else a.extra

    appdata = os.path.join(a.sandbox, "appdata")
    local = os.path.join(a.sandbox, "localappdata")
    os.makedirs(appdata, exist_ok=True)
    os.makedirs(local, exist_ok=True)
    env = dict(os.environ)
    env["PATH"] = a.mpv_dir + os.pathsep + env.get("PATH", "")
    env["APPDATA"] = appdata
    env["LOCALAPPDATA"] = local
    env["PYTHONIOENCODING"] = "utf-8"
    env["PYTHONUTF8"] = "1"

    if a.serial:
        cmd = [a.python, "-m", "unittest", "discover", "tests"] + extra
    else:
        cmd = [a.python, os.path.join("tools", "run_tests_parallel.py")] + extra
    with open(a.log, "w", encoding="utf-8") as f:
        f.write("command: %s\ncwd: %s\nstarted: %s\n\n"
                % (" ".join(cmd), a.root, time.strftime("%Y-%m-%d %H:%M:%S")))
    rc, secs = run_hidden(cmd, log=a.log, env=env, timeout=a.timeout, cwd=a.root)
    with open(a.log, "a", encoding="utf-8") as f:
        f.write("\nrun_suite: exit %d, wall %.1fs, finished %s\n"
                % (rc, secs, time.strftime("%Y-%m-%d %H:%M:%S")))
    print("exit %d, wall %.1fs" % (rc, secs))
    return rc


if __name__ == "__main__":
    sys.exit(main())
