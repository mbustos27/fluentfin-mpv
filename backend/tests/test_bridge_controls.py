"""``set_controls``: who draws the player controls (BRIDGE.md section 4,
`jellyfin_mpv_shim.bridge_controls`), with no player behind it.

fluentfin-mpv only. The bridge-level wiring (the command, the re-apply at
every item start after the shim's own sub-pos write) is in
tests/test_bridge.py, ``SetControlsTest``.
"""

# Run as a script, this is what puts the repo root on sys.path (see
# tests/test_bridge_protocol.py).
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import sys
import unittest

sys.argv = [sys.argv[0]]      # importing the shim reaches args.get_args()

from jellyfin_mpv_shim.bridge_controls import UiControls  # noqa: E402


class FakeMpv:
    """The mpv handle: ``sub-pos`` as mpv keeps it, written only through
    ``set`` with a string (a number is refused on the external backend),
    and the shim's own write at an item start (`shim_writes`)."""

    def __init__(self, sub_pos=100.0):
        self.sub_pos = sub_pos
        self.commands = []

    def command(self, *args):
        self.commands.append(args)
        if args[:2] == ("set", "sub-pos"):
            if not isinstance(args[2], str):
                raise TypeError("set takes a string")
            self.sub_pos = float(args[2])

    def shim_writes(self, base):
        """`PlayerManager.update_subtitle_visuals`, at an item start."""
        self.sub_pos = float(base)


class FakePM:
    def __init__(self, player, osc_enabled=True):
        self._player = player
        self.osc_enabled = osc_enabled
        self.osc_calls = []

    def enable_osc(self, enabled):
        self.osc_calls.append(enabled)


def sets(player):
    return [c[2] for c in player.commands if c[:2] == ("set", "sub-pos")]


class UiControlsTest(unittest.TestCase):
    def test_mpv_is_the_start_and_touches_nothing(self):
        controls, player = UiControls(), FakeMpv()
        pm = FakePM(player)
        controls.apply(pm, 100)
        self.assertEqual(pm.osc_calls, [True])
        self.assertEqual(player.commands, [], "the shim's sub-pos is left alone")
        self.assertFalse(controls.ui)

    def test_ui_hides_the_osc_and_mpv_gives_it_back(self):
        controls = UiControls()
        pm = FakePM(FakeMpv())
        controls.set("ui", 0.0)
        controls.apply(pm, 100)
        controls.set("mpv", 0.0)
        controls.apply(pm, 100)
        self.assertEqual(pm.osc_calls, [False, True])
        # "No player controls" in the shim's own settings stays no controls.
        quiet = FakePM(FakeMpv(), osc_enabled=False)
        controls.apply(quiet, 100)
        self.assertEqual(quiet.osc_calls, [False])

    def test_subtitles_move_with_the_bar_and_never_walk(self):
        # Shown, hidden, shown, hidden, shown: each show lands on the same
        # place and each hide on the shim's own. Moving from mpv's current
        # value instead would push the subtitles further up at every show.
        controls, player = UiControls(), FakeMpv()
        pm = FakePM(player)
        for inset in (0.18, 0.0, 0.18, 0.0, 0.18):
            controls.set("ui", inset)
            controls.apply(pm, 100)
        self.assertEqual(sets(player), ["82", "100", "82", "100", "82"])
        self.assertEqual(player.sub_pos, 82.0)

    def test_the_base_is_the_shims_own_position(self):
        # "middle" (80) in the shim's settings: the lift is from there.
        controls, player = UiControls(), FakeMpv(sub_pos=80.0)
        controls.set("ui", 0.1)
        controls.apply(FakePM(player), 80)
        self.assertEqual(sets(player), ["70"])

    def test_the_same_inset_twice_writes_once(self):
        controls, player = UiControls(), FakeMpv()
        pm = FakePM(player)
        for _ in range(3):
            controls.set("ui", 0.2)
            controls.apply(pm, 100)
        self.assertEqual(sets(player), ["80"])

    def test_every_item_start_lifts_again_after_the_shims_write(self):
        controls, player = UiControls(), FakeMpv()
        pm = FakePM(player)
        controls.set("ui", 0.2)
        controls.apply(pm, 100)
        for _ in range(3):
            player.shim_writes(100)
            controls.apply(pm, 100, item_started=True)
            self.assertEqual(player.sub_pos, 80.0)
        self.assertEqual(sets(player), ["80"] * 4)
        # Hidden bar at an item start: the shim's value stays, nothing sent.
        controls.set("ui", 0.0)
        controls.apply(pm, 100)
        player.shim_writes(100)
        controls.apply(pm, 100, item_started=True)
        self.assertEqual(sets(player), ["80"] * 4 + ["100"])

    def test_back_to_mpv_puts_the_subtitles_back(self):
        controls, player = UiControls(), FakeMpv()
        pm = FakePM(player)
        controls.set("ui", 0.25)
        controls.apply(pm, 100)
        controls.set("mpv", 0.25)
        controls.apply(pm, 100)
        self.assertEqual(sets(player), ["75", "100"])
        self.assertEqual(controls.inset, 0.0)

    def test_a_restarted_mpv_is_lifted_again(self):
        controls = UiControls()
        first, second = FakeMpv(), FakeMpv()
        controls.set("ui", 0.1)
        controls.apply(FakePM(first), 100)
        controls.apply(FakePM(second), 100, item_started=True)
        self.assertEqual(sets(first), ["90"])
        self.assertEqual(sets(second), ["90"])

    def test_the_range_holds(self):
        controls, player = UiControls(), FakeMpv(sub_pos=20.0)
        controls.set("ui", 0.5)
        controls.apply(FakePM(player), 20)
        self.assertEqual(sets(player), ["0"], "never below mpv's range")

    def test_mpv_errors_reach_the_caller(self):
        class Gone(FakeMpv):
            def command(self, *args):
                raise BrokenPipeError("mpv went away")

        controls = UiControls()
        controls.set("ui", 0.2)
        with self.assertRaises(BrokenPipeError):
            controls.apply(FakePM(Gone()), 100)


if __name__ == "__main__":
    unittest.main()
