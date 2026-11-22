"""1.6 player: the quality choice and the Playback stats over the bridge
(docs/BRIDGE.md: ``set_quality``, ``set_quality_defaults``, the ``stream``
event and ``get_stats``; `jellyfin_mpv_shim.quality`).

Driven through `Bridge.handle_line` with test_bridge's stand-ins (nothing
here imports ``player``). The negotiation the server makes is modelled by
the fake player's ``on_negotiate`` hook: a cap the server converts down
answers with a TranscodingUrl and a new play session, a server that
direct-plays whatever is asked answers DirectPlay.
"""

if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import ast
import json
import logging
import os
import threading
import time
import types
import unittest
from unittest import mock

from tests.test_bridge import (A, B, ROOT, SERVER, BridgeCase, FakeQueue,
                               FakeVideo)
from jellyfin_mpv_shim import bridge_protocol as proto  # noqa: E402
from jellyfin_mpv_shim import bridge_state  # noqa: E402
from jellyfin_mpv_shim import quality  # noqa: E402

TOKEN = SERVER["accessToken"]

#: A transcode the server answers a cap with: the url carries the token, as
#: a real TranscodingUrl does (api_key / ApiKey), and the target bitrates.
TRANSCODE_URL = ("/videos/%s/master.m3u8?DeviceId=dev-1&MediaSourceId=src"
                 "&VideoCodec=h264&AudioCodec=aac&VideoBitrate=7616000"
                 "&AudioBitrate=384000&api_key=%s&ApiKey=%s"
                 "&TranscodeReasons=ContainerBitrateExceedsLimit"
                 % (A, TOKEN, TOKEN))


def source(bitrate=20_000_000, width=1920, height=1080, transcode=False):
    out = {"Id": "src-" + A, "Bitrate": bitrate, "Container": "mkv",
           "MediaStreams": [
               {"Index": 0, "Type": "Video", "Codec": "hevc",
                "Profile": "Main 10", "Width": width, "Height": height,
                "VideoRangeType": "HDR10"},
               {"Index": 1, "Type": "Audio", "Language": "eng",
                "Codec": "eac3", "Channels": 6, "SampleRate": 48000,
                "DisplayTitle": "English - Dolby Digital+ - 5.1"},
               {"Index": 2, "Type": "Subtitle", "Codec": "subrip",
                "Language": "eng"}]}
    if transcode:
        out["TranscodingUrl"] = TRANSCODE_URL
    return out


class QualityCase(BridgeCase):
    """Connected, one item playing (A), DirectPlay of a 20 Mbps 1080p file."""

    def setUp(self):
        super().setUp()
        self.addCleanup(quality.DEFAULTS.reset)
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.a = self.pm._video
        self.a.media_source = source()
        self.a.transcode_reasons = []
        self.old = self.a.playback_info["PlaySessionId"]
        self.bridge.tracker.update(self.bridge._observe())

    def journal(self, name):
        return [e for e in self.runtime.journal if e[0] == name]

    def errors(self):
        return [m["data"] for m in self.messages()
                if m.get("event") == "error"]

    def events(self, name):
        return [d for n, d in self.bridge.tracker.update(
            self.bridge._observe()) if n == name]

    def server_converts(self, session="ps-transcode"):
        """The restart's PlaybackInfo: the cap is under the file, so the
        server converts it (a new play session, the transcode's url)."""
        def negotiate():
            self.a.playback_info = {"PlaySessionId": session}
            self.a.media_source = source(transcode=True)
            self.a.play_method = "Transcode"
            self.a.is_transcode = True
            self.a.transcode_reasons = ["ContainerBitrateExceedsLimit"]
        self.pm.on_negotiate = negotiate

    def server_sends_the_file(self, session="ps-direct"):
        """The restart's PlaybackInfo: the file as it is (DirectPlay)."""
        def negotiate():
            self.a.playback_info = {"PlaySessionId": session}
            self.a.media_source = source()
            self.a.play_method = "DirectPlay"
            self.a.is_transcode = False
            self.a.transcode_reasons = []
        self.pm.on_negotiate = negotiate

    def set_quality(self, bitrate=None, original=False):
        answer = self.send("set_quality", {"bitrate": bitrate,
                                           "original": original})
        self.assertTrue(answer["ok"], answer)
        self.run_plays()
        return answer


class NotConnectedTest(BridgeCase):

    def test_not_connected_comes_before_the_args(self):
        for args in ({"bitrate": "x"}, {"bitrate": 8_000_000}, {}):
            self.assertEqual(self.code("set_quality", args), "not_connected")
        self.assertEqual(self.code("set_quality_defaults", {"home": 5}),
                         "not_connected")
        self.assertEqual(self.code("get_stats"), "not_connected")


