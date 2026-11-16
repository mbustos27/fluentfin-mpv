"""FluentFin MPV's ``fluentfin.lua`` and uosc, measured against a real mpv.

Two kinds of run, both silent (``--ao=null``) and driven over mpv's IPC:

* ``fluentfin.lua`` with no window (``--vo=null``), beside a stand-in
  ``uosc.lua`` that records the messages uosc would get (``set-button``,
  ``disable-elements``, ``flash-elements``) in ``user-data/test/uosc``; the
  test publishes ``user-data/fluentfin/bar`` itself, as uosc's patch does.
  The key map, the wheel, the subtitle raise, the buttons and the hide.
* the vendored uosc itself, which lays nothing out without a video output
  (``--vo=null`` has no OSD size): a real window, so these run **only on a
  private desktop** (tools/backend-bench/hidden_run.py, which run_suite.py
  uses) and are skipped on the user's.

mpv is ``mpv.exe`` where there is one: the ``mpv.com`` console wrapper
starts it as a child, which a kill of the wrapper's handle leaves running.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import itertools
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

sys.argv = [sys.argv[0]]      # importing the shim reaches args.get_args()

from tests.test_fluentfin_osc_lua import Ipc  # noqa: E402
from jellyfin_mpv_shim import fluentfin_bar  # noqa: E402

MPV = shutil.which("mpv.exe") or shutil.which("mpv")
_seq = itertools.count()

#: A clip mpv can seek exactly (every frame a key frame), made once for the
#: module with mpv's own encoder: the lavfi test source seeks only roughly.
CLIP = {}


def setUpModule():
    if MPV is None:
        return
    CLIP["dir"] = tempfile.mkdtemp(prefix="ffbar-clip-")
    CLIP["path"] = os.path.join(CLIP["dir"], "clip.mkv")
    subprocess.run([MPV, "--no-config", "--terminal=no", "--ao=null",
                    "av://lavfi:testsrc=size=320x180:rate=5:duration=120",
                    "--o=" + CLIP["path"], "--of=matroska", "--ovc=mjpeg"],
                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                   stderr=subprocess.DEVNULL, timeout=60, check=True)


def tearDownModule():
    if "dir" in CLIP:
        shutil.rmtree(CLIP["dir"], ignore_errors=True)

SRT = "".join("%d\n00:%02d:%02d,000 --> 00:%02d:%02d,000\nline %d\n\n"
              % (n + 1, (n * 2) // 60, (n * 2) % 60, (n * 2 + 2) // 60,
                 (n * 2 + 2) % 60, n + 1) for n in range(30))

#: Records what uosc would be told, by script-message-to uosc.
STAND_IN = r"""
local utils = require("mp.utils")
local seen = {buttons = {}, disabled = {}, flashes = 0}
local function publish() mp.set_property_native("user-data/test/uosc", seen) end
mp.register_script_message("set-button", function(name, data)
    seen.buttons[name] = utils.parse_json(data)
    publish()
end)
mp.register_script_message("disable-elements", function(client, elements)
    seen.disabled[client] = elements
    publish()
end)
mp.register_script_message("flash-elements", function(elements)
    seen.flashes = seen.flashes + 1
    publish()
end)
publish()
"""


class EventIpc(Ipc):
    """`Ipc` that keeps the ``client-message`` events it reads past (the
    broadcast ``script-message``s, which reach every IPC client)."""

    def __init__(self, path, proc):
        self.events = []
        super().__init__(path, proc)

    def cmd(self, *args):
        self._rid += 1
        self._write((json.dumps({"command": list(args),
                                 "request_id": self._rid}) + "\n").encode())
        while True:
            while b"\n" not in self._buf:
                chunk = self._read(65536)
                if not chunk:
                    raise EOFError("mpv closed its socket")
                self._buf += chunk
            line, self._buf = self._buf.split(b"\n", 1)
            msg = json.loads(line)
            if msg.get("event") == "client-message":
                self.events.append(msg.get("args"))
            if msg.get("request_id") == self._rid:
                return msg


def private_desktop():
    """Whether this thread's desktop is not the interactive one (a window
    opened here is seen by nobody)."""
    if os.name != "nt":
        return bool(os.environ.get("DISPLAY"))
    import ctypes
    from ctypes import wintypes as wt

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    user32.GetThreadDesktop.restype = wt.HANDLE
    user32.GetThreadDesktop.argtypes = [wt.DWORD]
    user32.GetUserObjectInformationW.argtypes = [
        wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD,
        ctypes.POINTER(wt.DWORD)]
    desk = user32.GetThreadDesktop(kernel32.GetCurrentThreadId())
    buf = ctypes.create_unicode_buffer(256)
    needed = wt.DWORD()
    if not desk or not user32.GetUserObjectInformationW(
            desk, 2, buf, ctypes.sizeof(buf), ctypes.byref(needed)):
        return False
    return buf.value.lower() != "default"


@unittest.skipIf(MPV is None, "no mpv on PATH")
class _Case(unittest.TestCase):

    def start(self, *extra, input_conf=None, uosc="stand-in", window=False,
              script_opts=()):
        self.tmp = tempfile.mkdtemp(prefix="ffbar-")
        srt = os.path.join(self.tmp, "subs.srt")
        pathlib.Path(srt).write_text(SRT, encoding="utf-8")
        if os.name == "nt":
            path = "\\\\.\\pipe\\ffbar-%d-%d" % (os.getpid(), next(_seq))
        else:
            path = os.path.join(self.tmp, "ipc")
        self.log = os.path.join(self.tmp, "mpv.log")
        argv = [MPV, "--no-config", "--ao=null", "--osc=no", "--terminal=no",
                "--pause", "--keep-open=yes", "--start=30", "--volume=50",
                "--input-ipc-server=" + path, "--log-file=" + self.log,
                "--sub-file=" + srt]
        if window:
            argv += ["--vo=gpu", "--force-window=yes", "--geometry=1280x720",
                     "--focus-on=never",
                     "--osd-fonts-dir=" + fluentfin_bar.fonts_dir()]
        else:
            argv += ["--vo=null"]
        # One at a time: uosc's values carry commas, which a whole
        # --script-opts would split.
        for opt in list(script_opts) + fluentfin_bar.lua_script_opts():
            argv.append("--script-opts-append=" + opt)
        if uosc == "stand-in":
            stand_in = os.path.join(self.tmp, "uosc.lua")
            pathlib.Path(stand_in).write_text(STAND_IN, encoding="utf-8")
            argv.append("--script=" + stand_in)
        elif uosc == "real":
            argv.append("--script=" + fluentfin_bar.uosc_dir())
        argv.append("--script=" + fluentfin_bar.lua_script())
        if input_conf is not None:
            conf = os.path.join(self.tmp, "input.conf")
            pathlib.Path(conf).write_text(input_conf, encoding="utf-8")
            argv.insert(2, "--input-conf=" + conf)
        argv += list(extra)
        argv.append(CLIP["path"])
        self.proc = subprocess.Popen(argv, stdin=subprocess.DEVNULL,
                                     stdout=subprocess.DEVNULL,
                                     stderr=subprocess.DEVNULL)
        self.addCleanup(self._stop)
        self.ipc = EventIpc(path, self.proc)
        self.ipc.cmd("enable_event", "client-message")
        self.wait_for(lambda: self.ipc.get("time-pos") is not None,
                      "the file to load")
        self.wait_for(lambda: self.ipc.get(
            "input-bindings") and self.bound("fluentfin/pause"),
            "fluentfin.lua's bindings")

    def bound(self, name):
        return any(b.get("cmd", "").endswith(name)
                   for b in self.ipc.get("input-bindings") or ())

    def _stop(self):
        try:
            self.ipc.cmd("quit")
        except Exception:
            pass
        if hasattr(self, "ipc"):
            self.ipc.close()
        try:
            self.proc.wait(10)
        except subprocess.TimeoutExpired:
            self.proc.kill()          # our own Popen handle, nothing else
            self.proc.wait(10)
        shutil.rmtree(self.tmp, ignore_errors=True)

    def wait_for(self, predicate, what, timeout=8.0):
        deadline = time.monotonic() + timeout
        while not predicate():
            if time.monotonic() > deadline:
                self.fail("timed out waiting for " + what)
            time.sleep(0.05)

    def settle(self, prop, seconds=0.6):
        time.sleep(seconds)
        return self.ipc.get(prop)

    def key(self, name, times=1):
        for _ in range(times):
            self.ipc.cmd("keypress", name)
        time.sleep(0.3)

    def set_user(self, name, value):
        self.ipc.cmd("set_property", "user-data/" + name, value)

    def uosc_seen(self):
        return self.ipc.get("user-data/test/uosc") or {}

    def disabled(self):
        """``disable-elements`` by client (an empty Lua table comes back as
        a JSON list)."""
        return self.uosc_seen().get("disabled") or {}

    def messages(self, action, seconds=0.5):
        """The broadcast script messages ``action`` makes."""
        start = len(self.ipc.events)
        action()
        time.sleep(seconds)
        self.ipc.get("pause")           # reads past what came meanwhile
        return self.ipc.events[start:]


class KeyMapTest(_Case):

    def test_fluentfins_keys(self):
        self.start()
        self.assertTrue(self.ipc.get("pause"))
        self.key("k")
        self.assertFalse(self.ipc.get("pause"), "k pauses and plays")
        self.key("k")
        self.assertTrue(self.ipc.get("pause"))
        at = self.ipc.get("time-pos")
        self.key("l")
        self.assertAlmostEqual(self.settle("time-pos"), at + 10, delta=0.6)
        self.key("j", 2)
        self.assertAlmostEqual(self.settle("time-pos"), at - 10, delta=0.6)
        self.key("UP", 3)
        self.assertEqual(self.ipc.get("volume"), 65)
        self.key("DOWN")
        self.assertEqual(self.ipc.get("volume"), 60)

    def test_volume_is_held_to_0_to_100(self):
        self.start("--volume=98", "--volume-max=130")
        self.key("UP", 3)
        self.assertEqual(self.ipc.get("volume"), 100)
        self.ipc.cmd("set_property", "volume", 3)
        self.key("DOWN", 3)
        self.assertEqual(self.ipc.get("volume"), 0)

    def test_skip_next_and_previous_are_the_shims(self):
        self.start()
        for key, message in (("s", "fluentfin-skip"), ("N", "fluentfin-next"),
                             ("P", "fluentfin-prev")):
            with self.subTest(key=key):
                self.assertIn([message], self.messages(
                    lambda: self.ipc.cmd("keypress", key)))

    def test_the_users_input_conf_wins(self):
        self.start(input_conf="k no-osd set volume 7\nUP no-osd set volume 9\n")
        self.key("k")
        self.assertTrue(self.ipc.get("pause"), "k is theirs")
        self.assertEqual(self.ipc.get("volume"), 7)
        self.key("UP")
        self.assertEqual(self.ipc.get("volume"), 9)


class WithUoscTest(_Case):
    """fluentfin.lua once "uosc" has published its bar (the stand-in)."""

    def up(self, **kw):
        self.start(**kw)
        self.set_user("fluentfin/bar", 0)
        self.wait_for(lambda: "fluentfin_prev" in self.uosc_seen().get(
            "buttons", {}), "the buttons")

    def test_the_wheel_is_volume_and_never_a_seek(self):
        self.up()
        at = self.ipc.get("time-pos")
        self.key("WHEEL_UP", 2)
        self.assertEqual(self.ipc.get("volume"), 60)
        self.key("WHEEL_DOWN")
        self.assertEqual(self.ipc.get("volume"), 55)
        for key in ("WHEEL_LEFT", "WHEEL_RIGHT"):
            self.key(key, 2)
        self.assertEqual(self.ipc.get("volume"), 55)
        self.assertAlmostEqual(self.ipc.get("time-pos"), at, delta=0.01)
        self.ipc.cmd("set_property", "volume", 98)
        self.key("WHEEL_UP", 3)
        self.assertEqual(self.ipc.get("volume"), 100)
        self.assertGreaterEqual(self.uosc_seen()["flashes"], 1,
                                "uosc shows its volume bar")

    def test_the_buttons_follow_the_bridge(self):
        self.up()
        buttons = self.uosc_seen()["buttons"]
        self.assertEqual(buttons["fluentfin_prev"]["command"],
                         "script-message fluentfin-prev")
        self.assertTrue(buttons["fluentfin_next"]["hide"],
                        "no next item: no Next button")
        self.assertTrue(buttons["fluentfin_skip"]["hide"])
        self.set_user("fluentfin/next", "1")
        self.wait_for(lambda: not self.uosc_seen()["buttons"][
            "fluentfin_next"].get("hide"), "the Next button")
        flashes = self.uosc_seen()["flashes"]
        self.set_user("fluentfin/skip", "Skip Intro")
        self.wait_for(lambda: not self.uosc_seen()["buttons"][
            "fluentfin_skip"].get("hide"), "the Skip button")
        skip = self.uosc_seen()["buttons"]["fluentfin_skip"]
        self.assertEqual((skip["tooltip"], skip["command"]),
                         ("Skip Intro", "script-message fluentfin-skip"))
        self.assertGreater(self.uosc_seen()["flashes"], flashes,
                           "the bar shows itself with the offer")
        self.set_user("fluentfin/skip", "")
        self.wait_for(lambda: self.uosc_seen()["buttons"][
            "fluentfin_skip"].get("hide"), "the Skip button to go")

    def test_subtitles_above_the_bar_and_back(self):
        self.up()
        self.assertEqual(self.ipc.get("sub-pos"), 100)
        seen = []
        for _ in range(3):
            self.set_user("fluentfin/bar", 0.12)
            self.wait_for(lambda: self.ipc.get("sub-pos") < 100, "raise")
            seen.append(self.settle("sub-pos", 0.3))
            self.set_user("fluentfin/bar", 0)
            self.wait_for(lambda: self.ipc.get("sub-pos") == 100, "restore")
        self.assertEqual(seen, [88, 88, 88], "it does not walk")

    def test_the_shims_position_is_what_comes_back(self):
        self.up()
        self.set_user("fluentfin/bar", 0.12)
        self.wait_for(lambda: self.ipc.get("sub-pos") == 88, "raise")
        self.ipc.cmd("set", "sub-pos", "80")        # "middle": above it
        self.assertEqual(self.settle("sub-pos"), 80)
        self.ipc.cmd("set", "sub-pos", "100")       # "bottom": under it
        self.wait_for(lambda: self.ipc.get("sub-pos") == 88, "re-raise")
        self.ipc.cmd("set", "sub-pos", "80")
        self.set_user("fluentfin/bar", 0)
        self.assertEqual(self.settle("sub-pos"), 80)

    def test_fluentfins_controls_take_uoscs_place(self):
        """The shim's enable_osc(False) (set_controls "ui") says
        "osc-visibility never" to every OSC: uosc's elements go, the
        subtitles come down; "auto" brings both back."""
        self.up()
        self.set_user("fluentfin/bar", 0.12)
        self.wait_for(lambda: self.ipc.get("sub-pos") == 88, "raise")
        self.ipc.cmd("script-message", "osc-visibility", "never", "False")
        self.wait_for(lambda: self.disabled().get(
            "fluentfin") == "timeline,controls,volume,top_bar", "the hide")
        self.assertEqual(self.settle("sub-pos"), 100)
        # No prompt over FluentFin's controls.
        self.set_user("fluentfin/skip", "Skip Intro")
        self.ipc.cmd("script-message", "osc-visibility", "auto", "False")
        self.wait_for(lambda: self.disabled().get(
            "fluentfin") == "", "the elements back")
        self.wait_for(lambda: self.ipc.get("sub-pos") == 88, "raise again")

    def test_a_lift_written_with_the_hide_is_kept(self):
        """bridge_controls.UiControls hides the bar and lifts the subtitles
        in two commands back to back (set_controls "ui"): the restore must
        not put its older base back over the lift."""
        from jellyfin_mpv_shim import bridge_controls

        test = self

        class Player:
            def command(self, *args):
                test.ipc.cmd(*args)

        class Pm:
            osc_enabled = True
            _player = Player()

            def enable_osc(self, enabled):
                test.ipc.cmd("script-message", "osc-visibility",
                             "auto" if enabled else "never", "False")

        for _ in range(3):
            self.up()
            self.set_user("fluentfin/bar", 0.12)
            self.wait_for(lambda: self.ipc.get("sub-pos") == 88, "raise")
            controls = bridge_controls.UiControls()
            controls.set("ui", 0.15)
            controls.apply(Pm(), 100)
            self.assertEqual(self.settle("sub-pos", 0.8), 85)
            self.doCleanups()


class WithoutUoscTest(_Case):
    """The fork's run: fluentfin.lua keeps to the key map and the prompt."""

    def test_no_raise_no_buttons(self):
        self.start(uosc=None)
        self.ipc.cmd("script-message", "osc-visibility", "never", "False")
        self.set_user("fluentfin/skip", "Skip Intro")
        self.assertEqual(self.settle("sub-pos"), 100)
        self.assertFalse(self.bound("fluentfin/wheel-up"),
                         "the wheel stays the fork's")
        self.key("k")
        self.assertFalse(self.ipc.get("pause"))


