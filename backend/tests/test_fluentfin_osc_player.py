"""FluentFin MPV: which OSC the player loads in bridge mode, and what a
subtitle sidecar is called in mpv.

Imports `player`, which builds mpv at import (STARTHERE.md): run it with
the rest of the suite, off-screen.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import os
import sys
import threading
import unittest

sys.argv = [sys.argv[0]]      # importing player reaches args.get_args()

from jellyfin_mpv_shim import fluentfin_osc  # noqa: E402
from jellyfin_mpv_shim.media import Video  # noqa: E402
from jellyfin_mpv_shim.player import BUILTIN_OSC, PlayerManager  # noqa: E402


class Active:
    def __enter__(self):
        self._was = fluentfin_osc.active()
        fluentfin_osc.configure(True)

    def __exit__(self, *exc):
        fluentfin_osc.configure(self._was)


class FakeMpv:
    def __init__(self, version):
        self.mpv_version = version
        self.commands = []
        self.adds = []

    def command(self, *args):
        self.commands.append(args)

    def command_async(self, *args, callback=None):
        self.adds.append(args)


def pm_with(mpv):
    pm = PlayerManager.__new__(PlayerManager)
    pm._player = mpv
    return pm


class WhichOscTest(unittest.TestCase):
    #: What this machine's winget mpv says: a 0.41 development build whose
    #: stock OSC has no Preview API, so it drew no trickplay previews.
    DEV = "mpv v0.41.0-dev-g41f6a6450"

    def loaded(self, version):
        mpv = FakeMpv(version)
        pm_with(mpv)._load_classic_osc()
        return [c[1] for c in mpv.commands if c[0] == "load-script"]

    def test_the_bridge_loads_the_fork_whatever_the_version(self):
        with Active():
            for version in (self.DEV, "mpv 0.41.0", "mpv 0.42.0",
                            "mpv 0.40.0"):
                with self.subTest(version=version):
                    scripts = self.loaded(version)
                    self.assertEqual(len(scripts), 1, scripts)
                    self.assertEqual(os.path.basename(scripts[0]),
                                     fluentfin_osc.OSC_SCRIPT)

    def test_outside_the_bridge_nothing_changes(self):
        fluentfin_osc.configure(False)
        self.assertEqual(self.loaded("mpv 0.41.0"), [BUILTIN_OSC])
        self.assertEqual(os.path.basename(self.loaded("mpv 0.40.0")[0]),
                         "trickplay-osc.lua")


class SidecarIsLabelledTest(unittest.TestCase):
    """mpv titles a sidecar with the last part of its url ("Stream.srt")
    and knows no language for it: the OSC's subtitle button said nothing
    useful. sub-add is told the stream's own title and language."""

    URL = "http://srv/Videos/x/s/Subtitles/3/0/Stream.srt"

    def add(self, streams):
        mpv = FakeMpv("mpv 0.41.0")
        pm = pm_with(mpv)
        pm._lock = threading.RLock()
        pm._pending_subs = {}
        pm._play_epoch = 1
        pm.put_task = lambda *a: None
        v = Video.__new__(Video)
        v.subtitle_url = {3: self.URL}
        v.media_source = {"MediaStreams": streams}
        pm._video = v
        pm._add_external_sub(3)
        self.assertEqual(len(mpv.adds), 1)
        return mpv.adds[0]

    def test_title_and_language(self):
        args = self.add([{"Type": "Subtitle", "Index": 3, "Language": "eng",
                          "Title": "SDH", "DeliveryMethod": "External"}])
        self.assertEqual(args, ("sub-add", self.URL, "auto", "SDH", "eng"))

    def test_none_known_is_empty_which_mpv_reads_as_not_given(self):
        args = self.add([])
        self.assertEqual(args, ("sub-add", self.URL, "auto", "", ""))


if __name__ == "__main__":
    unittest.main()
