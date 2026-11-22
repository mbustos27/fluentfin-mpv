"""1.6 skip: the skip rules over the bridge (BRIDGE.md section 4
``set_skip_options``' 1.6 fields, ``skip_rules``, ``add_skip_rule``,
``forget_skip_rules``, ``answer_skip_suggestion``; section 5
``skip_suggestion`` and ``segments`` with ``source: "rule"``).

Driven through `Bridge.handle_line` with test_bridge's stand-ins (nothing
imports ``player``); the store is in memory."""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import unittest
from unittest import mock

# The module, not its classes: a TestCase imported by name would run twice.
from tests import test_bridge as tb

from jellyfin_mpv_shim import bridge_protocol as proto  # noqa: E402
from jellyfin_mpv_shim import conf, skip_rules  # noqa: E402

SERIES = "ab" * 16
USER = tb.SERVER["user"]["id"]
ACCOUNT = "s:" + USER


def episode_video(item_id, runtime=1400, season=1):
    video = tb.FakeVideo(item_id, name="Ep", parent=tb.FakeQueue([item_id]))
    video.item = {"Id": item_id, "Name": "Ep", "Type": "Episode",
                  "SeriesId": SERIES, "SeriesName": "Shadows",
                  "ParentIndexNumber": season,
                  "RunTimeTicks": runtime * 10_000_000}
    return video


class SkipBridgeCase(tb.BridgeCase):
    def setUp(self):
        super().setUp()
        self.store = skip_rules.SkipStore()
        patcher = mock.patch.object(skip_rules, "store",
                                    return_value=self.store)
        patcher.start()
        self.addCleanup(patcher.stop)
        # a burst of seeks is judged at the next reading (real time here)
        gap = mock.patch.object(skip_rules, "BURST_GAP", 0.0)
        gap.start()
        self.addCleanup(gap.stop)
        saved = (skip_rules.current_account(), skip_rules.OPTIONS.learn,
                 skip_rules.OPTIONS.rule_action,
                 conf.settings.segment_commercial,
                 conf.settings.segment_preview, dict(conf.SESSION_ONLY))

        def restore():
            (account, skip_rules.OPTIONS.learn,
             skip_rules.OPTIONS.rule_action,
             conf.settings.segment_commercial,
             conf.settings.segment_preview, session) = saved
            skip_rules.set_account(account)
            conf.SESSION_ONLY.clear()
            conf.SESSION_ONLY.update(session)

        self.addCleanup(restore)
        skip_rules.set_account(None)

    def play_episode(self, item_id, position):
        pm = self.runtime.pm
        video = episode_video(item_id)
        from jellyfin_mpv_shim import intro_sources
        from jellyfin_mpv_shim.media import Intro

        intro_sources.augment(video, Intro, self.store)
        pm.play(video)
        pm._player.playback_time = position
        return pm, video

    def tick(self):
        """One monitor tick's events, as `_emit_tick` makes them."""
        before = len(self.messages())
        self.bridge._emit_tick(self.bridge._observe())
        return [(m["event"], m["data"]) for m in self.messages()[before:]
                if "event" in m]


