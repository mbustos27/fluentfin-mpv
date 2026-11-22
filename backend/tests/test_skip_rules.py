"""1.6 skip: skip rules, the learner behind them and their store
(`skip_rules`). Pure: no mpv, no network, no bridge."""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import json
import os
import tempfile
import unittest

from jellyfin_mpv_shim import conf, intro_sources, skip_rules as sr

T = 10_000_000
SERIES = "ab" * 16
ACCOUNT = "s1:" + "01" * 16
OTHER_USER = "s1:" + "02" * 16


class Seg:
    def __init__(self, type, start, end, source="server"):
        self.type, self.start, self.end, self.source = type, start, end, source
        self.has_triggered = False


class Vid:
    def __init__(self, item, intros=()):
        self.item, self.intros = item, list(intros)


def episode(item_id="e1", season=1, runtime=1400, chapters=()):
    return {"Id": (item_id * 32)[:32], "SeriesId": SERIES,
            "SeriesName": "Shadows", "ParentIndexNumber": season,
            "RunTimeTicks": runtime * T, "Chapters": list(chapters)}


def make(t, s, e, so):
    return Seg(t, s, e, so)


class Base(unittest.TestCase):
    def setUp(self):
        self._saved = (sr.OPTIONS.learn, sr.OPTIONS.rule_action,
                       sr.OPTIONS.lookup_online, sr.current_account(),
                       conf.settings.segment_intro,
                       conf.settings.segment_outro,
                       conf.settings.segment_commercial)
        sr.OPTIONS.learn, sr.OPTIONS.rule_action = True, "ask"
        sr.OPTIONS.lookup_online = False
        conf.settings.segment_intro = "ask"
        conf.settings.segment_outro = "ask"
        conf.settings.segment_commercial = "off"
        sr.set_account(ACCOUNT)
        self.store = sr.SkipStore()
        self.key = sr.show_key(episode())

    def tearDown(self):
        (sr.OPTIONS.learn, sr.OPTIONS.rule_action, sr.OPTIONS.lookup_online,
         account, conf.settings.segment_intro, conf.settings.segment_outro,
         conf.settings.segment_commercial) = self._saved
        sr.set_account(account)

    def augment(self, item, intros=()):
        video = Vid(item, intros)
        intro_sources.augment(video, make, self.store)
        return video


class Keys(Base):
    def test_keyed_per_server_user_and_show(self):
        self.assertEqual(self.key, ACCOUNT + ":" + SERIES)
        self.assertEqual(sr.account_key("S-1", "0101" * 8), "s1:" + "01" * 16)
        self.assertIsNone(sr.account_key(None, "01" * 16))
        # an older UI with no server id: a hash, never the address
        key = sr.account_key(None, "01" * 16, "https://jf.example:8920/")
        self.assertTrue(key.startswith("url-"))
        self.assertNotIn("example", key)

    def test_a_movie_has_no_key(self):
        self.assertIsNone(sr.show_key({"Id": "m", "RunTimeTicks": 5}))

    def test_no_account_no_key(self):
        sr.set_account(None)
        self.assertIsNone(sr.show_key(episode()))

    def test_a_second_user_sees_none(self):
        self.store.add_rule(self.key, "start", 0, 40)
        sr.set_account(OTHER_USER)
        self.assertEqual(self.augment(episode()).intros, [])
        self.assertEqual(sr.listing(self.store, OTHER_USER), {"shows": []})
        self.assertEqual(len(sr.listing(self.store, ACCOUNT)["shows"]), 1)


class Anchors(Base):
    def segs(self, video):
        return [(s.type, round(s.start, 2), round(s.end, 2), s.source,
                 s.action) for s in video.intros]

    def test_a_start_rule_on_episodes_of_different_lengths(self):
        self.store.add_rule(self.key, "start", 10, 41)
        for runtime in (1300, 1500):
            self.assertEqual(self.segs(self.augment(episode(runtime=runtime))),
                             [("Commercial", 5.0, 41.0, "rule", "ask")])

    def test_an_end_rule_follows_each_episodes_end(self):
        self.store.add_rule(self.key, "end", 62, 0)
        self.assertEqual(self.segs(self.augment(episode(runtime=1300))),
                         [("Commercial", 1238.0, 1300.0, "rule", "ask")])
        self.assertEqual(self.segs(self.augment(episode(runtime=1500))),
                         [("Commercial", 1438.0, 1500.0, "rule", "ask")])

    def test_an_end_rule_without_a_runtime_is_left_out(self):
        self.store.add_rule(self.key, "end", 62, 0)
        item = episode()
        del item["RunTimeTicks"]
        self.assertEqual(self.augment(item).intros, [])

    def test_always_is_exact_and_ask_starts_early(self):
        rule = self.store.add_rule(self.key, "start", 10, 41, "always")
        self.assertEqual(self.segs(self.augment(episode())),
                         [("Commercial", 10.0, 41.0, "rule", "always")])
        self.store.forget(ACCOUNT, rule_id=rule)
        sr.OPTIONS.rule_action = "always"
        self.store.add_rule(self.key, "start", 10, 41)
        self.assertEqual(self.segs(self.augment(episode()))[0][4], "always",
                         "Settings' choice for a rule with none of its own")

    def test_a_rule_applies_with_its_type_set_to_off(self):
        conf.settings.segment_commercial = "off"
        conf.settings.segment_intro = "off"
        conf.settings.segment_outro = "off"
        self.store.add_rule(self.key, "start", 0, 30)
        self.assertEqual(len(self.augment(episode()).intros), 1)

    def test_the_rule_names_itself(self):
        rule = self.store.add_rule(self.key, "start", 0, 30)
        seg, = self.augment(episode()).intros
        self.assertEqual((seg.rule_id, seg.origin, seg.anchor),
                         (rule, "marked", "start"))


