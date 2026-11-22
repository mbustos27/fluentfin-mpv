"""fluentfin-mpv: chapter fallback, learned intros and their precedence
(`intro_sources`). Pure: no mpv, no network. 1.6 skip: the learner and its
store moved to `skip_rules` (tests/test_skip_rules.py)."""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import unittest

from jellyfin_mpv_shim import conf, intro_sources as src
from jellyfin_mpv_shim import skip_rules
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


ACCOUNT = "s:" + "01" * 16


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
        self._account = skip_rules.current_account()
        skip_rules.set_account(ACCOUNT)

    def tearDown(self):
        (src.OPTIONS.learn, src.OPTIONS.lookup_online,
         conf.settings.segment_intro, conf.settings.segment_outro,
         conf.settings.segment_recap) = self._saved
        skip_rules.set_account(self._account)


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
                    store or skip_rules.SkipStore())
        return video.intros

    @staticmethod
    def learned(item, start, end):
        store = skip_rules.SkipStore()
        store.learn_intro(skip_rules.show_key(item),
                          item["ParentIndexNumber"], start, end)
        return store

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
        store = self.learned(episode(), 60, 150)
        out = self.run_augment(episode(chapters(("Opening", 70), ("A", 160))),
                               store=store)
        self.assertEqual([s.source for s in out], ["chapters"])

    def test_learned_with_tolerance(self):
        store = self.learned(episode(), 60, 150)
        out = self.run_augment(episode(), store=store)
        self.assertEqual([(s.type, s.start, s.end, s.source) for s in out],
                         [("Intro", 55.0, 150.0, "learned")])

    def test_learned_is_per_season(self):
        store = self.learned(episode(season=1), 60, 150)
        self.assertEqual(self.run_augment(episode(season=2), store=store), [])

    def test_learning_off_offers_nothing(self):
        src.OPTIONS.learn = False
        store = self.learned(episode(), 60, 150)
        self.assertEqual(self.run_augment(episode(), store=store), [])


class Protocol(unittest.TestCase):
    def test_source_in_segment_list(self):
        out = segment_list([("Intro", 1, 2, "ask", "chapters"),
                            ("Outro", 3, 4, "ask"),
                            ("Recap", 5, 6, "ask", "bogus")])
        self.assertEqual([s["source"] for s in out],
                         ["chapters", "server", "server"])

    def test_skip_options(self):
        none = {"segmentActions": None, "ruleAction": None}
        self.assertEqual(validate_skip_options({}),
                         dict(none, onlineLookup=None, learnIntros=None))
        self.assertEqual(validate_skip_options({"onlineLookup": True,
                                                "learnIntros": False}),
                         dict(none, onlineLookup=True, learnIntros=False))
        with self.assertRaises(Exception):
            validate_skip_options({"learnIntros": "yes"})
        # 1.6 skip: learnSkips is the same switch, and wins
        self.assertFalse(validate_skip_options(
            {"learnIntros": True, "learnSkips": False})["learnIntros"])
        self.assertEqual(validate_skip_options(
            {"segmentActions": {"Commercial": "always", "Preview": "off"},
             "ruleAction": "always"}),
            {"onlineLookup": None, "learnIntros": None,
             "segmentActions": {"Commercial": "always", "Preview": "off"},
             "ruleAction": "always"})
        for bad in ({"learnSkips": 1}, {"segmentActions": []},
                    {"segmentActions": {"Ads": "ask"}},
                    {"segmentActions": {"Intro": "maybe"}},
                    {"ruleAction": "off"}):
            with self.assertRaises(Exception, msg=bad):
                validate_skip_options(bad)


if __name__ == "__main__":
    unittest.main()
