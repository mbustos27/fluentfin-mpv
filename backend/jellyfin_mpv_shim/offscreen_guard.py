"""Keeps the test suite's mpv windows off the user's desktop (Windows).

Importing ``player.py`` starts a real mpv with ``force-window``, and almost
every test module imports it. Upstream runs the suite under ``xvfb-run``;
Windows has no xvfb, so the suite runs on a private desktop inside a
kill-on-close job (``tools/backend-bench/run_suite.py`` and
``hidden_run.py``). Run any other way, the mpv windows open on the user's
desktop and stay there after the run: nothing ends them.

``jellyfin_mpv_shim/__init__.py`` calls `check` when ``unittest`` is loaded,
so a direct ``python -m unittest ...`` stops here, before any test module
gets as far as importing the player. The app never loads ``unittest``, so
this never runs in it. ``tools/bridge_smoke.py`` calls it for itself.

Stdlib only: ``bridge_smoke.py`` loads this file by path.
"""

import os
import sys

#: Set to 1 to run on the desktop anyway (watching a window test by eye).
OVERRIDE = "SEAGLASS_TESTS_ON_DESKTOP"

#: The exit status of a refused run.
EXIT_CODE = 3

_UOI_NAME = 2


def interactive_desktop():
    """Whether this thread draws on the user's own desktop (``WinSta0\\Default``).

    False on a private desktop (``hidden_run.py``), on a service's window
    station, and off Windows. True when the names cannot be read: a guard
    that cannot tell refuses.
    """
    if os.name != "nt":
        return False
    import ctypes
    from ctypes import wintypes as wt

    # Own instances, so these argtypes do not leak into ctypes.windll.
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    user32.GetProcessWindowStation.restype = wt.HANDLE
    user32.GetThreadDesktop.restype = wt.HANDLE
    user32.GetThreadDesktop.argtypes = [wt.DWORD]
    user32.GetUserObjectInformationW.argtypes = [
        wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD,
        ctypes.POINTER(wt.DWORD)]

    def name(handle):
        buf = ctypes.create_unicode_buffer(256)
        needed = wt.DWORD()
        if not handle or not user32.GetUserObjectInformationW(
                handle, _UOI_NAME, buf, ctypes.sizeof(buf),
                ctypes.byref(needed)):
            return None
        return buf.value.lower()

    station = name(user32.GetProcessWindowStation())
    desktop = name(user32.GetThreadDesktop(kernel32.GetCurrentThreadId()))
    if station is None or desktop is None:
        return True
    return station == "winsta0" and desktop == "default"


def message(what):
    return (
        "\n%s would open real mpv windows on your desktop, and they would "
        "stay open after the run.\n"
        "Run it off-screen instead (from the repository root):\n"
        "  backend\\.venv\\Scripts\\python.exe tools\\backend-bench\\run_suite.py "
        "--root backend --log <file> [--serial -- -k <test name>]\n"
        "  backend\\.venv\\Scripts\\python.exe tools\\backend-bench\\hidden_run.py "
        "--log <file> -- <command>\n"
        "To run it on the desktop anyway, set %s=1.\n" % (what, OVERRIDE))


def check(what, desktop=interactive_desktop, exit=os._exit, stream=None):
    """End this process (status `EXIT_CODE`) if it is on the user's desktop.

    `os._exit`, not `SystemExit`: unittest's loader catches anything a test
    module raises on import and goes on to the next module, which would
    import the player.
    """
    if os.environ.get(OVERRIDE) == "1" or not desktop():
        return
    stream = stream or sys.stderr
    try:
        sys.stdout.flush()
    except Exception:
        pass
    stream.write(message(what))
    stream.flush()
    exit(EXIT_CODE)
