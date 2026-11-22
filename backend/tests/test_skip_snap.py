"""1.6 skip: the margin -- a skip rule's edge snaps to the nearest black or
silence (`skip_snap`). The choice is pure; the analysis and the dump run a
real mpv with no window and no sound (``--vo=null --ao=null``) on a clip
generated here (a test pattern and a tone with one black, silent stretch;
never real footage). 1.6.1 (BH-160-M1): with the libmpv Nixie ships and no
mpv binary (`NoMpvExeTest`, which a release run requires); the mpv binary's
own test is skipped when there is none."""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
from unittest import mock

from jellyfin_mpv_shim import skip_snap as snap

LOG = """[ 0.012][v][ffmpeg] silencedetect: silence_start: 1.160998
[ 0.024][v][ffmpeg] silencedetect: silence_end: 2.159342 | silence_duration: 0.998345
[ 0.024][v][ffmpeg] blackdetect: black_start:1.144 black_end:2.184 black_duration:1.04
[ 0.020][i][duration] nixie-duration=3.135
"""


class Seg:
    def __init__(self, type, start, end, anchor="start"):
        self.type, self.start, self.end = type, start, end
        self.source, self.anchor = "rule", anchor
        self.rule_start, self.rule_end = start, end
        self.rule_id = "r0"
        self.corrected = False
        self.snap_pending = True


class Choice(unittest.TestCase):
    def test_parse(self):
        blacks, silences, duration = snap.parse_detections(LOG)
        self.assertEqual(blacks, [(1.144, 2.184)])
        self.assertEqual(silences, [(1.160998, 2.159342)])
        self.assertEqual(duration, 3.135)

    def test_a_start_rule_lands_just_before_the_gaps_end(self):
        edge = snap.choose_edge([(40.0, 41.0)], [(40.1, 40.9)], 39.0, 3.0,
                                "start")
        self.assertEqual(edge, 40.65, "black and silence together, inside")

    def test_an_end_rule_starts_just_after_the_gaps_start(self):
        self.assertEqual(snap.choose_edge([(1300.0, 1300.4)], [], 1301.0,
                                          3.0, "end"), 1300.2)

    def test_nothing_near_keeps_the_edge(self):
        self.assertIsNone(snap.choose_edge([(30.0, 31.0)], [], 41.0, 3.0,
                                           "start"))
        self.assertIsNone(snap.choose_edge([], [], 41.0, 3.0, "start"))

    def test_both_beat_either_and_nearer_beats_farther(self):
        blacks = [(39.0, 39.4), (42.5, 43.0)]
        silences = [(42.6, 43.1), (40.5, 41.2)]
        self.assertEqual(snap.choose_edge(blacks, silences, 41.0, 3.0,
                                          "start"), 42.8, "both, though farther")
        self.assertEqual(snap.choose_edge(blacks, [], 39.5, 3.0, "start"),
                         39.2, "the nearer of two")

    def test_silence_throughout_or_a_long_quiet_scene_is_no_cut(self):
        """The fake server's test video is digital silence throughout: the
        whole stretch read is one silence, which moved a rule's edge 1.8 s
        for nothing (the harness, 2026-11-21). A gap touching either end of
        what was read, or longer than a cut, is not one."""
        read = (93.0, 99.0)
        self.assertIsNone(snap.choose_edge([], [(93.0, 99.0)], 96.0, 3.0,
                                           "start", read=read))
        self.assertIsNone(snap.choose_edge([], [(94.0, 98.0)], 96.0, 3.0,
                                           "start", read=read))
        self.assertIsNone(snap.choose_edge([(97.0, 99.0)], [], 96.0, 3.0,
                                           "start", read=read))
        self.assertEqual(snap.choose_edge([(97.0, 97.6)], [], 96.0, 3.0,
                                          "start", read=read), 97.35)

    def test_a_corrected_rule_looks_less_far(self):
        seg = Seg("Commercial", 0, 41)
        self.assertEqual(snap.edge_of(seg), (41.0, snap.WINDOW, "start"))
        seg.corrected = True
        self.assertEqual(snap.edge_of(seg)[1], snap.CORRECTED_WINDOW)
        end = Seg("Commercial", 1338, 1400, anchor="end")
        self.assertEqual(snap.edge_of(end)[0], 1338.0)

    def test_covered(self):
        state = {"seekable-ranges": [{"start": 0.0, "end": 30.0}]}
        self.assertTrue(snap.covered(state, 20, 26))
        self.assertFalse(snap.covered(state, 28, 34))
        self.assertFalse(snap.covered(None, 0, 1))


