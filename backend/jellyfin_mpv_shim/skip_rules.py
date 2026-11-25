"""1.6 skip: skip rules -- the parts a show repeats every episode (network
bumpers, promos, what is left of ad breaks) -- and what Nixie learns from the
viewer's own seeks (docs/BRIDGE.md: ``skip_rules``, ``add_skip_rule``,
``forget_skip_rules``, ``answer_skip_suggestion``, ``skip_suggestion``, and
``segments`` with ``source: "rule"``).

One file, ``skip-rules.json`` in the backend's config directory, the backend
its only writer. Keyed per show as ``"<server>:<user>:<series id>"``, so two
users or two servers on one PC never share anything. Per show it holds:

* **rules**: a part to skip every episode, anchored to the episode's start
  (``start``/``end`` in seconds from 0:00) or to its end (``start``/``end``
  in seconds *before* the end, ``start > end >= 0``, since episode lengths
  vary and an end promo sits a fixed distance from the end). Each has an
  ``action`` (``"ask"``, ``"always"`` or null: the Settings choice) and an
  ``origin``: ``"marked"`` (the viewer marked it in the player) or
  ``"learned"`` (said yes to a suggestion).
* **intros**: the one-seek learned intro per season, as 1.4 learned it
  (``learned-intros.json``, migrated once), with 1.5.1's "moves" rule judged
  within the season: the first skip by hand of each episode is kept, and one
  of the same part 10 s or more from an earlier one marks the season
  "moves"; its fixed spot is never offered again.
* **seasons** (1.6.1, the merge review's R2): those kept skips and the
  "moves" mark, per season, apart from the intro itself, so a learned spot
  taken back (two undos, a seek back into it) or relearned keeps the
  season's evidence. Created with the season's first learned intro (a
  season never learned keeps nothing); only Settings' Forget (the intro, the
  show, everything) clears it. Mirrored into the intro entry while there is
  one, as 1.6.0 kept them, so a 1.6.0 backend reading this file still sees
  its marks.
* **candidates**: a viewer's forward seek near the start or the end of an
  episode; the same seek in a second episode asks once "Skip this every
  episode?" (``skip_suggestion``).
* **refused**: "Never for this show".

Beside the shows, ``legacyMigrated`` (1.6.1, the merge review's R3):
``learned-intros.json`` was migrated once, so a copy of it that comes back
(its rename failed, or 1.5.1 ran again after a downgrade) is never migrated
again; a second migration brought forgotten intros back.

Nothing here raises into playback: every entry point catches and logs.
"""

import hashlib
import json
import logging
import os
import threading
import time
import uuid

log = logging.getLogger("skip_rules")

#: The file, in the backend's config directory.
RULES_FILE = "skip-rules.json"
#: 1.4's learned intros, migrated into `RULES_FILE` once.
LEGACY_FILE = "learned-intros.json"
LEGACY_DONE_SUFFIX = ".migrated"

SOURCE_RULE = "rule"
SOURCE_LEARNED = "learned"

ANCHORS = ("start", "end")
RULE_ACTIONS = ("ask", "always")
ORIGINS = ("marked", "learned")

#: The longest part a rule may cover: an ad block, never an act.
MAX_RULE_SECONDS = 600.0

# -- the one-seek learned intro (1.4, kept) ----------------------------------

#: How much earlier than the learned start a later episode's skip is
#: offered: the cold open before the titles is not the same length twice.
START_TOLERANCE = 5.0
#: A skip taken back (a seek back into the range just after it) this many
#: times drops the range.
MAX_UNDOS = 2
#: The seek that teaches: lands this much later than it left (seconds),
#: leaving between 0:00 and 10:00.
LEARN_MIN_JUMP = 30.0
LEARN_MAX_JUMP = 150.0
LEARN_MAX_START = 600.0
#: A backward jump at least this long counts as a seek back.
BACK_MIN_JUMP = 5.0
#: How long after a skip a seek back is "undoing" it.
UNDO_WINDOW = 20.0
#: 1.5.1's "moves" (It's Always Sunny: the intro follows a cold open of a
#: different length each episode), as 1.5.1's review judged it (BH-151-M1):
#: within one season, two skips by hand of the same part this far apart
#: (seconds between the two ranges) mark the season "moves". 1.6 integration:
#: this replaces the skip stream's first form of the rule (a skip starting
#: 10 s outside the learned spot), which took a recap skipped before the
#: intro, or a skip just before the spot, for a moving intro.
MOVES_GAP = 10.0
#: Two skips whose lengths differ by more than this (seconds) are different
#: parts (a recap skipped in one episode, the intro in the next): not compared.
MOVES_SAME_PART = 15.0
#: The skips kept per season to compare a new one with (the latest).
MOVES_KEEP = 8


def same_part(a, b):
    """Whether two skips ``(start, end)`` can be of one part: their lengths
    are `MOVES_SAME_PART` or less apart."""
    return abs((a[1] - a[0]) - (b[1] - b[0])) <= MOVES_SAME_PART


def skips_apart(a, b):
    """Whether two skips' ranges ``(start, end)`` are `MOVES_GAP` or more
    apart (overlapping ranges are not apart at all)."""
    return max(a[0], b[0]) - min(a[1], b[1]) >= MOVES_GAP


def _skips(entry):
    """An intro entry's kept skips (`SkipStore.note_hand_skip`), the usable
    ones; an entry from before them (1.4 to 1.6's first form) has its own
    range as the first."""
    kept = [[float(a), float(b)] for a, b in (
        s for s in entry.get("skips") or ()
        if isinstance(s, list) and len(s) == 2 and _num(s[0]) and _num(s[1]))]
    return kept or [[float(entry["start"]), float(entry["end"])]]

# -- candidates and suggestions ----------------------------------------------

#: A forward seek this long (seconds) near an episode's start or end is a
#: candidate.
CANDIDATE_MIN_JUMP = 10.0
CANDIDATE_MAX_JUMP = 300.0
#: "Near": leaving within this many seconds of the start, or of the end.
CANDIDATE_EDGE = 600.0
#: Two seeks match: starts this close, lengths within `MATCH_LENGTH_SHARE`
#: of each other or `MATCH_LENGTH_SECONDS`, whichever is more.
MATCH_START = 10.0
MATCH_LENGTH_SHARE = 0.10
MATCH_LENGTH_SECONDS = 5.0
#: A candidate with no match after this long, or this many other episodes of
#: the show, is dropped.
CANDIDATE_MAX_AGE = 30 * 24 * 3600
CANDIDATE_MAX_EPISODES = 10
MAX_CANDIDATES = 20
#: Seeks one after the other are one skip (a viewer pressing +10 s four
#: times skipped 40 s): a seek leaving within `BURST_JOIN` seconds of where
#: the last one landed, within `BURST_GAP` seconds of it, continues it; the
#: skip is judged once no seek came for `BURST_GAP` seconds.
BURST_GAP = 4.0
BURST_JOIN = 3.0
#: A burst that lands this close to the episode's end is judged at once.
END_JUDGE = 4.0
#: The smallest jump between two position readings that is a seek (not
#: playback, at up to twice the speed).
SEEK_MIN = 2.0