class NotConnectedAndBadArgs(SkipBridgeCase):
    def test_not_connected_before_args(self):
        for cmd, args in (("skip_rules", {}), ("add_skip_rule", {"x": 1}),
                          ("forget_skip_rules", {}),
                          ("answer_skip_suggestion", {"id": 5})):
            self.assertEqual(self.code(cmd, args), "not_connected", cmd)

    def test_list_and_forget_before_connect_with_an_account(self):
        key = ACCOUNT + ":" + SERIES
        self.store.add_rule(key, "start", 0, 40, name="Shadows")
        account = {"serverId": "S", "userId": USER}
        answer = self.send("skip_rules", account)
        self.assertTrue(answer["ok"])
        self.assertEqual([s["name"] for s in answer["result"]["shows"]],
                         ["Shadows"])
        self.assertEqual(self.send("forget_skip_rules", account)["result"],
                         {"forgotten": 1})
        self.assertEqual(self.code("skip_rules", {"serverId": "S"}),
                         "bad_args", "both or neither")

    def test_bad_args(self):
        self.connect_server()
        for args in ({}, {"anchor": "middle", "startS": 0, "endS": 5},
                     {"anchor": "start", "startS": 40, "endS": 10},
                     {"anchor": "end", "startS": 10, "endS": 40},
                     {"anchor": "start", "startS": 0, "endS": 900},
                     {"anchor": "start", "startS": -1, "endS": 5},
                     {"anchor": "start", "startS": "0", "endS": 5},
                     {"anchor": "start", "startS": 0, "endS": 5,
                      "action": "never"},
                     {"anchor": "start", "startS": 0, "endS": 5,
                      "seriesId": "not an id"}):
            self.assertEqual(self.code("add_skip_rule", args), "bad_args",
                             args)
        for args in ({"ruleId": "../etc"}, {"seriesId": 3}):
            self.assertEqual(self.code("forget_skip_rules", args),
                             "bad_args", args)
        for args in ({}, {"id": "c0123456789ab"},
                     {"id": "c0123456789ab", "answer": "maybe"},
                     {"id": "nope", "answer": "yes"}):
            self.assertEqual(self.code("answer_skip_suggestion", args),
                             "bad_args", args)
        self.assertEqual(self.code("answer_skip_suggestion",
                                   {"id": "c0123456789ab", "answer": "yes"}),
                         "bad_args", "a suggestion nobody made")
        self.assertEqual(self.code("set_skip_options",
                                   {"segmentActions": {"Ads": "ask"}}),
                         "bad_args")

    def test_add_with_no_show_playing(self):
        self.connect_server()
        self.assertEqual(self.code("add_skip_rule", {
            "anchor": "start", "startS": 0, "endS": 30}), "no_playback")