class DevBackendTest(BridgeCase):
    """1.6.1 (the 1.6 review's queue: BRIDGE.md's ``connect`` wording): what
    the doc now says a ``--dev`` backend does before a server ``connect``."""
    dev = True

    def setUp(self):
        super().setUp()
        self.addCleanup(quality.DEFAULTS.reset)

    def test_defaults_before_any_connect_and_the_rest_after_a_window(self):
        self.assertIsNone(self.code("set_quality_defaults",
                                    {"home": {"bitrate": 6_000_000}}))
        self.assertIsNone(self.code("set_auto_play", {"autoPlay": False}))
        self.assertEqual(self.code("get_stats"), "not_connected")
        self.assertEqual(self.code("set_quality", {"bitrate": None}),
                         "not_connected")
        self.assertTrue(self.connect()["ok"])       # its window only
        self.assertFalse(self.bridge.connected)
        self.assertEqual(self.code("get_stats"), "no_playback")
        self.assertEqual(self.code("set_quality", {"bitrate": None}),
                         "no_playback")


class SetQualityArgsTest(QualityCase):

    def test_bad_args(self):
        for args in ({"bitrate": "8000000"}, {"bitrate": -1},
                     {"bitrate": 99_999}, {"bitrate": 1_000_000_001},
                     {"bitrate": True}, {"bitrate": 8.5e6},
                     {"original": "yes"}, {"original": 1},
                     {"bitrate": 8_000_000, "original": True}):
            self.assertEqual(self.code("set_quality", args), "bad_args", args)
        self.assertEqual(self.journal("restart_playback"), [],
                         "nothing restarted for a bad request")

    def test_auto_original_and_a_cap_are_accepted(self):
        for args in ({}, {"bitrate": None, "original": False},
                     {"original": True}, {"bitrate": 100_000},
                     {"bitrate": 1_000_000_000},
                     {"bitrate": 8_000_000, "ignored": 1}):
            self.assertIsNone(self.code("set_quality", args), args)

    def test_no_playback_with_nothing_playing(self):
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.code("set_quality", {"bitrate": 8_000_000}),
                         "no_playback")
        self.assertEqual(self.code("get_stats"), "no_playback")

    def test_defaults_bad_args(self):
        for args in ({"home": 5}, {"home": {"bitrate": "x"}},
                     {"away": {"original": 2}},
                     {"away": {"bitrate": 8_000_000, "original": True}}):
            self.assertEqual(self.code("set_quality_defaults", args),
                             "bad_args", args)
        self.assertIsNone(self.code("set_quality_defaults", {}))
        self.assertEqual((quality.DEFAULTS.home, quality.DEFAULTS.away),
                         (quality.AUTO, quality.AUTO), "nothing moved")


class SetQualityTest(QualityCase):

    def test_a_cap_restarts_the_item_as_the_same_viewing(self):
        self.server_converts()
        self.set_quality(8_000_000)
        self.assertEqual(self.a.trs_ovr, (8000, False),
                         "a cap the server compares, never a forced transcode")
        self.assertEqual(len(self.journal("restart_playback")), 1)
        self.assertIs(self.pm._video, self.a)
        self.assertEqual(self.journal("report_playing"), [("report_playing", A)],
                         "one Playing for the viewing: the restart sent none")
        self.assertIn(("report_progress_next", A), self.runtime.journal,
                      "progress goes on under the new session")
        self.assertEqual(self.errors(), [])
        stream, = self.events("stream")
        self.assertEqual(stream["quality"], {"bitrate": 8_000_000,
                                             "original": False})
        self.assertEqual(stream["playMethod"], "Transcode")
        self.assertEqual(stream["playSessionId"], "ps-transcode")
        self.assertEqual(stream["bitrate"], 8_000_000, "video + audio target")
        self.assertEqual(stream["transcodeReasons"],
                         ["ContainerBitrateExceedsLimit"])

    def test_the_choice_shown_is_the_one_playing_until_the_change_is_over(self):
        """The override is written before the restart negotiates with it; a
        ``stream`` sampled in between (the monitor runs on its own thread)
        must not show a cap that may yet be refused."""
        seen = []

        def negotiate():
            seen.append(quality.stream_payload(self.a)["quality"])
            self.a.playback_info = {"PlaySessionId": "ps-transcode"}
            self.a.media_source = source(transcode=True)
            self.a.play_method = "Transcode"
            self.a.is_transcode = True
        self.pm.on_negotiate = negotiate
        self.set_quality(8_000_000)
        self.assertEqual(seen, [{"bitrate": None, "original": False}])
        self.assertEqual(quality.stream_payload(self.a)["quality"],
                         {"bitrate": 8_000_000, "original": False})

    def test_original_then_auto(self):
        self.server_sends_the_file()
        self.set_quality(original=True)
        self.assertEqual(self.a.trs_ovr, (quality.ORIGINAL_KBPS, False))
        self.assertEqual(self.events("stream")[0]["quality"],
                         {"bitrate": None, "original": True})
        self.server_sends_the_file()
        self.set_quality()
        self.assertIsNone(self.a.trs_ovr, "Auto: the shim's own settings")
        self.assertEqual(self.events("stream")[0]["quality"],
                         {"bitrate": None, "original": False})
        self.assertEqual(len(self.journal("restart_playback")), 2)

    def test_the_choice_in_effect_does_nothing(self):
        self.set_quality()                       # Auto, already
        self.assertEqual(self.journal("restart_playback"), [])
        self.server_converts()
        self.set_quality(8_000_000)
        for _ in range(3):
            self.set_quality(8_000_000)
        self.assertEqual(len(self.journal("restart_playback")), 1,
                         "the same cap again restarts nothing")

    def test_a_change_for_an_item_that_has_gone(self):
        answer = self.send("set_quality", {"bitrate": 8_000_000})
        self.assertTrue(answer["ok"])
        self.pm._video = FakeVideo(B)            # replaced before it ran
        self.run_plays()
        self.assertEqual(self.journal("restart_playback"), [])
        self.assertIsNone(self.a.trs_ovr)

    def test_held_for_the_item_only(self):
        self.server_converts()
        self.set_quality(8_000_000)
        b = FakeVideo(B)
        quality.apply_default(b)
        self.assertIsNone(b.trs_ovr, "the next item starts with its default")


