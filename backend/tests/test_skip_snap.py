"""1.6 skip: the margin -- a skip rule's edge snaps to the nearest black or
silence (`skip_snap`). The choice is pure; the analysis and the dump run a
real mpv with no window and no sound (``--vo=null --ao=null``) on a clip
generated here (a test pattern and a tone with one black, silent stretch;
never real footage), and are skipped when no mpv is found."""

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
import time
import unittest
import uuid

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


if __name__ == "__main__":
    unittest.main()