# -- the margin --------------------------------------------------------------

#: After the app skipped a rule's part, a seek within this many seconds is a
#: correction: where it lands becomes the rule's end.
CORRECTION_WINDOW = 15.0
#: The smallest seek that corrects (a sample's own drift is not one).
CORRECTION_MIN = 1.5
#: How far past a rule's end a correction may land.
CORRECTION_MAX_PAST = 120.0
#: A jump that lands this close to the end of a part the app skips by itself
#: is that skip, not the viewer's.
APP_LANDING = 1.5
#: A seek landing this close to a chapter start is the chapters menu's.
CHAPTER_LANDING = 0.5
#: How long after a Skip press a forward jump is that press.
NOTE_SKIP_WINDOW = 5.0


def _norm_id(value):
    if not isinstance(value, str):
        return None
    text = value.strip().replace("-", "").lower()
    return text or None


def server_key(server_id, server_url=None):
    """The server's part of a key: its id, else (an older UI) a hash of its
    address, never the address itself."""
    sid = _norm_id(server_id)
    if sid:
        return sid
    if isinstance(server_url, str) and server_url.strip():
        digest = hashlib.sha1(server_url.strip().rstrip("/").lower()
                              .encode("utf-8")).hexdigest()[:16]
        return "url-" + digest
    return None


def account_key(server_id, user_id, server_url=None):
    server = server_key(server_id, server_url)
    user = _norm_id(user_id)
    if not server or not user:
        return None
    return "%s:%s" % (server, user)


class Options:
    """What the user chose in Settings > Playback (``set_skip_options``)."""

    def __init__(self):
        #: "Look up intros online" (``onlineLookup``; `intro_sources`).
        self.lookup_online = False
        #: "Learn skips from my seeks" (``learnSkips``, was ``learnIntros``).
        self.learn = True
        #: "When a show has a skip rule": what a rule with no choice of its
        #: own does.
        self.rule_action = "ask"


OPTIONS = Options()

#: The account playing (`set_account`, from ``connect``), or None.
_account = None
_account_lock = threading.Lock()


def set_account(key):
    global _account
    with _account_lock:
        _account = key


def current_account():
    with _account_lock:
        return _account


def series_id(item):
    try:
        return _norm_id(item.get("SeriesId"))
    except Exception:
        return None


def season_of(item):
    try:
        season = item.get("ParentIndexNumber")
        return season if isinstance(season, int) else None
    except Exception:
        return None


def show_key(item, account=None):
    """``"<server>:<user>:<series>"`` for an episode, else None."""
    account = account or current_account()
    series = series_id(item or {})
    if not account or not series:
        return None
    return "%s:%s" % (account, series)


def runtime_of(video):
    """The item's runtime in seconds, or None."""
    for source in (getattr(video, "media_source", None),
                   getattr(video, "item", None)):
        try:
            ticks = (source or {}).get("RunTimeTicks")
        except Exception:
            ticks = None
        if isinstance(ticks, (int, float)) and not isinstance(ticks, bool) \
                and ticks > 0:
            return ticks / 10_000_000
    return None


def _num(value):
    return (isinstance(value, (int, float)) and not isinstance(value, bool)
            and value == value and value not in (float("inf"),
                                                 float("-inf")))


def _valid_rule(rule):
    if not isinstance(rule, dict) or not isinstance(rule.get("id"), str):
        return False
    if rule.get("anchor") not in ANCHORS:
        return False
    start, end = rule.get("start"), rule.get("end")
    if not (_num(start) and _num(end) and start >= 0 and end >= 0):
        return False
    span = (end - start) if rule["anchor"] == "start" else (start - end)
    if not 0 < span <= MAX_RULE_SECONDS:
        return False
    if rule.get("action") not in RULE_ACTIONS + (None,):
        return False
    return rule.get("origin") in ORIGINS


def _valid_intro(entry):
    return (isinstance(entry, dict) and _num(entry.get("start"))
            and _num(entry.get("end")) and entry["end"] > entry["start"])


def _valid_candidate(entry):
    return (isinstance(entry, dict) and isinstance(entry.get("id"), str)
            and entry.get("anchor") in ANCHORS
            and _num(entry.get("start")) and _num(entry.get("end"))
            and isinstance(entry.get("items"), list))


def _kept(skips):
    """The usable ``[start, end]`` pairs of a list read from disk."""
    return [[float(s[0]), float(s[1])] for s in skips or ()
            if isinstance(s, list) and len(s) == 2 and _num(s[0])
            and _num(s[1])][-MOVES_KEEP:]


def _marks_of_entry(entry):
    """A season's record (`_clean_show`'s "seasons") as 1.6.0 kept it, in
    the intro entry: its kept skips (the intro's own range when it has
    none) and its "moves" mark."""
    moves = bool(entry.get("moves"))
    return {"skips": _kept(entry.get("skips")) if moves
            else _skips(entry)[-MOVES_KEEP:], "moves": moves}


#: 1.7.5 (BH-151-m4): the keys of a show entry this backend knows. Any other
#: is a newer backend's (a portable copy beside an older one, or a
#: downgrade): kept as it was read and written back, never dropped, as a
#: 1.5.0 backend dropped 1.5.1's "moves" marks.
_SHOW_KEYS = frozenset(("name", "rules", "intros", "seasons", "candidates",
                        "refused", "episodes", "last_item"))
#: 1.7.5 (BH-151-m4): the same for a season's record, and the file's own.
_SEASON_KEYS = frozenset(("skips", "moves"))
_FILE_KEYS = frozenset(("version", "shows", "legacyMigrated"))
#: The file's format as this backend writes it (a newer one's is kept).
FILE_VERSION = 2


def _unknown(entry, known):
    """``entry``'s keys this backend doesn't know, as read (1.7.5)."""
    return {k: v for k, v in entry.items()
            if isinstance(k, str) and k not in known}


def _clean_show(show):
    """A show entry read from disk, with anything unusable left out. 1.7.5
    (BH-151-m4): keys this backend doesn't know (a newer one's marks) are
    kept as they were (`_SHOW_KEYS`, `_SEASON_KEYS`)."""
    if not isinstance(show, dict):
        return None
    out = {
        "name": show.get("name") if isinstance(show.get("name"), str)
        else None,
        "rules": [r for r in show.get("rules") or [] if _valid_rule(r)],
        "intros": {k: v for k, v in (show.get("intros") or {}).items()
                   if isinstance(k, str) and _valid_intro(v)}
        if isinstance(show.get("intros"), dict) else {},
        "candidates": [c for c in show.get("candidates") or []
                       if _valid_candidate(c)],
        "refused": bool(show.get("refused")),
        "episodes": show.get("episodes") if isinstance(
            show.get("episodes"), int) else 0,
        "last_item": show.get("last_item") if isinstance(
            show.get("last_item"), str) else None,
    }
    # 1.6.1 (R2): the seasons' kept skips and "moves" marks; a season with
    # an intro and no record (a 1.6.0 file, or one a 1.6.0 backend wrote)
    # takes them from the intro entry, where 1.6.0 kept them
    seasons = show.get("seasons") if isinstance(show.get("seasons"),
                                                dict) else {}
    out["seasons"] = {
        k: dict(_unknown(v, _SEASON_KEYS), skips=_kept(v.get("skips")),
                moves=v.get("moves") is True)
        for k, v in seasons.items()
        if isinstance(k, str) and isinstance(v, dict)}
    for season, entry in out["intros"].items():
        if season not in out["seasons"]:
            out["seasons"][season] = _marks_of_entry(entry)
        _mirror(out, season)
    for k, v in _unknown(show, _SHOW_KEYS).items():
        out[k] = v
    return out