class AutoAdvanceRaceTest(QualityCase):
    """1.6.1 (the 1.6 review's queue, bridge.py:2982): a quality, audio or
    subtitle change is queued for the item playing; the next item's
    auto-advance is no command, so nothing dropped the job, and the change
    landed on the next item."""

    def plays(self):
        return [e[1] for e in self.journal("play")]

    def test_an_auto_advance_cannot_come_between_the_check_and_the_restart(
            self):
        self.server_converts()
        b = FakeVideo(B)
        switched = threading.Event()

        def advance():
            # the next item's start: `_play_media` takes the player's lock
            with self.pm._lock:
                self.pm._video = b
            switched.set()

        real = quality.refused_up_front

        def check(video, choice):
            # the item ends just as the change has checked it is playing
            threading.Thread(target=advance, daemon=True).start()
            switched.wait(0.5)
            return real(video, choice)

        before = len(self.plays())
        with mock.patch.object(quality, "refused_up_front", check):
            self.set_quality(8_000_000)
        self.assertTrue(switched.wait(5), "the next item came after")
        self.assertEqual(self.plays()[before:], [A],
                         "the restart was the item's it was for")
        self.assertIs(self.pm._video, b)
        self.assertIsNone(getattr(b, "trs_ovr", None))

    def test_a_track_change_for_an_item_that_ended_is_not_the_next_ones(self):
        b = FakeVideo(B)
        b.aid = 1
        for cmd, args in (("set_audio", {"index": 3}),
                          ("set_subtitle", {"index": 2})):
            self.pm._video = self.a
            self.assertTrue(self.send(cmd, args)["ok"])
            self.pm._video = b              # the auto-advance, before it ran
            self.run_plays()
        self.assertEqual(self.journal("set_streams"), [])
        self.assertEqual((b.aid, b.sid), (1, None))

    def test_a_track_change_for_the_item_playing_still_applies(self):
        self.assertTrue(self.send("set_audio", {"index": 3})["ok"])
        self.run_plays()
        self.assertEqual(self.journal("set_streams"),
                         [("set_streams", 3, None)])


