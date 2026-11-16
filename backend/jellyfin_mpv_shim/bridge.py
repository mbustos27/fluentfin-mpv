"""Bridge mode: the shim as FluentFin's playback backend.

    python -I -m jellyfin_mpv_shim.bridge --config-dir DIR [--log-file PATH] [--dev]

FluentFin starts this as a child process and talks JSON lines over
stdin/stdout. The contract is ``BRIDGE.md`` at the repository root
(``../docs/BRIDGE.md`` from ``backend/``), protocol 1; how the shim is
wired behind it -- what is switched off, how auth and the window work, the
smoke test -- is docs/bridge.md.

Order matters here more than anywhere else in the tree, and the order is
the reason for the shape of `main`:

1. **stdout first.** fd 1 is duplicated for the protocol and everything
   else that could write there is pointed at stderr, before anything else
   is imported (`bridge_protocol.isolate_stdout`).
2. **argv second.** Our own flags are parsed, then the shim's parser is
   pre-seeded with ``--config`` so ``args.get_args()`` never reads the
   real ``sys.argv`` (it would refuse ``--config-dir`` with the app's own
   usage line; docs/testing.md section 2).
3. **No player until ``connect``.** Importing ``player`` builds mpv, and
   mpv has to know its window -- embedded (``wid``) or its own -- before it
   is built. So the bridge imports nothing that reaches ``player`` until
   ``connect`` has fixed the window.

Threads: a reader (stdin -> dispatch; answers ``ping``/``get_state``/
``shutdown`` itself), a control worker (every other command, in order), a
play worker (anything that blocks for a playback start), and a monitor that
samples the player and emits events through `bridge_state.Tracker`.
"""

import argparse
import logging
import os
import queue
import signal
import sys
import threading
import time
import weakref

from . import bridge_controls
from . import bridge_http
from . import bridge_protocol as proto
from . import bridge_state
from .bridge_protocol import (BAD_ARGS, INTERNAL, MPV_FAILED, NOT_CONNECTED,
                              NO_PLAYBACK, UNKNOWN_CMD, WINDOW_LOCKED,
                              AUTH_FAILED, SERVER_UNREACHABLE, BridgeError)

log = logging.getLogger("bridge")

#: How often the monitor samples the player, at most, when nothing pokes it.
MONITOR_INTERVAL = 0.5

#: How long ``connect`` waits for the server to confirm the token.
VERIFY_TIMEOUT = 10

#: How long a server switch waits for the playback it stopped to be gone
#: before going ahead anyway. The stop itself never runs on the switching
#: thread: a start -- or a next, previous or track restart -- holds the
#: player's lock across its server round trips, and a progress report holds
#: the report lock across its own, either of which a server that accepts
#: and then hangs stretches to minutes.
STOP_WAIT = 5.0

#: The whole shutdown, from the request to the answer (BRIDGE.md section 3):
#: the control worker's join, the player lock, the report lock and the
#: final Stopped report's delivery all share this one deadline. Whatever is
#: not done by then is skipped -- mpv ended without the lock, no report --
#: and the exit status is 1.
SHUTDOWN_BUDGET = 8.0

#: How far past that deadline the process may take to exit: the straggler
#: grace `exit_watchdog.finish` gives the shim's own threads is cut to fit.
#: The UI kills the backend 10 s after asking.
EXIT_SLACK = 1.0

#: How long a stop or a start waits for a start the play worker has
#: committed to -- past the bridge's last check, not yet flagged in the
#: player -- to flag itself, so it can be cancelled. A few bytecodes.
COMMIT_WAIT = 0.5

#: The least an mpv reply is waited for during the shutdown's orderly stop,
#: once the deadline has bounded it to what is left (`_final_report`).
IPC_FLOOR = 0.25

#: How long a queued ``reported`` waits for the monitor to write it before
#: it is written directly (BRIDGE.md section 5). The monitor reads mpv
#: before each tick, and an mpv that stops answering holds that read for as
#: long as an IPC reply is waited for (120 s on the external backend).
REPORTED_WAIT = 1.0


def default_config_dir():
    """``%LOCALAPPDATA%\\FluentFinMpv\\mpv-backend`` -- separate from a
    standalone shim's config, and from an upstream FluentFin install's data
    folder, so none of them share a lock, credentials or settings."""
    if os.name == "nt":
        base = os.environ.get("LOCALAPPDATA") or os.path.join(
            os.path.expanduser("~"), "AppData", "Local")
    else:
        base = os.environ.get("XDG_CONFIG_HOME") or os.path.join(
            os.path.expanduser("~"), ".config")
    return os.path.join(base, "FluentFinMpv", "mpv-backend")


def find_mpv_binary():
    """The external backend's mpv: on PATH, else winget's install.

    ``mpv.exe`` by name on Windows, never ``mpv``: PATHEXT puts ``.COM``
    first, and ``mpv.com`` is the console wrapper, not the player.
    """
    import glob
    import shutil

    found = shutil.which("mpv.exe" if os.name == "nt" else "mpv")
    if found:
        return found
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        if local:
            winget = os.path.join(local, "Microsoft", "WinGet")
            candidates = [os.path.join(winget, "Links", "mpv.exe")]
            candidates += sorted(glob.glob(os.path.join(
                winget, "Packages", "mpv*", "mpv.exe")))
            for path in candidates:
                if os.path.isfile(path):
                    return path
    return None


def classify_exception(error):
    """A protocol error code for something the server or network raised."""
    try:
        from jellyfin_apiclient_python.exceptions import HTTPException
    except Exception:       # pragma: no cover - the apiclient is required
        HTTPException = ()
    if HTTPException and isinstance(error, HTTPException):
        if error.status in ("Unauthorized", "AccessRestricted", 401, 403):
            return AUTH_FAILED
        return SERVER_UNREACHABLE
    try:
        import requests

        if isinstance(error, requests.RequestException):
            return SERVER_UNREACHABLE
    except Exception:       # pragma: no cover
        pass
    if isinstance(error, (ConnectionError, TimeoutError)):
        return SERVER_UNREACHABLE
    return INTERNAL


def _guid(value):
    """A Jellyfin id as BRIDGE.md compares them: case- and dash-insensitive
    (the server writes N format, FluentFin may hold the D format)."""
    return str(value).strip().replace("-", "").lower()


def _play_session(video, options=None):
    """The PlaySessionId a stop report for ``video`` (or with ``options``)
    would carry: what the server closes, so what "reported" is keyed on.
    A restart of the same video negotiates a new one."""
    session = (options or {}).get("PlaySessionId") if isinstance(
        options, dict) else None
    if session is None:
        info = getattr(video, "playback_info", None)
        if isinstance(info, dict):
            session = info.get("PlaySessionId")
    return session


def _video_state(video):
    """A shallow copy of ``video``'s attributes, taken before a restart:
    what its play() overwrites while it negotiates -- the PlaybackInfo and
    its session, the media source, the stream maps (`map_streams`), the
    play method and transcode flags (`_get_url_from_source`),
    ``auth_via_header`` -- so a restart that finds no stream can put back
    the one still playing (`Bridge._restart_not_taken`). None for an object
    with no ``__dict__``."""
    try:
        return dict(vars(video))
    except TypeError:
        return None


def _negotiated_stream(video):
    """Whether ``video``'s last negotiation found something to play:
    `get_playback_url` leaves no media source when there was none, and
    `_get_url_from_source` sets ``play_method`` at each exit that returns a
    url and clears it otherwise (media.py)."""
    return (getattr(video, "media_source", None) is not None
            and getattr(video, "play_method", None) is not None)


def _item_label(video):
    """``"Name" (<id>)``, for a message that has to name an item."""
    item_id = proto.item_id_out(getattr(video, "item_id", None))
    name = (getattr(video, "item", None) or {}).get("Name")
    return '"%s" (%s)' % (name, item_id) if name else str(item_id)


class _SessionLedger:
    """Which play session of which video something has gone out for -- a
    Stopped report, a stream release -- so nothing sends it twice.

    Keyed on the video **object and** its PlaySessionId, one session per
    video (a video plays one session at a time): the session alone was
    never pruned and missed a later play that reused it -- a server that
    hands out the same id, or the same item played again. Held weakly, so
    it is bounded by the videos still alive; `forget` drops a video's entry
    when it starts a new play session. A video that cannot be weakly
    referenced is not recorded (the shim's own behaviour: it may be
    reported twice).

    A report built from bookkeeping after mpv went away has no video to
    key on (``video=None``): it is recorded by its session alone, in a short
    list (`ORPHANS`) that any check of that session reads, and that a new
    play session under the same id leaves (`forget`)."""

    #: How many sessions reported without a video are remembered.
    ORPHANS = 64

    def __init__(self):
        self._lock = threading.Lock()
        self._sessions = weakref.WeakKeyDictionary()
        self._orphans = []

    def add(self, video, session):
        if session is None:
            return
        with self._lock:
            if video is None:
                if session not in self._orphans:
                    self._orphans.append(session)
                    del self._orphans[:-self.ORPHANS]
                return
            try:
                self._sessions[video] = session
            except TypeError:
                pass

    def has(self, video, session):
        """Whether ``session`` of ``video`` is recorded. With no video (a
        report built from bookkeeping after mpv went away, `_video` already
        cleared), whether any video's is."""
        if session is None:
            return False
        with self._lock:
            if session in self._orphans:
                return True
            if video is None:
                return session in list(self._sessions.values())
            try:
                return self._sessions.get(video) == session
            except TypeError:
                return False

    def forget(self, video, session=None):
        """``video`` starts a new play session (``session``, its id)."""
        with self._lock:
            if session is not None and session in self._orphans:
                self._orphans.remove(session)
            try:
                self._sessions.pop(video, None)
            except TypeError:
                pass

    def __len__(self):
        with self._lock:
            return len(self._sessions)


def describe_exception(error):
    status = getattr(error, "status", None)
    if status is not None:
        # The apiclient's HTTPException: a status, and a message that is
        # either the requests error (not shown: it names the url) or -- the
        # bridge's own (`bridge_http`) -- a sentence worth reading.
        message = getattr(error, "message", None)
        if isinstance(message, str) and message:
            return proto.redact("%s: %s" % (status, message))
        return proto.redact("%s (%s)" % (status, type(error).__name__))
    return proto.redact("%s: %s" % (type(error).__name__, error))


# ---------------------------------------------------------------------------
# The shim behind the bridge