class Rules(SkipBridgeCase):
    def test_connect_picks_the_account_and_migrates(self):
        with mock.patch.object(self.store, "migrate") as migrate:
            self.connect_server()
        self.assertEqual(skip_rules.current_account(), ACCOUNT)
        migrate.assert_called_once_with(ACCOUNT)

    def test_add_list_forget_through_the_bridge(self):
        self.connect_server()
        answer = self.send("add_skip_rule", {
            "seriesId": SERIES.upper(), "seriesName": "Shadows",
            "anchor": "end", "startS": 62, "endS": 0, "action": "always"})
        rule = answer["result"]["ruleId"]
        shows = self.send("skip_rules")["result"]["shows"]
        self.assertEqual(shows, [{
            "seriesId": SERIES, "name": "Shadows", "refused": False,
            "learnedIntros": [],
            "rules": [{"id": rule, "anchor": "end", "startS": 62.0,
                       "endS": 0.0, "action": "always", "origin": "marked",
                       "created": shows[0]["rules"][0]["created"]}]}])
        self.assertEqual(self.send("forget_skip_rules", {"ruleId": rule})[
            "result"], {"forgotten": 1})
        self.assertEqual(self.send("skip_rules")["result"], {"shows": []})

    def test_a_rule_marked_while_playing_applies_now(self):
        self.connect_server()
        pm, video = self.play_episode("e" * 32, 1300.0)
        self.tick()
        answer = self.send("add_skip_rule", {"anchor": "end", "startS": 62,
                                             "endS": 0})
        self.assertTrue(answer["ok"], answer)
        pm.run_deferred()
        events = self.tick()
        data, = [d for n, d in events if n == "segments"]
        self.assertEqual(data["segments"], [{
            "type": "Commercial", "startTicks": 1338 * 10_000_000,
            "endTicks": 1400 * 10_000_000, "action": "ask", "source": "rule",
            "ruleId": answer["result"]["ruleId"], "origin": "marked",
            "anchor": "end"}])
        self.assertEqual(data["current"], None)
        pm._player.playback_time = 1340.0
        data, = [d for n, d in self.tick() if n == "segments"]
        self.assertEqual(data["current"], 0)
        # the bar mpv draws calls it an extra
        self.bridge._push_offer()
        self.assertIn(("set", "user-data/fluentfin/skip", "Skip Extra"),
                      pm._player.commands)

    def test_segments_unchanged_for_items_without_rules(self):
        """Golden: 1.5's segment shape, with no rule anywhere."""
        out = proto.segment_list([("Intro", 1, 2, "ask", "chapters", None),
                                  ("Outro", 3, 4, "ask"),
                                  ("Recap", 5, 6, "ask", "learned")])
        self.assertEqual(out, [
            {"type": "Intro", "startTicks": 10_000_000,
             "endTicks": 20_000_000, "action": "ask", "source": "chapters"},
            {"type": "Outro", "startTicks": 30_000_000,
             "endTicks": 40_000_000, "action": "ask", "source": "server"},
            {"type": "Recap", "startTicks": 50_000_000,
             "endTicks": 60_000_000, "action": "ask", "source": "learned"}])
        # and a rule's extra fields never leak onto another source
        self.assertNotIn("ruleId", proto.segment_list(
            [("Intro", 1, 2, "ask", "server", {"ruleId": "r1"})])[0])

    def test_an_always_part_skipped_stays_skipped_after_an_add(self):
        """1.6.1 (the 1.6 review's skip queue): an Add (or a Yes) mid-episode
        rebuilt every rule's segment, so an "always" part the player had
        already skipped re-armed and was skipped again on a seek back."""
        self.connect_server()
        first = self.store.add_rule(ACCOUNT + ":" + SERIES, "start", 5, 41,
                                    "always")
        pm, video = self.play_episode("e" * 32, 10.0)
        seg, = [s for s in video.intros if s.rule_id == first]
        seg.has_triggered = True            # the player skipped it
        pm._player.playback_time = 20.0     # the viewer seeks back into it
        self.tick()
        answer = self.send("add_skip_rule", {"anchor": "end", "startS": 62,
                                             "endS": 0, "action": "always"})
        self.assertTrue(answer["ok"], answer)
        pm.run_deferred()
        rules = {s.rule_id: s for s in video.intros
                 if s.source == skip_rules.SOURCE_RULE}
        self.assertIsNot(rules[first], seg, "rebuilt")
        self.assertTrue(rules[first].has_triggered, "not armed again")
        self.assertFalse(rules[answer["result"]["ruleId"]].has_triggered,
                         "the new part ahead is skipped when reached")

    def test_the_segments_event_of_an_item_with_no_rule_is_15s(self):
        """Golden (the 1.6 review's skip queue: it covered `segment_list`
        only): the whole ``segments`` event of an episode with no rule, as
        the bridge sends it, is 1.5's: the same keys and nothing more."""
        from jellyfin_mpv_shim.media import Intro

        self.connect_server()
        pm = self.runtime.pm
        video = episode_video("e" * 32)
        video.intros = [Intro("Intro", 10.0, 40.0, "server"),
                        Intro("Outro", 1300.0, 1400.0, "chapters")]
        pm.play(video)
        pm._player.playback_time = 12.0
        data, = [d for n, d in self.tick() if n == "segments"]
        self.assertEqual(data, {
            "itemId": "e" * 32,
            "segments": [
                {"type": "Intro", "startTicks": 100_000_000,
                 "endTicks": 400_000_000, "action": "ask",
                 "source": "server"},
                {"type": "Outro", "startTicks": 13_000_000_000,
                 "endTicks": 14_000_000_000, "action": "ask",
                 "source": "chapters"}],
            "current": 0})

    def test_skip_options_segment_actions_and_rule_action(self):
        self.connect_server()
        conf.settings.segment_commercial = "off"
        self.assertTrue(self.send("set_skip_options", {
            "segmentActions": {"Commercial": "ask", "Preview": "always"},
            "ruleAction": "always", "learnSkips": False})["ok"])
        self.assertEqual(conf.settings.segment_commercial, "ask")
        self.assertEqual(conf.settings.segment_preview, "always")
        self.assertEqual(skip_rules.OPTIONS.rule_action, "always")
        self.assertFalse(skip_rules.OPTIONS.learn)
        # held in memory only: a save writes the user's own value
        self.assertEqual(conf.SESSION_ONLY.get("segment_commercial"), "off")


class OfferEnd(unittest.TestCase):
    def test_a_skip_that_lands_a_frame_early_ends_the_offer(self):
        """mpv lands on the frame at or before an exact seek's target: a
        skip to 97.88 left the position at 97.875, inside the part, and the
        pill came back (the harness, 2026-11-21)."""
        segments = proto.segment_list([("Commercial", 60.1, 97.88, "ask",
                                        "rule", None)])
        self.assertEqual(proto.offered_segment(segments, 60.1), 0)
        self.assertEqual(proto.offered_segment(segments, 97.7), 0)
        self.assertIsNone(proto.offered_segment(segments, 97.875))
        self.assertIsNone(proto.offered_segment(segments, 97.88))


