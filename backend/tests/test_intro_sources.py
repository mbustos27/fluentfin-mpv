"""fluentfin-mpv: chapter fallback, learned intros and their precedence
(`intro_sources`). Pure: no mpv, no network."""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import os
import tempfile
import unittest

from jellyfin_mpv_shim import conf, intro_sources as src
from jellyfin_mpv_shim.bridge_protocol import segment_list, validate_skip_options

T = 10_000_000


class Seg:
    def __init__(self, type, start, end, source="server"):
        self.type, self.start, self.end, self.source = type, start, end, source


class Vid:
    def __init__(self, item, intros=()):
        self.item, self.intros = item, list(intros)


def chapters(*pairs):
    return [{"Name": n, "StartPositionTicks": int(s * T)} for n, s in pairs]


def episode(chs=(), season=1, runtime=1400):
    return {"Chapters": list(chs), "RunTimeTicks": runtime * T,
            "SeriesId": "AB" * 16, "ParentIndexNumber": season}


class Quiet(unittest.TestCase):
    def setUp(self):
        self._saved = (src.OPTIONS.learn, src.OPTIONS.lookup_online,
                       conf.settings.segment_intro,
                       conf.settings.segment_outro,
                       conf.settings.segment_recap)
        src.OPTIONS.learn, src.OPTIONS.lookup_online = True, False
        conf.settings.segment_intro = "ask"
        conf.settings.segment_outro = "ask"
        conf.settings.segment_recap = "off"

    def tearDown(self):
        (src.OPTIONS.learn, src.OPTIONS.lookup_online,
         conf.settings.segment_intro, conf.settings.segment_outro,
         conf.settings.segment_recap) = self._saved


class ChapterNames(unittest.TestCase):
    def test_names(self):
        for name, kind in [("Intro", "Intro"), ("OPENING", "Intro"),
                           ("OP", "Intro"), ("Opening Credits", "Intro"),
                           ("OP1", "Intro"), ("Recap", "Recap"),
                           ("Previously", "Recap"), ("Credits", "Outro"),
                           ("Ending", "Outro"), ("ED", "Outro"),
                           ("End Credits", "Outro"), (" end-credits ", "Outro"),
                           ("Chapter 1", None), ("Operation", None),
                           ("Part A", None), (None, None)]:
            self.assertEqual(src.chapter_type(name), kind, name)

    def test_spans(self):
        item = episode(chapters(("Cold open", 0), ("Opening", 60),
                                ("Part A", 150), ("Credits", 1300)))
        self.assertEqual(src.chapter_segments(item),
                         [("Intro", 60.0, 150.0), ("Outro", 1300.0, 1400.0)])

    def test_runaway_intro_is_ignored(self):
        item = episode(chapters(("Intro", 0)), runtime=3000)
        self.assertEqual(src.chapter_segments(item), [])


class Augment(Quiet):
    def run_augment(self, item, intros=(), store=None):
        video = Vid(item, intros)
        src.augment(video, lambda t, s, e, so: Seg(t, s, e, so),
                    store or src.LearnedStore())
        return video.intros

    def test_chapters_fill_missing_types(self):
        out = self.run_augment(episode(chapters(("Opening", 60), ("A", 150),
                                                ("Credits", 1300))))
        self.assertEqual([(s.type, s.source) for s in out],
                         [("Intro", "chapters"), ("Outro", "chapters")])

    def test_server_wins_per_type(self):
        out = self.run_augment(
            episode(chapters(("Opening", 60), ("A", 150), ("Credits", 1300))),
            [Seg("Intro", 50, 140)])
        self.assertEqual([(s.type, s.source) for s in out],
                         [("Intro", "server"), ("Outro", "chapters")])

    def test_types_set_to_off_are_not_added(self):
        conf.settings.segment_outro = "off"
        out = self.run_augment(episode(chapters(("Credits", 1300))))
        self.assertEqual(out, [])

    def test_chapters_beat_learned(self):
        store = src.LearnedStore()
        store.learn(src.series_key(episode()), 60, 150)
        out = self.run_augment(episode(chapters(("Opening", 70), ("A", 160))),
                               store=store)
        self.assertEqual([s.source for s in out], ["chapters"])

    def test_learned_with_tolerance(self):
        store = src.LearnedStore()
        store.learn(src.series_key(episode()), 60, 150)
        out = self.run_augment(episode(), store=store)
        self.assertEqual([(s.type, s.start, s.end, s.source) for s in out],
                         [("Intro", 55.0, 150.0, "learned")])

    def test_learned_is_per_season(self):
        store = src.LearnedStore()
        store.learn(src.series_key(episode(season=1)), 60, 150)
        self.assertEqual(self.run_augment(episode(season=2), store=store), [])

    def test_learning_off_offers_nothing(self):
        src.OPTIONS.learn = False
        store = src.LearnedStore()
        store.learn(src.series_key(episode()), 60, 150)
        self.assertEqual(self.run_augment(episode(), store=store), [])


