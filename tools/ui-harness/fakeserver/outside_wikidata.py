"""Nixie 1.7 (stream K): Wikidata's query service, as strict as the real one where Nixie depends on it (the plan's §2.1 and
the terms re-check of 2026-11-21). Three endpoints share one fake (outside.py routes them by host):
"main" (query.wikidata.org/sparql), "next" (query-next.wikidata.org/sparql) and "qlever" (qlever.cs.uni-freiburg.de/api/wikidata).

Refusals, as the real service gives them:
- a User-Agent with no contact (no http(s) URL and no e-mail address), or none at all: 403 with Wikimedia's robot-policy text;
- "next" before it opens to the community ({"next_open": false}, the default): 403 "Unauthorized User-Agent";
- more than 30 error queries a minute from one User-Agent (the terms re-check: a limit the plan missed): 429 with
  Retry-After for the rest of that minute;
- more than 5 queries at once: 429 with Retry-After;
- {"mode": "429"}: 429 with Retry-After {"retry_after"} (30 s);
- {"mode": "timeout"}: the query runs {"timeout_s"} seconds (60, the real limit) and answers 500 with Blazegraph's
  java.util.concurrent.TimeoutException;
- {"mode": "error"}: 500; a query that isn't a SELECT: 400 (MalformedQueryException).

A SELECT answers SPARQL JSON results with the variables its SELECT names. Each Wikidata item the query names (wd:Q...) gets
its own deterministic rows: some of the fake library's films (their Tmdb and Imdb ids, as smartdata.provider_ids gives
them; ?tmdbTv takes shows'), among filler rows that match nothing. ?date is the award's point in time.
"""

import hashlib
import json
import random
import re
import threading
import time
import urllib.parse

import smartdata

# both as the real service answered the live check (2026-11-22): 403, text/plain
NO_CONTACT = (b"Please set a user-agent and respect our robot policy https://w.wiki/4wJS. "
              b"See also https://phabricator.wikimedia.org/T400119.\n")
UNAUTHORIZED = b"Unauthorized User-Agent (3732189)"
TIMEOUT = (b"SPARQL-QUERY: queryStr=...\njava.util.concurrent.TimeoutException\n"
           b"\tat java.util.concurrent.FutureTask.get(FutureTask.java:205)\n")
MALFORMED = b"MalformedQueryException: Encountered \" <EOF> \". Was expecting one of: \"select\" ...\n"
CONTACT = re.compile(r"https?://\S+|[\w.+-]+@[\w-]+\.[\w.-]+")
ITEM = re.compile(r"\bwd:(Q\d+)\b")
SELECT = re.compile(r"\bSELECT\s+(?:DISTINCT\s+)?((?:\?\w+\s*)+)", re.IGNORECASE)
PATHS = {"main": "/sparql", "next": "/sparql", "qlever": "/api/wikidata"}
ERRORS_PER_MINUTE = 30
PARALLEL = 5