class RefusedTest(QualityCase):
    """The owner's server is not theirs: an account that may not have the
    video converted. Plain words, and playback goes on."""

    def no_conversion(self):
        quality.remember_policy(self.a.client, {
            "Id": SERVER["user"]["id"],
            "Policy": {"EnableVideoPlaybackTranscoding": False}})

    def test_refused_up_front_without_a_request(self):
        self.no_conversion()
        self.set_quality(8_000_000)
        self.assertEqual(self.journal("restart_playback"), [],
                         "nothing asked of the server")
        self.assertIsNone(self.a.trs_ovr)
        self.assertIs(self.pm._video, self.a, "it plays on")
        error, = self.events("error")
        self.assertEqual(error, {"code": "not_allowed",
                                 "message": quality.REFUSED, "fatal": False})
        state = self.bridge.tracker.snapshot(self.bridge._observe())
        self.assertEqual(state["state"], "playing", "not an error state")

    def test_a_cap_over_the_file_is_not_refused(self):
        self.no_conversion()
        self.server_sends_the_file()
        self.set_quality(40_000_000)
        self.assertEqual(len(self.journal("restart_playback")), 1)
        self.assertEqual(self.events("error"), [])

    def test_original_and_auto_are_never_refused(self):
        self.no_conversion()
        self.server_sends_the_file()
        self.set_quality(original=True)
        self.server_sends_the_file()
        self.set_quality()
        self.assertEqual(len(self.journal("restart_playback")), 2)
        self.assertEqual(self.events("error"), [])

    def test_a_cap_the_server_ignored_reads_original(self):
        """Policy unknown (fail open): asked, and the server sent the file
        as it is although it is over the cap."""
        self.server_sends_the_file()
        self.set_quality(8_000_000)
        self.assertEqual(len(self.journal("restart_playback")), 1)
        events = self.bridge.tracker.update(self.bridge._observe())
        errors = [d for n, d in events if n == "error"]
        self.assertEqual([e["code"] for e in errors], ["not_allowed"])
        self.assertEqual(errors[0]["message"], quality.REFUSED)
        stream, = [d for n, d in events if n == "stream"]
        self.assertEqual(stream["quality"], {"bitrate": None,
                                             "original": True})
        self.assertEqual(stream["playMethod"], "DirectPlay")
        self.assertIs(self.pm._video, self.a)

    def test_a_change_a_play_supersedes_says_nothing_and_leaves_the_next_item_alone(self):
        """Review (Major): a play, stop or connect arriving while the
        quality job waits for the player's lock drops its restart as
        superseded. No start took, so the stream playing says nothing about
        the cap: no ``not_allowed``, the choice not flipped to Original, the
        previous choice back, and the next item starts with its default."""
        real = self.pm.restart_playback

        def superseded_while_waiting():
            with self.bridge._state_lock:
                self.bridge._gen += 1       # a play/stop/connect came
            return real()
        self.pm.restart_playback = superseded_while_waiting
        self.set_quality(8_000_000)
        self.assertEqual(self.journal("restart_playback"),
                         [("restart_playback",)])
        self.assertEqual(self.events("error"), [], "no false not_allowed")
        self.assertIsNone(self.a.trs_ovr, "the previous choice (Auto) back")
        self.assertEqual(quality.stream_payload(self.a)["quality"],
                         {"bitrate": None, "original": False})
        # the next item
        self.pm.restart_playback = real
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        b = self.pm._video
        self.assertIsNot(b, self.a)
        self.assertIsNone(b.trs_ovr)
        events = self.bridge.tracker.update(self.bridge._observe())
        self.assertEqual([d for n, d in events if n == "error"], [])
        stream, = [d for n, d in events if n == "stream"]
        self.assertEqual(stream["quality"], {"bitrate": None,
                                             "original": False})

    def test_a_change_that_finds_no_stream_keeps_the_old_one(self):
        """The server can't (no stream for the cap): the restart is put
        back, the choice with it, and the message names the quality."""
        def negotiate():
            self.a.playback_info = {"PlaySessionId": "ps-none"}
            self.a.media_source = None
            self.a.play_method = None
        self.pm.on_negotiate = negotiate
        self.pm.next_play = "fail"
        self.set_quality(8_000_000)
        self.assertIsNone(self.a.trs_ovr, "the choice put back")
        self.assertEqual(self.a.playback_info["PlaySessionId"], self.old)
        errors = self.events("error")
        self.assertEqual(len(errors), 1)
        self.assertIn("quality", errors[0]["message"])
        self.assertNotIn(TOKEN, json.dumps(errors))


class DefaultsTest(BridgeCase):

    def setUp(self):
        super().setUp()
        self.addCleanup(quality.DEFAULTS.reset)

    def test_auto_until_told(self):
        self.assertEqual((quality.DEFAULTS.home, quality.DEFAULTS.away),
                         (quality.AUTO, quality.AUTO))
        video = FakeVideo(A)
        quality.apply_default(video)
        self.assertIsNone(video.trs_ovr)

    def test_home_and_away_by_the_queue(self):
        self.connect_server()
        self.assertIsNone(self.code("set_quality_defaults", {
            "home": {"bitrate": 20_000_000, "original": False},
            "away": {"bitrate": None, "original": True}}))
        home, away = FakeQueue([A]), FakeQueue([B])
        home.is_local, away.is_local = True, False
        a, b = FakeVideo(A, parent=home), FakeVideo(B, parent=away)
        quality.apply_default(a)
        quality.apply_default(b)
        self.assertEqual(a.trs_ovr, (20000, False))
        self.assertEqual(b.trs_ovr, (quality.ORIGINAL_KBPS, False))
        # One left out keeps what it had.
        self.assertIsNone(self.code("set_quality_defaults",
                                    {"home": {"original": False}}))
        self.assertEqual(quality.DEFAULTS.away, quality.ORIGINAL)
        self.assertEqual(quality.DEFAULTS.home, quality.AUTO)

    def test_only_the_first_start_takes_the_default(self):
        """A restart, a retry of the same video, or a forced transcode the
        shim wrote keep what they have."""
        quality.DEFAULTS.update(quality.Choice(4_000_000), quality.AUTO)
        video = FakeVideo(A)
        quality.apply_default(video)
        self.assertEqual(video.trs_ovr, (4000, False))
        video.trs_ovr = None                     # the user chose Auto
        for _ in range(3):
            quality.apply_default(video)
        self.assertIsNone(video.trs_ovr)
        forced = FakeVideo(B)
        forced.trs_ovr = (None, True)            # retry_failed_playback
        quality.apply_default(forced)
        self.assertEqual(forced.trs_ovr, (None, True))

    def no_conversion(self, video):
        quality.remember_policy(video.client, {
            "Id": SERVER["user"]["id"],
            "Policy": {"EnableVideoPlaybackTranscoding": False}})

    def test_a_default_cap_is_not_asked_for_an_account_that_may_not_convert(
            self):
        """1.6.1 (the 1.6 review's queue: the defaults skipped the checks a
        choice by hand has)."""
        quality.DEFAULTS.update(quality.Choice(4_000_000), quality.ORIGINAL)
        video = FakeVideo(A, client=types.SimpleNamespace())
        self.no_conversion(video)
        quality.apply_default(video)
        self.assertIsNone(video.trs_ovr, "Auto: the file as it is")
        original = FakeVideo(B, parent=FakeQueue([B]),
                             client=types.SimpleNamespace())
        original.parent.is_local = False
        self.no_conversion(original)
        quality.apply_default(original)
        self.assertEqual(original.trs_ovr, (quality.ORIGINAL_KBPS, False),
                         "Original is never refused")

    def test_a_default_cap_the_server_ignored_reads_original(self):
        quality.DEFAULTS.update(quality.Choice(4_000_000), quality.AUTO)
        video = FakeVideo(A)
        quality.apply_default(video)
        self.assertEqual(video.trs_ovr, (4000, False))
        video.media_source = source()            # 20 Mbps, sent as it is
        video.play_method = "DirectPlay"
        video.playback_info = {"PlaySessionId": "ps"}
        self.assertTrue(quality.settle_default(video))
        self.assertEqual(video.trs_ovr, (quality.ORIGINAL_KBPS, False))
        self.assertEqual(quality.choice_of(video), quality.ORIGINAL)
        self.assertFalse(quality.settle_default(video), "once")
        # a cap the server honoured, and a choice by hand, stay
        honoured = FakeVideo(B)
        quality.apply_default(honoured)
        honoured.media_source = source(transcode=True)
        honoured.play_method, honoured.is_transcode = "Transcode", True
        self.assertFalse(quality.settle_default(honoured))
        self.assertEqual(honoured.trs_ovr, (4000, False))
        by_hand = FakeVideo(A)
        by_hand.trs_ovr = (4000, False)
        by_hand.media_source = source()
        self.assertFalse(quality.settle_default(by_hand))

    def test_the_play_hook_settles_the_first_start_only(self):
        self.connect_server()
        self.runtime.start_plays = True
        with mock.patch.object(quality, "settle_default",
                               return_value=False) as settle:
            self.send("play", {"itemIds": [A]})
            self.run_plays()
            video = self.runtime.pm._video
            self.assertEqual([c.args for c in settle.call_args_list],
                             [(video,)])
            self.runtime.pm.restart_playback()
            self.assertEqual(len(settle.call_args_list), 1, "not a restart")

    def test_the_play_hook_applies_it(self):
        self.connect_server()
        self.send("set_quality_defaults",
                  {"home": {"bitrate": 6_000_000}})
        self.runtime.start_plays = True
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.assertEqual(self.runtime.pm._video.trs_ovr, (6000, False))