class ShimRuntime:
    """Everything that touches the shim's own modules, imported late.

    The `Bridge` talks to the shim only through this, which is what lets the
    unit tests drive the protocol with a stand-in and no mpv.
    """

    #: Settings the bridge forces for the run, never written to conf.json
    #: (`apply_cli_override`). Each is something BRIDGE.md section 4 says
    #: the backend must not do, or something FluentFin owns instead.
    HEADLESS_OVERRIDES = (
        ("enable_gui", False),             # no library browser, no tray
        ("start_minimized", False),
        ("headless", False),
        ("browser_fullscreen", False),
        ("discord_presence", False),
        # Not wanted here, and the daily check runs inside a playback start,
        # under the player lock (perf-backend.md, in the repository root's
        # research notes).
        ("check_updates", False),
        ("mpris", False),
        ("input_gamepad", False),          # gamepad-to-browser navigation
        ("health_check_interval", None),   # no reconnect loop: no websocket
        ("auto_download_enable", False),   # the sync manager never starts
        ("write_logs", False),             # --log-file is ours
        # mpv stays up between plays: a re-created one costs ~240 ms to the
        # first frame against ~42 ms (measured, perf-backend.md), and an
        # embedded one would take the owner's video surface with it.
        ("mpv_idle_quit", False),
    )

    #: Forced for an embedded window: the owner sizes, raises and
    #: fullscreens it, and the video surface stays up for the session.
    EMBEDDED_OVERRIDES = (
        ("fullscreen", False),
        ("raise_mpv", False),
        ("remember_window_size", False),
    )

    @staticmethod
    def attached_overrides(pipe):
        """fluentfin-mpv player controls: an ``attached`` window. The shim's
        external backend, connecting to the UI's mpv on ``pipe`` instead of
        starting one (``mpv_ext_start``), whatever conf.json says; and with
        its runtime writes on (``mpv_ext_no_ovr`` off: the OSC lever and the
        track and title writes are how it plays). Like every override, for
        the run only: none of it reaches conf.json."""
        return (
            ("mpv_ext", True),
            ("mpv_ext_start", False),
            ("mpv_ext_ipc", pipe),
            ("mpv_ext_no_ovr", False),
        )

    def __init__(self, config_dir):
        self.config_dir = config_dir
        self.pm = None
        self.player_module = None
        self._threads = ()
        self.load_ok = True
        #: Guards `pm` against `_closing`: a player published after the
        #: shutdown looked is ended by whoever published it.
        self._lifecycle = threading.Lock()
        self._closing = False
        #: ``(item_id, play_session_id, outcome)`` for every Stopped report
        #: a client of ours made, once it returned or raised (`make_client`,
        #: `bridge_http.watch_stops`). Set by the `Bridge`.
        self.on_stop_outcome = None
        #: ``(pm)``, run by the shutdown's orderly stop under both locks
        #: right after its ``stop()`` (the name is older than that order):
        #: the end of an item the bridge held for a ``play``
        #: (`Bridge._end_held`), which that stop() returns early on -- the
        #: last item at its end -- and does not report. After it, as the
        #: ``stop`` command's own: run first, with mpv not answering, the
        #: held end's report waited out one reply (`get_timeline_options`
        #: swallows the timeout and leaves mpv for alive) and stop() then
        #: another on ``playback_abort`` -- past the exit watchdog, no
        #: answer (``held_hung``). stop()'s timeout marks mpv lost and
        #: reports the item from bookkeeping. Set by the `Bridge`.
        self.before_final_stop = None

    # -- settings ----------------------------------------------------------

    def load_settings(self):
        from . import conffile, i18n
        from .conf import settings
        from .constants import APP_NAME

        self.load_ok = settings.load(conffile.get(APP_NAME, "conf.json"))
        i18n.configure()
        for key, value in self.HEADLESS_OVERRIDES:
            self._override(key, value)
        if not settings.mpv_ext_path:
            path = find_mpv_binary()
            if path:
                self._override("mpv_ext_path", path)
        try:
            from .trickplay import cleanup_stale_files

            cleanup_stale_files()
        except Exception:
            log.debug("could not clean up trickplay files", exc_info=True)
        self._check_shader_pack()
        return settings

    def _check_shader_pack(self):
        """Shader profiles off for the run when there is no pack to load.

        The pack is fetched by ``gen_pkg.sh`` and is not in git, so a dev
        checkout has none. The shim already copes -- every reader of the
        profile manager allows for None -- but the menu says so with a
        logged traceback on every start. Turning the feature off says the
        same thing in one line, with the same result.
        """
        from . import conffile
        from .conf import settings
        from .constants import APP_NAME
        from .utils import get_resource

        if not settings.shader_pack_enable:
            return
        places = [get_resource("default_shader_pack")]
        if settings.shader_pack_custom:
            places.insert(0, os.path.join(conffile.confdir(APP_NAME),
                                          "shader_pack"))
        for place in places:
            for name in ("pack-next.json", "pack.json"):
                if os.path.exists(os.path.join(place, name)):
                    return
        log.info("No shader pack is installed; shader profiles are off "
                 "for this run.")
        self._override("shader_pack_enable", False)

    @staticmethod
    def _override(key, value):
        from .conf import settings

        try:
            if getattr(settings, key) != value:
                settings.apply_cli_override(key, value)
        except (KeyError, AttributeError):
            log.warning("bridge override %s: no such setting", key)

    @staticmethod
    def log_level():
        from .conf import settings
        from .log_utils import _level

        return _level(settings.mpv_log_level)

    # -- the player --------------------------------------------------------

    def start_player(self, window):
        """Build mpv for ``window`` and start the shim's worker threads.
        Returns ``connect``'s result."""
        from . import window_embed

        embedded = window["mode"] == "embedded"
        # fluentfin-mpv player controls: the UI's own mpv, attached to over
        # its pipe (BRIDGE.md section 4); its window is the UI's, as embedded.
        attached = window["mode"] == "attached"
        if embedded or attached:
            for key, value in self.EMBEDDED_OVERRIDES:
                self._override(key, value)
        if attached:
            for key, value in self.attached_overrides(window["ipc"]):
                self._override(key, value)
        window_embed.configure(window["hwnd"] if embedded else None,
                               attach=attached)
        # The bar mpv draws, and its look (fluentfin_bar, fluentfin_osc).
        from . import fluentfin_bar, fluentfin_osc
        from .conf import settings

        bar = fluentfin_bar.choose(settings.osc_style, window["mode"],
                                   fluentfin_osc.user_sets("osd-fonts-dir"))
        fluentfin_osc.configure(True, bar=bar)
        if bar is not None and settings.kb_kill_shader == "k":
            # FluentFin's key map has k for pause, as FluentFin's own
            # controls do; the shim's binding is forced and would win.
            self._override("kb_kill_shader", "K")
        try:
            from . import player as player_module
        except BaseException:
            window_embed.configure(None)
            raise
        pm = player_module.playerManager
        # Published the moment the import has built mpv, before anything
        # else can fail or be interrupted: a shutdown landing while this is
        # still starting must find the mpv to end it (stdin EOF during the
        # first connect left an orphaned mpv.exe).
        if not self._publish(pm, player_module):
            raise RuntimeError("the backend is shutting down")
        from .action_thread import actionThread
        from .timeline import timelineManager

        timelineManager.start()
        pm.timeline_trigger = timelineManager.trigger
        actionThread.start()
        pm.action_trigger = actionThread.trigger
        self._threads = (("timeline", timelineManager),
                         ("action thread", actionThread))
        logging.getLogger().setLevel(self.log_level())
        try:
            version = str(pm._player.mpv_version)
        except Exception:
            version = "unknown"
        return {"mpv": version,
                "mpvBackend": ("external" if player_module.is_using_ext_mpv
                               else "libmpv")}

    def _publish(self, pm, player_module):
        """Make the player the shutdown's to end. False when the shutdown
        has already run -- it found no player then, so this one is ended
        here instead. Exactly one of the two ends it."""
        with self._lifecycle:
            self.pm = pm
            self.player_module = player_module
            closing = self._closing
        if closing:
            log.info("The backend is shutting down; ending the mpv that was "
                     "just started.")
            try:
                pm.terminate()
            except Exception:
                log.exception("Error ending mpv")
        return not closing

    def mpv_errors(self):
        if self.player_module is None:
            return (BrokenPipeError,)
        return self.player_module._mpv_errors

    # -- fluentfin-mpv: segments and the queue (BRIDGE.md section 5) --------

    @staticmethod
    def segments_of(video):
        """``video``'s media segments as ``(type, start, end, action)``:
        the shim's own list (`media.Video.intros`, fetched at the start for
        the types not set to off) with what each type is set to do."""
        from . import conf

        out = []
        for segment in list(getattr(video, "intros", None) or ()):
            try:
                out.append((segment.type, segment.start, segment.end,
                            conf.segment_action(segment.type)))
            except Exception:
                log.debug("unreadable segment", exc_info=True)
        return out

    @staticmethod
    def auto_play():
        """Whether the shim plays the next queue item at an item's end."""
        from .conf import settings

        return bool(settings.auto_play)

    @staticmethod
    def segment_label(segment_type):
        """The Skip button's text for a segment type, in the shim's
        language ("Skip Intro")."""
        from .media import segment_labels

        return segment_labels(segment_type)[0]

    # -- the server --------------------------------------------------------

    def make_client(self, server):
        """A Jellyfin client speaking as FluentFin: its token, user, device
        id/name and client name, so the server sees one session. REST only:
        no login, no websocket, no capabilities post (those are
        FluentFin's)."""
        from jellyfin_apiclient_python import JellyfinClient

        from .conf import settings

        client = JellyfinClient(allow_multiple_clients=True)
        client.config.data["app.default"] = True
        client.config.app(server["clientName"], server["clientVersion"],
                          server["deviceName"], server["deviceId"])
        client.config.data["http.user_agent"] = "%s/%s" % (
            server["clientName"], server["clientVersion"])
        client.config.data["auth.ssl"] = not settings.ignore_ssl_cert
        if settings.tls_client_cert:
            client.config.data["auth.tls_client_cert"] = settings.tls_client_cert
            client.config.data["auth.tls_client_key"] = settings.tls_client_key
            client.config.data["auth.tls_server_ca"] = settings.tls_server_ca
            client.auth.create_session_with_client_auth()
        client.config.auth(server["url"], server["userId"], server["token"],
                           not settings.ignore_ssl_cert)
        client.logged_in = True
        client.start(websocket=False)
        # A retried POST stays a POST, and a switch can retire the client
        # (bridge_http; docs/bridge.md section 3).
        bridge_http.guard(client)
        # How each Stopped report ended, for BRIDGE.md's `reported`.
        bridge_http.watch_stops(client, self._stop_outcome)
        # A PlaybackInfo the apiclient answered None for (a 500, a body that
        # is not JSON) raises where it comes back: `server_unreachable`.
        bridge_http.check_play_info(client)
        return client

    def _stop_outcome(self, item_id, session, outcome):
        callback = self.on_stop_outcome
        if callback is not None:
            callback(item_id, session, outcome)

    @staticmethod
    def retire_client(client, grace=None):
        """Nothing waits on ``client`` for long from now on: a server switch
        (or a shutdown abandoning a ``connect``) has moved on from it."""
        bridge_http.retire(client, grace)

    def verify_client(self, client, server):
        """One cheap authenticated request -- ``GET /Users/Me`` -- on the
        client that will play, bounded, so a bad token is ``auth_failed``
        and a dead server ``server_unreachable`` at ``connect`` rather than
        at the first play. It also warms that client's connection: measured
        15-26 ms for a cold first request against under 1 ms after
        (perf-backend.md).

        ``Users/Me`` answers with the user **the token belongs to**, so an
        ``Id`` that is not ``connect.user.id`` is a token for somebody else
        (``auth_failed``). ``GET /Users/{id}`` only showed the token was
        good on that server, not whose it is. 401/403 are ``auth_failed``;
        no connection, a timeout or a 5xx are ``server_unreachable``. **The
        apiclient returns None for a 500** (and for a body that is not
        JSON) rather than raising, so "no exception" is not success: only a
        user object is.
        """
        try:
            me = client.jellyfin._http("GET", "Users/Me", {
                "params": {}, "timeout": VERIFY_TIMEOUT, "retry": 0})
        except Exception as error:
            raise BridgeError(classify_exception(error),
                              "the server did not accept the connection: "
                              + describe_exception(error))
        if not isinstance(me, dict) or not isinstance(me.get("Id"), str):
            raise BridgeError(SERVER_UNREACHABLE,
                              "the server did not answer the user check "
                              "(an error, or not a Jellyfin server)")
        if _guid(me["Id"]) != _guid(server["userId"]):
            raise BridgeError(AUTH_FAILED, "the access token belongs to "
                              "another user")

    @staticmethod
    def stop_client(client):
        try:
            client.stop()
        except Exception:
            log.debug("could not stop a client", exc_info=True)

    def start_playback(self, client, user_id, request):
        """The server's remote Play path (`event_handler.start_playback`).

        A track pick counts as deliberate (``explicit_tracks``) only when
        both indices are given: with just one, the other would be read as
        "none", which is not what a remote Play means by leaving it out.
        """
        from .event_handler import start_playback

        aid = request["audioStreamIndex"]
        sid = request["subtitleStreamIndex"]
        return start_playback(
            client,
            request["itemIds"],
            start_index=request["startIndex"],
            offset_ticks=request["startPositionTicks"],
            user_id=user_id,
            aid=aid,
            sid=sid,
            srcid=request["mediaSourceId"],
            explicit_tracks=aid is not None and sid is not None,
            pause_stills=len(request["itemIds"]) == 1,
        )

    def set_idle_volume(self, volume):
        """Volume with nothing playing: the shim keeps one per kind of item
        and applies it at each start, so set both."""
        from .conf import settings

        changed = False
        for key in ("video_volume", "music_volume"):
            if getattr(settings, key) != volume:
                setattr(settings, key, volume)
                changed = True
        if changed:
            try:
                settings.save()
            except Exception:
                log.debug("could not save the volume", exc_info=True)

    def shutdown(self, deadline=None):
        """Stop playback and deliver its final report, then end mpv and the
        shim's threads, all by ``deadline`` (``time.monotonic()``; default
        `SHUTDOWN_BUDGET` from now). Each step on its own: one failing must
        not strand the rest.

        **Bounded by the one deadline** (BRIDGE.md section 3). The orderly
        stop needs the player's lock, which a start (or next, previous, a
        track restart) holds across its server round trips, and the report
        lock, which a progress report holds across its own; the report then
        has to be delivered. A server that is merely slow manages all three
        in time and the shutdown is the ordinary one. One that does not gets
        mpv ended without the locks and without the report -- which could
        only have gone to that same server -- and False is returned (exit
        status 1). No step waits on its own clock: the shim's own bounds
        (the reporter's 5 s drain, a thread's 15 s join) would add up past
        the UI's 10 s kill."""
        if deadline is None:
            deadline = time.monotonic() + SHUTDOWN_BUDGET
        with self._lifecycle:
            self._closing = True
            pm = self.pm
        if pm is None:
            return True
        from .player import bound_ipc_replies

        try:
            pm.cancel_load()
        except Exception:
            log.debug("cancel_load failed", exc_info=True)
        bound_ipc_replies()
        if self._final_report(pm, deadline, bound_ipc_replies):
            # The report is delivered, so terminate()'s own stop() and drain
            # find nothing left to do.
            self._bound_replies(bound_ipc_replies, deadline)
            self._run_steps([("player", pm.terminate)])
            self._stop_threads(deadline)
            return True
        self._run_steps([("mpv", lambda: self._end_mpv_unlocked(pm))])
        # Told to halt, not joined: the action thread may be parked on the
        # player's lock, the timeline thread on the report lock or inside
        # the request that holds it. exit_watchdog.finish bounds what is
        # left (`EXIT_SLACK`).
        for name, thread in self._threads:
            try:
                thread.halt = True
                thread.trigger.set()
            except Exception:
                log.debug("could not halt %s", name, exc_info=True)
        return False

    @staticmethod
    def _bound_replies(bound_ipc_replies, deadline):
        """mpv's replies waited for at most what is left of the deadline
        (`IPC_FLOOR` at least), not the teardown's 5 s each."""
        try:
            bound_ipc_replies(max(IPC_FLOOR, deadline - time.monotonic()))
        except Exception:
            log.debug("could not bound mpv's replies", exc_info=True)

    def _final_report(self, pm, deadline, bound_ipc_replies=None):
        """The orderly stop, if both locks come free by ``deadline``, and the
        delivery of its Stopped report. True only if both happened.

        Both locks are held across ``stop()`` (they are re-entrant, and
        ``stop()`` takes them in this order itself), so nothing can slip a
        report in between: the timeline's progress is gated off under them,
        and the reporter's ``session_playing`` takes the report lock. They
        are let go before the drain, which the reporter's worker may need
        the report lock for.

        ``stop()`` talks to mpv, so once the locks are had its replies are
        bounded to what is left (`_bound_replies`): a start holding the lock
        across a slow server request let it go mid-budget, and an mpv that
        had stopped answering then kept ``stop()`` 5 s on one reply, past
        the exit watchdog (``hung_mpv_next``). A reply that times out is
        mpv lost: the shim ends it (`_handle_mpv_disconnect`)."""
        def left():
            return max(0.0, deadline - time.monotonic())

        if not pm._lock.acquire(timeout=left()):
            log.error("The player is still busy after the shutdown's %.0fs "
                      "-- a request to the server that is not answering. "
                      "Ending mpv without the final playback report.",
                      SHUTDOWN_BUDGET)
            return False
        try:
            if not pm._tl_lock.acquire(timeout=left()):
                log.error("A playback report is still waiting on the server "
                          "after the shutdown's %.0fs. Ending mpv without "
                          "the final playback report.", SHUTDOWN_BUDGET)
                return False
            try:
                pm.should_send_timeline = False
                if bound_ipc_replies is not None:
                    self._bound_replies(bound_ipc_replies, deadline)
                steps = [("playback", pm.stop)]
                held = self.before_final_stop
                if held is not None:
                    # After stop(), which returns early -- no report -- for
                    # the last item at its end (`Bridge._end_held`); never
                    # before it: with mpv not answering, two replies waited
                    # out, past the exit watchdog (``held_hung``).
                    steps.append(("held end", lambda: held(pm)))
                self._run_steps(steps)
            finally:
                pm._tl_lock.release()
        finally:
            pm._lock.release()
        reporter = getattr(pm, "_reporter", None)
        if reporter is not None and not reporter.drain(left()):
            log.error("The final playback report was not delivered within "
                      "the shutdown's %.0fs. Ending mpv without waiting for "
                      "it.", SHUTDOWN_BUDGET)
            return False
        return True

    def _stop_threads(self, deadline):
        """``stop()`` for the shim's threads, with the joins cut to the
        deadline (their own is 15 s each)."""
        for name, thread in self._threads:
            log.info("Shutting down: %s", name)
            try:
                thread.halt = True
                thread.trigger.set()
                thread.join(max(0.0, deadline - time.monotonic()))
                if thread.is_alive():
                    log.warning("The %s did not stop in time; exiting "
                                "without it.", name)
            except Exception:
                log.exception("Error shutting down %s", name)

    @staticmethod
    def _run_steps(steps):
        for name, stop in steps:
            log.info("Shutting down: %s", name)
            try:
                stop()
            except Exception:
                log.exception("Error shutting down %s", name)

    def _end_mpv_unlocked(self, pm):
        """End mpv with the player's lock held elsewhere. Only the external
        backend has a process to end: libmpv runs inside this one and goes
        with it."""
        module = self.player_module
        if module is None or not getattr(module, "is_using_ext_mpv", False):
            return
        module._mark_own_close(pm._player)
        pm._player.terminate()

    def end_mpv_process(self):
        """The process's last step, on both ways out of `exit_watchdog`
        (``finish`` and the forced exit at the watchdog's deadline): an
        external mpv still running is ended by its process handle --
        TerminateProcess, never its IPC, which is what may have stopped
        answering. Nothing to do after an orderly shutdown, which ended it.

        Inside the UI's job object the job would end it anyway; outside one
        (a probe, a dev run, a job that failed to apply) nothing else does.
        Measured: an mpv that stopped answering while a ``next`` held the
        player's lock across a slow PlaybackInfo kept the orderly stop on
        its IPC until the watchdog ended the backend, and that mpv outlived
        it (``hung_mpv_next``)."""
        module = self.player_module
        pm = self.pm
        if pm is None or not getattr(module, "is_using_ext_mpv", False):
            return
        handle = getattr(pm, "_player", None)
        # Plain attributes (python-mpv-jsonipc sets them in __init__): no
        # property read, so nothing goes over the IPC.
        process = getattr(getattr(handle, "mpv_process", None), "process",
                          None)
        try:
            if process is None or process.poll() is not None:
                return
            process.kill()
        except Exception:
            return
        log.warning("mpv was still running at the exit; ended its process.")


