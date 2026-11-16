"""What the bridge tells the UI about playback, worked out from observations.

The bridge's monitor thread samples the player twice a second (and when
poked) into an `Observation`; `Tracker.update` turns the difference from the
last one into protocol events -- ``ended``, ``error``, ``item``, ``tracks``,
``state``, ``position`` -- in that order. The player's hooks only *record*
facts (``mark_ended``, ``report_error``); every event is emitted from
`update`, so an ``item`` can never overtake the ``ended`` before it or the
``state`` after it.

Pure logic over plain values and duck-typed video objects -- no mpv, no
shim imports -- so the sequences can be asserted over several steps
(docs/testing.md section 6) without a player.

**When an item ends.** At the moment the player's current video changes
away from it, not when the next one starts opening: a replacement that
fails to load leaves the old one playing (a PlaybackInfo error) or stops it
(a failed load), and only the first rule reports both truthfully. The
reason is whatever a hook marked -- ``eof`` from the queue's finished
callback, ``error`` from a stream interrupted mid-item -- and otherwise
``replaced`` (another video took over) or ``stopped`` (nothing did).

**When a play session is done** (``reported``, BRIDGE.md section 5):
`PlaySessions` keeps each PlaySessionId's phase -- shown in a ``state``,
its Playing submitted, its Stopped submitted -- and lets exactly one
outcome through per id. The outcomes come from the bridge's hooks and the
client's ``session_stop`` wrapper, on whatever thread finished the attempt;
they are only queued here, and written by the monitor after the events of
its tick (`Tracker.drain_reported`), so a ``reported`` does not go out ahead
of a ``state`` sampled before it was decided -- unless that tick does not
come (an mpv that stops answering holds the monitor's sample), when the
bridge writes them directly after about a second (`Bridge._reported_loop`).
"""

import collections
import threading
import time
import weakref

from . import bridge_protocol as proto

#: Which reason wins when several are marked on one item: an interruption
#: the shim noticed outranks the end-of-file the same abort produced, and a
#: deliberate stop outranks a replacement still being opened.
_REASON_RANK = {"replaced": 0, "stopped": 1, "eof": 2, "error": 3}

#: At most one position event per this many seconds while playing.
POSITION_INTERVAL = 0.45


class DevItem:
    """A ``play_file`` playback: a URL in the same mpv, no Jellyfin item."""

    def __init__(self, url, title=None):
        self.url = url
        self.title = title
        #: "opening" until mpv has the file, then "active".
        self.phase = "opening"
        self.started = time.monotonic()
        #: Whether mpv was seen playing it (idle-active False).
        self.seen_active = False
        #: Set once the file has been handed to mpv; ``started`` is reset
        #: then, so the open timeout counts from the loadfile.
        self.loaded = False


class Observation:
    """One sample of everything the protocol reports."""

    def __init__(self, connected=False, window=None, video=None,
                 opening=None, pending=None, start_in_progress=False,
                 dev=None, position=None, duration=None, paused=None,
                 volume=None, muted=None, fullscreen=False, mpv_aid=None,
                 mpv_sid=None, external_subtitles=None, dev_idle=None,
                 held=False, restart_session=None, now=None):
        self.connected = connected
        #: "embedded" | "attached" | "standalone" | None (no window yet).
        self.window = window
        #: The player's current video (``PlayerManager._video``).
        self.video = video
        #: The video a ``play()`` call is working on, or None.
        self.opening = opening
        #: An accepted ``play`` not yet handed to the player:
        #: ``{"itemIds": [...], "index": n}``.
        self.pending = pending
        self.start_in_progress = start_in_progress
        self.dev = dev
        #: mpv's view, in seconds; None where mpv has no answer.
        self.position = position
        self.duration = duration
        self.paused = paused
        self.volume = volume
        self.muted = muted
        self.fullscreen = fullscreen
        self.mpv_aid = mpv_aid
        self.mpv_sid = mpv_sid
        self.external_subtitles = external_subtitles or {}
        #: mpv's idle-active, read only while a ``play_file`` is up.
        self.dev_idle = dev_idle
        #: Transport commands sent while opening are queued behind the
        #: start and have not landed yet (the bridge's ``_transport``).
        self.held = held
        #: ``(video, PlaySessionId)`` while a restart of ``video`` (a track
        #: or quality change) negotiates, before mpv has its load: the
        #: session mpv is still playing, which ``state`` shows until then.
        self.restart_session = restart_session
        self.now = time.monotonic() if now is None else now