class StreamEventTest(QualityCase):

    def test_sent_after_next_for_an_item_and_then_quiet(self):
        queue = FakeQueue([A, B])
        tracker = bridge_state.Tracker()
        b = FakeVideo(B, parent=queue)
        b.media_source = source(bitrate=12_000_000, width=1280, height=720)
        events = tracker.update(bridge_state.Observation(
            connected=True, video=b, position=0.0, duration=60.0))
        names = [n for n, _ in events]
        self.assertLess(names.index("next"), names.index("stream"))
        stream, = [d for n, d in events if n == "stream"]
        self.assertEqual(stream, {
            "itemId": B, "playSessionId": b.playback_info["PlaySessionId"],
            "playMethod": "DirectPlay", "transcodeReasons": [],
            "bitrate": 12_000_000, "sourceBitrate": 12_000_000,
            "sourceWidth": 1280, "sourceHeight": 720,
            "quality": {"bitrate": None, "original": False},
            "isLocal": True})
        for second in range(1, 4):
            events = tracker.update(bridge_state.Observation(
                connected=True, video=b, position=float(second),
                duration=60.0))
            self.assertNotIn("stream", [n for n, _ in events],
                             "nothing walks")

    def test_not_while_a_restart_negotiates(self):
        self.a.play_method = "Transcode"
        events = self.bridge.tracker.update(bridge_state.Observation(
            connected=True, video=self.a, opening=self.a,
            start_in_progress=True))
        self.assertNotIn("stream", [n for n, _ in events])

    def test_never_for_a_file(self):
        tracker = bridge_state.Tracker()
        item = bridge_state.DevItem("av://lavfi:testsrc")
        item.phase = "active"
        for _ in range(3):
            events = tracker.update(bridge_state.Observation(
                connected=True, dev=item, position=1.0))
            self.assertNotIn("stream", [n for n, _ in events])

    def test_no_token_or_url_in_it(self):
        self.server_converts()
        self.set_quality(8_000_000)
        stream, = self.events("stream")
        text = json.dumps(stream)
        for secret in (TOKEN, "api_key", "ApiKey", "master.m3u8", "DeviceId"):
            self.assertNotIn(secret, text)


