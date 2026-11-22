"""Nixie 1.7 (stream T): the fake Trakt API (api.trakt.tv) the app's Trakt source asks in a harness run (the outside
layer's harness origin points it here), as strict as Trakt where the app depends on it (the smart collections plan, 2.1;
the terms re-check of 2026-11-21):

- GET /movies/trending, /shows/trending, /movies/popular, /shows/popular, /movies/anticipated, /shows/anticipated
  (?page=N&limit=M) and /movies/boxoffice (the weekend's top 10: not paged, no paging headers).
  * No User-Agent: 403 (Trakt may block a request without one).
  * No trakt-api-key, or not the harness's Client ID (HARNESS_CLIENT_ID, the app's TraktClientId.HarnessValue): 403, as
    Trakt answers an invalid key or an unapproved app. The key is compared, never kept or logged.
  * `limit` clamped at 250 (asking more gets 250; none gets 10, Trakt's default); a page past the end is an empty list.
  * Every paged answer carries X-Pagination-Page, -Limit, -Page-Count and -Item-Count; a page past the end says 0 and 0
    for the counts, as Trakt's did (live, 2026-11-21). {"counts"}: "honest" (the default),
    "placeholder" (Page-Count 1 and Item-Count = the page's size on every page, so a client that trusts it stops after
    page 1 of a long list), or "inflated" (Page-Count 99: a client that follows it must still stop at an empty page).
  * {"mode"}: "ok"; "429" (a bare 429: no Retry-After, no body, as Trakt's security systems send); "refuse" (403);
    "error" (502); "hang" (no answer for 30 s).
- The lists hold titles of the fake library (with its ProviderIds: Tmdb, Imdb, and Tvdb for shows) among titles it doesn't
  have, so the app's "Trending in your library" row and the Collections page's Trakt rows have something to
  match, and so do the contrast cases.
- Reached through outside.py (/__harness/outside/api.trakt.tv/<path>); POST /__harness/outside/config
  {"api.trakt.tv": {"mode", "counts", "sizes": {"movies/popular": n, ...}, "reset"}} configures it. Its config() names
  every request it took (path, query, User-Agent, trakt-api-version, Content-Type, and whether the key was right: never
  the key).
"""

import json
import threading
import time

import outside_exchange

#: the Client ID a harness run sends (ui\FluentFin.Core\Outside\TraktClientId.cs HarnessValue); nothing else is accepted
HARNESS_CLIENT_ID = "nixie-harness-trakt-client-id"
MAX_LIMIT = 250
DEFAULT_LIMIT = 10
MODES = ("ok", "429", "refuse", "error", "hang")
COUNTS = ("honest", "placeholder", "inflated")
LISTS = ("movies/trending", "shows/trending", "movies/popular", "shows/popular", "movies/anticipated", "shows/anticipated",
         "movies/boxoffice")
#: list sizes as Trakt's were on 2026-11-21 (the live check: X-Pagination-Item-Count at limit=250)
DEFAULT_SIZES = {"movies/trending": 240, "shows/trending": 679, "movies/popular": 486, "shows/popular": 737,
                 "movies/anticipated": 181, "shows/anticipated": 51, "movies/boxoffice": 10}
#: titles the library doesn't have get TMDb ids from here up (no fake library id is this high)
UNOWNED_BASE = 9_000_000


def owned_titles(lib, kind):
    """The fake library's films (kind "movie") or shows ("series"), as Trakt would list them: (name, year, ids)."""
    out = []
    if lib is None:
        return out
    import smartdata  # the fake library's ProviderIds (1.6 collections)
    for item in sorted(lib.items.values(), key=lambda i: i["Id"]):
        if item.get("kind") != kind or not item.get("Name"):
            continue
        ids = smartdata.provider_ids(lib, item)
        trakt_ids = {"trakt": int(item["Id"][:6], 16) + 1, "slug": item["Name"].lower().replace(" ", "-"),
                     "imdb": ids.get("Imdb"), "tmdb": int(ids["Tmdb"]) if ids.get("Tmdb") else None}
        if kind == "series":
            trakt_ids["tvdb"] = int(item["Id"][6:12], 16) + 100_000
        out.append((item["Name"], item.get("year"), trakt_ids))
    return out


def unowned(kind, n):
    """A title no fake library has: ids only it carries."""
    tmdb = UNOWNED_BASE + n
    ids = {"trakt": 5_000_000 + n, "slug": "elsewhere-%d" % n, "imdb": "tt9%06d" % n, "tmdb": tmdb}
    if kind == "series":
        ids["tvdb"] = 8_000_000 + n
    return ("Elsewhere %d" % n, 2000 + n % 26, ids)


def build_list(lib, name, size):
    """One list: every fourth place (from the 2nd) a library title, the rest titles it doesn't have; deterministic."""
    kind = "movie" if name.startswith("movies/") else "series"
    mine = owned_titles(lib, kind)
    offset = {"trending": 0, "popular": 3, "anticipated": 7, "boxoffice": 1}[name.split("/")[1]]
    mine = mine[offset:] + mine[:offset]
    titles, owned_at = [], 0
    for place in range(size):
        if place % 4 == 1 and owned_at < len(mine):
            titles.append(mine[owned_at])
            owned_at += 1
        else:
            titles.append(unowned(kind, place + 1000 * LISTS.index(name)))
    return titles