def _mirror(show, season):
    """The season's record into its intro entry, if it has one (1.6.0
    reads them there)."""
    entry = show["intros"].get(season)
    marks = show["seasons"].get(season)
    if entry is not None and marks is not None:
        entry["moves"] = marks["moves"]
        entry["skips"] = [list(s) for s in marks["skips"]]


def _empty_show(name=None):
    return {"name": name, "rules": [], "intros": {}, "seasons": {},
            "candidates": [], "refused": False, "episodes": 0,
            "last_item": None}


def _has_content(show):
    return bool(show["rules"] or show["intros"] or show["seasons"]
                or show["candidates"] or show["refused"])


def _kept_on_save(show):
    """1.7.5 (BH-151-m4): what `SkipStore._save` writes: a show with content,
    or with a newer backend's marks only (listed nowhere here, but kept)."""
    return _has_content(show) or any(_unknown(show, _SHOW_KEYS).values())


class SkipStore:
    """`RULES_FILE`: re-read when it changes on disk, written whole and
    atomically. With no path it only lives in memory (tests, and a backend
    with no config directory yet)."""

    def __init__(self, path=None):
        self.path = path
        self._lock = threading.RLock()
        self._shows = {}
        self._stamp = None
        self._migrated = False
        #: 1.6.1 (R3): ``legacyMigrated`` in the file
        self._legacy_done = False
        #: 1.6.1: the file was read once in this process (`_load`)
        self._read_once = False
        #: 1.7.5 (BH-151-m4): the file's keys this backend doesn't know (a
        #: newer one's), and its format if newer: written back as read
        self._other = {}
        self._version = FILE_VERSION

    # -- disk --------------------------------------------------------------

    def _load(self):
        if not self.path:
            return
        try:
            stamp = os.stat(self.path).st_mtime_ns
        except OSError:
            if self._stamp is not None:
                # deleted underneath us: nothing is kept
                self._shows = {}
                self._legacy_done = False
                self._other, self._version = {}, FILE_VERSION
            self._stamp = None
            return
        if stamp == self._stamp:
            return
        try:
            with open(self.path, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            shows = data.get("shows") if isinstance(data, dict) else None
            if not isinstance(shows, dict):
                raise ValueError("no shows")
            self._shows = {}
            for key, value in shows.items():
                show = _clean_show(value) if isinstance(key, str) else None
                if show is not None:
                    self._shows[key] = show
            self._legacy_done = data.get("legacyMigrated") is True
            self._other = _unknown(data, _FILE_KEYS)
            version = data.get("version")
            newer = (isinstance(version, int)
                     and not isinstance(version, bool)
                     and version > FILE_VERSION)
            self._version = version if newer else FILE_VERSION
            if not self._read_once:
                self._read_once = True
                self._release_asked()
        except Exception:
            log.warning("%s is unreadable; skip rules start over (a copy is "
                        "kept beside it)", RULES_FILE, exc_info=True)
            self._shows = {}
            self._legacy_done = False
            self._other, self._version = {}, FILE_VERSION
            try:
                os.replace(self.path, self.path + ".corrupt")
            except OSError:
                pass
            stamp = None
        self._stamp = stamp

    def _release_asked(self):
        """1.6.1 (the 1.6 review's skip queue): a candidate still "asked" as
        the file is first read was asked in an earlier run (the backend
        ended, or was restarted, with the question open): nobody can answer
        that one now, and `release` never ran, so it would never ask again.
        Released as `release` does (written with the next change)."""
        for show in self._shows.values():
            for candidate in show["candidates"]:
                if candidate.get("asked"):
                    candidate["asked"] = False
                    candidate["items"] = candidate["items"][-1:]

    def _save(self):
        if not self.path:
            return
        try:
            from . import durable

            shows = {k: v for k, v in self._shows.items()
                     if _kept_on_save(v)}
            # 1.7.5 (BH-151-m4): what a newer backend wrote that this one
            # doesn't know goes back as it was read
            data = dict(self._other)
            data.update({"version": self._version, "shows": shows})
            if self._legacy_done:
                data["legacyMigrated"] = True
            durable.replace_file(
                self.path, json.dumps(data, indent=1, sort_keys=True))
            self._stamp = os.stat(self.path).st_mtime_ns
        except Exception:
            log.warning("could not save the skip rules", exc_info=True)

    def migrate(self, account):
        """1.4's ``learned-intros.json`` (keys ``"<series>:<season>"``, no
        server or user) into this file once, under ``account`` -- the first
        account that plays after the upgrade. The old file is renamed, not
        deleted. 1.6.1 (R3): once for good, recorded in this file
        (``legacyMigrated``); a legacy file found after that is left alone.
        1.6.1 review: 1.6.0 wrote no such mark, so a readable file of 1.6.0's
        counts as migrated too (1.6.0 migrated at the first ``connect``,
        before anything else could write it), also one whose shows are all
        forgotten: a legacy file still there (1.6.0's rename failed) would
        bring back intros forgotten under 1.6.0. Never raises."""
        if self._migrated or not self.path or not account:
            return
        self._migrated = True
        legacy = os.path.join(os.path.dirname(self.path), LEGACY_FILE)
        with self._lock:
            self._load()
            if not self._legacy_done and self._stamp is not None:
                # read, with no mark: 1.6.0's (an unreadable one was set
                # aside by `_load` and starts over)
                self._legacy_done = True
                self._save()
            if self._legacy_done:
                if os.path.isfile(legacy):
                    log.info("%s is there again; it was migrated once, so it "
                             "is left alone", LEGACY_FILE)
                return
        try:
            if not os.path.isfile(legacy):
                return
            with open(legacy, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            entries = (data or {}).get("entries", {})
            # 1.5.1: its seasons' "moves" marks and kept skips, keyed
            # "<server>:<user>:<series>:<season>" (`intro_sources.moves_key`)
            seasons = (data or {}).get("seasons", {})
        except Exception:
            log.warning("%s unreadable; not migrated", LEGACY_FILE,
                        exc_info=True)
            entries, seasons = {}, {}
        moved = 0
        with self._lock:
            self._load()
            for key, value in (entries or {}).items():
                if not isinstance(key, str) or not _valid_intro(value):
                    continue
                series, _, season = key.rpartition(":")
                series = _norm_id(series)
                if not series or not season.lstrip("-").isdigit():
                    continue
                show = self._shows.setdefault("%s:%s" % (account, series),
                                              _empty_show())
                if season not in show["intros"]:
                    show["intros"][season] = entry = {
                        "start": float(value["start"]),
                        "end": float(value["end"]),
                        "undone": int(value.get("undone", 0) or 0),
                        "learned": int(value.get("learned", 0) or 0),
                        "moves": bool(value.get("moves", False))}
                    if season not in show["seasons"]:
                        show["seasons"][season] = _marks_of_entry(entry)
                    _mirror(show, season)
                    moved += 1
            marks = self._migrate_seasons(seasons)
            # saved even when it held nothing: the next start must not take
            # it again (R3)
            self._legacy_done = True
            self._save()
        try:
            os.replace(legacy, legacy + LEGACY_DONE_SUFFIX)
        except OSError:
            log.warning("could not rename %s after migrating it", LEGACY_FILE,
                        exc_info=True)
        log.info("Migrated %d learned intro(s) into %s", moved, RULES_FILE)

    def _migrate_seasons(self, seasons):
        """1.5.1's per-season "moves" (1.6 integration) into the season's
        record of its own server, user and show (1.5.1 keyed them as
        `account_key` and `show_key` do). 1.6.1 (R2): also a mark whose
        learned intro was gone (1.5.1 kept its marks apart from the ranges,
        so a seek back into the spot left the season's "moves"). The count
        carried over."""
        marks = 0
        for key, value in (seasons.items() if isinstance(seasons, dict)
                           else ()):
            parts = key.split(":") if isinstance(key, str) else []
            if (len(parts) != 4 or not parts[3].lstrip("-").isdigit()
                    or not isinstance(value, dict)):
                continue
            season = str(int(parts[3]))
            show = self._shows.get(":".join(parts[:3]))
            entry = ((show or {}).get("intros") or {}).get(season)
            kept = _kept(value.get("skips"))
            if value.get("moves") is True:
                record = {"skips": [], "moves": True}
            elif entry is not None:
                record = {"skips": _skips(dict(entry, skips=kept))[
                    -MOVES_KEEP:], "moves": False}
            elif kept:
                record = {"skips": kept, "moves": False}
            else:
                continue        # nothing kept and no spot: nothing to judge
            if show is None:
                show = self._shows[":".join(parts[:3])] = _empty_show()
            show["seasons"][season] = record
            _mirror(show, season)
            marks += 1
        return marks

    # -- reads -------------------------------------------------------------

    def show(self, key):
        """A copy of a show's entry, or None."""
        with self._lock:
            self._load()
            show = self._shows.get(key)
            return json.loads(json.dumps(show)) if show else None

    def shows_of(self, account):
        """``{series id: show}`` for one account (copies)."""
        prefix = account + ":"
        with self._lock:
            self._load()
            return {k[len(prefix):]: json.loads(json.dumps(v))
                    for k, v in self._shows.items()
                    if k.startswith(prefix) and _has_content(v)}

    def _marks(self, key, season):
        """The season's record (kept skips, "moves"), or None."""
        return ((self._shows.get(key) or {}).get("seasons") or {}).get(
            str(season))

    def intro(self, key, season):
        """The learned intro ``(start, end)`` for a season, or None (also
        when it "moves")."""
        with self._lock:
            self._load()
            entry = ((self._shows.get(key) or {}).get("intros") or {}).get(
                str(season))
            marks = self._marks(key, season) or {}
            if not entry or marks.get("moves") or entry.get("moves"):
                return None
            return float(entry["start"]), float(entry["end"])

    def intro_entry(self, key, season):
        """A copy of the season's learned intro with its record's ``moves``
        and ``skips``; for a season that moves and has no intro (taken back,
        then marked), ``{"moves": True, "skips": [...]}``. None for a season
        with neither (it may be learned)."""
        with self._lock:
            self._load()
            entry = ((self._shows.get(key) or {}).get("intros") or {}).get(
                str(season))
            marks = self._marks(key, season)
            if entry is None and not (marks and marks["moves"]):
                return None
            out = dict(entry or {})
            if marks is not None:
                out["moves"] = marks["moves"]
                out["skips"] = [list(s) for s in marks["skips"]]
            return out

    # -- writes ------------------------------------------------------------

    def _show_for(self, key, name=None):
        show = self._shows.get(key)
        if show is None:
            show = self._shows[key] = _empty_show(name)
        elif name and not show.get("name"):
            show["name"] = name
        return show

    def learn_intro(self, key, season, start, end, name=None):
        with self._lock:
            self._load()
            show = self._show_for(key, name)
            season = str(season)
            marks = show["seasons"].get(season)
            if marks and marks["moves"]:
                return          # a season that moves is never relearned
            skip = [round(start, 1), round(end, 1)]
            show["intros"][season] = {
                "start": skip[0], "end": skip[1], "undone": 0,
                "learned": int(time.time()), "moves": False}
            # 1.6 integration (1.5.1's "moves"): the skip it was learned from
            # is one of the season's kept skips; 1.6.1 (R2): added to those
            # the season kept before (a spot taken back and learned again),
            # never a new history
            if marks is None:
                marks = show["seasons"][season] = {"skips": [],
                                                   "moves": False}
            if skip not in marks["skips"]:
                marks["skips"] = (marks["skips"] + [skip])[-MOVES_KEEP:]
            _mirror(show, season)
            self._save()

    def note_hand_skip(self, key, season, start, end):
        """1.5.1's "moves", judged within the season (1.6 integration): the
        first skip by hand of an episode, ``start`` to ``end`` seconds, in the
        intro's window. One of the same part as a kept one (`same_part`) and
        `MOVES_GAP` or more from it (`skips_apart`) marks the season "moves"
        for good (its spot is no longer offered, nor relearned, until it is
        forgotten); else it is kept (the latest `MOVES_KEEP`). True when this
        skip marked it. A season never learned keeps nothing: there is no
        spot to stop offering. 1.6.1 (R2): kept in the season's record, which
        outlives the intro (two undos, a seek back into it), so the skips of
        a season whose spot was taken back still count."""
        with self._lock:
            self._load()
            show = self._shows.get(key)
            marks = self._marks(key, season)
            if show is None or marks is None or marks["moves"]:
                return False
            skip = [round(float(start), 1), round(float(end), 1)]
            kept = marks["skips"]
            moved = any(same_part(skip, earlier) and skips_apart(skip, earlier)
                        for earlier in kept)
            marks["moves"] = moved
            marks["skips"] = [] if moved else (kept + [skip])[-MOVES_KEEP:]
            _mirror(show, str(season))
            self._save()
            return moved

    def forget_intro(self, key, season):
        with self._lock:
            self._load()
            show = self._shows.get(key)
            if show and show["intros"].pop(str(season), None) is not None:
                self._save()

    def undo_intro(self, key, season):
        """A skip of the learned range taken back. Dropped at `MAX_UNDOS`."""
        with self._lock:
            self._load()
            show = self._shows.get(key)
            entry = show and show["intros"].get(str(season))
            if not entry:
                return
            entry["undone"] = int(entry.get("undone", 0)) + 1
            if entry["undone"] >= MAX_UNDOS:
                del show["intros"][str(season)]
            self._save()

    def add_rule(self, key, anchor, start, end, action=None, origin="marked",
                 name=None):
        """Adds a rule; returns its id. A rule that covers nearly the same
        part as one already there (same anchor, starts and ends within 2 s)
        replaces it rather than doubling it."""
        rule = {"id": "r" + uuid.uuid4().hex[:12], "anchor": anchor,
                "start": round(float(start), 2), "end": round(float(end), 2),
                "action": action, "origin": origin,
                "created": int(time.time()), "corrected": False}
        if not _valid_rule(rule):
            raise ValueError("not a usable rule")
        with self._lock:
            self._load()
            show = self._show_for(key, name)
            show["rules"] = [
                r for r in show["rules"]
                if not (r["anchor"] == anchor
                        and abs(r["start"] - rule["start"]) <= 2
                        and abs(r["end"] - rule["end"]) <= 2)]
            show["rules"].append(rule)
            self._save()
        return rule["id"]

    def correct_rule(self, key, rule_id, end):
        """One correction teaches: ``end`` is the rule's new end (seconds,
        in the rule's own anchor). False when it would make the rule empty
        or too long."""
        with self._lock:
            self._load()
            show = self._shows.get(key)
            rule = next((r for r in (show or {}).get("rules", [])
                         if r["id"] == rule_id), None)
            if rule is None:
                return False
            changed = dict(rule, end=round(float(end), 2), corrected=True)
            if not _valid_rule(changed):
                return False
            rule.update(changed)
            self._save()
            return True

    def note_episode(self, key, item_id):
        """A new episode of a show with something kept started: counts
        toward its candidates' expiry. Once per item, also across restarts
        of the same item. A show with nothing kept gets no entry."""
        with self._lock:
            self._load()
            show = self._shows.get(key)
            if show is None or show.get("last_item") == item_id:
                return
            show["last_item"] = item_id
            show["episodes"] = int(show.get("episodes", 0)) + 1
            now = time.time()
            show["candidates"] = [
                c for c in show["candidates"]
                if now - c.get("seen", now) <= CANDIDATE_MAX_AGE
                and show["episodes"] - c.get("episode", 0)
                <= CANDIDATE_MAX_EPISODES]
            self._save()

    def note_candidate(self, key, item_id, anchor, start, end, name=None):
        """A viewer's forward seek near an episode's start or end. Returns
        the candidate to ask about (a copy) when it matches one from another
        episode not asked about yet, the show not refused; else None."""
        with self._lock:
            self._load()
            show = self._show_for(key, name)
            if show["refused"]:
                return None
            length = abs(end - start)
            for candidate in show["candidates"]:
                if candidate["anchor"] != anchor:
                    continue
                other = abs(candidate["end"] - candidate["start"])
                if abs(candidate["start"] - start) > MATCH_START:
                    continue
                if abs(other - length) > max(MATCH_LENGTH_SECONDS,
                                             MATCH_LENGTH_SHARE * max(
                                                 other, length)):
                    continue
                if item_id in candidate["items"]:
                    return None     # the same episode again: no match
                if candidate.get("asked"):
                    return None
                candidate["items"].append(item_id)
                # the two seeks together: where the part is, on average
                count = len(candidate["items"])
                candidate["start"] = round(
                    (candidate["start"] * (count - 1) + start) / count, 2)
                candidate["end"] = round(
                    (candidate["end"] * (count - 1) + end) / count, 2)
                candidate["asked"] = True
                candidate["seen"] = time.time()
                self._save()
                return dict(candidate)
            show["candidates"].append({
                "id": "c" + uuid.uuid4().hex[:12], "anchor": anchor,
                "start": round(start, 2), "end": round(end, 2),
                "items": [item_id], "asked": False, "seen": time.time(),
                "episode": int(show.get("episodes", 0))})
            show["candidates"] = show["candidates"][-MAX_CANDIDATES:]
            self._save()
            return None

    def release(self, key, candidate_id):
        """A suggestion nobody answered (the player was left, another episode
        began): its candidate may ask again at the next matching skip."""
        with self._lock:
            self._load()
            show = self._shows.get(key)
            candidate = next((c for c in (show or {}).get("candidates", [])
                              if c["id"] == candidate_id), None)
            if candidate is not None and candidate.get("asked"):
                candidate["asked"] = False
                candidate["items"] = candidate["items"][-1:]
                self._save()

    def answer(self, account, candidate_id, answer, action=None):
        """The viewer's answer to a suggestion: ``yes`` makes the candidate a
        rule (returns its id), ``not_now`` drops the candidate, ``never``
        refuses the show. None when the candidate is not known (any more)."""
        prefix = account + ":"
        with self._lock:
            self._load()
            for key, show in self._shows.items():
                if not key.startswith(prefix):
                    continue
                candidate = next((c for c in show["candidates"]
                                  if c["id"] == candidate_id), None)
                if candidate is None:
                    continue
                show["candidates"].remove(candidate)
                result = {}
                if answer == "yes":
                    start, end = candidate["start"], candidate["end"]
                    self._save()
                    try:
                        result["ruleId"] = self.add_rule(
                            key, candidate["anchor"], start, end, action,
                            "learned")
                    except ValueError:
                        return None
                    return result
                if answer == "never":
                    show["refused"] = True
                    show["candidates"] = []
                self._save()
                return result
        return None

    def forget(self, account, series=None, rule_id=None):
        """Forgets one rule or learned intro (``rule_id``; a learned intro's
        is ``"intro-<series>-<season>"``), one show (``series``: its rules,
        learned intros, candidates and refusal), or, with neither,
        everything of the account. Returns how many shows changed."""
        prefix = account + ":"
        intro = None
        if rule_id is not None and rule_id.startswith("intro-"):
            parts = rule_id.split("-", 2)
            if len(parts) != 3:
                return 0
            intro = parts[2]
            series = parts[1]
        changed = 0
        with self._lock:
            self._load()
            for key in list(self._shows):
                if not key.startswith(prefix):
                    continue
                if series is not None and key != prefix + series:
                    continue
                show = self._shows[key]
                if rule_id is None:
                    del self._shows[key]
                    changed += 1
                    continue
                if intro is not None:
                    # Settings' Forget: the season's kept skips and "moves"
                    # go with its intro (1.6.1: they outlive the intro only
                    # for the player's undo and seek back)
                    gone = show["intros"].pop(intro, None) is not None
                    gone = show["seasons"].pop(intro, None) is not None \
                        or gone
                    if gone:
                        changed += 1
                    continue
                before = len(show["rules"])
                show["rules"] = [r for r in show["rules"]
                                 if r["id"] != rule_id]
                if len(show["rules"]) != before:
                    changed += 1
            if changed:
                self._save()
        return changed

    def clear_all(self):
        """Everything, every account (tests)."""
        with self._lock:
            self._shows = {}
            self._save()


_store = None
_store_path = object()
_store_lock = threading.Lock()


def store():
    """The store in the backend's config directory (in memory before one is
    known)."""
    global _store, _store_path
    from . import conf

    base = os.path.dirname(conf.config_path) if conf.config_path else None
    path = os.path.join(base, RULES_FILE) if base else None
    with _store_lock:
        if _store is None or path != _store_path:
            _store, _store_path = SkipStore(path), path
        return _store


# -- the list, for Settings and the bridge -----------------------------------

def listing(store_, account):
    """``skip_rules``' result for ``account``."""
    shows = []
    for series, show in sorted(
            store_.shows_of(account).items(),
            key=lambda kv: ((kv[1].get("name") or "").lower(), kv[0])):
        rules = [{"id": r["id"], "anchor": r["anchor"],
                  "startS": r["start"], "endS": r["end"],
                  "action": r.get("action"), "origin": r["origin"],
                  "created": r.get("created", 0)}
                 for r in show["rules"]]
        # 1.6.1 (R2): a season that moves is listed (and can be forgotten)
        # whether or not its intro is still there
        seasons = show.get("seasons") or {}
        listed = dict(show["intros"])
        for season, marks in seasons.items():
            if marks.get("moves") and season not in listed:
                last = (marks.get("skips") or [[0.0, 0.0]])[-1]
                listed[season] = {"start": last[0], "end": last[1]}
        intros = [{"id": "intro-%s-%s" % (series, season),
                   "season": int(season) if season.lstrip("-").isdigit()
                   else None,
                   "startS": entry["start"], "endS": entry["end"],
                   "moves": bool((seasons.get(season) or entry)
                                 .get("moves"))}
                  for season, entry in sorted(
                      listed.items(),
                      key=lambda kv: (len(kv[0]), kv[0]))]
        if not rules and not intros and not show["refused"]:
            continue
        shows.append({"seriesId": series, "name": show.get("name"),
                      "rules": rules, "learnedIntros": intros,
                      "refused": show["refused"]})
    return {"shows": shows}


# -- rules on an item ---------------------------------------------------------

def _overlap(a_start, a_end, b_start, b_end):
    return max(0.0, min(a_end, b_end) - max(a_start, b_start))


def rule_parts(video, key, store_):
    """``[(rule, start, end, action)]`` for the item, in seconds of this
    item, from the show's rules (an end rule needs the runtime)."""
    show = store_.show(key) if key else None
    if not show:
        return []
    runtime = runtime_of(video)
    out = []
    for rule in show["rules"]:
        if rule["anchor"] == "start":
            start, end = rule["start"], rule["end"]
        else:
            if runtime is None:
                continue
            start, end = runtime - rule["start"], runtime - rule["end"]
        if runtime is not None:
            start, end = max(0.0, start), min(runtime, end)
        if end - start < 1.0:
            continue
        action = rule.get("action") or OPTIONS.rule_action
        out.append((rule, float(start), float(end), action))
    return out


def add_rules(video, make_segment, key, store_):
    """Adds the show's rules to ``video.intros`` as segments with source
    ``"rule"``. A rule that covers most of an intro or the credits already
    there takes that segment's type (so its pill keeps saying "Skip intro")
    and its place (never two pills for one part); any other is a
    ``Commercial`` ("Skip extra"). An "ask" rule is offered from
    `START_TOLERANCE` before its start (a bumper is not the same length
    twice); an "always" one skips exactly its span. 1.7.5: the segment a
    rule takes over is kept on the rule's (``replaced``), so a refresh puts
    it back first (`taken_over`)."""
    for rule, start, end, action in rule_parts(video, key, store_):
        kind = "Commercial"
        replaced = None
        for other in list(video.intros):
            if other.type not in ("Intro", "Outro"):
                continue
            span = max(0.001, other.end - other.start)
            if _overlap(start, end, other.start, other.end) >= 0.5 * span:
                kind = other.type
                video.intros.remove(other)
                replaced = other
                break
        offered = start
        if action == "ask" and rule["anchor"] == "start":
            offered = max(0.0, start - START_TOLERANCE)
        segment = make_segment(kind, offered, end, SOURCE_RULE)
        segment.action = action
        segment.rule_id = rule["id"]
        segment.origin = rule["origin"]
        segment.anchor = rule["anchor"]
        segment.rule_start = start
        segment.rule_end = end
        segment.corrected = bool(rule.get("corrected"))
        segment.snap_pending = True
        segment.replaced = replaced
        video.intros.append(segment)


def taken_over(segments):
    """1.7.5: the segments the rules' segments among ``segments`` took over
    (`add_rules`: the server's or the chapters' Intro or Outro), each as it
    was, through a rule that took another rule's place. `bridge_skip.
    refresh_rules` puts them back before the rules are added again: a rule
    kept takes its Intro or Outro over again (it became a "Commercial" after
    an Add, Yes or Forget mid-episode), and a forgotten rule's comes back at
    once. One the rule's part had already skipped or offered in this episode
    (``has_triggered``) stays so: it is the same part."""
    out = []
    for segment in segments:
        if getattr(segment, "source", None) != SOURCE_RULE:
            continue
        triggered = bool(getattr(segment, "has_triggered", False))
        other, seen = getattr(segment, "replaced", None), 0
        while (other is not None and seen < 32
               and getattr(other, "source", None) == SOURCE_RULE):
            triggered = triggered or bool(getattr(other, "has_triggered",
                                                  False))
            other, seen = getattr(other, "replaced", None), seen + 1
        if (other is None or getattr(other, "source", None) == SOURCE_RULE
                or any(other is o for o in out)):
            continue
        if triggered:
            other.has_triggered = True
        out.append(other)
    return out


def carry_rule_state(before, segment, position):
    """1.6.1 (the 1.6 review's skip queue): a rule's segment rebuilt while
    its episode plays (`bridge_skip.refresh_rules`: a rule added, a Yes to a
    suggestion, a rule forgotten) keeps what this episode already did with
    it. ``before`` is the same rule's segment before (None for a rule new to
    the episode):

    * skipped stays skipped (``has_triggered``, ``skipped_at``): the player
      skips an "always" part once, and a seek back into it is the viewer's
      to watch (`AlwaysDoesNotLoop`); a fresh segment re-armed it;
    * its snapped edge stays, when the rule's own span is the same (a
      corrected rule snaps again);
    * a rule new to the episode whose end ``position`` has passed counts as
      skipped here: an "always" rule made from the skip just taken (Yes,
      Shift+S, the playhead past the part) must not skip again when the
      viewer seeks back into it. One added with the playhead inside its part
      (the sheet's "now to 30 s from now", skipped by itself) still skips it
      now, as 1.6.0 did (the 1.6.1 review: it tested the start)."""
    if before is not None:
        segment.has_triggered = bool(getattr(before, "has_triggered", False))
        skipped_at = getattr(before, "skipped_at", None)
        if skipped_at is not None:
            segment.skipped_at = skipped_at
        same = (getattr(before, "rule_start", None) == segment.rule_start
                and getattr(before, "rule_end", None) == segment.rule_end)
        if same and not getattr(before, "snap_pending", True):
            segment.start, segment.end = before.start, before.end
            segment.snap_pending = False
    elif _num(position) and position >= segment.rule_end:
        segment.has_triggered = True


def learned_intro(video, key, store_):
    """``(start, end)`` of the season's learned intro, or None."""
    season = season_of(getattr(video, "item", None) or {})
    if not key or season is None:
        return None
    return store_.intro(key, season)


# -- watching the viewer's seeks ----------------------------------------------

class SkipWatcher:
    """Watches the playback position (one `sample` per position reading,
    about every 0.5 s) and learns from the viewer's seeks:

    * the one-seek learned intro per season (1.4), its undo, and 1.5.1's
      "moves";
    * candidates, and a suggestion (`take_suggestions`) when a second
      episode of the show has the same skip;
    * corrections: a seek right after the app skipped a rule's part moves
      the rule's end there.

    App-made jumps never count: the Skip pill and S (`note_skip`), a part
    skipped by itself (a jump from inside an ``always`` segment to its end),
    and the chapters menu (a jump to a chapter start)."""

    def __init__(self, store_=None):
        self._store = store_
        self._video = None
        self._last = None
        self._skipped = None      # (learned segment, when) after its skip
        self._noted = None        # 1.5.1: the item whose first hand skip was noted
        self._by_skip = 0.0       # until when a forward jump is a Skip press
        self._skip_segment = None  # the segment the Skip press was for
        self._rule_skip = None    # (key, rule id, anchor, when) to correct
        self._suggestions = []
        self._lock = threading.Lock()
        #: the viewer's seeks still being made (`BURST_GAP`), judged once
        #: they stop: dict(video, key, item, start, end, t, duration)
        self._burst = None
        #: suggestions asked in this item and not answered yet: id -> key
        self._open = {}

    def _store_(self):
        return self._store or store()

    def note_skip(self, segment=None, now=None):
        """The app itself is about to seek (`skip_segment`): that jump is not
        the viewer's. ``segment`` is the one it skips: a rule's can be
        corrected by the next seek."""
        now = time.monotonic() if now is None else now
        self._by_skip = now + NOTE_SKIP_WINDOW
        self._skip_segment = segment

    def answered(self, candidate_id):
        """The viewer answered this suggestion (``answer_skip_suggestion``)."""
        with self._lock:
            self._open.pop(candidate_id, None)

    def _release_open(self, ended=None):
        """Another item began: what nobody answered may ask again. Not what
        was asked in the item that just ended (``ended``): a skip of the
        promos after an episode is asked as it ends, and its question stays
        on screen into the next one (review, Major 2); that one is released
        when the next item after it begins."""
        with self._lock:
            open_ = {c: v for c, v in self._open.items() if v[1] is not ended}
            self._open = {c: v for c, v in self._open.items()
                          if v[1] is ended}
        for candidate_id, (key, _video) in open_.items():
            try:
                self._store_().release(key, candidate_id)
            except Exception:
                log.debug("could not release a suggestion", exc_info=True)

    def take_suggestions(self):
        """The ``skip_suggestion`` events found since the last call."""
        with self._lock:
            out, self._suggestions = self._suggestions, []
            return out

    def sample(self, video, position, now=None, duration=None):
        try:
            self._sample(video, position,
                         time.monotonic() if now is None else now, duration)
        except Exception:
            log.debug("skip watcher failed", exc_info=True)

    # -- one sample ---------------------------------------------------------

    def _sample(self, video, position, now, duration):
        if video is not self._video:
            ended = self._video
            self._judge_burst()     # the last item's seeks, as they were
            self._release_open(ended)
            self._video, self._last, self._skipped = video, None, None
            self._noted = None
            self._rule_skip = None
            self._new_item(video)
        if self._burst is not None and now - self._burst["t"] > BURST_GAP:
            self._judge_burst()
        if not _num(position):
            return
        previous, self._last = self._last, float(position)
        if previous is None:
            return
        delta = position - previous
        item = getattr(video, "item", None) or {}
        key = show_key(item)
        if key is None:
            return
        segments = list(getattr(video, "intros", None) or ())
        app_jump = self._app_jump(segments, previous, position, now)
        if app_jump is not None:
            # the app skipped this segment: a rule's may be corrected next
            if getattr(app_jump, "rule_id", None):
                self._rule_skip = (key, app_jump.rule_id,
                                   getattr(app_jump, "anchor", "start"), now,
                                   getattr(app_jump, "rule_start", None))
            self._skip_segment = None
            self._learned_skip(segments, previous, now, from_app=True)
            return
        if self._correct(key, delta, position, now):
            return
        if abs(delta) >= CORRECTION_MIN:
            self._rule_skip = None
        self._learned_intro(key, item, segments, previous, position, delta,
                            now)
        if abs(delta) >= SEEK_MIN:
            self._seek(video, key, item, segments, previous, position, now,
                       duration)

    def _seek(self, video, key, item, segments, previous, position, now,
              duration):
        """A seek of the viewer's: it continues the burst being made, or
        starts one (the one before is judged first). One that lands within
        `END_JUDGE` of the end is judged at once: the episode ends before
        another seek could come, and the question must come while it plays
        (review, Major 2)."""
        burst = self._burst
        if (burst is not None and burst["video"] is video
                and now - burst["t"] <= BURST_GAP
                and abs(previous - burst["end"]) <= BURST_JOIN):
            if position < burst["start"]:
                self._burst = None      # back before it began: no skip
            else:
                burst["end"], burst["t"] = position, now
        else:
            self._judge_burst()
            if (position > previous
                    and not self._on_chapter(item, position)
                    and not self._inside_rule(segments, previous)):
                self._burst = {"video": video, "key": key, "item": item,
                               "start": previous, "end": position,
                               "t": now, "duration": duration}
        runtime = (duration if _num(duration) and duration > 0
                   else runtime_of(video))
        if (self._burst is not None and runtime is not None
                and runtime - self._burst["end"] <= END_JUDGE):
            self._judge_burst()

    def _judge_burst(self):
        """A burst of seeks that stopped: a candidate when it skipped 10 s to
        5 minutes forward, and did not end on a chapter's start."""
        burst, self._burst = self._burst, None
        if burst is None or not OPTIONS.learn:
            return
        length = burst["end"] - burst["start"]
        if not CANDIDATE_MIN_JUMP <= length <= CANDIDATE_MAX_JUMP:
            return
        if self._on_chapter(burst["item"], burst["end"]):
            return
        try:
            self._candidate(burst["video"], burst["key"], burst["item"],
                            burst["start"], burst["end"], burst["duration"])
        except Exception:
            log.debug("could not judge a skip", exc_info=True)

    def _new_item(self, video):
        item = getattr(video, "item", None) or {}
        key = show_key(item)
        if key is None:
            return
        try:
            item_id = str(item.get("Id") or "").replace("-", "").lower()
            if item_id:
                self._store_().note_episode(key, item_id)
        except Exception:
            log.debug("could not count the episode", exc_info=True)

    def _app_jump(self, segments, previous, position, now):
        """The segment the app just skipped, if this jump is that skip."""
        if position <= previous:
            return None
        if now <= self._by_skip:
            if position - previous < SEEK_MIN:
                # 1.6.1 (found at the 1.6 integration): a playback reading
                # between the app's `note_skip` and its seek landing; the
                # press waits for its jump, which was then taken for the
                # viewer's (a candidate, or a hand skip for "moves")
                return None
            pressed = self._skip_segment
            self._by_skip = 0.0
            if pressed is not None:
                return pressed
            # a press with no segment known: still not the viewer's
            return _NOT_A_SEGMENT
        for segment in segments:
            if abs(position - segment.end) > APP_LANDING:
                continue
            # the player skipped it just now (`PlayerManager.skip_intro`
            # marks the time): whatever the readings around the jump were
            skipped_at = getattr(segment, "skipped_at", None)
            if _num(skipped_at) and 0 <= now - skipped_at <= NOTE_SKIP_WINDOW:
                return segment
            action = getattr(segment, "action", None)
            if action is None:
                try:
                    from . import conf

                    action = conf.segment_action(segment.type)
                except Exception:
                    action = None
            # The readings come about every 0.5 s and the player skips up
            # to a second after the part begins, so the reading before the
            # jump can be just before the part (29.7 for a part from 30).
            if (action == "always"
                    and segment.start - APP_LANDING <= previous < segment.end):
                return segment
        return None

    @staticmethod
    def _on_chapter(item, position):
        try:
            for chapter in item.get("Chapters") or ():
                ticks = chapter.get("StartPositionTicks")
                if _num(ticks) and abs(ticks / 10_000_000 - position) \
                        <= CHAPTER_LANDING:
                    return True
        except Exception:
            pass
        return False

    @staticmethod
    def _inside_rule(segments, position):
        return any(getattr(s, "source", None) == SOURCE_RULE
                   and s.start <= position < s.end for s in segments)

    def _correct(self, key, delta, position, now):
        """A seek right after the app skipped a rule's part: the rule's end
        moves to where it lands (start rules; an end rule's skip ends the
        episode)."""
        pending = self._rule_skip
        if pending is None or abs(delta) < CORRECTION_MIN:
            return False
        rule_key, rule_id, anchor, when, rule_start = pending
        if now - when > CORRECTION_WINDOW or anchor != "start" \
                or rule_key != key:
            self._rule_skip = None
            return False
        lowest = (rule_start or 0.0) + 1.0
        show = self._store_().show(key) or {}
        rule = next((r for r in show.get("rules", []) if r["id"] == rule_id),
                    None)
        if rule is None or not lowest <= position <= rule["end"] + max(
                CORRECTION_MAX_PAST, 0):
            self._rule_skip = None
            return False
        if self._store_().correct_rule(key, rule_id, position):
            log.info("Skip rule %s corrected: it now ends at %.1f s",
                     rule_id, position)
            # a second nudge right after still refines it
            self._rule_skip = (rule_key, rule_id, anchor, now, rule_start)
            return True
        self._rule_skip = None
        return False

    def _learned_skip(self, segments, previous, now, from_app=False):
        learned = next((s for s in segments if s.type == "Intro"
                        and getattr(s, "source", None) == SOURCE_LEARNED),
                       None)
        if learned is not None and learned.start <= previous < learned.end:
            self._skipped = (learned, now)

    def _learned_intro(self, key, item, segments, previous, position, delta,
                       now):
        """1.4's one-seek learned intro, kept, with 1.5.1's "moves"."""
        season = season_of(item)
        if season is None:
            return
        fronts = [s for s in segments if s.type == "Intro"]
        learned = next((s for s in fronts
                        if getattr(s, "source", None) == SOURCE_LEARNED),
                       None)
        store_ = self._store_()
        if LEARN_MIN_JUMP <= delta <= LEARN_MAX_JUMP:
            qualifying = OPTIONS.learn and 0 <= previous <= LEARN_MAX_START
            entry = store_.intro_entry(key, season)
            # 1.5.1's "moves" (1.6 integration: its rule, judged within the
            # season): the item's first skip by hand in the intro's window,
            # wherever it is (also on the spot offered); app jumps never reach
            # here (`_app_jump`)
            noted = qualifying and self._noted is not self._video
            if noted:
                self._noted = self._video
            # 1.6.1 (R2): with or without the intro (the season's record
            # outlives it; the store keeps nothing for a season never learned)
            if noted and store_.note_hand_skip(key, season, previous,
                                               position):
                log.info("The learned intro of this season moves (skips by "
                         "hand %.0f s or more apart); its fixed spot is no "
                         "longer offered", MOVES_GAP)
            if learned is not None and learned.start <= previous < learned.end:
                self._skipped = (learned, now)
                return
            if entry is not None:
                return          # learned already (or it moves): not relearned
            if (not fronts and qualifying
                    and not any(s.start <= previous < s.end
                                for s in segments)):
                store_.learn_intro(key, season, previous, position,
                                   item.get("SeriesName"))
        elif delta <= -BACK_MIN_JUMP and learned is not None:
            if not learned.start <= position < learned.end:
                return
            skipped = self._skipped
            self._skipped = None
            if skipped is not None and skipped[0] is learned \
                    and now - skipped[1] <= UNDO_WINDOW:
                store_.undo_intro(key, season)
            else:
                store_.forget_intro(key, season)

    def _candidate(self, video, key, item, previous, position, duration):
        runtime = duration if _num(duration) and duration > 0 else \
            runtime_of(video)
        near_start = previous <= CANDIDATE_EDGE
        near_end = runtime is not None and runtime - previous <= CANDIDATE_EDGE
        if near_start and near_end:
            near_end = previous > runtime / 2
            near_start = not near_end
        if near_start:
            anchor, start, end = "start", previous, position
        elif near_end:
            anchor = "end"
            start, end = runtime - previous, max(0.0, runtime - position)
        else:
            return
        item_id = str(item.get("Id") or "").replace("-", "").lower()
        if not item_id:
            return
        found = self._store_().note_candidate(key, item_id, anchor, start,
                                              end, item.get("SeriesName"))
        if found is None:
            return
        if anchor == "start":
            at_start, at_end = found["start"], found["end"]
        else:
            at_start = (runtime or 0) - found["start"]
            at_end = (runtime or 0) - found["end"]
        suggestion = {
            "id": found["id"], "itemId": item_id,
            "seriesId": series_id(item),
            "seriesName": item.get("SeriesName")
            if isinstance(item.get("SeriesName"), str) else None,
            "anchor": anchor, "startS": max(0.0, at_start),
            "endS": max(0.0, at_end)}
        with self._lock:
            self._suggestions.append(suggestion)
            self._open[found["id"]] = (key, video)
        log.info("Suggesting a skip rule for this show (%s, %.1f-%.1f s)",
                 anchor, found["start"], found["end"])


class _NotASegment:
    """A Skip press whose segment is not known: the jump is the app's."""
    rule_id = None
    anchor = None
    rule_start = None


_NOT_A_SEGMENT = _NotASegment()