class Precedence(Base):
    def test_a_rule_over_the_intro_takes_its_place_and_name(self):
        self.store.add_rule(self.key, "start", 0, 100)
        video = self.augment(episode(), [Seg("Intro", 60, 95)])
        self.assertEqual([(s.type, s.source) for s in video.intros],
                         [("Intro", "rule")])

    def test_a_rule_beside_the_intro_is_an_extra(self):
        self.store.add_rule(self.key, "start", 0, 15)
        video = self.augment(episode(), [Seg("Intro", 60, 95)])
        self.assertEqual(sorted((s.type, s.source) for s in video.intros),
                         [("Commercial", "rule"), ("Intro", "server")])

    def test_an_end_rule_over_the_credits_keeps_skip_credits(self):
        self.store.add_rule(self.key, "end", 120, 0)
        video = self.augment(episode(runtime=1400), [Seg("Outro", 1300, 1400)])
        self.assertEqual([(s.type, s.source) for s in video.intros],
                         [("Outro", "rule")])

    def test_a_rule_over_chapter_intro(self):
        self.store.add_rule(self.key, "start", 55, 160)
        chapters = [{"Name": "Opening", "StartPositionTicks": 60 * T},
                    {"Name": "Part A", "StartPositionTicks": 150 * T}]
        video = self.augment(episode(chapters=chapters))
        self.assertEqual([(s.type, s.source) for s in video.intros],
                         [("Intro", "rule")])

    def test_online_yields_to_a_rule(self):
        self.store.add_rule(self.key, "start", 55, 160)
        video = self.augment(episode(), [Seg("Intro", 60, 150)])
        self.assertFalse(intro_sources.apply_online(video, make, "Intro", 60,
                                                    150))

    def test_learned_intro_yields_to_a_rule_over_it(self):
        """The learned guess is not a real intro: the rule stays an extra
        and the guess is not offered beside it."""
        self.store.learn_intro(self.key, 1, 60, 150)
        self.store.add_rule(self.key, "start", 55, 150)
        video = self.augment(episode())
        self.assertEqual([(s.type, s.source) for s in video.intros],
                         [("Commercial", "rule")])

    def test_no_rules_no_change(self):
        """Golden: an item with no rule gets exactly what 1.5 gave it."""
        chapters = [{"Name": "Opening", "StartPositionTicks": 60 * T},
                    {"Name": "Part A", "StartPositionTicks": 150 * T},
                    {"Name": "Credits", "StartPositionTicks": 1300 * T}]
        video = self.augment(episode(chapters=chapters),
                             [Seg("Recap", 0, 30)])
        self.assertEqual([(s.type, s.start, s.end, s.source)
                          for s in video.intros],
                         [("Recap", 0, 30, "server"),
                          ("Intro", 60.0, 150.0, "chapters"),
                          ("Outro", 1300.0, 1400.0, "chapters")])


