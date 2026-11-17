"""fluentfin-mpv: where a Skip intro / Skip credits can come from when the
server has no media segment for it (docs/BRIDGE.md, ``segments.source``).

Precedence, per segment type: server > chapters > online > learned. A
source fills a type only when no source before it gave one, so the viewer
never gets two pills for the same part.

* **chapters** -- the file's own chapters named Intro, Opening, OP, Recap,
  Previously, Credits, Ending, ED and so on (`chapter_segments`).
* **online** -- `intro_online` (opt-in, off by default).
* **learned** -- a range the viewer skipped by hand in an earlier episode of
  the same season (`LearnedStore`, `SkipLearner`), kept on this PC in
  ``learned-intros.json`` in the backend's config directory. The UI's
  "Forget learned intros" deletes that file.

Nothing here raises into playback: every entry point catches and logs.
"""

import json
import logging
import os
import re
import threading
import time

log = logging.getLogger("intro_sources")

SOURCE_SERVER = "server"
SOURCE_CHAPTERS = "chapters"
SOURCE_ONLINE = "online"
SOURCE_LEARNED = "learned"

#: The file the learned ranges are kept in, in the backend's config
#: directory. Shared with the UI, which deletes it to forget them.
LEARNED_FILE = "learned-intros.json"

TICKS = 10_000_000


class Options:
    """What the user chose in Settings > Playback (``set_skip_options``)."""

    def __init__(self):
        self.lookup_online = False
        self.learn = True


OPTIONS = Options()


# -- chapters ----------------------------------------------------------------

_INTRO = re.compile(
    r"(intro|introduction|opening|opening (credits|theme|titles?)|op|"
    r"title sequence|main titles?|theme song)( ?\d{1,2})?")
_RECAP = re.compile(r"(recap|previously( on.*)?|previously|story so far|"
                    r"last time on.*)")
_OUTRO = re.compile(r"(credits|end credits|ending credits|closing credits|"
                    r"ending|ending theme|ed|outro|end titles?)( ?\d{1,2})?")

#: The longest span a chapter may be and still count as an intro or recap:
#: a mislabelled chapter that runs to the end of the file is not one.
_MAX_FRONT_SECONDS = 600.0


def chapter_type(name):
    """The segment type a chapter name stands for (``Intro``, ``Recap`` or
    ``Outro``), or None. Case-insensitive; punctuation and a chapter
    number are ignored."""
    if not isinstance(name, str):
        return None
    text = re.sub(r"[\W_]+", " ", name, flags=re.UNICODE).strip().lower()
    if not text:
        return None
    if _RECAP.fullmatch(text):
        return "Recap"
    if _INTRO.fullmatch(text):
        return "Intro"
    if _OUTRO.fullmatch(text):
        return "Outro"
    return None


def chapter_segments(item):
    """``[(type, start, end)]`` in seconds, at most one per type, from the
    ``Chapters`` of a Jellyfin item dict (start of the chapter to the start
    of the next, or the item's runtime for the last)."""
    out = []
    try:
        chapters = [c for c in (item.get("Chapters") or [])
                    if isinstance(c, dict)
                    and isinstance(c.get("StartPositionTicks"), (int, float))]
        chapters.sort(key=lambda c: c["StartPositionTicks"])
        runtime = item.get("RunTimeTicks")
        seen = set()
        for index, chapter in enumerate(chapters):
            kind = chapter_type(chapter.get("Name"))
            if kind is None or kind in seen:
                continue
            start = chapter["StartPositionTicks"] / TICKS
            if index + 1 < len(chapters):
                end = chapters[index + 1]["StartPositionTicks"] / TICKS
            elif isinstance(runtime, (int, float)) and runtime > 0:
                end = runtime / TICKS
            else:
                continue
            if end <= start:
                continue
            if kind != "Outro" and end - start > _MAX_FRONT_SECONDS:
                continue
            seen.add(kind)
            out.append((kind, start, end))
    except Exception:
        log.debug("unreadable chapters", exc_info=True)
        return []
    return out


# -- learned intros ----------------------------------------------------------

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


