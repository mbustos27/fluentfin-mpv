"""FluentFin MPV's OSC patches, measured against a real mpv.

`trickplay-osc.lua` carries options of FluentFin MPV's (marked "FluentFin
MPV" there) whose defaults are upstream's. Two of them change behaviour
rather than looks, and both are checked here on an mpv with no window
(``--vo=null --ao=null``), driven over its IPC socket:

* ``raisesubs``: subtitles move above the bottom bar while it is shown
  and come back when it hides, keeping whatever position the shim set.
* ``wheelvolume``: the wheel is volume +-step, held to 0..100, and never a
  seek; the user's input.conf still wins.

Each starts mpv once per test (a fraction of a second). Skipped without an
``mpv`` on PATH.
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
import socket
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
FORK = ROOT / "jellyfin_mpv_shim" / "trickplay-osc.lua"
MPV = shutil.which("mpv")
_seq = itertools.count()

SRT = "".join("%d\n00:%02d:%02d,000 --> 00:%02d:%02d,000\nline %d\n\n"
              % (n + 1, (n * 2) // 60, (n * 2) % 60, (n * 2 + 2) // 60,
                 (n * 2 + 2) % 60, n + 1) for n in range(30))


class Ipc:
    def __init__(self, path, proc, timeout=20):
        deadline = time.monotonic() + timeout
        while True:
            try:
                if os.name == "nt":
                    self._fh = open(path, "r+b", buffering=0)
                    self._read = self._fh.read
                    self._write = self._fh.write
                else:
                    self._sock = socket.socket(socket.AF_UNIX)
                    self._sock.connect(path)
                    self._read = self._sock.recv
                    self._write = self._sock.sendall
                break
            except OSError:
                if proc.poll() is not None or time.monotonic() > deadline:
                    raise
                time.sleep(0.05)
        self._buf = b""
        self._rid = 0
        self.cmd("disable_event", "all")

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
            if msg.get("request_id") == self._rid:
                return msg

    def get(self, prop):
        return self.cmd("get_property", prop).get("data")

    def close(self):
        try:
            (self._fh if os.name == "nt" else self._sock).close()
        except Exception:
            pass


@unittest.skipIf(MPV is None, "no mpv on PATH")
class _MpvCase(unittest.TestCase):
    """One mpv, playing a paused minute of testsrc with a subtitle track,
    running the fork with the given ``osc-*`` script-opts."""

    def start(self, *opts, input_conf=None):
        self.tmp = tempfile.mkdtemp(prefix="ffosc-")
        srt = os.path.join(self.tmp, "subs.srt")
        with open(srt, "w", encoding="utf-8") as fh:
            fh.write(SRT)
        if os.name == "nt":
            path = r"\\.\pipe\ffosc-%d-%d" % (os.getpid(), next(_seq))
        else:
            path = os.path.join(self.tmp, "ipc")
        argv = [MPV, "--no-config", "--vo=null", "--ao=null", "--osc=no",
                "--terminal=no", "--pause", "--keep-open=yes",
                "--input-ipc-server=" + path, "--script=" + str(FORK),
                "--sub-file=" + srt,
                "--script-opts=" + ",".join(("osc-idlescreen=no",) + opts),
                "av://lavfi:testsrc=size=320x180:rate=10:duration=60"]
        if input_conf is not None:
            conf = os.path.join(self.tmp, "input.conf")
            with open(conf, "w", encoding="utf-8") as fh:
                fh.write(input_conf)
            argv.insert(2, "--input-conf=" + conf)
        self.proc = subprocess.Popen(argv, stdin=subprocess.DEVNULL,
                                     stdout=subprocess.DEVNULL,
                                     stderr=subprocess.DEVNULL)
        self.addCleanup(self._stop)
        self.ipc = Ipc(path, self.proc)
        self.wait_for(lambda: self.ipc.get("time-pos") is not None,
                      "the file to load")
        # The OSC has drawn (laid itself out) once it has had a tick.
        time.sleep(0.5)

    def _stop(self):
        try:
            self.ipc.cmd("quit")
        except Exception:
            pass
        self.ipc.close() if hasattr(self, "ipc") else None
        try:
            self.proc.wait(10)
        except subprocess.TimeoutExpired:
            self.proc.kill()          # our own Popen handle, nothing else
            self.proc.wait(10)
        shutil.rmtree(self.tmp, ignore_errors=True)

    def wait_for(self, predicate, what, timeout=5.0):
        deadline = time.monotonic() + timeout
        while not predicate():
            if time.monotonic() > deadline:
                self.fail("timed out waiting for " + what)
            time.sleep(0.05)

    def settle(self, prop, seconds=1.0):
        """``prop`` once it has stopped changing for a moment."""
        time.sleep(seconds)
        return self.ipc.get(prop)

    def osc(self, mode):
        self.ipc.cmd("script-message", "osc-visibility", mode, "no-osd")


class SubtitlesAboveTheBarTest(_MpvCase):

    def test_raised_while_shown_and_back_when_hidden(self):
        self.start("osc-raisesubs=yes")
        self.assertEqual(self.ipc.get("sub-pos"), 100)
        self.osc("always")
        self.wait_for(lambda: self.ipc.get("sub-pos") < 100,
                      "the subtitles to move up")
        raised = self.ipc.get("sub-pos")
        # The bar is 56 of the OSC's 720-odd units: about 92.
        self.assertGreaterEqual(raised, 85)
        self.osc("auto")
        self.wait_for(lambda: self.ipc.get("sub-pos") == 100,
                      "the subtitles to come back")

    def test_several_times_over(self):
        """Shown and hidden repeatedly, the position does not walk."""
        self.start("osc-raisesubs=yes")
        seen = []
        for _ in range(3):
            self.osc("always")
            self.wait_for(lambda: self.ipc.get("sub-pos") < 100, "raise")
            seen.append(self.settle("sub-pos", 0.3))
            self.osc("auto")
            self.wait_for(lambda: self.ipc.get("sub-pos") == 100, "restore")
        self.assertEqual(len(set(seen)), 1, seen)

    def test_hidden_by_never_too(self):
        self.start("osc-raisesubs=yes")
        self.osc("always")
        self.wait_for(lambda: self.ipc.get("sub-pos") < 100, "raise")
        self.osc("never")
        self.wait_for(lambda: self.ipc.get("sub-pos") == 100, "restore")

    def test_the_shims_position_is_what_comes_back(self):
        """The shim writes sub-pos itself (its subtitle position setting, at
        every start). Written while raised, that is the value to restore --
        and raised again, if it is under the bar."""
        self.start("osc-raisesubs=yes")
        self.osc("always")
        self.wait_for(lambda: self.ipc.get("sub-pos") < 100, "raise")
        raised = self.ipc.get("sub-pos")
        self.ipc.cmd("set", "sub-pos", "80")          # "middle": above it
        self.assertEqual(self.settle("sub-pos", 0.5), 80)
        self.ipc.cmd("set", "sub-pos", "100")         # "bottom": under it
        self.wait_for(lambda: self.ipc.get("sub-pos") == raised, "re-raise")
        self.ipc.cmd("set", "sub-pos", "80")
        self.osc("auto")
        self.assertEqual(self.settle("sub-pos"), 80)

    def test_off_by_default(self):
        """Upstream's OSC never touches sub-pos: the option is what does."""
        self.start()
        self.osc("always")
        self.assertEqual(self.settle("sub-pos"), 100)


