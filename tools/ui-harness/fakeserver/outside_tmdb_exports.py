"""Nixie 1.7 (stream K): files.tmdb.org's daily export files, as strict as the real host where Nixie depends on it (the
plan's §2.1 and the terms re-check of 2026-11-21):

- GET /p/exports/<type>_ids_MM_DD_YYYY.json.gz for collection and keyword (and the other types, empty): gzip bytes of
  JSON lines ({"id": n, "name": "..."}), sent as a file (application/octet-stream, no Content-Encoding).
- Generation starts about 07:00 UTC and every file is up by 08:00 UTC: today's file is 404 before 08:00 UTC (as is any
  later date). Files are kept 3 months: an older date is 404 too.
- {"require_key": true}: every file answers 401 (TMDb says a key requirement could come later).
- {"now": "2026-11-21T07:30:00Z"}: the fake's clock (else the real UTC time); {"now": null} goes back to it.

Names: the fake library's franchises (smartdata.FRANCHISES and the boxed "Kestrel Bay" one) and TMDb-style keywords for
library.TAGS, among filler lines that match nothing.
"""

import datetime
import gzip
import io
import json
import re
import threading

import smartdata

FILE = re.compile(r"^/p/exports/([a-z_]+)_ids_(\d\d)_(\d\d)_(\d{4})\.json\.gz$")
TYPES = ("collection", "keyword", "movie", "tv_series", "person", "tv_network", "production_company")
READY_HOUR = 8
KEPT_DAYS = 92

COLLECTION_NAMES = {
    smartdata.BOXED_FRANCHISE: "Kestrel Bay Collection",
    "9101": "The Lantern Keepers Collection",
    "9102": "Northwind Collection",
    "9103": "The Paper Moon Saga",
    "9104": "Harbor Lights Collection",
    "9105": "Ironwood Chronicles Collection",
    "9106": "Seventh Wave Collection",
}

# TMDb's own keyword ids where the fake library's tags are TMDb keywords (library.TAGS), plus a few the allowlist knows
KEYWORDS = {
    818: "based on novel or book", 4379: "time travel", 10051: "heist", 10683: "coming of age", 7312: "road trip",
    9882: "space", 9826: "murder", 9748: "revenge", 4565: "dystopia", 6054: "friendship", 10349: "survival",
    12339: "slasher", 9715: "superhero", 12377: "zombie", 3133: "vampire",
}


class FakeTmdbExports:
    def __init__(self, lib):
        self.lib = lib
        self.lock = threading.Lock()
        self.require_key = False
        self.now = None
        self.served = 0

    def config(self):
        return {"require_key": self.require_key, "now": self.now.isoformat() if self.now else None, "served": self.served}

    def configure(self, settings):
        with self.lock:
            if "require_key" in settings:
                self.require_key = bool(settings["require_key"])
            if "now" in settings:
                self.now = parse_time(settings["now"]) if settings["now"] else None

    def clock(self):
        return self.now or datetime.datetime.now(datetime.timezone.utc)

    def answer(self, method, path, query, headers, body):
        if method not in ("GET", "HEAD"):
            return 405, {"Content-Type": "text/plain"}, b"Method Not Allowed"
        if self.require_key:
            return 401, {"Content-Type": "application/json"}, json.dumps(
                {"status_code": 7, "status_message": "Invalid API key: You must be granted a valid key.", "success": False}).encode()
        m = FILE.match(path)
        if not m or m.group(1) not in TYPES:
            return 404, {"Content-Type": "application/xml"}, not_found(path)
        try:
            day = datetime.date(int(m.group(4)), int(m.group(2)), int(m.group(3)))
        except ValueError:
            return 404, {"Content-Type": "application/xml"}, not_found(path)
        now = self.clock()
        today = now.date()
        if day > today or (day == today and now.hour < READY_HOUR) or (today - day).days > KEPT_DAYS:
            return 404, {"Content-Type": "application/xml"}, not_found(path)
        with self.lock:
            self.served += 1
        return 200, {"Content-Type": "application/octet-stream"}, export_file(m.group(1))


def parse_time(text):
    when = datetime.datetime.fromisoformat(str(text).replace("Z", "+00:00"))
    return when if when.tzinfo else when.replace(tzinfo=datetime.timezone.utc)


def not_found(path):
    key = path.lstrip("/")
    return ("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>NoSuchKey</Code><Message>The specified key does not "
            "exist.</Message><Key>%s</Key></Error>" % key).encode()


def lines(kind):
    if kind == "collection":
        rows = [{"id": int(k), "name": v} for k, v in COLLECTION_NAMES.items()]
        rows += [{"id": 70000 + n, "name": "Filler Collection %d" % n} for n in range(40)]
    elif kind == "keyword":
        rows = [{"id": k, "name": v} for k, v in KEYWORDS.items()]
        rows += [{"id": 300000 + n, "name": "filler keyword %d" % n} for n in range(40)]
    else:
        rows = []
    rows.sort(key=lambda r: r["id"])
    return rows


def export_file(kind):
    """The day's file: one JSON object a line, gzip, as TMDb publishes them."""
    text = "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in lines(kind))
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode="wb", mtime=0) as f:
        f.write(text.encode("utf-8"))
    return buf.getvalue()