# ---------------------------------------------------------------------------
# The protocol


class Bridge:
    """One backend process's protocol state. See the module docstring for
    the threads."""

    def __init__(self, writer, runtime, dev=False):
        self.writer = writer
        self.runtime = runtime
        self.dev = dev
        self.tracker = bridge_state.Tracker()
        #: fluentfin-mpv: who draws the player controls (``set_controls``).
        self.controls = bridge_controls.UiControls()
        #: fluentfin-mpv: ``(mpv handle, skip text, next flag)`` last
        #: written to ``user-data/fluentfin/skip`` and ``/next`` for the
        #: bar's Skip and Next buttons (`_push_offer`).
        self._offer_pushed = None

        #: Fixed by the first connect for the life of the process.
        self.window = None
        self.player_info = None
        self.client = None
        self.server = None
        self.connected = False

        self._state_lock = threading.RLock()
        #: Bumped by every command that replaces what is playing or about
        #: to (play, play_file, stop, a server switch). A queued start from
        #: an older generation is dropped before it reaches the player.
        self._gen = 0
        self.pending = None
        self.opening = None
        #: The video a play() hook has passed its last supersede check for
        #: and handed to the player, until the player returns. Between the
        #: two the player has not yet flagged the start, so a cancel would
        #: find nothing to cancel: `_supersede` waits for the flag
        #: (`COMMIT_WAIT`).
        self._committed = None
        #: The play sessions reported Stopped, and those whose stream was
        #: released, through the player's own calls (`_install_hooks`).
        #: Nothing sends either twice: a stop, a failed or cancelled
        #: replacement, a next whose start did not take, the shutdown.
        #: Separate, because an end of file reports and *then* releases.
        self._reported = _SessionLedger()
        self._released = _SessionLedger()
        #: The outgoing items of replacements that are starting, or that
        #: were cancelled or failed to load: whatever ends them now, it is
        #: not their end of file. One that outlives a replacement failing
        #: before mpv (PlaybackInfo) leaves it, and plays on.
        self._replaced = weakref.WeakSet()
        #: A client `connect` is verifying: a shutdown retires it, so the
        #: control worker it holds can be joined.
        self._verifying = None
        self.dev_item = None
        #: Transport commands queued behind a start (`_transport`). While
        #: any is, the state stays "opening": the start's own last step is
        #: an unpause, and the queued pause lands a moment later.
        self._held = set()
        self._cancelled = weakref.WeakSet()
        self._start_failed = weakref.WeakSet()
        #: ``(video, has_lock, epoch)``: the finished callback of an item
        #: that reached its end while a ``play`` command was replacing it,
        #: held back so the queue's advance does not start an item that
        #: play then loads over (`_install_hooks`). Run after all if that
        #: play leaves the item current (`_finish_deferred`).
        self._deferred_finish = None
        #: ``(video, PlaySessionId)``: a restart of ``video`` negotiating,
        #: before mpv has its load -- ``state`` still shows that session
        #: (`bridge_state.Observation.restart_session`).
        self._restart_shown = None
        #: Each video's quality override (``trs_ovr``) as of the start of it
        #: that last took: the one its stream was negotiated with. A quality
        #: change writes the new one before its restart begins -- the menu
        #: on its own thread, the restart queued behind it -- so the
        #: restart's snapshot already has it (`_put_back`).
        self._quality = weakref.WeakKeyDictionary()
        #: The video an answered ``stop`` is ending, until its deferred
        #: stop has run: transport treats it as already gone
        #: (`_playing`), so a ``next`` sent right after the ``stop`` is
        #: ``no_playback`` rather than a start the stop then cuts short.
        self._stop_for = None
        #: Held while ``state``/``reported`` events are worked out and
        #: written (the monitor, `_reported_loop`) and while the shutdown
        #: writes the last ``reported`` events, so none is written after the
        #: answer.
        self._emit_lock = threading.Lock()
        #: Set when an outcome is queued: `_reported_loop` writes it if the
        #: monitor has not within `REPORTED_WAIT`.
        self._reported_due = threading.Event()
        #: Set, under `_emit_lock`, once the shutdown has written the last
        #: ``reported`` events: nothing writes one after that.
        self._reported_closed = False
        runtime.on_stop_outcome = self._stop_outcome
        runtime.before_final_stop = (
            lambda pm: self._end_held(pm, unload=False))

        self._control_q = queue.Queue()
        self._play_q = queue.Queue()
        self._play_thread = None
        self._control_thread = None
        self._threads = []
        #: Per thread. ``replacing``: the start being made is a ``play``
        #: command's, which owes the outgoing item its Stopped report
        #: (`_report_outgoing`) -- its ``pending`` dict. ``job_gen``: the
        #: generation of the play-worker job running on this thread.
        #: ``start``: the play() this thread is in, ``{"video", "loaded"}``
        #: -- `_play_media`, which calls the load hooks, runs on the thread
        #: of the play() that called it, so a load start or error is that
        #: start's, whatever another thread is starting (`_on_load_start`).
        #: ``tracks_before``: the tracks a `set_streams` on this thread is
        #: changing from, for a restart that cannot apply them.
        self._local = threading.local()
        self._wake = threading.Event()
        self._monitor_stop = threading.Event()
        self._tick_cond = threading.Condition()
        self._ticks = 0

        self._shutdown = threading.Event()
        self._exit_status = None
        #: When the first shutdown request came: `SHUTDOWN_BUDGET` counts
        #: from here, and `shutdown_deadline` is set from it by `stop`.
        self._shutdown_at = None
        self.shutdown_deadline = None
        self.clean_shutdown = True
        self.shutdown_request_id = None
        self.shutdown_reason = None
        writer.on_broken = lambda: self.request_shutdown("stdout closed", 0)

    # -- lifecycle ---------------------------------------------------------

    def start(self, stdin=None):
        """``hello`` first, then the threads. Writing ``hello`` before the
        reader starts is what guarantees it is the first line: anything the
        UI sends early waits in the pipe until it is read."""
        from .constants import CLIENT_VERSION

        self.tracker.update(self._observe())     # primes; nothing changed
        self.writer.event("hello", {
            "protocol": proto.PROTOCOL_VERSION,
            "backend": "jellyfin-mpv-shim",
            "version": CLIENT_VERSION,
            "bridge": proto.BRIDGE_VERSION,
        })
        self._control_thread = self._spawn("bridge-control",
                                           self._control_loop)
        self._play_thread = self._spawn("bridge-play", self._play_loop)
        self._spawn("bridge-monitor", self._monitor_loop)
        self._spawn("bridge-reported", self._reported_loop)
        if stdin is not None:
            self._spawn("bridge-reader", self._read_loop, stdin)

    def _spawn(self, name, target, *args):
        thread = threading.Thread(target=target, args=args, name=name,
                                  daemon=True)
        thread.start()
        self._threads.append(thread)
        return thread

    def request_shutdown(self, reason, status=0):
        with self._state_lock:
            if self._exit_status is None:
                self._exit_status = status
                self.shutdown_reason = reason
                self._shutdown_at = time.monotonic()
        self._shutdown.set()

    def wait(self):
        """Block until something asks for a shutdown; return the status."""
        try:
            while not self._shutdown.wait(0.5):
                pass
        except KeyboardInterrupt:
            self.request_shutdown("interrupted", 0)
        return self._exit_status or 0

    def stop(self):
        """Everything up to the process exit: playback stopped and reported,
        mpv gone, the shutdown request answered. Does not exit.

        **One deadline**, `SHUTDOWN_BUDGET` from the request, shared by
        every step below (BRIDGE.md section 3); `shutdown_deadline` keeps
        it for `main`'s exit.

        The control worker is joined (bounded) **before** the shutdown: a
        first ``connect`` it is running may be starting mpv right now, and a
        shutdown that ran first would find no player and leave that mpv
        running after we exit. Commands still queued behind it are answered
        ``internal`` rather than run (`_control_loop`). A ``connect`` waiting
        on its new server's answer is not worth the budget: that client is
        retired, so the check gives up at once."""
        self._shutdown.set()
        with self._state_lock:
            if self._shutdown_at is None:
                self._shutdown_at = time.monotonic()
            deadline = self._shutdown_at + SHUTDOWN_BUDGET
            # Read after the flag is up; `_switch_server` sets this and then
            # reads the flag, so one of the two retires the client.
            verifying = self._verifying
        self.shutdown_deadline = deadline
        log.info("Shutting down (%s).", self.shutdown_reason)
        self._monitor_stop.set()
        self._wake.set()
        self._supersede()           # queued starts dropped, one in flight cut
        if verifying is not None:
            self.runtime.retire_client(verifying, 0)
        self._control_q.put(None)
        self._play_q.put(None)
        control = self._control_thread
        if (control is not None and control.is_alive()
                and control is not threading.current_thread()):
            control.join(max(0.0, deadline - time.monotonic()))
            if control.is_alive():
                log.warning("The control worker is still busy at the "
                            "shutdown's deadline; shutting down anyway.")
        #: False when mpv had to be ended without the orderly stop, or the
        #: final report was not delivered in time.
        self.clean_shutdown = self.runtime.shutdown(deadline) is not False
        # BRIDGE.md section 5: every session still without an outcome gets
        # one before the answer -- `failed` once its Playing or Stopped
        # went (the final report not delivered by the deadline, or never
        # made), `none` while it was only opening. A report worker that
        # finishes after this is the second outcome, and dropped.
        swept = self.tracker.sweep_sessions()
        if swept:
            log.info("Shutting down with %d play session(s) not reported "
                     "done.", swept)
        if self.client is not None:
            self.runtime.stop_client(self.client)
        self._flush_reported(final=True)
        if self.shutdown_request_id is not None:
            self.writer.respond(self.shutdown_request_id, {})
        while True:
            try:
                req = self._control_q.get_nowait()
            except queue.Empty:
                break
            if req is not None:
                self.writer.fail(req.id, INTERNAL, "the backend is shutting "
                                 "down")
        self.writer.close()

    # -- reading -----------------------------------------------------------

    def _read_loop(self, stream):
        try:
            while not self._shutdown.is_set():
                line = stream.readline()
                if not line:
                    break
                try:
                    if self.handle_line(line) == "stop":
                        return
                except Exception:
                    # One line must never take the reader -- and with it
                    # every later command -- down.
                    log.exception("could not handle a line")
        except Exception:
            log.exception("stdin reader failed")
        self.request_shutdown("stdin closed", 0)

    def handle_line(self, raw):
        """Parse and dispatch one line. Returns "stop" after ``shutdown``."""
        try:
            req = proto.decode_line(raw)
        except proto.FramingError as error:
            log.warning("Ignoring a line that is not a request: %s", error)
            self.writer.event("error", {"code": BAD_ARGS,
                                        "message": str(error),
                                        "fatal": False})
            return None
        except BridgeError as error:
            self.writer.fail(error.request_id, error.code, error.message)
            return None
        if req is None:
            return None
        if req.cmd == "connect":
            # Before anything can log it -- including the line below.
            proto.add_secret(req.args.get("accessToken"))
        log.info("<- %s (id %d)", req.cmd, req.id)
        if req.cmd == "ping":
            self.writer.respond(req.id, {})
        elif req.cmd == "get_state":
            self.writer.respond(req.id, self.get_state())
        elif req.cmd == "shutdown":
            self.shutdown_request_id = req.id
            self.request_shutdown("shutdown command", 0)
            return "stop"
        elif req.cmd not in self.HANDLERS or (req.cmd == "play_file"
                                              and not self.dev):
            self.writer.fail(req.id, UNKNOWN_CMD,
                             "unknown command %r" % req.cmd)
        else:
            self._control_q.put(req)
        return None

    def _control_loop(self):
        while True:
            req = self._control_q.get()
            if req is None:
                return
            try:
                if self._shutdown.is_set():
                    # Answered, not run: the shutdown is waiting on this
                    # thread (`stop`), and a queued play must not start now.
                    self.writer.fail(req.id, INTERNAL,
                                     "the backend is shutting down")
                else:
                    self.execute(req)
            except Exception:
                # `execute` answers everything itself; this is the backstop
                # that keeps one bad command from ending the worker.
                log.exception("control worker: %s failed", req.cmd)

    def execute(self, req):
        """Run one queued command and answer it. Exactly one response."""
        try:
            try:
                result = self.HANDLERS[req.cmd](self, req.args)
            except BridgeError as error:
                log.info("-> %s (id %d) failed: %s", req.cmd, req.id,
                         error.code)
                self.writer.fail(req.id, error.code, error.message)
            except Exception as error:
                log.exception("%s failed", req.cmd)
                self.writer.fail(req.id, INTERNAL, describe_exception(error))
            else:
                self.writer.respond(req.id, result or {})
        except Exception:
            # The answer itself could not be written (it would not encode):
            # still exactly one response, with nothing of it in it.
            log.exception("could not answer %s (id %d)", req.cmd, req.id)
            self.writer.fail(req.id, INTERNAL, "the response could not be "
                             "encoded")
        finally:
            self.poke()

    # -- the play worker ---------------------------------------------------

    def _queue_play(self, label, fn):
        with self._state_lock:
            gen = self._gen
        self._play_q.put((gen, label, fn))

    def _play_loop(self):
        while True:
            job = self._play_q.get()
            if job is None:
                return
            gen, label, fn = job
            with self._state_lock:
                if gen < self._gen or self._shutdown.is_set():
                    log.info("Dropping %s: superseded before it started",
                             label)
                    continue
                self._local.job_gen = gen
            try:
                fn()
            except Exception as error:
                log.exception("%s failed", label)
                try:
                    with self._state_lock:
                        stale = gen < self._gen
                    if stale:
                        # A stop, a newer play or a switch came since, and
                        # cleared the error: a failure now -- often the
                        # switch's retirement of this job's client -- is
                        # not the UI's any more.
                        log.info("Not reporting %s failing: superseded",
                                 label)
                    elif not getattr(error, "_bridge_reported", False):
                        self.tracker.report_error(
                            classify_exception(error),
                            describe_exception(error))
                except Exception:
                    log.exception("could not report %s failing", label)
            finally:
                self._local.job_gen = None
                self.poke()

    def _superseded_job(self):
        """Whether the play-worker job running on this thread has been
        superseded since it was queued. False off the play worker (an
        auto-advance on the action thread is nobody's job). Call under
        `_state_lock`."""
        gen = getattr(self._local, "job_gen", None)
        return gen is not None and gen < self._gen

    def _job_superseded(self):
        """`_superseded_job`, taking the lock."""
        with self._state_lock:
            return self._superseded_job()

    def _supersede(self):
        """A new start or a stop: drop queued starts, cancel one in flight.
        Returns the dev playback that was showing, if any.

        A start the play() hook has committed to but the player has not yet
        flagged (`_committed`) is waited for, briefly, so the cancel lands:
        otherwise a stop arriving in those few bytecodes was answered ``ok``
        and the start played anyway."""
        with self._state_lock:
            self._gen += 1
            self.pending = None
            self._held.clear()      # their jobs are dropped with the rest
            opening = self.opening
            committed = self._committed
            dev = self.dev_item
            self.dev_item = None
        pm = self.runtime.pm
        if pm is None:
            return dev
        if committed is not None:
            deadline = time.monotonic() + COMMIT_WAIT
            while (not pm._start_in_progress
                   and self._committed is committed
                   and time.monotonic() < deadline):
                time.sleep(0.001)
        if pm._start_in_progress:
            if opening is not None:
                try:
                    self._cancelled.add(opening)
                except TypeError:
                    pass
            pm.cancel_load()
        return dev

    # -- hooks into the player ---------------------------------------------

    def _install_hooks(self):
        """Observe the player without changing what it does: the start of
        and the end of every ``play()`` (including the queue's own advances
        and restarts), the queue's finished callback, load errors, mpv
        failing to come back, and the embedded window's fullscreen
        requests."""
        from . import window_embed

        pm = self.runtime.pm
        original_play = pm.play
        original_finished = pm.finished_callback
        original_stopped = pm.send_timeline_stopped
        original_release = pm.release_stream
        original_initial = pm.send_timeline_initial
        original_set_streams = pm.set_streams

        def play(video, *args, **kwargs):
            with self._state_lock:
                if self._superseded_job():
                    log.info("Not starting %s: superseded",
                             getattr(video, "item_id", video))
                    return None
                self.pending = None
                current = pm._video
                if current is not None and current is not video:
                    self.tracker.mark_ended(current, "replaced")
                self.opening = video
            self.poke()
            outgoing = None
            owed = getattr(self._local, "replacing", None)
            if owed:
                # The one start a `play` command makes (not a next, an
                # auto-advance or a track restart, which report their own).
                self._local.replacing = None
                outgoing = self._report_outgoing(pm, video)
            with self._state_lock:
                # Again: the report waits on the player's lock and the
                # report lock, for as long as a hung progress report holds
                # the latter, and a stop, play or connect in that time was
                # answered ok and then ignored -- this start went ahead,
                # after a switch on the old server.
                abandoned = self._superseded_job()
                if not abandoned:
                    self._committed = video
                    if isinstance(owed, dict):
                        owed["video"] = video   # what its transport is for
            if abandoned:
                log.info("Not starting %s: superseded while the item it "
                         "replaces was being reported stopped",
                         getattr(video, "item_id", video))
                if outgoing is not None:
                    self._end_reported(pm, outgoing)
                with self._state_lock:
                    if self.opening is video:
                        self.opening = None
                self.poke()
                return None
            raised = None
            epoch = pm._play_epoch
            # A restart renegotiates on the same video: the session it had,
            # what reports it, and everything the negotiation overwrites,
            # should it find no stream (`_restart_not_taken`).
            before = _play_session(video)
            restart = (_video_state(video) if current is video and
                       current is not None else None)
            sending = pm.should_send_timeline
            # This start's own record: `_play_media` runs on this thread, so
            # its load hooks are this start's (`_on_load_start`).
            outer_start = getattr(self._local, "start", None)
            start = {"video": video, "loaded": False}
            self._local.start = start
            if restart is not None:
                with self._state_lock:
                    self._restart_shown = (video, before)
            try:
                return original_play(video, *args, **kwargs)
            except BaseException as error:
                raised = error
                raise
            finally:
                self._local.start = outer_start
                reached = start["loaded"]
                with self._state_lock:
                    if self._committed is video:
                        self._committed = None
                    cancelled = video in self._cancelled
                    failed = video in self._start_failed
                    stale = self._superseded_job()
                if pm._video is video and pm._play_epoch != epoch:
                    # A new play session of this video (a restart, the same
                    # item played again): its Playing has gone out, so its
                    # end is owed a report, whatever the last one got --
                    # even from a server that reuses the PlaySessionId.
                    session = _play_session(video)
                    self._reported.forget(video, session)
                    self._released.forget(video, session)
                    self._session_phase(video, bridge_state.PlaySessions.OPENED)
                    # The quality this stream was negotiated with, for a
                    # later change that cannot be applied (`_put_back`).
                    try:
                        self._quality[video] = getattr(video, "trs_ovr",
                                                       None)
                    except TypeError:
                        pass
                if outgoing is not None and pm._video is not video:
                    if cancelled or failed:
                        # Before `opening` goes: the outgoing item, held
                        # paused for the load that never came, showed as
                        # "paused" until the finished callback got round to
                        # it -- for good, if it had a next item.
                        self._end_reported(pm, outgoing)
                    else:
                        # Failed before reaching mpv: it plays on, and may
                        # yet reach its own end.
                        self._replaced.discard(outgoing)
                elif (not owed and current is not None and current is not video
                        and pm._video is current
                        and self._was_reported(current)):
                    # BRIDGE.md section 5: a next, a previous or an
                    # auto-advance reported `current` Stopped before it
                    # called play() (`play_next`, `play_prev`, the finished
                    # callback), and this start did not take -- failed in
                    # mpv or before it, or was cancelled. The item before it
                    # does not stay current: a failed load's own stop()
                    # returns early once mpv has dropped the file
                    # (`playback_abort`), and it sat "paused" on a closed
                    # session, the error state dropped and a `stop` doing
                    # nothing (``next_load_error``). Before `opening` goes,
                    # for the same reason as above.
                    self._end_reported(pm, current)
                elif pm._video is video and pm._play_epoch == epoch:
                    # Before `opening` goes, as above.
                    self._restart_not_taken(pm, video, before, restart,
                                            sending, reached,
                                            cancelled or failed, raised,
                                            stale)
                if (pm._video is not video and not cancelled and not failed
                        and video is not None):
                    # A start that did not end up playing and said nothing
                    # on the way out: no URL, or the negotiation raised.
                    # Before `opening` goes: with nothing left playing, a
                    # sample in between read "idle" -- and a UI leaves the
                    # player page on that -- before the "error".
                    if raised is not None:
                        code = classify_exception(raised)
                        message = describe_exception(raised)
                        raised._bridge_reported = True
                    else:
                        code, message = MPV_FAILED, "could not start playback"
                    if stale:
                        # Superseded since: a stop, a newer play or a switch
                        # (whose retirement is what made this request fail)
                        # has cleared the error, and this one is not the
                        # UI's any more.
                        log.info("Not reporting %s failing: superseded",
                                 getattr(video, "item_id", "a start"))
                    else:
                        self.tracker.report_error(
                            code, message,
                            item_id=getattr(video, "item_id", None))
                with self._state_lock:
                    if self.opening is video:
                        self.opening = None
                    # Not before: a restart that found no stream has only
                    # now put back the session it keeps.
                    if (self._restart_shown is not None
                            and self._restart_shown[0] is video):
                        self._restart_shown = None
                self._settle_sessions(pm, video, before)
                self.poke()

        def finished_callback(has_lock, epoch=None):
            # Under the player's lock (re-entrant; the original takes it
            # too), as the original reads them: read before it, the epoch
            # could still be the one a start holding the lock was about to
            # bump, and an item that was replaced or stopped ended "eof".
            with pm._lock:
                if epoch is None or epoch == pm._play_epoch:
                    video = pm._video
                    if video is not None and self._defer_finish(
                            video, has_lock, epoch):
                        # A `play` is replacing it, and owns what comes
                        # next: its end would have the queue start the next
                        # item -- Playing to the server -- for that play to
                        # load over, with no Stopped (``play_vs_advance``).
                        # Held, and run after all if the play leaves it
                        # current (`_finish_deferred`).
                        #
                        # Progress reports off, as the end itself turns them
                        # off: `update`'s end-of-file poll rescue, gated on
                        # them, queued this callback again on every pass,
                        # and each queueing woke the action thread for the
                        # next -- a spin for as long as the play's item
                        # fetch took (``spin:20,*``: ~40,000 holds and 14 s
                        # of CPU in 19 s). Nothing is lost: the play reports
                        # this item before it starts, and a held end that
                        # runs after all reports it itself.
                        pm.should_send_timeline = False
                        return None
                    if video is not None and video not in self._replaced:
                        # Not the outgoing item of a replacement: that ends
                        # because it was replaced or stopped, never at its
                        # end, whatever mpv's abort for the load looks like
                        # (it reaches here before the hook can end it).
                        self.tracker.mark_ended(video, "eof")
                self.poke()
                try:
                    return original_finished(has_lock, epoch)
                finally:
                    self.poke()

        def send_timeline_stopped(finished=False, options=None, client=None,
                                  stopped=None):
            # Under the report lock (re-entrant; the original takes it too),
            # so the check, the report and the record are one step -- also
            # against the thread `_report_stopped_offline` reports on.
            with pm._tl_lock:
                video, session = self._stop_subject(pm, options, stopped)
                if self._reported.has(video, session):
                    # Reported already: when a replacement began
                    # (`_report_outgoing`), or by a next/previous whose
                    # start then did not take -- dropped as superseded, or
                    # cancelled while it negotiated -- leaving the item
                    # loaded. Whatever ends it now (the stop, a replacement,
                    # a cancelled or failed load's own stop(), the finished
                    # callback, the shutdown) must not report it again.
                    pm.should_send_timeline = False
                    log.info("Not reporting %s stopped again",
                             getattr(video, "item_id", "an item"))
                    return None
                reporter = client if client is not None else getattr(
                    pm._video, "client", None)
                result = original_stopped(finished, options, client, stopped)
                self._reported.add(video, session)
                if reporter is not None:
                    # Its Stopped is on the report worker now: the shutdown
                    # owes it `failed` if that never finishes.
                    self.tracker.sessions.note(
                        session, getattr(video, "item_id", None) or (
                            options or {}).get("ItemId"),
                        bridge_state.PlaySessions.STOPPING)
                return result

        def send_timeline_initial(*args, **kwargs):
            # `_play_media`, under the player's lock, once the start has
            # succeeded: this submits the new play session's Playing.
            # Recorded first, and under that lock, so a hook deciding that
            # a start never opened (`_settle_sessions`) cannot race it.
            self._session_phase(pm._video, bridge_state.PlaySessions.OPENED)
            try:
                return original_initial(*args, **kwargs)
            finally:
                if self.controls.ui:
                    # fluentfin-mpv: after `update_subtitle_visuals`, which
                    # the start has just run: it put the shim's own sub-pos
                    # back, and a restarted mpv brings its OSC back too
                    # (bridge_controls).
                    self._apply_controls(pm, at_load=True,
                                         item_started=True)

        def release_stream(video):
            # Its own record: an end of file reports the item and *then*
            # releases its stream (`_unload_ended`), which the Stopped
            # record must not skip.
            session = _play_session(video)
            if self._released.has(video, session):
                return None         # released already, with the report
            result = original_release(video)
            self._released.add(video, session)
            return result

        def set_streams(aid, sid):
            # The tracks it changes from, for a restart that cannot apply
            # the new ones (`_restart_not_taken`): `Video.set_streams` has
            # written them by the time the restart's play() begins. Under
            # the player's lock, as the original (re-entrant).
            with pm._lock:
                video = pm._video
                outer = getattr(self._local, "tracks_before", None)
                self._local.tracks_before = None if video is None else (
                    video, getattr(video, "aid", None),
                    getattr(video, "sid", None),
                    getattr(video, "explicit_tracks", None))
                try:
                    return original_set_streams(aid, sid)
                finally:
                    self._local.tracks_before = outer

        pm.play = play
        pm.finished_callback = finished_callback
        pm.send_timeline_stopped = send_timeline_stopped
        pm.send_timeline_initial = send_timeline_initial
        pm.release_stream = release_stream
        pm.set_streams = set_streams
        pm.on_load_error = self._on_load_error
        pm.on_load_start = self._on_load_start
        pm.on_reopen_failed = self._on_reopen_failed
        window_embed.on_fullscreen_request = self._on_fullscreen_request

    def _report_outgoing(self, pm, video):
        """BRIDGE.md section 5, replacing playback: the item a ``play``
        replaces is reported Stopped and its stream released before the new
        one starts, as ``play_next``/``play_prev`` do (``player.py``). The
        shim's own remote-Play path does not -- there a start over playback
        is a hand-over -- so the server kept the old play session (resume
        point, played state, transcode) open until its idle timeout.

        Called from the ``play()`` hook, i.e. once the new item has resolved
        (its queue built), rather than before ``start_playback``: an item
        fetch that fails leaves the old item playing *and* reported. Under
        the player's lock, so the item read is the one playing when the
        report goes, not one a next or a finished callback holding the lock
        is about to replace and report itself.

        Exactly once: the play session is remembered (`_reported`, by the
        hook's wrapper), and nothing reports it again (`_install_hooks`).
        Returns the outgoing item -- reported now or earlier (an earlier
        attempt, or a next whose start did not take) -- or None; if this
        replacement does not take, the hook ends it (`_end_reported`)."""
        with pm._lock:
            outgoing = pm._video
            if outgoing is None or outgoing is video:
                return None
            if not self._was_reported(outgoing):
                log.info("Replacing %s: reporting it stopped",
                         getattr(outgoing, "item_id", "an item"))
                try:
                    pm.send_timeline_stopped(True)
                except Exception:
                    # Not reported, so not ours to end either: whatever
                    # stops it reports it.
                    log.warning("Could not report the replaced item stopped",
                                exc_info=True)
                    return None
            try:
                # Once: the wrapper skips a stream released already. A next
                # that reported the item did not release it.
                pm.release_stream(outgoing)
            except Exception:
                log.debug("could not release the replaced item's stream",
                          exc_info=True)
            self._mark_replaced(outgoing)
            return outgoing

    def _mark_replaced(self, video):
        try:
            self._replaced.add(video)
        except TypeError:
            pass

    def _defer_finish(self, video, has_lock, epoch):
        """Whether to hold back ``video``'s finished callback: a ``play``
        command is replacing it -- accepted and not handed over yet
        (``pending``), or negotiating its item (``opening``, another video).
        That is the only start not made under the player's lock, which the
        finished callback holds, so any other ``opening`` here is it.

        That play owns what comes next. Run now, the callback had the queue
        advance: the next item started -- Playing to the server -- and the
        play then loaded over it with no Stopped (``play_vs_advance``). Held
        instead (once per item and epoch, keeping a `_finished_lock` taken),
        and run after all if the play leaves the item current
        (`_finish_deferred`). Call under the player's lock.

        Logged once per item and epoch: mpv's end-of-file and abort
        observers both queue the callback, and a second hold is the same
        end."""
        with self._state_lock:
            opening = self.opening
            if self.pending is None and (opening is None or opening is video):
                return False
            held = self._deferred_finish
            again = (held is not None and held[0] is video
                     and held[2] == epoch)
            if again:
                has_lock = has_lock or held[1]
            self._deferred_finish = (video, has_lock, epoch)
        if not again:
            log.info("Holding the end of %s: a play is replacing it",
                     getattr(video, "item_id", "an item"))
        return True

    def _finish_deferred(self):
        """After a ``play`` command's start: the end of the item it was
        replacing, held back meanwhile (`_defer_finish`), runs now if that
        item is still current -- the start failed before mpv (PlaybackInfo,
        no stream) or before play() (the item fetch) -- as it would have
        run: the queue advances, or the item ends (BRIDGE.md section 5).
        Not while a newer ``play`` is pending: its own start does this when
        it is done. Nothing once the item is gone or a new file has started
        (the epoch moved on), and nothing during the shutdown: the end is
        left held for the shutdown's stop, which reports it (`_end_held`).

        Nothing either once this job has been superseded: whatever did it
        ends the item -- a stop or a switch (`_end_held` after its stop()),
        a newer ``play`` (its own start, then this); not a ``play_file``
        (``--dev``), whose stop() leaves a held end alone. A stop that came
        while this job's item fetch was finishing -- after its
        `_supersede`, before its deferred stop was queued -- found the end
        queued here ahead of it: the queue advanced, the next item got
        Playing, and the stop then stopped that one."""
        pm = self.runtime.pm
        with self._state_lock:
            held = self._deferred_finish
            if (held is None or self.pending is not None
                    or self._shutdown.is_set() or self._superseded_job()):
                return
            self._deferred_finish = None
        video, has_lock, epoch = held
        if pm is None or pm._video is not video or pm._play_epoch != epoch:
            return
        log.info("Running the end of %s: the play that was replacing it "
                 "did not", getattr(video, "item_id", "an item"))
        pm.put_task(pm.finished_callback, has_lock, epoch)

    def _end_held(self, pm, video=None, unload=True):
        """A stop (`_stop_playback`) or the shutdown (`ShimRuntime`'s
        ``before_final_stop``) ending an item whose end it finds held
        (`_defer_finish`) -- ``video``'s, or any -- while that item is still
        current: the held end, as the finished callback makes it at the end
        of a queue -- its Stopped (``finished``), then (``unload``) the
        unload -- and never the queue's advance, which a stop or a shutdown
        must not start. Returns whether it did. Call under the player's
        lock (the shutdown also holds the report lock).

        The ``stop()`` both make returns early once mpv has dropped the file
        (``playback_abort``) -- the last item at its end -- and reports
        nothing. The held end was then the only thing that would end it,
        and it ran only when the ``play`` holding it was done: the shutdown
        refuses it (`_finish_deferred`), so the item's play session had
        Playing and no Stopped (``held_last:shutdown``, ``held_last:eof``:
        ``reported failed``); and a ``play`` job dropped before it started
        never runs it, leaving the item current for good. Reported through
        the wrapper of `send_timeline_stopped`, so a session reported
        already -- by the ``play`` that replaced it -- is not reported
        again. Not unloaded during the shutdown, which ends what is loaded
        under its deadline (`_end_reported`)."""
        with self._state_lock:
            held = self._deferred_finish
            if held is None or (video is not None and held[0] is not video):
                return False
            self._deferred_finish = None
        video, _has_lock, epoch = held
        if pm._video is not video or pm._play_epoch != epoch:
            return False
        log.info("Ending %s: it reached its end while a play was replacing "
                 "it", getattr(video, "item_id", "an item"))
        self.tracker.mark_ended(video, "eof")
        pm.send_timeline_stopped(True)
        if unload:
            pm._unload_ended(video)
        return True

    def _was_reported(self, video, options=None):
        """Whether this play session of ``video`` has been reported
        Stopped."""
        return self._reported.has(video, _play_session(video, options))

    @staticmethod
    def _stop_subject(pm, options, stopped):
        """The video and session a Stopped report is for, as the record
        (`_reported`) keys them. ``stopped`` names its video (``stop()``);
        otherwise it is the video playing -- unless the options name another
        session: a report built from bookkeeping after mpv went away
        (`_report_stopped_offline`, on a thread of its own), by which time a
        new item may be playing. That one is checked and recorded by its
        session alone: matched against ``pm._video`` it could be sent for an
        item reported already, and its record overwrote the new item's."""
        if stopped is not None:
            return stopped, _play_session(stopped, options)
        video = pm._video
        wanted = options.get("PlaySessionId") if isinstance(options,
                                                            dict) else None
        if wanted is not None and _play_session(video) != wanted:
            video = None
        return video, _play_session(video, options)

    # -- play sessions (BRIDGE.md section 5, ``reported``) -----------------

    def _session_phase(self, video, phase):
        """``video``'s current play session reached ``phase``."""
        if video is None or getattr(video, "client", None) is None:
            return              # play_file has no session; offline, no server
        self.tracker.sessions.note(_play_session(video),
                                   getattr(video, "item_id", None), phase)

    def _settle_sessions(self, pm, video, before):
        """After a ``play()``: the sessions it leaves with nothing to come.

        - A restart (a track change on a transcode, a quality change)
          renegotiates on the same video, and upstream sends no Stopped for
          the session it replaces (``restart_playback``): that one is
          ``failed`` -- abandoned -- as soon as a new PlaybackInfo has
          replaced its id, whether the new session then opens, is
          cancelled, finds no stream or fails to load, so a UI still
          tracking it closes it rather than holding it open. A restart
          whose PlaybackInfo request itself failed replaced nothing: the old
          session goes on (`_restart_not_taken`, which runs first).
        - A start that did not end up playing -- cancelled, failed to load,
          failed after PlaybackInfo, superseded -- negotiated a session
          whose Playing never went out (`send_timeline_initial` records it
          under the player's lock before it does): ``none``. One whose
          Stopped went out (a failed load's stop() of the item itself) or
          that is playing is not settled here."""
        if video is None or getattr(video, "client", None) is None:
            return
        item = getattr(video, "item_id", None)
        now = _play_session(video)
        phases = self.tracker.sessions
        if (before is not None and before != now
                and phases.phase(before) == phases.OPENED):
            self._session_done(item, before, bridge_state.FAILED,
                               "its restart opened another")
        if (now is not None and pm._video is not video
                and phases.phase(now) in (None, phases.SEEN)):
            self._session_done(item, now, bridge_state.NONE,
                               "it never opened")

    def _stop_outcome(self, item_id, session, outcome):
        """A client's Stopped report returned or raised
        (`bridge_http.watch_stops`). On the report worker, or the caller
        once it is stopped: only queued here, never written."""
        self._session_done(item_id, session, outcome, "Stopped report")

    def _session_done(self, item_id, session, outcome, why):
        if self.tracker.finish_session(session, item_id, outcome):
            log.info("Play session of %s done (%s): %s",
                     item_id or "an item", why, outcome)
            self.poke()
            self._reported_due.set()

    def _send_reported(self):
        """Write the queued ``reported`` events. Call with `_emit_lock`.
        Nothing once the shutdown has written its last ones."""
        if self._reported_closed:
            return
        for data in self.tracker.drain_reported():
            try:
                self.writer.event("reported", data)
            except Exception:
                log.exception("could not send a reported event")

    def _flush_reported(self, final=False):
        """Every queued ``reported`` event, written now (the shutdown's,
        before its answer, ``final``: the last that are written)."""
        with self._emit_lock:
            self._send_reported()
            if final:
                self._reported_closed = True

    def _reported_loop(self):
        """BRIDGE.md section 5: a ``reported`` is written on the monitor's
        next tick, after that tick's ``state`` events -- or directly, here,
        if the monitor has not written it within `REPORTED_WAIT`.

        The monitor reads mpv before every tick, outside `_emit_lock`, and an
        mpv that stops answering holds that read for as long as an IPC
        reply is waited for: every outcome queued meanwhile waited with it,
        until the shutdown (``hung_mpv_stop``). The report worker cannot
        write it itself: `ProtocolWriter.send` holds its lock across a
        blocking pipe write, and a UI that stopped reading would stall
        report delivery. Written here, a ``state`` the monitor sampled
        before the outcome was decided can follow its ``reported`` (the UI
        keeps the session closed, BRIDGE.md section 6). Under `_emit_lock`,
        and drained once (`Tracker.drain_reported`): each is written once,
        by the monitor or by this. It leaves the last ones to the shutdown,
        which writes them before its answer."""
        while not self._monitor_stop.is_set():
            if not self._reported_due.wait(MONITOR_INTERVAL):
                continue
            self._reported_due.clear()
            with self._tick_cond:
                self._tick_cond.wait_for(
                    lambda: (not self.tracker.reported_pending()
                             or self._monitor_stop.is_set()),
                    timeout=REPORTED_WAIT)
            if self._monitor_stop.is_set():
                return
            if not self.tracker.reported_pending():
                continue
            log.warning("The monitor has not written a play session's "
                        "outcome in %.0fs (mpv not answering?); writing it "
                        "now.", REPORTED_WAIT)
            try:
                with self._emit_lock:
                    self._send_reported()
            except Exception:
                log.exception("could not write the reported events")

    def _end_reported(self, pm, outgoing,
                      why="the start it was reported stopped for did not "
                          "take"):
        """``outgoing`` was reported stopped for a start that then did not
        take: a ``play`` command's replacement (which also released its
        stream) superseded before it started, cancelled, or whose load
        failed; or a next, previous or auto-advance whose start did not take
        at all. ``outgoing`` must not play on as if nothing happened, and
        must not be reported again: unloaded here, under the player's lock,
        the way the finished callback unloads an item whose report has gone
        (its stream released once, `release_stream`'s record). Not for a
        ``play`` command's replacement that failed before reaching mpv
        (PlaybackInfo): that leaves the old item playing (docs/bridge.md
        section 5). Also for a restarted item whose load mpv was handed
        and dropped (`_restart_not_taken`), ``why`` saying so.

        Not during the shutdown, which ends whatever is loaded itself, under
        its one deadline (`ShimRuntime.shutdown`). The unload talks to mpv,
        which may be the thing not answering: a next holding the player's
        lock across a slow PlaybackInfo, cancelled by the shutdown, unloaded
        the item before it into a suspended mpv and kept the lock past the
        deadline (``hung_mpv_next``: exit 1, where the shutdown's own stop
        bounds mpv's replies and exits 0)."""
        if self._shutdown.is_set():
            return
        with pm._lock:
            if pm._video is not outgoing:
                return
            log.info("Ending %s: %s", getattr(outgoing, "item_id", "an item"),
                     why)
            try:
                pm._unload_ended(outgoing)
            except Exception:
                log.warning("Could not end the replaced item",
                            exc_info=True)

    def _restart_not_taken(self, pm, video, before, restart, sending,
                           reached, interrupted, raised, stale):
        """A restart -- `restart_playback`: a track change on a transcode, a
        quality change -- that did not take, ``video`` still current.
        ``restart`` is the video's attributes from before it (`_video_state`).

        **mpv had its load** (``reached``, `_on_load_start`): failed or
        cancelled from there, the start dropped the new file and the old
        one with it (`_abort_load`), and its own stop() returns early once
        mpv has (``playback_abort``). The video stayed current with nothing
        loaded: "paused", the load's error state dropped, a ``stop`` doing
        nothing (``restart_load_error``, ``restart_cancel_queue``). Ended
        here instead, before `opening` goes -- without a Stopped, which
        would carry a position mpv no longer has (0, the resume point
        wiped; the finished callback's interrupted branch says the same):
        the session it played was abandoned when the new PlaybackInfo
        replaced it (``failed``), and the new one never sent Playing
        (``none``, both `_settle_sessions`). A server that handed the
        restart the same id gets ``failed`` for it here.

        **No usable stream** -- the PlaybackInfo request failed (or anything
        before it raised), or it answered with no media source or nothing
        this player can play (play()'s "no URL" return): mpv was never
        touched and plays on, on the stream and session it had, which goes
        on reporting and gets its normal Stopped (BRIDGE.md section 5). What
        the negotiation had overwritten is put back (`_put_back`): the
        PlaybackInfo and its session, the media source, the stream maps and
        play method, the tracks the change asked for, mpv's auth header --
        so ``state`` shows what is playing, and a ``stop`` reports it (a
        video with no media source made the stop raise, a no-op:
        ``restart_no_source``). The progress reports, which play() turns off
        at its start and only a start that loads turns back on, go back on;
        the session the failed negotiation was handed, never Playing, is
        ``none``; and an ``error`` event names the item (not the state: it
        plays on), with the code a new item gets for the same failure --
        ``mpv_failed`` for no stream, the request's own for a failed one.
        Not for a restart a stop, play or switch interrupted: it is
        theirs. Not undone either: the old transcode, which
        `get_playback_url` ends before it asks (a transcoded item's stream
        then runs out, as with a replacement's, docs/bridge.md section 5).

        **Cancelled with a stream in hand** (the profile lookup, mpv still
        untouched): that session replaced the old one, which is abandoned
        (``failed``, `_settle_sessions`), and the stop, play or switch that
        cancelled it ends the video."""
        if reached:
            self._end_reported(pm, video, "its restart's load did not take, "
                               "and mpv has dropped the file")
            session = _play_session(video)
            if (pm._video is not video and session is not None
                    and session == before):
                self._session_done(getattr(video, "item_id", None), session,
                                   bridge_state.FAILED,
                                   "its restart's load did not take")
            return
        if restart is None:
            return
        if raised is None and interrupted and _negotiated_stream(video):
            return
        if raised is not None:
            code, reason = (classify_exception(raised),
                            describe_exception(raised))
        elif getattr(video, "media_source", None) is None:
            code, reason = MPV_FAILED, "the server offered no media source"
        else:
            code, reason = MPV_FAILED, ("the server offered no stream this "
                                        "player can play")
        abandoned = _play_session(video)
        log.info("The restart of %s found no stream (%s); it keeps the play "
                 "session it had.", getattr(video, "item_id", "an item"),
                 reason)
        self._put_back(pm, video, restart)
        session = _play_session(video)
        item = getattr(video, "item_id", None)
        if (abandoned is not None and abandoned != session
                and self.tracker.sessions.phase(abandoned) in (
                    None, bridge_state.PlaySessions.SEEN)):
            self._session_done(item, abandoned, bridge_state.NONE,
                               "its restart found no stream")
        if not interrupted and sending and session == before:
            pm.should_send_timeline = True
        if interrupted or stale:
            return
        if raised is not None:
            try:
                raised._bridge_reported = True
            except Exception:
                pass
        self.tracker.report_error(
            code, "%s: the track or quality change could not be applied "
            "(%s); it plays on as before" % (_item_label(video), reason),
            item_id=item, sticky=False)

    def _put_back(self, pm, video, restart):
        """`_restart_not_taken`: ``video`` as it was before its restart --
        ``restart``, a shallow copy of its attributes (the negotiation binds
        new objects to those names; it does not edit the old ones), then
        the tracks a `set_streams` on this thread changed (`Video.set_streams`
        wrote them before the restart began), then the quality the stream
        playing was negotiated with (`_quality`: `set_trs_override` also
        wrote the new one before the restart began, and the next
        renegotiation -- a track change on a transcode -- asked for the
        quality that had failed), then mpv's auth header, which
        is global: play()'s no-URL return revokes it, and a start that
        raised after installing it leaves it on where the old stream had it
        off (another host's)."""
        had = bool(restart.get("auth_via_header"))
        has = bool(getattr(video, "auth_via_header", False))
        vars(video).update(restart)
        tracks = getattr(self._local, "tracks_before", None)
        if tracks is not None and tracks[0] is video:
            video.aid, video.sid, video.explicit_tracks = tracks[1:]
        try:
            if video in self._quality:
                video.trs_ovr = self._quality[video]
        except TypeError:
            pass
        if had == has:
            return
        try:
            if had:
                video.auth_via_header = bool(pm._apply_auth_headers(video))
                if not video.auth_via_header:
                    # As play() does when the header did not take: our own
                    # subtitle urls carry the token instead.
                    video.reauthorize_sidecars()
            else:
                pm._revoke_auth_header(video)
        except Exception:
            log.warning("could not put mpv's auth header back as it was",
                        exc_info=True)

    def _on_load_start(self, payload):
        """`_play_media` is about to hand mpv the start's file (or, cancelled,
        to drop what it has): from here, a start that does not take leaves
        mpv with nothing loaded. It runs on the thread of the play() that
        called it, so it is that start's -- the one recorded on this thread
        (``_local.start``) -- not ``opening``'s, which is the latest start
        on any thread: a ``play`` negotiating while a restart on the action
        thread (the OSC's) loaded took the restart's load for its own, and a
        restart whose load then failed was left current with nothing
        loaded."""
        start = getattr(self._local, "start", None)
        if start is not None:
            start["loaded"] = True
            with self._state_lock:
                # From here mpv plays the restart's stream, or nothing.
                if (self._restart_shown is not None
                        and self._restart_shown[0] is start["video"]):
                    self._restart_shown = None

    def _on_load_error(self, payload):
        """A start's load failed (`_play_media`, on that start's thread:
        ``_local.start``), or a playing stream was interrupted (the finished
        callback, on the action thread, outside any start): the item
        playing."""
        pm = self.runtime.pm
        detail = payload.get("detail") or (
            "timed out opening the media" if payload.get("timed_out")
            else "mpv could not open the media")
        start = getattr(self._local, "start", None)
        if start is not None:
            video = start["video"]
            if video in self._cancelled:
                return
            try:
                self._start_failed.add(video)
            except TypeError:
                pass
            if video is pm._video:
                self.tracker.mark_ended(video, "error")
            self.tracker.report_error(MPV_FAILED, detail,
                                      item_id=getattr(video, "item_id", None))
        else:
            video = pm._video
            self.tracker.mark_ended(video, "error")
            self.tracker.report_error(MPV_FAILED, detail,
                                      item_id=getattr(video, "item_id", None))
        self.poke()

    def _on_reopen_failed(self, error):
        self.writer.event("error", {
            "code": MPV_FAILED,
            "message": "mpv could not be restarted: "
                       + describe_exception(error),
            "fatal": True})
        self.request_shutdown("mpv could not be restarted", 1)

    def _on_fullscreen_request(self, want):
        self.writer.event("fullscreen_request", {"fullscreen": bool(want)})

    # -- gates -------------------------------------------------------------

    def _require_server(self):
        if not self.connected:
            raise BridgeError(NOT_CONNECTED, "send connect first")

    def _require_player(self):
        """Transport: a server connection, or (``--dev``) a window."""
        if self.connected and self.runtime.pm is not None:
            return self.runtime.pm
        if self.dev and self.runtime.pm is not None:
            return self.runtime.pm
        raise BridgeError(NOT_CONNECTED, "send connect first")

    def _playing(self, pm):
        """The video playing, for transport: ``pm._video``, unless an
        answered ``stop`` is ending it (`_stop_for`). That stop is deferred
        to the action thread, so ``_video`` outlives its answer; a ``next``
        read it as playing, started the next item, and the stop then ended
        that one -- Playing, then Stopped, for an item nobody asked for
        (``stop_then:next``)."""
        video = pm._video
        if video is None:
            return None
        with self._state_lock:
            return None if video is self._stop_for else video

    def _starting(self, pm):
        """An item opening, for transport: a ``play`` not handed over yet,
        or a start in progress that no ``stop`` or newer ``play`` has
        cancelled (it is only unwinding)."""
        with self._state_lock:
            if self.pending is not None:
                return True
            opening = self.opening
        if not pm._start_in_progress:
            return False
        return opening is None or opening not in self._cancelled

    def _has_playback(self, pm, starting_counts=True):
        if self._playing(pm) is not None or self.dev_item is not None:
            return True
        return starting_counts and self._starting(pm)

    def _need_playback(self, pm, starting_counts=True):
        if not self._has_playback(pm, starting_counts):
            raise BridgeError(NO_PLAYBACK, "nothing is playing")

    def _mpv_write(self, fn):
        """A direct property write, for ``play_file`` playback and the idle
        player. Never raises past a dead mpv."""
        pm = self.runtime.pm
        if pm is None or not pm._mpv_alive:
            return
        try:
            fn(pm._player)
        except self.runtime.mpv_errors():
            pm._handle_mpv_disconnect()

    # -- commands ----------------------------------------------------------

    def cmd_connect(self, args):
        cfg = proto.validate_connect(args, dev=self.dev)
        window = cfg["window"]
        if self.window is None:
            if window is None:
                raise BridgeError(BAD_ARGS, "window is required on the first "
                                  "connect")
            if (window["mode"] == "embedded"
                    and not proto.is_window_handle(window["hwnd"])):
                raise BridgeError(BAD_ARGS, "window.hwnd is not a window")
            try:
                info = self.runtime.start_player(window)
            except Exception as error:
                log.exception("mpv could not be started")
                raise BridgeError(MPV_FAILED, "mpv could not be started: "
                                  + describe_exception(error))
            self.window = window
            self.player_info = info
            self._install_hooks()
        elif window is not None and window != self.window:
            raise BridgeError(WINDOW_LOCKED,
                              "the window is fixed for this process (%s); "
                              "restart the backend to change it"
                              % self.window["mode"])
        if cfg["server"] is not None:
            self._switch_server(cfg["server"])
        return dict(self.player_info)

    def _switch_server(self, server):
        # The token is already registered for redaction: `handle_line` does
        # it before the request is so much as logged.
        old = self.client
        if old is not None:
            # First, so nothing below -- the stop's report, a start holding
            # the player's lock across PlaybackInfo, the new server's first
            # reports behind the old one's on the shared lock and worker --
            # waits on the old server for more than a moment. A healthy one
            # still gets its Stopped report; a hung one loses it (bridge_http;
            # docs/bridge.md section 3).
            self.runtime.retire_client(old)
        # BRIDGE.md section 3: a new connect stops any playback first.
        self._stop_playback(blocking=True)
        with self._state_lock:
            old, self.client = self.client, None
            self.server = None
            self.connected = False
        if old is not None:
            threading.Thread(target=self.runtime.stop_client, args=(old,),
                             daemon=True).start()
        client = self.runtime.make_client(server)
        with self._state_lock:
            self._verifying = client
            closing = self._shutdown.is_set()
        if closing:
            self.runtime.retire_client(client, 0)
        try:
            self.runtime.verify_client(client, server)
        except BaseException:
            self.runtime.stop_client(client)
            raise
        finally:
            with self._state_lock:
                self._verifying = None
        with self._state_lock:
            self.client = client
            self.server = server
            self.connected = True
        self.tracker.clear_error()
        log.info("Connected to %s as user %s (device %s, client %s)",
                 server["url"], server["userId"], server["deviceName"],
                 server["clientName"])

    def cmd_play(self, args):
        self._require_server()
        request = proto.validate_play(args)
        self.tracker.clear_error()
        dev = self._supersede()
        if dev is not None:
            self.tracker.mark_ended(dev, "replaced")
        pm = self.runtime.pm
        if pm is not None:
            # Replaced, whatever ends it from here: a restart this play just
            # cancelled ends the item itself, before this start's hook marks
            # it (`_restart_not_taken`), and it ended "stopped"
            # (``restart_cancel_play``). A stop or an end of file marked
            # later still outranks this.
            self.tracker.mark_ended(pm._video, "replaced")
        client, user_id = self.client, self.server["userId"]
        with self._state_lock:
            self.pending = {"itemIds": request["itemIds"],
                            "index": request["startIndex"]}
        pending = self.pending

        def run():
            item_id = request["itemIds"][request["startIndex"]]
            # Owed by this start's play(): see `_report_outgoing`. The hook
            # also records there which video this play started, for the
            # transport queued behind it (`_transport`).
            self._local.replacing = pending
            try:
                started = self.runtime.start_playback(client, user_id,
                                                      request)
                # Neither failure is reported once the job is superseded:
                # the stop, play or switch that superseded it cleared the
                # error, and a switch's retirement of this client is what
                # makes a slow old server's request fail -- after it.
                if not started and not self._job_superseded():
                    self.tracker.report_error(
                        INTERNAL, "item %s cannot be played" % item_id,
                        item_id=item_id)
            except Exception as error:
                log.warning("Could not start %s", item_id, exc_info=True)
                if (not self._job_superseded()
                        and not getattr(error, "_bridge_reported", False)):
                    self.tracker.report_error(classify_exception(error),
                                              describe_exception(error),
                                              item_id=item_id)
            finally:
                # Never left set: an item fetch that failed before play(),
                # or no video at all, would hand it to whatever this thread
                # starts next -- a next would report its item twice.
                self._local.replacing = None
                with self._state_lock:
                    if self.pending is pending:
                        self.pending = None
                # The end of the item this was replacing, if it came
                # meanwhile and the item is still current.
                self._finish_deferred()

        self._queue_play("play", run)
        return {}

    def cmd_play_file(self, args):
        pm = self.runtime.pm
        if pm is None:
            raise BridgeError(NOT_CONNECTED, "send connect with a window "
                              "first")
        url = proto.get_str(args, "url")
        title = proto.get_str(args, "title", required=False, nullable=True)
        self.tracker.clear_error()
        previous = self._supersede()
        if previous is not None:
            self.tracker.mark_ended(previous, "replaced")
        item = bridge_state.DevItem(url, title)
        with self._state_lock:
            self.dev_item = item

        def run():
            with pm._lock:
                pm._ensure_mpv()
                if pm._video is not None:
                    pm.stop()
                # `http-header-fields` is global and outlives the item it was
                # set for (docs/auth-headers.md): the last server item's
                # `Authorization: MediaBrowser ... Token=` would go to
                # whatever host this URL names. Every server start clears it
                # in `_apply_auth_headers`; this start goes round that.
                pm._revoke_auth_header()
                player = pm._player
                try:
                    player.force_media_title = title or url
                except Exception:
                    pass
                player.pause = False
                player.play(url)
            item.started = time.monotonic()
            item.loaded = True

        self._queue_play("play_file", run)
        return {}

    def cmd_stop(self, args):
        self._require_player()
        self.tracker.clear_error()
        self._stop_playback(blocking=False)
        return {}

    def _stop_playback(self, blocking):
        """Stop what is playing and cancel what is starting, **never on the
        calling thread**: ``stop()`` takes the player's lock, which a start
        -- or a next, previous or track restart -- holds across its server
        round trips, and then the report lock, which a progress report holds
        across its own (30 s per attempt, retried). A server that accepts
        and then hangs would hold the control worker -- and every command
        queued behind it, a ``connect`` to another server included -- for
        as long as the apiclient keeps retrying, while ``state`` already
        said ``idle`` (``stop_hung``). A free player lock is no bound.

        So the stop is always deferred to the action thread (`put_task`).
        Not ``blocking`` (the ``stop`` command): answered at once; the
        Stopped report goes out when the stop runs, or -- a shutdown coming
        first -- from the shutdown's own stop, under its deadline.
        ``blocking`` (a server switch): waited for at most `STOP_WAIT`;
        returns whether the playback went."""
        pm = self.runtime.pm
        dev = self._supersede()
        if pm is None:
            return True
        if dev is not None:
            self.tracker.mark_ended(dev, "stopped")
            self._mpv_write(lambda p: p.command("stop"))
        target = pm._video
        if target is not None:
            self.tracker.mark_ended(target, "stopped")
        with self._state_lock:
            gen = self._gen
            # Answered as stopped from now on (`_playing`): a next, a
            # previous or a track change sent behind this stop must not
            # start what the stop then ends.
            self._stop_for = target

        def stop(p):
            # Deferred, this runs once the player's lock is free -- possibly
            # after a newer `play` has started. It stops what this command
            # meant to stop, never that. The check and the stop are one step
            # under that lock: checked before it, a newer start holding the
            # lock across its load (`_video` still the old item) passed the
            # check, and the stop then ended the new item.
            try:
                with p._lock:
                    with self._state_lock:
                        newer = self._gen != gen
                    if newer and p._video is not target:
                        log.info("Not stopping: a newer start has replaced "
                                 "what this stop was for.")
                        return
                    p.stop()
                    if target is not None:
                        # stop() returns early once mpv has dropped the
                        # file -- the last item at its end -- and its end,
                        # held for a play this stop superseded, then waited
                        # for that play's job: for good, if the job was
                        # dropped before it started (`_end_held`).
                        self._end_held(p, target)
            finally:
                with self._state_lock:
                    if self._stop_for is target:
                        self._stop_for = None

        if not blocking:
            pm.put_task(stop, pm)
            return True
        if target is None and not pm._start_in_progress:
            return True             # nothing playing, nothing starting
        pm.put_task(stop, pm)
        deadline = time.monotonic() + STOP_WAIT
        while pm._video is not None or pm._start_in_progress:
            if time.monotonic() >= deadline or self._shutdown.is_set():
                log.warning("Playback did not stop within %.0fs: the player "
                            "is waiting on the server. Going on; it stops "
                            "once that request returns.", STOP_WAIT)
                return False
            time.sleep(0.05)
        return True

    # -- transport ---------------------------------------------------------

    def _opening(self, pm):
        """Whether an item is opening (BRIDGE.md section 5): a ``play``
        accepted and not yet handed over, a start in progress, or a
        ``play_file`` not yet handed to mpv (once mpv has the file, a
        property write reaches it as it would a playing one)."""
        with self._state_lock:
            if self.pending is not None or self.opening is not None:
                return True
            dev = self.dev_item
        if dev is not None:
            return not dev.loaded
        return bool(pm._start_in_progress)

    def _transport(self, pm, label, action, dev_action):
        """``action(pm)`` through `run_action`, or ``dev_action(mpv)`` on a
        ``play_file`` playback -- now, or, while an item is opening, once it
        has started.

        Queued on the play worker, behind the start: BRIDGE.md section 5
        says a transport command sent while ``opening`` is applied once the
        item has started, not acknowledged and lost. Run at once instead, a
        pause reached ``pause_if_playing`` with nothing playing yet (a
        no-op) or was deferred to the action thread and undone by the
        start's own final unpause, and a seek was overridden by the resume
        offset. A ``stop`` or another ``play`` in between bumps the
        generation, and the play worker drops it (`_play_loop`). While one
        is queued the state stays ``opening`` (`_held`).

        It is for the item that was opening, and for nothing else: a
        replacement that fails before reaching mpv leaves the old item
        playing, and the queued seek landed on that. So the opening item is
        captured here -- the video, or the ``play`` whose video does not
        exist yet -- and the action is dropped, where it applies (under the
        lock, after the start), unless that item is what is playing."""
        def run(expect=None):
            if self.dev_item is not None:
                self._mpv_write(dev_action)
            elif expect is None:
                pm.run_action(action)
            else:
                def for_the_opening_item(p):
                    if expect() is None or p._video is not expect():
                        log.info("Not applying a queued %s: the item it was "
                                 "sent for did not start", label)
                        return None
                    return action(p)

                pm.run_action(for_the_opening_item)

        if not self._opening(pm):
            run()
            return
        with self._state_lock:
            opening, pending = self.opening, self.pending
        if opening is not None:
            expect = lambda: opening                    # noqa: E731
        elif pending is not None:
            expect = lambda: pending.get("video")       # noqa: E731
        else:
            expect = None   # a start elsewhere (the action thread's)
        token = object()

        def after_start():
            try:
                run(expect)
            except Exception:
                # Not an error event: the start it waited for may itself
                # have failed, and that has been reported already.
                log.info("A queued %s did not apply", label, exc_info=True)
            finally:
                with self._state_lock:
                    self._held.discard(token)
                self.poke()

        log.info("Queueing %s until the item has started", label)
        with self._state_lock:
            self._held.add(token)
        self._queue_play(label, after_start)

    def cmd_pause(self, args):
        return self._set_paused(True)

    def cmd_unpause(self, args):
        return self._set_paused(False)

    def _set_paused(self, value):
        pm = self._require_player()
        self._need_playback(pm)
        self._transport(
            pm, "pause" if value else "unpause",
            (lambda p: p.pause_if_playing()) if value
            else (lambda p: p.play_if_paused()),
            lambda p: setattr(p, "pause", value))
        return {}

    def cmd_toggle_pause(self, args):
        pm = self._require_player()
        self._need_playback(pm)

        def toggle(p):
            p.toggle_pause()
            p.timeline_handle()

        self._transport(pm, "toggle_pause", toggle,
                        lambda p: setattr(p, "pause", not p.pause))
        return {}

    def cmd_seek(self, args):
        pm = self._require_player()
        ticks = proto.get_int(args, "positionTicks", minimum=0)
        self._need_playback(pm)
        seconds = proto.ticks_to_seconds(ticks)
        self._transport(pm, "seek",
                        lambda p: p.seek(seconds, absolute=True),
                        lambda p: p.command("seek", str(seconds),
                                            "absolute+exact"))
        return {}

    def cmd_seek_relative(self, args):
        pm = self._require_player()
        seconds = float(proto.get_number(args, "seconds"))
        self._need_playback(pm)
        self._transport(pm, "seek_relative", lambda p: p.seek(seconds),
                        lambda p: p.command("seek", str(seconds)))
        return {}

    def cmd_next(self, args):
        return self._move(+1)

    def cmd_previous(self, args):
        return self._move(-1)

    def _move(self, direction):
        pm = self._require_player()
        if self._playing(pm) is None:
            raise BridgeError(NO_PLAYBACK, "nothing is playing")
        self._queue_play("next" if direction > 0 else "previous",
                         pm.play_next if direction > 0 else pm.play_prev)
        return {}

    def cmd_set_audio(self, args):
        pm = self._require_player()
        index = proto.get_int(args, "index", minimum=0)
        if self._playing(pm) is None:
            raise BridgeError(NO_PLAYBACK, "nothing is playing")
        # set_streams may restart a transcode, which blocks for the load.
        self._queue_play("set_audio", lambda: pm.set_streams(index, None))
        return {}

    def cmd_set_subtitle(self, args):
        pm = self._require_player()
        index = proto.get_int(args, "index", minimum=-1)
        if self._playing(pm) is None:
            raise BridgeError(NO_PLAYBACK, "nothing is playing")
        self._queue_play("set_subtitle", lambda: pm.set_streams(None, index))
        return {}

    def cmd_set_volume(self, args):
        pm = self._require_player()
        volume = proto.get_int(args, "volume", minimum=0, maximum=100)
        if self._playing(pm) is not None:
            pm.run_action(lambda p: p.set_volume(volume))
        else:
            if self.dev_item is None:
                self.runtime.set_idle_volume(volume)
            self._mpv_write(lambda p: setattr(p, "volume", volume))
        return {}

    def cmd_set_mute(self, args):
        self._require_player()
        muted = proto.get_bool(args, "muted")
        self._mpv_write(lambda p: setattr(p, "mute", muted))
        return {}

    def cmd_set_fullscreen(self, args):
        pm = self._require_player()
        fullscreen = proto.get_bool(args, "fullscreen")
        # fluentfin-mpv player controls: attached is the UI's window too
        if self.window["mode"] in ("embedded", "attached"):
            pm.set_embedded_fullscreen(fullscreen)
        else:
            pm.run_action(lambda p: p.set_fullscreen(fullscreen))
        return {}

    def cmd_set_controls(self, args):
        """fluentfin-mpv: who draws the player controls (BRIDGE.md section
        4, `bridge_controls`). Applied now when the player is free, else on
        the action thread once a start lets go of it; held for every later
        item (each start re-applies it, `send_timeline_initial`) and for a
        restarted mpv."""
        pm = self._require_player()
        controls, inset = proto.validate_controls(args)
        self.controls.set(controls, inset)
        pm.run_action(self._apply_controls)
        return {}

    def cmd_skip_segment(self, args):
        """fluentfin-mpv: skip the segment on offer (BRIDGE.md section 4,
        ``segments.current``), the way the shim's own Skip button does:
        `PlayerManager.skip_intro` on the action thread, which seeks to the
        end of the segment the position is in. The offer is the one last
        sent, the one the UI saw."""
        pm = self._require_player()
        if self._playing(pm) is None:
            raise BridgeError(NO_PLAYBACK, "nothing is playing")
        if self.tracker.offer() is None:
            return {"skipped": False}
        pm.put_task(pm.skip_intro)
        self.poke()
        return {"skipped": True}

    def _subtitle_base(self):
        """The shim's own ``sub-pos`` (its subtitle position setting), what
        `PlayerManager.update_subtitle_visuals` writes at every item start."""
        from .conf import settings

        table = getattr(getattr(self.runtime, "player_module", None),
                        "SUBTITLE_POS", None)
        try:
            return float((table or {}).get(settings.subtitle_position, 100))
        except (TypeError, ValueError):
            return 100.0

    def _apply_controls(self, pm, at_load=False, item_started=False):
        """Never raises. A lost mpv is the shim's to handle, except inside a
        playback start (``at_load``), whose own path does that."""
        if pm is None:
            return
        try:
            self.controls.apply(pm, self._subtitle_base(), item_started)
        except self.runtime.mpv_errors():
            if not at_load:
                pm._handle_mpv_disconnect()
        except Exception:
            log.warning("Could not apply the player controls' settings",
                        exc_info=True)

    HANDLERS = {
        "connect": cmd_connect,
        "play": cmd_play,
        "play_file": cmd_play_file,
        "pause": cmd_pause,
        "unpause": cmd_unpause,
        "toggle_pause": cmd_toggle_pause,
        "stop": cmd_stop,
        "next": cmd_next,
        "previous": cmd_previous,
        "seek": cmd_seek,
        "seek_relative": cmd_seek_relative,
        "set_audio": cmd_set_audio,
        "set_subtitle": cmd_set_subtitle,
        "set_volume": cmd_set_volume,
        "set_mute": cmd_set_mute,
        "set_fullscreen": cmd_set_fullscreen,
        "set_controls": cmd_set_controls,
        "skip_segment": cmd_skip_segment,
    }

    # -- the monitor -------------------------------------------------------

    def poke(self):
        """Sample now rather than at the next interval."""
        self._wake.set()

    def get_state(self):
        """The current snapshot, at most one sample old. Waits briefly for
        a fresh sample, never for long: this runs on the reader thread, and
        a wedged mpv must not stop ``ping`` being answered."""
        with self._tick_cond:
            seen = self._ticks
            self.poke()
            self._tick_cond.wait_for(lambda: self._ticks != seen,
                                     timeout=0.3)
        snap = self.tracker.last_state
        if snap is None:
            snap = self.tracker.snapshot(self._observe(cheap=True))
        return snap

    def _monitor_loop(self):
        while not self._monitor_stop.is_set():
            self._wake.wait(MONITOR_INTERVAL)
            self._wake.clear()
            if self._monitor_stop.is_set():
                break
            try:
                obs = self._observe()
                self._dev_step(obs)
            except Exception:
                log.exception("monitor tick failed")
                obs = None
            self._emit_tick(obs)
            self._push_offer()
            with self._tick_cond:
                self._ticks += 1
                self._tick_cond.notify_all()

    def _emit_tick(self, obs):
        """The events for one sample, then every ``reported`` queued by
        now -- so an outcome decided after ``obs`` was taken follows the
        ``state`` it showed. mpv was read before this (`_observe`), outside
        `_emit_lock`: a hung mpv must not hold the shutdown's last
        ``reported`` events (`_flush_reported`), nor the ones
        `_reported_loop` writes when this tick does not come. A
        ``reported`` is never skipped, even when the monitor is told to stop
        mid-tick: the shutdown answer waits for this lock."""
        with self._emit_lock:
            try:
                events = [] if obs is None else self.tracker.update(obs)
            except Exception:
                log.exception("monitor tick failed")
                events = []
            for name, data in events:
                if self._monitor_stop.is_set():
                    break
                try:
                    self.writer.event(name, data)
                except Exception:
                    # One event that will not encode must not end the
                    # monitor: every later event comes from this thread.
                    log.exception("could not send a %s event", name)
            self._send_reported()

    def _push_offer(self):
        """fluentfin-mpv: what the bar mpv draws (``fluentfin.lua``) needs
        from the bridge: the Skip button's text for the segment on offer in
        ``user-data/fluentfin/skip`` (empty when there is none, or when
        FluentFin draws the controls -- ``set_controls`` ``"ui"``: the UI
        offers it from the ``segments`` event), and ``"1"`` in
        ``user-data/fluentfin/next`` while the queue has a next item (its
        Next button). Written when they change, and again for a new mpv;
        not once the monitor is stopping (a shutdown is ending mpv). Never
        raises: it runs on the monitor thread, which every later event
        comes from, and a lost mpv is the shim's to notice."""
        pm = self.runtime.pm
        if pm is None or not pm._mpv_alive or self._monitor_stop.is_set():
            return
        try:
            offer = self.tracker.offer()
            text = ""
            if offer is not None and not self.controls.ui:
                text = self.runtime.segment_label(offer["type"])
            following = self.tracker.following()
            has_next = ("1" if following and following.get("nextItemId")
                        else "")
            player = pm._player
            pushed = self._offer_pushed
            if (pushed is not None and pushed[0] is player
                    and pushed[1:] == (text, has_next)):
                return
            player.command("set", "user-data/fluentfin/skip", text)
            player.command("set", "user-data/fluentfin/next", has_next)
            self._offer_pushed = (player, text, has_next)
        except Exception:
            log.debug("could not update the bar", exc_info=True)

    def _observe(self, cheap=False):
        """One sample. ``cheap`` reads nothing from mpv."""
        from_window = self.window["mode"] if self.window else None
        with self._state_lock:
            obs = bridge_state.Observation(
                connected=self.connected, window=from_window,
                pending=self.pending, opening=self.opening,
                dev=self.dev_item, held=bool(self._held),
                restart_session=self._restart_shown)
        pm = self.runtime.pm
        if pm is None:
            return obs
        obs.video = pm._video
        obs.start_in_progress = bool(pm._start_in_progress)
        if obs.video is not None:
            # fluentfin-mpv: the ``segments`` and ``next`` events.
            obs.segments = self.runtime.segments_of(obs.video)
            obs.auto_play = self.runtime.auto_play()
        # fluentfin-mpv player controls: attached is the UI's window too
        embedded = from_window in ("embedded", "attached")
        if embedded:
            from . import window_embed

            obs.fullscreen = window_embed.ui_fullscreen
        else:
            obs.fullscreen = getattr(self, "_last_fullscreen", False)
        if cheap or not pm._mpv_alive:
            return obs
        player = pm._player
        try:
            obs.volume = player.volume
            obs.muted = player.mute
            if not embedded:
                obs.fullscreen = self._last_fullscreen = bool(
                    player.fullscreen)
            if obs.video is not None or obs.dev is not None:
                obs.position = player.playback_time
                obs.duration = player.duration
                obs.paused = player.pause
            if obs.video is not None:
                obs.mpv_aid = player.aid
                obs.mpv_sid = player.sid
                obs.external_subtitles = dict(pm.external_subtitles)
            if obs.dev is not None:
                obs.dev_idle = player.idle_active
        except Exception:
            log.debug("could not sample mpv", exc_info=True)
        return obs

    def _dev_step(self, obs):
        """Advance a ``play_file`` playback from what mpv reports."""
        item = obs.dev
        if item is None or not item.loaded:
            return
        idle = obs.dev_idle
        now = time.monotonic()
        if item.phase == "opening":
            if idle is False and obs.position is not None:
                item.phase = "active"
                item.seen_active = True
                return
            timeout = 30
            try:
                from .conf import settings

                timeout = settings.playback_timeout
            except Exception:
                pass
            if (idle and now - item.started > 3.0) or (
                    now - item.started > timeout):
                with self._state_lock:
                    if self.dev_item is not item:
                        return      # superseded meanwhile; not a failure
                    self.dev_item = None
                obs.dev = None
                self.tracker.report_error(MPV_FAILED, "mpv could not open "
                                          + item.url)
            return
        if idle:
            with self._state_lock:
                if self.dev_item is not item:
                    # A stop or a new start took it while this sample was
                    # being read, and marked why; mpv going idle is that
                    # stop's doing, not the end of the file.
                    return
                self.dev_item = None
            self.tracker.mark_ended(item, "eof")
            obs.dev = None