class Learner(Quiet):
    def setUp(self):
        super().setUp()
        self.store = src.LearnedStore()
        self.learner = src.SkipLearner(self.store)
        self.item = episode()
        self.key = src.series_key(self.item)

    def play(self, video, *positions, start=0.0):
        t = start
        for p in positions:
            t += 0.5
            self.learner.sample(video, p, now=t)

    def test_forward_jump_is_learned(self):
        self.play(Vid(self.item), 60, 60.5, 150)
        self.assertEqual(self.store.get(self.key), (60.5, 150.0))

    def test_jumps_outside_the_rule_are_not(self):
        for before, after in [(60, 85), (60, 215), (700, 790)]:
            self.play(Vid(self.item), before, after)
            self.assertIsNone(self.store.get(self.key), (before, after))

    def test_not_learned_when_an_intro_is_known(self):
        self.play(Vid(self.item, [Seg("Intro", 10, 20, "chapters")]), 60, 150)
        self.assertIsNone(self.store.get(self.key))

    def test_skipped_recap_or_preview_is_not_an_intro(self):
        for kind in ("Recap", "Preview", "Outro"):
            self.play(Vid(self.item, [Seg(kind, 30, 100, "chapters")]),
                      40, 100.5)
            self.assertIsNone(self.store.get(self.key), kind)

    def test_jump_from_skip_segment_is_not_learned(self):
        self.learner.note_skip(now=0.0)
        self.play(Vid(self.item), 60, 150)
        self.assertIsNone(self.store.get(self.key))

    def test_not_learned_when_off(self):
        src.OPTIONS.learn = False
        self.play(Vid(self.item), 60, 150)
        self.assertIsNone(self.store.get(self.key))

    def test_movie_is_not_learned(self):
        self.play(Vid({"RunTimeTicks": 5 * T}), 60, 150)
        self.assertEqual(self.store._entries, {})

    def learned_video(self):
        self.store.learn(self.key, 60, 150)
        return Vid(self.item, [Seg("Intro", 55, 150, "learned")])

    def test_seek_back_into_it_forgets(self):
        video = self.learned_video()
        self.play(video, 100, 80)
        self.assertIsNone(self.store.get(self.key))

    def test_skip_then_undo_twice_forgets(self):
        video = self.learned_video()
        self.play(video, 60, 150, 70)        # skipped, taken back
        self.assertEqual(self.store.get(self.key), (60.0, 150.0))
        self.play(video, 150, 70)            # sample 150 again: back again?
        self.assertIsNone(self.store.get(self.key))

    def test_skip_alone_keeps_it(self):
        video = self.learned_video()
        self.play(video, 60, 150, 151, 152)
        self.assertEqual(self.store.get(self.key), (60.0, 150.0))


class Client:
    """The apiclient's config, as far as `show_key` reads it."""

    def __init__(self, user):
        self.config = type("Config", (), {"data": {"auth.user_id": user}})()