class TickTest(unittest.TestCase):
    def test_workers_start_near_the_edge_once_and_stop_past_it(self):
        snapper = snap.Snapper(binary="unused",
                               root=tempfile.mkdtemp(prefix="snap-tick-"))
        self.addCleanup(shutil.rmtree, snapper.root, True)
        started = []
        snapper._work = lambda pm, video, seg: started.append(seg)

        class V:
            pass

        video = V()
        start_rule = Seg("Commercial", 0, 41)
        end_rule = Seg("Commercial", 1338, 1400, anchor="end")
        passed = Seg("Commercial", 0, 5)
        video.intros = [start_rule, end_rule, passed]
        snapper.tick(None, video, 10.0)
        snapper.tick(None, video, 10.5)
        for thread in snapper.threads:
            thread.join(5)
        self.assertEqual(started, [start_rule], "the end rule waits")
        self.assertFalse(passed.snap_pending, "already played")
        snapper.tick(None, video, 1100.0)
        for thread in snapper.threads:
            thread.join(5)
        self.assertEqual(started, [start_rule, end_rule])

    def test_a_rules_rebuild_while_a_snap_runs_starts_no_second_one(self):
        """1.6.1 review: a rules rebuild mid-episode (a Yes, an Add) makes a
        new segment for each rule, still pending while its edge is being
        read (`carry_rule_state`); keyed by the object, a second analysis of
        the same edge started, and the first ran on for nothing. The one
        analysis now lands on the rule's segment as it stands."""
        root = tempfile.mkdtemp(prefix="snap-rebuild-")
        self.addCleanup(shutil.rmtree, root, True)
        snapper = snap.Snapper(analyser=snap.Analyser("libmpv", "unused"),
                               root=root)
        self.addCleanup(snapper.close)
        gate = threading.Event()
        self.addCleanup(gate.set)
        analysed = []

        def analyse(tool, path, started=None):
            analysed.append(path)
            gate.wait(10)
            return LOG

        class Player:
            playback_time = 10.0
            demuxer_cache_state = {"seekable-ranges": [{"start": 0,
                                                        "end": 100}]}

            def command(self, name, start, end, path):
                with open(path, "wb") as handle:
                    handle.write(b"dump")

        class V:
            pass

        video = V()
        first = Seg("Commercial", 0, 41)
        video.intros = [first]

        class PM:
            _mpv_alive = True
            _player = Player()
            _video = video

        with mock.patch.object(snap, "analyse", analyse):
            snapper.tick(PM, video, 10.0)
            deadline = time.monotonic() + 5
            while not analysed and time.monotonic() < deadline:
                time.sleep(0.02)
            rebuilt = Seg("Commercial", 0, 41)      # the same rule, rebuilt
            video.intros = [rebuilt]
            snapper.tick(PM, video, 10.5)
            snapper.tick(PM, video, 11.0)
            gate.set()
            for thread in snapper.threads:
                thread.join(10)
        self.assertEqual(len(analysed), 1, "one analysis of the edge")
        self.assertAlmostEqual(rebuilt.end, 42.77, delta=0.05)
        self.assertFalse(rebuilt.snap_pending)
        # a corrected rule (another edge) is read again
        corrected = Seg("Commercial", 0, 44)
        corrected.corrected = True
        video.intros = [corrected]
        self.assertNotEqual(snap.snap_key(corrected), snap.snap_key(rebuilt))