class FakeStatsHandle:
    """mpv's properties as ``get_property`` answers them, and every read."""

    VALUES = {
        "video-params": {"w": 1280, "h": 720, "pixelformat": "nv12"},
        "hwdec-current": "d3d11va", "container-fps": 23.976,
        "estimated-vf-fps": 23.9, "frame-drop-count": 2,
        "decoder-frame-drop-count": 1, "vo-delayed-frame-count": 4,
        "audio-codec-name": "aac",
        "audio-params": {"samplerate": 48000, "channel-count": 2},
        "video-bitrate": 7_500_000, "audio-bitrate": 380_000,
        "demuxer-cache-duration": 12.5,
        "demuxer-cache-state": {"fw-bytes": 9_000_000},
        "cache-speed": 2_500_000, "file-format": "hls", "video-format": "h264",
        "mpv-version": "mpv v0.41.0", "aid": 1, "sid": False,
    }

    def __init__(self, base):
        self.base = base
        self.reads = []

    def command(self, *args):
        if args[0] == "get_property":
            self.reads.append(args[1])
            return self.VALUES.get(args[1])
        return self.base.command(*args)

    def __getattr__(self, name):
        return getattr(self.base, name)

    def __setattr__(self, name, value):
        if name in ("base", "reads", "VALUES"):
            object.__setattr__(self, name, value)
        else:
            setattr(self.base, name, value)


