"""Nixie 1.6 (smart collections phase 1): the fake server's data for Nixie's collections, as a real server has it
(docs\\research\\smart-collections-data.md, measured on the QA servers 10.11.11 and 12.4.0):

- ``ProviderIds.TmdbCollection`` on the films TMDb files in a collection (six franchises of two to four films);
- with FAKESERVER_COLLECTIONS, the admin's box set "Kestrel Bay Trilogy" carries ``ProviderIds.Tmdb`` = its franchise's
  id, and one owned film of that franchise is left out of it (the Collection page's "Also in your library");
- ``ProductionLocations`` on films (never on shows, as Jellyfin's TMDb provider);
- recurring directors (People with their kinds: three directors of four films each);
- a show left half watched over a month ago ("Unfinished series").

Tags are TMDb-style keywords already (library.TAGS), and Genres and Studios are as before.

The fakes rule: like Jellyfin, ``ProviderIds``, ``Studios``, ``People`` and ``ProductionLocations`` come in an /Items list
only when its ``Fields`` asks for them, and a Fields name the server doesn't know is dropped (the answer is 200 without
it). The generation uses a random source of its own, after everything else, so the rest of the fixture is unchanged.
"""

import datetime
import random

COUNTRIES = (["United States of America"] * 10 + ["United Kingdom"] * 3 + ["France"] * 3 + ["Japan"] * 2 +
             ["Canada", "Germany", "South Korea", "Spain"])
DIRECTORS = ["Ava Abara", "Bruno Okonkwo-Lind", "Celeste Marrow"]
FRANCHISES = [("9101", 3), ("9102", 2), ("9103", 4), ("9104", 2), ("9105", 3), ("9106", 2)]
BOXED_FRANCHISE = "9001"
BOXED_NAME = "Kestrel Bay Trilogy"


def apply(lib, view_id):
    """Adds the smart-collection data to the Movies view (view_id) and its shows' user data."""
    rng = random.Random(1606)
    movies = [lib.items[i] for i in lib.children.get(view_id, []) if lib.items[i]["kind"] == "movie"]
    for film in movies:
        film["countries"] = [rng.choice(COUNTRIES)] + ([rng.choice(COUNTRIES)] if rng.random() < 0.2 else [])
    in_boxes = {m for b in lib.items.values() if b["kind"] == "boxset" and not b.get("playlist")
                for m in lib.children.get(b["Id"], [])}
    # the admin's box set that leaves one owned film of its franchise out
    box = next((b for b in lib.items.values() if b["kind"] == "boxset" and b["Name"] == BOXED_NAME), None)
    loose = [f for f in movies if f["Id"] not in in_boxes and not f.get("showcase")]
    rng.shuffle(loose)
    if box:
        box["tmdb"] = BOXED_FRANCHISE
        for member in lib.children.get(box["Id"], []):
            lib.items[member]["tmdb_collection"] = BOXED_FRANCHISE
        left_out = loose.pop()
        left_out["tmdb_collection"] = BOXED_FRANCHISE
    for tmdb, size in FRANCHISES:
        for film in [loose.pop() for _ in range(size)]:
            film["tmdb_collection"] = tmdb
    # three directors of four films each
    for name in DIRECTORS:
        person = lib._person_named(name)
        for film in rng.sample(movies, 4):
            film["people"] = [p for p in film["people"] if p.get("Type") != "Director"] + [
                {"Name": person["Name"], "Id": person["Id"], "Role": "", "Type": "Director",
                 "PrimaryImageTag": person.get("PrimaryImageTag", "")}]
    # a show left half watched 45 days ago, with episodes to go
    shows = [lib.items[i] for v in lib.views if lib.items[v].get("collection") == "tvshows"
             for i in lib.children.get(v, []) if lib.items[i]["kind"] == "series" and not lib.items[i].get("showcase")]
    for show, days in zip(sorted(shows, key=lambda s: s["Name"])[:2], (45, 70)):
        eps = lib.episodes_of(show["Id"])
        if len(eps) < 3:
            continue
        when = lib.now - datetime.timedelta(days=days)
        for k, ep in enumerate(eps):
            ud = lib._ud(ep["Id"])
            if k < 2:
                ud.update(played=True, play_count=1, last_played=when + datetime.timedelta(minutes=k), position=0)
            else:
                ud.update(played=False, position=0, last_played=None)


def provider_ids(lib, item):
    """The item's ProviderIds as Jellyfin sends them (the box set's Tmdb is its franchise's id)."""
    iid = item["Id"]
    if item["kind"] == "boxset":
        return {"Tmdb": item["tmdb"]} if item.get("tmdb") else {}
    ids = {"Tmdb": str(int(iid[:6], 16)), "Imdb": "tt%07d" % (int(iid[6:12], 16) % 10000000)}
    if item.get("tmdb_collection"):
        ids["TmdbCollection"] = item["tmdb_collection"]
    return ids


def with_fields(lib, d, item, fields):
    """An /Items answer's item: ProviderIds, Studios, People and ProductionLocations only when Fields asks for them."""
    if item["kind"] in ("view", "person"):
        return d
    if "providerids" in fields:
        d["ProviderIds"] = provider_ids(lib, item)
    if "studios" in fields:
        d["Studios"] = [{"Name": s, "Id": lib.studio_id(s)} for s in item.get("studios") or []]
    if "people" in fields:
        d["People"] = list(item.get("people") or [])
    if "productionlocations" in fields and item["kind"] == "movie":
        d["ProductionLocations"] = list(item.get("countries") or [])
    return d


def detail(lib, d, item):
    """The item page's record: its ProviderIds with the franchise, and its countries."""
    d["ProviderIds"] = provider_ids(lib, item)
    if item["kind"] == "movie":
        d["ProductionLocations"] = list(item.get("countries") or [])
    return d
