"""Nixie 1.7 (stream T): the fake TMDb API's trending lists (api.themoviedb.org/3/trending/{movie,tv}/week), which the
app's "TMDb trending" source asks in a harness run (the outside layer's harness origin points it here), as strict as TMDb
where the app depends on it (the smart collections plan, 2.1):

- GET /3/trending/movie/week and /3/trending/tv/week (?page=N, 20 results a page, total_pages 500 as TMDb caps it).
  * The token only as `Authorization: Bearer <token>`: none, or not the harness's (HARNESS_TOKEN), is TMDb's 401 with its
    status_code 7. An `api_key` in the address is refused the same way and counted (the app must never put one there).
  * A page outside 1..500: TMDb's 400 with status_code 22.
  * {"mode"}: "ok"; "429" (TMDb's 429, status_code 25, with Retry-After); "refuse" (401); "error" (502); "hang" (30 s).
  * The results carry TMDb's fields (poster_path among them): the app must match on `id` and never fetch an image.
- GET /t/p/<size>/<file>: an image, WebP when the request's Accept allows it, as TMDb's image host has since 2025-04. The
  app never asks for one; `image_requests` in the state counts any that came (the "no image requests" check).
- The lists hold the fake library's films and shows (by their ProviderIds' Tmdb) among titles it doesn't have.
- Reached through outside.py (/__harness/outside/api.themoviedb.org/<path>, and image.tmdb.org for the image route);
  POST /__harness/outside/config {"api.themoviedb.org": {"mode", "sizes", "reset"}} configures it. Its config() names every
  request it took (path, query, User-Agent, whether the Bearer was right: never the token) and counts image requests.
"""

import json
import threading
import time

import outside_exchange

#: the token a harness run types into Settings (a JWT's shape, as TMDb's API Read Access Token has); nothing else works
HARNESS_TOKEN = "eyJhbGciOiJIUzI1NiJ9.eyJuaXhpZSI6Imhhcm5lc3MifQ.bml4aWUtaGFybmVzcy1zaWduYXR1cmU"
PAGE_SIZE = 20
MAX_PAGE = 500
MODES = ("ok", "429", "refuse", "error", "hang")
LISTS = ("movie", "tv")
DEFAULT_SIZES = {"movie": 240, "tv": 200}
UNOWNED_BASE = 9_500_000
INVALID_KEY = {"status_code": 7, "status_message": "Invalid API key: You must be granted a valid key.", "success": False}
INVALID_PAGE = {"status_code": 22, "status_message": "Invalid page: Pages start at 1 and max at 500. They are expected to be an integer.",
                "success": False}
OVER_LIMIT = {"status_code": 25, "status_message": "Your request count (41) is over the allowed limit of (40).", "success": False}
#: a 1x1 WebP and a 1x1 JPEG, the two answers of the image route
WEBP = bytes.fromhex("52494646240000005745425056503820180000003001009d012a0100010002003425a400037000feff00000000")
JPEG = bytes.fromhex("ffd8ffe000104a46494600010100000100010000ffdb004300080606070605080707070909080a0c140d0c0b0b0c1912130f141d1a"
                     "1f1e1d1a1c1c20242e2720222c231c1c2837292c30313434341f27393d38323c2e333432ffc0000b080001000101011100ffc400"
                     "1f0000010501010101010100000000000000000102030405060708090a0bffda0008010100003f00d2cf20ffd9")


def owned(lib, media):
    """The fake library's films ("movie") or shows ("tv") by their Tmdb id: (tmdb, name, year)."""
    if lib is None:
        return []
    import smartdata  # the fake library's ProviderIds (1.6 collections)
    kind = "movie" if media == "movie" else "series"
    out = []
    for item in sorted(lib.items.values(), key=lambda i: i["Id"]):
        if item.get("kind") == kind and item.get("Name"):
            ids = smartdata.provider_ids(lib, item)
            if ids.get("Tmdb"):
                out.append((int(ids["Tmdb"]), item["Name"], item.get("year")))
    return out


def result(media, tmdb, name, year):
    """A result as TMDb's trending answers it (the fields the app ignores included)."""
    date = "%04d-06-15" % (year or 2020)
    body = {"adult": False, "backdrop_path": "/b%d.jpg" % tmdb, "id": tmdb, "overview": "", "poster_path": "/p%d.jpg" % tmdb,
            "media_type": media, "original_language": "en", "genre_ids": [18], "popularity": 100.0, "vote_average": 7.0,
            "vote_count": 100}
    if media == "movie":
        body.update(title=name, original_title=name, release_date=date, video=False)
    else:
        body.update(name=name, original_name=name, first_air_date=date, origin_country=["US"])
    return body