# ---------------------------------------------------------------------------
# Entry point


def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        prog="python -m jellyfin_mpv_shim.bridge",
        description="jellyfin-mpv-shim as a playback backend over stdio "
                    "(protocol %d)." % proto.PROTOCOL_VERSION)
    parser.add_argument("--config-dir", metavar="DIR",
                        help="configuration directory (default: %s)"
                        % default_config_dir())
    parser.add_argument("--log-file", metavar="PATH",
                        help="also write the log here (it always goes to "
                             "stderr)")
    parser.add_argument("--dev", action="store_true",
                        help="enable the play_file command (testing only)")
    return parser.parse_args(argv)


def preseed_shim_args(config_dir):
    """Hand the shim's argument parser its answer before anything asks it,
    so ``args.get_args()`` never parses the real ``sys.argv`` -- whose
    flags are ours, not the shim's."""
    from . import args as shim_args

    shim_args._args = shim_args._build_parser().parse_args(
        ["--config", config_dir, "--no-gui"])


def configure_logging(log_file=None, level=logging.INFO):
    root = logging.getLogger()
    for handler in list(root.handlers):
        root.removeHandler(handler)
    formatter = proto.RedactingFormatter(
        "%(asctime)s [%(levelname)8s] %(name)s: %(message)s")
    if sys.stderr is not None:
        stream = logging.StreamHandler(sys.stderr)
        stream.setFormatter(formatter)
        root.addHandler(stream)
    if log_file:
        try:
            handler = logging.FileHandler(log_file, mode="w",
                                          encoding="utf-8")
            handler.setFormatter(formatter)
            root.addHandler(handler)
        except OSError:
            log.warning("could not open the log file %s", log_file,
                        exc_info=True)
    root.setLevel(level)
    logging.getLogger("requests").setLevel(logging.CRITICAL)
    logging.getLogger("urllib3").setLevel(logging.WARNING)
    install_excepthooks()


