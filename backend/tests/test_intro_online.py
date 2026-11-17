"""fluentfin-mpv: online intro lookups (`intro_online`). No network: every
request goes to a recorded fixture through an injected fetcher. The rules
under test come from TheIntroDB's terms (docs/research/intro-sources.md):
one episode at a time, memory-only cache, honour 429 / Retry-After, a plain
User-Agent, a minimal request."""

import json
import os
import re
import unittest
import urllib.parse

from jellyfin_mpv_shim import conf, intro_online as net, intro_sources as src

T = 10_000_000
TOKEN = "s3cret-token"
SERVER = "https://jf.example.invalid:8920"

# Shapes from the services' OpenAPI documents.
INTRODB_200 = {
    "tvdb_id": 71663, "type": "tv", "season": 1, "episode": 3,
    "intro": [{"start_ms": None, "end_ms": 23000}],
    "recap": [{"start_ms": 0, "end_ms": 0}],
    "credits": [{"start_ms": 1300000, "end_ms": None}],
    "preview": [{"start_ms": 1390000, "end_ms": 1400000}],
}
ANISKIP_200 = {"statusCode": 200, "message": "ok", "found": True, "results": [
    {"interval": {"startTime": 12.5, "endTime": 101.9}, "skipType": "op",
     "skipId": "x", "episodeLength": 1400.0},
    {"interval": {"startTime": 1300.0, "endTime": 1400.0},
     "skipType": "ed", "skipId": "y", "episodeLength": 1400.0}]}

EPISODE = {"Type": "Episode", "SeriesId": "ab" * 16, "ParentIndexNumber": 1,
           "IndexNumber": 3, "RunTimeTicks": 1400 * T,
           "Path": "/media/secret/show.mkv", "Name": "A Title",
           "ProviderIds": {"Tvdb": "999"}}
SHOW = {"ids": {"Tvdb": "71663", "Tmdb": "1396"}, "genres": ["Drama"]}


class Fake:
    def __init__(self, routes):
        self.routes, self.calls = routes, []

    def __call__(self, url, headers, timeout):
        self.calls.append((url, dict(headers), timeout))
        for pattern, answer in self.routes:
            if re.search(pattern, url):
                return answer if isinstance(answer, tuple) else (
                    200, {}, json.dumps(answer).encode())
        return 404, {}, b"{}"