def _item_id(obj):
    return proto.item_id_out(getattr(obj, "item_id", None))


def _queue(video):
    parent = getattr(video, "parent", None)
    try:
        ids = [proto.item_id_out(q.get("Id")) for q in (parent.queue or [])]
        return {"itemIds": ids, "index": int(parent.seq)}
    except Exception:
        return {"itemIds": [], "index": 0}


#: ``reported`` outcomes (BRIDGE.md section 5).
SENT, FAILED, NONE = "sent", "failed", "none"


class PlaySessions:
    """Each play session's phase, and its one ``reported`` outcome.

    Keyed on the PlaySessionId, which is what the UI tracks. Phases only
    go up -- ``seen`` (a ``state`` showed it), ``opened`` (its Playing was
    submitted), ``stopping`` (its Stopped was submitted) -- and end in one
    outcome. **At most one per id for the process**: the first `finish`
    wins; a later one (a report worker finishing after the shutdown's
    sweep, a second attempt) is dropped, and nothing re-opens a finished
    id. A server that hands one id to two plays (``fixed_sessions`` in the
    probes; Jellyfin makes a new one per PlaybackInfo) gets one outcome for
    both, which is what a UI tracking ids by value can follow.

    Finished ids are remembered up to `KEEP`, oldest dropped first."""

    SEEN, OPENED, STOPPING = "seen", "opened", "stopping"
    _RANK = {SEEN: 0, OPENED: 1, STOPPING: 2}
    KEEP = 4096

    def __init__(self):
        self._lock = threading.Lock()
        #: id -> [item id, phase], not finished yet.
        self._live = {}
        #: id -> outcome, finished.
        self._done = collections.OrderedDict()

    def note(self, session, item_id, phase):
        """Record that ``session`` (of ``item_id``) reached ``phase``. A
        finished id is left alone."""
        if not session:
            return
        with self._lock:
            if session in self._done:
                return
            entry = self._live.get(session)
            if entry is None:
                self._live[session] = [item_id, phase]
                return
            if item_id and not entry[0]:
                entry[0] = item_id
            if self._RANK[phase] > self._RANK[entry[1]]:
                entry[1] = phase

    def phase(self, session):
        """``seen``/``opened``/``stopping``, ``done``, or None (unknown)."""
        with self._lock:
            if session in self._done:
                return "done"
            entry = self._live.get(session)
            return entry[1] if entry is not None else None

    def finish(self, session, item_id, outcome):
        """The one outcome of ``session``: the ``reported`` payload, or
        None when it has had one already (or has no id)."""
        if not session:
            return None
        with self._lock:
            if session in self._done:
                return None
            entry = self._live.pop(session, None)
            if not item_id and entry is not None:
                item_id = entry[0]
            self._done[session] = outcome
            while len(self._done) > self.KEEP:
                self._done.popitem(last=False)
        return {"itemId": proto.item_id_out(item_id),
                "playSessionId": session, "outcome": outcome}

    def unfinished(self):
        """``[(id, item id, phase)]`` of every session with no outcome."""
        with self._lock:
            return [(s, e[0], e[1]) for s, e in self._live.items()]