class TempFolders(unittest.TestCase):
    """Review (Major 3): the dumps' folders were removed only in a worker's
    ``finally``, which a crash or an exit mid-analysis never runs, and
    nothing removed them later. Now each backend keeps them under its own
    root (per config directory), sweeps that root as it starts, and ends
    its analysers and removes the folders on a stop and at its exit."""

    def setUp(self):
        self.base = tempfile.mkdtemp(prefix="skip-snap-test-")
        self.addCleanup(shutil.rmtree, self.base, True)
        self.root = os.path.join(self.base, "mine")
        self.other = os.path.join(self.base, "another-backend")
        for folder in (self.root, self.other):
            os.makedirs(os.path.join(folder, "nixie-snap-left"))
            with open(os.path.join(folder, "nixie-snap-left", "part.mkv"),
                      "wb") as handle:
                handle.write(b"x" * 1024)
        os.makedirs(os.path.join(self.root, "not-ours"))

    def sleeper(self):
        """A process of this test's own, standing in for the analysing
        mpv (ended through its own handle)."""
        process = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(60)"],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.addCleanup(lambda: process.poll() is None and process.kill())
        return process

    def test_a_leftover_folder_goes_at_the_next_start_and_only_ours(self):
        snapper = snap.Snapper(binary="unused", root=self.root)
        self.addCleanup(snapper.close)
        self.assertEqual(os.listdir(self.root), ["not-ours"])
        self.assertEqual(os.listdir(self.other), ["nixie-snap-left"],
                         "another backend's root is not touched")

    def test_each_config_directory_has_its_own_root(self):
        a = snap.snap_root(os.path.join("data-a", "conf.json"))
        b = snap.snap_root(os.path.join("data-b", "conf.json"))
        self.assertNotEqual(a, b)
        self.assertEqual(os.path.dirname(os.path.dirname(a)),
                         tempfile.gettempdir())

    def test_the_exit_ends_the_analyser_and_removes_the_folders(self):
        snapper = snap.Snapper(binary="unused", root=self.root)
        busy = os.path.join(self.root, "nixie-snap-busy")
        os.makedirs(busy)
        snapper._folders.add(busy)
        process = self.sleeper()
        snapper._process(process)
        snapper.close()
        self.assertIsNotNone(process.wait(10), "the analyser ended")
        self.assertEqual(os.listdir(self.root), ["not-ours"])
        # an analyser starting after the close is ended at once
        late = self.sleeper()
        snapper._process(late)
        self.assertIsNotNone(late.wait(10))

    def test_a_stop_ends_the_analysis(self):
        snapper = snap.Snapper(binary="unused", root=self.root)
        self.addCleanup(snapper.close)
        snapper._work = lambda pm, video, seg: None

        class V:
            intros = []

        snapper.tick(None, V(), 10.0)
        process = self.sleeper()
        snapper._process(process)
        snapper.tick(None, None, None)      # stopped
        self.assertIsNotNone(process.wait(10))


def _mpv():
    return snap.mpv_binary() or shutil.which("mpv.exe") or shutil.which("mpv")


