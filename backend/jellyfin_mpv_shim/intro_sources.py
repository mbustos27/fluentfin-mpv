"""fluentfin-mpv: where a Skip intro / Skip credits can come from when the
server has no media segment for it (docs/BRIDGE.md, ``segments.source``).

Precedence, per segment type: server > chapters > online > learned. A
source fills a type only when no source before it gave one, so the viewer
never gets two pills for the same part.

* **chapters** -- the file's own chapters named Intro, Opening, OP, Recap,
  Previously, Credits, Ending, ED and so on (`chapter_segments`); 1.6 skip:
  and ad chapters (Commercial, Ad break, Advertisement, Promo, Sponsor).
* **rule** -- 1.6 skip: the show's skip rules (`skip_rules`), the viewer's
  own: one that covers an intro or the credits takes its place.
* **online** -- `intro_online` (opt-in, off by default).
* **learned** -- a range the viewer skipped by hand in an earlier episode of
  the same season (`skip_rules.SkipWatcher`), kept on this PC in
  ``skip-rules.json`` in the backend's config directory (1.4 to 1.5:
  ``learned-intros.json``, migrated once). 1.5.1's "moves", judged within
  a season (skips by hand of one part 10 s or more apart): such a season
  is offered no learned spot (`skip_rules.SkipStore.note_hand_skip`).

Nothing here raises into playback: every entry point catches and logs.
"""

import logging
import re
import threading

# 1.6 skip: the options, the rules and the learned intros live in skip_rules
from .skip_rules import OPTIONS, SOURCE_RULE  # noqa: F401 (re-exported)

log = logging.getLogger("intro_sources")

SOURCE_SERVER = "server"
SOURCE_CHAPTERS = "chapters"
SOURCE_ONLINE = "online"
SOURCE_LEARNED = "learned"

TICKS = 10_000_000

#: How much earlier than the learned start a later episode's skip is
#: offered: the cold open before the titles is not the same length twice.
START_TOLERANCE = 5.0


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

# 1.6 skip: ad chapters. Whole words anywhere in the name ("Ad break 2",
# "Sponsor message"), so "Commercially" or "Promotional" is not one.
_AD = re.compile(r"\b(commercials?|ad ?breaks?|ads|adverts?|"
                 r"advertisements?|advertising|promos?|sponsors?|"
                 r"sponsored)\b")
#: A "Commercial" chapter longer than this is not one (an act named so).
_MAX_AD_SECONDS = 300.0


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
    if _AD.search(text):        # 1.6 skip
        return "Commercial"
    return None


def chapter_segments(item):
    """``[(type, start, end)]`` in seconds, at most one per type (1.6 skip:
    any number of ``Commercial``, each at most 5 minutes), from the
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
            if kind == "Commercial":        # 1.6 skip
                if end - start <= _MAX_AD_SECONDS:
                    out.append((kind, start, end))
                continue
            seen.add(kind)
            out.append((kind, start, end))
    except Exception:
        log.debug("unreadable chapters", exc_info=True)
        return []
    return out


# -- adding the segments to a video ------------------------------------------

def augment(video, make_segment, store_=None):
    """Adds to ``video.intros`` what the chapters, the show's skip rules, the
    online sources and the learned ranges offer for the types the server
    gave nothing for, and not the types set to off (a rule carries its own
    action, whatever its type is set to). ``make_segment(type, start, end,
    source)`` builds the segment. Never raises."""
    try:
        from . import conf, skip_rules

        item = getattr(video, "item", None) or {}
        have = {s.type for s in video.intros}
        wanted = {t for t in ("Intro", "Outro", "Recap", "Preview",
                              "Commercial")
                  if conf.segment_action(t) != "off"}
        chapter_types = set()

        def add(kind, start, end, source):
            if kind not in wanted or (kind in have
                                      and kind not in chapter_types):
                return
            if kind == "Commercial" and source == SOURCE_CHAPTERS:
                chapter_types.add(kind)     # every ad chapter, not the first
            video.intros.append(make_segment(kind, start, end, source))
            have.add(kind)

        for kind, start, end in chapter_segments(item):
            add(kind, start, end, SOURCE_CHAPTERS)

        # 1.6 skip: the show's rules, before the online answer (a rule
        # outranks it) and the learned intro.
        store = store_ or skip_rules.store()
        key = skip_rules.show_key(item)
        if key is not None:
            skip_rules.add_rules(video, make_segment, key, store)
            have = {s.type for s in video.intros}

        missing = wanted - have - {"Commercial"}
        if OPTIONS.lookup_online and missing:
            # Off the playback path: the answer is applied when it arrives
            # (`apply_online`), and playback never waits for it.
            start_online(video, make_segment, missing)

        if OPTIONS.learn and key and "Intro" in wanted and "Intro" not in have:
            learned = skip_rules.learned_intro(video, key, store)
            if learned is not None:
                start, end = learned
                start = max(0.0, start - START_TOLERANCE)
                # 1.6 skip: a rule over the learned guess is the viewer's
                # own word for that part; the guess is not offered too
                if not any(getattr(s, "source", None) == SOURCE_RULE
                           and min(s.end, end) - max(s.start, start)
                           >= 0.5 * (end - start) for s in video.intros):
                    add("Intro", start, end, SOURCE_LEARNED)
    except Exception:
        log.debug("extra segments failed", exc_info=True)


def apply_online(video, make_segment, kind, start, end):
    """A segment from an online source, added late: it takes the place of a
    learned one (online outranks learned) and yields to server, chapters or
    a skip rule. Returns whether it was added."""
    if any(s.type == kind and getattr(s, "source", "server")
           in (SOURCE_SERVER, SOURCE_CHAPTERS, SOURCE_ONLINE, SOURCE_RULE)
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
