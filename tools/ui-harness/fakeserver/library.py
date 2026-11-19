"""The fake server's library: generated once from a seed (the same names, ids and artwork every run), the user's
watch state, and the queries FluentFin and the backend make (Jellyfin's DTO shapes).

Views: Movies (movies), Shows (tvshows), Documentaries (movies), Anime (tvshows). Every playable item streams the
same test video, so its runtime is that video's. A few items are fixed "showcase" ones the harness navigates to;
their names sort first in their library, so they are on the first page.
"""

import datetime
import hashlib
import random
import threading
import uuid

TICKS = 10_000_000

ADJECTIVES = ["Silent", "Crimson", "Hidden", "Last", "Broken", "Golden", "Distant", "Frozen", "Burning", "Quiet",
              "Hollow", "Restless", "Electric", "Painted", "Forgotten", "Velvet", "Iron", "Midnight", "Lonely", "Wild",
              "Paper", "Glass", "Northern", "Sleeping", "Wandering", "Bright", "Secret", "Endless", "Falling", "Copper"]
NOUNS = ["Harbor", "Garden", "Signal", "Winter", "River", "Kingdom", "Machine", "Lighthouse", "Orchard", "Station",
         "Mirror", "Frontier", "Archive", "Comet", "Valley", "Promise", "Carnival", "Engine", "Canyon", "Island",
         "Letter", "Empire", "Shadow", "Horizon", "Theory", "Circus", "Tide", "Forest", "Bridge", "Crown"]
PLACES = ["Avalon", "Port Ellery", "Northgate", "Saltmarsh", "Cedar Falls", "Ravenmoor", "Kestrel Bay", "Old Mill",
          "Lowtown", "Brightwater", "Ashford", "Marrow Hill"]
FIRST = ["Ada", "Ben", "Clara", "Dev", "Elena", "Felix", "Grace", "Hugo", "Iris", "Jonah", "Kira", "Leo", "Maya",
         "Nico", "Olive", "Pavel", "Quinn", "Rosa", "Sami", "Tess", "Umar", "Vera", "Wes", "Yara", "Zane"]
LAST = ["Abbott", "Brandt", "Castillo", "Dorsey", "Ellison", "Fujita", "Garrow", "Hale", "Ibarra", "Jensen",
        "Kowalski", "Lindqvist", "Moreau", "Nakamura", "Okafor", "Petrov", "Quill", "Rasmussen", "Sato", "Thorne",
        "Ueda", "Varga", "Whitlock", "Xu", "Yilmaz", "Zielinski"]
GENRES = ["Action", "Adventure", "Animation", "Comedy", "Crime", "Drama", "Family", "Fantasy", "History", "Horror",
          "Mystery", "Romance", "Science Fiction", "Thriller", "War", "Western"]
DOC_GENRES = ["Documentary", "History", "Nature", "Science", "Music", "Sport"]
TAGS = ["based on novel", "time travel", "heist", "coming of age", "road trip", "found family", "space",
        "small town", "revenge", "dystopia", "friendship", "survival"]
MOVIE_RATINGS = ["G", "PG", "PG-13", "R", "NR"]
TV_RATINGS = ["TV-Y7", "TV-PG", "TV-14", "TV-MA"]
STUDIOS = ["Northlight Pictures", "Blue Kite Films", "Harborview Studios", "Paper Moon", "Ironwood Media",
           "Studio Kestrel", "Lantern House", "Seventh Wave"]

VIEWS = [  # name, collection type, how many top-level items
    ("Movies", "movies", 320),
    ("Shows", "tvshows", 72),
    ("Documentaries", "movies", 48),
    ("Anime", "tvshows", 28),
]

# Nixie 1.3.1: the showcase movie's (fake) YouTube trailer id
TRAILER_ID = "hArNeSs0001"

SHOWCASE = {"movie": "A Quiet Harbor", "series": "A Signal in Winter", "anime": "A Comet Over Avalon",
            "documentary": "A Field Guide to Rivers"}


def _id(*parts):
    return hashlib.md5(("ffh:" + ":".join(str(p) for p in parts)).encode("utf-8")).hexdigest()


def _tag(item_id, kind):
    return hashlib.md5(("tag:%s:%s" % (item_id, kind)).encode()).hexdigest()


def norm_id(value):
    """An id as the server keeps it: 32 lower-case hex digits, from N or D format."""
    return (value or "").replace("-", "").lower()


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%S.0000000Z")


