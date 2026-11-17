"""fluentfin-mpv: chapter fallback, learned intros and their precedence
(`intro_sources`). Pure: no mpv, no network."""

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
            with open(path, "w") as f:
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
