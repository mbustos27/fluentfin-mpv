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
        """The fork's bar: fluentfin.lua (the key map, which goes with
        either bar), then the fork; never mpv's own OSC."""
        with Active():
            for version in (self.DEV, "mpv 0.41.0", "mpv 0.42.0",
                            "mpv 0.40.0"):
                with self.subTest(version=version):
                    scripts = self.loaded(version)
                    self.assertEqual([os.path.basename(s) for s in scripts],
                                     ["fluentfin.lua",
                                      fluentfin_osc.OSC_SCRIPT])

    def test_the_uosc_bar_takes_the_forks_place(self):
        from jellyfin_mpv_shim import fluentfin_bar

        was = fluentfin_osc.active(), fluentfin_osc.bar()
        fluentfin_osc.configure(True, bar=fluentfin_bar.UOSC)
        try:
            for version in (self.DEV, "mpv 0.42.0"):
                with self.subTest(version=version):
                    self.assertEqual(self.loaded(version),
                                     [fluentfin_bar.uosc_dir(),
                                      fluentfin_bar.lua_script()])
        finally:
            fluentfin_osc.configure(was[0], bar=was[1] or fluentfin_bar.FORK)

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


class _Reached(Exception):
    """The start reached its load wait: mpv has the url, which is what the
    test is about."""


class _TitleMpv:
    """mpv as far as its title goes: ``media-title`` is
    ``force-media-title`` when that is set, else the file's url (a stream
    has no title tag). ``seen``: every ``media-title`` from the moment mpv
    has the file -- what uosc's top bar and the OSC show."""

    def __init__(self):
        for name, value in (("seen", []), ("url", None),
                            ("force_media_title", "")):
            object.__setattr__(self, name, value)

    def media_title(self):
        return self.force_media_title or self.url

    def __setattr__(self, name, value):
        object.__setattr__(self, name, value)
        if self.url is not None:
            self.seen.append(self.media_title())

    def command(self, *args):
        pass

    def play(self, url):
        object.__setattr__(self, "url", url)
        self.seen.append(self.media_title())


class _TranscodedEpisode:
    is_photo = False
    client = None
    TITLE = "Show — S1E2 · Name (Transcode)"

    def __init__(self):
        self.item = {"Id": "a" * 32, "Type": "Episode", "MediaType": "Video"}
        self.media_source = {"Path": "/media/ep.mkv", "MediaStreams": []}
    def get_proper_title(self):
        return self.TITLE


class TitleNeverTheUrlTest(unittest.TestCase):
    """The reviewer's case: a transcode's url carries ``ApiKey`` (the
    token), and mpv's ``media-title`` is the url until ``force-media-title``
    is set -- which the start did only after the load wait, so uosc's top
    bar could show the url while the file opened."""

    URL = ("http://jf.example/videos/%s/master.m3u8?MediaSourceId=s"
           "&ApiKey=tok-abcdef-0123456789&api_key=tok-abcdef-0123456789"
           % ("a" * 32))

    def test_the_title_is_set_before_mpv_has_the_url(self):
        from unittest import mock

        from jellyfin_mpv_shim import player as player_module

        pm = PlayerManager.__new__(PlayerManager)
        pm._player = _TitleMpv()
        pm._lock = threading.RLock()
        pm._video = None
        pm.menu = mock.Mock()
        pm.trickplay = None
        pm.repeat_mode = "off"
        pm.fullscreen_disable = True
        pm._aspect_override = None
        pm._deinterlace_override = None
        pm._load_generation = 0
        pm._load_failed = threading.Event()
        pm._load_completed = threading.Event()
        pm._load_cancelled = False
        pm._failed_playback = None
        for name in ("_ensure_mpv", "resume_colorspace_hint",
                     "_apply_render_presets", "_notify_load_start",
                     "_sync_window_geometry"):
            setattr(pm, name, lambda *args: None)
        # The load wait is where the file opens: the start ends there.
        with mock.patch.object(player_module, "wait_property",
                               side_effect=_Reached()), \
                self.assertRaises(_Reached):
            pm._play_media(_TranscodedEpisode(), self.URL, 0)
        seen = pm._player.seen
        self.assertTrue(seen, "mpv never had the file")
        self.assertEqual(seen[0], _TranscodedEpisode.TITLE)
        for title in seen:
            self.assertNotIn("apikey", title.lower())
            self.assertNotIn("api_key", title.lower())


if __name__ == "__main__":
    unittest.main()
