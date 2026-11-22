"""Nixie 1.7 (stream T): the strict fakes of Trakt (outside_trakt.py) and TMDb trending (outside_tmdb_trending.py), checked
on their own with a stand-in for the fake server's request handler, with the fake library's titles in their lists, and
through outside.py's routing hook. Standard library only:

    python -B -m unittest discover -s tools/ui-harness/fakeserver -p "test_trending_fakes.py"
"""

import json
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import library  # noqa: E402
import smartdata  # noqa: E402
import outside  # noqa: E402
import outside_tmdb_trending as tmdb_trending  # noqa: E402
import outside_trakt as trakt  # noqa: E402

UA = "Nixie/1.7.0 (https://github.com/mbustos27/fluentfin-mpv)"


class Handler:
    """What the fakes use of the fake server's handler: headers, the query (keys lower-cased), _send, _json, _body."""

    def __init__(self, headers=None, query=None, body=None):
        self.headers = {k: v for k, v in (headers or {}).items()}
        self.q = {k.lower(): [str(v)] for k, v in (query or {}).items()}
        self.route = None
        self.body = body or {}
        self.status = self.data = self.ctype = None
        self.sent_headers = {}

    def _send(self, code, body=b"", ctype=None, headers=None):
        self.status, self.data, self.ctype, self.sent_headers = code, body, ctype, dict(headers or {})

    def _json(self, code, obj):
        self._send(code, json.dumps(obj).encode() if obj is not None else b"", "application/json")

    def _body(self):
        return self.body

    def json(self):
        return json.loads(self.data)


class CaseInsensitive(dict):
    def get(self, key, default=None):
        for k, v in self.items():
            if k.lower() == key.lower():
                return v
        return default


def lib():
    return library.Library(library.norm_id("ab" * 16), library.norm_id("cd" * 16), 10_000_000)


def trakt_headers(**extra):
    h = CaseInsensitive({"User-Agent": UA, "trakt-api-key": trakt.HARNESS_CLIENT_ID, "trakt-api-version": "2",
                         "Content-Type": "application/json"})
    for k, v in extra.items():
        key = k.replace("_", "-")
        h = CaseInsensitive({kk: vv for kk, vv in h.items() if kk.lower() != key.lower()})
        if v is not None:
            h[key] = v
    return h