class WheelIsVolumeTest(_MpvCase):

    def wheel(self, key, times=1):
        for _ in range(times):
            self.ipc.cmd("keypress", key)
        time.sleep(0.3)
        return self.ipc.get("volume")

    def set_volume(self, value):
        self.ipc.cmd("set", "volume", str(value))

    def test_five_a_notch_up_to_100(self):
        self.start("osc-wheelvolume=5")
        self.set_volume(80)
        self.assertEqual(self.wheel("WHEEL_UP"), 85)
        self.assertEqual(self.wheel("WHEEL_UP", 5), 100)
        self.assertEqual(self.wheel("WHEEL_UP"), 100)
        self.assertEqual(self.wheel("WHEEL_DOWN", 3), 85)

    def test_down_to_0(self):
        self.start("osc-wheelvolume=5")
        self.set_volume(7)
        self.assertEqual(self.wheel("WHEEL_DOWN"), 2)
        self.assertEqual(self.wheel("WHEEL_DOWN"), 0)
        self.assertEqual(self.wheel("WHEEL_DOWN"), 0)

    def test_above_100_the_wheel_only_comes_down(self):
        """Set to 120 some other way (a key, mpv.conf): the wheel neither
        raises it further nor snaps it back to 100."""
        self.start("osc-wheelvolume=5")
        self.set_volume(120)
        self.assertEqual(self.wheel("WHEEL_UP"), 120)
        self.assertEqual(self.wheel("WHEEL_DOWN"), 115)

    def test_the_horizontal_wheel_never_seeks(self):
        self.start("osc-wheelvolume=5")
        before = self.ipc.get("time-pos")
        self.wheel("WHEEL_RIGHT", 3)
        self.wheel("WHEEL_LEFT")
        self.assertEqual(self.settle("time-pos", 0.5), before)

    def test_the_users_input_conf_wins(self):
        self.start("osc-wheelvolume=5",
                   input_conf="WHEEL_UP add volume 1\n")
        self.set_volume(50)
        self.assertEqual(self.wheel("WHEEL_UP"), 51)

    def test_without_the_option_mpv_keeps_its_own(self):
        """The control: upstream leaves mpv's bindings, which take the
        volume past 100 and seek on the horizontal wheel."""
        self.start()
        self.set_volume(100)
        self.assertGreater(self.wheel("WHEEL_UP"), 100)
        before = self.ipc.get("time-pos")
        self.wheel("WHEEL_RIGHT")
        self.assertNotEqual(self.settle("time-pos", 0.5), before)


