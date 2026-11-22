"""The showcase library: a small, curated library for the README's screenshots, in place of the generated one.

Off by default. FAKESERVER_SHOWCASE=<manifest.json, or a folder holding showcase.json> turns it on: the server's
libraries then hold only the manifest's films, series and collections, with their own names, years and overviews, and
every picture is one of the manifest's files. Nothing generated is shown: an image the manifest has no file for is not
advertised (no ImageTag) and answers 404, people have no photos, and chapters have no images.

The content rule: only public-domain or Creative Commons pictures from Wikimedia Commons (or made for Nixie), each
credited in the manifest and in the README's credits; overviews in our own words, never copied synopses. validate()
checks what can be checked, and GET /__harness/showcase lists the credits and any problem. The library the README's
screenshots use is showcase/ (showcase.json, its pictures in art/ made by make_art.py from the files in sources.tsv).

The manifest (JSON; paths are relative to its folder, or to "art_dir" when given):

    {
      "art_dir": "art",
      "views": [{"name": "Movies", "type": "movies"}, {"name": "Shows", "type": "tvshows"}],
      "films": [{
        "key": "sintel", "name": "Sintel", "year": 2010, "view": "Movies", "role": "movie",
        "genres": ["Animation", "Fantasy"], "rating": "PG-13", "community": 7.4, "critic": null,
        "overview": "Our own two sentences.", "tagline": "", "studios": ["Blender Foundation"],
        "people": [{"name": "Colin Levy", "type": "Director"}], "countries": ["Netherlands"],
        "art": {"Primary": "sintel-poster.jpg", "Backdrop": "sintel-backdrop.jpg", "Logo": "sintel-logo.png"},
        "user": {"progress": 0.4, "days_ago": 1, "favorite": true}
      }],
      "series": [{
        "key": "...", "name": "...", "year": 1954, "view": "Shows", "role": "series", "status": "Ended", ...,
        "seasons": [{"index": 1, "year": 1954, "art": {"Primary": "..."},
                     "episodes": [{"index": 1, "name": "...", "overview": "...", "art": {"Primary": "..."}}]}],
        "user": {"watched": 3, "days_ago": 3}
      }],
      "collections": [{"name": "Blender open movies", "members": ["sintel", "bbb"], "overview": "...", "art": {}}],
      "credits": [{"file": "sintel-poster.jpg", "work": "Sintel", "author": "Blender Foundation",
                   "licence": "CC BY 3.0", "licence_url": "https://creativecommons.org/licenses/by/3.0/",
                   "source": "https://commons.wikimedia.org/wiki/File:..."}]
    }

- "role": "movie" and "series" are required once each (the harness's fixture opens them); "anime" and "documentary"
  are optional.
- Pictures fall back as Jellyfin's do: a film's or series' Thumb is its Backdrop; a season's Primary is its series';
  an episode's Primary is its series' Backdrop; a collection's pictures are its first member's; a library's Primary is
  its first item's Backdrop. A Logo is shown only when the manifest has one.
- "user": a film's progress (0-1, resumable), played, favorite, days_ago (when it was last played); a series' watched
  (the first N episodes played: the next one is Next Up) and an optional progress on episode N + 1.
- Optional per film or series: "runtime" (minutes) for the item pages' runtime; the stream is still the test video.

Standard library only.
"""

import datetime
import json
import os
import re

#: licences a showcase picture may carry
LICENCE = re.compile(r"^(Public domain|CC0 1\.0|CC BY(-SA)? [1-4]\.0|CC BY(-SA)? 2\.5)$")
#: where a picture may come from: Wikimedia Commons, or made for Nixie ("nixie")
SOURCE = re.compile(r"^(https://commons\.wikimedia\.org/wiki/File:.+|nixie)$")
OVERVIEW_MAX = 320
ART_MAX_BYTES = 700_000
TYPES = {".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png", ".webp": "image/webp"}


def licence_url(licence):
    """A Creative Commons licence's deed, from its name ("CC BY 3.0", "CC BY-SA 4.0", "CC0 1.0")."""
    if licence.startswith("CC0"):
        return "https://creativecommons.org/publicdomain/zero/1.0/"
    kind, version = licence[3:].rsplit(" ", 1)
    return "https://creativecommons.org/licenses/%s/%s/" % (kind.lower(), version)