class TraktFakeTests(unittest.TestCase):
    def setUp(self):
        self.lib = lib()
        self.fake = trakt.FakeTrakt(self.lib)

    def ask(self, path, query=None, **headers):
        h = Handler(trakt_headers(**headers), query)
        self.fake.api(h, "GET", path)
        return h

    def test_without_a_user_agent_or_the_harness_key_it_refuses_with_403(self):
        self.assertEqual(403, self.ask("/movies/trending", User_Agent=None).status)
        self.assertEqual(403, self.ask("/movies/trending", trakt_api_key=None).status)
        self.assertEqual(403, self.ask("/movies/trending", trakt_api_key="someone-elses").status)
        self.assertEqual([True, False, False], [r["key_ok"] for r in self.fake.state()["requests"]])
        self.assertNotIn("someone-elses", json.dumps(self.fake.state()))
        self.assertEqual(200, self.ask("/movies/trending").status)

    def test_limit_is_clamped_at_250_and_the_default_is_10(self):
        h = self.ask("/movies/popular", {"page": 1, "limit": 1000})
        self.assertEqual(250, len(h.json()))
        self.assertEqual("250", h.sent_headers["X-Pagination-Limit"])
        self.assertEqual(10, len(self.ask("/movies/popular").json()))

    def test_honest_counts_name_the_last_page_and_a_page_past_it_is_empty(self):
        h = self.ask("/movies/popular", {"page": 1, "limit": 250})
        self.assertEqual("2", h.sent_headers["X-Pagination-Page-Count"])
        self.assertEqual("486", h.sent_headers["X-Pagination-Item-Count"])
        self.assertEqual(236, len(self.ask("/movies/popular", {"page": 2, "limit": 250}).json()))
        past = self.ask("/movies/popular", {"page": 3, "limit": 250})
        self.assertEqual([], past.json())
        self.assertEqual(("0", "0"), (past.sent_headers["X-Pagination-Page-Count"], past.sent_headers["X-Pagination-Item-Count"]))

    def test_placeholder_counts_say_one_page_although_there_are_more(self):
        self.fake.configure({"counts": "placeholder"})
        h = self.ask("/movies/popular", {"page": 1, "limit": 250})
        self.assertEqual("1", h.sent_headers["X-Pagination-Page-Count"])
        self.assertEqual(236, len(self.ask("/movies/popular", {"page": 2, "limit": 250}).json()))
        self.fake.configure({"counts": "inflated"})
        self.assertEqual("99", self.ask("/movies/trending", {"page": 1, "limit": 250}).sent_headers["X-Pagination-Page-Count"])

    def test_a_bare_429_has_no_retry_after_and_no_body(self):
        self.fake.configure({"mode": "429"})
        h = self.ask("/shows/trending", {"page": 1, "limit": 250})
        self.assertEqual(429, h.status)
        self.assertEqual(b"", h.data)
        self.assertEqual({}, h.sent_headers)

    def test_the_box_office_is_ten_films_without_paging_headers(self):
        h = self.ask("/movies/boxoffice")
        self.assertEqual(10, len(h.json()))
        self.assertTrue(all("revenue" in e and "movie" in e for e in h.json()))
        self.assertFalse(any(k.startswith("X-Pagination") for k in h.sent_headers))

    def test_the_lists_wrap_titles_as_trakt_does_and_hold_library_titles(self):
        trending = self.ask("/movies/trending", {"page": 1, "limit": 250}).json()
        self.assertTrue(all("watchers" in e and "movie" in e for e in trending))
        self.assertEqual(240, len(trending))
        popular = self.ask("/shows/popular", {"page": 1, "limit": 250}).json()
        self.assertTrue(all("ids" in e and "tvdb" in e["ids"] for e in popular))
        anticipated = self.ask("/shows/anticipated", {"page": 1, "limit": 250}).json()
        self.assertTrue(all("list_count" in e and "show" in e for e in anticipated))
        films = {smartdata.provider_ids(self.lib, i)["Tmdb"] for i in self.lib.items.values() if i.get("kind") == "movie"}
        listed = {str(e["movie"]["ids"]["tmdb"]) for e in trending}
        self.assertGreater(len(films & listed), 5)
        self.assertGreater(len(listed - films), 50)

    def test_what_it_records_names_the_headers_but_never_the_key(self):
        self.ask("/movies/trending", {"page": 1, "limit": 250})
        r = self.fake.state()["requests"][-1]
        self.assertEqual(("2", "application/json", True, UA), (r["api_version"], r["content_type"], r["key_ok"], r["ua"]))
        self.assertNotIn(trakt.HARNESS_CLIENT_ID, json.dumps(self.fake.state()))


class TmdbFakeTests(unittest.TestCase):
    def setUp(self):
        self.lib = lib()
        self.fake = tmdb_trending.FakeTmdbTrending(self.lib)

    def ask(self, path, query=None, auth="Bearer " + tmdb_trending.HARNESS_TOKEN, accept="application/json"):
        headers = CaseInsensitive({"User-Agent": UA, "Accept": accept})
        if auth is not None:
            headers["Authorization"] = auth
        h = Handler(headers, query)
        self.fake.api(h, "GET", path)
        return h

    def test_only_the_bearer_token_works_and_an_api_key_in_the_address_is_refused(self):
        self.assertEqual(401, self.ask("/3/trending/movie/week", auth=None).status)
        self.assertEqual(7, self.ask("/3/trending/movie/week", auth="Bearer nope").json()["status_code"])
        self.assertEqual(401, self.ask("/3/trending/movie/week", {"api_key": tmdb_trending.HARNESS_TOKEN}).status)
        self.assertEqual(1, self.fake.state()["api_key_in_url"])
        self.assertNotIn(tmdb_trending.HARNESS_TOKEN, json.dumps(self.fake.state()))
        self.assertEqual(200, self.ask("/3/trending/movie/week").status)

    def test_pages_hold_20_and_outside_1_to_500_are_refused(self):
        answer = self.ask("/3/trending/tv/week", {"page": 2}).json()
        self.assertEqual((2, 20, 500), (answer["page"], len(answer["results"]), answer["total_pages"]))
        self.assertTrue(all(r["media_type"] == "tv" and "name" in r for r in answer["results"]))
        self.assertEqual(22, self.ask("/3/trending/tv/week", {"page": 501}).json()["status_code"])
        self.assertEqual(400, self.ask("/3/trending/tv/week", {"page": 0}).status)

    def test_a_429_carries_tmdbs_code_and_retry_after(self):
        self.fake.configure({"mode": "429"})
        h = self.ask("/3/trending/movie/week")
        self.assertEqual((429, "10", 25), (h.status, h.sent_headers["Retry-After"], h.json()["status_code"]))

    def test_images_are_webp_when_accept_allows_and_are_counted(self):
        self.assertEqual("image/webp", self.ask("/t/p/w500/p1.jpg", accept="image/webp,image/*").ctype)
        self.assertEqual("image/jpeg", self.ask("/t/p/w500/p1.jpg", accept="image/jpeg").ctype)
        self.assertEqual(2, self.fake.state()["image_requests"])

    def test_the_lists_hold_library_titles_among_others(self):
        films = {smartdata.provider_ids(self.lib, i)["Tmdb"] for i in self.lib.items.values() if i.get("kind") == "movie"}
        listed = set()
        for page in range(1, 6):
            listed |= {str(r["id"]) for r in self.ask("/3/trending/movie/week", {"page": page}).json()["results"]}
        self.assertGreater(len(films & listed), 5)
        self.assertGreater(len(listed - films), 30)