class FakeWikidata:
    def __init__(self, lib):
        self.lib = lib
        self.lock = threading.Lock()
        self.mode = "ok"
        self.retry_after = 30
        self.timeout_s = 60.0
        self.next_open = False
        self.errors = {}        # User-Agent -> times of its error answers
        self.running = 0
        self.queries = 0

    def endpoint(self, name):
        return _Endpoint(self, name)

    def config(self):
        return {"mode": self.mode, "retry_after": self.retry_after, "timeout_s": self.timeout_s, "next_open": self.next_open,
                "queries": self.queries}

    def configure(self, settings):
        with self.lock:
            if "mode" in settings:
                self.mode = str(settings["mode"])
            if "retry_after" in settings:
                self.retry_after = int(settings["retry_after"])
            if "timeout_s" in settings:
                self.timeout_s = float(settings["timeout_s"])
            if "next_open" in settings:
                self.next_open = bool(settings["next_open"])
            if settings.get("reset_errors"):
                self.errors.clear()

    def _error(self, agent, status, headers, body):
        with self.lock:
            self.errors.setdefault(agent, []).append(time.time())
        return status, headers, body

    def answer(self, endpoint, method, path, query, headers, body):
        agent = next((v for k, v in headers.items() if k.lower() == "user-agent"), "")
        if path.rstrip("/") != PATHS[endpoint]:
            return 404, {"Content-Type": "text/plain"}, b"Not Found"
        if not agent or not CONTACT.search(agent):
            return self._error(agent, 403, {"Content-Type": "text/plain"}, NO_CONTACT)
        if endpoint == "next" and not self.next_open:
            return self._error(agent, 403, {"Content-Type": "text/plain"}, UNAUTHORIZED)
        now = time.time()
        with self.lock:
            recent = [t for t in self.errors.get(agent, []) if now - t < 60]
            self.errors[agent] = recent
            if len(recent) >= ERRORS_PER_MINUTE:
                wait = max(1, int(60 - (now - recent[0])) + 1)
                return 429, {"Content-Type": "text/plain", "Retry-After": str(wait)}, b"Too Many Requests\n"
            if self.running >= PARALLEL:
                return 429, {"Content-Type": "text/plain", "Retry-After": "5"}, b"Too Many Requests\n"
            self.running += 1
            self.queries += 1
        try:
            if self.mode == "429":
                return 429, {"Content-Type": "text/plain", "Retry-After": str(self.retry_after)}, b"Too Many Requests\n"
            if self.mode == "timeout":
                time.sleep(self.timeout_s)
                return self._error(agent, 500, {"Content-Type": "text/plain"}, TIMEOUT)
            if self.mode == "error":
                return self._error(agent, 500, {"Content-Type": "text/plain"}, b"Internal Server Error\n")
            text = sparql_text(method, query, headers, body)
            select = SELECT.search(text or "")
            if not select:
                return self._error(agent, 400, {"Content-Type": "text/plain"}, MALFORMED)
            names = [v.lstrip("?") for v in select.group(1).split()]
            rows = self.rows(ITEM.findall(text), names)
            out = {"head": {"vars": names}, "results": {"bindings": rows}}
            return 200, {"Content-Type": "application/sparql-results+json;charset=utf-8"}, json.dumps(out).encode("utf-8")
        finally:
            with self.lock:
                self.running -= 1

    def rows(self, items, names):
        lib = self.lib
        films = sorted((i for i in lib.items.values() if i["kind"] == "movie"), key=lambda i: i["Id"])
        shows = sorted((i for i in lib.items.values() if i["kind"] == "series"), key=lambda i: i["Id"])
        out = []
        for qid in items:
            rng = random.Random(int(hashlib.sha256(qid.encode()).hexdigest()[:8], 16))
            owned = rng.sample(films, min(len(films), 9))
            owned_shows = rng.sample(shows, min(len(shows), 3)) if "tmdbTv" in names else []
            for n, film in enumerate(owned + owned_shows + [None] * 12):
                ids = smartdata.provider_ids(lib, film) if film else {
                    "Tmdb": str(900000 + rng.randrange(90000)), "Imdb": "tt9%06d" % rng.randrange(1000000)}
                is_show = film is not None and film["kind"] == "series"
                row = {}
                for name in names:
                    if name == "item":
                        row[name] = {"type": "uri", "value": "http://www.wikidata.org/entity/Q%d" % (5000000 + rng.randrange(10 ** 6))}
                    elif name == "tmdb" and not is_show:
                        row[name] = {"type": "literal", "value": ids["Tmdb"]}
                    elif name == "tmdbTv" and is_show:
                        row[name] = {"type": "literal", "value": ids["Tmdb"]}
                    elif name == "imdb" and "Imdb" in ids:
                        row[name] = {"type": "literal", "value": ids["Imdb"]}
                    elif name == "date":
                        row[name] = {"datatype": "http://www.w3.org/2001/XMLSchema#dateTime", "type": "literal",
                                     "value": "%d-03-01T00:00:00Z" % (1960 + (n * 7 + rng.randrange(5)) % 66)}
                out.append(row)
        return out


class _Endpoint:
    def __init__(self, fake, name):
        self.fake = fake
        self.name = name

    def config(self):
        return self.fake.config()

    def configure(self, settings):
        self.fake.configure(settings)

    def answer(self, method, path, query, headers, body):
        return self.fake.answer(self.name, method, path, query, headers, body)


def sparql_text(method, query, headers, body):
    """The query, from ?query= (GET), a form body or an application/sparql-query body (POST)."""
    q = urllib.parse.parse_qs(query or "")
    if "query" in q:
        return q["query"][0]
    if method == "POST" and body:
        ctype = next((v for k, v in headers.items() if k.lower() == "content-type"), "")
        text = body.decode("utf-8", "replace")
        if "sparql-query" in ctype:
            return text
        form = urllib.parse.parse_qs(text)
        if "query" in form:
            return form["query"][0]
    return None