class Watch(Base):
    """`SkipWatcher`: the one-seek intro, candidates, corrections."""

    def setUp(self):
        super().setUp()
        self.watcher = sr.SkipWatcher(self.store)
        self.t = 0.0

    def play(self, video, *positions, duration=None):
        for p in positions:
            self.t += 0.5
            self.watcher.sample(video, p, now=self.t, duration=duration)

    def wait(self, seconds):
        self.t += seconds

    def skip(self, video, *positions, duration=None):
        """`play`, then a reading well after the last seek: a burst of seeks
        is judged once they stop (`BURST_GAP`)."""
        self.play(video, *positions, duration=duration)
        self.wait(sr.BURST_GAP + 1)
        self.play(video, positions[-1] + 0.5, duration=duration)

    # -- the one-seek learned intro (1.4, kept) --------------------------

    def test_forward_jump_is_learned(self):
        self.play(Vid(episode()), 60, 60.5, 150)
        self.assertEqual(self.store.intro(self.key, 1), (60.5, 150.0))

    def test_jumps_outside_the_rule_are_not(self):
        for before, after in [(60, 85), (60, 215), (700, 790)]:
            self.play(Vid(episode()), before, after)
            self.assertIsNone(self.store.intro(self.key, 1), (before, after))

    def test_not_learned_when_an_intro_is_known(self):
        self.play(Vid(episode(), [Seg("Intro", 10, 20, "chapters")]), 60, 150)
        self.assertIsNone(self.store.intro(self.key, 1))

    def test_jump_from_skip_segment_is_not_learned(self):
        self.watcher.note_skip(now=0.0)
        self.play(Vid(episode()), 60, 150)
        self.assertIsNone(self.store.intro(self.key, 1))
        self.assertIsNone(self.store.show(self.key))

    def test_not_learned_when_off(self):
        sr.OPTIONS.learn = False
        self.play(Vid(episode()), 60, 150)
        self.assertIsNone(self.store.show(self.key))

    def learned_video(self, item_id="e2"):
        self.store.learn_intro(self.key, 1, 60, 150)
        return Vid(episode(item_id), [Seg("Intro", 55, 150, "learned")])

    def test_seek_back_into_it_forgets(self):
        video = self.learned_video()
        self.play(video, 100, 80)
        self.assertIsNone(self.store.intro(self.key, 1))

    def test_skip_then_undo_twice_forgets(self):
        video = self.learned_video()
        self.play(video, 60, 150, 70)
        self.assertEqual(self.store.intro(self.key, 1), (60.0, 150.0))
        self.play(video, 150, 70)
        self.assertIsNone(self.store.intro(self.key, 1))

    def test_moves(self):
        """1.5.1's rule (1.6 integration: as its review judged it, `Moves`):
        a skip by hand of the same part 10 s or more from the learned spot
        marks the season "moves"; its spot is no longer offered, and not
        relearned."""
        self.store.learn_intro(self.key, 1, 104, 132)     # S1E1's title
        self.play(Vid(episode("e2")), 40, 40.5, 72)       # S1E2's, earlier
        self.assertIsNone(self.store.intro(self.key, 1))
        self.assertTrue(self.store.intro_entry(self.key, 1)["moves"])
        self.assertEqual(self.augment(episode("e3")).intros, [],
                         "no fixed spot offered")
        self.play(Vid(episode("e3")), 200, 200.5, 240)
        self.assertIsNone(self.store.intro(self.key, 1), "not relearned")

    def test_a_skip_near_the_learned_spot_does_not_move_it(self):
        self.store.learn_intro(self.key, 1, 104, 132)
        self.play(Vid(episode("e2")), 97, 97.5, 133)
        self.assertEqual(self.store.intro(self.key, 1), (104.0, 132.0))

    def test_migration_keeps_moves(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, sr.LEGACY_FILE), "w",
                      encoding="utf-8") as f:
                json.dump({"version": 1, "entries": {
                    SERIES + ":1": {"start": 60, "end": 150, "undone": 0},
                    SERIES + ":2": {"start": 70, "end": 160, "moves": True},
                    "bad": {"start": 1}}}, f)
            store = sr.SkipStore(os.path.join(d, sr.RULES_FILE))
            store.migrate(ACCOUNT)
            self.assertEqual(store.intro(self.key, 1), (60.0, 150.0))
            self.assertIsNone(store.intro(self.key, 2))
            self.assertTrue(store.intro_entry(self.key, 2)["moves"])
            self.assertFalse(os.path.exists(os.path.join(d, sr.LEGACY_FILE)))
            self.assertTrue(os.path.exists(os.path.join(
                d, sr.LEGACY_FILE + sr.LEGACY_DONE_SUFFIX)))
            # once: a second store (the next start) has nothing to migrate
            sr.SkipStore(os.path.join(d, sr.RULES_FILE)).migrate(OTHER_USER)
            self.assertEqual(sr.SkipStore(os.path.join(
                d, sr.RULES_FILE)).shows_of(OTHER_USER), {})

    # -- candidates and the suggestion ------------------------------------

    def test_the_same_skip_in_two_episodes_asks_once(self):
        self.skip(Vid(episode("e1")), 2, 2.5, 41)
        self.assertEqual(self.watcher.take_suggestions(), [])
        self.skip(Vid(episode("e2")), 3, 3.5, 43)
        found, = self.watcher.take_suggestions()
        self.assertEqual((found["anchor"], found["seriesId"],
                          found["seriesName"]), ("start", SERIES, "Shadows"))
        self.assertAlmostEqual(found["startS"], 3.0)
        self.assertAlmostEqual(found["endS"], 42.0)
        # once: the same skip again while it waits for an answer does not ask
        # again (a new episode with it unanswered does: the next test)
        self.skip(self.watcher._video, 2, 2.5, 42)
        self.assertEqual(self.watcher.take_suggestions(), [])

    def test_an_end_skip_in_two_episodes_of_different_lengths(self):
        self.skip(Vid(episode("e1", runtime=1300)), 1240, 1240.5, 1299.5,
                  duration=1300)
        self.skip(Vid(episode("e2", runtime=1500)), 1440, 1440.5, 1499.5,
                  duration=1500)
        found, = self.watcher.take_suggestions()
        self.assertEqual(found["anchor"], "end")
        self.assertAlmostEqual(found["startS"], 1440.0, places=0)

    def test_an_end_skip_is_asked_as_it_lands_and_stays_through_the_next(self):
        """Review (Major 2): a seek to the last seconds was judged only once
        the next item began, its question named the item before and was
        released at once (the UI dropped it too). Now it is asked at the
        landing, while its episode plays, and stays open through the next
        episode; the one after that releases it."""
        for item in ("e1", "e2"):
            self.play(Vid(episode(item, runtime=1300)), 1240.0, 1240.5,
                      1299.4, duration=1300)
        found, = self.watcher.take_suggestions()
        self.assertEqual(found["itemId"], ("e2" * 32)[:32])
        self.assertEqual(found["anchor"], "end")
        # the next episode begins: still asked, so it is not asked twice
        self.play(Vid(episode("e3", runtime=1300)), 0.5, 1.0, duration=1300)
        asked = [c["asked"] for c in self.store.show(self.key)["candidates"]]
        self.assertEqual(asked, [True])
        # and the one after: released (nobody answered)
        self.play(Vid(episode("e4", runtime=1300)), 0.5, duration=1300)
        asked = [c["asked"] for c in self.store.show(self.key)["candidates"]]
        self.assertEqual(asked, [False])

    def test_two_different_skips_or_one_episode_ask_nothing(self):
        self.skip(Vid(episode("e1")), 2, 2.5, 41)
        self.skip(Vid(episode("e2")), 200, 200.5, 300)      # elsewhere
        self.skip(Vid(episode("e1")), 2, 2.5, 41)           # same episode
        self.skip(Vid(episode("e3")), 2, 2.5, 120)          # far longer
        self.assertEqual(self.watcher.take_suggestions(), [])

    def test_app_jumps_never_count(self):
        # the pill or S
        for item in ("e1", "e2"):
            self.watcher.note_skip(now=self.t)
            self.skip(Vid(episode(item)), 2, 41)
        # a part skipped by itself
        always = Seg("Commercial", 2, 41, "rule")
        always.action = "always"
        for item in ("e3", "e4"):
            self.skip(Vid(episode(item), [always]), 2, 41.2)
        # the chapters menu
        chapters = [{"Name": "A", "StartPositionTicks": 41 * T}]
        for item in ("e5", "e6"):
            self.skip(Vid(episode(item, chapters=chapters)), 2, 41)
        self.assertEqual(self.watcher.take_suggestions(), [])

    def always_part(self, item, start=30, end=90, source="rule"):
        seg = Seg("Commercial" if source == "rule" else "Intro", start, end,
                  source)
        if source == "rule":
            seg.action = "always"
            seg.rule_id = "r1"
        return Vid(episode(item), [seg])

    def assert_nothing_learned(self):
        show = self.store.show(self.key) or {}
        self.assertEqual(show.get("intros", {}), {})
        self.assertEqual(show.get("candidates", []), [])
        self.assertEqual(self.watcher.take_suggestions(), [])

    def test_an_always_skip_read_just_before_its_part_is_the_apps(self):
        """Review (Major 1): the readings come every 0.5 s and the player
        skips up to a second late, so the reading before the app's jump can
        be just before the part. Rule 30-90 s, always: readings 29.2, 29.7,
        then 90.0, in two episodes. It stored a learned intro 29.7-90 and
        asked "Skip this every episode?" about a part Nixie skips."""
        for item in ("e1", "e2"):
            self.skip(self.always_part(item), 29.2, 29.7, 90.0, 90.5)
        self.assert_nothing_learned()

    def test_the_servers_always_intro_is_the_apps_too(self):
        conf.settings.segment_intro = "always"
        for item in ("e1", "e2"):
            self.skip(self.always_part(item, source="server"), 29.2, 29.7,
                      90.0, 90.5)
        self.assert_nothing_learned()

    def test_a_jump_the_player_marked_is_the_apps_whatever_the_readings(self):
        """The player marks the moment it skips (`skipped_at`): a reading
        two seconds before the part (a busy action thread) is still its."""
        for item in ("e1", "e2"):
            video = self.always_part(item)
            self.play(video, 27.5, 28.0)
            video.intros[0].skipped_at = self.t + 0.2
            self.skip(video, 90.0, 90.5)
        self.assert_nothing_learned()

    def test_a_refusal_never_asks_again(self):
        self.skip(Vid(episode("e1")), 2, 2.5, 41)
        self.skip(Vid(episode("e2")), 3, 3.5, 43)
        found, = self.watcher.take_suggestions()
        self.assertEqual(self.store.answer(ACCOUNT, found["id"], "never"), {})
        for item in ("e3", "e4", "e5"):
            self.skip(Vid(episode(item)), 2, 2.5, 41)
        self.assertEqual(self.watcher.take_suggestions(), [])
        self.assertTrue(sr.listing(self.store, ACCOUNT)["shows"][0]["refused"])

    def test_yes_makes_a_learned_rule(self):
        self.skip(Vid(episode("e1")), 2, 2.5, 41)
        self.skip(Vid(episode("e2")), 3, 3.5, 43)
        found, = self.watcher.take_suggestions()
        result = self.store.answer(ACCOUNT, found["id"], "yes")
        rule, = self.store.show(self.key)["rules"]
        self.assertEqual(result, {"ruleId": rule["id"]})
        self.assertEqual((rule["origin"], rule["anchor"], rule["action"]),
                         ("learned", "start", None))
        seg, = self.augment(episode("e3")).intros
        self.assertEqual((seg.source, seg.origin), ("rule", "learned"))
        self.assertIsNone(self.store.answer(ACCOUNT, found["id"], "yes"),
                          "answered once")

    def test_an_unanswered_question_asks_again(self):
        """The question stays open through the episode after the one it
        was asked in (its toast is still up there, review Major 2): a skip
        there does not ask twice. Unanswered after that, the next matching
        skip asks again."""
        self.skip(Vid(episode("e1")), 2, 2.5, 41)
        self.skip(Vid(episode("e2")), 3, 3.5, 43)
        self.assertEqual(len(self.watcher.take_suggestions()), 1)
        self.skip(Vid(episode("e3")), 2, 2.5, 41)     # still open
        self.assertEqual(self.watcher.take_suggestions(), [])
        self.skip(Vid(episode("e4")), 2, 2.5, 41)     # e2's released
        self.assertEqual(len(self.watcher.take_suggestions()), 1)
        # answered (in e4), then the next episode: nothing to release
        found = self.store.show(self.key)["candidates"][0]
        self.watcher.answered(found["id"])
        self.store.answer(ACCOUNT, found["id"], "never")
        self.skip(Vid(episode("e5")), 2, 2.5, 41)
        self.assertEqual(self.watcher.take_suggestions(), [])

    def test_not_now_drops_it_and_it_can_ask_again_later(self):
        self.skip(Vid(episode("e1")), 2, 2.5, 41)
        self.skip(Vid(episode("e2")), 3, 3.5, 43)
        found, = self.watcher.take_suggestions()
        self.assertEqual(self.store.answer(ACCOUNT, found["id"], "not_now"),
                         {})
        self.assertEqual(self.store.show(self.key)["candidates"], [])
        self.skip(Vid(episode("e3")), 2, 2.5, 41)
        self.skip(Vid(episode("e4")), 2, 2.5, 41)
        self.assertEqual(len(self.watcher.take_suggestions()), 1)

    def test_candidates_expire_after_ten_episodes(self):
        self.skip(Vid(episode("e1")), 2, 2.5, 41)
        for n in range(11):
            self.play(Vid(episode("x%d" % n)), 500)     # plays, no skip
        self.assertEqual(self.store.show(self.key)["candidates"], [])
        self.skip(Vid(episode("e2")), 3, 3.5, 43)
        self.assertEqual(self.watcher.take_suggestions(), [])

    def test_learning_off_learns_no_candidates(self):
        sr.OPTIONS.learn = False
        self.skip(Vid(episode("e1")), 2, 2.5, 41)
        self.skip(Vid(episode("e2")), 3, 3.5, 43)
        self.assertEqual(self.watcher.take_suggestions(), [])

    # -- corrections (the margin) ------------------------------------------

    def rule_video(self, item_id="e2"):
        self.store.add_rule(self.key, "start", 0, 41)
        return self.augment(episode(item_id))

    def test_a_seek_right_after_a_skip_moves_the_rules_end(self):
        video = self.rule_video()
        seg, = video.intros
        self.play(video, 5)
        self.watcher.note_skip(seg, now=self.t)
        self.play(video, 41, 41.5, 42)
        self.play(video, 37)                    # back 5 s: it ended early
        rule, = self.store.show(self.key)["rules"]
        self.assertEqual((rule["end"], rule["corrected"]), (37.0, True))
        self.assertEqual(self.augment(episode("e3")).intros[0].end, 37.0)

    def test_a_forward_seek_right_after_moves_it_later(self):
        video = self.rule_video()
        self.play(video, 5)
        self.watcher.note_skip(video.intros[0], now=self.t)
        self.play(video, 41, 41.5, 51.5)
        self.assertEqual(self.store.show(self.key)["rules"][0]["end"], 51.5)

    def test_a_seek_long_after_is_no_correction(self):
        video = self.rule_video()
        self.play(video, 5)
        self.watcher.note_skip(video.intros[0], now=self.t)
        self.play(video, 41, 41.5)
        self.wait(sr.CORRECTION_WINDOW + 1)
        self.play(video, 36.5)
        self.assertEqual(self.store.show(self.key)["rules"][0]["end"], 41.0)

    def test_an_always_skip_can_be_corrected_too(self):
        self.store.add_rule(self.key, "start", 5, 41, "always")
        video = self.augment(episode("e2"))
        self.play(video, 6, 41.1, 41.6, 45)
        self.assertEqual(self.store.show(self.key)["rules"][0]["end"], 45.0)

    def test_a_correction_is_not_a_candidate(self):
        video = self.rule_video()
        self.play(video, 5)
        self.watcher.note_skip(video.intros[0], now=self.t)
        self.skip(video, 41, 41.5, 61.5)
        self.assertEqual(self.store.show(self.key)["candidates"], [])

    def test_seeks_one_after_another_are_one_skip(self):
        """+10 s four times near the start is one 40 s skip, not four of
        10 s: the same burst in two episodes asks once, for 0:02-0:42."""
        for item in ("e1", "e2"):
            self.skip(Vid(episode(item)), 2, 12, 12.5, 22.5, 23, 33, 33.5, 43)
        found, = self.watcher.take_suggestions()
        self.assertAlmostEqual(found["startS"], 2.0)
        self.assertAlmostEqual(found["endS"], 43.0)
        candidates = self.store.show(self.key)["candidates"]
        self.assertEqual(len(candidates), 1)

    def test_a_seek_back_in_the_burst_refines_it(self):
        """Overshot and came back a little: the skip ends where it landed;
        back before where it began: no skip at all."""
        for item in ("e1", "e2"):
            self.skip(Vid(episode(item)), 2, 60, 60.5, 45)
        found, = self.watcher.take_suggestions()
        self.assertAlmostEqual(found["endS"], 45.0)
        before = len(self.store.show(self.key)["candidates"])
        self.skip(Vid(episode("e3")), 100, 150, 150.5, 90)
        self.assertEqual(len(self.store.show(self.key)["candidates"]), before)

    def test_seeks_far_apart_in_time_are_two(self):
        video = Vid(episode("e1"))
        self.play(video, 2, 30)
        self.wait(sr.BURST_GAP + 1)
        self.skip(video, 30.5, 60)
        self.assertEqual(len(self.store.show(self.key)["candidates"]), 2)