class Moves(Quiet):
    """1.5.1 fixes (It's Always Sunny's "Skip intro" at 0:00): skips by hand
    of one part 10 s or more apart in one season mark that season "moves",
    and a season that moves is no longer offered a learned spot. Per server,
    user, show and season (the review, BH-151-M1: a show whose intro sits
    elsewhere in season 2 keeps its learned spot in both seasons); forgotten
    with the learned intros."""

    def setUp(self):
        super().setUp()
        self.store = src.LearnedStore()
        self.learner = src.SkipLearner(self.store)
        self.t = 0.0

    def ep(self, n, season=1, series="AB" * 16, server="srv1", user="u1"):
        item = dict(episode(season=season), Id="%032d" % n, SeriesId=series,
                    ServerId=server)
        video = Vid(item)
        video.client = Client(user)
        return video

    def play(self, video, *positions):
        for p in positions:
            self.t += 0.5
            self.learner.sample(video, p, now=self.t)

    def offered(self, video):
        video.intros = []
        src.augment(video, lambda t, s, e, so: Seg(t, s, e, so), self.store)
        return [(s.type, s.start, s.end, s.source) for s in video.intros]

    def test_sunny_the_intro_moves_within_a_season_and_its_spot_goes(self):
        first = self.ep(1)
        self.play(first, 150, 150.5, 190)                 # S1E1: the intro after a long cold open
        second = self.ep(2)
        self.assertEqual([("Intro", 145.5, 190.0, "learned")], self.offered(second))
        self.play(second, 70, 70.5, 110)                  # S1E2: the same length, 40 s before the spot offered
        self.assertTrue(self.store.moves(src.moves_key(second)))
        self.assertEqual([], self.offered(self.ep(3)))    # no fixed spot in season 1 any more
        self.assertIsNotNone(self.store.get(src.series_key(second.item)))   # kept, just not offered
        # season 2 is judged on its own
        self.play(self.ep(10, season=2), 100, 100.5, 140)
        self.assertEqual([("Intro", 95.5, 140.0, "learned")], self.offered(self.ep(11, season=2)))

    def test_a_different_spot_in_each_season_keeps_both(self):
        # the hunt's case: season 1 at the start, season 2 after a cold open added that season
        self.play(self.ep(1), 0, 0.5, 50)
        self.play(self.ep(2), 1, 1.5, 51)
        self.play(self.ep(10, season=2), 120, 120.5, 170)
        self.play(self.ep(11, season=2), 121, 121.5, 171)
        self.assertFalse(self.store.moves(src.moves_key(self.ep(3))))
        self.assertFalse(self.store.moves(src.moves_key(self.ep(12, season=2))))
        self.assertEqual([("Intro", 0.0, 51.0, "learned")], self.offered(self.ep(3)))
        self.assertEqual([("Intro", 116.5, 171.0, "learned")], self.offered(self.ep(12, season=2)))

    def test_a_recap_skipped_in_one_episode_is_not_the_intro_of_the_next(self):
        self.play(self.ep(1), 2, 2.5, 40)                 # E1's first hand skip: the recap (37.5 s)
        self.play(self.ep(2), 55, 55.5, 110)              # E2's: the intro (54.5 s), another part
        self.assertFalse(self.store.moves(src.moves_key(self.ep(3))))
        self.assertEqual("learned", self.offered(self.ep(3))[0][3])

    def test_skips_within_10_s_keep_the_offer(self):
        self.play(self.ep(1), 60, 60.5, 100)
        self.play(self.ep(2), 80, 80.5, 120)              # overlapping
        self.play(self.ep(3), 109, 109.5, 150)            # 9.5 s after the first ended: not 10
        self.assertFalse(self.store.moves(src.moves_key(self.ep(4))))
        self.assertEqual("learned", self.offered(self.ep(4))[0][3])

    def test_only_an_episodes_first_hand_skip_counts(self):
        video = self.ep(1)
        self.play(video, 0, 0.5, 40, 150, 150.5, 190)     # the cold open, then the intro, in one episode
        self.assertFalse(self.store.moves(src.moves_key(video)))

    def test_a_skip_pill_press_is_not_by_hand(self):
        self.play(self.ep(1), 150, 150.5, 190)
        video = self.ep(2)
        self.learner.note_skip(now=self.t)
        self.play(video, 0, 0.5, 40)                      # the pill's own jump
        self.assertFalse(self.store.moves(src.moves_key(video)))

    def test_learning_off_notes_nothing(self):
        src.OPTIONS.learn = False
        self.play(self.ep(1), 150, 150.5, 190)
        self.play(self.ep(2), 0, 0.5, 40)
        self.assertEqual({}, self.store._seasons)

    def test_kept_per_server_user_show_and_season(self):
        self.play(self.ep(1), 150, 150.5, 190)
        self.play(self.ep(2), 70, 70.5, 110)
        self.assertTrue(self.store.moves(src.moves_key(self.ep(3))))
        self.assertFalse(self.store.moves(src.moves_key(self.ep(3, server="srv2"))))
        self.assertFalse(self.store.moves(src.moves_key(self.ep(3, user="u2"))))
        self.assertFalse(self.store.moves(src.moves_key(self.ep(3, series="CD" * 16))))
        self.assertFalse(self.store.moves(src.moves_key(self.ep(3, season=2))))
        self.assertEqual("srv1:u1:" + "ab" * 16 + ":1", src.moves_key(self.ep(3)))
        self.assertIsNone(src.moves_key(Vid({"RunTimeTicks": 5 * T})))   # a film

    def test_forget_learned_intros_clears_it(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, src.LEARNED_FILE)
            self.store = src.LearnedStore(path)
            self.learner = src.SkipLearner(self.store)
            self.play(self.ep(1), 150, 150.5, 190)
            self.play(self.ep(2), 70, 70.5, 110)
            season = src.moves_key(self.ep(3))
            self.assertTrue(src.LearnedStore(path).moves(season))    # kept in the file
            os.remove(path)                                           # what the UI's Forget does
            self.assertFalse(self.store.moves(season))
            self.play(self.ep(4), 150, 150.5, 190)
            self.assertFalse(self.store.moves(season))
            self.store.clear()
            self.assertEqual({}, self.store._seasons)

    def test_a_file_from_150_reads_and_keeps_its_ranges(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, src.LEARNED_FILE)
            with open(path, "w", encoding="utf-8") as f:
                f.write('{"version": 1, "entries": {"s:1": {"start": 60, "end": 150, "undone": 0}}}')
            store = src.LearnedStore(path)
            self.assertEqual((60.0, 150.0), store.get("s:1"))
            self.assertFalse(store.moves("srv1:u1:s:1"))
            store.note_skip("srv1:u1:s:1", 60, 150)
            self.assertEqual((60.0, 150.0), src.LearnedStore(path).get("s:1"))