def path_from(value):
    """FAKESERVER_SHOWCASE's value as the manifest's path: a .json file, or a folder holding showcase.json."""
    if not value:
        return None
    return value if value.lower().endswith(".json") else os.path.join(value, "showcase.json")


def load(path):
    with open(path, encoding="utf-8") as f:
        manifest = json.load(f)
    base = os.path.dirname(os.path.abspath(path))
    manifest["_art"] = os.path.join(base, manifest.get("art_dir") or "")
    return manifest


def validate(manifest):
    """What breaks the content rule or the fixture, as plain sentences (empty: none)."""
    problems = []
    art_dir = manifest["_art"]
    credits = {c.get("file"): c for c in manifest.get("credits", [])}
    roles = [e.get("role") for e in manifest.get("films", []) + manifest.get("series", []) if e.get("role")]
    for role in ("movie", "series"):
        if roles.count(role) != 1:
            problems.append("the manifest needs exactly one item with role %r (has %d)" % (role, roles.count(role)))
    used = set()

    def art_of(entry, what):
        for kind, name in (entry.get("art") or {}).items():
            if not name:
                continue
            used.add(name)
            full = os.path.join(art_dir, name)
            if not os.path.isfile(full):
                problems.append("%s: %s file %s is missing" % (what, kind, name))
            elif os.path.getsize(full) > ART_MAX_BYTES:
                problems.append("%s: %s file %s is over %d KB" % (what, kind, name, ART_MAX_BYTES // 1000))
            if os.path.splitext(name)[1].lower() not in TYPES:
                problems.append("%s: %s file %s is not .jpg, .png or .webp" % (what, kind, name))

    for entry in manifest.get("films", []) + manifest.get("series", []):
        what = entry.get("name") or entry.get("key") or "an item"
        for field in ("key", "name", "year", "view", "genres", "overview"):
            if not entry.get(field):
                problems.append("%s: no %s" % (what, field))
        if len(entry.get("overview") or "") > OVERVIEW_MAX:
            problems.append("%s: the overview is over %d characters (keep it short, in our own words)" % (what, OVERVIEW_MAX))
        art = entry.get("art") or {}
        for kind in ("Primary", "Backdrop"):
            if not art.get(kind):
                problems.append("%s: no %s picture" % (what, kind))
        art_of(entry, what)
        for season in entry.get("seasons", []):
            art_of(season, "%s season %s" % (what, season.get("index")))
            for ep in season.get("episodes", []):
                art_of(ep, "%s S%sE%s" % (what, season.get("index"), ep.get("index")))
                if len(ep.get("overview") or "") > OVERVIEW_MAX:
                    problems.append("%s S%sE%s: the overview is over %d characters" % (what, season.get("index"), ep.get("index"), OVERVIEW_MAX))
    keys = {e.get("key") for e in manifest.get("films", [])}
    for box in manifest.get("collections", []):
        art_of(box, "collection %s" % box.get("name"))
        for member in box.get("members", []):
            if member not in keys:
                problems.append("collection %s: no film with key %r" % (box.get("name"), member))
    for name in sorted(used):
        c = credits.get(name)
        if not c:
            problems.append("%s has no credit" % name)
            continue
        if not LICENCE.match(c.get("licence") or ""):
            problems.append("%s: licence %r is not public domain, CC0 or CC BY(-SA)" % (name, c.get("licence")))
        if not SOURCE.match(c.get("source") or ""):
            problems.append("%s: source %r is not a Wikimedia Commons file page (or 'nixie')" % (name, c.get("source")))
        if c.get("licence", "").startswith("CC BY") and not c.get("author"):
            problems.append("%s: a CC BY picture needs its author" % name)
        if LICENCE.match(c.get("licence") or "") and c["licence"].startswith("CC") and c.get("licence_url") != licence_url(c["licence"]):
            problems.append("%s: licence %s links to %r, not %s" % (name, c["licence"], c.get("licence_url"),
                                                                  licence_url(c["licence"])))
    return problems


def build(lib, manifest, ids, tag):
    """Fills lib (library.Library, before anything else is in it) from the manifest. ids(*parts) and tag(id, kind) are
    library's own _id and _tag."""
    art_dir = manifest["_art"]
    now = lib.now
    lib.showcase_manifest = manifest

    def files(art):
        return {k: os.path.join(art_dir, v) for k, v in (art or {}).items() if v and os.path.isfile(os.path.join(art_dir, v))}

    def people(entry):
        out = []
        for p in entry.get("people", []):
            pid = ids("person", p["name"])
            if pid not in lib.people:
                lib.people[pid] = {"Id": pid, "Name": p["name"], "Type": "Person", "kind": "person", "created": now,
                                   "parent": None, "view": None, "showcase": None, "art": {}}
                lib.items[pid] = lib.people[pid]
            out.append({"Name": p["name"], "Id": pid, "Role": p.get("role", ""), "Type": p.get("type", "Actor")})
        return out

    def common(item_id, kind, entry, parent, view, created, year):
        return {"Id": item_id, "kind": kind, "Name": entry["name"], "parent": parent, "view": view, "created": created,
                "year": year, "premiere": datetime.datetime(year, entry.get("month", 6), entry.get("day", 1)),
                "genres": list(entry.get("genres", [])), "tags": list(entry.get("tags", [])),
                "rating": entry.get("rating", "NR"), "community": entry.get("community"), "critic": entry.get("critic"),
                "people": people(entry), "studios": list(entry.get("studios", [])), "overview": entry.get("overview", ""),
                "tagline": entry.get("tagline", ""), "showcase": entry.get("role"), "countries": list(entry.get("countries", [])),
                "runtime_minutes": entry.get("runtime")}

    views = {}
    for n, v in enumerate(manifest.get("views", [])):
        vid = ids("view", v["name"])
        lib.items[vid] = {"Id": vid, "kind": "view", "Name": v["name"], "collection": v["type"], "parent": None,
                          "view": vid, "created": now - datetime.timedelta(days=900 - n), "showcase": None, "art": {}}
        lib.views.append(vid)
        lib.children[vid] = []
        views[v["name"]] = vid

    by_key = {}
    entries = manifest.get("films", []) + manifest.get("series", [])
    for n, entry in enumerate(entries):
        vid = views[entry["view"]]
        # newest first in the manifest's order: Latest shows them as the manifest lists them
        created = now - datetime.timedelta(days=2 + n, hours=n)
        art = files(entry.get("art"))
        if "Backdrop" in art:
            art.setdefault("Thumb", art["Backdrop"])
        if entry in manifest.get("films", []):
            mid = ids("movie", entry["view"], entry["name"])
            item = common(mid, "movie", entry, vid, vid, created, entry["year"])
            item["art"] = art
            lib.items[mid] = item
            lib.children[vid].append(mid)
            by_key[entry["key"]] = mid
            continue
        sid = ids("series", entry["view"], entry["name"])
        series = common(sid, "series", entry, vid, vid, created, entry["year"])
        series.update(status=entry.get("status", "Ended"), art=art)
        lib.items[sid] = series
        lib.children[vid].append(sid)
        lib.children[sid] = []
        by_key[entry["key"]] = sid
        for season in entry.get("seasons", []):
            s = season["index"]
            season_id = ids("season", sid, s)
            year = season.get("year", entry["year"])
            season_art = files(season.get("art"))
            if "Primary" in art:
                season_art.setdefault("Primary", art["Primary"])
            lib.items[season_id] = {"Id": season_id, "kind": "season", "Name": season.get("name", "Season %d" % s),
                                    "parent": sid, "view": vid, "series": sid, "index": s, "created": created, "year": year,
                                    "premiere": datetime.datetime(year, 9, 1), "overview": season.get("overview", ""),
                                    "genres": [], "tags": [], "rating": series["rating"], "people": [], "studios": [],
                                    "showcase": None, "art": season_art}
            lib.children[sid].append(season_id)
            lib.children[season_id] = []
            for ep in season.get("episodes", []):
                e = ep["index"]
                eid = ids("episode", season_id, e)
                ep_entry = dict(ep, genres=series["genres"], rating=series["rating"], studios=[], role=None)
                item = common(eid, "episode", ep_entry, season_id, vid, created - datetime.timedelta(days=e), year)
                item.update(series=sid, season=season_id, index=e, parent_index=s,
                            premiere=datetime.datetime(year, 9, 1) + datetime.timedelta(days=7 * (e - 1)))
                ep_art = files(ep.get("art"))
                if "Backdrop" in art:
                    ep_art.setdefault("Primary", art["Backdrop"])
                item["art"] = ep_art
                lib.items[eid] = item
                lib.children[season_id].append(eid)

    boxes = manifest.get("collections", [])
    if boxes:
        vid = ids("view", "Collections")
        lib.items[vid] = {"Id": vid, "kind": "view", "Name": "Collections", "collection": "boxsets", "parent": None,
                          "view": vid, "created": now - datetime.timedelta(days=800), "showcase": None, "art": {}}
        lib.views.append(vid)
        lib.children[vid] = []
        for n, box in enumerate(boxes):
            bid = ids("boxset", box["name"])
            members = [by_key[k] for k in box["members"]]
            first = lib.items[members[0]]
            art = files(box.get("art"))
            for kind in ("Primary", "Backdrop", "Thumb"):
                if kind in first["art"]:
                    art.setdefault(kind, first["art"][kind])
            years = [lib.items[m]["year"] for m in members]
            lib.items[bid] = {"Id": bid, "kind": "boxset", "Name": box["name"], "parent": vid, "view": vid,
                              "created": now - datetime.timedelta(days=10 + n), "year": min(years),
                              "premiere": min(lib.items[m]["premiere"] for m in members),
                              "genres": sorted({g for m in members for g in lib.items[m]["genres"]})[:3], "tags": [],
                              "rating": first["rating"], "people": [], "studios": [],
                              "overview": box.get("overview", ""), "tagline": "", "community": None, "critic": None,
                              "showcase": None, "art": art}
            lib.children[vid].append(bid)
            lib.children[bid] = members

    for vid in lib.views:
        kids = lib.children[vid]
        kids.sort(key=lambda i: lib.sort_name(lib.items[i]))
        backdrop = next((lib.items[i]["art"].get("Backdrop") for i in kids if lib.items[i]["art"].get("Backdrop")), None)
        if backdrop:
            lib.items[vid]["art"]["Primary"] = backdrop

    _seed_user(lib, manifest, by_key)
    return lib


def _seed_user(lib, manifest, by_key):
    now = lib.now
    for entry in manifest.get("films", []):
        u = entry.get("user") or {}
        if not u:
            continue
        ud = lib._ud(by_key[entry["key"]])
        when = now - datetime.timedelta(days=u.get("days_ago", 5), hours=1)
        if u.get("played"):
            ud.update(played=True, play_count=1, last_played=when)
        if u.get("progress"):
            ud.update(position=int(lib.runtime_of(lib.items[by_key[entry["key"]]]) * float(u["progress"])), last_played=when)
        if u.get("favorite"):
            ud["favorite"] = True
    for entry in manifest.get("series", []):
        u = entry.get("user") or {}
        if not u:
            continue
        sid = by_key[entry["key"]]
        eps = lib.episodes_of(sid)
        when = now - datetime.timedelta(days=u.get("days_ago", 3))
        watched = int(u.get("watched", 0))
        for k, e in enumerate(eps[:watched]):
            lib._ud(e["Id"]).update(played=True, play_count=1, last_played=when + datetime.timedelta(minutes=k))
        if u.get("progress") and watched < len(eps):
            lib._ud(eps[watched]["Id"]).update(position=int(lib.runtime_of(eps[watched]) * float(u["progress"])),
                                               last_played=when + datetime.timedelta(minutes=watched + 1))
        if u.get("favorite"):
            lib._ud(sid)["favorite"] = True


def art_file(item, kind):
    """The file to send for an item's picture of this kind, or None (the server answers 404)."""
    return (item.get("art") or {}).get(kind)


def content_type(path):
    return TYPES.get(os.path.splitext(path)[1].lower(), "application/octet-stream")


def report(lib):
    """GET /__harness/showcase: the credits of the pictures in use, and the manifest's problems."""
    manifest = getattr(lib, "showcase_manifest", None)
    if manifest is None:
        return {"on": False}
    used = sorted({os.path.relpath(p, manifest["_art"]).replace(os.sep, "/")
                   for item in lib.items.values() for p in (item.get("art") or {}).values()})
    credits = [c for c in manifest.get("credits", []) if c.get("file") in used]
    return {"on": True, "items": sum(1 for i in lib.items.values() if i["kind"] in ("movie", "series", "episode")),
            "pictures": used, "credits": credits, "problems": validate(manifest)}