class RoutedHandler(Handler):
    """The handler as outside.py sees it: the raw path with its query, the body bytes."""

    def __init__(self, path, headers):
        super().__init__(headers)
        self.path = path
        self.body_bytes = b""


class OutsideRoutingTests(unittest.TestCase):
    def setUp(self):
        self.outside = outside.FakeOutside(lib())

    def route(self, url, headers):
        h = RoutedHandler(url, headers)
        path = url.split("?", 1)[0]
        self.assertTrue(self.outside.handle(h, "GET", path))
        return h

    def test_trakt_and_tmdb_are_reached_through_the_outside_prefix(self):
        h = self.route("/__harness/outside/api.trakt.tv/movies/trending?page=1&limit=250", trakt_headers())
        self.assertEqual(200, h.status)
        self.assertEqual("240", h.sent_headers["X-Pagination-Item-Count"])
        self.assertEqual(240, len(h.json()))
        t = self.route("/__harness/outside/api.themoviedb.org/3/trending/tv/week?page=1",
                       CaseInsensitive({"User-Agent": UA, "Authorization": "Bearer " + tmdb_trending.HARNESS_TOKEN}))
        self.assertEqual(200, t.status)
        self.assertEqual(20, len(t.json()["results"]))
        # what outside.py recorded for the privacy check: the host, the path, the query
        hosts = [(r["host"], r["path"], r["query"]) for r in self.outside.requests]
        self.assertEqual([("api.trakt.tv", "/movies/trending", "page=1&limit=250"), ("api.themoviedb.org", "/3/trending/tv/week", "page=1")], hosts)

    def test_the_refusals_come_through_the_hook_too(self):
        self.assertEqual(403, self.route("/__harness/outside/api.trakt.tv/movies/trending?page=1", trakt_headers(User_Agent=None)).status)
        self.assertEqual(401, self.route("/__harness/outside/api.themoviedb.org/3/trending/movie/week?page=1", CaseInsensitive({"User-Agent": UA})).status)
        img = self.route("/__harness/outside/image.tmdb.org/t/p/w500/p1.jpg", CaseInsensitive({"User-Agent": UA, "Accept": "image/webp"}))
        self.assertEqual("image/webp", img.ctype)
        self.assertEqual(1, self.outside.config()["image.tmdb.org"]["image_requests"])

    def test_config_sets_the_fakes_by_host(self):
        h = RoutedHandler("/__harness/outside/config", {})
        h.body = {"api.trakt.tv": {"mode": "429"}, "api.themoviedb.org": {"mode": "429"}}
        self.assertTrue(self.outside.handle(h, "POST", "/__harness/outside/config"))
        self.assertEqual("429", h.json()["api.trakt.tv"]["mode"])
        self.assertEqual(429, self.route("/__harness/outside/api.trakt.tv/shows/popular?page=1&limit=250", trakt_headers()).status)


if __name__ == "__main__":
    unittest.main()