class Store(unittest.TestCase):
    def test_file_roundtrip_and_forget(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, src.LEARNED_FILE)
            a = src.LearnedStore(path)
            a.learn("s:1", 60, 150)
            self.assertEqual(src.LearnedStore(path).get("s:1"), (60.0, 150.0))
            os.remove(path)               # what the UI's Forget does
            self.assertIsNone(a.get("s:1"))

    def test_corrupt_file_is_ignored(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, src.LEARNED_FILE)
            with open(path, "w", encoding="utf-8") as f:
                f.write("{nope")
            self.assertIsNone(src.LearnedStore(path).get("s:1"))


class Protocol(unittest.TestCase):
    def test_source_in_segment_list(self):
        out = segment_list([("Intro", 1, 2, "ask", "chapters"),
                            ("Outro", 3, 4, "ask"),
                            ("Recap", 5, 6, "ask", "bogus")])
        self.assertEqual([s["source"] for s in out],
                         ["chapters", "server", "server"])

    def test_skip_options(self):
        self.assertEqual(validate_skip_options({}),
                         {"onlineLookup": None, "learnIntros": None})
        self.assertEqual(validate_skip_options({"onlineLookup": True,
                                                "learnIntros": False}),
                         {"onlineLookup": True, "learnIntros": False})
        with self.assertRaises(Exception):
            validate_skip_options({"learnIntros": "yes"})


if __name__ == "__main__":
    unittest.main()