class AlwaysDoesNotLoop(Base):
    def test_a_seek_back_into_an_always_part_is_not_skipped_again(self):
        """The player skips an ``always`` segment once (``has_triggered``);
        a rule's segment is an ordinary segment to it."""
        self.store.add_rule(self.key, "start", 5, 41, "always")
        seg, = self.augment(episode()).intros
        from jellyfin_mpv_shim.media import Video

        video = Video.__new__(Video)
        video.intros = [seg]
        ready, found = video.get_current_intro(10)
        self.assertIs(found, seg)
        seg.has_triggered = True        # what the player's skip records
        ready, found = video.get_current_intro(10)
        self.assertTrue(found.has_triggered)

    def rebuilt(self, before, position):
        """The show's rule segments built again (what
        `bridge_skip.refresh_rules` does after a Yes, an Add or a forget),
        carrying what the episode did with each."""
        olds = {s.rule_id: s for s in before}
        video = self.augment(episode())
        for seg in video.intros:
            sr.carry_rule_state(olds.get(seg.rule_id), seg, position)
        return {s.rule_id: s for s in video.intros}

    def test_an_always_part_skipped_stays_skipped_when_the_rules_rebuild(self):
        """1.6.1 (the 1.6 review's skip queue): a Yes or an Add mid-episode
        rebuilt every rule's segment, so an "always" part the player had
        skipped re-armed: a seek back into it skipped it again."""
        first = self.store.add_rule(self.key, "start", 5, 41, "always")
        seg, = self.augment(episode()).intros
        seg.has_triggered, seg.skipped_at = True, 12.5
        # the viewer seeks back into it, then marks another part
        added = self.store.add_rule(self.key, "end", 62, 0, "always")
        segs = self.rebuilt([seg], position=20.0)
        self.assertTrue(segs[first].has_triggered, "not skipped again")
        self.assertEqual(segs[first].skipped_at, 12.5)
        self.assertFalse(segs[added].has_triggered,
                         "the new part, still ahead, is skipped when reached")

    def test_a_rule_made_from_the_skip_just_taken_does_not_skip_it_again(self):
        # a Yes (or Shift+S) right after skipping 5-41 by hand: at 43 s
        segs = self.rebuilt([], position=43.0)
        self.assertEqual(segs, {})
        made = self.store.add_rule(self.key, "start", 5, 41, "always",
                                   "learned")
        segs = self.rebuilt([], position=43.0)
        self.assertTrue(segs[made].has_triggered)

    def test_a_rule_added_inside_its_part_still_skips_it_now(self):
        """1.6.1 review (a regression from 1.6.0): the sheet's default part is
        now to 30 s from now; added "skipped by itself" with the playhead
        inside it, it counted as skipped and this episode played it."""
        from jellyfin_mpv_shim.media import Video

        made = self.store.add_rule(self.key, "start", 20, 50, "always")
        for position in (20.0, 21.0, 49.5):
            segs = self.rebuilt([], position=position)
            self.assertFalse(segs[made].has_triggered, position)
        video = Video.__new__(Video)
        video.intros = list(self.rebuilt([], position=21.0).values())
        ready, found = video.get_current_intro(21.5)
        self.assertEqual((found.rule_id, found.has_triggered), (made, False),
                         "the player skips it")
        self.assertTrue(self.rebuilt([], position=50.0)[made].has_triggered,
                        "past its end: as taken")

    def test_a_snapped_edge_stays_unless_the_rule_moved(self):
        made = self.store.add_rule(self.key, "start", 5, 41, "always")
        seg, = self.augment(episode()).intros
        seg.end, seg.snap_pending = 42.35, False        # snapped
        segs = self.rebuilt([seg], position=1.0)
        self.assertEqual((segs[made].end, segs[made].snap_pending),
                         (42.35, False))
        self.store.correct_rule(self.key, made, 44.0)   # corrected: snaps anew
        segs = self.rebuilt([seg], position=1.0)
        self.assertEqual((segs[made].end, segs[made].snap_pending),
                         (44.0, True))


