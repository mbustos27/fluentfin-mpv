"""A playback start never fullscreens an embedded window.

`_play_media` turns fullscreen on for video when ``settings.fullscreen``
says so. In an embedded window (the bridge, `jellyfin_mpv_shim.window_embed`)
that is the window owner's decision -- FluentFin has its own auto-fullscreen
-- so the start must not write mpv's ``fs`` there, whatever the setting
says (BRIDGE.md section 4; docs/bridge.md section 4).

`test_window_embed` drives `WindowMixin` without importing ``player``; this
needs `_play_media` itself, which lives on `PlayerManager`, so this module
imports ``player`` -- which builds a real mpv, as `test_window_geometry`
does (docs/testing.md section 1). The start runs on a `PlayerManager` made
with ``__new__`` and stops at the line after the decision: the title, read
from the video, raises.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import sys
import threading
import unittest
from unittest import mock

sys.argv = [sys.argv[0]]      # importing the shim reaches args.get_args()

from jellyfin_mpv_shim import player as player_module  # noqa: E402
from jellyfin_mpv_shim import window_embed  # noqa: E402
from jellyfin_mpv_shim.conf import settings  # noqa: E402
from jellyfin_mpv_shim.player import PlayerManager  # noqa: E402


class _Reached(Exception):
    """Raised by the video's title: the start got past the decision."""


class _Mpv:
    """An mpv handle that records every property write."""

    def __init__(self):
        object.__setattr__(self, "writes", [])

    def __setattr__(self, name, value):
        self.writes.append((name, value))
        object.__setattr__(self, name, value)

    def command(self, *args):
        pass

    def play(self, url):
        pass


class _Video:
    """A video item -- not audio, not a photo -- as `_play_media` reads it."""

    is_photo = False
    client = None

    def __init__(self):
        self.item = {"Id": "a" * 32, "Type": "Movie", "MediaType": "Video"}
        self.media_source = {"Path": "/media/film.mkv", "MediaStreams": []}
        self.titles = 0

    def get_proper_title(self):
        # The first read is the one before the file is handed over
        # (FluentFin MPV: the title is set before the url); the second,
        # after the fullscreen decision, ends the start.
        self.titles += 1
        if self.titles > 1:
            raise _Reached()
        return "Film"


def _player():
    """A `PlayerManager` with what `_play_media` touches before its
    fullscreen decision; the rest of the start is stubbed or never
    reached."""
    pm = PlayerManager.__new__(PlayerManager)
    pm._player = _Mpv()
    pm._lock = threading.RLock()
    pm._video = None
    pm.menu = mock.Mock()
    pm.trickplay = None
    pm.repeat_mode = "off"
    pm.fullscreen_disable = False
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
    return pm


class StartFullscreenTest(unittest.TestCase):
    def setUp(self):
        saved = settings.fullscreen
        self.addCleanup(setattr, settings, "fullscreen", saved)
        settings.fullscreen = True          # the setting says fullscreen
        self.addCleanup(window_embed.configure, None)
        patcher = mock.patch.object(player_module, "wait_property",
                                    return_value=True)
        patcher.start()
        self.addCleanup(patcher.stop)

    def start(self):
        pm = _player()
        with self.assertRaises(_Reached):
            pm._play_media(_Video(), "http://jf.example/stream", 0)
        return [w for w in pm._player.writes if w[0] == "fs"]

    def test_an_embedded_start_leaves_fullscreen_to_the_owner(self):
        window_embed.configure(0x1234)
        self.assertEqual(self.start(), [])

    def test_a_standalone_start_still_goes_fullscreen(self):
        """The control: the same start with the mode off writes it, so the
        test above is about the mode and not a start that never got there."""
        window_embed.configure(None)
        self.assertEqual(self.start(), [("fs", True)])


if __name__ == "__main__":
    unittest.main()