class Online(unittest.TestCase):
    def setUp(self):
        net.reset()

    def test_request_is_minimal(self):
        fake = Fake([("theintrodb", INTRODB_200)])
        net.lookup(EPISODE, SHOW, "1.2", fake)
        url, headers, timeout = fake.calls[0]
        parts = urllib.parse.urlsplit(url)
        self.assertEqual(parts.netloc, "api.theintrodb.org")
        self.assertEqual(parts.path, "/v4/media")
        self.assertEqual(dict(urllib.parse.parse_qsl(parts.query)),
                         {"tvdb_id": "71663", "season": "1", "episode": "3",
                          "duration_ms": "1400000"})
        self.assertEqual(headers["User-Agent"], "Seaglass/1.2")
        self.assertLessEqual(timeout, 3.0)
        everything = url + json.dumps(headers)
        for private in (TOKEN, SERVER, "secret", "A Title", "show.mkv",
                        "Authorization"):
            self.assertNotIn(private, everything)

    def test_show_id_order(self):
        self.assertEqual(net.introdb_query({"Imdb": "tt1", "Tmdb": "5"}, 1, 2,
                                           None), "tmdb_id=5&season=1&episode=2")
        self.assertIsNone(net.introdb_query({}, 1, 2, 5))

    def test_movie_sends_no_season(self):
        fake = Fake([("theintrodb", {"intro": [{"start_ms": 1000,
                                               "end_ms": 9000}]})])
        out = net.lookup({"Type": "Movie", "RunTimeTicks": 100 * T},
                         {"ids": {"Tmdb": "603"}}, "1", fake)
        self.assertNotIn("season", fake.calls[0][0])
        self.assertEqual(out, [("Intro", 1.0, 9.0)])

    def test_parse(self):
        out = net.lookup(EPISODE, SHOW, "1", Fake([("theintrodb",
                                                    INTRODB_200)]))
        self.assertEqual(out, [("Intro", 0.0, 23.0),
                               ("Outro", 1300.0, 1400.0),
                               ("Preview", 1390.0, 1400.0)])

    def test_one_request_per_episode_and_memory_only_cache(self):
        fake = Fake([("theintrodb", INTRODB_200)])
        net.lookup(EPISODE, SHOW, "1", fake)
        net.lookup(EPISODE, SHOW, "1", fake)
        self.assertEqual(len(fake.calls), 1)
        # TheIntroDB terms 4.3: nothing is written to disk. The module has
        # no file API at all.
        with open(net.__file__, encoding="utf-8") as handle:
            text = handle.read()
        code = re.sub(r'""".*?"""', "", text, flags=re.S)
        self.assertIsNone(re.search(r"(?<![\w.])open\(", code))
        for forbidden in ("os.replace", "json.dump(", "sqlite",
                          "pickle"):
            self.assertNotIn(forbidden, code)

    def test_never_a_season_or_next_episode(self):
        fake = Fake([("theintrodb", INTRODB_200)])
        net.lookup(EPISODE, SHOW, "1", fake)
        self.assertEqual(len(fake.calls), 1)
        self.assertIn("episode=3", fake.calls[0][0])

    def test_429_pauses_the_source(self):
        fake = Fake([("theintrodb", (429, {"Retry-After": "120"}, b"{}"))])
        clock = iter([100.0, 101.0, 300.0]).__next__
        self.assertEqual(net.lookup(EPISODE, SHOW, "1", fake, clock=clock), [])
        self.assertEqual(net.lookup(EPISODE, SHOW, "1", fake, clock=clock), [])
        self.assertEqual(len(fake.calls), 1)          # paused: not asked
        net.lookup(EPISODE, SHOW, "1", fake, clock=clock)
        self.assertEqual(len(fake.calls), 2)          # asked again after 120 s

    def test_failures_are_quiet(self):
        def boom(url, headers, timeout):
            raise TimeoutError("slow")
        self.assertEqual(net.lookup(EPISODE, SHOW, "1", boom), [])
        self.assertEqual(net.lookup(EPISODE, SHOW, "1",
                                    Fake([("theintrodb",
                                           (200, {}, b"not json"))])), [])

    def test_a_404_is_remembered_but_a_500_is_not(self):
        fake = Fake([("theintrodb", (404, {}, b"{}"))])
        net.lookup(EPISODE, SHOW, "1", fake)
        net.lookup(EPISODE, SHOW, "1", fake)
        self.assertEqual(len(fake.calls), 1)
        net.reset()
        bad = Fake([("theintrodb", (503, {}, b""))])
        net.lookup(EPISODE, SHOW, "1", bad)
        net.lookup(EPISODE, SHOW, "1", bad)
        self.assertEqual(len(bad.calls), 2)

    def test_aniskip_only_for_anime_and_only_for_what_is_missing(self):
        routes = [("theintrodb", (404, {}, b"{}")),
                  ("aniskip", ANISKIP_200),
                  ("arm.haglund.dev/api/v2/thetvdb",
                   [{"myanimelist": 1, "thetvdb-season": 1},
                    {"myanimelist": 2, "thetvdb-season": 2}])]
        fake = Fake(routes)
        self.assertEqual(net.lookup(EPISODE, SHOW, "1", fake),
                         [])                              # not anime
        self.assertFalse([c for c in fake.calls if "aniskip" in c[0]])
        anime = {"ids": {"Tvdb": "71663"}, "genres": ["Anime"]}
        out = net.lookup(EPISODE, anime, "1", fake)
        self.assertEqual(out, [("Intro", 12.5, 101.9),
                               ("Outro", 1300.0, 1400.0)])
        url = [c[0] for c in fake.calls if "aniskip" in c[0]][0]
        self.assertIn("/v2/skip-times/1/3?", url)         # season 1 entry
        self.assertIn("episodeLength=1400.000", url)
        for c in fake.calls:
            self.assertNotIn("secret", c[0] + json.dumps(c[1]))

    def test_aniskip_uses_a_mal_id_from_the_server_directly(self):
        fake = Fake([("theintrodb", (404, {}, b"{}")), ("aniskip",
                                                        ANISKIP_200)])
        out = net.lookup(EPISODE, {"ids": {"MyAnimeList": "5114"},
                                   "genres": []}, "1", fake)
        self.assertEqual(len(out), 2)
        self.assertFalse([c for c in fake.calls if "haglund" in c[0]])


class Vid:
    def __init__(self, item):
        self.item, self.intros = item, []


class Seg:
    def __init__(self, type, start, end, source="server"):
        self.type, self.start, self.end, self.source = type, start, end, source


class Precedence(unittest.TestCase):
    def test_online_replaces_learned_and_yields_to_chapters(self):
        v = Vid({})
        v.intros = [Seg("Intro", 50, 100, "learned")]
        self.assertTrue(src.apply_online(v, Seg, "Intro", 0, 20))
        self.assertEqual([(s.source) for s in v.intros], ["online"])
        v.intros = [Seg("Intro", 50, 100, "chapters")]
        self.assertFalse(src.apply_online(v, Seg, "Intro", 0, 20))
        self.assertEqual([(s.source) for s in v.intros], ["chapters"])

    def test_lookup_runs_off_thread_and_is_applied_when_it_arrives(self):
        v = Vid({})
        got = src.start_online(v, Seg, {"Intro"}, wait=True,
                               lookup=lambda item, missing: [
                                   ("Intro", 0.0, 23.0),
                                   ("Outro", 1.0, 2.0)])
        self.assertEqual([(s.type, s.source) for s in v.intros],
                         [("Intro", "online")])    # only what was missing


if __name__ == "__main__":
    unittest.main()