class StoreFile(Base):
    def test_roundtrip_forget_and_list(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, sr.RULES_FILE)
            a = sr.SkipStore(path)
            rule = a.add_rule(self.key, "start", 0, 40, name="Shadows")
            a.learn_intro(self.key, 2, 60, 150)
            b = sr.SkipStore(path)
            shows = sr.listing(b, ACCOUNT)["shows"]
            self.assertEqual([(s["seriesId"], s["name"]) for s in shows],
                             [(SERIES, "Shadows")])
            self.assertEqual([r["id"] for r in shows[0]["rules"]], [rule])
            intro_id = shows[0]["learnedIntros"][0]["id"]
            self.assertEqual(intro_id, "intro-%s-2" % SERIES)
            self.assertEqual(b.forget(ACCOUNT, rule_id=intro_id), 1)
            self.assertEqual(b.forget(ACCOUNT, rule_id=rule), 1)
            self.assertEqual(sr.listing(a, ACCOUNT), {"shows": []},
                             "the other store re-reads the file")

    def test_forget_a_show_and_all(self):
        self.store.add_rule(self.key, "start", 0, 40)
        other = ACCOUNT + ":" + "cd" * 16
        self.store.add_rule(other, "start", 0, 40)
        self.store.add_rule(OTHER_USER + ":" + SERIES, "start", 0, 40)
        self.assertEqual(self.store.forget(ACCOUNT, series=SERIES), 1)
        self.assertEqual(self.store.forget(ACCOUNT), 1)
        self.assertEqual(sr.listing(self.store, ACCOUNT), {"shows": []})
        self.assertEqual(len(sr.listing(self.store, OTHER_USER)["shows"]), 1,
                         "another user's stay")

    def test_a_corrupt_or_half_written_file_starts_over(self):
        for text in ("{nope", '{"shows": [1, 2]}', '{"version": 2, "shows": '
                     '{"k": {"rules": [{"id": "r1", "anchor": "start", '
                     '"start": 50, "end": 10, "origin": "marked"}]}}}', ""):
            with self.subTest(text=text[:20]), \
                    tempfile.TemporaryDirectory() as d:
                path = os.path.join(d, sr.RULES_FILE)
                with open(path, "w", encoding="utf-8") as f:
                    f.write(text)
                store = sr.SkipStore(path)
                video = Vid(episode())
                intro_sources.augment(video, make, store)   # never raises
                self.assertEqual(video.intros, [])
                store.add_rule(self.key, "start", 0, 40)
                self.assertEqual(len(sr.SkipStore(path).show(
                    self.key)["rules"]), 1)

    LEGACY = {"version": 1,
              "entries": {SERIES + ":1": {"start": 150.5, "end": 190,
                                          "undone": 0}},
              "seasons": {ACCOUNT + ":" + SERIES + ":1": {
                  "skips": [[150.5, 190.0]], "moves": False}}}

    def write_legacy(self, folder, data=None):
        path = os.path.join(folder, sr.LEGACY_FILE)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(self.LEGACY if data is None else data, f)
        return path

    def test_a_second_migration_never_brings_a_forgotten_intro_back(self):
        """1.6.1, the merge review's R3 (its twice_probe.py): migrate,
        Forget all, the legacy file back (1.5.1 ran again after a downgrade,
        or the rename failed), the next start: the intro stays forgotten."""
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, sr.RULES_FILE)
            legacy = self.write_legacy(d)
            first = sr.SkipStore(path)
            first.migrate(ACCOUNT)
            self.assertEqual(first.intro(self.key, 1), (150.5, 190.0))
            self.assertTrue(os.path.exists(legacy + sr.LEGACY_DONE_SUFFIX))
            self.assertEqual(first.forget(ACCOUNT), 1)
            self.assertIsNone(first.intro(self.key, 1))
            self.write_legacy(d)
            again = sr.SkipStore(path)
            again.migrate(ACCOUNT)
            self.assertIsNone(again.intro(self.key, 1))
            self.assertTrue(os.path.exists(legacy), "left alone")
            with open(path, encoding="utf-8") as f:
                self.assertTrue(json.load(f)["legacyMigrated"])

    def test_migrated_once_even_when_the_rename_fails_or_it_held_nothing(
            self):
        from unittest import mock

        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, sr.RULES_FILE)
            self.write_legacy(d, {"version": 1, "entries": {}})
            sr.SkipStore(path).migrate(ACCOUNT)
            with open(path, encoding="utf-8") as f:
                self.assertTrue(json.load(f)["legacyMigrated"],
                                "recorded though nothing moved")
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, sr.RULES_FILE)
            self.write_legacy(d)
            real = os.replace

            def no_rename(src, dst):
                if src.endswith(sr.LEGACY_FILE):
                    raise OSError("in use")
                return real(src, dst)

            with mock.patch.object(sr.os, "replace", no_rename):
                sr.SkipStore(path).migrate(ACCOUNT)
            store = sr.SkipStore(path)
            store.forget(ACCOUNT, rule_id="intro-%s-1" % SERIES)
            sr.SkipStore(path).migrate(ACCOUNT)    # the file is still there
            self.assertIsNone(sr.SkipStore(path).intro(self.key, 1))

    def test_a_file_160_wrote_counts_as_migrated(self):
        """1.6.1 review (a gap in R3): 1.6.0 wrote no ``legacyMigrated``; with
        its rename failed, 1.6.1 migrated the legacy file again and brought
        back intros forgotten under 1.6.0, also when every show was
        forgotten (an empty ``shows``)."""
        for shows in ({}, {self.key: {"name": "Shadows", "rules": [{
                "id": "r1", "anchor": "start", "start": 0, "end": 40,
                "action": None, "origin": "marked"}]}}):
            with self.subTest(shows=len(shows)), \
                    tempfile.TemporaryDirectory() as d:
                path = os.path.join(d, sr.RULES_FILE)
                with open(path, "w", encoding="utf-8") as f:
                    json.dump({"version": 2, "shows": shows}, f)  # 1.6.0's
                legacy = self.write_legacy(d)          # its rename failed
                store = sr.SkipStore(path)
                store.migrate(ACCOUNT)
                self.assertIsNone(store.intro(self.key, 1))
                self.assertTrue(os.path.exists(legacy), "left alone")
                with open(path, encoding="utf-8") as f:
                    saved = json.load(f)
                self.assertTrue(saved["legacyMigrated"], "recorded")
                self.assertEqual(len(saved["shows"]), len(shows))
        # an unreadable file is set aside and starts over: it migrates
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, sr.RULES_FILE)
            open(path, "wb").close()
            self.write_legacy(d)
            store = sr.SkipStore(path)
            store.migrate(ACCOUNT)
            self.assertEqual(store.intro(self.key, 1), (150.5, 190.0))

    def test_a_question_left_open_by_a_restart_asks_again(self):
        """1.6.1 (the 1.6 review's skip queue): "asked" was kept in the file,
        so a question open when the backend ended (nobody answered, nothing
        released it) never came back."""
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, sr.RULES_FILE)
            before = sr.SkipStore(path)
            self.assertIsNone(before.note_candidate(self.key, "e1", "start",
                                                    2.0, 41.0))
            asked = before.note_candidate(self.key, "e2", "start", 3.0, 43.0)
            self.assertIsNotNone(asked, "asked once")
            self.assertIsNone(before.note_candidate(self.key, "e3", "start",
                                                    2.5, 42.0),
                              "not again while it is open")
            # the backend ends with the question open; the next start
            after = sr.SkipStore(path)
            again = after.note_candidate(self.key, "e4", "start", 2.0, 41.0)
            self.assertIsNotNone(again)
            self.assertEqual(again["id"], asked["id"])
            # while this run's own question is open, it waits as before
            self.assertIsNone(after.note_candidate(self.key, "e5", "start",
                                                   2.0, 41.0))

    def test_a_rule_too_long_or_empty_is_refused(self):
        for anchor, start, end in (("start", 40, 10), ("start", 0, 700),
                                   ("end", 0, 30), ("middle", 0, 5)):
            with self.assertRaises(ValueError):
                self.store.add_rule(self.key, anchor, start, end)

    def test_the_same_part_marked_twice_is_one_rule(self):
        self.store.add_rule(self.key, "start", 0, 40)
        self.store.add_rule(self.key, "start", 1, 41)
        self.assertEqual(len(self.store.show(self.key)["rules"]), 1)