class Tracker:
    """Turns observations into events. Thread-safe: hooks call
    `mark_ended` / `report_error` from any thread, the monitor calls
    `update`."""

    def __init__(self):
        self._lock = threading.RLock()
        self.announced = None
        self._marks = weakref.WeakKeyDictionary()
        self._errors = []
        #: Sticky until a new start or a stop: ``{"itemId", "code",
        #: "message"}``.
        self.error = None
        self.last_state = None
        self._last_key = None
        self._last_tracks = None
        self._last_position = None
        self._last_position_at = float("-inf")
        self._volume = 100
        self._muted = False
        self.sessions = PlaySessions()
        #: ``reported`` payloads waiting for the monitor (`drain_reported`).
        self._reported = []

    # -- facts from the hooks ----------------------------------------------

    def finish_session(self, session, item_id, outcome):
        """Queue the one ``reported`` event of ``session``. Any thread: it
        is only queued (`drain_reported`). True if it was the first."""
        data = self.sessions.finish(session, item_id, outcome)
        if data is None:
            return False
        with self._lock:
            self._reported.append(data)
        return True

    def sweep_sessions(self):
        """The shutdown's last word on every session with no outcome:
        ``failed`` once its Playing or Stopped was submitted (the final
        report not delivered, or never made), ``none`` while it was only
        opening. Returns how many."""
        count = 0
        for session, item_id, phase in self.sessions.unfinished():
            outcome = NONE if phase == PlaySessions.SEEN else FAILED
            if self.finish_session(session, item_id, outcome):
                count += 1
        return count

    def drain_reported(self):
        """The ``reported`` payloads queued since the last call."""
        with self._lock:
            out, self._reported = self._reported, []
        return out

    def reported_pending(self):
        """Whether a ``reported`` payload is queued and not yet drained."""
        with self._lock:
            return bool(self._reported)

    def mark_ended(self, video, reason):
        """Record why ``video`` will have ended, when it does. The
        highest-ranked reason marked wins."""
        if video is None:
            return
        with self._lock:
            try:
                current = self._marks.get(video)
                if (current is None or _REASON_RANK.get(reason, 0)
                        >= _REASON_RANK.get(current, 0)):
                    self._marks[video] = reason
            except TypeError:
                pass        # not weak-referenceable; the default applies

    def report_error(self, code, message, item_id=None, fatal=False,
                     sticky=True):
        """Queue an ``error`` event, and (``sticky``) hold the state at
        "error" until the next start or stop."""
        with self._lock:
            self._errors.append({"code": code, "message": proto.redact(message),
                                 "fatal": bool(fatal)})
            if sticky:
                self.error = {"itemId": item_id, "code": code,
                              "message": message}

    def clear_error(self):
        with self._lock:
            self.error = None

    # -- the snapshot ------------------------------------------------------

    def _subject(self, obs):
        """The video whose fields the snapshot reports: the one opening,
        else the one playing."""
        if obs.opening is not None:
            return obs.opening
        return obs.video

    def snapshot(self, obs):
        """The ``state`` snapshot (BRIDGE.md section 5) for ``obs``."""
        with self._lock:
            return self._snapshot(obs)

    def _snapshot(self, obs):
        if obs.volume is not None:
            try:
                self._volume = int(round(float(obs.volume)))
            except (TypeError, ValueError):
                pass
        if obs.muted is not None:
            self._muted = bool(obs.muted)
        dev = obs.dev
        subject = self._subject(obs)
        restarting = (obs.opening is not None and obs.opening is obs.video)
        if obs.pending is not None:
            state = "opening"
        elif ((obs.opening is not None and not restarting)
                or (restarting and obs.start_in_progress)
                or (obs.start_in_progress and obs.video is None)
                or (dev is not None and dev.phase == "opening")
                # A pause sent while opening has not landed: the start ends
                # unpaused, so "playing" now would be a flash of a state the
                # UI never asked for (BRIDGE.md section 5).
                or obs.held):
            state = "opening"
        elif obs.video is not None or (dev is not None
                                       and dev.phase == "active"):
            state = "paused" if obs.paused else "playing"
        elif self.error is not None:
            state = "error"
        else:
            state = "idle"

        snap = {
            "state": state,
            "connected": bool(obs.connected),
            "itemId": None,
            "mediaSourceId": None,
            "playSessionId": None,
            "playMethod": None,
            "positionTicks": 0,
            "durationTicks": None,
            "volume": self._volume,
            "muted": self._muted,
            "audioStreamIndex": None,
            "subtitleStreamIndex": None,
            "queue": {"itemIds": [], "index": 0},
            "fullscreen": bool(obs.fullscreen),
            "window": obs.window,
        }
        if obs.pending is not None:
            ids = [proto.item_id_out(i)
                   for i in obs.pending.get("itemIds") or []]
            index = int(obs.pending.get("index") or 0)
            snap["itemId"] = ids[index] if 0 <= index < len(ids) else None
            snap["queue"] = {"itemIds": ids, "index": index}
            return snap
        if state == "error":
            snap["itemId"] = proto.item_id_out(self.error.get("itemId"))
            return snap
        if subject is not None and state != "idle":
            snap["itemId"] = _item_id(subject)
            source = getattr(subject, "media_source", None) or {}
            snap["mediaSourceId"] = source.get("Id") or getattr(
                subject, "srcid", None)
            info = getattr(subject, "playback_info", None) or {}
            snap["playSessionId"] = info.get("PlaySessionId")
            restart = obs.restart_session
            if restart is not None and restart[0] is subject:
                # A restart still negotiating: mpv plays the session it had,
                # and a restart that finds no stream keeps it. Shown, the
                # new id took the UI's crash rule off the old session (it
                # tracks the latest one, and never reopens one it has seen),
                # which then played on with nobody owing it a Stopped.
                snap["playSessionId"] = restart[1]
            snap["playMethod"] = proto.play_method_name(subject)
            snap["queue"] = _queue(subject)
            if state in ("playing", "paused"):
                snap["audioStreamIndex"] = proto.audio_index_from_mpv(
                    subject, obs.mpv_aid)
                snap["subtitleStreamIndex"] = proto.subtitle_index_from_mpv(
                    subject, obs.mpv_sid, obs.external_subtitles)
            else:
                snap["audioStreamIndex"] = getattr(subject, "aid", None)
                snap["subtitleStreamIndex"] = getattr(subject, "sid", None)
        if state in ("playing", "paused"):
            snap["positionTicks"] = proto.seconds_to_ticks(obs.position) or 0
            snap["durationTicks"] = proto.seconds_to_ticks(obs.duration)
        if snap["durationTicks"] is None and subject is not None:
            runtime = (getattr(subject, "item", None) or {}).get(
                "RunTimeTicks")
            if isinstance(runtime, int) and not isinstance(runtime, bool):
                snap["durationTicks"] = runtime
        return snap

    # -- the update --------------------------------------------------------

    def _current(self, obs):
        if obs.video is not None:
            return obs.video
        if obs.dev is not None and obs.dev.phase == "active":
            return obs.dev
        return None

    def update(self, obs):
        """Events since the last update, in the order the UI must see
        them. Returns ``[(event, data), ...]``."""
        with self._lock:
            events = []
            current = self._current(obs)
            announced = self.announced
            if announced is not None and current is not announced:
                reason = self._marks.pop(announced, None) if _weakable(
                    announced) else None
                if reason is None:
                    reason = "replaced" if current is not None else "stopped"
                events.append(("ended", {"itemId": _item_id(announced),
                                         "reason": reason}))
                self.announced = None
                self._last_tracks = None
            for error in self._errors:
                events.append(("error", error))
            self._errors = []
            if current is not None and current is not self.announced:
                self.announced = current
                if not isinstance(current, DevItem):
                    # A start that got this far succeeded.
                    self.error = None
                    events.append(("item", proto.item_payload(
                        _item_id(current), getattr(current, "item", None))))
                    tracks = proto.tracks_payload(
                        getattr(current, "media_source", None))
                    events.append(("tracks", tracks))
                    self._last_tracks = tracks
                else:
                    self.error = None
            elif (current is not None and not isinstance(current, DevItem)
                    and not obs.start_in_progress):
                # A restart (a track change on a transcode, a quality change)
                # renegotiates and can change the source's streams.
                tracks = proto.tracks_payload(
                    getattr(current, "media_source", None))
                if tracks != self._last_tracks:
                    events.append(("tracks", tracks))
                    self._last_tracks = tracks

            if (self.error is not None and current is not None
                    and obs.pending is None and obs.opening is None
                    and not obs.start_in_progress):
                # Something is playing and nothing is starting: the error
                # was a replacement that failed before it reached mpv, and
                # what was playing carried on. It must not resurface as the
                # state when that ends. (A failed LOAD stops what was
                # playing before the start finishes, so it never gets here.)
                self.error = None
            snap = self._snapshot(obs)
            if snap["playSessionId"]:
                # The UI tracks a session from the first state showing it
                # until its `reported` (BRIDGE.md section 6).
                self.sessions.note(snap["playSessionId"], snap["itemId"],
                                   PlaySessions.SEEN)
            # Position and duration move continuously -- a live source's
            # duration grows with the demuxer -- and travel in `position`
            # events. A `state` is for everything else changing.
            key = dict(snap)
            key.pop("positionTicks")
            key.pop("durationTicks")
            state_changed = key != self._last_key
            if state_changed:
                events.append(("state", snap))
                self._last_key = key
            self.last_state = snap

            if snap["state"] in ("playing", "paused"):
                position = (snap["positionTicks"], snap["durationTicks"])
                due = (obs.now - self._last_position_at >= POSITION_INTERVAL
                       or state_changed)
                if position != self._last_position and due:
                    events.append(("position", {
                        "positionTicks": position[0],
                        "durationTicks": position[1]}))
                    self._last_position = position
                    self._last_position_at = obs.now
            else:
                self._last_position = None
            return events


def _weakable(obj):
    try:
        weakref.ref(obj)
        return True
    except TypeError:
        return False