class GetStatsTest(QualityCase):

    def setUp(self):
        super().setUp()
        self.handle = FakeStatsHandle(self.pm._player)
        self.pm._player = self.handle

    def send(self, cmd, args=None):
        """`get_stats` is answered from a thread of its own (beside the
        command queue): wait for its one answer."""
        if cmd != "get_stats":
            return super().send(cmd, args)
        req = self.ask("get_stats")
        answer = self.answer_to(req)
        self.assertIsNotNone(answer, "get_stats never answered")
        return answer

    def ask(self, cmd):
        """A request through the reader's dispatch, not waiting for it."""
        self.next_id += 1
        self.bridge.handle_line(json.dumps(
            {"id": self.next_id, "cmd": cmd}).encode() + b"\n")
        while not self.bridge._control_q.empty():
            self.bridge.execute(self.bridge._control_q.get_nowait())
        return self.next_id

    def answers(self, req):
        return [m for m in self.messages() if m.get("id") == req]

    def answer_to(self, req, timeout=5.0):
        found = self.wait_message(lambda m: m.get("id") == req, timeout)
        return found

    def test_the_rows_from_the_server_and_mpv(self):
        self.server_converts()
        self.set_quality(8_000_000)
        answer = self.send("get_stats")
        self.assertTrue(answer["ok"], answer)
        stats = answer["result"]
        self.assertEqual(stats["playMethod"], "Transcode")
        self.assertEqual(stats["transcodeReasons"],
                         ["ContainerBitrateExceedsLimit"])
        self.assertEqual((stats["container"], stats["streamFormat"]),
                         ("mkv", "hls"))
        self.assertEqual((stats["videoCodec"], stats["videoProfile"],
                          stats["hdr"]), ("hevc", "Main 10", "HDR10"))
        self.assertEqual(stats["streamVideoCodec"], "h264")
        self.assertEqual((stats["sourceWidth"], stats["sourceHeight"],
                          stats["width"], stats["height"]),
                         (1920, 1080, 1280, 720))
        self.assertEqual(stats["frameRate"], 23.976)
        self.assertEqual(stats["hardwareDecoding"], "d3d11va")
        self.assertEqual((stats["droppedFrames"], stats["delayedFrames"]),
                         (3, 4))
        self.assertEqual((stats["audioCodec"], stats["audioChannels"],
                          stats["audioSampleRate"]), ("aac", 2, 48000))
        self.assertEqual(stats["streamBitrate"], 8_000_000)
        self.assertEqual(stats["measuredBitrate"], 7_880_000)
        self.assertEqual((stats["bufferSeconds"], stats["bufferBytes"],
                          stats["networkSpeed"]), (12.5, 9_000_000, 2_500_000))
        self.assertEqual(stats["mpvVersion"], "mpv v0.41.0")
        self.assertIs(stats["isLocal"], True)
        text = json.dumps(stats)
        for secret in (TOKEN, "api_key", "ApiKey", "m3u8", "Authorization"):
            self.assertNotIn(secret, text)

    def test_software_decoding_and_no_answers(self):
        self.handle.VALUES = dict(FakeStatsHandle.VALUES, **{
            "hwdec-current": "no", "video-params": None, "cache-speed": None})
        stats = self.send("get_stats")["result"]
        self.assertIsNone(stats["hardwareDecoding"])
        self.assertIsNone(stats["width"])
        self.assertIsNone(stats["networkSpeed"])
        self.assertEqual(stats["audioTrack"], "English - Dolby Digital+ - 5.1")

    def test_a_nan_from_mpv_reads_null_and_the_stats_are_answered(self):
        """1.6.1 (the 1.6 review's queue): a NaN (a frame rate mpv has not
        worked out yet) went into the answer, which the protocol's JSON
        cannot carry: the request was never answered."""
        nan = float("nan")
        self.handle.VALUES = dict(FakeStatsHandle.VALUES, **{
            "container-fps": nan, "estimated-vf-fps": float("inf"),
            "demuxer-cache-duration": nan, "video-bitrate": nan})
        answer = self.send("get_stats")
        self.assertTrue(answer["ok"], answer)
        stats = answer["result"]
        self.assertIsNone(stats["frameRate"])
        self.assertIsNone(stats["bufferSeconds"])
        self.assertEqual(stats["measuredBitrate"], 380_000, "the audio's")

    def test_an_answer_that_cannot_be_sent_is_still_answered(self):
        reads = quality.StatsReads(self.bridge.writer)
        reads.ask(990, lambda deadline, cancelled: {"frameRate": float("nan")})
        answer = self.answer_to(990)
        self.assertIsNotNone(answer, "answered")
        self.assertEqual((answer["ok"], answer["error"]["code"]),
                         (False, "internal"))
        self.assertEqual(len(self.answers(990)), 1)

    def test_the_polled_stats_are_not_logged_at_info(self):
        """1.6.1 (the 1.6 review's queue): the open panel's read once a
        second put an INFO line a second in the backend's log."""
        with self.assertLogs("bridge", "DEBUG") as logs:
            for _ in range(3):
                self.assertTrue(self.send("get_stats")["ok"])
            self.send("ping")
        lines = [(r.levelno, r.getMessage()) for r in logs.records]
        self.assertEqual(
            [m for level, m in lines if level >= logging.INFO
             and m.startswith("<- get_stats")], [])
        self.assertEqual(len([m for level, m in lines
                              if m.startswith("<- get_stats")]), 3)
        self.assertIn((logging.INFO, "<- ping (id %d)" % self.next_id),
                      lines, "other commands as before")

    def stuck(self, on="video-params"):
        """mpv not answering one property: the read waits until released."""
        release = threading.Event()
        entered = threading.Event()
        values = self.handle.VALUES
        handle = self.handle

        def command(*args):
            if args[0] == "get_property":
                handle.reads.append(args[1])
                if args[1] == on:
                    entered.set()
                    release.wait(10)
                return values.get(args[1])
            return handle.base.command(*args)
        object.__setattr__(handle, "command", command)
        self.addCleanup(release.set)
        return entered, release

    def test_a_stuck_read_does_not_delay_a_stop(self):
        """Review (Major, promoted): read on the control worker, a hung mpv's
        stats reads (each up to the IPC client's 120 s) queued ahead of a
        stop. Now: the stop is answered at once, the stats read in progress
        is answered then too, and mpv is asked nothing more for it."""
        entered, release = self.stuck()
        stats = self.ask("get_stats")
        self.assertTrue(entered.wait(5), "the read started")
        started = time.monotonic()
        stop = self.send("stop")
        self.assertTrue(stop["ok"])
        self.assertLess(time.monotonic() - started, 1.0, "stop not delayed")
        answer, = self.answers(stats)
        self.assertEqual(answer["error"]["code"], "no_playback")
        reads = len(self.handle.reads)
        release.set()
        time.sleep(0.3)
        self.assertEqual(len(self.handle.reads), reads,
                         "nothing more asked after the stop")
        self.assertEqual(len(self.answers(stats)), 1, "exactly one answer")

    def test_one_read_at_a_time_and_an_answer_within_the_budget(self):
        self.addCleanup(setattr, quality.StatsReads, "BUDGET",
                        quality.StatsReads.BUDGET)
        quality.StatsReads.BUDGET = 0.3
        entered, release = self.stuck()
        first = self.ask("get_stats")
        self.assertTrue(entered.wait(5))
        second = self.ask("get_stats")
        answer, = self.answers(second)
        self.assertEqual(answer["error"]["code"], "mpv_failed",
                         "answered at once while one is being read")
        late = self.answer_to(first, timeout=3)
        self.assertEqual(late["error"]["code"], "mpv_failed",
                         "answered at the budget, mpv not answering")
        release.set()
        time.sleep(0.3)
        self.assertEqual(self.handle.reads, ["video-params"],
                         "no property asked past the budget")
        self.assertEqual(len(self.answers(first)), 1)
        # mpv answers again: the next one is read in full
        object.__delattr__(self.handle, "command")
        third = self.send("get_stats")
        self.assertTrue(third["ok"], third)

    def test_the_shutdown_answers_a_read_in_progress_first(self):
        entered, _release = self.stuck()
        stats = self.ask("get_stats")
        self.assertTrue(entered.wait(5))
        self.bridge._end_stats("internal", "the backend is shutting down")
        answer, = self.answers(stats)
        self.assertEqual(answer["error"]["code"], "internal")

    def test_mpv_is_read_only_when_asked(self):
        """The monitor's samples never read a stats property: the panel's
        reads are get_stats', and the UI sends none while it is closed."""
        for _ in range(5):
            self.bridge.tracker.update(self.bridge._observe())
        self.assertEqual(self.handle.reads, [])
        self.send("get_stats")
        self.assertEqual(sorted(set(self.handle.reads)),
                         sorted(quality.STATS_PROPERTIES))


class RestartIsTheShimsTest(unittest.TestCase):
    """`set_quality` is the shim's own restart: `restart_playback` plays the
    video again from mpv's playback_time, as the same viewing (no second
    Playing). Pinned in the real player's source, which the fakes stand in
    for (importing it would build an mpv)."""

    def test_restart_playback_keeps_the_position_and_the_viewing(self):
        path = os.path.join(ROOT, "jellyfin_mpv_shim", "player.py")
        with open(path, encoding="utf-8") as f:
            tree = ast.parse(f.read())
        method, = [n for n in ast.walk(tree)
                   if isinstance(n, ast.FunctionDef)
                   and n.name == "restart_playback"]
        source = ast.unparse(method)
        self.assertIn("current_time = self._player.playback_time", source)
        self.assertIn("self.play(video, current_time, "
                      "no_initial_timeline=True", source)