class AdChapters(Base):
    def test_names(self):
        for name, kind in [("Commercial", "Commercial"),
                           ("COMMERCIAL BREAK", "Commercial"),
                           ("Ad break 2", "Commercial"),
                           ("Advertisement", "Commercial"),
                           ("Promo", "Commercial"),
                           ("Sponsor", "Commercial"),
                           ("sponsor-message", "Commercial"),
                           ("Commercially Speaking", None),
                           ("Promotional", None), ("Adventure", None),
                           ("Ad", None), ("Intro", "Intro"),
                           ("Credits", "Outro")]:
            self.assertEqual(intro_sources.chapter_type(name), kind, name)

    def test_every_ad_chapter_under_five_minutes(self):
        conf.settings.segment_commercial = "ask"
        chapters = [{"Name": n, "StartPositionTicks": s * T} for n, s in
                    [("Act 1", 0), ("Commercial", 300), ("Act 2", 360),
                     ("Ad break", 700), ("Act 3", 760), ("Promo", 1000),
                     ("Act 4", 1400)]]
        item = episode(chapters=chapters, runtime=2000)
        self.assertEqual(
            [(s.type, s.start, s.end, s.source)
             for s in self.augment(item).intros],
            [("Commercial", 300.0, 360.0, "chapters"),
             ("Commercial", 700.0, 760.0, "chapters")],
            "the 400 s promo is not one")

    def test_ad_chapters_only_when_not_off_and_not_from_the_server(self):
        chapters = [{"Name": "Commercial", "StartPositionTicks": 300 * T},
                    {"Name": "Act 2", "StartPositionTicks": 360 * T}]
        self.assertEqual(self.augment(episode(chapters=chapters)).intros, [])
        conf.settings.segment_commercial = "ask"
        video = self.augment(episode(chapters=chapters),
                             [Seg("Commercial", 10, 20)])
        self.assertEqual([s.source for s in video.intros], ["server"])