class Suggestion(SkipBridgeCase):
    def skip(self, item_id, start, end):
        pm, video = self.play_episode(item_id, start)
        events = self.tick()
        pm._player.playback_time = start + 0.5
        events += self.tick()
        pm._player.playback_time = end
        events += self.tick()
        pm._player.playback_time = end + 0.5
        events += self.tick()
        return [d for n, d in events if n == "skip_suggestion"]

    def test_two_episodes_one_suggestion_then_yes(self):
        self.connect_server()
        self.assertEqual(self.skip("e" * 32, 2.0, 41.0), [])
        found, = self.skip("f" * 32, 3.0, 43.0)
        self.assertEqual(
            {k: found[k] for k in ("itemId", "seriesId", "seriesName",
                                   "anchor")},
            {"itemId": "f" * 32, "seriesId": SERIES, "seriesName": "Shadows",
             "anchor": "start"})
        self.assertEqual(found["startTicks"], 30_000_000)
        self.assertEqual(found["endTicks"], 420_000_000)
        answer = self.send("answer_skip_suggestion",
                           {"id": found["id"], "answer": "yes"})
        self.assertTrue(answer["result"]["ruleId"].startswith("r"))
        self.assertEqual(self.skip("d" * 32, 2.0, 41.0), [],
                         "answered: a skip of the rule's part asks nothing")
        rule, = self.send("skip_rules")["result"]["shows"][0]["rules"]
        self.assertEqual(rule["origin"], "learned")
        self.assertEqual(self.code("answer_skip_suggestion",
                                   {"id": found["id"], "answer": "yes"}),
                         "bad_args", "answered once")

    def test_an_end_skip_is_asked_while_its_episode_plays(self):
        """Review (Major 2), end to end on the backend's side: the seek to
        the last seconds of the second episode emits skip_suggestion in
        that tick, naming that episode; the next episode starting leaves it
        open, and Yes then makes the end rule."""
        self.connect_server()
        for item_id in ("e" * 32, "f" * 32):
            pm, video = self.play_episode(item_id, 1340.0)
            events = self.tick()
            pm._player.playback_time = 1340.5
            events += self.tick()
            pm._player.playback_time = 1398.0
            events += self.tick()
        found, = [d for n, d in events if n == "skip_suggestion"]
        self.assertEqual((found["itemId"], found["anchor"]),
                         ("f" * 32, "end"))
        self.play_episode("d" * 32, 1.0)
        self.tick()
        answer = self.send("answer_skip_suggestion",
                           {"id": found["id"], "answer": "yes"})
        rule, = self.send("skip_rules")["result"]["shows"][0]["rules"]
        self.assertEqual((rule["anchor"], rule["id"]),
                         ("end", answer["result"]["ruleId"]))

    def test_never_for_this_show(self):
        self.connect_server()
        self.skip("e" * 32, 2.0, 41.0)
        found, = self.skip("f" * 32, 3.0, 43.0)
        self.assertEqual(self.send("answer_skip_suggestion", {
            "id": found["id"], "answer": "never"})["result"], {})
        self.assertEqual(self.skip("d" * 32, 2.0, 41.0), [])
        self.assertEqual(self.skip("c" * 32, 2.0, 41.0), [])
        self.assertTrue(self.send("skip_rules")["result"]["shows"][0][
            "refused"])

    def test_a_skip_press_is_not_a_seek_to_learn(self):
        """The pill's own jump, twice, in two episodes: no suggestion."""
        self.connect_server()
        self.store.add_rule(ACCOUNT + ":" + SERIES, "start", 2, 41)
        for item_id in ("e" * 32, "f" * 32):
            pm, video = self.play_episode(item_id, 3.0)
            self.tick()
            self.assertTrue(self.send("skip_segment")["result"]["skipped"])
            pm.run_deferred()
            self.assertEqual([d for n, d in self.tick()
                              if n == "skip_suggestion"], [])


if __name__ == "__main__":
    unittest.main()
