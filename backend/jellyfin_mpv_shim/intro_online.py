"""fluentfin-mpv: opt-in online intro lookups (the intro-sources research note (repo root, docs, research)).

Sources: TheIntroDB (``api.theintrodb.org/v4/media``, no key) and, for anime,
AniSkip (``api.aniskip.com/v2``, by MyAnimeList id). **Not** SkipMe.db: its
notice restricts the data to its Jellyfin plugin.

What leaves the PC, and only when the user turned lookups on: one GET with
the show's public provider id (TheIntroDB: TVDB, else TMDB, else IMDb;
AniSkip: MAL), the season, the episode and the runtime, and a plain
``Seaglass/<version>`` User-Agent. Never the server address, user, token,
file path or any title.

TheIntroDB's terms (https://theintrodb.org/docs/terms, 4.3 and 6.2) shape
this module:

* **One episode at a time.** `lookup` is called for the item being played,
  and nothing here ever asks for a season, the library or "the next few".
* **Session-only cache.** Responses are kept in `_CACHE`, a dict in memory;
  nothing from TheIntroDB is ever written to disk.
* **Rate limits.** A 429 pauses that source until its Retry-After (default
  300 s, at most a day); nothing is retried inside a lookup.
* **Fail quietly, never delay playback.** About 3 s timeout, every failure
  is a log line at debug level, and the caller runs this off the playback
  path (`intro_sources.augment` starts a thread).
"""

import json
import logging
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

log = logging.getLogger("intro_online")

TIMEOUT = 3.0
THEINTRODB = "https://api.theintrodb.org/v4/media"
ANISKIP = "https://api.aniskip.com/v2/skip-times/%d/%d"
ARM = "https://arm.haglund.dev/api/v2/"

#: TheIntroDB's segment names -> Jellyfin's MediaSegmentType.
_INTRODB_TYPES = {"intro": "Intro", "recap": "Recap", "credits": "Outro",
                  "preview": "Preview"}
_ANISKIP_TYPES = {"op": "Intro", "mixed-op": "Intro", "ed": "Outro",
                  "mixed-ed": "Outro", "recap": "Recap"}

_DEFAULT_PAUSE = 300.0
_MAX_PAUSE = 86400.0

_lock = threading.Lock()
#: Session-only: key -> list of (type, start, end) or [] for "nothing there".
_CACHE = {}
#: source -> monotonic time before which it is not asked.
_PAUSED = {}
#: series id -> its provider ids / genres (an answer from the user's own
#: server, not from an intro service).
_SERIES = {}


def reset():
    """Forget everything held in memory (tests, "Forget" in Settings)."""
    with _lock:
        _CACHE.clear()
        _PAUSED.clear()
        _SERIES.clear()


class Rejected(Exception):
    """The service answered 429."""

    def __init__(self, pause):
        super().__init__("rate limited")
        self.pause = pause


def user_agent(version):
    return "Seaglass/%s" % (version or "1")