class Moves(Base):
    """1.5.1's "moves" (It's Always Sunny's "Skip intro" at 0:00) in this
    learner (1.6 integration), 1.5.1's own cases (its test_intro_sources'
    Moves, ported from `LearnedStore`/`SkipLearner`): the first skip by hand
    of each episode is kept per season, and one of the same part 10 s or more
    from an earlier one marks the season "moves"; per server, user, show and
    season (the review of 1.5.1, BH-151-M1)."""

    def setUp(self):
        super().setUp()
        self.watcher = sr.SkipWatcher(self.store)
        self.t = 0.0

    def play(self, video, *positions):
        for p in positions:
            self.t += 0.5
            self.watcher.sample(video, p, now=self.t)

    def ep(self, n, season=1, series=SERIES):
        return Vid(dict(episode(season=season), Id="%032d" % n,
                        SeriesId=series))

    def moves(self, video):
        entry = self.store.intro_entry(sr.show_key(video.item),
                                       video.item["ParentIndexNumber"])
        return bool(entry and entry.get("moves"))

    def offered(self, video):
        video.intros = []
        intro_sources.augment(video, make, self.store)
        return [(s.type, s.start, s.end, s.source) for s in video.intros]

    def test_sunny_the_intro_moves_within_a_season_and_its_spot_goes(self):
        first = self.ep(1)
        self.play(first, 150, 150.5, 190)     # S1E1: the intro after a long cold open
        second = self.ep(2)
        self.assertEqual([("Intro", 145.5, 190.0, "learned")],
                         self.offered(second))
        self.play(second, 70, 70.5, 110)      # S1E2: the same length, 40 s before the spot offered
        self.assertTrue(self.moves(second))
        self.assertEqual([], self.offered(self.ep(3)))
        self.assertIsNotNone(self.store.intro_entry(self.key, 1),
                             "kept, just not offered")
        # season 2 is judged on its own
        self.play(self.ep(10, season=2), 100, 100.5, 140)
        self.assertEqual([("Intro", 95.5, 140.0, "learned")],
                         self.offered(self.ep(11, season=2)))

    def test_a_different_spot_in_each_season_keeps_both(self):
        # the hunt's case: season 1 at the start, season 2 after a cold open
        # added that season (this learner keeps the first spot learned)
        self.play(self.ep(1), 0, 0.5, 50)
        self.play(self.ep(2), 1, 1.5, 51)
        self.play(self.ep(10, season=2), 120, 120.5, 170)
        self.play(self.ep(11, season=2), 121, 121.5, 171)
        self.assertFalse(self.moves(self.ep(3)))
        self.assertFalse(self.moves(self.ep(12, season=2)))
        self.assertEqual([("Intro", 0.0, 50.0, "learned")],
                         self.offered(self.ep(3)))
        self.assertEqual([("Intro", 115.5, 170.0, "learned")],
                         self.offered(self.ep(12, season=2)))

    def test_a_recap_skipped_in_one_episode_is_not_the_intro_of_the_next(self):
        self.play(self.ep(1), 2, 2.5, 40)     # E1's first hand skip: the recap (37.5 s)
        self.play(self.ep(2), 55, 55.5, 110)  # E2's: the intro (54.5 s), another part
        self.assertFalse(self.moves(self.ep(3)))
        self.assertEqual("learned", self.offered(self.ep(3))[0][3])

    def test_skips_within_10_s_keep_the_offer(self):
        self.play(self.ep(1), 60, 60.5, 100)
        self.play(self.ep(2), 80, 80.5, 120)      # overlapping
        self.play(self.ep(3), 109, 109.5, 150)    # 9.5 s after the first ended: not 10
        self.assertFalse(self.moves(self.ep(4)))
        self.assertEqual("learned", self.offered(self.ep(4))[0][3])
        # the latest skips are kept to compare the next with
        self.assertEqual([[60.5, 100.0], [80.5, 120.0], [109.5, 150.0]],
                         self.store.intro_entry(self.key, 1)["skips"])

    def test_only_an_episodes_first_hand_skip_counts(self):
        video = self.ep(1)
        self.play(video, 0, 0.5, 40, 150, 150.5, 190)   # the cold open, then the intro, in one episode
        self.assertFalse(self.moves(video))
        self.assertEqual(self.store.intro(self.key, 1), (0.5, 40.0))

    def test_a_skip_pill_press_is_not_by_hand(self):
        self.play(self.ep(1), 150, 150.5, 190)
        video = self.ep(2)
        self.play(video, 0, 0.5)
        self.watcher.note_skip(now=self.t)    # the app, just before its own jump
        self.play(video, 40)
        self.assertFalse(self.moves(video))

    def test_a_reading_between_the_press_and_its_seek_is_not_by_hand(self):
        """1.6.1 (found at the 1.6 integration, `_app_jump`): a 0.5 s
        playback reading that came between the app's `note_skip` and its
        seek landing was taken for the press's jump, and the seek then
        counted as the viewer's (a hand skip 40 s from the spot: "moves")."""
        self.play(self.ep(1), 150, 150.5, 190)
        video = self.ep(2)
        self.play(video, 0, 0.5)
        self.watcher.note_skip(now=self.t)
        self.play(video, 1.0)                 # playback, before the seek lands
        self.play(video, 40)                  # the app's seek
        self.assertFalse(self.moves(video))
        self.assertEqual(self.store.intro_entry(self.key, 1)["skips"],
                         [[150.5, 190.0]], "nothing kept from the app's jump")

    def test_learning_off_notes_nothing(self):
        sr.OPTIONS.learn = False
        self.play(self.ep(1), 150, 150.5, 190)
        self.play(self.ep(2), 0, 0.5, 40)
        self.assertIsNone(self.store.show(self.key))

    def test_kept_per_server_user_show_and_season(self):
        self.play(self.ep(1), 150, 150.5, 190)
        self.play(self.ep(2), 70, 70.5, 110)
        self.assertTrue(self.moves(self.ep(3)))
        self.assertFalse(self.moves(self.ep(3, series="cd" * 16)))
        self.assertFalse(self.moves(self.ep(3, season=2)))
        for account in (OTHER_USER, "s2:" + "01" * 16):
            sr.set_account(account)
            self.assertFalse(self.moves(self.ep(3)), account)
        sr.set_account(ACCOUNT)
        self.assertEqual(ACCOUNT + ":" + SERIES, sr.show_key(self.ep(3).item))
        self.assertIsNone(sr.show_key({"RunTimeTicks": 5 * T}))   # a film

    def test_forgetting_the_learned_intro_clears_it(self):
        self.play(self.ep(1), 150, 150.5, 190)
        self.play(self.ep(2), 70, 70.5, 110)
        self.assertTrue(self.moves(self.ep(3)))
        # Settings' Skip rules: Forget on the season's learned intro
        self.store.forget(ACCOUNT, rule_id="intro-%s-1" % SERIES)
        self.assertFalse(self.moves(self.ep(3)))
        self.play(self.ep(4), 150, 150.5, 190)    # learned again, from nothing
        self.assertEqual(self.store.intro(self.key, 1), (150.5, 190.0))
        self.assertEqual([[150.5, 190.0]],
                         self.store.intro_entry(self.key, 1)["skips"])

    def test_a_file_from_151_migrates_its_marks(self):
        """1.5.1's ``learned-intros.json`` keeps its seasons' marks beside the
        ranges ("seasons", keyed server:user:series:season): each goes onto
        its own learned intro."""
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, sr.LEGACY_FILE), "w",
                      encoding="utf-8") as f:
                json.dump({"version": 1, "entries": {
                    SERIES + ":1": {"start": 150.5, "end": 190, "undone": 0},
                    SERIES + ":2": {"start": 100.5, "end": 140, "undone": 0}},
                    "seasons": {
                        ACCOUNT + ":" + SERIES + ":1": {"skips": [], "moves": True},
                        ACCOUNT + ":" + SERIES + ":2": {
                            "skips": [[100.5, 140.0]], "moves": False},
                        OTHER_USER + ":" + SERIES + ":2": {"skips": [], "moves": True},
                        "bad": {"moves": True}}}, f)
            self.store = sr.SkipStore(os.path.join(d, sr.RULES_FILE))
            self.watcher = sr.SkipWatcher(self.store)
            self.store.migrate(ACCOUNT)
            self.assertIsNone(self.store.intro(self.key, 1))
            self.assertTrue(self.moves(self.ep(3)))
            self.assertEqual(self.store.intro(self.key, 2), (100.5, 140.0),
                             "another user's mark is not this one's")
            # season 2's kept skip is compared with the next one
            self.play(self.ep(12, season=2), 40, 40.5, 80)
            self.assertTrue(self.moves(self.ep(13, season=2)))