class ExistingMessagesUnchangedTest(unittest.TestCase):
    """1.6 adds a command or three and one event; nothing that was there
    changes shape. The ``state`` snapshot keeps BRIDGE.md section 5's keys,
    and an item's events are 1.5's with ``stream`` after ``next``."""

    STATE_KEYS = {"state", "connected", "itemId", "mediaSourceId",
                  "playSessionId", "playMethod", "positionTicks",
                  "durationTicks", "volume", "muted", "audioStreamIndex",
                  "subtitleStreamIndex", "queue", "fullscreen", "window"}

    def test_the_state_snapshot_keeps_its_keys(self):
        tracker = bridge_state.Tracker()
        video = FakeVideo(A, parent=FakeQueue([A]))
        video.media_source = source()
        for obs in (bridge_state.Observation(connected=True),
                    bridge_state.Observation(connected=True, video=video,
                                             position=1.0, duration=60.0)):
            snap = tracker.snapshot(obs)
            self.assertEqual(set(snap), self.STATE_KEYS)

    def test_an_items_events_are_15s_with_stream_after_next(self):
        tracker = bridge_state.Tracker()
        video = FakeVideo(A, parent=FakeQueue([A, B]))
        video.media_source = source()
        events = tracker.update(bridge_state.Observation(
            connected=True, video=video, position=0.0, duration=60.0))
        names = [n for n, _ in events]
        self.assertEqual([n for n in names if n != "stream"],
                         ["item", "tracks", "segments", "next", "state",
                          "position"])
        self.assertEqual(names.index("stream"), names.index("next") + 1)
        payloads = dict(events)
        self.assertEqual(set(payloads["item"]),
                         {"itemId", "name", "type", "runTimeTicks"})
        self.assertEqual(set(payloads["tracks"]), {"audio", "subtitle"})
        self.assertEqual(set(payloads["next"]),
                         {"itemId", "nextItemId", "autoPlay"})

    def test_the_new_commands_answer_unknown_cmd_nowhere(self):
        from jellyfin_mpv_shim import bridge as bridge_mod

        for cmd in ("set_quality", "set_quality_defaults", "get_stats"):
            self.assertIn(cmd, bridge_mod.Bridge.HANDLERS)


class PolicyFromConnectTest(unittest.TestCase):
    """The account's policy comes from connect's own ``Users/Me`` answer,
    through the apiclient's real request path: the quality check makes no
    request of its own."""

    def setUp(self):
        from tests.test_bridge import VerifyAgainstTheApiclientTest

        self.case = VerifyAgainstTheApiclientTest("test_the_users_own_id_passes")
        self.case.setUp()
        self.addCleanup(self.case.doCleanups)

    def test_connect_primes_the_policy(self):
        from jellyfin_mpv_shim import user_policy

        code, get = self.case.verify(self.case.answer(200, {
            "Id": SERVER["user"]["id"],
            "Policy": {"EnableVideoPlaybackTranscoding": False}}))
        self.assertIsNone(code)
        self.assertEqual(get.call_count, 1)
        with mock.patch.object(self.case.client.jellyfin, "get_user",
                               side_effect=AssertionError("asked again")):
            self.assertEqual(user_policy.policy_for(self.case.client),
                             {"EnableVideoPlaybackTranscoding": False})
            self.assertFalse(quality.may_convert_video(self.case.client))

    def test_no_policy_in_the_answer_fails_open(self):
        code, _get = self.case.verify(self.case.answer(200, {
            "Id": SERVER["user"]["id"]}))
        self.assertIsNone(code)
        with mock.patch.object(self.case.client.jellyfin, "get_user",
                               side_effect=RuntimeError("no server")):
            self.assertTrue(quality.may_convert_video(self.case.client))


class ChoiceTest(unittest.TestCase):

    def test_override_round_trip(self):
        for choice in (quality.AUTO, quality.ORIGINAL,
                       quality.Choice(120_000_000), quality.Choice(1_500_000),
                       quality.Choice(8_000_000)):
            video = FakeVideo(A)
            video.trs_ovr = quality.override_for(choice)
            self.assertEqual(quality.choice_of(video), choice)

    def test_the_shims_forced_transcode_reads_auto(self):
        video = FakeVideo(A)
        video.trs_ovr = (None, True)
        self.assertEqual(quality.choice_of(video), quality.AUTO)

    def test_target_bitrate_keeps_nothing_else_of_the_url(self):
        self.assertEqual(quality._target_bitrate(TRANSCODE_URL), 8_000_000)
        self.assertIsNone(quality._target_bitrate(None))
        self.assertIsNone(quality._target_bitrate("/x?VideoBitrate=abc"))

    def test_not_allowed_is_a_protocol_code(self):
        self.assertIn(quality.NOT_ALLOWED, proto.ERROR_CODES)


if __name__ == "__main__":
    unittest.main()