class FakeTmdbTrending:
    def __init__(self, lib=None):
        self.lib = lib
        self.lock = threading.Lock()
        self.mode = "ok"
        self.sizes = dict(DEFAULT_SIZES)
        self.requests = []
        self.image_requests = 0
        self.api_key_in_url = 0
        self._lists = {}

    def configure(self, body):
        with self.lock:
            if body.get("mode") in MODES:
                self.mode = body["mode"]
            sizes = body.get("sizes")
            if isinstance(sizes, dict):
                for name, size in sizes.items():
                    if name in LISTS and isinstance(size, int) and 0 <= size <= MAX_PAGE * PAGE_SIZE:
                        self.sizes[name] = size
                self._lists = {}
            if body.get("reset"):
                self.requests, self.image_requests, self.api_key_in_url = [], 0, 0
            return self.state()

    def state(self):
        return {"mode": self.mode, "sizes": dict(self.sizes), "requests": list(self.requests),
                "image_requests": self.image_requests, "api_key_in_url": self.api_key_in_url}

    def config(self):
        """outside.py's /__harness/outside/config answer for this fake."""
        with self.lock:
            return self.state()

    def answer(self, method, path, query, headers, body):
        """outside.py's hook: (status, headers, payload) for GET https://api.themoviedb.org<path>?<query>."""
        return outside_exchange.answer(self, method, path, query, headers)

    def results(self, media):
        with self.lock:
            if media not in self._lists:
                mine = owned(self.lib, media)
                out = []
                for place in range(self.sizes[media]):
                    if place % 3 == 2 and mine:
                        tmdb, name, year = mine.pop(0)
                    else:
                        tmdb, name, year = UNOWNED_BASE + place + (0 if media == "movie" else 100_000), "Elsewhere %d" % place, 2001 + place % 25
                    out.append(result(media, tmdb, name, year))
                self._lists[media] = out
            return self._lists[media]

    def api(self, handler, method, path):
        """GET /3/trending/<movie|tv>/week (the outside layer's harness origin maps https://api.themoviedb.org/<path> here)."""
        low = path.lower().rstrip("/")
        auth = (handler.headers.get("Authorization") or "").strip()
        bearer_ok = auth == "Bearer " + HARNESS_TOKEN
        with self.lock:
            self.requests.append({"t": round(time.time(), 3), "path": low, "query": {k: v[0] for k, v in handler.q.items() if k != "api_key"},
                                  "api_key_in_url": "api_key" in handler.q, "ua": handler.headers.get("User-Agent") or "",
                                  "bearer_ok": bearer_ok, "accept": handler.headers.get("Accept")})
            if "api_key" in handler.q:
                self.api_key_in_url += 1
            mode = self.mode
        if low.startswith("/t/p/"):
            with self.lock:
                self.image_requests += 1
            handler.route = "tmdb:image"
            if "image/webp" in (handler.headers.get("Accept") or ""):
                return handler._send(200, WEBP, "image/webp")
            return handler._send(200, JPEG, "image/jpeg")
        if "api_key" in handler.q or not bearer_ok:
            handler.route = "tmdb:invalid-key"
            return handler._json(401, INVALID_KEY)
        media = {"/3/trending/movie/week": "movie", "/3/trending/tv/week": "tv"}.get(low)
        if media is None or method not in ("GET", "HEAD"):
            handler.route = "tmdb:not-found"
            return handler._json(404, {"status_code": 34, "status_message": "The resource you requested could not be found.", "success": False})
        if mode == "hang":
            time.sleep(30)
            return handler._send(504, b"")
        if mode == "429":
            handler.route = "tmdb:429"
            body = json.dumps(OVER_LIMIT).encode()
            return handler._send(429, body, "application/json;charset=utf-8", {"Retry-After": "10"})
        if mode == "refuse":
            handler.route = "tmdb:refused"
            return handler._json(401, INVALID_KEY)
        if mode == "error":
            handler.route = "tmdb:error"
            return handler._send(502, b"<html>Bad gateway</html>", "text/html")
        raw = (handler.q.get("page") or ["1"])[0]
        try:
            page = int(raw)
        except ValueError:
            page = 0
        if page < 1 or page > MAX_PAGE:
            handler.route = "tmdb:invalid-page"
            return handler._json(400, INVALID_PAGE)
        everything = self.results(media)
        chunk = everything[(page - 1) * PAGE_SIZE: page * PAGE_SIZE]
        body = {"page": page, "results": chunk, "total_pages": MAX_PAGE, "total_results": MAX_PAGE * PAGE_SIZE}
        handler.route = "tmdb:trending %s p%d" % (media, page)
        return handler._send(200, json.dumps(body).encode("utf-8"), "application/json;charset=utf-8")