@unittest.skipUnless(_mpv(), "no mpv to analyse with")
class RealMpvTest(unittest.TestCase):
    """A clip with black and silence at 9.0-9.6 s; a rule that ends at 8."""

    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.mkdtemp(prefix="nixie-snap-test-")
        cls.clip = os.path.join(cls.folder, "clip.mkv")
        gap = "enable='between(t,9,9.6)'"
        subprocess.run(
            [_mpv(), "--no-config", "--msg-level=all=warn",
             "--force-window=no",
             "av://lavfi:testsrc=size=320x180:rate=25:duration=20,"
             "drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:" + gap,
             "--audio-file=av://lavfi:sine=f=440:d=20,volume=volume=0:" + gap,
             "--o=" + cls.clip, "--of=matroska", "--ovc=mpeg4",
             "--oac=aac"],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, timeout=120,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.folder, ignore_errors=True)

    def test_the_analysis_finds_the_gap(self):
        self.assertTrue(os.path.getsize(self.clip) > 10000)
        work = tempfile.mkdtemp(dir=self.folder)
        part = os.path.join(work, "part.mkv")
        shutil.copy(self.clip, part)
        blacks, silences, duration = snap.parse_detections(
            snap.analyse(_mpv(), part))
        self.assertAlmostEqual(duration, 20.0, delta=0.2)
        self.assertTrue(any(abs(a - 9.0) < 0.1 and abs(b - 9.6) < 0.1
                            for a, b in blacks), blacks)
        self.assertTrue(any(abs(a - 9.0) < 0.1 for a, b in silences),
                        silences)

    def test_the_cached_stretch_is_dumped_and_the_end_snaps(self):
        import python_mpv_jsonipc as jsonipc

        player = jsonipc.MPV(
            start_mpv=True, ipc_socket="nixie-snap-test-" + uuid.uuid4().hex,
            mpv_location=_mpv(), discard_output=True, vo="null", ao="null",
            config="no", cache="yes", pause=True, osc="no",
            force_window="no", load_scripts="no")
        self.addCleanup(player.terminate)
        player.play(self.clip)

        class PM:
            _mpv_alive = True

        pm = PM()
        pm._player = player
        pm._video = video = object()
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if snap.covered(player.demuxer_cache_state, 5, 11):
                break
            time.sleep(0.2)
        seg = Seg("Commercial", 0.0, 8.0)
        root = os.path.join(self.folder, "snap-root")
        snapper = snap.Snapper(binary=_mpv(), root=root)
        snapper._snap(pm, video, seg)
        self.assertAlmostEqual(seg.end, 9.35, delta=0.2)
        self.assertEqual(seg.start, 0.0)
        self.assertEqual(os.listdir(root), [], "the dump is deleted")
        snapper.close()