class Library:
    def __init__(self, server_id, user_id, runtime_ticks, seed=20261115, now=None):
        self.server_id = server_id
        self.user_id = user_id
        self.runtime = runtime_ticks
        #: how long the end credits (the Outro segment) run, to the end; POST /__harness/segments changes it
        self.outro_seconds = 40
        self.now = now or datetime.datetime(2026, 11, 14, 20, 0, 0)
        self.lock = threading.RLock()
        self.items = {}          # id -> item dict
        self.children = {}       # id -> [child ids] in order
        self.views = []          # view ids
        self.people = {}         # id -> person item
        self.user = {}           # item id -> user data dict (played, position, favorite, play_count, last_played)
        self.rng = random.Random(seed)
        self._names = set()
        self._build()

    # ---------------------------------------------------------------- generation
    def _name(self, kind):
        rng = self.rng
        for _ in range(1000):
            pattern = rng.randrange(6)
            a, n, n2, p = rng.choice(ADJECTIVES), rng.choice(NOUNS), rng.choice(NOUNS), rng.choice(PLACES)
            name = ["The %s %s" % (a, n), "%s of %s" % (n, p), "%s %ss" % (a, n), "Return to %s" % p,
                    "The %s and the %s" % (n, n2), "%s %s" % (a, p)][pattern]
            if kind == "documentary":
                name = rng.choice(["Inside the %s", "The Story of the %s", "Life on the %s", "Chasing the %s"]) % n + \
                    (" (Part %d)" % rng.randint(2, 3) if rng.random() < 0.1 else "")
            if name not in self._names and not name.startswith("A "):
                self._names.add(name)
                return name
        raise RuntimeError("ran out of names")

    def _overview(self, name, kind):
        rng = self.rng
        who = "%s %s" % (rng.choice(FIRST), rng.choice(LAST))
        what = rng.choice(["a missing map", "an old promise", "a stolen engine", "a letter never sent",
                           "the last ferry", "a broken treaty", "a voice on the radio", "a forgotten song"])
        where = rng.choice(PLACES)
        if kind == "documentary":
            return "A close look at %s, filmed over four seasons around %s." % (name.lower(), where)
        return ("When %s finds %s in %s, a quiet life turns into a race against time. %s follows the people who "
                "cannot let it go, and what it costs them." % (who, what, where, name))

    def _person(self):
        rng = self.rng
        name = "%s %s" % (rng.choice(FIRST), rng.choice(LAST))
        pid = _id("person", name)
        if pid not in self.people:
            self.people[pid] = {"Id": pid, "Name": name, "Type": "Person", "kind": "person", "created": self.now,
                                "parent": None, "view": None}
            self.items[pid] = self.people[pid]
        return self.people[pid]

    def _common(self, item_id, kind, name, parent, view, created, year, genres, rating):
        rng = self.rng
        cast = []
        for i in range(rng.randint(4, 9)):
            person = self._person()
            cast.append({"Name": person["Name"], "Id": person["Id"], "Role": "%s %s" % (rng.choice(FIRST), rng.choice(LAST)),
                         "Type": "Actor" if i < 6 else rng.choice(["Director", "Writer", "Producer"]),
                         "PrimaryImageTag": _tag(person["Id"], "Primary")})
        return {"Id": item_id, "kind": kind, "Name": name, "parent": parent, "view": view, "created": created,
                "year": year, "premiere": datetime.datetime(year, rng.randint(1, 12), rng.randint(1, 28)),
                "genres": genres, "tags": rng.sample(TAGS, rng.randint(0, 3)), "rating": rating,
                "community": round(rng.uniform(4.8, 9.1), 1), "critic": rng.randint(28, 99),
                "people": cast, "studios": rng.sample(STUDIOS, rng.randint(1, 2)),
                "overview": self._overview(name, kind), "tagline": rng.choice(
                    ["Some doors stay open.", "Nothing stays buried.", "Every road ends somewhere.",
                     "The tide always returns.", "Listen closely.", ""])}

    def rename_views(self, renames):
        """The harness renames libraries (POST /__harness/libraries): the ids of the views renamed."""
        changed = []
        for vid in self.views:
            new = renames.get(self.items[vid]["Name"])
            if new:
                self.items[vid]["Name"] = str(new)
                changed.append(vid)
        return changed

    def add_views(self, names):
        """The harness adds empty libraries of movies (POST /__harness/libraries): their ids."""
        added = []
        for name in names:
            vid = _id("view", name)
            if vid in self.items:
                continue
            self.items[vid] = {"Id": vid, "kind": "view", "Name": name, "collection": "movies", "parent": None,
                               "view": vid, "created": self.now}
            self.views.append(vid)
            self.children[vid] = []
            added.append(vid)
        return added

    def _build(self):
        rng = self.rng
        start = self.now - datetime.timedelta(days=3 * 365)
        span = (self.now - start).total_seconds()
        for view_name, ctype, count in VIEWS:
            vid = _id("view", view_name)
            self.items[vid] = {"Id": vid, "kind": "view", "Name": view_name, "collection": ctype, "parent": None,
                               "view": vid, "created": start}
            self.views.append(vid)
            self.children[vid] = []
            doc = view_name == "Documentaries"
            for n in range(count):
                showcase = None
                if n == 0:
                    showcase = {"Movies": "movie", "Shows": "series", "Anime": "anime",
                                "Documentaries": "documentary"}[view_name]
                name = SHOWCASE[showcase] if showcase else self._name("documentary" if doc else ctype)
                created = start + datetime.timedelta(seconds=rng.uniform(0, span))
                if showcase:
                    created = self.now - datetime.timedelta(days=2 + len(self.views))
                year = rng.randint(1978, 2026)
                if ctype == "movies":
                    mid = _id("movie", view_name, name)
                    genres = rng.sample(DOC_GENRES if doc else GENRES, rng.randint(1, 3))
                    if doc and "Documentary" not in genres:
                        genres.insert(0, "Documentary")
                    item = self._common(mid, "movie", name, vid, vid, created, year, genres, rng.choice(MOVIE_RATINGS))
                    item["showcase"] = showcase
                    self.items[mid] = item
                    self.children[vid].append(mid)
                else:
                    self._series(vid, view_name, name, created, year, showcase)
            self.children[vid].sort(key=lambda i: self.sort_name(self.items[i]))
        self._seed_user_data()

    def _series(self, vid, view_name, name, created, year, showcase):
        rng = self.rng
        sid = _id("series", view_name, name)
        genres = rng.sample(["Animation"] + GENRES if view_name == "Anime" else GENRES, rng.randint(1, 3))
        if view_name == "Anime" and "Animation" not in genres:
            genres.insert(0, "Animation")
        series = self._common(sid, "series", name, vid, vid, created, year, genres, rng.choice(TV_RATINGS))
        series["showcase"] = showcase
        series["status"] = rng.choice(["Continuing", "Ended"])
        self.items[sid] = series
        self.children[vid].append(sid)
        self.children[sid] = []
        seasons = 3 if showcase else rng.choice([1, 1, 2, 2, 3, 3, 4, 5, 6])
        for s in range(1, seasons + 1):
            season_id = _id("season", sid, s)
            season_year = min(2026, year + s - 1)
            self.items[season_id] = {"Id": season_id, "kind": "season", "Name": "Season %d" % s, "parent": sid,
                                     "view": vid, "series": sid, "index": s, "created": created, "year": season_year,
                                     "premiere": datetime.datetime(season_year, 9, 1), "overview": "",
                                     "genres": [], "tags": [], "rating": series["rating"], "people": [], "studios": []}
            self.children[sid].append(season_id)
            self.children[season_id] = []
            for e in range(1, (8 if showcase else rng.randint(6, 13)) + 1):
                eid = _id("episode", season_id, e)
                ep_name = rng.choice(["The %s", "%s", "A %s Apart", "Under the %s", "After the %s",
                                      "The %s Problem"]) % rng.choice(NOUNS)
                ep_created = created + datetime.timedelta(days=(s - 1) * 120 + e * 7)
                if ep_created > self.now:
                    ep_created = self.now - datetime.timedelta(hours=rng.randint(1, 500))
                ep = self._common(eid, "episode", ep_name, season_id, vid, ep_created, season_year, series["genres"],
                                  series["rating"])
                ep.update(series=sid, season=season_id, index=e, parent_index=s,
                          premiere=datetime.datetime(season_year, 9, 1) + datetime.timedelta(days=7 * (e - 1)))
                ep["people"] = ep["people"][:4]
                self.items[eid] = ep
                self.children[season_id].append(eid)

    def _seed_user_data(self):
        rng = random.Random(7)
        movies = [i for i in self.items.values() if i["kind"] == "movie"]
        for m in movies:
            if m.get("showcase"):
                continue
            r = rng.random()
            if r < 0.25:
                self._ud(m["Id"]).update(played=True, play_count=rng.randint(1, 3),
                                         last_played=self.now - datetime.timedelta(days=rng.randint(2, 600)))
            elif r < 0.29:
                self._ud(m["Id"]).update(position=int(self.runtime * rng.uniform(0.15, 0.8)),
                                         last_played=self.now - datetime.timedelta(hours=rng.randint(2, 300)))
            if rng.random() < 0.08:
                self._ud(m["Id"])["favorite"] = True
        series = [i for i in self.items.values() if i["kind"] == "series"]
        for s in series:
            if s.get("showcase") == "series":
                # S1E1-E3 watched, E4 next up, nothing resumable: playing the showcase episode starts at 0
                eps = self.episodes_of(s["Id"])
                for k, e in enumerate(eps[:3]):
                    self._ud(e["Id"]).update(played=True, play_count=1,
                                             last_played=self.now - datetime.timedelta(days=3, hours=-k))
                continue
            if s.get("showcase") or rng.random() > 0.3:
                continue
            eps = self.episodes_of(s["Id"])
            watched = rng.randint(1, max(1, len(eps) - 1))
            when = self.now - datetime.timedelta(days=rng.randint(1, 60))
            for k, e in enumerate(eps[:watched]):
                self._ud(e["Id"]).update(played=True, play_count=1, last_played=when + datetime.timedelta(minutes=k))
            if rng.random() < 0.35 and watched < len(eps):
                self._ud(eps[watched]["Id"]).update(position=int(self.runtime * rng.uniform(0.2, 0.7)),
                                                    last_played=when + datetime.timedelta(minutes=watched + 1))
            if rng.random() < 0.1:
                self._ud(s["Id"])["favorite"] = True

    def _ud(self, item_id):
        return self.user.setdefault(item_id, {"played": False, "position": 0, "favorite": False, "play_count": 0,
                                              "last_played": None})

    # ---------------------------------------------------------------- lookups
    def get(self, item_id):
        return self.items.get(norm_id(item_id))

    def showcase(self):
        found = {}
        for item in self.items.values():
            if item.get("showcase"):
                found[item["showcase"]] = item
        series = found["series"]
        season = self.items[self.children[series["Id"]][0]]
        episode = self.items[self.children[season["Id"]][0]]
        return {"movie": {"id": found["movie"]["Id"], "name": found["movie"]["Name"], "library": "Movies"},
                "series": {"id": series["Id"], "name": series["Name"], "library": "Shows"},
                "season": {"id": season["Id"], "name": season["Name"]},
                "episode": {"id": episode["Id"], "name": episode["Name"], "index": 1},
                "anime": {"id": found["anime"]["Id"], "name": found["anime"]["Name"], "library": "Anime"},
                "documentary": {"id": found["documentary"]["Id"], "name": found["documentary"]["Name"],
                                "library": "Documentaries"},
                "views": [self.items[v]["Name"] for v in self.views]}

    def episodes_of(self, series_id, season_id=None):
        out = []
        for season in self.children.get(series_id, []):
            if season_id and season != season_id:
                continue
            out.extend(self.items[e] for e in self.children.get(season, []))
        return out

    def descendants(self, item_id):
        out = []
        stack = list(reversed(self.children.get(item_id, [])))
        while stack:
            cid = stack.pop()
            out.append(self.items[cid])
            stack.extend(reversed(self.children.get(cid, [])))
        return out

    @staticmethod
    def sort_name(item):
        name = item["Name"]
        # Jellyfin's own sort names: episodes and seasons by their numbers, so SortName lists them in order
        if item["kind"] == "episode":
            return "%03d - %04d - %s" % (item["parent_index"], item["index"], name.lower())
        if item["kind"] == "season":
            return "%04d" % item["index"]
        for article in ("The ", "A ", "An "):
            if name.startswith(article) and item["kind"] in ("movie", "series"):
                # Jellyfin strips leading articles; the showcase names keep theirs ("A ...") to sort first
                return name[len(article):].lower() if article != "A " else " " + name.lower()
        return name.lower()

    # ---------------------------------------------------------------- user data
    def user_data(self, item):
        iid = item["Id"]
        with self.lock:
            ud = dict(self.user.get(iid) or {"played": False, "position": 0, "favorite": False, "play_count": 0,
                                             "last_played": None})
        out = {"PlaybackPositionTicks": ud["position"], "PlayCount": ud["play_count"], "IsFavorite": ud["favorite"],
               "Played": ud["played"], "Key": iid, "ItemId": iid}
        if ud["last_played"]:
            out["LastPlayedDate"] = iso(ud["last_played"])
        if ud["position"] and self.runtime:
            out["PlayedPercentage"] = round(100.0 * ud["position"] / self.runtime, 2)
        if item["kind"] in ("series", "season"):
            eps = self.episodes_of(item["series"] if item["kind"] == "season" else iid,
                                   iid if item["kind"] == "season" else None)
            unplayed = sum(1 for e in eps if not (self.user.get(e["Id"]) or {}).get("played"))
            out["UnplayedItemCount"] = unplayed
            out["Played"] = unplayed == 0
        return out

    def report(self, item_id, position_ticks=None, stopped=False):
        """A playback report: the resume point, and played once past 90 % (Jellyfin's default)."""
        item = self.get(item_id)
        if not item:
            return None
        with self.lock:
            ud = self._ud(item["Id"])
            ud["last_played"] = _utcnow()
            if position_ticks is not None:
                ud["position"] = max(0, int(position_ticks))
            if stopped and self.runtime and ud["position"] >= 0.9 * self.runtime:
                ud.update(played=True, position=0, play_count=ud["play_count"] + 1)
        return item

    def set_played(self, item_id, played):
        item = self.get(item_id)
        if not item:
            return None
        targets = [item] + (self.descendants(item["Id"]) if item["kind"] in ("series", "season") else [])
        with self.lock:
            for t in targets:
                if t["kind"] in ("movie", "episode"):
                    ud = self._ud(t["Id"])
                    ud.update(played=played, position=0, last_played=_utcnow() if played else None,
                              play_count=ud["play_count"] + 1 if played else ud["play_count"])
        return item

    def set_position(self, item_id, ticks):
        """The user's resume point, as Jellyfin's POST /UserItems/{id}/UserData sets it (0: out of Continue Watching)."""
        item = self.get(item_id)
        if item and item["kind"] in ("movie", "episode"):
            with self.lock:
                self._ud(item["Id"])["position"] = max(0, int(ticks))
        return item

    def set_favorite(self, item_id, favorite):
        item = self.get(item_id)
        if item:
            with self.lock:
                self._ud(item["Id"])["favorite"] = favorite
        return item

    # ---------------------------------------------------------------- DTOs
    def image_tags(self, item):
        iid, kind = item["Id"], item["kind"]
        tags = {"Primary": _tag(iid, "Primary")}
        if kind in ("movie", "series"):
            tags.update(Logo=_tag(iid, "Logo"), Thumb=_tag(iid, "Thumb"), Banner=_tag(iid, "Banner"))
        return tags

    def dto(self, item, detail=False):
        from imagegen import blurhash_cached as blurhash
        iid, kind = item["Id"], item["kind"]
        types = {"view": "CollectionFolder", "movie": "Movie", "series": "Series", "season": "Season",
                 "episode": "Episode", "person": "Person"}
        d = {"Name": item["Name"], "ServerId": self.server_id, "Id": iid, "Etag": _tag(iid, "etag")[:16],
             "DateCreated": iso(item["created"]), "CanDelete": False, "CanDownload": kind in ("movie", "episode"),
             "SortName": self.sort_name(item), "Type": types[kind], "LocationType": "FileSystem",
             "IsFolder": kind in ("view", "series", "season"), "ImageTags": {}, "BackdropImageTags": [],
             "ImageBlurHashes": {}}
        if kind == "person":
            d.update(ImageTags={"Primary": _tag(iid, "Primary")}, PrimaryImageAspectRatio=2 / 3,
                     ImageBlurHashes={"Primary": {_tag(iid, "Primary"): blurhash(iid + "Primary")}})
            return d
        if kind == "view":
            d.update(CollectionType=item["collection"], ImageTags={"Primary": _tag(iid, "Primary")},
                     PrimaryImageAspectRatio=16 / 9, ChildCount=len(self.children.get(iid, [])),
                     ImageBlurHashes={"Primary": {_tag(iid, "Primary"): blurhash(iid + "Primary")}},
                     Path="/media/%s" % item["Name"].lower(), ParentId=None)
            return d
        tags = self.image_tags(item)
        d["ImageTags"] = tags
        d["ImageBlurHashes"] = {k: {v: blurhash(iid + k)} for k, v in tags.items()}
        d["ParentId"] = item["parent"]
        d["UserData"] = self.user_data(item)
        d["PremiereDate"] = iso(item["premiere"])
        d["ProductionYear"] = item["year"]
        d["OfficialRating"] = item["rating"]
        d["Genres"] = item["genres"]
        d["GenreItems"] = [{"Name": g, "Id": _id("genre", g)} for g in item["genres"]]
        d["Tags"] = item["tags"]
        d["Overview"] = item["overview"]
        if kind in ("movie", "series", "episode"):
            d["CommunityRating"] = item["community"]
            d["BackdropImageTags"] = [_tag(iid, "Backdrop")] if kind != "episode" else []
            if d["BackdropImageTags"]:
                d["ImageBlurHashes"]["Backdrop"] = {d["BackdropImageTags"][0]: blurhash(iid + "Backdrop")}
        if kind == "movie":
            d.update(CriticRating=item["critic"], PrimaryImageAspectRatio=2 / 3, MediaType="Video",
                     VideoType="VideoFile", RunTimeTicks=self.runtime, Container="mkv", HasSubtitles=True,
                     Width=1280, Height=720, IsHD=True)
        elif kind == "series":
            d.update(PrimaryImageAspectRatio=2 / 3, Status=item["status"], AirDays=["Sunday"],
                     RunTimeTicks=self.runtime, ChildCount=len(self.children.get(iid, [])),
                     RecursiveItemCount=len(self.episodes_of(iid)), MediaType="Unknown")
            if item["status"] == "Ended":
                d["EndDate"] = iso(datetime.datetime(min(2026, item["year"] + 4), 6, 1))
        elif kind in ("season", "episode"):
            series = self.items[item["series"]]
            d.update(SeriesId=series["Id"], SeriesName=series["Name"], SeriesPrimaryImageTag=_tag(series["Id"], "Primary"),
                     ParentBackdropItemId=series["Id"], ParentBackdropImageTags=[_tag(series["Id"], "Backdrop")],
                     ParentLogoItemId=series["Id"], ParentLogoImageTag=_tag(series["Id"], "Logo"),
                     ParentThumbItemId=series["Id"], ParentThumbImageTag=_tag(series["Id"], "Thumb"),
                     IndexNumber=item["index"])
            if kind == "season":
                d.update(PrimaryImageAspectRatio=2 / 3, ChildCount=len(self.children.get(iid, [])), MediaType="Unknown")
            else:
                season = self.items[item["season"]]
                d.update(SeasonId=season["Id"], SeasonName=season["Name"], ParentIndexNumber=item["parent_index"],
                         PrimaryImageAspectRatio=16 / 9, MediaType="Video", VideoType="VideoFile",
                         RunTimeTicks=self.runtime, Container="mkv", HasSubtitles=True, Width=1280, Height=720)
        if detail:
            d["People"] = item["people"]
            d["Studios"] = [{"Name": s, "Id": _id("studio", s)} for s in item["studios"]]
            d["ProviderIds"] = {"Tmdb": str(int(iid[:6], 16)), "Imdb": "tt%07d" % (int(iid[6:12], 16) % 10000000)}
            d["ExternalUrls"] = []
            d["Taglines"] = [item["tagline"]] if item.get("tagline") else []
            # Nixie 1.3.1: the showcase movie has a YouTube trailer (the app plays it in its theatre window from the fake
            # server's own page, GET /__harness/trailer/<id>: no run ever contacts YouTube)
            d["RemoteTrailers"] = [{"Url": "https://www.youtube.com/watch?v=" + TRAILER_ID, "Name": "Trailer"}]                 if item.get("showcase") == "movie" else []
            d["LockedFields"] = []
            d["LockData"] = False
            d["Path"] = self.path(item)
            if kind in ("movie", "episode"):
                d["Chapters"] = self.chapters(item)
        return d

    def path(self, item):
        kind = item["kind"]
        if kind == "movie":
            view = self.items[item["view"]]["Name"].lower()
            return "/media/%s/%s (%d)/%s (%d).mkv" % (view, item["Name"], item["year"], item["Name"], item["year"])
        if kind == "episode":
            series = self.items[item["series"]]
            return "/media/%s/%s/Season %02d/%s S%02dE%02d.mkv" % (
                self.items[item["view"]]["Name"].lower(), series["Name"], item["parent_index"], series["Name"],
                item["parent_index"], item["index"])
        if kind == "series":
            return "/media/%s/%s" % (self.items[item["view"]]["Name"].lower(), item["Name"])
        return ""

    def chapters(self, item):
        names = ["Opening", "The Arrival", "Complications", "Turning Point", "Confrontation", "Credits"]
        step = self.runtime // len(names)
        return [{"StartPositionTicks": k * step, "Name": n, "ImageTag": _tag(item["Id"], "Chapter%d" % k),
                 "ImageDateModified": iso(item["created"])} for k, n in enumerate(names)]

    def segments(self, item):
        """Intro and credits as MediaSegments: episodes have both, movies only credits."""
        rt = self.runtime
        out = []
        if item["kind"] == "episode":
            out.append({"Id": _id("seg", item["Id"], "intro"), "ItemId": item["Id"], "Type": "Intro",
                        "StartTicks": 10 * TICKS, "EndTicks": 40 * TICKS})
        if item["kind"] in ("episode", "movie"):
            out.append({"Id": _id("seg", item["Id"], "outro"), "ItemId": item["Id"], "Type": "Outro",
                        "StartTicks": max(0, rt - int(self.outro_seconds * TICKS)), "EndTicks": rt})
        return out

    # ---------------------------------------------------------------- queries
    def query(self, q):
        """GET /Items: q holds lower-cased keys with lists of values."""
        def one(key, default=None):
            v = q.get(key)
            return v[0] if v else default

        def many(key):
            return [x for v in q.get(key, []) for x in v.split(",") if x]

        def flag(key):
            v = one(key)
            return None if v is None else v.lower() == "true"

        types = {t.lower() for t in many("includeitemtypes")}
        exclude = {t.lower() for t in many("excludeitemtypes")}
        recursive = flag("recursive") or False
        parent = norm_id(one("parentid"))
        ids = [norm_id(i) for i in many("ids")]
        term = (one("searchterm") or "").lower()

        if ids:
            pool = [self.items[i] for i in ids if i in self.items]
        elif parent:
            if parent not in self.items:
                return {"Items": [], "TotalRecordCount": 0, "StartIndex": 0}
            pool = self.descendants(parent) if recursive else [self.items[c] for c in self.children.get(parent, [])]
        elif recursive or term:
            pool = [d for v in self.views for d in self.descendants(v)] + list(self.people.values())
        else:
            pool = [self.items[v] for v in self.views]

        kind_of = {"movie": "movie", "series": "series", "season": "season", "episode": "episode",
                   "person": "person", "collectionfolder": "view"}
        if types:
            wanted = {kind_of.get(t) for t in types}
            pool = [i for i in pool if i["kind"] in wanted]
        elif term:
            pool = [i for i in pool if i["kind"] in ("movie", "series", "episode", "person")]
        else:
            pool = [i for i in pool if i["kind"] != "person"]
        if exclude:
            gone = {kind_of.get(t) for t in exclude}
            pool = [i for i in pool if i["kind"] not in gone]
        if term:
            pool = [i for i in pool if term in i["Name"].lower()]
        for key, field in (("genres", "genres"), ("tags", "tags")):
            values = {x for v in q.get(key, []) for x in v.split("|") if x}
            if values:
                pool = [i for i in pool if values & set(i.get(field) or [])]
        years = {int(y) for y in many("years") if y.isdigit()}
        if years:
            pool = [i for i in pool if i.get("year") in years]
        ratings = set(x for v in q.get("officialratings", []) for x in v.split("|") if x)
        if ratings:
            pool = [i for i in pool if i.get("rating") in ratings]
        filters = {f.lower() for f in many("filters")}
        if flag("isfavorite") or "isfavorite" in filters:
            pool = [i for i in pool if (self.user.get(i["Id"]) or {}).get("favorite")]
        if flag("isplayed") is True or "isplayed" in filters:
            pool = [i for i in pool if self.user_data(i).get("Played")]
        if flag("isplayed") is False or "isunplayed" in filters:
            pool = [i for i in pool if not self.user_data(i).get("Played")]
        if "isresumable" in filters:
            pool = [i for i in pool if (self.user.get(i["Id"]) or {}).get("position")]
        if many("mediatypes"):
            if "video" in {m.lower() for m in many("mediatypes")}:
                pool = [i for i in pool if i["kind"] in ("movie", "episode")]
        if one("namestartswith"):
            prefix = one("namestartswith").lower()
            pool = [i for i in pool if self.sort_name(i).lstrip().startswith(prefix)]
        if not (all(i["kind"] == "view" for i in pool) and not many("sortby")):   # views keep their order
            pool = self.sort(pool, many("sortby"), (one("sortorder") or "Ascending").split(",")[0])
        total = len(pool)
        start = self.int_of(one("startindex"), 0)
        limit = self.int_of(one("limit"), None)
        page = pool[start:start + limit if limit is not None else None]
        return {"Items": [self.dto(i) for i in page], "TotalRecordCount": total, "StartIndex": start}

    @staticmethod
    def int_of(value, default):
        try:
            return int(value)
        except (TypeError, ValueError):
            return default

    def sort(self, pool, keys, order):
        keys = [k.lower() for k in keys] or ["sortname"]

        def key(i):
            ud = self.user.get(i["Id"]) or {}
            out = []
            for k in keys:
                if k in ("sortname", "name"):
                    out.append(self.sort_name(i))
                elif k == "datecreated":
                    out.append(i["created"].timestamp())
                elif k == "premieredate":
                    out.append(i.get("premiere", i["created"]).timestamp())
                elif k == "productionyear":
                    out.append(i.get("year") or 0)
                elif k == "communityrating":
                    out.append(i.get("community") or 0)
                elif k == "criticrating":
                    out.append(i.get("critic") or 0)
                elif k == "dateplayed" or k == "datelastcontentadded":
                    out.append((ud.get("last_played") or datetime.datetime(1970, 1, 1)).timestamp())
                elif k == "playcount":
                    out.append(ud.get("play_count", 0))
                elif k in ("indexnumber", "parentindexnumber"):
                    out.append((i.get("parent_index") or 0, i.get("index") or 0))
                elif k == "random":
                    out.append(random.random())
                else:
                    out.append(0)
            return out
        return sorted(pool, key=key, reverse=order.lower().startswith("desc"))

    def resume(self, limit=12, media_video=True, parent=None):
        pool = [i for i in self.items.values() if i["kind"] in ("movie", "episode")
                and (self.user.get(i["Id"]) or {}).get("position")]
        if parent:
            allowed = {d["Id"] for d in self.descendants(norm_id(parent))}
            pool = [i for i in pool if i["Id"] in allowed]
        pool.sort(key=lambda i: self.user[i["Id"]].get("last_played") or datetime.datetime(1970, 1, 1), reverse=True)
        return {"Items": [self.dto(i) for i in pool[:limit]], "TotalRecordCount": len(pool), "StartIndex": 0}

    def next_up(self, limit=24, series_id=None):
        rows = []
        for s in self.items.values():
            if s["kind"] != "series" or (series_id and s["Id"] != norm_id(series_id)):
                continue
            eps = self.episodes_of(s["Id"])
            last = None
            for k, e in enumerate(eps):
                if (self.user.get(e["Id"]) or {}).get("played"):
                    last = k
            if last is None or last + 1 >= len(eps):
                if series_id and last is None and eps:
                    rows.append((datetime.datetime(1970, 1, 1), eps[0]))
                continue
            nxt = eps[last + 1]
            if (self.user.get(nxt["Id"]) or {}).get("position"):
                continue   # resumable: in Continue Watching instead (EnableResumable=false)
            when = (self.user.get(eps[last]["Id"]) or {}).get("last_played") or datetime.datetime(1970, 1, 1)
            rows.append((when, nxt))
        rows.sort(key=lambda r: r[0], reverse=True)
        return {"Items": [self.dto(e) for _, e in rows[:limit]], "TotalRecordCount": len(rows), "StartIndex": 0}

    def latest(self, parent, limit=16):
        view = self.get(parent)
        if not view or view["kind"] != "view":
            return []
        kids = [self.items[c] for c in self.children[view["Id"]]]
        if view["collection"] == "tvshows":
            def newest(s):
                eps = self.episodes_of(s["Id"])
                return max((e["created"] for e in eps), default=s["created"])
            kids.sort(key=newest, reverse=True)
        else:
            kids.sort(key=lambda i: i["created"], reverse=True)
        return [self.dto(i) for i in kids[:limit]]

    def similar(self, item_id, limit=12):
        item = self.get(item_id)
        if not item or not item.get("view"):
            return {"Items": [], "TotalRecordCount": 0, "StartIndex": 0}
        if item["kind"] in ("season", "episode"):
            item = self.items[item["series"]]
        peers = [self.items[c] for c in self.children[item["view"]] if c != item["Id"]]
        genres = set(item.get("genres") or [])
        peers.sort(key=lambda p: (-len(genres & set(p.get("genres") or [])), self.sort_name(p)))
        return {"Items": [self.dto(p) for p in peers[:limit]], "TotalRecordCount": len(peers), "StartIndex": 0}

    def filters(self, parent):
        pool = self.descendants(norm_id(parent)) if parent else [i for i in self.items.values() if i["kind"] != "person"]
        pool = [i for i in pool if i["kind"] in ("movie", "series")]
        return {"Genres": sorted({g for i in pool for g in i["genres"]}),
                "Tags": sorted({t for i in pool for t in i["tags"]}),
                "OfficialRatings": sorted({i["rating"] for i in pool}),
                "Years": sorted({i["year"] for i in pool}, reverse=True)}

    def seasons(self, series_id):
        s = self.get(series_id)
        kids = [self.items[c] for c in self.children.get(s["Id"], [])] if s else []
        return {"Items": [self.dto(k) for k in kids], "TotalRecordCount": len(kids), "StartIndex": 0}

    def episodes(self, series_id, season_id=None, start=0, limit=None):
        s = self.get(series_id)
        eps = self.episodes_of(s["Id"], norm_id(season_id) or None) if s else []
        page = eps[start:start + limit if limit else None]
        return {"Items": [self.dto(e) for e in page], "TotalRecordCount": len(eps), "StartIndex": start}

    def counts(self):
        kinds = {}
        for i in self.items.values():
            kinds[i["kind"]] = kinds.get(i["kind"], 0) + 1
        return kinds


def _utcnow():
    return datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)


def new_guid():
    return uuid.uuid4().hex