class MovesOutliveTheSpot(Base):
    """1.6.1, the 1.6 merge review's R2: 1.5.1 kept a season's skips and its
    "moves" mark apart from the learned range, and 1.6.0 put them inside the
    intro entry, so two undos or a seek back into the spot (which drop the
    entry) took the season's evidence with them, and the next spot learned
    started a new history; a 1.5.1 mark whose range was already gone was
    dropped at the migration. Now the season keeps its own record."""

    # Moves' helpers
    def setUp(self):
        super().setUp()
        self.watcher = sr.SkipWatcher(self.store)
        self.t = 0.0

    def play(self, video, *positions):
        for p in positions:
            self.t += 0.5
            self.watcher.sample(video, p, now=self.t)

    def ep(self, n, season=1, series=SERIES):
        return Vid(dict(episode(season=season), Id="%032d" % n,
                        SeriesId=series))

    def moves(self, video):
        entry = self.store.intro_entry(sr.show_key(video.item),
                                       video.item["ParentIndexNumber"])
        return bool(entry and entry.get("moves"))

    def offered(self, video):
        video.intros = []
        intro_sources.augment(video, make, self.store)
        return [(s.type, s.start, s.end, s.source) for s in video.intros]

    def undo_twice(self, video):
        """The Skip pill pressed on the learned spot, then a seek back into
        it, twice (the player's undo)."""
        self.play(video, 146, 146.5)
        for back in (160, 170):
            self.watcher.note_skip(now=self.t)
            self.play(video, 190, back)

    def test_two_undos_then_a_hand_skip_40_s_away_moves(self):
        self.play(self.ep(1), 150, 150.5, 190)       # learned 150.5-190
        second = self.ep(2)
        second.intros = []
        intro_sources.augment(second, make, self.store)
        self.undo_twice(second)
        self.assertIsNone(self.store.intro(self.key, 1), "the spot is gone")
        self.play(self.ep(4), 70, 70.5, 110)          # same part, 40 s earlier
        self.assertTrue(self.moves(self.ep(5)))
        self.assertEqual([], self.offered(self.ep(5)), "not learned again")

    def test_the_reviewers_probe_undo_twice_through_the_store(self):
        # moves_probe.py case B, as the reviewer ran it
        self.play(self.ep(1), 150, 150.5, 190)
        self.store.undo_intro(self.key, 1)
        self.store.undo_intro(self.key, 1)
        self.play(self.ep(4), 70, 70.5, 110)
        self.assertTrue(bool((self.store.intro_entry(self.key, 1) or {})
                             .get("moves")))
        self.assertIsNone(self.store.intro(self.key, 1))

    def test_a_seek_back_into_the_spot_keeps_the_seasons_skips(self):
        self.play(self.ep(1), 150, 150.5, 190)
        second = self.ep(2)
        second.intros = []
        intro_sources.augment(second, make, self.store)
        self.play(second, 185, 160)       # a seek back into it: forgotten
        self.assertIsNone(self.store.intro(self.key, 1))
        self.play(self.ep(3), 70, 70.5, 110)
        self.assertTrue(self.moves(self.ep(4)))

    def test_learned_again_at_the_same_spot_keeps_one_history(self):
        self.play(self.ep(1), 150, 150.5, 190)
        self.store.undo_intro(self.key, 1)
        self.store.undo_intro(self.key, 1)
        self.play(self.ep(2), 151, 151.5, 190)        # learned again, near
        self.assertEqual(self.store.intro(self.key, 1), (151.5, 190.0))
        self.assertEqual([[150.5, 190.0], [151.5, 190.0]],
                         self.store.intro_entry(self.key, 1)["skips"])
        self.assertFalse(self.moves(self.ep(3)))

    def test_a_151_mark_whose_range_was_gone_migrates(self):
        # moves_probe.py case A: 1.5.1 marked season 1 "moves", and a seek
        # back had already forgotten its range
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, sr.LEGACY_FILE), "w",
                      encoding="utf-8") as f:
                json.dump({"version": 1, "entries": {}, "seasons": {
                    ACCOUNT + ":" + SERIES + ":1": {"skips": [],
                                                    "moves": True},
                    ACCOUNT + ":" + SERIES + ":2": {
                        "skips": [[100.5, 140.0]], "moves": False}}}, f)
            self.store = sr.SkipStore(os.path.join(d, sr.RULES_FILE))
            self.watcher = sr.SkipWatcher(self.store)
            self.store.migrate(ACCOUNT)
            self.play(self.ep(1), 150, 150.5, 190)
            self.assertIsNone(self.store.intro(self.key, 1),
                              "a season that moves is not learned")
            self.assertTrue(self.moves(self.ep(2)))
            # season 2's kept skip (no range) is compared with the next
            self.play(self.ep(12, season=2), 40, 40.5, 80)
            self.assertTrue(self.moves(self.ep(13, season=2)))
            # Settings lists the season that moves, and Forget clears it
            listed = sr.listing(self.store, ACCOUNT)["shows"][0]
            self.assertEqual([(i["season"], i["moves"])
                              for i in listed["learnedIntros"]],
                             [(1, True), (2, True)])
            self.store.forget(ACCOUNT, rule_id="intro-%s-1" % SERIES)
            self.assertFalse(self.moves(self.ep(3)))
            self.play(self.ep(4), 150, 150.5, 190)
            self.assertEqual(self.store.intro(self.key, 1), (150.5, 190.0))

    def test_kept_on_disk_and_mirrored_for_a_160_backend(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, sr.RULES_FILE)
            self.store = sr.SkipStore(path)
            self.watcher = sr.SkipWatcher(self.store)
            self.play(self.ep(1), 150, 150.5, 190)
            self.play(self.ep(2), 70, 70.5, 110)
            with open(path, encoding="utf-8") as f:
                show = json.load(f)["shows"][self.key]
            self.assertTrue(show["seasons"]["1"]["moves"])
            self.assertTrue(show["intros"]["1"]["moves"],
                            "1.6.0 reads the mark in the entry")
            # a file 1.6.0 wrote (no "seasons"): its marks are read from the
            # entries
            del show["seasons"]
            with open(path, "w", encoding="utf-8") as f:
                json.dump({"version": 2, "shows": {self.key: show}}, f)
            reread = sr.SkipStore(path)
            self.assertIsNone(reread.intro(self.key, 1))
            self.assertTrue(reread.intro_entry(self.key, 1)["moves"])

    def test_a_season_never_learned_keeps_nothing(self):
        # a server's intro: nothing learned, so hand skips are not kept
        video = Vid(dict(episode(), Id="%032d" % 1),
                    [Seg("Intro", 10, 40, "server")])
        self.play(video, 150, 150.5, 190)
        self.assertIsNone(self.store.show(self.key))


if __name__ == "__main__":
    unittest.main()