def install_excepthooks():
    """Uncaught exceptions go through the log, i.e. through
    `RedactingFormatter` and into the log file -- not to stderr raw.

    Python's own hooks print the traceback straight to ``sys.stderr``,
    bypassing every handler: a request's repr or a URL in a frame's
    exception message reached the UI's log unredacted, and never reached
    ``--log-file`` at all. SystemExit in a thread stays silent, as it is by
    default."""
    crash_log = logging.getLogger("bridge.crash")

    def on_thread_exception(args):
        if args.exc_type is SystemExit:
            return
        name = getattr(args.thread, "name", None) or "a thread"
        crash_log.critical("Uncaught exception in %s", name,
                           exc_info=(args.exc_type, args.exc_value,
                                     args.exc_traceback))

    def on_exception(exc_type, exc_value, exc_traceback):
        crash_log.critical("Uncaught exception",
                           exc_info=(exc_type, exc_value, exc_traceback))

    threading.excepthook = on_thread_exception
    sys.excepthook = on_exception


def _install_signal_handlers(bridge):
    def on_signal(signum, frame):
        # No logging here: a handler can land while the logging lock is
        # held (mpv_shim._claim_sigterm).
        bridge.request_shutdown("signal %d" % signum, 0)

    for name in ("SIGTERM", "SIGINT", "SIGBREAK"):
        sig = getattr(signal, name, None)
        if sig is None:
            continue
        try:
            signal.signal(sig, on_signal)
        except (ValueError, OSError):
            pass


