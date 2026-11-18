"""FluentFin MPV (1.2.0): the change from one episode to the next.

The user's 1.1.3 log, between episodes: mpv opened each new file on its own
default tracks, then the start selected the chosen ones (`configure_streams`):
mpv printed its whole track list at every write ("Track switched", the audio
seemingly re-selected, the subtitles flapping), and where the chosen audio was
not the file's default it opened a second audio decoder on every episode
(measured off-screen against the UI harness's fake server: aac opened, then
"Uninit decoder", "dropping frame due to pin disconnect", ac3). Now the
loadfile carries the tracks as per-file options, and a track mpv already holds
is not written again. And the volume: the per-type volume re-applied at each
start was the one saved on the timeline tick, so a change in an episode's last
seconds was undone by the next one.
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
from types import SimpleNamespace as NS
from unittest import mock

sys.argv = [sys.argv[0]]      # importing player reaches args.get_args()

from jellyfin_mpv_shim import player as player_module  # noqa: E402
from jellyfin_mpv_shim.conf import settings  # noqa: E402
from jellyfin_mpv_shim.media import Video  # noqa: E402
from jellyfin_mpv_shim.player import (  # noqa: E402
    PlayerManager, loadfile_takes_index, past_credits, same_track,
    track_choice)


class _Reached(Exception):
    """The start got past the load: the test has what it came for."""


class _Mpv:
    """mpv as both bindings show it: a property is read from mpv, never kept
    as a Python attribute (one in ``__dict__`` is a refused write's shadow).
    Records writes, commands and plain loads."""

    def __init__(self, **props):
        object.__setattr__(self, "props", dict(props))
        object.__setattr__(self, "writes", [])
        object.__setattr__(self, "commands", [])
        object.__setattr__(self, "loads", [])
        object.__setattr__(self, "refuse_options", False)

    def __getattr__(self, name):
        try:
            return self.__dict__["props"][name]
        except KeyError:
            raise AttributeError(name) from None

    def __setattr__(self, name, value):
        self.writes.append((name, value))
        self.props[name] = value

    def command(self, name, *args):
        if name == "loadfile" and self.refuse_options:
            raise ValueError("invalid parameter")
        self.commands.append((name,) + args)

    def play(self, url):
        self.loads.append(url)


def _video(aid=None, sid=None, transcode=False, secondary=None):
    v = Video.__new__(Video)
    v.aid, v.sid = aid, sid
    v.secondary_sid = secondary
    v.is_transcode = transcode
    v.audio_seq = {1: 1, 2: 2}                 # Jellyfin index -> mpv aid
    v.subtitle_seq = {3: 1, 4: 2}              # embedded
    v.subtitle_url = {5: "http://srv/Videos/x/x/Subtitles/5/0/Stream.srt"}
    return v


class TrackChoiceTest(unittest.TestCase):
    """What the loadfile and configure_streams both select, from the item."""

    def test_the_table(self):
        cases = [
            # (aid, sid, transcode) -> (audio, sub, external)
            ((2, 4, False), (2, 2, None)),
            ((1, None, False), (1, "no", None)),
            ((1, -1, False), (1, "no", None)),
            ((2, 5, False), (2, "no", 5)),          # a sidecar: none until it lands
            ((2, 3, True), (None, 1, None)),        # a transcode's audio is the stream's
            ((None, 3, False), (None, 1, None)),    # no answer: mpv's own audio
        ]
        for (aid, sid, transcode), expected in cases:
            with self.subTest(aid=aid, sid=sid, transcode=transcode):
                audio, sub, external, _problem = track_choice(
                    _video(aid, sid, transcode))
                self.assertEqual((audio, sub, external), expected)

    def test_an_index_neither_map_knows_leaves_mpv_its_choice(self):
        audio, sub, external, problem = track_choice(_video(9, 9))
        self.assertEqual((audio, sub, external), (None, None, None))
        self.assertEqual(problem, ("audio", 9))
        self.assertEqual(track_choice(_video(1, 9))[3], ("subtitle", 9))

    def test_the_load_options_say_the_same(self):
        pm = PlayerManager.__new__(PlayerManager)
        self.assertEqual(pm._track_load_options(_video(2, 4)), {"aid": 2, "sid": 2})
        # a sidecar: no per-file sid (it would make mpv drop the next file's per-file aid; measured)
        self.assertEqual(pm._track_load_options(_video(2, 5)), {"aid": 2})
        self.assertEqual(pm._track_load_options(_video(2, 3, transcode=True)), {"sid": 1})
        self.assertEqual(pm._track_load_options(_video(9, 9)), {})


class LoadfileTest(unittest.TestCase):

    def pm(self, where):
        pm = PlayerManager.__new__(PlayerManager)
        pm._player = _Mpv()
        if where != "unset":
            pm._loadfile_index = where
        return pm

    def test_mpv_0_38_and_later_take_the_index_first(self):
        pm = self.pm(True)
        pm._load_url("http://srv/stream", {"aid": 2, "sid": "no"})
        self.assertEqual(pm._player.commands,
                         [("loadfile", "http://srv/stream", "replace", "-1", "aid=2,sid=no")])
        self.assertEqual(pm._player.loads, [])

    def test_an_older_mpv_takes_the_options_third(self):
        pm = self.pm(False)
        pm._load_url("http://srv/stream", {"sid": 1})
        self.assertEqual(pm._player.commands, [("loadfile", "http://srv/stream", "replace", "sid=1")])

    def test_a_plain_load_without_options_an_unknown_mpv_or_a_refusal(self):
        for where, options, refuse in ((True, {}, False), ("unset", {"aid": 2}, False),
                                       (None, {"aid": 2}, False), (True, {"aid": 2}, True)):
            with self.subTest(where=where, options=options, refuse=refuse):
                pm = self.pm(where)
                object.__setattr__(pm._player, "refuse_options", refuse)
                pm._load_url("http://srv/stream", options)
                self.assertEqual(pm._player.commands, [])
                self.assertEqual(pm._player.loads, ["http://srv/stream"])

    def test_a_dead_mpv_is_not_a_refusal(self):
        pm = self.pm(True)

        def gone(*_args):
            raise BrokenPipeError()
        object.__setattr__(pm._player, "command", gone)
        with self.assertRaises(BrokenPipeError):
            pm._load_url("http://srv/stream", {"aid": 2})

    def test_versions(self):
        for version, expected in (("mpv 0.38.0", True), ("mpv v0.41.0-dev-g41f6a6450", True),
                                  ("mpv 0.37.1", False), ("mpv 1.0", True), ("", False), (None, False)):
            with self.subTest(version=version):
                self.assertIs(loadfile_takes_index(version), expected)


class NoReselectTest(unittest.TestCase):
    """configure_streams writes a track only when mpv does not hold it."""

    def pm(self, **props):
        pm = PlayerManager.__new__(PlayerManager)
        pm._lock = threading.RLock()
        pm._player = _Mpv(**props)
        pm.external_subtitles = {}
        pm._pending_subs = {}
        pm._play_epoch = 1
        pm.put_task = lambda *a: None
        return pm

    def test_what_the_load_selected_is_not_written_again(self):
        pm = self.pm(audio=2, sub=2, secondary_sid="no")
        pm._video = _video(2, 4)
        pm.configure_streams()
        self.assertEqual(pm._player.writes, [])

    def test_python_mpv_answers_no_as_false(self):
        pm = self.pm(audio=1, sub=False, secondary_sid=False)
        pm._video = _video(1, None)
        pm.configure_streams()
        self.assertEqual(pm._player.writes, [])

    def test_a_different_track_is_written(self):
        pm = self.pm(audio=1, sub="no", secondary_sid="no")
        pm._video = _video(2, 3)
        pm.configure_streams()
        self.assertEqual(pm._player.writes, [("audio", 2), ("sub", 1)])

    def test_a_sidecar_waits_with_nothing_shown_and_is_asked_for_once(self):
        pm = self.pm(audio=2, sub=1, secondary_sid="no")
        adds = []
        object.__setattr__(pm._player, "command_async",
                           lambda name, *args, callback=None: adds.append(args[0]))
        pm._video = _video(2, 5)
        pm.configure_streams()
        self.assertEqual(pm._player.writes, [("sub", "no")])
        self.assertEqual(len(adds), 1)

    def test_an_unreadable_or_shadowed_property_is_written(self):
        pm = self.pm(audio=2, secondary_sid="no")      # `sub` unreadable
        pm._video = _video(2, 3)
        pm.configure_streams()
        self.assertEqual(pm._player.writes, [("sub", 1)])

        pm = self.pm(audio=2, sub=1, secondary_sid="no")
        pm._player.__dict__["sub"] = 1                  # a refused write's shadow
        pm._video = _video(2, 3)
        pm.configure_streams()
        self.assertEqual(pm._player.writes, [("sub", 1)])

    def test_same_track(self):
        self.assertTrue(same_track(2, 2))
        self.assertTrue(same_track("2", 2))
        self.assertTrue(same_track("no", "no"))
        self.assertTrue(same_track(False, "no"))
        self.assertFalse(same_track(None, "no"))
        self.assertFalse(same_track("auto", 1))
        self.assertFalse(same_track(1, 2))


class _Item:
    """A video item as `_play_media` reads it, up to the end of the load."""

    is_photo = False
    client = None

    def __init__(self, aid=2, sid=3):
        self.item = {"Id": "a" * 32, "Type": "Episode", "MediaType": "Video"}
        self.media_source = {"Path": "/media/e2.mkv", "MediaStreams": []}
        self.aid, self.sid = aid, sid
        self.is_transcode = False
        self.audio_seq = {1: 1, 2: 2}
        self.subtitle_seq = {3: 1, 4: 2}
        self.subtitle_url = {}

    def get_proper_title(self):
        return "S1E2"


class TheStartTest(unittest.TestCase):
    """`_play_media` itself: the loadfile carries the tracks, and the volume
    of the episode before carries over."""

    def setUp(self):
        patcher = mock.patch.object(player_module, "wait_property",
                                    side_effect=_Reached())
        patcher.start()
        self.addCleanup(patcher.stop)
        saved = (settings.video_volume, settings.music_volume)
        self.addCleanup(lambda: (setattr(settings, "video_volume", saved[0]),
                                 setattr(settings, "music_volume", saved[1])))
        saver = mock.patch.object(type(settings), "save", autospec=True)
        self.saves = saver.start()
        self.addCleanup(saver.stop)

    def pm(self, **props):
        pm = PlayerManager.__new__(PlayerManager)
        pm._player = _Mpv(**props)
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
        pm._loadfile_index = True
        for name in ("_ensure_mpv", "resume_colorspace_hint",
                     "_apply_render_presets", "_notify_load_start",
                     "_sync_window_geometry"):
            setattr(pm, name, lambda *args: None)
        return pm

    def start(self, pm, item):
        with self.assertRaises(_Reached):
            pm._play_media(item, "http://srv/Videos/e2/stream", 0)

    def test_the_loadfile_carries_the_tracks(self):
        pm = self.pm()
        self.start(pm, _Item(aid=2, sid=3))
        self.assertEqual([c for c in pm._player.commands if c[0] == "loadfile"],
                         [("loadfile", "http://srv/Videos/e2/stream", "replace", "-1", "aid=2,sid=1")])
        self.assertEqual(pm._player.loads, [])

    def test_the_volume_of_the_episode_before_carries_over(self):
        settings.video_volume = 63
        pm = self.pm(volume=41)
        pm._video = _Item()               # the episode ending, its volume changed since the last tick
        self.start(pm, _Item())
        self.assertEqual([v for k, v in pm._player.writes if k == "volume"], [41])
        self.assertEqual(settings.video_volume, 41)

    def test_a_first_start_takes_the_saved_volume(self):
        settings.video_volume = 63
        pm = self.pm(volume=41)
        self.start(pm, _Item())
        self.assertEqual([v for k, v in pm._player.writes if k == "volume"], [63])


class NextPastTheCreditsTest(unittest.TestCase):
    """Review (1.2.0): the app's countdown moves on 10 s into the credits,
    and Next can be pressed any time in them; Jellyfin marks played only
    from 90 %, so a 23:40 episode whose credits start at 20:40 was left at
    88 %, in Continue Watching (auto-play used to run to the end). Past the
    credits start, the item left is marked played, behind its stop."""

    CREDITS = [NS(type="Intro", start=30.0, end=90.0),
               NS(type="Outro", start=1240.0, end=1420.0)]

    def pm(self, position, intros=CREDITS):
        pm = PlayerManager.__new__(PlayerManager)
        pm._lock = threading.RLock()
        pm._player = _Mpv(playback_time=position, duration=1420.0)
        pm.syncplay = NS(is_enabled=lambda: False)
        self.events = []
        pm.send_timeline_stopped = lambda *a: self.events.append("stopped")
        pm.play = lambda v, *a, **k: self.events.append(("play", v.name))
        pm.queue_played_mark = lambda v, watched=True: self.events.append(
            ("mark", v.name, watched))
        following = NS(name="E2")
        pm._video = NS(name="E1", intros=intros, parent=NS(
            has_next=True, get_next=lambda: NS(video=following)))
        return pm

    def test_next_in_the_credits_marks_the_episode_played_after_its_stop(self):
        self.pm(1250.0).play_next()      # 88 %, 10 s into the credits
        self.assertEqual(self.events,
                         ["stopped", ("play", "E2"), ("mark", "E1", True)])

    def test_before_the_credits_or_without_them_nothing_is_marked(self):
        for position, intros in ((1000.0, self.CREDITS), (1250.0, []),
                                 (1250.0, [NS(type="Outro", start=300.0, end=360.0)])):
            with self.subTest(position=position, intros=intros):
                self.pm(position, intros).play_next()
                self.assertEqual(self.events, ["stopped", ("play", "E2")])

    def test_mark_watched_and_skip_marks_once(self):
        self.pm(1250.0).watched_skip()
        self.assertEqual([e for e in self.events if e[0] == "mark"],
                         [("mark", "E1", True)])

    def test_past_credits(self):
        self.assertTrue(past_credits(1240.0, 1420.0, self.CREDITS))
        self.assertFalse(past_credits(1239.0, 1420.0, self.CREDITS))
        self.assertFalse(past_credits(None, 1420.0, self.CREDITS))
        self.assertFalse(past_credits(1300.0, None, self.CREDITS))
        self.assertFalse(past_credits(1300.0, 1420.0, None))


class PreviousForANextUpStartTest(unittest.TestCase):
    """Review (1.2.0): an episode started from Next Up or Continue Watching
    has a queue that starts with it, so nothing told the app a Previous was
    possible (`play_prev` widened the queue only once pressed, and the app's
    button was disabled). The start puts the earlier episodes in, and the
    ``next`` event the bridge builds from that queue names one."""

    def setUp(self):
        from tests.test_play_prev_lookup import _player

        self.pm, self.video, self.media, self.api = _player(
            playing="e4", queue=["e4", "e5"])
        self.pm._lock = threading.RLock()

    def event(self):
        from jellyfin_mpv_shim import bridge_protocol as proto
        from jellyfin_mpv_shim import bridge_state

        return proto.next_payload("e4", bridge_state._queue(self.video), True)

    def test_the_next_event_names_the_episode_before(self):
        self.assertNotIn("previousItemId", self.event())
        self.pm.widen_queue_soon(self.video, run=lambda work: work())
        self.assertEqual([q["Id"] for q in self.media.queue],
                         ["e1", "e2", "e3", "e4", "e5"])
        self.assertEqual(self.event().get("previousItemId"), "e3")
        self.assertEqual(self.event().get("nextItemId"), "e5")
        self.assertEqual(self.api.calls, 1)

    def test_nothing_is_asked_when_something_is_behind_it_already(self):
        self.media.has_prev = True
        self.pm.widen_queue_soon(self.video, run=lambda work: work())
        self.assertEqual(self.api.calls, 0)

    def test_an_item_that_has_gone_meanwhile_is_left_alone(self):
        def later(work):
            self.pm._video = None           # another item, or a stop
            work()
        self.pm.widen_queue_soon(self.video, run=later)
        self.assertEqual([q["Id"] for q in self.media.queue], ["e4", "e5"])


if __name__ == "__main__":
    unittest.main()