def http_get(url, headers, timeout=TIMEOUT):
    """``(status, headers, body bytes)``; HTTP errors are answers, not
    exceptions; anything else raises."""
    request = urllib.request.Request(url, headers=headers, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, dict(response.headers), response.read(
                1_000_000)
    except urllib.error.HTTPError as error:
        try:
            body = error.read(1_000_000)
        except Exception:
            body = b""
        return error.code, dict(error.headers or {}), body


def _retry_after(headers):
    for name, value in (headers or {}).items():
        if name.lower() == "retry-after":
            try:
                return min(_MAX_PAUSE, max(1.0, float(value)))
            except (TypeError, ValueError):
                break
    return _DEFAULT_PAUSE


def _get_json(source, url, version, fetch, now):
    """The JSON of a 200; None for a 404 / anything else that is not an
    answer; `Rejected` for a 429 (and the source is paused)."""
    if now < _PAUSED.get(source, 0):
        raise Rejected(_PAUSED[source] - now)     # not asked, not cached
    status, headers, body = fetch(
        url, {"Accept": "application/json",
              "User-Agent": user_agent(version)}, TIMEOUT)
    if status == 429:
        pause = _retry_after(headers)
        _PAUSED[source] = now + pause
        raise Rejected(pause)
    if status >= 500:
        raise OSError("server error %d" % status)   # not cached
    if status != 200:
        return None
    return json.loads(body.decode("utf-8"))


def _duration_ms(item):
    ticks = item.get("RunTimeTicks")
    return int(ticks // 10_000) if isinstance(ticks, (int, float)) \
        and ticks > 0 else None


# -- TheIntroDB ----------------------------------------------------------------

def introdb_query(ids, season, episode, duration_ms):
    """The query string of one lookup: the show's id (TVDB, else TMDB, else
    IMDb), the season and episode (episodes only), the runtime. Nothing
    else, ever."""
    params = []
    for key, name in (("Tvdb", "tvdb_id"), ("Tmdb", "tmdb_id"),
                      ("Imdb", "imdb_id")):
        value = ids.get(key)
        if value:
            params.append((name, str(value)))
            break
    else:
        return None
    if season is not None and episode is not None:
        params += [("season", str(season)), ("episode", str(episode))]
    if duration_ms:
        params.append(("duration_ms", str(duration_ms)))
    return urllib.parse.urlencode(params)


def parse_introdb(data, duration_s):
    """``[(type, start, end)]`` seconds: a null start is 0, a null end is
    the media's end, ``0..0`` ("confirmed none") is dropped, times are held
    to the runtime, the first segment of each type is kept."""
    out, seen = [], set()
    for name, kind in _INTRODB_TYPES.items():
        for entry in (data.get(name) or []):
            if not isinstance(entry, dict):
                continue
            start = entry.get("start_ms")
            end = entry.get("end_ms")
            start = 0.0 if start is None else start / 1000.0
            if end is None:
                if not duration_s:
                    continue
                end = duration_s
            else:
                end = end / 1000.0
            if duration_s:
                end = min(end, duration_s)
            if end <= start or kind in seen:
                continue
            seen.add(kind)
            out.append((kind, start, end))
    return out


def lookup_introdb(ids, season, episode, duration_ms, version, fetch, now):
    query = introdb_query(ids, season, episode, duration_ms)
    if query is None:
        return None
    key = ("introdb", query)
    with _lock:
        if key in _CACHE:
            return _CACHE[key]
    data = _get_json("introdb", THEINTRODB + "?" + query, version, fetch, now)
    result = [] if not isinstance(data, dict) else parse_introdb(
        data, duration_ms / 1000.0 if duration_ms else None)
    with _lock:
        _CACHE[key] = result      # memory only: TheIntroDB terms 4.3
    return result


# -- AniSkip -------------------------------------------------------------------

def parse_aniskip(data):
    out, seen = [], set()
    if not isinstance(data, dict) or not data.get("found", True):
        return out
    for entry in data.get("results") or []:
        try:
            kind = _ANISKIP_TYPES.get(entry["skipType"])
            start = float(entry["interval"]["startTime"])
            end = float(entry["interval"]["endTime"])
        except (KeyError, TypeError, ValueError):
            continue
        if kind and end > start and kind not in seen:
            seen.add(kind)
            out.append((kind, start, end))
    return out


def aniskip_url(mal_id, episode, length_s):
    query = [("types", t) for t in ("op", "ed", "mixed-op", "mixed-ed",
                                    "recap")]
    query.append(("episodeLength", "%.3f" % (length_s or 0)))
    return ANISKIP % (mal_id, episode) + "?" + urllib.parse.urlencode(query)


def mal_id_for(ids, season, version, fetch, now):
    """The MyAnimeList id of the season: the one in the provider ids when a
    plugin set it, else ARM's mapping of the show's public id (one GET,
    only the id)."""
    direct = ids.get("MyAnimeList") or ids.get("Mal")
    if direct and str(direct).isdigit():
        return int(direct)
    for key, path in (("AniList", "ids?source=anilist&include=myanimelist&id="),
                      ("AniDB", "ids?source=anidb&include=myanimelist&id=")):
        if ids.get(key):
            data = _cached_json("arm", ARM + path + urllib.parse.quote(
                str(ids[key])), version, fetch, now)
            if isinstance(data, dict) and data.get("myanimelist"):
                return int(data["myanimelist"])
    for key, path, field in (("Tvdb", "thetvdb", "thetvdb-season"),
                             ("Tmdb", "themoviedb", "themoviedb-season")):
        if ids.get(key) and season is not None:
            data = _cached_json("arm", ARM + path + "?id="
                                + urllib.parse.quote(str(ids[key])),
                                version, fetch, now)
            for entry in data if isinstance(data, list) else ():
                if (isinstance(entry, dict) and entry.get(field) == season
                        and entry.get("myanimelist")):
                    return int(entry["myanimelist"])
    return None


def _cached_json(source, url, version, fetch, now):
    key = (source, url)
    with _lock:
        if key in _CACHE:
            return _CACHE[key]
    data = _get_json(source, url, version, fetch, now)
    with _lock:
        _CACHE[key] = data
    return data


def lookup_aniskip(ids, season, episode, duration_ms, version, fetch, now):
    if episode is None:
        return None
    mal = mal_id_for(ids, season, version, fetch, now)
    if mal is None:
        return None
    url = aniskip_url(mal, episode, duration_ms / 1000.0 if duration_ms
                      else 0)
    key = ("aniskip", url)
    with _lock:
        if key in _CACHE:
            return _CACHE[key]
    data = _get_json("aniskip", url, version, fetch, now)
    result = parse_aniskip(data)
    with _lock:
        _CACHE[key] = result
    return result


# -- the entry point -------------------------------------------------------------

def is_anime(ids, genres):
    return bool(ids.get("AniList") or ids.get("AniDB")
                or ids.get("MyAnimeList") or ids.get("Mal")
                or "anime" in {str(g).lower() for g in genres or ()})


def lookup(item, show, version=None, fetch=http_get, missing=None,
           clock=time.monotonic):
    """``[(type, start, end)]`` for ``item`` (the episode or movie being
    played) from TheIntroDB, then AniSkip for anime when something is still
    missing (``missing``: the types still wanted, None = any). ``show`` is
    ``{"ids": {...}, "genres": [...]}`` of the series (an episode's own
    ids are the episode's, not the show's) or of the movie. Blocking: the
    caller runs it off the playback path. Never raises."""
    out = []
    try:
        ids = show.get("ids") or {}
        movie = item.get("Type") == "Movie"
        season = None if movie else item.get("ParentIndexNumber")
        episode = None if movie else item.get("IndexNumber")
        if not movie and (not isinstance(season, int)
                          or not isinstance(episode, int)):
            return []
        duration = _duration_ms(item)
        now = clock()
        found = []
        try:
            found = lookup_introdb(ids, season, episode, duration, version,
                                   fetch, now) or []
        except Rejected:
            pass
        except Exception:
            log.debug("TheIntroDB lookup failed", exc_info=True)
        out = list(found)
        have = {k for k, _s, _e in out}
        wanted = set(missing) if missing is not None else None
        still = [t for t in ("Intro", "Outro", "Recap")
                 if t not in have and (wanted is None or t in wanted)]
        if still and not movie and is_anime(ids, show.get("genres")):
            try:
                for seg in lookup_aniskip(ids, season, episode, duration,
                                          version, fetch, now) or []:
                    if seg[0] not in have:
                        out.append(seg)
            except Rejected:
                pass
            except Exception:
                log.debug("AniSkip lookup failed", exc_info=True)
    except Exception:
        log.debug("online intro lookup failed", exc_info=True)
    return out


def series_info(video):
    """``{"ids", "genres"}`` of the show: from the user's own server (the
    series item; kept in memory), or the item itself for a movie."""
    item = getattr(video, "item", None) or {}
    if item.get("Type") == "Movie":
        return {"ids": item.get("ProviderIds") or {},
                "genres": item.get("Genres") or []}
    series_id = item.get("SeriesId")
    if not series_id:
        return {"ids": {}, "genres": []}
    with _lock:
        if series_id in _SERIES:
            return _SERIES[series_id]
    series = video.client.jellyfin.get_item(series_id)
    info = {"ids": series.get("ProviderIds") or {},
            "genres": series.get("Genres") or []}
    with _lock:
        _SERIES[series_id] = info
    return info