#: The label patch, run on its own inside mpv's Lua: the block between its
#: markers is loaded with a stub `mp.get_property` and `tracks_mpv`, and
#: the labels come back through a user-data property.
LABEL_DRIVER = r"""
local utils = require 'mp.utils'
local fh = io.open(%(fork)s, "rb")
local src = fh:read("*a")
fh:close()
local s = src:find("-- BEGIN patch FluentFin MPV (track names on the track buttons)", 1, true)
local e = src:find("-- END patch", s, true)
local props = {}
local env = setmetatable({mp = {get_property = function(k) return props[k] end}},
                         {__index = _G})
local chunk = src:sub(s, e - 1)
local fn
if setfenv then
    fn = assert(loadstring(chunk, "labels"))
    setfenv(fn, env)
else
    fn = assert(load(chunk, "labels", "t", env))
end
fn()
local out = {}
for i, c in ipairs(utils.parse_json(%(cases)s)) do
    props = {[c.kind] = c.selected}
    env.tracks_mpv = {audio = {}, sub = {}}
    for _, t in ipairs(c.tracks) do env.tracks_mpv[c.kind][t.id] = t end
    out[i] = env.ff_track_label(c.kind)
end
mp.set_property("user-data/fluentfin-test/labels", utils.format_json(out))
"""


def _track(kind, selected="1", **fields):
    track = {"id": 1, "osc_id": 1}
    track.update({k.replace("_", "-"): v for k, v in fields.items()})
    return {"kind": kind, "selected": selected, "tracks": [track]}


#: (case, the label it must get).
LABELS = [
    (_track("audio", lang="eng", codec="aac"), "English"),
    (_track("audio", lang="ja", title="Commentary"), "Japanese"),
    (_track("sub", lang="eng", title="English SDH"), "English SDH"),
    # A sidecar: mpv titles it with the end of its url.
    (_track("sub", lang="eng", title="Stream.srt", external=True,
            external_filename="http://srv/Videos/x/Subtitles/2/0/Stream.srt"),
     "English"),
    # ... which can carry the token (docs/auth-headers.md, the sidecar
    # rule): never on screen.
    (_track("sub", title="Stream.srt?ApiKey=SECRETTOKEN", codec="subrip",
            external=True, external_filename="http://srv/x/Stream.srt"
            "?ApiKey=SECRETTOKEN"), "SUBRIP"),
    (_track("sub", selected="no", lang="eng"), "Off"),
    (_track("audio", codec="aac"), "AAC"),
    (_track("audio", lang="und", codec="opus"), "OPUS"),
    (_track("audio", lang="tlh"), "TLH"),
    (_track("sub", lang="eng", forced=True), "English (F)"),
    (_track("sub", lang="eng",
            title="English Signs & Songs Full Subtitles"), "English Si…"),
    (_track("sub", title="{\\an8}Top"), "(/an8)Top"),
    (_track("sub", selected="7", lang="eng"), "–"),
]


@unittest.skipIf(MPV is None, "no mpv on PATH")
class TrackLabelTest(unittest.TestCase):

    def test_labels(self):
        tmp = tempfile.mkdtemp(prefix="ffosc-")
        self.addCleanup(shutil.rmtree, tmp, True)
        driver = os.path.join(tmp, "labels.lua")
        with open(driver, "w", encoding="utf-8") as fh:
            fh.write(LABEL_DRIVER % {
                "fork": json.dumps(str(FORK)),
                "cases": "[==[%s]==]" % json.dumps([c for c, _ in LABELS])})
        if os.name == "nt":
            path = r"\\.\pipe\ffosc-%d-%d" % (os.getpid(), next(_seq))
        else:
            path = os.path.join(tmp, "ipc")
        proc = subprocess.Popen(
            [MPV, "--no-config", "--idle=yes", "--vo=null", "--ao=null",
             "--osc=no", "--terminal=no", "--input-ipc-server=" + path,
             "--script=" + driver],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL)
        ipc = Ipc(path, proc)

        def stop():
            try:
                ipc.cmd("quit")
            except Exception:
                pass
            ipc.close()
            try:
                proc.wait(10)
            except subprocess.TimeoutExpired:
                proc.kill()           # our own Popen handle, nothing else
                proc.wait(10)
        self.addCleanup(stop)
        deadline = time.monotonic() + 10
        got = None
        while got is None and time.monotonic() < deadline:
            got = ipc.get("user-data/fluentfin-test/labels")
            time.sleep(0.05)
        self.assertIsNotNone(got, "the label driver never answered")
        labels = json.loads(got)
        for (case, want), label in zip(LABELS, labels):
            with self.subTest(case=case):
                self.assertEqual(label, want)
                self.assertNotIn("SECRETTOKEN", label)
                self.assertNotIn("{", label)


if __name__ == "__main__":
    unittest.main()