def main(argv=None):
    protocol_out = proto.isolate_stdout()
    options = parse_args(argv)
    sys.argv = sys.argv[:1]
    config_dir = os.path.abspath(options.config_dir or default_config_dir())
    os.makedirs(config_dir, exist_ok=True)
    preseed_shim_args(config_dir)
    configure_logging(options.log_file)

    runtime = ShimRuntime(config_dir)
    runtime.load_settings()
    logging.getLogger().setLevel(runtime.log_level())
    if not runtime.load_ok:
        log.error("conf.json in %s is not valid JSON and was ignored.",
                  config_dir)
    log.info("Bridge %s (protocol %d) starting; config %s%s",
             proto.BRIDGE_VERSION, proto.PROTOCOL_VERSION, config_dir,
             " [dev]" if options.dev else "")

    writer = proto.ProtocolWriter(protocol_out)
    bridge = Bridge(writer, runtime, dev=options.dev)
    _install_signal_handlers(bridge)
    stdin = getattr(sys.stdin, "buffer", None)
    if stdin is None:
        stdin = os.fdopen(0, "rb", buffering=0)
    bridge.start(stdin)
    status = bridge.wait()

    from . import exit_watchdog

    # On both ways out, the watchdog's forced exit included: an external mpv
    # still running is ended by its process handle (`end_mpv_process`).
    exit_watchdog.set_final_action(runtime.end_mpv_process)
    # Armed before the sequence: the failure it guards against is a step
    # that never returns (docs/architecture.md section 4). With the bridge's
    # own budget rather than the app's 20 s, which the UI does not wait for.
    exit_watchdog.arm(SHUTDOWN_BUDGET + EXIT_SLACK + 0.5)
    try:
        bridge.stop()
        if not bridge.clean_shutdown:
            status = status or 1
    except Exception:
        log.exception("shutdown failed")
        status = status or 1
    log.info("Bridge stopped (%s).", bridge.shutdown_reason)
    # The shim's threads that outlived the shutdown get what is left of the
    # budget, not the watchdog's own 3 s grace: a degraded shutdown answers
    # at the deadline and leaves the timeline and action threads parked.
    exit_watchdog.GRACE_SECONDS = exit_grace(
        bridge.shutdown_deadline, exit_watchdog.GRACE_SECONDS)
    exit_watchdog.finish(status)


def exit_grace(deadline, grace):
    """The straggler grace that keeps the exit within `EXIT_SLACK` of the
    shutdown's deadline: ``grace`` at most, nothing once past it."""
    if deadline is None:
        return grace
    return max(0.0, min(grace, deadline + EXIT_SLACK - time.monotonic()))


if __name__ == "__main__":
    main()
