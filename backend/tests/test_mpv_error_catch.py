"""An mpv error is caught where the player says it is, on Python 3.14.

``except (_mpv_errors, TypeError)`` nests a tuple -- ``_mpv_errors`` is one
-- and matching any exception against it raises "TypeError: catching
classes that do not inherit from BaseException is not allowed" instead
(CPython checks only the top level of the tuple). Two upstream sites did
this: the timeline tick's volume save (`_maybe_save_volume`), so a hung mpv
logged a traceback on every tick and skipped the rest of it, and the photo
display duration in `_play_media`, so an mpv error there broke the start.

This module imports ``player``, which builds a real mpv (docs/testing.md
section 1): run it off-screen. The start runs on a `PlayerManager` made
with ``__new__``, as `test_window_embed_start` does.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import ast
import os
import sys
import threading
import unittest
from unittest import mock

sys.argv = [sys.argv[0]]      # importing the shim reaches args.get_args()

from jellyfin_mpv_shim import player as player_module  # noqa: E402
from jellyfin_mpv_shim.conf import settings  # noqa: E402
from jellyfin_mpv_shim.player import PlayerManager  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class _Reached(Exception):
    """Raised by the item's title: the start got past the photo setup."""


class _Mpv:
    """An mpv handle whose ``volume`` read and ``image_display_duration``
    write raise ``error`` (None: they work)."""

    def __init__(self, error=None):
        object.__setattr__(self, "error", error)
        object.__setattr__(self, "writes", [])
        object.__setattr__(self, "_volume", 55.0)

    @property
    def volume(self):
        if self.error is not None:
            raise self.error
        return self._volume

    def __setattr__(self, name, value):
        if name == "image_display_duration" and self.error is not None:
            raise self.error
        self.writes.append((name, value))
        object.__setattr__(self, "_volume" if name == "volume" else name,
                           value)

    def command(self, *args):
        pass

    def play(self, url):
        pass


class _Photo:
    is_photo = True
    client = None

    def __init__(self):
        self.item = {"Id": "a" * 32, "Type": "Photo", "MediaType": "Photo"}
        self.media_source = {"Path": "/media/photo.jpg", "MediaStreams": []}

    def get_proper_title(self):
        raise _Reached()


def _errors():
    """Each of the backend's mpv errors, and one more for good measure."""
    return [cls("mpv went away") for cls in player_module._mpv_errors]


class VolumeSaveTest(unittest.TestCase):
    """`_maybe_save_volume`, the timeline tick's: an mpv that does not
    answer is "no volume to save", not a traceback that skips the rest of
    the tick (`_pushed_stopped`, the idle bookkeeping)."""

    def player(self, error):
        pm = PlayerManager.__new__(PlayerManager)
        pm._player = _Mpv(error)
        pm._video = mock.Mock(item={"Type": "Movie", "MediaType": "Video"})
        return pm

    def test_an_mpv_error_is_swallowed(self):
        for error in _errors() + [TypeError("no volume yet")]:
            with self.subTest(error=type(error).__name__), \
                    mock.patch.object(settings, "save") as save:
                self.assertIsNone(self.player(error)._maybe_save_volume())
                save.assert_not_called()

    def test_a_volume_is_still_saved(self):
        """The control: the same call with mpv answering saves it."""
        saved = settings.video_volume
        self.addCleanup(setattr, settings, "video_volume", saved)
        settings.video_volume = 100
        with mock.patch.object(settings, "save") as save:
            self.player(None)._maybe_save_volume()
        self.assertEqual(settings.video_volume, 55)
        save.assert_called_once_with()


class PhotoDurationTest(unittest.TestCase):
    """The photo display duration, written before the file is handed to
    mpv: an mpv error there is logged and the start goes on."""

    def setUp(self):
        patcher = mock.patch.object(player_module, "wait_property",
                                    return_value=True)
        patcher.start()
        self.addCleanup(patcher.stop)

    def player(self, error):
        pm = PlayerManager.__new__(PlayerManager)
        pm._player = _Mpv(error)
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
        return pm

    def start(self, error):
        pm = self.player(error)
        with self.assertRaises(_Reached):
            pm._play_media(_Photo(), "http://jf.example/photo", 0)
        return pm

    def test_an_mpv_error_is_swallowed(self):
        for error in _errors():
            with self.subTest(error=type(error).__name__):
                self.start(error)

    def test_the_duration_is_still_set(self):
        """The control: with mpv answering, the photo gets its duration."""
        pm = self.start(None)
        self.assertIn(("image_display_duration",
                       max(1, int(settings.photo_display_secs))),
                      pm._player.writes)


class NoNestedExceptTupleTest(unittest.TestCase):
    """Nothing in the package catches a tuple that holds `_mpv_errors` (or
    any other tuple constant) as an element: unpack it."""

    #: Module-level names bound to a tuple of exception classes.
    TUPLES = {"_mpv_errors", "ZIP_READ_ERRORS"}

    def test_no_except_nests_a_tuple(self):
        package = os.path.join(ROOT, "jellyfin_mpv_shim")
        found = []
        for folder, _dirs, files in os.walk(package):
            for name in files:
                if not name.endswith(".py"):
                    continue
                path = os.path.join(folder, name)
                with open(path, encoding="utf-8") as fh:
                    tree = ast.parse(fh.read(), path)
                for node in ast.walk(tree):
                    if not (isinstance(node, ast.ExceptHandler)
                            and isinstance(node.type, ast.Tuple)):
                        continue
                    for element in node.type.elts:
                        nested = isinstance(element, (ast.Tuple, ast.BinOp))
                        name_ = getattr(element, "id",
                                        getattr(element, "attr", None))
                        if nested or name_ in self.TUPLES:
                            found.append("%s:%d" % (
                                os.path.relpath(path, ROOT), node.lineno))
        self.assertEqual(found, [])

    def test_the_names_are_tuples(self):
        """The control: the names above are the tuples they stand for."""
        from jellyfin_mpv_shim.archive_errors import ZIP_READ_ERRORS

        self.assertIsInstance(player_module._mpv_errors, tuple)
        self.assertIsInstance(ZIP_READ_ERRORS, tuple)


if __name__ == "__main__":
    unittest.main()