class Analysers(unittest.TestCase):
    """1.6.1 (BH-160-M1): what analyses, and how it is started."""

    def setUp(self):
        self.base = tempfile.mkdtemp(prefix="skip-snap-libmpv-")
        self.addCleanup(shutil.rmtree, self.base, True)

    def dll(self, *parts):
        path = os.path.join(self.base, *parts, snap.LIBMPV)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        open(path, "wb").close()
        return path

    def test_frozen_the_packages_libmpv_beside_the_backend_and_only_it(self):
        exe = os.path.join(self.base, "app", "backend",
                           "fluentfin-mpv-backend.exe")
        shipped = self.dll("app", "libmpv")
        elsewhere = self.dll("elsewhere")
        with mock.patch.object(sys, "frozen", True, create=True), \
                mock.patch.object(sys, "executable", exe), \
                mock.patch.dict(os.environ, {
                    snap.LIBMPV_ENV: os.path.dirname(elsewhere)}):
            self.assertEqual(os.path.normcase(snap.libmpv_path()),
                             os.path.normcase(shipped))
            self.assertEqual(snap.child_command(),
                             [exe, snap.ANALYSE_SWITCH])
            os.remove(shipped)
            self.assertIsNone(snap.libmpv_path(), "never another folder's")

    def test_from_source_the_folder_named_by_the_environment(self):
        named = self.dll("named")
        with mock.patch.dict(os.environ,
                             {snap.LIBMPV_ENV: os.path.dirname(named)}):
            self.assertEqual(os.path.normcase(snap.libmpv_path()),
                             os.path.normcase(named))
        command = snap.child_command()
        self.assertEqual(command[:2], [sys.executable, "-I"])
        self.assertTrue(command[2].endswith("skip_analyse.py"))
        self.assertTrue(os.path.isfile(command[2]))

    def test_a_cancel_ends_the_analysing_interpreter_itself(self):
        """Ending the analyser's process by its handle (`Snapper.cancel`)
        leaves nothing of it running, also when this Python is a venv's
        launcher with the interpreter as its child (Windows)."""
        work = tempfile.mkdtemp(dir=self.base)
        flag = os.path.join(work, "alive")
        script = os.path.join(work, "linger.py")
        with open(script, "w", encoding="utf-8") as handle:
            handle.write("import sys, time\n"
                         "deadline = time.monotonic() + 30\n"
                         "while time.monotonic() < deadline:\n"
                         "    open(sys.argv[1], 'w').close()\n"
                         "    time.sleep(0.1)\n")
        python = snap.child_command()[0]
        process = subprocess.Popen(
            [python, "-I", script, flag], stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.addCleanup(lambda: process.poll() is None and process.kill())
        deadline = time.monotonic() + 10
        while not os.path.exists(flag) and time.monotonic() < deadline:
            time.sleep(0.05)
        self.assertTrue(os.path.exists(flag), "it started")
        snapper = snap.Snapper(binary="unused", root=os.path.join(work, "r"))
        self.addCleanup(snapper.close)
        snapper._process(process)
        snapper.cancel()
        process.wait(10)
        time.sleep(0.5)
        os.remove(flag)
        time.sleep(0.5)
        self.assertFalse(os.path.exists(flag), "nothing of it still runs")

    def test_libmpv_comes_before_an_mpv_binary(self):
        named = self.dll("named")
        with mock.patch.dict(os.environ,
                             {snap.LIBMPV_ENV: os.path.dirname(named)}), \
                mock.patch.object(snap, "mpv_binary",
                                  return_value=r"C:\mpv\mpv.exe"):
            self.assertEqual(snap.find_analyser().kind, "libmpv")
        with mock.patch.object(snap, "libmpv_path", return_value=None), \
                mock.patch.object(snap, "mpv_binary",
                                  return_value=r"C:\mpv\mpv.exe"):
            self.assertEqual(snap.find_analyser(),
                             snap.Analyser("mpv", r"C:\mpv\mpv.exe"))
        with mock.patch.object(snap, "libmpv_path", return_value=None), \
                mock.patch.object(snap, "mpv_binary", return_value=None):
            self.assertIsNone(snap.find_analyser())

    def test_the_self_check_fails_without_libmpv_and_never_asks_for_mpv(self):
        import io
        import json

        out = io.StringIO()
        with mock.patch.object(snap, "libmpv_path", return_value=None), \
                mock.patch.object(snap, "mpv_binary",
                                  side_effect=AssertionError("asked")):
            self.assertEqual(snap.self_check(out), 1)
        result = json.loads(out.getvalue())
        self.assertFalse(result["ok"])
        self.assertEqual(result["error"], "no libmpv to analyse with")

    def test_a_libmpv_analysis_starts_the_child_with_the_filters(self):
        part = os.path.join(self.base, "part.mkv")
        open(part, "wb").close()
        seen = []

        class Done:
            returncode = 0

            def __init__(self, command, **kwargs):
                seen.append((command, kwargs))
                with open(command[-3], "w", encoding="utf-8") as handle:
                    handle.write("nixie-duration=6.0\n")

            def wait(self, timeout=None):
                return 0

        with mock.patch.object(snap.subprocess, "Popen", Done):
            text = snap.analyse(snap.Analyser("libmpv", r"C:\x\libmpv-2.dll"),
                                part)
        self.assertEqual(text, "nixie-duration=6.0\n")
        command, kwargs = seen[0]
        self.assertEqual(command[:len(snap.child_command())],
                         snap.child_command())
        self.assertEqual(command[-5:], [
            r"C:\x\libmpv-2.dll", part,
            os.path.join(self.base, "analyse.log"), snap.BLACK_FILTER,
            snap.SILENCE_FILTER])
        self.assertIs(kwargs["stdout"], subprocess.DEVNULL,
                      "nothing of it reaches the bridge's stdout")

    def test_the_analysis_runs_below_normal_priority_with_few_threads(self):
        """1.6.1 review: the analysis reads untimed, as fast as the CPU goes,
        and now on every PC: it must never compete with playback."""
        from jellyfin_mpv_shim import skip_analyse

        part = os.path.join(self.base, "part.mkv")
        open(part, "wb").close()
        seen = []

        class Done:
            returncode = 0

            def __init__(self, command, **kwargs):
                seen.append((command, kwargs))

            def wait(self, timeout=None):
                return 0

        with mock.patch.object(snap.subprocess, "Popen", Done):
            snap.analyse(snap.Analyser("libmpv", r"C:\x\libmpv-2.dll"), part)
            snap.analyse(snap.Analyser("mpv", r"C:\mpv\mpv.exe"), part)
        below = 0x00004000 if os.name == "nt" else 0
        for command, kwargs in seen:
            self.assertEqual(kwargs["creationflags"] & below, below, command)
        self.assertIn("--vd-lavc-threads=2", seen[1][0])
        self.assertIn(("vd-lavc-threads", "2"),
                      skip_analyse.analyse_options("a", "b"))
        if os.name != "nt":
            return
        # and Windows takes it: a process of this test's own, by its handle
        import ctypes

        process = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(30)"],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, creationflags=snap._creation_flags())
        self.addCleanup(lambda: process.poll() is None and process.kill())
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.GetPriorityClass.argtypes = [ctypes.c_void_p]
        kernel32.GetPriorityClass.restype = ctypes.c_uint32
        self.assertEqual(kernel32.GetPriorityClass(int(process._handle)),
                         below)


def _hide_mpv(test):
    """PATH and LOCALAPPDATA (WinGet's folders) pointed at an empty folder,
    and no ``mpv_ext_path``: no mpv binary is reachable, as on most PCs
    with the release package. Asserted, so the test cannot pass on a PC
    whose mpv it failed to hide."""
    from jellyfin_mpv_shim.conf import settings

    empty = tempfile.mkdtemp(prefix="skip-snap-no-mpv-")
    test.addCleanup(shutil.rmtree, empty, True)
    for patcher in (mock.patch.dict(os.environ, {"PATH": empty,
                                                 "LOCALAPPDATA": empty}),
                    mock.patch.object(settings, "mpv_ext_path", None)):
        patcher.start()
        test.addCleanup(patcher.stop)
    test.assertIsNone(shutil.which("mpv.exe"))
    test.assertIsNone(shutil.which("mpv"))
    test.assertIsNone(snap.mpv_binary(), "an mpv binary is still reachable")


def _require_libmpv(test):
    """The libmpv Nixie ships (``NIXIE_LIBMPV_DIR``, or the checkout's
    vendor/libmpv); a release's test run sets FLUENTFIN_MPV_REQUIRE_LIBMPV
    so this fails there instead of skipping."""
    path = snap.libmpv_path()
    if path:
        return path
    if os.environ.get("FLUENTFIN_MPV_REQUIRE_LIBMPV") == "1":
        test.fail("no libmpv-2.dll (set %s) and FLUENTFIN_MPV_REQUIRE_LIBMPV"
                  " is 1" % snap.LIBMPV_ENV)
    test.skipTest("no libmpv-2.dll to analyse with (set %s)"
                  % snap.LIBMPV_ENV)


class LibmpvPlayer:
    """The playing mpv for `Snapper._snap`'s dump: a libmpv handle in this
    process, no window and no sound, with what ``_snap`` reads of
    ``pm._player``."""

    def __init__(self, dll, clip):
        import json

        from jellyfin_mpv_shim import skip_analyse

        self._json = json
        self.mpv = skip_analyse.Libmpv(dll)
        for name, value in (("config", "no"), ("load-scripts", "no"),
                            ("osc", "no"), ("vo", "null"), ("ao", "null"),
                            ("force-window", "no"), ("idle", "yes"),
                            ("cache", "yes"), ("pause", "yes")):
            self.mpv.set_option(name, value)
        self.mpv.initialize("no")
        self.mpv.command("loadfile", clip)

    def _drain(self):
        while self.mpv.wait(0)[0] != 0:
            pass

    @property
    def playback_time(self):
        self._drain()
        value = self.mpv.get("playback-time")
        return float(value) if value else None

    @property
    def demuxer_cache_state(self):
        self._drain()
        value = self.mpv.get("demuxer-cache-state")
        return self._json.loads(value) if value else None

    def command(self, *args):
        self.mpv.command(*args)

    def terminate(self):
        self.mpv.close()


class NoMpvExeTest(unittest.TestCase):
    """BH-160-M1: the release package ships libmpv-2.dll and no mpv.exe,
    and 1.6.0 analysed only with an mpv binary, so with no mpv installed no
    skip rule ever snapped. Here no mpv binary is reachable (asserted), and
    a rule's edge still snaps: the playing mpv dumps its cache (libmpv, in
    this process) and the analysis runs with the same libmpv in a process
    of the backend's own. The live test above is skipped without an mpv
    binary; this one fails without libmpv when a release runs it."""

    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.mkdtemp(prefix="nixie-snap-libmpv-test-")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.folder, ignore_errors=True)

    def setUp(self):
        self.dll = _require_libmpv(self)
        _hide_mpv(self)
        self.clip = os.path.join(self.folder, "clip.mkv")
        if not os.path.isfile(self.clip):
            self.assertTrue(snap.make_test_clip(self.dll, self.clip),
                            "libmpv encodes the check clip")

    def test_the_skip_snaps_to_the_cut_with_no_mpv_binary(self):
        player = LibmpvPlayer(self.dll, self.clip)
        self.addCleanup(player.terminate)

        class PM:
            _mpv_alive = True

        pm = PM()
        pm._player = player
        pm._video = video = object()
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if snap.covered(player.demuxer_cache_state, 5, 11):
                break
            time.sleep(0.2)
        seg = Seg("Commercial", 0.0, 8.0)
        root = os.path.join(self.folder, "snap-root")
        snapper = snap.Snapper(root=root)       # finds its own analyser
        self.addCleanup(snapper.close)
        with self.assertLogs("skip_snap", "INFO") as logs:
            snapper._snap(pm, video, seg)
        self.assertAlmostEqual(seg.end, 9.35, delta=0.2, msg=logs.output)
        self.assertEqual(seg.start, 0.0)
        self.assertTrue(any("analysed with libmpv" in line
                            for line in logs.output), logs.output)
        self.assertEqual(os.listdir(root), [], "the dump is deleted")

    def test_the_self_check_passes_with_no_mpv_binary(self):
        import io
        import json

        out = io.StringIO()
        self.assertEqual(snap.self_check(out), 0, out.getvalue())
        result = json.loads(out.getvalue())
        self.assertEqual(result["analyser"], "libmpv")
        self.assertAlmostEqual(result["snapped"], snap.CHECK_SNAP,
                               delta=0.15)
        self.assertAlmostEqual(result["duration"], 20.0, delta=0.2)

    def test_the_analysis_itself_finds_the_black_and_the_silence(self):
        work = tempfile.mkdtemp(dir=self.folder)
        part = os.path.join(work, "part.mkv")
        shutil.copy(self.clip, part)
        blacks, silences, duration = snap.parse_detections(
            snap.analyse(snap.find_analyser(), part))
        self.assertAlmostEqual(duration, 20.0, delta=0.2)
        self.assertTrue(any(abs(a - 9.0) < 0.1 and abs(b - 9.6) < 0.1
                            for a, b in blacks), blacks)
        self.assertTrue(any(abs(a - 9.0) < 0.1 for a, b in silences),
                        silences)

    def test_a_file_libmpv_cannot_play_leaves_the_edge(self):
        work = tempfile.mkdtemp(dir=self.folder)
        part = os.path.join(work, "part.mkv")
        with open(part, "wb") as handle:
            handle.write(b"not a video" * 100)
        with self.assertLogs("skip_snap", "INFO") as logs:
            text = snap.analyse(snap.find_analyser(), part)
        self.assertEqual(snap.parse_detections(text or "")[2], None)
        self.assertTrue(any("ended with status 5" in line
                            for line in logs.output), logs.output)


if __name__ == "__main__":
    unittest.main()