@unittest.skipUnless(private_desktop(), "needs a video output: run it "
                     "through tools/backend-bench/run_suite.py (a private "
                     "desktop), never on the user's")
class RealUoscTest(_Case):
    """The vendored uosc with FluentFin's options, on a real window."""

    def up(self):
        self.start(window=True, uosc="real",
                   script_opts=fluentfin_bar.uosc_script_opts(
                       "#60CDFF", 1.0, "#101010", 64))
        self.wait_for(lambda: self.ipc.get("user-data/fluentfin/bar")
                      is not None, "uosc's bar")

    def errors(self):
        with open(self.log, encoding="utf-8", errors="replace") as fh:
            return [line for line in fh
                    if "[e][uosc]" in line or "[e][fluentfin]" in line]

    def test_uosc_runs_with_fluentfins_options(self):
        self.up()
        time.sleep(0.5)
        self.assertEqual(self.errors(), [])

    def test_the_bar_lifts_the_subtitles_and_the_hide_takes_it_away(self):
        self.up()
        self.ipc.cmd("script-message-to", "uosc", "set-min-visibility", "1")
        self.wait_for(lambda: (self.ipc.get("user-data/fluentfin/bar")
                               or 0) > 0.05, "the bar")
        bar = self.ipc.get("user-data/fluentfin/bar")
        self.wait_for(lambda: self.ipc.get("sub-pos") == int(100 - bar * 100),
                      "the raise")
        self.ipc.cmd("script-message", "osc-visibility", "never", "False")
        self.wait_for(lambda: self.ipc.get("user-data/fluentfin/bar") == 0,
                      "uosc's elements to go")
        self.wait_for(lambda: self.ipc.get("sub-pos") == 100, "the restore")
        self.ipc.cmd("script-message", "osc-visibility", "auto", "False")

        def shown():
            # Asked again until uosc has its elements back: fluentfin.lua
            # has them re-created by a message of its own, which an early
            # ask can overtake (then it reaches no element).
            self.ipc.cmd("script-message-to", "uosc", "set-min-visibility",
                         "1")
            return (self.ipc.get("user-data/fluentfin/bar") or 0) > 0.05

        self.wait_for(shown, "the bar back")
        self.wait_for(lambda: self.ipc.get("sub-pos") < 100, "raised again")
        self.assertEqual(self.errors(), [])


if __name__ == "__main__":
    unittest.main()