def series_key(item):
    """``"<series id>:<season number>"`` for an episode, else None."""
    try:
        series = item.get("SeriesId")
        season = item.get("ParentIndexNumber")
        if not series or not isinstance(season, int):
            return None
        return "%s:%d" % (str(series).replace("-", "").lower(), season)
    except Exception:
        return None


class LearnedStore:
    """Ranges learned per series and season, in a JSON file. Re-read when the
    file changes (the UI deletes it to forget), written whole and atomically.
    With no path it only lives in memory."""

    def __init__(self, path=None):
        self.path = path
        self._lock = threading.RLock()
        self._entries = {}
        self._stamp = None

    def _load(self):
        if not self.path:
            return
        try:
            stamp = os.stat(self.path).st_mtime_ns
        except OSError:
            self._entries, self._stamp = {}, None
            return
        if stamp == self._stamp:
            return
        try:
            with open(self.path, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            entries = data.get("entries", {})
            self._entries = {
                k: v for k, v in entries.items()
                if isinstance(k, str) and isinstance(v, dict)
                and isinstance(v.get("start"), (int, float))
                and isinstance(v.get("end"), (int, float))
                and v["end"] > v["start"]}
        except Exception:
            log.warning("learned intros unreadable; starting over",
                        exc_info=True)
            self._entries = {}
        self._stamp = stamp

    def _save(self):
        if not self.path:
            return
        try:
            tmp = self.path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as handle:
                json.dump({"version": 1, "entries": self._entries}, handle,
                          indent=1, sort_keys=True)
            os.replace(tmp, self.path)
            self._stamp = os.stat(self.path).st_mtime_ns
        except Exception:
            log.warning("could not save learned intros", exc_info=True)

    def get(self, key):
        """``(start, end)`` seconds, or None."""
        with self._lock:
            self._load()
            entry = self._entries.get(key)
            if entry is None:
                return None
            return float(entry["start"]), float(entry["end"])

    def learn(self, key, start, end):
        with self._lock:
            self._load()
            self._entries[key] = {"start": round(start, 1),
                                  "end": round(end, 1), "undone": 0,
                                  "learned": int(time.time())}
            self._save()

    def forget(self, key):
        with self._lock:
            self._load()
            if self._entries.pop(key, None) is not None:
                self._save()

    def undo(self, key):
        """A skip of this range taken back. Dropped at `MAX_UNDOS`."""
        with self._lock:
            self._load()
            entry = self._entries.get(key)
            if entry is None:
                return
            entry["undone"] = int(entry.get("undone", 0)) + 1
            if entry["undone"] >= MAX_UNDOS:
                del self._entries[key]
            self._save()

    def clear(self):
        with self._lock:
            self._entries = {}
            if self.path:
                try:
                    os.remove(self.path)
                except FileNotFoundError:
                    pass
                except OSError:
                    log.warning("could not remove %s", self.path,
                                exc_info=True)
                self._stamp = None


_store = None
_store_path = object()


def store():
    """The store in the backend's config directory (in memory before one
    is known)."""
    global _store, _store_path
    from . import conf

    base = os.path.dirname(conf.config_path) if conf.config_path else None
    path = os.path.join(base, LEARNED_FILE) if base else None
    if _store is None or path != _store_path:
        _store, _store_path = LearnedStore(path), path
    return _store


# -- adding the segments to a video ------------------------------------------

def augment(video, make_segment, store_=None):
    """Adds to ``video.intros`` what the chapters, the online sources and
    the learned ranges offer for the types the server gave nothing for,
    and not the types set to off. ``make_segment(type, start, end, source)``
    builds the segment. Never raises."""
    try:
        from . import conf

        item = getattr(video, "item", None) or {}
        have = {s.type for s in video.intros}
        wanted = {t for t in ("Intro", "Outro", "Recap", "Preview")
                  if conf.segment_action(t) != "off"}

        def add(kind, start, end, source):
            if kind in have or kind not in wanted:
                return
            video.intros.append(make_segment(kind, start, end, source))
            have.add(kind)

        for kind, start, end in chapter_segments(item):
            add(kind, start, end, SOURCE_CHAPTERS)

        if OPTIONS.lookup_online and wanted - have:
            # Off the playback path: the answer is applied when it arrives
            # (`apply_online`), and playback never waits for it.
            start_online(video, make_segment, wanted - have)

        key = series_key(item)
        if OPTIONS.learn and key and "Intro" in wanted and "Intro" not in have:
            learned = (store_ or store()).get(key)
            if learned is not None:
                start, end = learned
                add("Intro", max(0.0, start - START_TOLERANCE), end,
                    SOURCE_LEARNED)
    except Exception:
        log.debug("extra segments failed", exc_info=True)


def apply_online(video, make_segment, kind, start, end):
    """A segment from an online source, added late: it takes the place of a
    learned one (online outranks learned) and yields to server or chapters.
    Returns whether it was added."""
    if any(s.type == kind and getattr(s, "source", "server")
           in (SOURCE_SERVER, SOURCE_CHAPTERS, SOURCE_ONLINE)
           for s in video.intros):
        return False
    video.intros[:] = [s for s in video.intros
                       if not (s.type == kind
                               and getattr(s, "source", None)
                               == SOURCE_LEARNED)]
    video.intros.append(make_segment(kind, start, end, SOURCE_ONLINE))
    return True


def start_online(video, make_segment, missing, lookup=None, wait=False):
    """Looks the item up online in a thread (``wait``: and returns once it
    is done, for tests). `lookup(item, missing)` defaults to
    `intro_online`."""
    def run():
        try:
            from . import intro_online

            item = getattr(video, "item", None) or {}
            if lookup is None:
                show = intro_online.series_info(video)
                version = None
                try:
                    version = video.client.config.data.get("app.version")
                except Exception:
                    pass
                found = intro_online.lookup(item, show, version,
                                            missing=missing)
            else:
                found = lookup(item, missing)
            for kind, start, end in found:
                if kind in missing:
                    apply_online(video, make_segment, kind, start, end)
        except Exception:
            log.debug("online intro lookup failed", exc_info=True)

    thread = threading.Thread(target=run, name="intro-lookup", daemon=True)
    thread.start()
    if wait:
        thread.join(10)
    return thread


class SkipLearner:
    """Watches the playback position and learns from the viewer's seeks.
    One `sample` per position reading (about every 0.5 s)."""

    def __init__(self, store_=None):
        self._store = store_
        self._video = None
        self._last = None
        self._skipped = None     # (learned segment, when) after its skip
        self._by_skip = 0.0      # until when a forward jump is a Skip press

    def note_skip(self, now=None):
        """The app itself is about to seek (`skip_segment`): that jump is
        not the viewer finding the intro."""
        self._by_skip = (time.monotonic() if now is None else now) + 5.0

    def _store_(self):
        return self._store or store()

    def sample(self, video, position, now=None):
        try:
            self._sample(video, position,
                         time.monotonic() if now is None else now)
        except Exception:
            log.debug("skip learner failed", exc_info=True)

    def _sample(self, video, position, now):
        if video is not self._video:
            self._video, self._last, self._skipped = video, None, None
        if not isinstance(position, (int, float)):
            return
        previous, self._last = self._last, float(position)
        if previous is None:
            return
        delta = position - previous
        intros = list(getattr(video, "intros", None) or ())
        fronts = [s for s in intros if s.type == "Intro"]
        learned = next((s for s in fronts
                        if getattr(s, "source", None) == SOURCE_LEARNED), None)
        key = series_key(getattr(video, "item", None) or {})
        if key is None:
            return
        if LEARN_MIN_JUMP <= delta <= LEARN_MAX_JUMP:
            if learned is not None:
                if learned.start <= previous < learned.end:
                    self._skipped = (learned, now)
            elif (not fronts and OPTIONS.learn
                    and 0 <= previous <= LEARN_MAX_START
                    and now > self._by_skip
                    and not any(s.start <= previous < s.end
                                for s in (getattr(video, "intros", None)
                                          or ()))):
                self._store_().learn(key, previous, position)
        elif delta <= -BACK_MIN_JUMP and learned is not None:
            if not learned.start <= position < learned.end:
                return
            skipped = self._skipped
            self._skipped = None
            if skipped is not None and skipped[0] is learned \
                    and now - skipped[1] <= UNDO_WINDOW:
                self._store_().undo(key)
            else:
                self._store_().forget(key)
