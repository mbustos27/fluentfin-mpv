"""FluentFin MPV (1.5): a restart -- a track change on a transcode, a quality
change -- is the same viewing on the server.

`restart_playback` renegotiates on the video playing. Upstream sent Playing
for the new play session and never a Stopped for the one it left, so the QA
servers (Jellyfin 10.11.11 and 12.4.0) logged two starts and one finish for
one viewing. It now asks play() for ``no_initial_timeline``: progress under
the new PlaySessionId, the one start `_play_media` would otherwise repeat
left out. The bridge's side (one Playing, one Stopped, the controls put back)
is in test_bridge (``test_a_restart_is_one_viewing_on_the_server``,
``test_a_restart_lifts_the_subtitles_again``).
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
import types
import unittest

sys.argv = [sys.argv[0]]      # importing player reaches args.get_args()

from jellyfin_mpv_shim.player import PlayerManager  # noqa: E402


def _manager(video, at):
    pm = PlayerManager.__new__(PlayerManager)
    pm._lock = threading.RLock()
    pm._video = video
    pm._player = types.SimpleNamespace(playback_time=at)
    pm.plays = []
    pm.play = lambda *args, **kwargs: pm.plays.append((args, kwargs))
    return pm


class RestartPlaybackTest(unittest.TestCase):
    def test_a_restart_asks_for_no_second_playing(self):
        video = object()
        pm = _manager(video, 42.5)
        self.assertTrue(pm.restart_playback())
        self.assertEqual(pm.plays, [((video, 42.5), {
            "no_initial_timeline": True, "apply_memory": False})])

    def test_nothing_playing_restarts_nothing(self):
        pm = _manager(None, 0)
        self.assertFalse(pm.restart_playback())
        self.assertEqual(pm.plays, [])


if __name__ == "__main__":
    unittest.main()
