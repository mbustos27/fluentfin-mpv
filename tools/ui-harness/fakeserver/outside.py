"""Nixie 1.7 (stream K, the outside layer): the fake server's stand-ins for the outside sources Nixie fetches public lists
from. A harness run's app sends every outside request here (NIXIE_OUTSIDE_BASE, Core\\Outside\\OutsideRouting): a request
for https://<host>/<path> arrives as /__harness/outside/<host>/<path>, so no automated run reaches a real service.

Each source's fake is in its own file and is as strict as the real service where Nixie depends on it (the plan's §2.1, the
Error 153 lesson): outside_tmdb_exports.py (files.tmdb.org) and outside_wikidata.py (query.wikidata.org, query-next, QLever).
Stream T adds its own (api.trakt.tv, api.themoviedb.org) to HOSTS in its marked block.

Control (127.0.0.1 only):
- GET /__harness/outside/requests: every outside request so far (host, method, path, query, headers, body), for the
  harness's privacy check; POST /__harness/outside/requests {"clear": true} empties it.
- POST /__harness/outside/config {"<host>": {...}}: a fake's settings (each file says which); answers them all.
"""

import json
import threading
import urllib.parse

import outside_tmdb_exports
import outside_wikidata
import outside_trakt  # 1.7 trending
import outside_tmdb_trending  # 1.7 trending

PREFIX = "/__harness/outside/"


class FakeOutside:
    def __init__(self, lib):
        self.lib = lib
        self.lock = threading.Lock()
        self.requests = []
        wikidata = outside_wikidata.FakeWikidata(lib)
        tmdb_trending = outside_tmdb_trending.FakeTmdbTrending(lib)  # 1.7 trending
        self.hosts = {
            "files.tmdb.org": outside_tmdb_exports.FakeTmdbExports(lib),
            "query.wikidata.org": wikidata.endpoint("main"),
            "query-next.wikidata.org": wikidata.endpoint("next"),
            "qlever.cs.uni-freiburg.de": wikidata.endpoint("qlever"),
            # 1.7 trending: stream T's fakes (api.trakt.tv, api.themoviedb.org, and TMDb's image host, which the app never asks)
            "api.trakt.tv": outside_trakt.FakeTrakt(lib),
            "api.themoviedb.org": tmdb_trending,
            "image.tmdb.org": tmdb_trending,
            # end 1.7 trending
        }

    def config(self):
        return {host: fake.config() for host, fake in self.hosts.items() if hasattr(fake, "config")}

    def handle(self, handler, method, raw_path):
        """Answers /__harness/outside/...; False when the path isn't one of its."""
        low = raw_path.lower()
        if not low.startswith(PREFIX):
            return False
        rest = raw_path[len(PREFIX):]
        if low.rstrip("/") == PREFIX + "requests":
            if method == "POST" and handler._body().get("clear"):
                with self.lock:
                    self.requests.clear()
            with self.lock:
                handler._json(200, {"requests": list(self.requests)})
            return True
        if low.rstrip("/") == PREFIX + "config" and method in ("GET", "POST"):
            body = handler._body() if method == "POST" else {}
            for host, settings in body.items():
                fake = self.hosts.get(host)
                if fake is not None and hasattr(fake, "configure") and isinstance(settings, dict):
                    fake.configure(settings)
            handler._json(200, self.config())
            return True
        host, _, path = rest.partition("/")
        host = host.lower()
        query = urllib.parse.urlsplit(handler.path).query
        headers = {k: v for k, v in handler.headers.items()}
        body = handler.body_bytes.decode("utf-8", "replace") if handler.body_bytes else ""
        with self.lock:
            self.requests.append({"host": host, "method": method, "path": "/" + path, "query": query,
                                  "headers": headers, "body": body})
        handler.route = "outside:" + host
        fake = self.hosts.get(host)
        if fake is None:
            handler._send(404, b"no such outside host in the fake", "text/plain")
            return True
        status, out_headers, payload = fake.answer(method, "/" + path, query, headers, handler.body_bytes or b"")
        ctype = out_headers.pop("Content-Type", None)
        handler._send(status, payload, ctype, out_headers)
        return True


def dumps(obj):
    return json.dumps(obj).encode("utf-8")
