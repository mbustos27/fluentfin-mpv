"""mpv drawing into a window another process owns (the bridge's embedded
mode, `jellyfin_mpv_shim.window_embed`).

The window belongs to FluentFin: the shim must never move, resize, retitle,
raise or fullscreen it, nor save a geometry for it, and every way the user
can toggle fullscreen has to become a request to the owner instead
(BRIDGE.md section 4; docs/bridge.md section 4).

`WindowMixin` is driven on a minimal host class with a recording mpv handle,
because importing ``player`` builds a real mpv (docs/testing.md section 1).
The methods that import ``player`` per call for ``_mpv_errors`` get a
stand-in module for the duration. Each refusal has a control case with the
mode off, so a test cannot pass because the method never writes at all.
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
from collections import OrderedDict
from unittest import mock

sys.argv = [sys.argv[0]]      # importing the shim reaches args.get_args()

from jellyfin_mpv_shim import mpv_options  # noqa: E402
from jellyfin_mpv_shim import window_embed  # noqa: E402
from jellyfin_mpv_shim.conf import settings  # noqa: E402
from jellyfin_mpv_shim.player_window import WindowMixin  # noqa: E402


class Handle:
    """An mpv handle that records every property write."""

    def __init__(self):
        object.__setattr__(self, "writes", [])
        object.__setattr__(self, "commands", [])
        for name, value in (("fs", False), ("fullscreen", False),
                            ("window_maximized", False),
                            ("window_minimized", False), ("osd_width", 1280),
                            ("osd_height", 720), ("geometry", "1280x720"),
                            ("force_window", True), ("keepaspect", True),
                            ("playback_abort", True)):
            object.__setattr__(self, name, value)

    def __setattr__(self, name, value):
        self.writes.append((name, value))
        object.__setattr__(self, name, value)
        if name == "fs":
            object.__setattr__(self, "fullscreen", value)

    def command(self, *args):
        self.commands.append(args)


class GatedHandle(Handle):
    """A handle whose ``fs`` write, from one chosen thread, stops halfway:
    the value already decided, the write not yet landed."""

    def __init__(self):
        super().__init__()
        #: (thread, entered, go) or None.
        object.__setattr__(self, "gate", None)

    def __setattr__(self, name, value):
        gate = self.gate
        if (name == "fs" and gate is not None
                and threading.current_thread() is gate[0]):
            gate[1].set()
            gate[2].wait(5)
        super().__setattr__(name, value)


class ReadCountingHandle(Handle):
    """A handle that records which window properties were read."""

    WATCHED = ("fullscreen", "window_maximized", "osd_width", "osd_height")

    def __init__(self):
        super().__init__()
        object.__setattr__(self, "reads", [])

    def __getattribute__(self, name):
        if name in ReadCountingHandle.WATCHED:
            object.__getattribute__(self, "reads").append(name)
        return object.__getattribute__(self, name)


class Host(WindowMixin):
    def __init__(self):
        self._player = Handle()
        self._video = None
        self._mpv_alive = True
        self._loading = False
        self.mpvtk_active = False
        self._geometry_armed = "1280x720"
        self.fullscreen_disable = False
        self.on_decorations_changed = None
        self._lock = threading.RLock()
        self.tasks = []

    def put_task(self, func, *args):
        self.tasks.append((func, args))

    def run_tasks(self):
        while self.tasks:
            func, args = self.tasks.pop(0)
            func(*args)

    def _library_showing(self):
        return False

    def _handle_mpv_disconnect(self):
        pass


class EmbedCase(unittest.TestCase):
    embedded = True

    def setUp(self):
        self.requests = []
        window_embed.configure(0x1234 if self.embedded else None)
        window_embed.on_fullscreen_request = self.requests.append
        self.addCleanup(window_embed.configure, None)
        self.addCleanup(setattr, window_embed, "on_fullscreen_request", None)
        fake_player = types.ModuleType("jellyfin_mpv_shim.player")
        fake_player._mpv_errors = (BrokenPipeError,)
        fake_player.win_utils = None
        patcher = mock.patch.dict(sys.modules,
                                  {"jellyfin_mpv_shim.player": fake_player})
        patcher.start()
        self.addCleanup(patcher.stop)
        self.host = Host()

    def set(self, **values):
        for key, value in values.items():
            old = getattr(settings, key)
            self.addCleanup(setattr, settings, key, old)
            setattr(settings, key, value)

    def writes(self, *names):
        return [w for w in self.host._player.writes if w[0] in names]


class FullscreenIsARequestTest(EmbedCase):
    def test_a_claimed_key_or_remote_asks_the_owner(self):
        with mock.patch.object(settings, "save") as save:
            self.host.set_fullscreen(True, persist=True)
        self.assertEqual(self.requests, [True])
        self.assertEqual(self.writes("fs", "fullscreen"), [])
        save.assert_not_called()

    def test_toggle_reads_the_owners_state(self):
        window_embed.ui_fullscreen = True
        self.host.toggle_fullscreen()
        self.assertEqual(self.requests, [False])

    def test_mpvs_own_toggle_is_a_request_and_is_put_back(self):
        """The double-click binding and the OSC button flip the property
        inside mpv; the observer is where they arrive."""
        self.host._player.fs = True             # mpv flipped it
        self.host._on_embedded_fullscreen("fullscreen", True)
        self.assertEqual(self.requests, [True])
        self.host.run_tasks()
        self.assertIs(self.host._player.fs, False, "back to the owner's")

    def test_an_echo_of_the_owners_state_is_not_a_request(self):
        for value in (False, False, False):
            self.host._on_embedded_fullscreen("fullscreen", value)
        window_embed.ui_fullscreen = True
        self.host._on_embedded_fullscreen("fullscreen", True)
        self.assertEqual(self.requests, [])
        self.assertEqual(self.host.tasks, [])

    def test_the_owner_answering_first_wins_over_the_put_back(self):
        self.host._player.fs = True
        self.host._on_embedded_fullscreen("fullscreen", True)
        self.host.set_embedded_fullscreen(True)      # the UI said yes
        self.host.run_tasks()
        self.assertIs(self.host._player.fs, True)

    def test_set_embedded_fullscreen_mirrors_into_mpv(self):
        self.host.set_embedded_fullscreen(True)
        self.assertIs(window_embed.ui_fullscreen, True)
        self.assertEqual(self.writes("fs"), [("fs", True)])
        # ...which the observer then sees as an echo.
        self.host._on_embedded_fullscreen("fullscreen", True)
        self.assertEqual(self.requests, [])

    def test_a_round_trip_over_several_toggles_does_not_walk(self):
        for _ in range(3):
            self.host._player.fs = not window_embed.ui_fullscreen
            self.host._on_embedded_fullscreen("fullscreen",
                                              self.host._player.fs)
            want = self.requests[-1]
            self.host.set_embedded_fullscreen(want)
            self.host.run_tasks()
            self.assertIs(self.host._player.fs, want)
        self.assertEqual(self.requests, [True, False, True])

    def test_the_put_back_and_the_owners_answer_do_not_interleave(self):
        """The put-back runs on the action thread, the owner's answer on the
        bridge's control worker. Interleaved, the put-back read the old
        state, the answer landed, and the put-back then wrote the old state
        over it: mpv's property disagreeing with the window, which the
        observer reads as a fresh request."""
        handle = GatedHandle()
        self.host._player = handle
        object.__setattr__(handle, "fs", True)          # mpv flipped it
        object.__setattr__(handle, "fullscreen", True)
        self.host._on_embedded_fullscreen("fullscreen", True)
        self.assertEqual(self.requests, [True])
        entered, go = threading.Event(), threading.Event()
        put_back = threading.Thread(target=self.host.run_tasks)
        object.__setattr__(handle, "gate", (put_back, entered, go))
        self.addCleanup(go.set)
        put_back.start()
        self.assertTrue(entered.wait(5), "the put-back never wrote")
        # It has read the owner's state (not fullscreen) and is writing it;
        # now the owner says yes.
        answer = threading.Thread(target=self.host.set_embedded_fullscreen,
                                  args=(True,))
        answer.start()
        answer.join(0.3)
        go.set()
        put_back.join(5)
        answer.join(5)
        self.assertIs(window_embed.ui_fullscreen, True)
        self.assertIs(handle.fs, True, "mpv must agree with the window")

    def test_playback_and_menu_paths_do_not_fullscreen(self):
        self.set(fullscreen=True)
        self.host.browse_yield()
        self.assertEqual(self.writes("fs"), [])
        self.host._apply_browse_fullscreen()
        self.assertEqual(self.writes("fs"), [])


class TheWindowIsNotOursTest(EmbedCase):
    def test_geometry_is_never_written(self):
        # A live size that is not the armed one, so a write would happen.
        object.__setattr__(self.host._player, "osd_width", 1600)
        object.__setattr__(self.host._player, "osd_height", 900)
        self.host._sync_window_geometry()
        self.host._rearm_window_geometry((800, 600))
        self.assertEqual(self.writes("geometry"), [])

    def test_a_load_does_not_even_read_the_window_size(self):
        """`_sync_window_geometry` runs on every load; embedded, the size
        is the owner's and the reads are IPC round trips for nothing.
        (`_rearm_window_geometry` would refuse the write anyway: this pins
        the earlier return too.)"""
        self.host._player = ReadCountingHandle()
        object.__setattr__(self.host._player, "osd_width", 1600)
        self.host._sync_window_geometry()
        self.assertEqual(self.host._player.reads, [])

    def test_geometry_is_never_saved(self):
        self.set(remember_window_size=True, window_width=1111)
        with mock.patch.object(settings, "save") as save:
            self.host._save_window_geometry()
        self.assertEqual(settings.window_width, 1111)
        save.assert_not_called()

    def test_the_surface_is_never_dropped(self):
        self.host._set_force_window(False)
        self.assertEqual(self.writes("force_window"), [])

    def test_no_raise_maximize_minimize_or_drag(self):
        self.host.raise_window()
        self.assertIs(self.host.toggle_window_maximized(), False)
        self.assertIs(self.host.minimize_window(), False)
        self.assertIs(self.host.begin_window_drag(), False)
        self.assertEqual(self.writes("window_maximized", "window_minimized"),
                         [])
        self.assertEqual(self.host._player.commands, [])


class StandaloneControlTest(EmbedCase):
    """The same calls with the mode off: today's behaviour, so the tests
    above are about the mode and not about methods that never write."""

    embedded = False

    def test_fullscreen_is_written(self):
        self.host.set_fullscreen(True)
        self.assertEqual(self.writes("fs"), [("fs", True)])
        self.assertEqual(self.requests, [])

    def test_geometry_is_written(self):
        self.host._rearm_window_geometry((800, 600))
        self.assertEqual(self.writes("geometry"), [("geometry", "800x600")])

    def test_a_load_reads_the_window_size(self):
        self.host._player = ReadCountingHandle()
        object.__setattr__(self.host._player, "osd_width", 1600)
        self.host._sync_window_geometry()
        self.assertIn("osd_width", self.host._player.reads)
        self.assertEqual(self.writes("geometry"), [("geometry", "1600x720")])

    def test_maximize_and_minimize_are_written(self):
        self.assertIs(self.host.toggle_window_maximized(), True)
        self.assertIs(self.host.minimize_window(), True)
        self.assertEqual(len(self.writes("window_maximized",
                                         "window_minimized")), 2)

    def test_the_observer_does_nothing(self):
        self.host._on_embedded_fullscreen("fullscreen", True)
        self.assertEqual(self.requests, [])


class OptionsTest(unittest.TestCase):
    def tearDown(self):
        window_embed.configure(None)

    def options(self):
        return OrderedDict([("geometry", "1280x720"), ("title", "x"),
                            ("border", False), ("window_maximized", True),
                            ("auto_window_resize", False), ("osc", False)])

    def test_off_changes_nothing(self):
        window_embed.configure(None)
        self.assertEqual(window_embed.apply_to_options(self.options()),
                         self.options())

    def test_embedded(self):
        window_embed.configure(1311242)
        got = window_embed.apply_to_options(self.options())
        for key in ("geometry", "title", "border", "window_maximized"):
            self.assertNotIn(key, got)
        self.assertEqual(got["wid"], "1311242")
        self.assertIs(got["auto_window_resize"], False)
        self.assertIs(got["force_window"], True)
        self.assertIs(got["idle"], True)
        self.assertIs(got["window_dragging"], False)
        self.assertIs(got["fullscreen"], False)
        self.assertEqual(got["background_color"], "#000000")
        self.assertIs(got["osc"], False, "everything else is left alone")

    def test_the_handle_is_unsigned_32_bit(self):
        window_embed.configure(0xFFFFFFFE)
        self.assertEqual(window_embed.apply_to_options({})["wid"],
                         "4294967294")

    def test_fullscreen_starts_at_the_owners_state(self):
        window_embed.configure(5)
        window_embed.ui_fullscreen = True
        self.assertIs(window_embed.apply_to_options({})["fullscreen"], True)

    def test_build_mpv_options_applies_it_last(self):
        window_embed.configure(77)
        got = mpv_options.build_mpv_options("mpv", [], True, False)
        self.assertEqual(got["wid"], "77")
        self.assertNotIn("geometry", got)
        self.assertEqual(list(got)[-1], "background_color")
        window_embed.configure(None)
        got = mpv_options.build_mpv_options("mpv", [], True, False)
        self.assertNotIn("wid", got)
        self.assertIn("geometry", got)

    def test_a_request_handler_that_raises_is_contained(self):
        window_embed.on_fullscreen_request = mock.Mock(
            side_effect=RuntimeError("ui gone"))
        self.addCleanup(setattr, window_embed, "on_fullscreen_request", None)
        window_embed.request_fullscreen(True)      # must not raise
        window_embed.on_fullscreen_request = None
        window_embed.request_fullscreen(False)     # nobody listening


if __name__ == "__main__":
    unittest.main()