def entry(name, place, title):
    """A title as Trakt's list answers it: trending {watchers, movie|show}, anticipated {list_count, …}, box office
    {revenue, movie}, popular the title bare."""
    label, year, ids = title
    body = {"title": label, "year": year, "ids": dict(ids)}
    wrapper = "movie" if name.startswith("movies/") else "show"
    which = name.split("/")[1]
    if which == "popular":
        return body
    if which == "trending":
        return {"watchers": max(1, 500 - place), wrapper: body}
    if which == "anticipated":
        return {"list_count": max(1, 9000 - place * 7), wrapper: body}
    return {"revenue": max(1, 90_000_000 - place * 7_000_000), wrapper: body}


class FakeTrakt:
    def __init__(self, lib=None):
        self.lib = lib
        self.lock = threading.Lock()
        self.mode = "ok"
        self.counts = "honest"
        self.sizes = dict(DEFAULT_SIZES)
        self.requests = []
        self._lists = {}

    def configure(self, body):
        with self.lock:
            if body.get("mode") in MODES:
                self.mode = body["mode"]
            if body.get("counts") in COUNTS:
                self.counts = body["counts"]
            sizes = body.get("sizes")
            if isinstance(sizes, dict):
                for name, size in sizes.items():
                    if name in LISTS and isinstance(size, int) and 0 <= size <= 5000:
                        self.sizes[name] = size
                self._lists = {}
            if body.get("reset"):
                self.requests = []
            return self.state()

    def state(self):
        return {"mode": self.mode, "counts": self.counts, "sizes": dict(self.sizes), "requests": list(self.requests)}

    def config(self):
        """outside.py's /__harness/outside/config answer for this fake."""
        with self.lock:
            return self.state()

    def answer(self, method, path, query, headers, body):
        """outside.py's hook: (status, headers, payload) for GET https://api.trakt.tv<path>?<query>."""
        return outside_exchange.answer(self, method, path, query, headers)

    def titles(self, name):
        with self.lock:
            if name not in self._lists:
                self._lists[name] = build_list(self.lib, name, self.sizes[name])
            return self._lists[name]

    def _int(self, handler, key, default):
        value = (handler.q.get(key) or [None])[0]
        try:
            return int(value) if value is not None else default
        except ValueError:
            return default

    def api(self, handler, method, path):
        """GET <trakt path> (the outside layer's harness origin maps https://api.trakt.tv/<path> here)."""
        name = path.strip("/").lower()
        agent = (handler.headers.get("User-Agent") or "").strip()
        key = (handler.headers.get("trakt-api-key") or "").strip()
        record = {"t": round(time.time(), 3), "path": "/" + name, "query": {k: v[0] for k, v in handler.q.items()},
                  "ua": agent, "api_version": handler.headers.get("trakt-api-version"),
                  "content_type": handler.headers.get("Content-Type"), "key_ok": key == HARNESS_CLIENT_ID}
        with self.lock:
            self.requests.append(record)
            mode = self.mode
        if not agent:
            handler.route = "trakt:no-agent"
            return handler._send(403, b"", "application/json")
        if key != HARNESS_CLIENT_ID:
            handler.route = "trakt:bad-key"
            return handler._send(403, b"", "application/json")
        if name not in LISTS or method not in ("GET", "HEAD"):
            handler.route = "trakt:not-found"
            return handler._send(404, b"", "application/json")
        if mode == "hang":
            time.sleep(30)
            return handler._send(504, b"")
        if mode == "429":
            handler.route = "trakt:429"
            return handler._send(429, b"")      # bare: no Retry-After, no body
        if mode == "refuse":
            handler.route = "trakt:refused"
            return handler._send(403, b"", "application/json")
        if mode == "error":
            handler.route = "trakt:error"
            return handler._send(502, b"<html>Bad gateway</html>", "text/html")
        titles = self.titles(name)
        if name == "movies/boxoffice":
            handler.route = "trakt:boxoffice"
            body = [entry(name, i, t) for i, t in enumerate(titles[:10])]
            return handler._send(200, json.dumps(body).encode("utf-8"), "application/json")
        page = max(1, self._int(handler, "page", 1))
        limit = self._int(handler, "limit", DEFAULT_LIMIT)
        limit = DEFAULT_LIMIT if limit < 1 else min(limit, MAX_LIMIT)
        start = (page - 1) * limit
        chunk = titles[start:start + limit]
        body = [entry(name, start + i, t) for i, t in enumerate(chunk)]
        page_count = -(-len(titles) // limit)
        item_count = len(titles)
        if not chunk:
            page_count, item_count = 0, 0     # Trakt's answer past the end (live, 2026-11-21)
        elif self.counts == "placeholder":
            page_count, item_count = 1, len(chunk)
        elif self.counts == "inflated":
            page_count = 99
        headers = {"X-Pagination-Page": str(page), "X-Pagination-Limit": str(limit),
                   "X-Pagination-Page-Count": str(page_count), "X-Pagination-Item-Count": str(item_count)}
        handler.route = "trakt:%s p%d" % (name, page)
        return handler._send(200, json.dumps(body).encode("utf-8"), "application/json", headers)
