"""The bridge's command dispatch, connect flow, events and shutdown.

Driven through `Bridge.handle_line` exactly as the reader thread drives it,
with a stand-in for `ShimRuntime` and the player behind it: nothing here
imports ``player`` (which would build a real mpv, docs/testing.md section 1)
or opens a socket. The real thing, mpv included, is
``tools/bridge_smoke.py`` (docs/bridge.md section 6).

The stand-ins model the fields the bridge reads off the player --
``_video``, ``_start_in_progress``, ``_mpv_alive``, ``_play_epoch``,
``external_subtitles``, mpv's ``http_header_fields`` (global, set by every
server start), the stop reports, and `run_action` deferring when the
player's lock is held -- because a fake that omits the field a test is
named after makes that test unreachable while it passes
(docs/testing.md section 4). `PlayerNamesTest` pins the method names the
fakes stand in for against the real classes.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import ast
import datetime
import io
import json
import logging
import os
import queue
import subprocess
import sys
import threading
import time
import types
import unittest
from unittest import mock

from tests import _tmpdirs

sys.argv = [sys.argv[0]]      # importing the shim reaches args.get_args()

from jellyfin_mpv_shim import bridge as bridge_mod  # noqa: E402
from jellyfin_mpv_shim import bridge_http  # noqa: E402
from jellyfin_mpv_shim import bridge_protocol as proto  # noqa: E402
from jellyfin_mpv_shim import bridge_state  # noqa: E402
from jellyfin_mpv_shim import window_embed  # noqa: E402
from jellyfin_mpv_shim.bridge_protocol import BridgeError  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

#: Item ids as Jellyfin writes them (N format), and one dashed (D format).
A = "a" * 32
B = "b" * 32
C = "c" * 32
DASHED = "1234ABCD-5678-90ef-ABCD-1234567890EF"
DASHED_N = "1234abcd567890efabcd1234567890ef"


# ---------------------------------------------------------------------------
# Stand-ins


#: Every PlaybackInfo answer carries a new PlaySessionId; so does each fake.
_SESSIONS = iter(range(1, 1 << 30))


class FakeVideo:
    """The fields of `media.Video` the bridge reports from. ``client`` is
    the server client it plays from (None: no server, so no Playing or
    Stopped request -- what the older tests here assume)."""

    def __init__(self, item_id, name="Item", parent=None, aid=None, sid=None,
                 client=None):
        self.item_id = item_id
        self.client = client
        self.item = {"Name": name, "Type": "Movie", "RunTimeTicks": 100}
        self.media_source = {"Id": "src-" + item_id, "MediaStreams": [
            {"Index": 1, "Type": "Audio", "Language": "eng"}]}
        self.playback_info = {"PlaySessionId": "ps-%s-%d" % (
            item_id, next(_SESSIONS))}
        self.play_method = "DirectPlay"
        self.is_transcode = False
        self.srcid = None
        self.aid, self.sid = aid, sid
        #: `Video.set_streams` marks a pick deliberate.
        self.explicit_tracks = False
        #: Whether mpv was given the server's header for this video's
        #: stream (the player sets it at each start, `_apply_auth_headers`).
        self.auth_via_header = False
        self.audio_uid, self.subtitle_uid = {1: 1}, {}
        self.subtitle_url, self.subtitle_enc = {}, set()
        self.parent = parent
        self.sidecars_reauthorized = 0
        #: The quality override the next negotiation asks for
        #: (`set_trs_override`; None: the default bitrate).
        self.trs_ovr = None
        #: fluentfin-mpv: `media.Video.intros`, the media segments fetched
        #: at the start (`media.Intro`: type, start, end, has_triggered).
        self.intros = []

    def reauthorize_sidecars(self):
        self.sidecars_reauthorized += 1

    def set_trs_override(self, video_bitrate, force_transcode):
        # `media.Video`'s: a quality change writes it, then restarts.
        self.trs_ovr = ((video_bitrate, force_transcode) if force_transcode
                        else None)


class FakeQueue:
    def __init__(self, ids, seq=0):
        self.queue = [{"Id": i, "PlaylistItemId": "p%d" % n}
                      for n, i in enumerate(ids)]
        self.seq = seq


class FakeHandle:
    """The mpv handle: properties the monitor reads, and the writes."""

    def __init__(self):
        self.volume, self.mute, self.fullscreen = 100.0, False, False
        self.pause, self.playback_time, self.duration = False, None, None
        self.aid = self.sid = None
        self.idle_active = True
        #: mpv's global header option: whatever the last start left there.
        self.http_header_fields = []
        self.commands = []
        self.played = []
        #: `http_header_fields` as it was at each `play(url)`.
        self.headers_at_play = []
        #: mpv's ``force-window``: what it was built with (`FakeRuntime`
        #: sets it as `window_embed.apply_to_options` would), and every
        #: write after that.
        self._force_window = False
        self.force_window_writes = []

    @property
    def force_window(self):
        return self._force_window

    @force_window.setter
    def force_window(self, value):
        self.force_window_writes.append(value)
        self._force_window = value

    def command(self, *args):
        self.commands.append(args)
        if args[:2] == ("set", "sub-pos"):
            # mpv's `set` takes a string (docs/mpv-backends.md section 1)
            self.sub_pos = float(args[2])
        if args[0] == "seek" and args[2:] == ("absolute",):
            self.playback_time = float(args[1])

    def play(self, url):
        self.played.append(url)
        self.headers_at_play.append(list(self.http_header_fields))

    def terminate(self):
        self.commands.append(("terminate",))


#: The header a server start installs (`_apply_auth_headers`).
AUTH_HEADER = ('Authorization: MediaBrowser Client="FluentFin", '
               'Token="tok-abcdef-0123456789"')


class FakeReporter:
    """`SessionReporter.drain`: whether the queued reports reached the
    server by ``timeout``. ``hang`` is a Stopped report that never does;
    ``queued`` (a report waiting to go) cannot be delivered in no time --
    a drain the shutdown left no budget for says so."""

    def __init__(self):
        self.hang = False
        self.queued = True
        self.drains = []
        #: `submit`: run at once (the worker keeping up), or held in
        #: `jobs` until `run_jobs` or a `drain` (a report still pending).
        self.hold = False
        self.jobs = []

    def submit(self, fn, label="report"):
        # The worker swallows a failed report (session_reporter.py).
        if self.hold:
            self.jobs.append(fn)
            return
        try:
            fn()
        except Exception:
            pass

    def run_jobs(self):
        jobs, self.jobs = self.jobs, []
        for fn in jobs:
            try:
                fn()
            except Exception:
                pass

    def drain(self, timeout=5.0):
        self.drains.append(timeout)
        if self.hang:
            time.sleep(timeout)
            return False
        if timeout > 0:
            self.run_jobs()
        return timeout > 0 or not self.queued


def _options(video):
    """`get_timeline_options` for ``video``: None when it has no PlaybackInfo
    -- a restart whose request failed stored the apiclient's None there --
    so there is nothing to report with."""
    if video.playback_info is None:
        return None
    return {"ItemId": video.item_id,
            "PlaySessionId": video.playback_info["PlaySessionId"]}


class FakePM:
    """`PlayerManager`, as far as the bridge reaches into it."""

    def __init__(self, journal):
        self.journal = journal
        self._player = FakeHandle()
        self._video = None
        self._start_in_progress = False
        self._load_cancelled = False
        self._mpv_alive = True
        self._play_epoch = 0
        self._lock = threading.RLock()
        #: The report lock: `send_timeline_stopped` takes it, and so does
        #: the timeline thread for the whole of a progress report -- which
        #: is what a hung server holds it across.
        self._tl_lock = threading.RLock()
        self._reporter = FakeReporter()
        self.should_send_timeline = False
        self.external_subtitles = {}
        self.on_load_error = None
        self.on_load_start = None
        self.on_reopen_failed = None
        #: What the next play() does: "load", "fail" (no video, no word),
        #: "fail_load" (mpv could not open it: the load error, then the
        #: stop `_play_media` does) or an exception to raise.
        self.next_play = "load"
        #: Run by play() before it flags the start (the real one resets the
        #: cancel flag and sets `_start_in_progress` a few lines in), and
        #: after it (the PlaybackInfo round trip), and in `_play_media`'s
        #: load wait with the lock held, once each.
        self.on_enter = None
        self.on_negotiate = None
        self.on_load = None
        #: Run as play() returns, the lock let go: where the finished
        #: callback mpv's abort queued, waiting on that lock, gets in --
        #: before the bridge's hook has finished with the start.
        self.on_return = None
        #: mpv has nothing loaded: `_abort_load` stopped it. The real stop()
        #: then reads `playback_abort` and, when mpv has already answered
        #: the abort, returns early and leaves `_video` set (seen in the
        #: probes); ``early_stop_after_abort`` picks that branch.
        self._aborted = False
        self.early_stop_after_abort = False
        #: Actions `run_action` deferred because the lock was held, and
        #: tasks `put_task` queued for the action thread.
        self.deferred = []
        #: What play_next() moves to, the way the real one does it (stop
        #: report, then play); None just records the call. play_prev()
        #: likewise with ``queue_prev``.
        self.queue_next = None
        self.queue_prev = None
        #: Run inside send_timeline_stopped, the report lock held, once.
        self.on_report = None
        #: set_streams: the pick needs a new stream (a transcode, a burned-in
        #: subtitle), so it restarts the item through play().
        self.restart_on_tracks = False
        #: What `update`'s end-of-file poll rescue reads: when the start
        #: began (play() sets it; the rescue waits 5 s past it), and mpv at
        #: the item's end -- ``eof-reached``, or for the last item, which
        #: mpv does not keep open, ``playback-abort``.
        self.start_time = time.time()
        self.eof_reached = False
        #: The real one's dedup of the end's two observers and the rescue:
        #: the first `_queue_finished` takes it (``has_lock``), and the next
        #: start's `_play_media` lets it go.
        self._finished_lock = threading.Lock()
        #: Set by `put_task` (and a deferred `run_action`): the action
        #: thread's trigger, which sends it round again at once (`pump`).
        self.woken = False
        #: mpv not answering (suspended): seconds each read of a property
        #: waits -- the reply bound the shutdown left -- before it times out
        #: (`_read_mpv`). None: mpv answers.
        self.mpv_hung = None

    def _read_mpv(self):
        """A property read over the IPC. Hung, it waits out the bound and
        raises TimeoutError -- one of `_mpv_errors` on the external
        backend. Read where the real one reads: stop()'s
        ``playback_abort``, and `get_timeline_options` for a report made
        without options (which swallows the timeout and leaves mpv for
        alive)."""
        if self.mpv_hung is not None:
            self._note("mpv_read", "timed out")
            time.sleep(self.mpv_hung)
            raise TimeoutError("mpv did not answer")

    def _note(self, *what):
        self.journal.append(what)

    def run_action(self, fn):
        """The real one's shape: inline when the lock is free, deferred to
        the action thread (here: `deferred`) when a start holds it."""
        self._note("run_action")
        if self._lock.acquire(blocking=False):
            try:
                return fn(self)
            finally:
                self._lock.release()
        self._defer(fn)
        return None

    def put_task(self, fn, *args):
        self._note("put_task")
        self._defer(lambda _pm: fn(*args))

    def _defer(self, fn):
        self.woken = True
        tasks = getattr(self, "_tasks", None)
        if tasks is not None:
            tasks.put(fn)
        else:
            self.deferred.append(fn)

    def run_deferred(self):
        while self.deferred:
            self.deferred.pop(0)(self)

    def _queue_finished(self):
        # The end's observers and the poll rescue: the `_finished_lock`
        # taken without blocking (whichever comes first has it), and the
        # callback queued with the epoch.
        has_lock = self._finished_lock.acquire(False)
        self.put_task(self.finished_callback, has_lock, self._play_epoch)

    def update(self):
        """`PlayerManager.update`, one pass of the action thread: the queued
        tasks, then the end-of-file poll rescue -- gated, as the real one
        is (`PlayerNamesTest` pins it), on the progress reports being on and
        the start being over 5 s old: an item at its end has its finished
        callback queued again."""
        self.run_deferred()
        if (self._video is not None and self.should_send_timeline
                and time.time() - (self.start_time or 0) > 5
                and self.eof_reached):
            self._queue_finished()

    def pump(self, passes=50):
        """The action thread's loop (`ActionThread.run`): update(), and round
        again at once while something queued meanwhile has set its trigger
        (`put_task`); otherwise it would wait a second. Returns how many
        passes ran back to back, at most ``passes``."""
        made = 0
        while made < passes:
            self.woken = False
            self.update()
            made += 1
            if not self.woken:
                break
        return made

    def restart_playback(self):
        # @synchronous("_lock"): play() on the video playing (a quality
        # change, or a track change that needs a new stream).
        with self._lock:
            self._note("restart_playback")
            video = self._video
            if video is None:
                return False
            self.play(video)
            return True

    def start_action_thread(self):
        """Deferred work runs on a thread of its own from now on, as the
        shim's action thread runs it. Returns the stopper."""
        self._tasks = queue.Queue()

        def pump():
            while True:
                fn = self._tasks.get()
                if fn is None:
                    return
                try:
                    fn(self)
                except Exception:
                    logging.getLogger("test").exception("deferred task")

        thread = threading.Thread(target=pump, daemon=True)
        thread.start()

        def stop():
            self._tasks.put(None)
            thread.join(5)
        return stop

    def _revoke_auth_header(self, video=None):
        if video is not None:
            video.auth_via_header = False
        if not self._mpv_alive:
            return
        self._player.http_header_fields = []

    def _apply_auth_headers(self, video):
        # Every server start installs it (a global), and says it took.
        if not self._mpv_alive:
            return False
        self._player.http_header_fields = [AUTH_HEADER]
        return True

    def send_timeline_stopped(self, finished=False, options=None,
                              client=None, stopped=None):
        # @synchronous("_tl_lock") in the real one.
        with self._tl_lock:
            self.should_send_timeline = False
            hook, self.on_report = self.on_report, None
            if hook is not None:
                hook()              # inside, the report lock held
            video = stopped if stopped is not None else self._video
            if video is not None:
                self._note("report_stopped", video.item_id)
            elif options is not None and client is not None:
                # Built from bookkeeping once `_video` is gone
                # (`_report_stopped_offline`): the options say what.
                self._note("report_stopped", options.get("ItemId"))
            # The real one's options and client: given, else from `_video`
            # (not `stopped`); the request goes on the report worker.
            source = self._video
            if options is None and source is not None:
                try:
                    self._read_mpv()    # get_timeline_options: swallowed
                except TimeoutError:
                    pass
                options = _options(source)
            if client is None and source is not None:
                client = source.client
            if options is not None and client is not None:
                self._reporter.submit(
                    lambda: client.jellyfin.session_stop(options),
                    "session_stop")

    def send_timeline_initial(self):
        # `_play_media` calls it under the player's lock once the start has
        # succeeded: the new play session's Playing.
        video = self._video
        if video is not None:
            self._note("report_playing", video.item_id)

    def terminate(self):
        # @synchronous("_lock"); stop() then the reporter's own drain.
        with self._lock:
            self.stop()
            self._note("terminate")

    def release_stream(self, video):
        if video is not None:
            self._note("release_stream", video.item_id)

    def _unload_ended(self, video):
        self._note("unload", video.item_id)
        self.should_send_timeline = False
        self._video = None
        self.release_stream(video)

    def _abort_load(self):
        self._aborted = True

    def pause_if_playing(self):
        self._note("pause_if_playing")
        self._player.pause = True

    def play_if_paused(self):
        self._note("play_if_paused")
        self._player.pause = False

    def toggle_pause(self):
        self._note("toggle_pause")

    def timeline_handle(self):
        self._note("timeline_handle")

    def seek(self, seconds, absolute=False):
        self._note("seek", seconds, absolute)

    def stop(self):
        # @synchronous("_lock") in the real one, and it reports the stop --
        # unless mpv has already dropped the file (playback_abort), when it
        # returns early and leaves `_video` alone.
        with self._lock:
            self._note("stop")
            if self._video is not None and self._mpv_alive:
                try:
                    self._read_mpv()            # playback_abort
                except TimeoutError:
                    # mpv lost: the video goes, reported from bookkeeping.
                    self._handle_mpv_disconnect()
                    return
            if self._aborted and self.early_stop_after_abort:
                return
            video, self._video = self._video, None
            if video is not None:
                self.release_stream(video)
                # Options and client from the video it stops, as the real
                # one passes them.
                self.send_timeline_stopped(
                    options=_options(video), client=video.client,
                    stopped=video)

    def set_volume(self, volume):
        self._note("set_volume", volume)

    def set_streams(self, aid, sid):
        # @synchronous("_lock"). The real one: `Video.set_streams` writes the
        # pick (deliberate from then on), and one that needs a new stream
        # restarts the item -- `restart_playback`, play() on the same video
        # under this lock (``restart_on_tracks``).
        with self._lock:
            self._note("set_streams", aid, sid)
            video = self._video
            if video is None or not self.restart_on_tracks:
                return
            if aid is not None:
                video.aid = aid
            if sid is not None:
                video.sid = sid
            video.explicit_tracks = True
            self.play(video)

    def play_next(self):
        self._note("play_next")
        if self.queue_next is not None:
            with self._lock:        # @synchronous("_lock") in the real one
                video, self.queue_next = self.queue_next, None
                self._move_to(video)

    def timeline_handle(self):
        # The real one wakes the progress reporter for an early report.
        self._note("timeline_handle")

    def skip_intro(self):
        # fluentfin-mpv: the shim's Skip (`PlayerManager.skip_intro`): a
        # seek to the end of the FIRST segment the position is in (end
        # included), if any.
        self._note("skip_intro")
        video, position = self._video, self._player.playback_time
        for intro in (getattr(video, "intros", None) or ()):
            if position is not None and intro.start <= position <= intro.end:
                self._player.playback_time = intro.end
                intro.has_triggered = True
                return

    def play_prev(self):
        self._note("play_prev")
        if self.queue_prev is not None:
            with self._lock:
                video, self.queue_prev = self.queue_prev, None
                self._move_to(video)

    def _move_to(self, video):
        # The queue's videos play from the client the queue was made with.
        if video.client is None and self._video is not None:
            video.client = self._video.client
        self.send_timeline_stopped(True)
        self.play(video)

    def set_fullscreen(self, value):
        self._note("set_fullscreen", value)

    def set_embedded_fullscreen(self, value):
        self._note("set_embedded_fullscreen", value)

    #: fluentfin-mpv (set_controls): the style's "controls at all", which a
    #: return to mpv's controls restores (`PlayerManager.osc_enabled`).
    osc_enabled = True

    def enable_osc(self, enabled):
        self._note("enable_osc", enabled)

    def cancel_load(self):
        # Gated on the start's own flag, like the real one.
        self._note("cancel_load")
        if not self._start_in_progress:
            return False
        self._load_cancelled = True
        return True

    def _ensure_mpv(self):
        pass

    def _handle_mpv_disconnect(self):
        """The real one: mpv marked lost (once), the video dropped
        (`_end_playback`), and its Stopped built from bookkeeping
        (`_report_stopped_offline`: the options name the session, `_video`
        already gone), then its stream released. There on a thread of its
        own, which takes the report lock once the caller lets go of it;
        inline here, so what the report is does not hang on that thread's
        timing."""
        self._note("disconnect")
        if not self._mpv_alive:
            return
        self._mpv_alive = False
        self.should_send_timeline = False
        video, self._video = self._video, None
        if video is not None and video.playback_info is not None:
            self.send_timeline_stopped(options=_options(video),
                                       client=video.client)
            self.release_stream(video)

    def play(self, video, *args, **kwargs):
        """The real one's order: a few steps, then the cancel flag reset and
        the start flagged; the negotiation; then `_play_media`, which holds
        the file paused while it loads and checks for a cancel."""
        self._note("play", getattr(video, "item_id", None))
        # The real one's first step: no progress report for the start; only
        # a start that loads turns them back on (`_play_media`).
        self.should_send_timeline = False
        self.start_time = time.time()
        hook, self.on_enter = self.on_enter, None
        if hook is not None:
            hook()
        self._load_cancelled = False
        self._start_in_progress = True
        try:
            # `_apply_auth_headers`: every server start installs it.
            try:
                video.auth_via_header = self._apply_auth_headers(video)
            except AttributeError:
                pass                        # not a video with attributes
            hook, self.on_negotiate = self.on_negotiate, None
            if hook is not None:
                hook()
            outcome = self.next_play
            if outcome == "fail":
                # play()'s "no URL found" return, before its cancel check:
                # the header it installed is revoked (it is global).
                self.next_play = "load"
                self._revoke_auth_header(video)
                return
            if self._load_cancelled:
                # "Playback start cancelled during the profile lookup": a
                # cancel during the negotiation returns before mpv is
                # touched, and whatever was playing stays loaded.
                self._load_cancelled = False
                self.next_play = "load"
                return
            outcome, self.next_play = self.next_play, "load"
            if isinstance(outcome, BaseException):
                raise outcome
            with self._lock:                # _play_media is @synchronous
                # `_notify_load_start`: before the cancel check and the load,
                # every way out after it drops what mpv had (`_abort_load`).
                if self.on_load_start is not None:
                    self.on_load_start({"title": video.item_id})
                self._player.pause = True   # held for the load (#785)
                hook, self.on_load = self.on_load, None
                if hook is not None:
                    hook()                  # mpv loading, the lock held
                if self._load_cancelled:
                    # "Playback start cancelled": drop the file, stop().
                    self._load_cancelled = False
                    self._abort_load()
                    self.stop()
                    return
                if outcome == "load":
                    self._aborted = False
                    self.eof_reached = False
                    self._video = video
                    self._play_epoch += 1
                    # With the epoch: an end queued before it is stale.
                    if self._finished_lock.locked():
                        self._finished_lock.release()
                    # `update_subtitle_visuals`, which the real start runs
                    # just before its Playing: the shim's own sub-pos (its
                    # "bottom" setting), over whatever was there.
                    self._player.sub_pos = 100.0
                    self.send_timeline_initial()
                    self.should_send_timeline = True
                    # `_play_media` ends with an unconditional
                    # set_paused(False).
                    self._player.pause = False
                elif outcome == "fail_load":
                    # The load error first, then stop(), which takes down
                    # what was playing -- or returns early, mpv having
                    # dropped the file.
                    if self.on_load_error is not None:
                        self.on_load_error({"detail": "could not open",
                                            "timed_out": False})
                    self._abort_load()
                    self.stop()
        finally:
            hook, self.on_return = self.on_return, None
            if hook is not None:
                hook()
            self._start_in_progress = False

    def finished_callback(self, has_lock, epoch=None):
        # @synchronous("_lock"); stale epochs skipped; with a next item
        # (``queue_next``) the auto-advance -- the stop report, then play()
        # with the lock held -- but only for the callback that took the
        # `_finished_lock` (``has_lock``; "No lock, skipping..."); at the
        # end of the queue: the stop report, then the unload.
        with self._lock:
            self._note("finished_callback", epoch)
            if epoch is not None and epoch != self._play_epoch:
                return
            video = self._video
            if video is None:
                return
            if self.queue_next is not None:
                if not has_lock:
                    self._note("finished_callback_no_lock")
                    return
                new, self.queue_next = self.queue_next, None
                self._move_to(new)
                return
            self.send_timeline_stopped(True)
            self._unload_ended(video)


class FakeGuard:
    """`bridge_http.ClientGuard.last_status`: what the last request this
    thread made was answered, per calling thread."""

    def __init__(self):
        self._local = threading.local()

    def last_status(self):
        return getattr(self._local, "status", None)


class FakeApi:
    """``client.jellyfin``: the Stopped report, answered as ``answer``
    says -- a status (204 accepted; 500 the apiclient turns into None), an
    exception to raise, or an Event to wait for first (a slow server)."""

    def __init__(self, guard):
        self.guard = guard
        self.answer = 204
        self.stops = []

    def session_stop(self, data):
        self.stops.append(dict(data))
        answer = self.answer
        if isinstance(answer, threading.Event):
            answer.wait(10)
            answer = 204
        self.guard._local.status = None
        if isinstance(answer, BaseException):
            raise answer
        self.guard._local.status = answer
        return None


class FakeClient:
    def __init__(self, server):
        self.server = server
        self.stopped = False
        self.retired = False
        self.bridge_guard = FakeGuard()
        self.jellyfin = FakeApi(self.bridge_guard)

    def stop(self):
        self.stopped = True


class FakeRuntime:
    def __init__(self):
        self.journal = []
        self.pm = None
        self.started = []
        #: ``defer_surface`` of each `start_player` (connect.prewarm).
        self.deferred = []
        self.fail_start = None
        self.verify_error = None
        self.clients = []
        self.playbacks = []
        self.idle_volume = None
        self.shut = False
        #: start_playback goes on to the player's play(), as the shim's
        #: does once the item has resolved; off, it only records.
        self.start_plays = False
        #: Raised by start_playback before play(): the item fetch failing.
        self.fail_fetch = None
        #: (entered, release): start_player signals, then waits -- mpv
        #: still starting.
        self.start_gate = None
        #: Whether shutdown() found the player.
        self.shutdown_saw_player = None
        self.shutdown_result = True
        #: Set by the Bridge, as on `ShimRuntime`.
        self.on_stop_outcome = None
        self.before_final_stop = None
        #: shutdown() runs the real `ShimRuntime._final_report` on the
        #: player: both locks, ``before_final_stop``, stop(), the drain.
        self.final_report = False

    _run_steps = staticmethod(bridge_mod.ShimRuntime._run_steps)
    _bound_replies = staticmethod(bridge_mod.ShimRuntime._bound_replies)
    # fluentfin-mpv: the real ones (the shim's settings and labels).
    segments_of = staticmethod(bridge_mod.ShimRuntime.segments_of)
    auto_play = staticmethod(bridge_mod.ShimRuntime.auto_play)
    segment_label = staticmethod(bridge_mod.ShimRuntime.segment_label)

    def start_player(self, window, defer_surface=False):
        self.journal.append(("start_player", window["mode"]))
        if self.start_gate is not None:
            entered, release = self.start_gate
            entered.set()
            release.wait(10)
        if self.fail_start is not None:
            raise self.fail_start
        self.started.append(window)
        self.deferred.append(defer_surface)
        # As `ShimRuntime.start_player`: the embedded window (or the UI's own
        # attached mpv), and whether its video output waits for the first
        # item (connect.prewarm).
        embedded = window["mode"] == "embedded"
        window_embed.configure(window["hwnd"] if embedded else None,
                               attach=window["mode"] == "attached",
                               defer_surface=defer_surface and embedded)
        self.pm = FakePM(self.journal)
        # The construction option `window_embed.apply_to_options` gives an
        # embedded mpv (a standalone one: the shim's own, off here).
        self.pm._player._force_window = (embedded and
                                         not window_embed.surface_deferred)
        return {"mpv": "mpv v0.41.0-test", "mpvBackend": "external"}

    def mpv_errors(self):
        return (BrokenPipeError,)

    def make_client(self, server):
        self.journal.append(("make_client", server["url"]))
        client = FakeClient(server)
        # The real wrapper, as `ShimRuntime.make_client` installs it.
        bridge_http.watch_stops(client, self._stop_outcome)
        self.clients.append(client)
        return client

    def _stop_outcome(self, item_id, session, outcome):
        if self.on_stop_outcome is not None:
            self.on_stop_outcome(item_id, session, outcome)

    def verify_client(self, client, server):
        self.journal.append(("verify_client",))
        self.verified = client
        if self.verify_error is not None:
            raise self.verify_error

    def stop_client(self, client):
        client.stop()

    def retire_client(self, client, grace=None):
        self.journal.append(("retire_client", client.server["url"], grace))
        client.retired = True

    def start_playback(self, client, user_id, request):
        self.journal.append(("start_playback", request["itemIds"]))
        self.playbacks.append((client, user_id, request))
        if self.fail_fetch is not None:
            raise self.fail_fetch
        if self.start_plays:
            self.pm.play(FakeVideo(request["itemIds"][request["startIndex"]],
                                   client=client))
        return True

    def set_idle_volume(self, volume):
        self.idle_volume = volume

    def set_subtitle_size(self, percent):
        self.subtitle_size = percent

    def set_auto_play(self, value):
        self.auto_play_set = value

    def shutdown(self, deadline=None):
        self.shut = True
        self.shutdown_deadline = deadline
        self.shutdown_saw_player = self.pm is not None
        if self.final_report and self.pm is not None:
            return bridge_mod.ShimRuntime._final_report(
                self, self.pm, deadline or time.monotonic() + 8)
        return self.shutdown_result


class Sink(io.RawIOBase):
    def __init__(self):
        self.data = bytearray()

    def writable(self):
        return True

    def write(self, b):
        self.data += bytes(b)
        return len(b)


SERVER = {
    "server": {"url": "https://jf.example", "id": "S", "name": "Home"},
    "user": {"id": "0123456789abcdef0123456789abcdef", "name": "alice"},
    "accessToken": "tok-abcdef-0123456789",
    "device": {"id": "dev-1", "name": "Desk"},
    "client": {"name": "FluentFin", "version": "1.0.0"},
}


class BridgeCase(unittest.TestCase):
    dev = False

    def setUp(self):
        self.sink = Sink()
        self.runtime = FakeRuntime()
        self.bridge = bridge_mod.Bridge(proto.ProtocolWriter(self.sink),
                                        self.runtime, dev=self.dev)
        self.next_id = 0
        self.addCleanup(window_embed.configure, None)
        self.addCleanup(setattr, window_embed, "on_fullscreen_request", None)
        self.addCleanup(proto._secrets.clear)
        # fluentfin-mpv: a connect points the S key at this bridge.
        from jellyfin_mpv_shim import fluentfin_osc

        self.addCleanup(setattr, fluentfin_osc, "skip_handler", None)
        patcher = mock.patch.object(proto, "is_window_handle",
                                    return_value=True)
        self.is_window = patcher.start()
        self.addCleanup(patcher.stop)

    def messages(self):
        return [json.loads(line) for line in bytes(self.sink.data).splitlines()]

    def send(self, cmd, args=None):
        """One request, through the reader's dispatch and -- if it was
        queued -- the control worker's, run here on this thread. Returns
        the one response."""
        self.next_id += 1
        msg = {"id": self.next_id, "cmd": cmd}
        if args is not None:
            msg["args"] = args
        self.bridge.handle_line(json.dumps(msg).encode() + b"\n")
        while not self.bridge._control_q.empty():
            self.bridge.execute(self.bridge._control_q.get_nowait())
        answers = [m for m in self.messages() if m.get("id") == self.next_id]
        self.assertEqual(len(answers), 1, "exactly one response per request")
        return answers[0]

    def code(self, cmd, args=None):
        answer = self.send(cmd, args)
        return None if answer["ok"] else answer["error"]["code"]

    def elsewhere(self, cmd, args=None):
        """One command run to its answer on a thread of its own -- the
        control worker -- while this thread is inside the player."""
        self.next_id += 1
        req = proto.Request(self.next_id, cmd, args or {})
        worker = threading.Thread(target=self.bridge.execute, args=(req,),
                                  daemon=True)
        worker.start()
        worker.join(10)
        self.assertFalse(worker.is_alive(), "%s never answered" % cmd)
        return [m for m in self.messages() if m.get("id") == req.id][0]

    def start_threads(self):
        """The bridge's own worker and monitor threads, for the tests that
        are about a thread surviving; stopped at cleanup."""
        self.bridge.start(stdin=None)

        def stop():
            self.bridge._monitor_stop.set()
            self.bridge._wake.set()
            self.bridge._control_q.put(None)
            self.bridge._play_q.put(None)
            for thread in self.bridge._threads:
                thread.join(5)

        self.addCleanup(stop)

    def thread(self, name):
        return [t for t in self.bridge._threads if t.name == name][0]

    def wait_message(self, predicate, timeout=5.0):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for message in self.messages():
                if predicate(message):
                    return message
            time.sleep(0.02)
        return None

    def run_plays(self):
        """The play worker's own loop, run here until the queue is empty."""
        self.bridge._play_q.put(None)
        self.bridge._play_loop()

    def connect(self, window=None, **extra):
        args = {"protocol": 1, "window": window or {"mode": "standalone"}}
        args.update(extra)
        return self.send("connect", args)

    def connect_server(self, window=None):
        return self.connect(window, **SERVER)


# ---------------------------------------------------------------------------


class DispatchTest(BridgeCase):
    def test_ping(self):
        self.assertEqual(self.send("ping"), {"id": 1, "ok": True,
                                             "result": {}})

    def test_get_state_before_connect(self):
        state = self.send("get_state")["result"]
        self.assertEqual((state["state"], state["connected"],
                          state["window"]), ("idle", False, None))
        self.assertEqual(set(state), {
            "state", "connected", "itemId", "mediaSourceId", "playSessionId",
            "playMethod", "positionTicks", "durationTicks", "volume",
            "muted", "audioStreamIndex", "subtitleStreamIndex", "queue",
            "fullscreen", "window"})

    def test_unknown_command(self):
        self.assertEqual(self.code("frobnicate"), "unknown_cmd")

    def test_play_file_does_not_exist_without_dev(self):
        self.assertEqual(self.code("play_file", {"url": "x"}), "unknown_cmd")

    def test_garbage_is_an_error_event_and_nothing_else(self):
        self.bridge.handle_line(b"not json at all\n")
        self.bridge.handle_line(b'{"cmd": "ping"}\n')
        msgs = self.messages()
        self.assertEqual(len(msgs), 2)
        for msg in msgs:
            self.assertEqual(msg["event"], "error")
            self.assertEqual(msg["data"]["code"], "bad_args")
            self.assertIs(msg["data"]["fatal"], False)
        self.assertEqual(self.send("ping")["ok"], True)

    def test_everything_but_three_needs_connect(self):
        for cmd, args in (("play", {"itemIds": [A]}), ("pause", None),
                          ("unpause", None), ("toggle_pause", None),
                          ("stop", None), ("next", None), ("previous", None),
                          ("seek", {"positionTicks": 1}),
                          ("seek_relative", {"seconds": 1}),
                          ("set_audio", {"index": 1}),
                          ("set_subtitle", {"index": -1}),
                          ("set_volume", {"volume": 5}),
                          ("set_subtitle_size", {"percent": 120}),
                          ("set_speed", {"speed": 1.5}),
                          ("set_subtitle_delay", {"seconds": 0.1}),
                          ("set_auto_play", {"autoPlay": False}),
                          ("set_mute", {"muted": True}),
                          ("set_fullscreen", {"fullscreen": True})):
            with self.subTest(cmd=cmd):
                self.assertEqual(self.code(cmd, args), "not_connected")
        self.assertEqual(self.runtime.journal, [])

    def test_bad_args_once_connected(self):
        self.connect_server()
        for cmd, args in (("seek", {"positionTicks": "10"}),
                          ("seek", {}),
                          ("seek_relative", {"seconds": True}),
                          ("set_volume", {"volume": 101}),
                          ("set_volume", {"volume": -1}),
                          ("set_mute", {"muted": "yes"}),
                          ("set_subtitle_size", {"percent": 49}),
                          ("set_subtitle_size", {"percent": 201}),
                          ("set_subtitle_size", {"percent": "120"}),
                          ("set_subtitle_size", {}),
                          ("set_speed", {"speed": 0.25}),
                          ("set_speed", {"speed": 3}),
                          ("set_speed", {"speed": "1.5"}),
                          ("set_speed", {}),
                          ("set_subtitle_delay", {"seconds": 31}),
                          ("set_subtitle_delay", {"seconds": "-1"}),
                          ("set_subtitle_delay", {}),
                          ("set_auto_play", {"autoPlay": "no"}),
                          ("set_auto_play", {"autoPlay": None}),
                          ("set_auto_play", {}),
                          ("set_audio", {"index": -1}),
                          ("set_subtitle", {"index": -2}),
                          ("set_fullscreen", {}),
                          ("play", {"itemIds": []}),
                          ("play", {"itemIds": [A], "startIndex": 3})):
            with self.subTest(cmd=cmd, args=args):
                self.assertEqual(self.code(cmd, args), "bad_args")

    def test_no_playback(self):
        self.connect_server()
        for cmd, args in (("pause", None), ("unpause", None),
                          ("toggle_pause", None), ("next", None),
                          ("previous", None), ("seek", {"positionTicks": 0}),
                          ("seek_relative", {"seconds": -10}),
                          ("set_audio", {"index": 1}),
                          ("set_subtitle", {"index": 2})):
            with self.subTest(cmd=cmd):
                self.assertEqual(self.code(cmd, args), "no_playback")
        # Stopping nothing, the volume and fullscreen are not about playback.
        for cmd, args in (("stop", None), ("set_volume", {"volume": 30}),
                          ("set_mute", {"muted": True}),
                          ("set_fullscreen", {"fullscreen": True})):
            with self.subTest(cmd=cmd):
                self.assertTrue(self.send(cmd, args)["ok"])
        self.assertEqual(self.runtime.idle_volume, 30)
        self.assertEqual(self.runtime.pm._player.volume, 30)
        self.assertIs(self.runtime.pm._player.mute, True)

    def test_subtitle_size_holds_with_nothing_playing(self):
        """fluentfin-mpv: kept as the shim's setting and written to mpv's
        ``sub-scale`` (a percent is a scale of percent / 100)."""
        self.connect_server()
        self.assertTrue(self.send("set_subtitle_size", {"percent": 130})["ok"])
        self.assertEqual(self.runtime.subtitle_size, 130)
        self.assertEqual(self.runtime.pm._player.sub_scale, 1.3)
        self.assertTrue(self.send("set_subtitle_size", {"percent": 50})["ok"])
        self.assertEqual(self.runtime.pm._player.sub_scale, 0.5)

    def test_auto_play_is_the_uis_choice_held_for_later_items(self):
        """1.2.0: ``set_auto_play`` sets the shim's ``auto_play`` (what
        `finished_callback` reads at an item's end), with or without
        playback; nothing goes to the server."""
        self.connect_server()
        self.assertTrue(self.send("set_auto_play", {"autoPlay": False})["ok"])
        self.assertIs(self.runtime.auto_play_set, False)
        self.runtime.pm._video = FakeVideo(A)
        del self.runtime.journal[:]
        self.assertTrue(self.send("set_auto_play", {"autoPlay": True})["ok"])
        self.assertIs(self.runtime.auto_play_set, True)
        self.assertEqual(self.runtime.journal, [])
        # the real runtime's: the shim's own setting, which the next event reads
        from jellyfin_mpv_shim.conf import settings
        saved = settings.auto_play
        try:
            bridge_mod.ShimRuntime.set_auto_play(False)
            self.assertIs(settings.auto_play, False)
            self.assertIs(bridge_mod.ShimRuntime.auto_play(), False)
            bridge_mod.ShimRuntime.set_auto_play(True)
            self.assertIs(bridge_mod.ShimRuntime.auto_play(), True)
        finally:
            settings.auto_play = saved

    def test_speed_and_subtitle_delay_are_written_to_mpv(self):
        """1.2.0: ``set_speed`` and ``set_subtitle_delay`` write
        mpv's ``speed`` and ``sub-delay`` at once, with nothing playing and
        while an item plays; nothing goes to the server."""
        self.connect_server()
        player = self.runtime.pm._player
        self.assertTrue(self.send("set_speed", {"speed": 1.5})["ok"])
        self.assertEqual(player.speed, 1.5)
        self.assertTrue(
            self.send("set_subtitle_delay", {"seconds": -0.3})["ok"])
        self.assertEqual(player.sub_delay, -0.3)
        self.runtime.pm._video = FakeVideo(A)
        del self.runtime.journal[:]
        self.assertTrue(self.send("set_speed", {"speed": 1})["ok"])
        self.assertEqual(player.speed, 1.0)
        self.assertTrue(
            self.send("set_subtitle_delay", {"seconds": 0.2})["ok"])
        self.assertEqual(player.sub_delay, 0.2)
        self.assertEqual(self.runtime.journal, [])

    def test_unknown_fields_are_ignored(self):
        self.connect_server()
        self.assertTrue(self.send("set_volume", {"volume": 5,
                                                 "fadeMs": 200})["ok"])

    def test_transport_maps_onto_the_player(self):
        self.connect_server()
        pm = self.runtime.pm
        pm._video = FakeVideo(A)
        del self.runtime.journal[:]
        self.send("pause")
        self.send("unpause")
        self.send("toggle_pause")
        self.send("seek", {"positionTicks": 6_000_000_000})
        self.send("seek_relative", {"seconds": -10})
        self.send("set_volume", {"volume": 70})
        self.send("set_fullscreen", {"fullscreen": True})
        self.send("set_audio", {"index": 2})
        self.send("set_subtitle", {"index": -1})
        self.send("next")
        self.send("previous")
        self.run_plays()
        calls = [c for c in self.runtime.journal if c != ("run_action",)]
        self.assertEqual(calls, [
            ("pause_if_playing",), ("play_if_paused",), ("toggle_pause",),
            ("timeline_handle",), ("seek", 600.0, True), ("seek", -10.0, False),
            ("set_volume", 70), ("set_fullscreen", True),
            ("set_streams", 2, None), ("set_streams", None, -1),
            ("play_next",), ("play_prev",)])

    def test_shutdown_answers_after_the_shutdown_sequence(self):
        self.assertEqual(self.bridge.handle_line(
            b'{"id": 9, "cmd": "shutdown"}\n'), "stop")
        self.assertEqual(self.messages(), [], "not before playback stopped")
        self.assertEqual(self.bridge.wait(), 0)
        self.bridge.stop()
        self.assertTrue(self.runtime.shut)
        self.assertEqual(self.messages(), [{"id": 9, "ok": True,
                                            "result": {}}])

    def test_queued_commands_are_still_answered_at_shutdown(self):
        self.bridge.handle_line(b'{"id": 3, "cmd": "pause"}\n')
        self.bridge.handle_line(b'{"id": 4, "cmd": "shutdown"}\n')
        self.bridge.stop()
        answered = {m["id"]: m["ok"] for m in self.messages()}
        self.assertEqual(answered, {3: False, 4: True})


class EofTest(BridgeCase):
    def test_eof_is_a_shutdown(self):
        stdin = io.BytesIO(b'{"id": 1, "cmd": "ping"}\n')
        self.bridge._read_loop(stdin)
        self.assertEqual(self.bridge.wait(), 0)
        self.assertEqual(self.bridge.shutdown_reason, "stdin closed")
        self.assertEqual(self.messages(), [{"id": 1, "ok": True,
                                            "result": {}}])

    def test_a_closed_stdout_is_a_shutdown(self):
        self.bridge.writer._stream = mock.Mock(
            write=mock.Mock(side_effect=BrokenPipeError))
        self.bridge.writer.event("state", {})
        self.assertEqual(self.bridge.wait(), 0)
        self.assertEqual(self.bridge.shutdown_reason, "stdout closed")


class ConnectTest(BridgeCase):
    def test_the_first_connect_needs_a_window(self):
        args = {"protocol": 1}
        args.update(SERVER)
        self.assertEqual(self.code("connect", args), "bad_args")
        self.assertEqual(self.runtime.started, [])

    def test_connect_starts_mpv_and_the_client(self):
        answer = self.connect_server()
        self.assertEqual(answer["result"], {"mpv": "mpv v0.41.0-test",
                                            "mpvBackend": "external"})
        self.assertTrue(self.bridge.connected)
        self.assertEqual(self.runtime.journal[:3], [
            ("start_player", "standalone"),
            ("make_client", "https://jf.example"), ("verify_client",)])
        self.assertEqual(self.runtime.clients[0].server["deviceId"], "dev-1")

    def test_the_token_is_redacted_before_the_request_is_logged(self):
        token = SERVER["accessToken"]
        seen = []

        def info(message, *args):
            # What the first log line about this request would print.
            seen.append(proto.redact(token))

        with mock.patch.object(bridge_mod.log, "info", side_effect=info):
            self.bridge.handle_line(json.dumps(dict(
                id=1, cmd="connect",
                args=dict(SERVER, protocol=1,
                          window={"mode": "standalone"}))).encode())
        self.assertTrue(seen)
        self.assertNotIn(token, seen[0])

    def test_the_window_is_fixed_for_the_process(self):
        embedded = {"mode": "embedded", "hwnd": 4242}
        self.assertTrue(self.connect_server(embedded)["ok"])
        self.assertEqual(self.code("connect", dict(
            SERVER, protocol=1, window={"mode": "standalone"})),
            "window_locked")
        self.assertEqual(self.code("connect", dict(
            SERVER, protocol=1, window={"mode": "embedded", "hwnd": 77})),
            "window_locked")
        self.assertTrue(self.connect_server(embedded)["ok"])
        self.assertTrue(self.send("connect", dict(SERVER, protocol=1))["ok"])
        self.assertEqual(len(self.runtime.started), 1)
        self.assertEqual(self.runtime.started[0],
                         {"mode": "embedded", "hwnd": 4242})

    def test_a_handle_that_is_not_a_window(self):
        self.is_window.return_value = False
        self.assertEqual(self.code("connect", dict(
            SERVER, protocol=1, window={"mode": "embedded", "hwnd": 5})),
            "bad_args")
        self.assertEqual(self.runtime.started, [])

    def test_mpv_failing_leaves_the_window_open_to_a_retry(self):
        self.runtime.fail_start = FileNotFoundError("mpv.exe")
        self.assertEqual(self.code("connect", dict(
            SERVER, protocol=1, window={"mode": "standalone"})),
            "mpv_failed")
        self.assertIsNone(self.bridge.window)
        self.runtime.fail_start = None
        self.assertTrue(self.connect_server(
            {"mode": "embedded", "hwnd": 9})["ok"])

    def test_auth_failure(self):
        self.runtime.verify_error = BridgeError(proto.AUTH_FAILED, "401")
        self.assertEqual(self.code("connect", dict(
            SERVER, protocol=1, window={"mode": "standalone"})),
            "auth_failed")
        self.assertFalse(self.bridge.connected)
        self.assertTrue(self.runtime.clients[0].stopped)
        self.assertEqual(self.code("play", {"itemIds": [A]}),
                         "not_connected")

    def test_a_new_connect_stops_playback_first(self):
        self.connect_server()
        first = self.runtime.clients[0]
        self.addCleanup(self.runtime.pm.start_action_thread())
        self.runtime.pm._video = FakeVideo(A)
        del self.runtime.journal[:]
        self.assertTrue(self.connect_server()["ok"])
        names = [c[0] for c in self.runtime.journal]
        self.assertLess(names.index("stop"), names.index("make_client"))
        self.assertIsNot(self.bridge.client, first)

    def test_the_old_client_is_retired_before_the_stop(self):
        """So the stop's report, and anything holding the player's locks
        on a request to the old server, give up on it within moments
        (bridge_http) -- and the new server's reports never queue behind
        the old one's."""
        self.connect_server()
        first = self.runtime.clients[0]
        self.addCleanup(self.runtime.pm.start_action_thread())
        self.runtime.pm._video = FakeVideo(A)
        del self.runtime.journal[:]
        self.assertTrue(self.connect_server()["ok"])
        names = [c[0] for c in self.runtime.journal]
        self.assertTrue(first.retired)
        self.assertLess(names.index("retire_client"), names.index("stop"))
        self.assertFalse(self.runtime.clients[1].retired)

    def test_classify(self):
        from jellyfin_apiclient_python.exceptions import HTTPException

        for status, code in (("Unauthorized", "auth_failed"),
                             ("AccessRestricted", "auth_failed"),
                             ("ServerUnreachable", "server_unreachable"),
                             ("ReadTimeout", "server_unreachable"),
                             (500, "server_unreachable")):
            self.assertEqual(bridge_mod.classify_exception(
                HTTPException(status, None)), code)
        self.assertEqual(bridge_mod.classify_exception(
            ConnectionRefusedError()), "server_unreachable")
        self.assertEqual(bridge_mod.classify_exception(KeyError("x")),
                         "internal")


def _sub_pos_sets(player):
    return [c[2] for c in player.commands if c[:2] == ("set", "sub-pos")]


class SetControlsTest(BridgeCase):
    """fluentfin-mpv: ``set_controls`` (BRIDGE.md section 4): FluentFin's
    own controls over the video hide mpv's OSC and lift the subtitles while
    their bar shows; the logic is `bridge_controls` (test_bridge_controls)."""

    def osc_calls(self):
        return [c[1] for c in self.runtime.journal if c[0] == "enable_osc"]

    def test_needs_connect_and_good_args(self):
        self.assertEqual(self.code("set_controls", {"controls": "ui"}),
                         "not_connected")
        self.connect_server()
        for args in (None, {}, {"controls": "fluentfin"},
                     {"controls": "ui", "subtitleInset": 0.9},
                     {"controls": "ui", "subtitleInset": "0.1"}):
            with self.subTest(args=args):
                self.assertEqual(self.code("set_controls", args), "bad_args")
        self.assertEqual(self.osc_calls(), [])

    def test_ui_then_mpv(self):
        self.connect_server()
        player = self.runtime.pm._player
        # With nothing playing: it holds for whatever plays next.
        self.assertTrue(self.send("set_controls", {
            "controls": "ui", "subtitleInset": 0.18})["ok"])
        self.assertTrue(self.send("set_controls", {
            "controls": "ui", "subtitleInset": 0})["ok"])
        self.assertTrue(self.send("set_controls", {"controls": "mpv"})["ok"])
        self.assertEqual(self.osc_calls(), [False, False, True])
        self.assertEqual(_sub_pos_sets(player), ["82", "100"])

    def test_waits_for_a_start_holding_the_player(self):
        self.connect_server()
        pm = self.runtime.pm
        held, release = threading.Event(), threading.Event()

        def start():
            with pm._lock:          # a playback start, across its load
                held.set()
                release.wait(5)

        holder = threading.Thread(target=start, daemon=True)
        holder.start()
        held.wait(5)
        self.assertTrue(self.send("set_controls", {
            "controls": "ui", "subtitleInset": 0.1})["ok"],
            "answered at once")
        self.assertEqual(self.osc_calls(), [])
        release.set()
        holder.join(5)
        pm.run_deferred()
        self.assertEqual(self.osc_calls(), [False])
        self.assertEqual(_sub_pos_sets(pm._player), ["90"])

    def test_every_item_start_lifts_the_subtitles_again(self):
        # The shim writes its own sub-pos at every item start
        # (update_subtitle_visuals, modelled in FakePM.play). The lift must
        # land after it, item after item, and on a restarted mpv too.
        self.connect_server()
        pm = self.runtime.pm
        self.send("set_controls", {"controls": "ui", "subtitleInset": 0.2})
        for round_, video in enumerate((FakeVideo(A), FakeVideo(B),
                                        FakeVideo(A))):
            pm.play(video)
            self.assertEqual(pm._player.sub_pos, 80.0,
                             "item %d: above the bar after the start" % round_)
        pm._player = FakeHandle()                 # a new mpv
        pm.play(FakeVideo(B))
        self.assertEqual(pm._player.sub_pos, 80.0, "a new mpv: lifted too")
        self.assertEqual(self.osc_calls(), [False] * 5,
                         "the OSC hidden at the command and at every start")
        # The bar hidden: an item start leaves the shim's own value.
        self.send("set_controls", {"controls": "ui", "subtitleInset": 0})
        pm.play(FakeVideo(A))
        self.assertEqual(pm._player.sub_pos, 100.0)
        # With mpv's own controls an item start does nothing extra.
        self.send("set_controls", {"controls": "mpv"})
        del self.runtime.journal[:]
        pm.play(FakeVideo(B))
        self.assertEqual(self.osc_calls(), [])
        self.assertEqual(pm._player.sub_pos, 100.0)

    def test_a_lost_mpv_is_handed_to_the_shim(self):
        self.connect_server()
        pm = self.runtime.pm

        def gone(*args):
            raise BrokenPipeError("mpv went away")

        pm._player.command = gone
        self.assertTrue(self.send("set_controls", {
            "controls": "ui", "subtitleInset": 0.1})["ok"])
        self.assertIn(("disconnect",), self.runtime.journal)

    def test_a_dev_backend_with_only_its_window(self):
        self.bridge.dev = True
        self.assertTrue(self.connect()["ok"])
        self.assertTrue(self.send("set_controls", {"controls": "ui"})["ok"])
        self.assertEqual(self.osc_calls(), [False])


class SegmentsTest(BridgeCase):
    """fluentfin-mpv: media segments over the bridge (BRIDGE.md sections 4
    and 5): sampled from the shim's own list, the ``skip_segment``
    command, and the offer handed to mpv's bar."""

    def setUp(self):
        super().setUp()
        from jellyfin_mpv_shim.media import Intro

        self.Intro = Intro

    def playing(self, position=None):
        self.connect_server()
        pm = self.runtime.pm
        video = FakeVideo(A, parent=FakeQueue([A, B]))
        video.intros = [self.Intro("Intro", 10.0, 70.0),
                        self.Intro("Outro", 100.0, 120.0),
                        self.Intro("Commercial", 30.0, 40.0)]
        pm.play(video)
        pm._player.playback_time = position
        return pm, video

    def tick(self):
        obs = self.bridge._observe()
        events = self.bridge.tracker.update(obs)
        self.bridge._push_offer()
        return events

    def skip_texts(self, pm):
        return [c[2] for c in pm._player.commands
                if c[:2] == ("set", "user-data/fluentfin/skip")]

    def test_sampled_with_what_each_type_is_set_to(self):
        from jellyfin_mpv_shim.conf import settings

        pm, video = self.playing(20.0)
        with mock.patch.multiple(settings, segment_intro="ask",
                                 segment_outro="always",
                                 segment_commercial="off", auto_play=True):
            events = self.tick()
        data, = [d for n, d in events if n == "segments"]
        self.assertEqual([(s["type"], s["action"]) for s in data["segments"]],
                         [("Intro", "ask"), ("Outro", "always")])
        self.assertEqual(data["current"], 0)
        following, = [d for n, d in events if n == "next"]
        self.assertEqual(following, {"itemId": A, "nextItemId": B,
                                     "autoPlay": True})

    def test_skip_segment(self):
        self.assertEqual(self.code("skip_segment"), "not_connected")
        self.connect_server()
        self.assertEqual(self.code("skip_segment"), "no_playback")
        pm, video = self.playing(5.0)
        self.tick()
        self.assertEqual(self.send("skip_segment")["result"],
                         {"skipped": False}, "nothing on offer yet")
        self.assertNotIn(("skip_intro",), self.runtime.journal)
        pm._player.playback_time = 12.0
        self.tick()
        answer = self.send("skip_segment")
        self.assertEqual(answer["result"], {"skipped": True})
        # On the action thread: a seek to the offered segment's end.
        self.assertIn(("put_task",), self.runtime.journal)
        pm.run_deferred()
        self.assertIn(("seek", 70.0, "absolute"), pm._player.commands)
        self.assertEqual(pm._player.playback_time, 70.0)
        self.assertTrue(video.intros[0].has_triggered)
        self.assertIn(("timeline_handle",), self.runtime.journal)
        events = self.tick()
        data, = [d for n, d in events if n == "segments"]
        self.assertIsNone(data["current"], "the skip ended the offer")
        self.assertEqual(self.send("skip_segment")["result"],
                         {"skipped": False})

    def test_the_segment_skipped_is_the_one_offered(self):
        """Reviewer: `skip_intro` goes to the end of the FIRST segment the
        position is in, end included. A recap ending where the intro
        begins (0-90, 90-120) at 90, or an always-skipped recap overlapping
        it (0-100, 90-120) at 95: the offer is the intro, and the seek went
        to the recap's end."""
        from jellyfin_mpv_shim.conf import settings

        for recap_end, position in ((90.0, 90.0), (100.0, 95.0)):
            with self.subTest(recap_end=recap_end), mock.patch.multiple(
                    settings, segment_recap="always", segment_intro="ask"):
                self.setUp()
                pm, video = self.playing(position)
                video.intros = [self.Intro("Recap", 0.0, recap_end),
                                self.Intro("Intro", 90.0, 120.0)]
                self.tick()
                self.assertEqual(self.bridge.tracker.offer()["type"],
                                 "Intro")
                self.assertEqual(self.send("skip_segment")["result"],
                                 {"skipped": True})
                pm.run_deferred()
                seeks = [c for c in pm._player.commands if c[0] == "seek"]
                self.assertEqual(seeks, [("seek", 120.0, "absolute")])
                self.assertEqual([s.has_triggered for s in video.intros],
                                 [False, True])

    def test_a_position_that_left_the_segment_skips_nothing(self):
        pm, video = self.playing(15.0)
        self.tick()
        # Seen in the segment; mpv has moved past it before the action
        # thread gets there: no seek back.
        self.assertEqual(self.send("skip_segment")["result"],
                         {"skipped": True})
        pm._player.playback_time = 75.0
        pm.run_deferred()
        self.assertEqual([c for c in pm._player.commands if c[0] == "seek"],
                         [])
        # Sampled outside it: nothing on offer, nothing accepted.
        self.tick()
        self.assertEqual(self.send("skip_segment")["result"],
                         {"skipped": False})

    def test_the_s_key_skips_the_offered_segment_too(self):
        from jellyfin_mpv_shim import fluentfin_osc

        self.addCleanup(setattr, fluentfin_osc, "skip_handler", None)
        self.addCleanup(fluentfin_osc.configure, False)
        pm, video = self.playing(15.0)
        fluentfin_osc.configure(True)
        self.tick()
        self.assertTrue(fluentfin_osc.on_message(pm, ["fluentfin-skip"]))
        pm.run_deferred()
        self.assertIn(("seek", 70.0, "absolute"), pm._player.commands)
        self.assertNotIn(("skip_intro",), self.runtime.journal)

    def test_the_bar_shows_the_offer_only_with_mpvs_controls(self):
        pm, video = self.playing(5.0)
        for _ in range(3):
            self.tick()
        self.assertEqual(self.skip_texts(pm), [""], "written once")
        pm._player.playback_time = 15.0
        for _ in range(3):
            self.tick()
        self.assertEqual(self.skip_texts(pm), ["", "Skip Intro"])
        # FluentFin's controls: they offer it, mpv's bar does not.
        self.send("set_controls", {"controls": "ui"})
        self.tick()
        self.assertEqual(self.skip_texts(pm)[-1], "")
        self.send("set_controls", {"controls": "mpv"})
        self.tick()
        self.assertEqual(self.skip_texts(pm)[-1], "Skip Intro")
        # A new mpv is told again.
        pm._player = FakeHandle()
        pm._player.playback_time = 15.0
        self.tick()
        self.assertEqual(self.skip_texts(pm), ["Skip Intro"])
        pm._player.playback_time = 71.0
        self.tick()
        self.assertEqual(self.skip_texts(pm), ["Skip Intro", ""])

    def test_the_bar_knows_whether_there_is_a_next_item(self):
        pm, video = self.playing(5.0)           # A of [A, B]

        def flags():
            return [c[2] for c in pm._player.commands
                    if c[:2] == ("set", "user-data/fluentfin/next")]

        for _ in range(3):
            self.tick()
        self.assertEqual(flags(), ["1"], "written once")
        last = FakeVideo(B, parent=FakeQueue([A, B], seq=1))
        pm.play(last)
        for _ in range(3):
            self.tick()
        self.assertEqual(flags(), ["1", ""])

    def test_the_monitor_outlives_a_label_that_will_not_come(self):
        """`_push_offer` runs on the monitor thread, which every later
        event comes from: nothing in it may end that thread."""
        pm, video = self.playing(15.0)
        self.tick()
        with mock.patch.object(self.runtime, "segment_label",
                               side_effect=ImportError("no media")):
            self.bridge._offer_pushed = None
            self.bridge._push_offer()           # never raises
        self.start_threads()
        with mock.patch.object(self.runtime, "segment_label",
                               side_effect=ImportError("no media")):
            self.bridge._offer_pushed = None
            for _ in range(3):
                self.bridge.poke()
                time.sleep(0.2)
            self.assertTrue(self.thread("bridge-monitor").is_alive())

    def test_nothing_is_written_once_the_monitor_stops(self):
        pm, video = self.playing(15.0)
        self.bridge.tracker.update(self.bridge._observe())
        self.bridge._monitor_stop.set()
        self.bridge._push_offer()
        self.assertEqual(self.skip_texts(pm), [])

    def test_a_failing_write_is_tried_again(self):
        pm, video = self.playing(15.0)
        self.tick()

        def refuse(*args):
            raise BrokenPipeError("mpv went away")

        with mock.patch.object(pm._player, "command", refuse):
            self.bridge._push_offer()           # never raises
        pm._player.commands.clear()
        self.bridge._offer_pushed = None
        self.bridge._push_offer()
        self.assertEqual(self.skip_texts(pm), ["Skip Intro"])


class DevConnectTest(BridgeCase):
    dev = True

    def test_window_only_connect_is_enough_for_play_file(self):
        self.assertTrue(self.connect()["ok"])
        self.assertFalse(self.bridge.connected)
        self.assertEqual(self.code("play", {"itemIds": [A]}),
                         "not_connected")
        self.assertTrue(self.send("play_file", {
            "url": "av://lavfi:testsrc", "title": "t"})["ok"])
        self.run_plays()
        self.assertEqual(self.runtime.pm._player.played,
                         ["av://lavfi:testsrc"])
        self.assertTrue(self.send("pause")["ok"])
        self.assertIs(self.runtime.pm._player.pause, True)
        self.assertTrue(self.send("stop")["ok"])
        self.assertIn(("stop",), self.runtime.pm._player.commands)
        self.assertIsNone(self.bridge.dev_item)

    def test_play_file_before_any_window(self):
        self.assertEqual(self.code("play_file", {"url": "x"}),
                         "not_connected")

    def test_play_file_claims_a_deferred_surface(self):
        """The self-test's play is a first play too."""
        self.assertTrue(self.connect(PrewarmTest.EMBEDDED,
                                     prewarm=True)["ok"])
        self.assertEqual(self.runtime.deferred, [True])
        self.assertTrue(self.send("play_file", {"url": "av://x"})["ok"])
        self.bridge._surface_thread.join(5)
        self.assertEqual(self.runtime.pm._player.force_window_writes, [True])
        self.run_plays()
        self.assertEqual(self.runtime.pm._player.played, ["av://x"])


class PrewarmTest(BridgeCase):
    """``connect.prewarm`` (BRIDGE.md section 4): connected after sign-in,
    long before the first ``play``. mpv runs, but with an embedded window it
    has no video output -- no window of its own inside the owner's, no input
    queues attached across the processes, no GPU surface -- until the first
    play is accepted, which turns ``force-window`` on once, on a thread of
    its own, while the start's server requests run. The rest of ``connect``
    is the ordinary one."""

    EMBEDDED = {"mode": "embedded", "hwnd": 4242}

    def prewarm(self, window=None):
        return self.connect(window or self.EMBEDDED, prewarm=True, **SERVER)

    def writes(self):
        thread = self.bridge._surface_thread
        if thread is not None:
            thread.join(5)
            self.assertFalse(thread.is_alive())
        return self.runtime.pm._player.force_window_writes

    def test_mpv_starts_without_its_video_output(self):
        answer = self.prewarm()
        self.assertEqual(answer["result"], {"mpv": "mpv v0.41.0-test",
                                            "mpvBackend": "external"})
        self.assertEqual(self.runtime.deferred, [True])
        self.assertIs(window_embed.surface_deferred, True)
        self.assertIs(self.runtime.pm._player.force_window, False)
        # the ordinary connect otherwise: the client and its user check
        self.assertTrue(self.bridge.connected)
        self.assertEqual(self.runtime.journal[:3], [
            ("start_player", "embedded"),
            ("make_client", "https://jf.example"), ("verify_client",)])
        self.assertIsNone(self.bridge._surface_thread)
        self.assertEqual(self.writes(), [])

    def test_the_first_play_turns_it_on_once_alongside_its_start(self):
        """When the play is accepted, not after its start: the output is
        built while the start's server requests run (measured: 224 ms from
        PlayAsync to ``playing`` at 40 ms per request, against 283 ms when
        it was built as the file opened)."""
        self.prewarm()
        during = []
        original = self.runtime.start_playback

        def start_playback(client, user_id, request):
            during.append(list(self.runtime.pm._player.force_window_writes))
            return original(client, user_id, request)

        self.runtime.start_playback = start_playback
        for item in (A, B, C):
            self.assertTrue(self.send("play", {"itemIds": [item]})["ok"])
            self.assertEqual(self.writes(), [True],
                             "written for the first play only")
            self.run_plays()
        self.assertEqual(during, [[True], [True], [True]])
        self.assertIs(self.runtime.pm._player.force_window, True)
        self.assertIs(window_embed.surface_deferred, False)
        self.assertEqual([p[2]["itemIds"] for p in self.runtime.playbacks],
                         [[A], [B], [C]])

    def test_a_play_that_then_fails_or_is_dropped_still_has_it(self):
        """An idle, black output: what a backend not pre-warmed shows."""
        self.prewarm()
        self.runtime.fail_fetch = ConnectionError("item fetch")
        self.send("play", {"itemIds": [A]})
        self.send("stop")               # drops the queued start
        self.run_plays()
        self.assertEqual(self.runtime.playbacks, [])
        self.assertEqual(self.writes(), [True])

    def test_a_refused_play_claims_nothing(self):
        self.prewarm()
        self.assertEqual(self.code("play", {"itemIds": []}), "bad_args")
        self.assertIsNone(self.bridge._surface_thread)
        self.assertIs(window_embed.surface_deferred, True)
        self.assertEqual(self.writes(), [])

    def test_nothing_during_the_shutdown(self):
        self.prewarm()
        self.bridge._shutdown.set()
        self.assertIs(self.bridge._claim_surface(), False)
        self.assertIs(window_embed.surface_deferred, True)
        self.assertEqual(self.writes(), [])

    def test_without_prewarm_the_surface_is_there_from_connect(self):
        self.connect_server(self.EMBEDDED)
        self.assertEqual(self.runtime.deferred, [False])
        self.assertIs(self.runtime.pm._player.force_window, True)
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.assertIsNone(self.bridge._surface_thread)
        self.assertEqual(self.writes(), [])

    def test_only_the_connect_that_starts_mpv_reads_it(self):
        self.connect_server(self.EMBEDDED)
        self.assertTrue(self.prewarm()["ok"])
        self.assertTrue(self.send("connect", dict(
            SERVER, protocol=1, prewarm=True))["ok"])
        self.assertEqual(self.runtime.deferred, [False])
        self.assertIs(window_embed.surface_deferred, False)

    def test_standalone_ignores_it(self):
        self.prewarm({"mode": "standalone"})
        self.assertEqual(self.runtime.deferred, [False])
        self.assertIs(window_embed.surface_deferred, False)
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.assertIsNone(self.bridge._surface_thread)
        self.assertEqual(self.writes(), [])

    def test_attached_ignores_it(self):
        """fluentfin-mpv player controls: the surface is the UI's own mpv,
        which this backend never builds or defers; FluentFin sends the
        attached ``connect`` at the first play anyway."""
        self.addCleanup(window_embed.configure, None)
        self.assertTrue(self.prewarm(
            {"mode": "attached", "ipc": "fluentfin-mpv-1-0011223344556677"})
            ["ok"])
        self.assertEqual(self.runtime.deferred, [False])
        self.assertIs(window_embed.surface_deferred, False)
        self.assertIs(window_embed.attached, True)
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.assertIsNone(self.bridge._surface_thread)
        self.assertEqual(self.writes(), [])

    def test_not_a_bool_is_refused_before_mpv_starts(self):
        self.assertEqual(self.code("connect", dict(
            SERVER, protocol=1, window=self.EMBEDDED, prewarm="yes")),
            "bad_args")
        self.assertEqual(self.runtime.started, [])
        self.assertIsNone(self.bridge.window)

    def test_a_dead_mpv_gets_no_write_and_the_claim_stands(self):
        """A re-created mpv is built from options that now have the
        surface (`apply_to_options`)."""
        self.prewarm()
        self.runtime.pm._mpv_alive = False
        self.send("play", {"itemIds": [A]})
        self.assertEqual(self.writes(), [])
        self.assertIs(window_embed.surface_deferred, False)
        self.assertIs(window_embed.apply_to_options({})["force_window"], True)

    def test_a_write_that_raises_is_contained(self):
        self.prewarm()

        class Boom(Exception):
            pass

        def boom(value):
            raise Boom()

        player = self.runtime.pm._player
        with mock.patch.object(type(player), "force_window",
                               property(lambda p: False,
                                        lambda p, v: boom(v))):
            with self.assertLogs(bridge_mod.log, "ERROR"):
                self.send("play", {"itemIds": [A]})
                self.bridge._surface_thread.join(5)
        self.run_plays()
        self.assertTrue(self.send("ping")["ok"])
        self.assertEqual(len(self.runtime.playbacks), 1)


class PlayTest(BridgeCase):
    def test_play_is_accepted_then_started(self):
        self.connect_server()
        answer = self.send("play", {"itemIds": [A, B], "startIndex": 1,
                                    "startPositionTicks": 100,
                                    "audioStreamIndex": 2,
                                    "subtitleStreamIndex": -1})
        self.assertEqual(answer["result"], {})
        state = self.bridge.tracker.snapshot(self.bridge._observe(cheap=True))
        self.assertEqual((state["state"], state["itemId"], state["queue"]),
                         ("opening", B, {"itemIds": [A, B],
                                           "index": 1}))
        self.assertEqual(self.runtime.playbacks, [], "not on the reader")
        self.run_plays()
        client, user_id, request = self.runtime.playbacks[0]
        self.assertIs(client, self.runtime.clients[0])
        self.assertEqual(user_id, SERVER["user"]["id"])
        self.assertEqual((request["startPositionTicks"],
                          request["subtitleStreamIndex"]), (100, -1))
        self.assertIsNone(self.bridge.pending)

    def test_a_later_play_supersedes_a_queued_one(self):
        self.connect_server()
        self.send("play", {"itemIds": [A]})
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.assertEqual([r["itemIds"] for _c, _u, r in
                          self.runtime.playbacks], [[B]])

    def test_stop_drops_a_queued_play(self):
        self.connect_server()
        self.send("play", {"itemIds": [A]})
        self.send("stop")
        self.run_plays()
        self.assertEqual(self.runtime.playbacks, [])
        self.assertIsNone(self.bridge.pending)

    def test_play_cancels_a_start_in_flight(self):
        self.connect_server()
        self.runtime.pm._start_in_progress = True
        self.send("play", {"itemIds": [A]})
        self.assertIn(("cancel_load",), self.runtime.journal)

    def test_a_failed_start_is_an_error_event(self):
        self.connect_server()
        self.runtime.start_playback = mock.Mock(
            side_effect=ConnectionResetError("reset"))
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        events = self.bridge.tracker.update(self.bridge._observe(cheap=True))
        errors = [d for name, d in events if name == "error"]
        self.assertEqual([e["code"] for e in errors], ["server_unreachable"])
        self.assertEqual(self.bridge.tracker.last_state["state"], "error")
        self.assertEqual(self.bridge.tracker.last_state["itemId"], A)


class ItemIdFormatTest(BridgeCase):
    """BRIDGE.md: ``play`` takes ids with or without dashes, and every id
    the backend emits is in N format."""

    def test_dashed_ids_are_accepted_and_normalised(self):
        self.connect_server()
        self.assertTrue(self.send("play", {"itemIds": [DASHED, A]})["ok"])
        state = self.bridge.tracker.snapshot(self.bridge._observe(cheap=True))
        self.assertEqual(state["itemId"], DASHED_N)
        self.assertEqual(state["queue"]["itemIds"], [DASHED_N, A])
        self.run_plays()
        self.assertEqual(self.runtime.playbacks[0][2]["itemIds"],
                         [DASHED_N, A])

    def test_ids_that_are_not_ids_are_bad_args(self):
        self.connect_server()
        for ids in (["a"], ["%s-" % A[:31]], [A + "0"], [A.replace("a", "g")]):
            with self.subTest(ids=ids):
                self.assertEqual(self.code("play", {"itemIds": ids}),
                                 "bad_args")

    def test_everything_emitted_is_n_format(self):
        tracker = bridge_state.Tracker()
        queue = FakeQueue([DASHED, A.upper()])
        video = FakeVideo(DASHED, parent=queue)
        tracker.update(bridge_state.Observation())
        events = dict(tracker.update(bridge_state.Observation(
            video=video, position=1.0)))
        self.assertEqual(events["item"]["itemId"], DASHED_N)
        self.assertEqual(events["state"]["itemId"], DASHED_N)
        self.assertEqual(events["state"]["queue"]["itemIds"], [DASHED_N, A])
        events = dict(tracker.update(bridge_state.Observation()))
        self.assertEqual(events["ended"]["itemId"], DASHED_N)


class HooksTest(BridgeCase):
    """The observation-only wrappers on the player."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.pm = self.runtime.pm
        self.events = []

    def tick(self):
        events = self.bridge.tracker.update(self.bridge._observe())
        self.events.extend(events)
        return events

    def test_a_replacement_is_marked_and_reported_in_order(self):
        a, b = FakeVideo(A), FakeVideo(B)
        self.pm.play(a)
        self.tick()
        self.pm.play(b)
        events = self.tick()
        self.assertEqual([n for n, _d in events][:3],
                         ["ended", "item", "tracks"])
        self.assertEqual(events[0][1], {"itemId": A, "reason": "replaced"})
        self.assertEqual(events[1][1]["itemId"], B)

    def test_a_replacement_that_fails_to_load_ends_the_old_item_replaced(self):
        """Where the hook's mark is the only witness: the failed load stops
        the old item and nothing takes over, so the default would say
        "stopped". A was ended by the replacement attempt (BRIDGE.md
        section 5), and B's failure is the error beside it."""
        self.pm.play(FakeVideo(A))
        self.tick()
        self.pm.next_play = "fail_load"
        self.pm.play(FakeVideo(B))
        events = self.tick()
        self.assertEqual([d for n, d in events if n == "ended"],
                         [{"itemId": A, "reason": "replaced"}])
        self.assertEqual([d["code"] for n, d in events if n == "error"],
                         ["mpv_failed"])
        self.assertEqual(self.bridge.tracker.last_state["state"], "error")

    def test_a_start_that_goes_nowhere_is_an_error(self):
        self.pm.next_play = "fail"
        self.pm.play(FakeVideo(A))
        errors = [d for n, d in self.tick() if n == "error"]
        self.assertEqual([e["code"] for e in errors], ["mpv_failed"])

    def test_a_start_that_raises_is_reported_once(self):
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.pm.next_play = HTTPException("Unauthorized", None)
        with self.assertRaises(HTTPException) as caught:
            self.pm.play(FakeVideo(A))
        self.assertTrue(caught.exception._bridge_reported)
        errors = [d for n, d in self.tick() if n == "error"]
        self.assertEqual([e["code"] for e in errors], ["auth_failed"])

    def test_a_cancelled_start_is_not_an_error(self):
        video = FakeVideo(A)
        self.bridge._cancelled.add(video)
        self.pm.next_play = "fail"
        self.pm.play(video)
        self.assertEqual([n for n, _d in self.tick() if n == "error"], [])

    def test_a_superseded_start_never_reaches_the_player(self):
        self.bridge._local.job_gen = self.bridge._gen
        self.addCleanup(setattr, self.bridge._local, "job_gen", None)
        self.bridge._gen += 1
        self.pm.play(FakeVideo(A))
        self.assertNotIn(("play", A), self.runtime.journal)

    def test_a_job_generation_does_not_outlive_its_job(self):
        """The hook judges a start by the generation of the job running on
        its thread. Left behind after the job, it would drop the next start
        made on that thread outside a job as "superseded"."""
        self.send("play", {"itemIds": [A]})
        self.run_plays()                # a job ran on this thread
        self.bridge._gen += 1           # and something came after it
        self.pm.play(FakeVideo(B))
        self.assertIn(("play", B), self.runtime.journal)

    def test_off_the_play_worker_a_start_is_nobodys_job(self):
        """An auto-advance on the action thread: no job, never dropped."""
        self.bridge._gen += 5
        self.pm.play(FakeVideo(A))
        self.assertIn(("play", A), self.runtime.journal)

    def test_end_of_file_is_marked_by_the_finished_callback(self):
        a = FakeVideo(A)
        self.pm.play(a)
        self.tick()
        self.pm._play_epoch = 3
        self.pm.finished_callback(True, 3)
        self.pm._video = None
        ended = [d for n, d in self.tick() if n == "ended"]
        self.assertEqual(ended, [{"itemId": A, "reason": "eof"}])

    def test_a_finished_callback_racing_a_start_marks_nothing(self):
        """``stop_window:play`` ended A and then B "eof": mpv's abort for A,
        when B's load replaced it, queued a finished callback stamped with
        A's epoch, and the hook compared that epoch before the start holding
        the player's lock had bumped it. Under the lock, it sees the bump."""
        a, b = FakeVideo(A), FakeVideo(B)
        self.pm.play(a)
        self.tick()
        epoch = self.pm._play_epoch
        callback = threading.Thread(
            target=self.pm.finished_callback, args=(True, epoch),
            daemon=True)

        def load():
            callback.start()        # the action thread, during the load
            time.sleep(0.2)

        self.pm.on_load = load
        self.pm.play(b)             # not a `play` command: nothing reported
        callback.join(5)
        self.assertFalse(callback.is_alive())
        ended = [d for n, d in self.tick() if n == "ended"]
        self.assertEqual(ended, [{"itemId": A, "reason": "replaced"}])
        self.assertEqual(self.pm._video, b)

    def test_a_finished_callback_racing_a_restart_marks_nothing(self):
        """The same race against a restart (a track change on a transcode),
        which holds the player's lock across its load: mpv's end-file for
        the file the restart replaces queues a callback with the old epoch.
        Decided outside the lock, it saw that epoch and the item, marked it
        ``eof``, and the item later ended ``eof`` on a ``stop``. A ``play``
        replacing the item would have held the callback (the item it owns);
        a restart is the same item, so only the lock stands here."""
        a = FakeVideo(A)
        self.pm.play(a)
        self.tick()
        epoch = self.pm._play_epoch
        callback = threading.Thread(
            target=self.pm.finished_callback, args=(True, epoch),
            daemon=True)

        def load():
            callback.start()        # the action thread, during the load
            time.sleep(0.2)

        self.pm.on_load = load
        self.pm.play(a)             # the restart
        callback.join(5)
        self.assertFalse(callback.is_alive())
        self.assertIs(self.pm._video, a)
        self.send("stop")
        self.pm.run_deferred()
        ended = [d for n, d in self.tick() if n == "ended"]
        self.assertEqual(ended, [{"itemId": A, "reason": "stopped"}])

    def test_a_stale_finished_callback_marks_nothing(self):
        a = FakeVideo(A)
        self.pm.play(a)
        self.tick()
        self.pm._play_epoch = 4
        self.pm.finished_callback(True, 3)
        self.pm._video = None
        ended = [d for n, d in self.tick() if n == "ended"]
        self.assertEqual(ended, [{"itemId": A, "reason": "stopped"}])

    def test_an_interrupted_stream_ends_with_error(self):
        a = FakeVideo(A)
        self.pm.play(a)
        self.tick()
        self.pm.on_load_error({"detail": "server stopped sending",
                               "timed_out": False, "owns_window": False})
        self.pm._video = None
        events = self.tick()
        self.assertEqual([d for n, d in events if n == "ended"],
                         [{"itemId": A, "reason": "error"}])
        self.assertEqual([d["code"] for n, d in events if n == "error"],
                         ["mpv_failed"])

    def test_the_stop_command_marks_stopped(self):
        a = FakeVideo(A)
        self.pm.play(a)
        self.tick()
        self.send("stop")
        self.pm.run_deferred()          # the action thread runs the stop
        ended = [d for n, d in self.tick() if n == "ended"]
        self.assertEqual(ended, [{"itemId": A, "reason": "stopped"}])

    def test_fullscreen_requests_become_events(self):
        window_embed.on_fullscreen_request(True)
        self.assertIn({"event": "fullscreen_request",
                       "data": {"fullscreen": True}}, self.messages())

    def test_mpv_not_coming_back_is_fatal(self):
        self.pm.on_reopen_failed(RuntimeError("no mpv"))
        fatal = [m for m in self.messages() if m.get("event") == "error"]
        self.assertEqual(fatal[-1]["data"]["fatal"], True)
        self.assertEqual(self.bridge.wait(), 1)


class TrackerTest(unittest.TestCase):
    """`bridge_state.Tracker` over several steps (docs/testing.md
    section 6): the order of events, and that nothing walks."""

    def setUp(self):
        self.tracker = bridge_state.Tracker()
        self.now = 1000.0
        self.tracker.update(self.obs())

    def obs(self, **kw):
        kw.setdefault("connected", True)
        kw.setdefault("window", "embedded")
        kw.setdefault("now", self.now)
        return bridge_state.Observation(**kw)

    def step(self, seconds=0.5, **kw):
        self.now += seconds
        return self.tracker.update(self.obs(**kw))

    def names(self, events):
        return [n for n, _d in events]

    def test_a_queue_from_request_to_idle(self):
        queue = FakeQueue([A, B])
        a, b = FakeVideo(A, parent=queue), FakeVideo(B, parent=queue)
        events = self.step(pending={"itemIds": [A, B], "index": 0})
        self.assertEqual(self.names(events), ["state"])
        self.assertEqual((events[0][1]["state"], events[0][1]["itemId"]),
                         ("opening", A))
        events = self.step(opening=a, start_in_progress=True)
        self.assertEqual(self.names(events), ["state"])
        self.assertEqual((events[0][1]["state"], events[0][1]["itemId"]),
                         ("opening", A), "the same item, now resolved")
        events = self.step(video=a, position=0.0, duration=60.0,
                           paused=False)
        self.assertEqual(self.names(events),
                         ["item", "tracks", "segments", "next", "state",
                          "position"])
        snap = events[4][1]
        self.assertEqual((snap["state"], snap["itemId"], snap["mediaSourceId"],
                          snap["playSessionId"], snap["playMethod"],
                          snap["queue"]),
                         ("playing", A, "src-" + A,
                          a.playback_info["PlaySessionId"], "DirectPlay",
                          {"itemIds": [A, B], "index": 0}))
        positions = []
        for second in range(1, 6):
            events = self.step(video=a, position=float(second),
                               duration=60.0, paused=False)
            self.assertEqual(self.names(events), ["position"])
            positions.append(events[0][1]["positionTicks"])
        self.assertEqual(positions, sorted(set(positions)))
        self.tracker.mark_ended(a, "eof")
        queue.seq = 1
        events = self.step(video=a, opening=b, start_in_progress=True,
                           position=60.0, duration=60.0)
        self.assertEqual(self.names(events), ["state"])
        self.assertEqual(events[0][1]["itemId"], B)
        events = self.step(video=b, position=0.0, duration=30.0)
        self.assertEqual(self.names(events),
                         ["ended", "item", "tracks", "segments", "next",
                          "state", "position"])
        self.assertEqual(events[0][1], {"itemId": A, "reason": "eof"})
        events = self.step(video=None)
        self.assertEqual(self.names(events), ["ended", "state"])
        self.assertEqual(events[0][1], {"itemId": B, "reason": "stopped"})
        self.assertEqual(events[1][1]["state"], "idle")
        for _ in range(3):
            self.assertEqual(self.step(video=None), [], "idle stays quiet")

    def test_a_restart_is_not_a_new_item(self):
        a = FakeVideo(A)
        self.step(video=a, position=10.0)
        events = self.step(video=a, opening=a, start_in_progress=True)
        self.assertEqual(self.names(events), ["state"])
        self.assertEqual(events[0][1]["state"], "opening")
        a.media_source = {"Id": "src-" + A, "MediaStreams": [
            {"Index": 1, "Type": "Audio"}, {"Index": 2, "Type": "Audio"}]}
        events = self.step(video=a, position=10.0)
        self.assertEqual(self.names(events), ["tracks", "state", "position"])
        self.assertEqual(self.step(video=a, position=10.0), [])

    def test_a_restart_negotiating_shows_the_session_playing(self):
        """`Observation.restart_session`: the bridge's word that ``video``'s
        restart has not reached mpv; the snapshot shows that session, not
        the one the negotiation has written on the video."""
        video = FakeVideo(A)
        video.playback_info = {"PlaySessionId": "ps-new"}
        for override, shown in (((video, "ps-old"), "ps-old"),
                                ((FakeVideo(B), "ps-other"), "ps-new"),
                                (None, "ps-new")):
            with self.subTest(override=override):
                snap = bridge_state.Tracker().snapshot(bridge_state.Observation(
                    connected=True, video=video, opening=video,
                    start_in_progress=True, restart_session=override))
                self.assertEqual((snap["state"], snap["playSessionId"]),
                                 ("opening", shown))

    def test_duration_alone_does_not_repeat_the_state(self):
        a = FakeVideo(A)
        self.step(video=a, position=1.0, duration=2.0)
        for n in range(3):
            events = self.step(video=a, position=1.0, duration=3.0 + n)
            self.assertEqual(self.names(events), ["position"])

    def test_positions_are_rate_limited(self):
        a = FakeVideo(A)
        self.step(video=a, position=1.0)
        self.assertEqual(self.names(self.step(0.1, video=a, position=1.1)),
                         [])
        self.assertEqual(self.names(self.step(0.4, video=a, position=1.5)),
                         ["position"])

    def test_a_failed_replacement_leaves_the_old_item_playing(self):
        a = FakeVideo(A)
        self.step(video=a, position=1.0)
        self.tracker.report_error("server_unreachable", "PlaybackInfo",
                                  item_id=B)
        events = self.step(video=a, position=2.0)
        self.assertEqual(self.names(events), ["error", "position"])
        self.assertEqual(self.tracker.last_state["state"], "playing")
        events = self.step(video=None)
        self.assertEqual(events[-1][1]["state"], "idle",
                         "the error must not resurface when a ends")

    def test_a_failed_load_lands_in_error(self):
        a, b = FakeVideo(A), FakeVideo(B)
        self.step(video=a, position=1.0)
        self.tracker.mark_ended(a, "replaced")
        self.step(video=a, opening=b, start_in_progress=True)
        self.tracker.report_error("mpv_failed", "timed out", item_id=B)
        events = self.step(video=None, opening=b, start_in_progress=True)
        self.assertEqual(self.names(events), ["ended", "error"])
        self.assertEqual(events[0][1], {"itemId": A, "reason": "replaced"})
        events = self.step(video=None)
        self.assertEqual(events[-1][1]["state"], "error")
        self.assertEqual(events[-1][1]["itemId"], B)
        self.tracker.clear_error()
        self.assertEqual(self.step(video=None)[-1][1]["state"], "idle")

    def test_dev_playback(self):
        item = bridge_state.DevItem("av://lavfi:testsrc")
        events = self.step(dev=item)
        self.assertEqual(events[0][1]["state"], "opening")
        item.phase = "active"
        events = self.step(dev=item, position=0.5, paused=False)
        self.assertEqual(self.names(events), ["state", "position"],
                         "no item or tracks for a file")
        self.assertIsNone(events[0][1]["itemId"])
        self.tracker.mark_ended(item, "eof")
        events = self.step(dev=None)
        self.assertEqual(events[0], ("ended", {"itemId": None,
                                               "reason": "eof"}))

    def test_the_strongest_reason_wins(self):
        a = FakeVideo(A)
        self.step(video=a)
        for reason in ("replaced", "eof", "stopped"):
            self.tracker.mark_ended(a, reason)
        self.assertEqual(self.step(video=None)[0][1]["reason"], "eof")

    # -- fluentfin-mpv: segments and next (BRIDGE.md section 5) -------------

    SEGMENTS = [("Intro", 10.0, 70.0, "ask"), ("Outro", 100.0, 120.0,
                                                 "always")]

    def of(self, events, name):
        return [d for n, d in events if n == name]

    def test_segments_follow_the_position_and_say_each_change_once(self):
        queue = FakeQueue([A, B])
        a = FakeVideo(A, parent=queue)
        events = self.step(video=a, opening=a, start_in_progress=True,
                           position=40.0, segments=self.SEGMENTS)
        self.assertEqual(self.names(events)[:4],
                         ["item", "tracks", "segments", "next"])
        first, = self.of(events, "segments")
        self.assertEqual(first["itemId"], A)
        self.assertEqual([s["type"] for s in first["segments"]],
                         ["Intro", "Outro"])
        self.assertIsNone(first["current"],
                          "no offer from a position read while opening")
        self.assertIsNone(self.tracker.offer())
        currents = []
        for position in (0.0, 5.0, 10.0, 30.0, 60.0, 69.9, 70.0, 80.0,
                         101.0, 110.0, 130.0):
            events = self.step(video=a, position=position,
                               segments=self.SEGMENTS)
            for data in self.of(events, "segments"):
                currents.append((position, data["current"]))
        # Sent when it changed and only then; never for the always one.
        self.assertEqual(currents, [(10.0, 0), (70.0, None)])
        for _ in range(3):
            self.assertEqual(self.of(self.step(video=a, position=130.0,
                                               segments=self.SEGMENTS),
                                     "segments"), [], "nothing walks")

    def test_the_offer_is_the_last_one_sent(self):
        a = FakeVideo(A)
        self.step(video=a, position=20.0, segments=self.SEGMENTS)
        self.assertEqual(self.tracker.offer()["type"], "Intro")
        self.step(video=a, position=70.0, segments=self.SEGMENTS)
        self.assertIsNone(self.tracker.offer())
        self.step(video=a, position=20.0, segments=self.SEGMENTS)
        self.step(video=None)
        self.assertIsNone(self.tracker.offer(), "an ended item offers none")

    def test_a_restart_withholds_the_offer_until_it_plays(self):
        a = FakeVideo(A)
        self.step(video=a, position=20.0, segments=self.SEGMENTS)
        events = self.step(video=a, opening=a, start_in_progress=True,
                           position=20.0, segments=self.SEGMENTS)
        self.assertEqual(self.of(events, "segments"), [],
                         "a restart sends nothing of the item's again")
        events = self.step(video=a, position=20.0, segments=self.SEGMENTS)
        self.assertEqual(self.of(events, "segments"), [])
        self.assertEqual(self.tracker.offer()["type"], "Intro")

    def test_next_follows_the_queue_and_each_item_gets_its_own(self):
        queue = FakeQueue([A, B, C], seq=1)
        b = FakeVideo(B, parent=queue)
        events = self.step(video=b, position=1.0, auto_play=True)
        self.assertEqual(self.of(events, "next"), [
            {"itemId": B, "nextItemId": C, "previousItemId": A,
             "autoPlay": True}])
        for _ in range(3):
            self.assertEqual(self.of(self.step(video=b, position=2.0,
                                               auto_play=True), "next"), [])
        # The queue changes around it (widened backwards: previous now
        # prepends the season's start) -- said once.
        queue.queue.insert(0, {"Id": DASHED_N, "PlaylistItemId": "p9"})
        queue.seq = 2
        events = self.step(video=b, position=3.0, auto_play=True)
        self.assertEqual(self.of(events, "next"), [],
                         "the same neighbours: nothing new")
        queue.queue.pop(1)                  # A gone from before B
        queue.seq = 1
        events = self.step(video=b, position=4.0, auto_play=True)
        self.assertEqual(self.of(events, "next"), [
            {"itemId": B, "nextItemId": C, "previousItemId": DASHED_N,
             "autoPlay": True}])
        # Auto play turned off.
        events = self.step(video=b, position=5.0, auto_play=False)
        self.assertEqual(self.of(events, "next")[0]["autoPlay"], False)
        # The next item, with the same segments as this one: its own
        # events all the same.
        c = FakeVideo(C, parent=queue)
        queue.seq = 2
        events = self.step(video=c, position=0.0, auto_play=False)
        self.assertEqual(self.names(events)[:5],
                         ["ended", "item", "tracks", "segments", "next"])
        self.assertEqual(self.of(events, "next"), [
            {"itemId": C, "previousItemId": B, "autoPlay": False}])

    def test_a_file_gets_no_segments_or_next(self):
        item = bridge_state.DevItem("av://lavfi:testsrc")
        item.phase = "active"
        for _ in range(3):
            events = self.step(dev=item, position=20.0,
                               segments=self.SEGMENTS)
            self.assertFalse({"segments", "next"} & set(self.names(events)))

    def test_volume_mute_fullscreen_are_state(self):
        events = self.step(volume=40.4, muted=True, fullscreen=True)
        snap = events[0][1]
        self.assertEqual((snap["volume"], snap["muted"], snap["fullscreen"]),
                         (40, True, True))
        self.assertEqual(self.step(fullscreen=True), [],
                         "volume and mute remembered between samples")


class EndToEndShutdownTest(unittest.TestCase):
    """The real entry point, no mpv: stdin EOF is a clean exit 0 and the
    only thing on stdout is `hello`."""

    def run_bridge(self, stdin_data):
        config = _tmpdirs.tmpdir("jms-bridge-")
        code = ("import sys; sys.path.insert(0, %r); "
                "from jellyfin_mpv_shim.bridge import main; "
                "main(['--config-dir', %r])" % (ROOT, config))
        return subprocess.run([sys.executable, "-I", "-c", code],
                              input=stdin_data, capture_output=True,
                              timeout=120)

    def test_eof_exits_zero_after_hello(self):
        done = self.run_bridge(b"")
        self.assertEqual(done.returncode, 0, done.stderr[-2000:])
        lines = done.stdout.splitlines()
        self.assertEqual(len(lines), 1, done.stdout)
        hello = json.loads(lines[0])
        self.assertEqual(hello["event"], "hello")
        self.assertEqual(hello["data"]["protocol"], 1)
        self.assertEqual(hello["data"]["backend"], "jellyfin-mpv-shim")

    def test_shutdown_command_exits_zero(self):
        done = self.run_bridge(b'{"id":1,"cmd":"ping"}\n'
                               b'{"id":2,"cmd":"shutdown"}\n')
        self.assertEqual(done.returncode, 0, done.stderr[-2000:])
        msgs = [json.loads(line) for line in done.stdout.splitlines()]
        self.assertEqual(msgs[1:], [{"id": 1, "ok": True, "result": {}},
                                    {"id": 2, "ok": True, "result": {}}])


class PerfChoicesTest(BridgeCase):
    """The four bridge-mode choices from the backend performance survey
    (perf-backend.md in the repository root's research notes)."""

    def apply_headless(self):
        from jellyfin_mpv_shim.conf import settings

        for key, _value in bridge_mod.ShimRuntime.HEADLESS_OVERRIDES:
            self.addCleanup(setattr, settings, key, getattr(settings, key))
        on_disk = settings.dict()
        for key, value in bridge_mod.ShimRuntime.HEADLESS_OVERRIDES:
            bridge_mod.ShimRuntime._override(key, value)
        return settings, on_disk

    def test_idle_quit_and_update_checks_are_off_in_every_window_mode(self):
        """In the overrides every bridge gets, not only the embedded ones:
        a standalone bridge pays the same ~240 ms re-creation."""
        settings, on_disk = self.apply_headless()
        self.assertIs(settings.mpv_idle_quit, False)
        self.assertIs(settings.check_updates, False)
        embedded_keys = {k for k, _v in
                         bridge_mod.ShimRuntime.EMBEDDED_OVERRIDES}
        self.assertNotIn("mpv_idle_quit", embedded_keys,
                         "not left to the embedded mode alone")
        # ...and none of it reaches conf.json.
        self.assertEqual(settings.dict()["mpv_idle_quit"],
                         on_disk["mpv_idle_quit"])
        self.assertEqual(settings.dict()["check_updates"],
                         on_disk["check_updates"])

    def test_position_is_polled_not_observed(self):
        """An observer on playback-time is ~30 IPC messages a second; the
        monitor reads it once a sample instead."""
        reads = []

        class Counting(FakeHandle):
            def __getattribute__(self, name):
                if name == "playback_time":
                    reads.append(name)
                return object.__getattribute__(self, name)

            def observe_property(self, *args):
                raise AssertionError("the bridge observed %r" % (args,))

            bind_property_observer = observe_property

        self.connect_server()
        pm = self.runtime.pm
        pm._player = Counting()
        pm._video = FakeVideo(A)
        for _ in range(3):
            self.bridge.tracker.update(self.bridge._observe())
        self.assertEqual(len(reads), 3, "one read per sample")

    def test_connect_verifies_on_the_client_that_plays(self):
        self.connect_server()
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.assertIs(self.runtime.verified, self.runtime.playbacks[0][0])

    def test_the_check_is_one_bounded_get_of_the_user(self):
        from jellyfin_apiclient_python.exceptions import HTTPException

        calls = []
        outcome = [None]

        class Api:
            def _http(self, action, url, request):
                calls.append((action, url, request))
                if outcome[0] is not None:
                    raise outcome[0]
                return {"Id": SERVER["user"]["id"]}

        client = mock.Mock(jellyfin=Api())
        server = proto.validate_connect(dict(SERVER, protocol=1))["server"]
        runtime = bridge_mod.ShimRuntime("unused")
        runtime.verify_client(client, server)
        self.assertEqual(calls, [("GET", "Users/Me", {
            "params": {}, "timeout": bridge_mod.VERIFY_TIMEOUT,
            "retry": 0})])
        for status, code in (("Unauthorized", "auth_failed"),
                             ("ServerUnreachable", "server_unreachable"),
                             ("ReadTimeout", "server_unreachable")):
            outcome[0] = HTTPException(status, None)
            with self.subTest(status=status):
                with self.assertRaises(BridgeError) as caught:
                    runtime.verify_client(client, server)
                self.assertEqual(caught.exception.code, code)


class PlayerNamesTest(unittest.TestCase):
    """The stand-ins above model player methods by name; these are the real
    ones, read from the source (importing ``player`` builds mpv). A rename
    upstream fails here rather than leaving a fake answering for a method
    that no longer exists."""

    def test_the_player_methods_the_bridge_calls_exist(self):
        names = set()
        for module, classes in (("player.py", {"PlayerManager"}),
                                ("player_reporting.py", {"ReportingMixin"}),
                                ("player_window.py", {"WindowMixin"})):
            path = os.path.join(ROOT, "jellyfin_mpv_shim", module)
            with open(path, encoding="utf-8") as fh:
                tree = ast.parse(fh.read())
            for node in ast.walk(tree):
                if isinstance(node, ast.ClassDef) and node.name in classes:
                    names |= {n.name for n in node.body
                              if isinstance(n, ast.FunctionDef)}
        for name in ("_revoke_auth_header", "send_timeline_stopped",
                     "release_stream", "run_action", "cancel_load", "stop",
                     "play", "play_next", "play_prev", "terminate",
                     "set_streams", "set_embedded_fullscreen",
                     "pause_if_playing", "play_if_paused", "seek",
                     "_ensure_mpv", "put_task", "_unload_ended",
                     "finished_callback", "_abort_load",
                     "send_timeline_initial", "_notify_load_start",
                     "_apply_auth_headers", "get_timeline_options",
                     "update", "_queue_finished", "restart_playback",
                     "_handle_mpv_disconnect", "_report_stopped_offline",
                     "skip_intro", "timeline_handle"):
            with self.subTest(name=name):
                self.assertIn(name, names)

    def test_mpv_not_answering_is_lost_in_stop_and_swallowed_in_a_report(self):
        """`FakePM.mpv_hung` models the two places a shutdown meets an mpv
        that stopped answering (rr8): stop()'s ``playback_abort`` read,
        whose timeout is mpv lost (`_handle_mpv_disconnect`: the video
        dropped, its Stopped from bookkeeping); and `get_timeline_options`,
        which swallows it and leaves mpv for alive -- so a report made first
        waits a reply out and the stop then waits another. If either moves,
        so must the fake."""
        functions, _source = self._functions("player.py", "PlayerManager")

        def handlers(node):
            """The ``except`` clauses naming `_mpv_errors`, with the names
            each one calls."""
            out = []
            for handler in (n for n in ast.walk(node)
                            if isinstance(n, ast.ExceptHandler)):
                if handler.type is not None and "_mpv_errors" in ast.unparse(
                        handler.type):
                    out.append({c.func.attr for c in ast.walk(handler)
                                if isinstance(c, ast.Call)
                                and isinstance(c.func, ast.Attribute)})
            return out

        stop = functions["stop"]
        abort = [n for n in ast.walk(stop) if isinstance(n, ast.Try)
                 and "playback_abort" in ast.unparse(n.body[0])]
        self.assertEqual(len(abort), 1, "stop() reads playback_abort once")
        self.assertEqual(handlers(abort[0]), [{"_handle_mpv_disconnect"}])
        lost = ast.unparse(functions["_handle_mpv_disconnect"])
        self.assertIn("self._end_playback()", lost)
        self.assertIn("self._report_stopped_offline", lost)
        reporting, _ = self._functions("player_reporting.py",
                                       "ReportingMixin")
        swallowed = handlers(reporting["get_timeline_options"])
        self.assertTrue(swallowed, "get_timeline_options catches nothing")
        for calls in swallowed:
            self.assertNotIn("_handle_mpv_disconnect", calls)

    def test_the_poll_rescue_is_gated_on_the_progress_reports(self):
        """`FakePM.update` models `update`'s end-of-file poll rescue, which
        queues the finished callback again for an item at its end -- only
        while ``should_send_timeline`` is on. A held end turns that off so
        the rescue does not re-queue it on every pass (rr7 item 1); if the
        gate moves, so must the fake and the hold."""
        functions, _source = self._functions("player.py", "PlayerManager")
        update = functions["update"]
        parents = {child: node for node in ast.walk(update)
                   for child in ast.iter_child_nodes(node)}
        calls = [n for n in ast.walk(update) if isinstance(n, ast.Call)
                 and isinstance(n.func, ast.Attribute)
                 and n.func.attr == "_queue_finished"]
        self.assertTrue(calls, "update no longer queues the end itself")
        for call in calls:
            node, gated = call, False
            while node in parents:
                node = parents[node]
                if isinstance(node, ast.If) and any(
                        isinstance(n, ast.Attribute)
                        and n.attr == "should_send_timeline"
                        for n in ast.walk(node.test)):
                    gated = True
            self.assertTrue(gated, "a rescue not gated on the reports")

    def test_a_quality_change_writes_the_video_before_it_restarts(self):
        """`FakeVideo.set_trs_override` stands in for `media.Video`'s: the
        override the next negotiation reads, written by the quality change
        before it queues the restart (rr7 item 3)."""
        functions, _source = self._functions("media.py", "Video")
        writes = [n for n in ast.walk(functions["set_trs_override"])
                  if isinstance(n, ast.Attribute) and n.attr == "trs_ovr"
                  and isinstance(n.ctx, ast.Store)]
        self.assertTrue(writes)
        menu, _ = self._functions("menu.py", "OSDMenu")
        handle = ast.unparse(menu["change_transcode_quality_handle"])
        self.assertLess(handle.index("set_trs_override"),
                        handle.index("restart_playback"))

    @staticmethod
    def _functions(module, cls):
        path = os.path.join(ROOT, "jellyfin_mpv_shim", module)
        with open(path, encoding="utf-8") as fh:
            source = fh.read()
        tree = ast.parse(source)
        node = next(n for n in tree.body if isinstance(n, ast.ClassDef)
                    and n.name == cls)
        return {n.name: n for n in node.body
                if isinstance(n, ast.FunctionDef)}, source

    def test_the_load_hooks_run_on_the_starts_own_thread(self):
        """`Bridge._on_load_start`/`_on_load_error` take a load for the
        start recorded on the calling thread: `_play_media` -- the only
        caller of `_notify_load_start`, and of `_notify_load_error` besides
        the finished callback (a playing item's stream) -- is called by
        play() alone, directly, so on play()'s thread."""
        functions, _source = self._functions("player.py", "PlayerManager")

        def callers(name):
            return sorted(f for f, node in functions.items()
                          for call in ast.walk(node)
                          if isinstance(call, ast.Attribute)
                          and call.attr == name and f != name)

        self.assertEqual(callers("_play_media"), ["play"])
        self.assertEqual(callers("_notify_load_start"), ["_play_media"])
        self.assertEqual(callers("_notify_load_error"),
                         ["_play_media", "finished_callback"])

    def test_a_negotiation_without_a_stream_shows_on_the_video(self):
        """`bridge._negotiated_stream` reads the video after a restart a
        stop cancelled: no media source when the PlaybackInfo had none
        (`get_playback_url` returns before choosing a url), and no
        ``play_method`` when nothing could be played -- `_get_url_from_source`
        clears it first and sets it only on the exits that return a url."""
        functions, source = self._functions("media.py", "Video")
        body = ast.get_source_segment(source, functions["get_playback_url"])
        no_source = body.index("if self.media_source is None:")
        self.assertLess(no_source, body.index("self._get_url_from_source()"))
        self.assertIn("return None", body[no_source:no_source + 1500])
        decide = functions["_get_url_from_source"]
        first = decide.body[0]
        self.assertEqual(ast.unparse(first), "self.play_method = None")
        for ret in (n for n in ast.walk(decide) if isinstance(n, ast.Return)):
            self.assertIsNotNone(ret.value, "a bare return keeps a method")

    def test_the_load_start_hook_comes_before_the_load(self):
        """`Bridge._on_load_start` takes ``on_load_start`` as "mpv is being
        handed this start's file, and every way out from here that does not
        take drops what mpv had": `_notify_load_start` calls the hook, and
        `_play_media` calls that before its cancel check, the load and the
        ``_abort_load`` of each way out."""
        path = os.path.join(ROOT, "jellyfin_mpv_shim", "player.py")
        with open(path, encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        found = {}
        for node in ast.walk(tree):
            if isinstance(node, ast.FunctionDef) and node.name in (
                    "_play_media", "_notify_load_start"):
                found[node.name] = ast.get_source_segment(
                    open(path, encoding="utf-8").read(), node)
        self.assertIn("self.on_load_start", found["_notify_load_start"])
        body = found["_play_media"]
        notify = body.index("self._notify_load_start(video)")
        self.assertLess(notify, body.index("if self._load_cancelled:"))
        # FluentFin MPV 1.2.0: the load carries the tracks (_load_url)
        self.assertLess(notify, body.index("self._load_url(self.url"))
        self.assertLess(notify, body.index("self._abort_load()"))


class PlayFileAuthHeaderTest(BridgeCase):
    """``http-header-fields`` is global and outlives the item it was set
    for (docs/auth-headers.md): a ``play_file`` after a server item went
    round `_apply_auth_headers` and sent that item's ``Authorization:
    MediaBrowser ... Token=`` to the file's host."""

    dev = True

    def test_play_file_after_a_server_play_sends_no_auth_header(self):
        self.connect_server()
        self.runtime.start_plays = True
        handle = self.runtime.pm._player
        for n, url in enumerate(("http://elsewhere.example/1.mkv",
                                 "http://elsewhere.example/2.mkv",
                                 "http://elsewhere.example/3.mkv")):
            self.send("play", {"itemIds": [A]})
            self.run_plays()
            self.assertEqual(handle.http_header_fields, [AUTH_HEADER],
                             "the server start installed it (control)")
            self.send("play_file", {"url": url})
            self.run_plays()
            self.assertEqual(handle.played[-1], url)
            self.assertEqual(handle.headers_at_play[-1], [],
                             "round %d: mpv still held the token" % n)


class ReplaceReportsTest(BridgeCase):
    """BRIDGE.md section 5: a ``play`` over playback reports Stopped for
    the outgoing item and releases its stream before the new one starts,
    as next/previous do -- once per item."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm

    def reports(self):
        return [e[1] for e in self.runtime.journal
                if e[0] == "report_stopped"]

    def play(self, item):
        self.assertTrue(self.send("play", {"itemIds": [item]})["ok"])
        self.run_plays()

    def assert_in_order(self, *wanted):
        journal = list(self.runtime.journal)
        at = -1
        for entry in wanted:
            self.assertIn(entry, journal[at + 1:],
                          "%r missing or out of order" % (entry,))
            at = journal.index(entry, at + 1)

    def test_each_item_is_reported_stopped_exactly_once(self):
        for item in (A, B, C):
            self.play(item)
        self.send("stop")
        self.pm.run_deferred()          # the action thread runs the stop
        self.assertEqual(self.reports(), [A, B, C])

    def test_the_report_and_the_release_come_before_the_new_start(self):
        self.play(A)
        self.play(B)
        self.assert_in_order(("report_stopped", A), ("release_stream", A),
                             ("play", B))

    def test_next_is_not_reported_twice(self):
        """play_next reports its own stop: the bridge's report is owed by a
        ``play`` command's start, not by every play()."""
        self.play(A)
        self.pm.queue_next = FakeVideo(B)
        self.send("next")
        self.run_plays()
        self.send("stop")
        self.pm.run_deferred()          # the action thread runs the stop
        self.assertEqual(self.reports(), [A, B])

    def test_an_item_ending_on_its_own_is_reported_and_released_once(self):
        """The finished callback reports the item and *then* unloads it,
        releasing its stream: the record of the report must not swallow
        the release (their records are separate)."""
        self.play(A)
        self.pm.finished_callback(True, self.pm._play_epoch)
        self.assertEqual(self.reports(), [A])
        self.assertEqual([e for e in self.runtime.journal
                          if e == ("release_stream", A)],
                         [("release_stream", A)])
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A])

    def test_an_item_fetch_that_fails_reports_nothing(self):
        """Before play(): the new item never resolved, and the old one is
        still playing -- and still reported to the server."""
        self.play(A)
        self.runtime.fail_fetch = ConnectionResetError("reset")
        self.play(B)
        self.assertEqual(self.reports(), [])
        self.assertEqual(self.pm._video.item_id, A)

    def test_a_failed_load_reports_the_old_item_once(self):
        """The old item was reported when the new one started; the failed
        load's stop() takes it down without a second report -- or, when mpv
        has already dropped the file and stop() returns early, the hook
        does. Either way the old item is gone, not left playing."""
        for early in (False, True):
            with self.subTest(stop_returns_early=early):
                del self.runtime.journal[:]
                self.pm.early_stop_after_abort = early
                self.play(A)
                self.pm.next_play = "fail_load"
                self.play(B)
                self.assertEqual(self.reports(), [A])
                self.assertEqual(
                    [e for e in self.runtime.journal
                     if e == ("release_stream", A)], [("release_stream", A)])
                self.assertIsNone(self.pm._video)
                self.send("stop")
                self.assertEqual(self.reports(), [A])

    def test_a_replacement_cancelled_while_opening_reports_once(self):
        """``ppp:stop``/``ppp:play``: A playing, ``play B`` reports A and
        loads B; a ``stop`` (or ``play C``) cancels B's load. mpv has
        dropped the file, the cancelled start's stop() returns early, and
        A -- held paused for B's load -- used to read "paused" until the
        finished callback reported it a second time."""
        for then, callback in (("stop", "first"), ("play", "first"),
                               ("stop", "never"), ("play", "never")):
            with self.subTest(then=then, callback=callback):
                del self.runtime.journal[:]
                self.pm.early_stop_after_abort = True
                self.play(A)
                self.bridge.tracker.update(self.bridge._observe())
                epoch = self.pm._play_epoch
                if callback == "first":
                    # mpv's abort for A, as B's load replaced it: queued,
                    # waiting on the lock, in before the hook is done.
                    self.pm.on_return = lambda: self.pm.finished_callback(
                        True, epoch)
                # "never": A has a next item, and mpv's abort queues no
                # finished callback for one.
                states = []
                poke = self.bridge.poke

                def look():
                    states.append(self.bridge.tracker.snapshot(
                        self.bridge._observe())["state"])
                    poke()

                self.bridge.poke = look
                self.addCleanup(setattr, self.bridge, "poke", poke)
                # While mpv loads B, with the player's lock held.
                if then == "stop":
                    self.pm.on_load = lambda: self.elsewhere("stop")
                else:
                    self.pm.on_load = lambda: self.elsewhere(
                        "play", {"itemIds": [C]})
                self.play(B)
                self.run_plays()            # C, queued during B's load
                self.bridge.poke = poke
                self.pm.run_deferred()      # the stop, deferred by the load
                self.assertEqual(self.reports().count(A), 1, self.reports())
                self.assertNotIn("paused", states)
                ended = [d for n, d in self.bridge.tracker.update(
                    self.bridge._observe()) if n == "ended"]
                if then == "stop":
                    self.assertIsNone(self.pm._video)
                    self.assertEqual(ended, [{"itemId": A,
                                              "reason": "stopped"}])
                else:
                    self.assertEqual(self.pm._video.item_id, C)
                    self.assertEqual(ended, [{"itemId": A,
                                              "reason": "replaced"}])
                self.send("stop")
                self.run_plays()
                self.bridge.tracker.update(self.bridge._observe())

    def test_an_item_a_failed_replacement_left_playing_can_still_end_eof(self):
        """A replacement that fails before reaching mpv (PlaybackInfo)
        leaves the old item playing, reported once. If it then plays to its
        end, that end is "eof" -- not a second report, and not "replaced"."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.play(A)
        self.bridge.tracker.update(self.bridge._observe())
        self.pm.next_play = HTTPException(500, None)
        self.play(B)
        self.assertEqual(self.pm._video.item_id, A)
        self.pm.finished_callback(True, self.pm._play_epoch)
        ended = [d for n, d in self.bridge.tracker.update(
            self.bridge._observe()) if n == "ended"]
        self.assertEqual(ended, [{"itemId": A, "reason": "eof"}])
        self.assertEqual(self.reports(), [A])

    def test_an_item_the_shutdown_leaves_to_its_stop_does_not_end_eof(self):
        """A replacement cancelled as the backend shuts down: the hook
        leaves the item it reported to the shutdown's own stop
        (`_end_reported`), so it is still current, still the outgoing item
        of a replacement, and no ``play`` holds its end any more. mpv's
        end-file for it then runs the finished callback: not its end of
        file -- it was replaced, or stopped -- and the monitor still writes
        events until the shutdown stops it."""
        self.play(A)
        self.bridge.tracker.update(self.bridge._observe())
        a = self.pm._video
        epoch = self.pm._play_epoch
        self.pm.early_stop_after_abort = True   # mpv dropped the file

        def shutting_down():
            self.bridge._shutdown.set()
            self.elsewhere("stop")              # cancels B's load

        self.pm.on_load = shutting_down
        self.play(B)
        self.assertIs(self.pm._video, a, "left to the shutdown's stop")
        self.pm.finished_callback(True, epoch)  # mpv's end-file for A
        ended = [d for n, d in self.bridge.tracker.update(
            self.bridge._observe()) if n == "ended"]
        self.assertEqual(len(ended), 1)
        self.assertNotEqual(ended[0]["reason"], "eof")

    def test_a_failed_item_fetch_leaves_no_report_owed_on_the_thread(self):
        """The ``play`` command's run() owes its start's report to the
        play() hook; an item fetch that fails never reaches play(), and a
        flag left set handed the report to the next start on that thread --
        a ``next``, which reports its own outgoing item: twice."""
        self.play(A)
        self.runtime.fail_fetch = ConnectionResetError("reset")
        self.play(B)
        self.runtime.fail_fetch = None
        # Now that the next's own report is recorded, a flag left set no
        # longer shows as a second report -- it shows as the next's start
        # running the replacement's report path against a dead `play`.
        self.assertIsNone(getattr(self.bridge._local, "replacing", None))
        self.pm.queue_next = FakeVideo(C)
        self.send("next")
        self.run_plays()
        self.assertEqual(self.reports(), [A])
        self.assertEqual(self.pm._video.item_id, C)

    def test_the_report_waits_for_the_player_lock(self):
        """A next holding the player's lock is about to report A and move
        to C itself. The replacement's report has to wait for it and then
        report what is playing by then -- C -- not A a second time."""
        self.play(A)
        holding, go = threading.Event(), threading.Event()

        def a_next_in_progress():
            with self.pm._lock:
                holding.set()
                go.wait(5)
                self.pm.send_timeline_stopped(True)     # play_next's
                self.pm._video = FakeVideo(C)

        holder = threading.Thread(target=a_next_in_progress, daemon=True)
        holder.start()
        self.assertTrue(holding.wait(5))
        self.send("play", {"itemIds": [B]})
        worker = threading.Thread(target=self.run_plays, daemon=True)
        worker.start()
        time.sleep(0.3)             # the worker is at the report by now
        go.set()
        holder.join(5)
        worker.join(5)
        self.assertFalse(worker.is_alive())
        self.assertEqual(self.reports(), [A, C])
        self.assertEqual(self.pm._video.item_id, B)


class SupersededWhileReportingTest(BridgeCase):
    """A ``stop``, ``play`` or ``connect`` while a ``play``'s start waits in
    `_report_outgoing` -- for the report lock, which the timeline thread
    holds across a progress report the server is slow to answer. The player
    has not flagged the start yet, so nothing could cancel it: the command
    was answered ok, the start went ahead, and after a switch it played and
    reported on the old server."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.bridge.tracker.update(self.bridge._observe())
        patcher = mock.patch.object(bridge_mod, "STOP_WAIT", 0.3)
        patcher.start()
        self.addCleanup(patcher.stop)
        # The timeline thread, inside a progress report.
        self.release = threading.Event()
        held = threading.Event()

        def progress_report():
            with self.pm._tl_lock:
                held.set()
                self.release.wait(10)

        threading.Thread(target=progress_report, daemon=True).start()
        self.assertTrue(held.wait(5))
        self.addCleanup(self.release.set)
        # The play worker, as a thread of its own.
        self.worker = threading.Thread(target=self.bridge._play_loop,
                                       daemon=True)
        self.bridge._play_thread = self.worker
        self.worker.start()
        self.addCleanup(self.bridge._play_q.put, None)

    def reports(self):
        return [e[1] for e in self.runtime.journal
                if e[0] == "report_stopped"]

    def start_b_and_wait_for_the_report(self):
        self.send("play", {"itemIds": [B]})
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if (self.bridge.opening is not None
                    and self.bridge.opening.item_id == B):
                break
            time.sleep(0.01)
        time.sleep(0.1)     # inside `_report_outgoing`, on the report lock
        self.assertNotIn(("play", B), self.runtime.journal)

    def finish(self):
        self.release.set()
        self.bridge._play_q.put(None)
        self.worker.join(5)
        self.assertFalse(self.worker.is_alive())

    def test_a_stop_then(self):
        self.start_b_and_wait_for_the_report()
        self.assertTrue(self.send("stop")["ok"])
        self.finish()
        self.assertIsNone(self.pm._video, "A ends with the start it was "
                          "reported for, not whenever the stop gets the lock")
        self.pm.run_deferred()
        self.assertNotIn(("play", B), self.runtime.journal)
        self.assertIsNone(self.pm._video)
        self.assertEqual(self.reports(), [A])
        ended = [d for n, d in self.bridge.tracker.update(
            self.bridge._observe()) if n == "ended"]
        self.assertEqual(ended, [{"itemId": A, "reason": "stopped"}])

    def test_a_play_then(self):
        self.start_b_and_wait_for_the_report()
        self.assertTrue(self.send("play", {"itemIds": [C]})["ok"])
        self.finish()
        self.pm.run_deferred()
        self.assertNotIn(("play", B), self.runtime.journal)
        self.assertEqual(self.pm._video.item_id, C)
        self.assertEqual(self.reports(), [A])

    def test_a_connect_then(self):
        old = self.bridge.client
        self.start_b_and_wait_for_the_report()
        self.assertTrue(self.connect_server()["ok"])
        self.assertIsNot(self.bridge.client, old)
        self.finish()
        self.assertIsNone(self.pm._video)
        self.pm.run_deferred()
        self.assertNotIn(("play", B), self.runtime.journal)
        self.assertIsNone(self.pm._video)
        self.assertEqual(self.reports(), [A])
        self.assertEqual([c for c, _u, r in self.runtime.playbacks
                          if r["itemIds"] == [B]], [old],
                         "B was resolved on the old client and never played")


class CommittedStartTest(BridgeCase):
    """The few bytecodes between the hook's last check and the player
    flagging the start: a stop landing there found nothing to cancel."""

    def test_a_stop_in_the_gap_still_cancels(self):
        self.connect_server()
        self.runtime.start_plays = True
        pm = self.runtime.pm
        stopped = threading.Event()

        def stop_now():
            # On the control thread, while the worker sits in the gap.
            def control():
                self.bridge.execute(proto.Request(99, "stop", {}))
                stopped.set()
            threading.Thread(target=control, daemon=True).start()
            time.sleep(0.2)

        pm.on_enter = stop_now
        pm.on_negotiate = lambda: time.sleep(0.2)     # PlaybackInfo
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.assertTrue(stopped.wait(5))
        self.assertIsNone(pm._video, "the stop was answered and ignored")

    def test_the_commit_ends_with_the_start(self):
        """However the start ends -- playing, going nowhere, raising, or
        failing to load -- the commit goes with it. Left behind, every later
        stop or play waited `COMMIT_WAIT` for a start long finished."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.connect_server()
        self.runtime.start_plays = True
        pm = self.runtime.pm
        for outcome in ("load", "fail", HTTPException(500, None),
                        "fail_load"):
            with self.subTest(outcome=outcome):
                pm.next_play = outcome
                self.send("play", {"itemIds": [A]})
                self.run_plays()
                self.assertIsNone(self.bridge._committed)
                started = time.monotonic()
                self.assertTrue(self.send("stop")["ok"])
                self.assertLess(time.monotonic() - started,
                                bridge_mod.COMMIT_WAIT * 0.8)
                pm.run_deferred()


class StopCommandTest(BridgeCase):
    """``stop_hung``: A playing, the timeline thread inside a progress
    report the server never answers, holding the report lock. The ``stop``
    ran inline whenever the player's lock was free, and ``stop()`` then
    waited on the report lock -- with the report's retries, minutes -- on
    the control worker: the ``stop`` and a ``connect`` to another server
    behind it were never answered, while ping, get_state and ``state:
    idle`` said all was well."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        patcher = mock.patch.object(bridge_mod, "STOP_WAIT", 0.3)
        patcher.start()
        self.addCleanup(patcher.stop)

    def reports(self):
        return [e[1] for e in self.runtime.journal
                if e[0] == "report_stopped"]

    def hang_a_progress_report(self, seconds=4.0):
        self.release = threading.Event()
        held = threading.Event()

        def progress_report():
            with self.pm._tl_lock:
                held.set()
                self.release.wait(seconds)

        threading.Thread(target=progress_report, daemon=True).start()
        self.assertTrue(held.wait(5))
        self.addCleanup(self.release.set)

    def answered_within(self, seconds, cmd, args=None):
        """``cmd`` on a thread of its own (the control worker); its answer,
        or None when it is not answered within ``seconds``."""
        self.next_id += 1
        req = proto.Request(self.next_id, cmd, args or {})
        worker = threading.Thread(target=self.bridge.execute, args=(req,),
                                  daemon=True)
        worker.start()
        worker.join(seconds)
        if worker.is_alive():
            return None
        return [m for m in self.messages() if m.get("id") == req.id][0]

    def test_a_stop_is_answered_while_a_progress_report_hangs(self):
        self.addCleanup(self.pm.start_action_thread())
        self.hang_a_progress_report()
        stop = self.answered_within(1.0, "stop")
        self.assertIsNotNone(stop, "the stop waited on the report lock")
        self.assertTrue(stop["ok"])
        switch = self.answered_within(2.0, "connect", dict(SERVER,
                                                          protocol=1))
        self.assertIsNotNone(switch, "the connect queued behind it")
        self.assertTrue(switch["ok"])
        self.assertIsNot(self.bridge.client, self.runtime.clients[0])
        # The report goes out once the server lets go of the lock.
        self.release.set()
        deadline = time.monotonic() + 5
        while not self.reports() and time.monotonic() < deadline:
            time.sleep(0.02)
        time.sleep(0.1)
        self.assertEqual(self.reports(), [A])
        self.assertIsNone(self.pm._video)

    def test_the_stop_reports_once_whenever_it_runs(self):
        """Answered before its report goes out: the report is still made,
        exactly once, whether the stop runs then or another stop came
        first."""
        self.send("stop")
        self.assertEqual(self.reports(), [], "deferred, not inline")
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A])
        self.assertIsNone(self.pm._video)

    def shim_shutdown(self, budget):
        """The real `ShimRuntime.shutdown` on this test's player (no mpv
        behind it). Returns (clean, seconds)."""
        player = types.ModuleType("jellyfin_mpv_shim.player")
        player.bound_ipc_replies = lambda seconds=5: None
        player.is_using_ext_mpv = True
        player._mark_own_close = lambda handle: None
        runtime = bridge_mod.ShimRuntime("unused")
        runtime.pm, runtime.player_module = self.pm, player
        with mock.patch.dict(sys.modules,
                             {"jellyfin_mpv_shim.player": player}):
            started = time.monotonic()
            clean = runtime.shutdown(time.monotonic() + budget)
        return clean, time.monotonic() - started

    def test_a_shutdown_before_the_stop_runs_reports_it_once(self):
        """The shutdown's own stop() reports it, under its deadline; the
        deferred stop then finds nothing."""
        self.send("stop")
        clean, _seconds = self.shim_shutdown(2.0)
        self.assertIs(clean, True)
        self.assertEqual(self.reports(), [A])
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A])

    def test_a_stop_stuck_on_the_report_lock_counts_against_the_deadline(self):
        """The deferred stop holds the player's lock while it waits on the
        report lock: the shutdown waits for it within its one budget, not
        beyond."""
        self.addCleanup(self.pm.start_action_thread())
        self.hang_a_progress_report(seconds=6.0)
        self.send("stop")
        time.sleep(0.2)             # the action thread is inside stop()
        clean, seconds = self.shim_shutdown(0.5)
        self.assertIs(clean, False)
        self.assertLess(seconds, 1.2)


class NextNotTakenTest(BridgeCase):
    """``next_window:*``, ``next_cancel:*``, and the extra Stopped of
    ``hung_switch:PlaybackInfo``: ``play_next`` reports X Stopped and then
    calls play(Y). If Y's start does not take -- dropped as superseded
    (a stop, play or connect while play_next waited on the report lock),
    or cancelled while it negotiated -- X stays loaded, and whatever ended
    it next reported it a second time."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.bridge.tracker.update(self.bridge._observe())
        self.pm.queue_next = FakeVideo(B)
        patcher = mock.patch.object(bridge_mod, "STOP_WAIT", 0.3)
        patcher.start()
        self.addCleanup(patcher.stop)

    def journal(self, kind):
        return [e[1] for e in self.runtime.journal if e[0] == kind]

    def then(self, what):
        if what == "stop":
            answer = self.elsewhere("stop")
        elif what == "play":
            answer = self.elsewhere("play", {"itemIds": [C]})
        else:
            answer = self.elsewhere("connect", dict(SERVER, protocol=1))
        self.assertTrue(answer["ok"])

    def finish(self, what):
        self.run_plays()                # a play C queued meanwhile
        self.pm.run_deferred()          # the stop: the command's, a switch's
        if what == "play":
            self.assertEqual(self.pm._video.item_id, C)
        else:
            self.assertIsNone(self.pm._video)
        self.assertEqual(self.journal("report_stopped").count(A), 1,
                         self.journal("report_stopped"))
        self.assertEqual(self.journal("release_stream").count(A), 1)

    def test_superseded_while_the_next_waits_on_the_report_lock(self):
        for what in ("stop", "play", "connect"):
            with self.subTest(then=what):
                self.start_window()
                self.send("next")
                time.sleep(0.3)     # play_next: the lock held, on _tl_lock
                self.then(what)
                self.release.set()
                self.bridge._play_q.put(None)
                self.worker.join(5)
                self.assertFalse(self.worker.is_alive())
                self.assertNotIn(("play", B), self.runtime.journal,
                                 "dropped by the hook as superseded")
                self.finish(what)
                self.end_window()

    def start_window(self):
        del self.runtime.journal[:]
        if self.pm._video is None or self.pm._video.item_id != A:
            self.send("play", {"itemIds": [A]})
            self.run_plays()
            del self.runtime.journal[:]
        self.pm.queue_next = FakeVideo(B)
        self.release = threading.Event()
        held = threading.Event()

        def progress_report():
            with self.pm._tl_lock:
                held.set()
                self.release.wait(10)

        threading.Thread(target=progress_report, daemon=True).start()
        self.assertTrue(held.wait(5))
        self.worker = threading.Thread(target=self.bridge._play_loop,
                                       daemon=True)
        self.worker.start()

    def end_window(self):
        self.release.set()
        self.send("stop")
        self.pm.run_deferred()

    def test_cancelled_while_the_next_negotiates(self):
        for what in ("stop", "play", "connect"):
            with self.subTest(then=what):
                del self.runtime.journal[:]
                if self.pm._video is None or self.pm._video.item_id != A:
                    self.send("play", {"itemIds": [A]})
                    self.run_plays()
                    del self.runtime.journal[:]
                self.pm.queue_next = FakeVideo(B)
                self.pm.on_negotiate = lambda what=what: self.then(what)
                self.send("next")
                self.run_plays()
                self.assertEqual(self.journal("report_stopped"), [A],
                                 "play_next reported A")
                self.finish(what)
                self.send("stop")
                self.pm.run_deferred()


class NextTargetNeverOpenedTest(BridgeCase):
    """rr5 item 1 (``next_load_error``, ``next_load_timeout_stop``,
    ``prev_load_error``, ``eof_advance_fail``): the target of a next, a
    previous or an auto-advance never opens. play_next (play_prev, the
    finished callback) reported the item before it Stopped and then called
    play(); a load that fails ends in the load's own stop(), which returns
    early once mpv has dropped the file (``playback_abort``) -- so that
    item stayed current: "paused" on a closed session, the error state
    dropped as "something is playing", and a ``stop`` doing nothing. A
    target failing before mpv left it playing on a closed session.
    BRIDGE.md section 5: ``state: "error"``; the item before it, already
    reported stopped, does not stay current."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm

    def journal(self, kind):
        return [e[1] for e in self.runtime.journal if e[0] == kind]

    def start_a(self):
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.bridge.tracker.update(self.bridge._observe())
        del self.runtime.journal[:]
        return self.pm._video

    @staticmethod
    def outcome(name):
        if name == "raise":
            from jellyfin_apiclient_python.exceptions import HTTPException
            return HTTPException(500, None)
        return name

    def move(self, how, name):
        """``how`` (next, previous, or the finished callback's advance, on
        the action thread) to B, whose start ends as ``name`` says: mpv
        failing the load, no URL, the negotiation raising, or cancelled by a
        ``stop`` during the load."""
        if name == "cancelled":
            self.pm.on_load = lambda: self.elsewhere("stop")
        else:
            self.pm.next_play = self.outcome(name)
        if how == "previous":
            self.pm.queue_prev = FakeVideo(B)
        else:
            self.pm.queue_next = FakeVideo(B)
        if how == "advance":
            try:
                self.pm.finished_callback(True, self.pm._play_epoch)
            except Exception:
                pass            # the action thread logs it
        else:
            self.assertIsNone(self.code(how))
            self.run_plays()

    def test_the_item_before_it_does_not_stay_current(self):
        for early in (True, False):
            for how in ("next", "previous", "advance"):
                for name in ("fail_load", "fail", "raise", "cancelled"):
                    with self.subTest(stop_returns_early=early, how=how,
                                      outcome=name):
                        self.pm.early_stop_after_abort = early
                        self.start_a()
                        self.move(how, name)
                        self.pm.run_deferred()  # a cancelling stop's stop
                        self.assertIsNone(self.pm._video, "A stayed current")
                        self.assertEqual(self.journal("report_stopped"), [A])
                        self.assertEqual(self.journal("release_stream"), [A])
                        events = self.bridge.tracker.update(
                            self.bridge._observe())
                        state = [d for n, d in events if n == "state"][-1]
                        ended = [d for n, d in events if n == "ended"]
                        reason = ("eof" if how == "advance" else
                                  "stopped" if name == "cancelled"
                                  else "replaced")
                        self.assertEqual(ended, [{"itemId": A,
                                                  "reason": reason}])
                        if name == "cancelled":
                            self.assertEqual(state["state"], "idle")
                        else:
                            self.assertEqual(
                                (state["state"], state["itemId"]),
                                ("error", B))
                        self.assertTrue(self.send("stop")["ok"])
                        self.pm.run_deferred()
                        self.assertEqual(self.bridge.tracker.snapshot(
                            self.bridge._observe())["state"], "idle")
                        self.assertEqual(self.journal("report_stopped"), [A])
                        self.assertEqual(self.journal("release_stream"), [A])

    def test_no_idle_between_the_failed_start_and_its_error(self):
        """A start failing before mpv was reported only after `opening` was
        cleared and its play session settled -- which pokes the monitor:
        with the item before it gone, a sample in between read "idle", and a
        UI leaves the player page on that."""
        for name in ("fail", "raise"):
            with self.subTest(outcome=name):
                self.start_a()
                states = []
                poke = self.bridge.poke

                def look():
                    states.append(self.bridge.tracker.snapshot(
                        self.bridge._observe())["state"])
                    poke()

                self.bridge.poke = look
                try:
                    self.move("next", name)
                finally:
                    self.bridge.poke = poke
                self.assertNotIn("idle", states)
                self.assertEqual(states[-1], "error")
                self.send("stop")
                self.pm.run_deferred()

    def test_one_that_opens_is_not_touched(self):
        """The control: a next that loads ends A as "replaced" through the
        player, and nothing here ends anything."""
        self.start_a()
        self.move("next", "load")
        self.assertEqual(self.pm._video.item_id, B)
        self.assertEqual(self.journal("report_stopped"), [A])
        self.assertNotIn(("unload", A), self.runtime.journal)

    def test_an_item_nobody_reported_is_left_alone(self):
        """Only the item its caller reported: a start off the paths the
        bridge drives that does not report what is playing first (the
        shim's retry of a failed start, from its own UI) and goes nowhere
        leaves that item playing, its Stopped still owed."""
        self.start_a()
        self.pm.next_play = "fail"
        self.pm.play(FakeVideo(B, client=self.bridge.client))
        self.assertEqual(self.pm._video.item_id, A)
        self.assertEqual(self.journal("report_stopped"), [])
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.journal("report_stopped"), [A])


class StaleErrorTest(BridgeCase):
    """``stale_error:connect`` and the error in ``hung_switch:item``: a
    ``play`` whose item fetch is in flight is superseded -- a stop, a newer
    play, a switch -- and the fetch fails afterwards (a switch's retirement
    of the old client makes it fail 2 s later). The error landed after the
    command that cleared it: an ``error`` event, and ``state: "error"``
    stuck after a successful switch."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.pm = self.runtime.pm
        patcher = mock.patch.object(bridge_mod, "STOP_WAIT", 0.3)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.gate = threading.Event()
        self.entered = threading.Event()
        self.addCleanup(self.gate.set)
        self.worker = threading.Thread(target=self.bridge._play_loop,
                                       daemon=True)
        self.worker.start()
        self.addCleanup(self.bridge._play_q.put, None)

    def fetch(self, outcome):
        """start_playback waits for the gate, then ends with ``outcome``:
        an exception raised, or a value returned."""
        def start_playback(client, user_id, request):
            self.entered.set()
            self.gate.wait(5)
            if isinstance(outcome, BaseException):
                raise outcome
            return outcome

        self.runtime.start_playback = start_playback

    def after(self, then):
        self.assertTrue(self.send("play", {"itemIds": [B]})["ok"])
        self.assertTrue(self.entered.wait(5))
        if then is not None:
            self.assertTrue(then()["ok"])
        self.gate.set()
        self.bridge._play_q.put(None)
        self.worker.join(5)
        self.assertFalse(self.worker.is_alive())
        events = self.bridge.tracker.update(self.bridge._observe())
        return ([d["code"] for n, d in events if n == "error"],
                self.bridge.tracker.last_state["state"])

    def unreachable(self):
        from jellyfin_apiclient_python.exceptions import HTTPException

        return HTTPException("ServerUnreachable", "gave up")

    def test_a_fetch_failing_after_a_stop(self):
        self.fetch(self.unreachable())
        self.assertEqual(self.after(lambda: self.send("stop")),
                         ([], "idle"))

    def test_a_fetch_failing_after_a_switch(self):
        self.fetch(self.unreachable())
        self.assertEqual(self.after(self.connect_server), ([], "idle"))

    def test_a_start_that_went_nowhere_after_a_stop(self):
        """``start_playback`` answering "nothing to play" once the job was
        superseded -- not ``internal``."""
        self.fetch(False)
        self.assertEqual(self.after(lambda: self.send("stop")),
                         ([], "idle"))

    def test_the_control_still_reports_it(self):
        """Not superseded: the failure is the UI's."""
        self.fetch(self.unreachable())
        self.assertEqual(self.after(None),
                         (["server_unreachable"], "error"))

    def test_a_queued_job_failing_after_a_switch(self):
        """The play worker's own handler: a ``next`` (or a track restart)
        raising after the switch retired its client."""
        def next_job():
            self.entered.set()
            self.gate.wait(5)
            raise self.unreachable()

        self.pm._video = FakeVideo(A)
        self.bridge._queue_play("next", next_job)
        self.assertTrue(self.entered.wait(5))
        self.assertTrue(self.connect_server()["ok"])
        self.gate.set()
        self.bridge._play_q.put(None)
        self.worker.join(5)
        self.pm.run_deferred()
        events = self.bridge.tracker.update(self.bridge._observe())
        self.assertEqual([n for n, _d in events if n == "error"], [])
        self.assertNotEqual(self.bridge.tracker.last_state["state"], "error")

    def test_a_start_failing_in_the_player_after_it_was_superseded(self):
        """The hook's own report, for a start the supersede could not
        cancel (not flagged yet): the negotiation then raised."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.bridge._play_q.put(None)
        self.worker.join(5)
        self.bridge._local.job_gen = self.bridge._gen
        self.addCleanup(setattr, self.bridge._local, "job_gen", None)

        def superseded():
            with self.bridge._state_lock:
                self.bridge._gen += 1   # a stop that found nothing to cancel

        self.pm.on_enter = superseded
        self.pm.next_play = HTTPException("ServerUnreachable", "gave up")
        with self.assertRaises(HTTPException):
            self.pm.play(FakeVideo(B))
        events = self.bridge.tracker.update(self.bridge._observe())
        self.assertEqual([n for n, _d in events if n == "error"], [])


class SessionLedgerTest(BridgeCase):
    """What "reported" is keyed on: the video object and its play session,
    held weakly. By PlaySessionId alone, never pruned, a later play that
    reused an id was never reported Stopped (``replay:fixed``: X, Y, X, stop
    -- 3 Playing, 2 Stopped), and the set only grew."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm

    def reports(self):
        return [e[1] for e in self.runtime.journal
                if e[0] == "report_stopped"]

    def test_an_item_played_again_with_the_same_session_id(self):
        pm = self.pm
        #: Kept alive, as a queue keeps its videos: the first A's record
        #: is still there when the second A ends.
        videos = []

        def start_playback(client, user_id, request):
            self.runtime.journal.append(("start_playback",
                                         request["itemIds"]))
            video = FakeVideo(request["itemIds"][0])
            video.playback_info = {"PlaySessionId": "ps-fixed-"
                                   + video.item_id}
            videos.append(video)
            pm.play(video)
            return True

        self.runtime.start_playback = start_playback
        for item in (A, B, A):
            self.assertTrue(self.send("play", {"itemIds": [item]})["ok"])
            self.run_plays()
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A, B, A])

    def test_a_restart_is_a_new_play_session(self):
        """A reported by a replacement that went nowhere before mpv (A
        plays on), then restarted (a track change on a transcode: the same
        video, and here the same session id): its end is owed a report
        again. (A next whose start goes nowhere no longer leaves A loaded:
        `NextTargetNeverOpenedTest`.)"""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.send("play", {"itemIds": [A]})
        self.run_plays()
        a = self.pm._video
        a.playback_info = {"PlaySessionId": "ps-fixed"}
        self.pm.next_play = HTTPException(500, None)
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.assertIs(self.pm._video, a)
        self.assertEqual(self.reports(), [A])
        self.pm.play(a)                     # restart_playback
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A, A])

    def test_it_holds_only_videos_still_alive(self):
        import gc

        for _ in range(30):
            self.send("play", {"itemIds": [A]})
            self.run_plays()
        self.send("stop")
        self.pm.run_deferred()
        self.bridge.tracker.update(self.bridge._observe())
        gc.collect()
        self.assertEqual(len(self.reports()), 30)
        self.assertLessEqual(len(self.bridge._reported), 2)
        self.assertLessEqual(len(self.bridge._released), 2)

    def test_a_report_built_from_bookkeeping_is_not_sent_twice(self):
        """mpv lost after a replacement reported A and failed before mpv
        (A played on): the shim reports A once more from bookkeeping
        (`_report_stopped_offline`), `_video` already cleared -- the session
        only in the options."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.send("play", {"itemIds": [A]})
        self.run_plays()
        a = self.pm._video
        self.pm.next_play = HTTPException(500, None)
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.assertIs(self.pm._video, a)
        self.assertEqual(self.reports(), [A])
        self.pm._video = None               # _handle_mpv_disconnect
        self.pm.send_timeline_stopped(
            options={"PlaySessionId": a.playback_info["PlaySessionId"],
                     "ItemId": A}, client=object())
        self.assertEqual(self.reports(), [A])

    def bookkeeping(self, video):
        """`_report_stopped_offline(video)`: options from bookkeeping, no
        ``stopped``, on a thread of its own after mpv went away."""
        self.pm.send_timeline_stopped(
            options={"PlaySessionId": video.playback_info["PlaySessionId"],
                     "ItemId": video.item_id}, client=video.client)

    def test_a_bookkeeping_report_is_matched_by_its_session_alone(self):
        """rr4 item 3: the report from bookkeeping arrives once a new item
        is loaded. Matched against ``pm._video`` it was sent for A a second
        time, and recorded under C -- overwriting C's own record, so C was
        reported twice as well."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.send("play", {"itemIds": [A]})
        self.run_plays()
        a = self.pm._video
        self.pm.next_play = HTTPException(500, None)
        self.send("play", {"itemIds": [B]})     # fails before mpv: A plays on
        self.run_plays()
        self.assertEqual(self.reports(), [A])
        self.pm._video = None                   # mpv lost
        self.send("play", {"itemIds": [C]})     # a new mpv, C loaded
        self.run_plays()
        c = self.pm._video
        self.pm.next_play = HTTPException(500, None)
        self.send("play", {"itemIds": [B]})     # reports C; B goes nowhere
        self.run_plays()
        self.assertIs(self.pm._video, c)
        self.assertEqual(self.reports(), [A, C])
        self.bookkeeping(a)                     # the offline thread, late
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A, C])

    def test_a_bookkeeping_report_is_recorded(self):
        """Sent once, and recorded by its session: a second one for the
        same session (mpv's shutdown and the disconnect each report the
        video they saw) is not sent."""
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        a = self.pm._video
        self.pm._video = None                   # mpv lost, nothing reported
        self.bookkeeping(a)
        self.bookkeeping(a)
        self.assertEqual(self.reports(), [A])

    def test_a_bookkeeping_record_does_not_outlive_a_new_session(self):
        """A server that reuses ids: A reported from bookkeeping, then
        played again under the same id -- that play's end is owed its
        report."""
        def start_playback(client, user_id, request):
            video = FakeVideo(request["itemIds"][0], client=client)
            video.playback_info = {"PlaySessionId": "ps-fixed"}
            self.pm.play(video)
            return True

        self.runtime.start_playback = start_playback
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.pm._video, a = None, self.pm._video    # mpv lost
        self.bookkeeping(a)
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A, A])

    def test_the_check_report_and_record_are_one_step(self):
        """G1: two reports of one session racing -- the offline-report
        thread, and another. Checked outside the report lock, both found
        nothing recorded while the first was still being sent, and both
        went out."""
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        a = self.pm._video
        self.pm._video = None
        inside, go = threading.Event(), threading.Event()
        self.addCleanup(go.set)

        def slow_send():
            inside.set()
            go.wait(5)

        self.pm.on_report = slow_send
        first = threading.Thread(target=self.bookkeeping, args=(a,),
                                 daemon=True)
        first.start()
        self.assertTrue(inside.wait(5))
        second = threading.Thread(target=self.bookkeeping, args=(a,),
                                  daemon=True)
        second.start()
        time.sleep(0.2)             # the second is at its check by now
        go.set()
        first.join(5)
        second.join(5)
        self.assertEqual(self.reports(), [A])

    def test_a_report_that_raised_is_not_recorded(self):
        """G8: recorded only once it went. Recorded first, a report that
        raised -- a next whose stop report failed, the item still loaded --
        left the item marked reported, and its stop sent nothing."""
        self.send("play", {"itemIds": [A]})
        self.run_plays()

        def fail():
            raise RuntimeError("the report could not be made")

        self.pm.on_report = fail
        with self.assertRaises(RuntimeError):
            self.pm.send_timeline_stopped(True)
        self.assertEqual(self.reports(), [])
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A])

    def test_a_restart_is_a_new_play_session_for_the_release_too(self):
        """G4: a replacement that failed before mpv reported A *and*
        released its stream; A played on, then restarted under the same
        session id (a server that reuses them). The new session's stream is
        owed a release, as its end is owed a report."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.send("play", {"itemIds": [A]})
        self.run_plays()
        a = self.pm._video
        a.playback_info = {"PlaySessionId": "ps-fixed"}
        self.pm.next_play = HTTPException(500, None)
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.assertIs(self.pm._video, a)
        self.pm.play(a)                         # restart_playback
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A, A])
        self.assertEqual(self.runtime.journal.count(("release_stream", A)),
                         2)

    def test_a_restart_that_went_nowhere_is_not_a_new_session(self):
        """G9: the epoch is read before the start. A was reported by a
        replacement that did not take (failing before mpv: A plays on); a
        restart that then fails before mpv starts no new session, so A's
        stop must not report it again."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.send("play", {"itemIds": [A]})
        self.run_plays()
        a = self.pm._video
        self.pm.next_play = HTTPException(500, None)
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.assertIs(self.pm._video, a)
        self.assertEqual(self.reports(), [A])
        self.pm.next_play = HTTPException(500, None)
        with self.assertRaises(HTTPException):
            self.pm.play(a)                     # restart_playback, failing
        self.assertIs(self.pm._video, a)
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.reports(), [A])


class StopThenTransportTest(BridgeCase):
    """rr4 item 1 (``stop_then:next``): the ``stop`` command is answered at
    once and its stop deferred to the action thread, so ``_video`` still
    named the item when a ``next`` behind it was read. The next started the
    following item -- Playing to the server -- and the deferred stop then
    ended it: Stopped. Every transport command for the stopped item now
    behaves as if nothing were playing."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.pm.queue_next = FakeVideo(B)
        self.pm.queue_prev = FakeVideo(C)

    def played(self):
        return [e[1] for e in self.runtime.journal if e[0] == "play"]

    def test_next_and_previous_after_a_stop_are_no_playback(self):
        for cmd in ("next", "previous"):
            with self.subTest(cmd=cmd):
                self.send("stop")
                self.assertEqual(self.code(cmd), "no_playback")
                self.run_plays()
                self.pm.run_deferred()
                self.assertNotIn(B, self.played())
                self.assertNotIn(C, self.played())
                self.assertIsNone(self.pm._video)
                self.send("play", {"itemIds": [A]})
                self.run_plays()
                del self.runtime.journal[:]
                self.pm.queue_next = FakeVideo(B)
                self.pm.queue_prev = FakeVideo(C)

    def test_whichever_runs_first_the_next_item_never_plays(self):
        """The reviewer's order: the next's job before the deferred stop
        (4 runs in 7 on the real bridge)."""
        self.send("stop")
        self.send("next")
        self.run_plays()
        self.pm.run_deferred()
        self.assertNotIn(B, self.played())
        reports = [e[1] for e in self.runtime.journal
                   if e[0] == "report_stopped"]
        self.assertEqual(reports, [A])

    def test_track_changes_after_a_stop_are_no_playback(self):
        self.send("stop")
        self.assertEqual(self.code("set_audio", {"index": 1}), "no_playback")
        self.assertEqual(self.code("set_subtitle", {"index": -1}),
                         "no_playback")
        self.run_plays()
        self.assertNotIn(("set_streams", 1, None), self.runtime.journal)
        self.assertNotIn(("set_streams", None, -1), self.runtime.journal)

    def test_pause_and_seek_after_a_stop_are_no_playback(self):
        self.send("stop")
        for cmd, args in (("pause", None), ("unpause", None),
                          ("toggle_pause", None),
                          ("seek", {"positionTicks": 10}),
                          ("seek_relative", {"seconds": 5})):
            with self.subTest(cmd=cmd):
                self.assertEqual(self.code(cmd, args), "no_playback")
        self.run_plays()
        self.pm.run_deferred()
        for call in ("pause_if_playing", "play_if_paused", "toggle_pause",
                     "seek"):
            self.assertNotIn(call, [e[0] for e in self.runtime.journal])

    def test_a_start_the_stop_cancelled_does_not_count_as_opening(self):
        """A pause behind a stop that cut a start short: nothing is opening
        any more, the start is only unwinding."""
        self.send("stop")
        self.pm.run_deferred()
        paused = []

        def while_loading():
            self.elsewhere("stop")
            paused.append(self.elsewhere("pause"))

        self.pm.on_load = while_loading
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.assertEqual(paused[0]["error"]["code"], "no_playback")

    def test_the_volume_after_a_stop_is_the_next_items(self):
        self.send("stop")
        self.assertTrue(self.send("set_volume", {"volume": 40})["ok"])
        self.assertEqual(self.runtime.idle_volume, 40)
        self.assertNotIn(("set_volume", 40), self.runtime.journal)

    def test_once_the_stop_has_run_the_next_item_can_be_moved_to(self):
        """No lasting mark: a new item after the stop takes transport."""
        self.send("stop")
        self.pm.run_deferred()
        self.send("play", {"itemIds": [C]})
        self.run_plays()
        self.pm.queue_next = FakeVideo(B)
        self.assertIsNone(self.code("next"))
        self.run_plays()
        self.assertEqual(self.pm._video.item_id, B)

    def test_the_stopped_item_played_again_takes_transport(self):
        """The mark goes with the stop: the same video started again (a
        retry, a restart) is not answered as stopped."""
        a = self.pm._video
        self.send("stop")
        self.pm.run_deferred()
        self.pm.play(a)
        self.assertIs(self.pm._video, a)
        self.assertIsNone(self.code("pause"))

    def test_a_newer_start_the_stop_spared_takes_transport(self):
        """``stop`` then ``play C``, C loaded before the deferred stop runs:
        the stop leaves C alone, and a ``next`` on C is C's."""
        self.send("stop")
        self.send("play", {"itemIds": [C]})
        self.run_plays()
        self.assertEqual(self.pm._video.item_id, C)
        self.pm.queue_next = FakeVideo(B)
        self.assertIsNone(self.code("next"))
        self.pm.run_deferred()
        self.run_plays()
        self.assertEqual(self.pm._video.item_id, B)


    def test_a_spared_stop_leaves_the_newer_stops_mark(self):
        """``stop``, ``play C``, ``stop``, then a ``next`` before the second
        stop has run. The first stop, run, spares C (a newer start) and
        clears its mark only if the mark is still its own: cleared
        unconditionally, it took the second stop's mark on C with it, the
        ``next`` read C as playing and started B -- Playing to the server --
        and the second stop then ended B."""
        self.send("stop")
        self.send("play", {"itemIds": [C]})
        self.run_plays()
        self.assertEqual(self.pm._video.item_id, C)
        self.send("stop")
        first = self.pm.deferred.pop(0)
        first(self.pm)                  # spares C: a newer start
        self.assertEqual(self.pm._video.item_id, C)
        self.pm.queue_next = FakeVideo(B)
        self.assertEqual(self.code("next"), "no_playback")
        self.run_plays()
        self.pm.run_deferred()          # the second stop
        self.assertNotIn(B, self.played())
        self.assertIsNone(self.pm._video)
        self.assertIsNone(self.bridge._stop_for)


class DeferredStopUnderTheLockTest(BridgeCase):
    """rr4 item 2: the deferred stop checked its target before taking the
    player's lock. A newer ``play C`` holds that lock through its load while
    ``_video`` is still the old item, so the check passed and the stop --
    waiting on the lock -- then ended C."""

    def test_the_stop_reaching_the_action_thread_during_the_load(self):
        self.connect_server()
        self.runtime.start_plays = True
        pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.send("stop")
        self.send("play", {"itemIds": [C]})
        tasks = list(pm.deferred)
        del pm.deferred[:]
        threads = []

        def on_load():                      # C loading, the lock held
            def action_thread():
                for task in tasks:
                    task(pm)
            thread = threading.Thread(target=action_thread, daemon=True)
            thread.start()
            threads.append(thread)
            time.sleep(0.3)                 # the stop task at the lock

        pm.on_load = on_load
        self.run_plays()
        threads[0].join(5)
        self.assertFalse(threads[0].is_alive())
        self.assertIsNotNone(pm._video, "the stop meant for A stopped C")
        self.assertEqual(pm._video.item_id, C)
        self.assertIsNone(self.bridge._stop_for)


class ReportedEventTest(BridgeCase):
    """BRIDGE.md section 5, ``reported``: one per play session, once the
    backend is done with its Stopped report -- ``sent`` on a 2xx, ``failed``
    on anything else or when abandoned, ``none`` for a session that never
    sent Playing. The client's ``session_stop`` is the real wrapper
    (`bridge_http.watch_stops`) over a fake API that answers as told."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm

    def play(self, item):
        self.assertTrue(self.send("play", {"itemIds": [item]})["ok"])
        self.run_plays()
        return self.pm._video

    def stop(self):
        self.assertTrue(self.send("stop")["ok"])
        self.pm.run_deferred()

    def reported(self, flush=True):
        if flush:
            self.bridge._flush_reported()
        return [(m["data"]["itemId"], m["data"]["playSessionId"],
                 m["data"]["outcome"]) for m in self.messages()
                if m.get("event") == "reported"]

    @staticmethod
    def session(video):
        return video.playback_info["PlaySessionId"]

    def test_queued_by_the_reporting_thread_written_by_the_monitor(self):
        """The report worker only hands the outcome on: a UI that stops
        reading must not stall report delivery (the writer's lock is held
        across a blocking pipe write)."""
        a = self.play(A)
        self.stop()                 # the report ran here, on this thread
        self.assertEqual(self.reported(flush=False), [])
        self.bridge._emit_tick(self.bridge._observe())
        self.assertEqual(self.reported(flush=False),
                         [(A, self.session(a), "sent")])

    def test_stop(self):
        a = self.play(A)
        self.stop()
        self.stop()
        self.assertEqual(self.reported(), [(A, self.session(a), "sent")])

    def test_replace(self):
        a = self.play(A)
        b = self.play(B)
        self.stop()
        self.assertEqual(self.reported(), [(A, self.session(a), "sent"),
                                           (B, self.session(b), "sent")])

    def test_next_and_previous(self):
        a = self.play(A)
        self.pm.queue_next = FakeVideo(B)
        self.send("next")
        self.run_plays()
        b = self.pm._video
        self.pm.queue_prev = FakeVideo(C)
        self.send("previous")
        self.run_plays()
        c = self.pm._video
        self.stop()
        self.assertEqual(self.reported(), [(A, self.session(a), "sent"),
                                           (B, self.session(b), "sent"),
                                           (C, self.session(c), "sent")])

    def test_end_of_file(self):
        a = self.play(A)
        self.pm.finished_callback(True, self.pm._play_epoch)
        self.stop()
        self.assertEqual(self.reported(), [(A, self.session(a), "sent")])

    def test_an_interrupted_stream(self):
        """The finished callback's interrupted branch: its own options --
        the position it measured -- for the item still playing."""
        a = self.play(A)
        self.pm.send_timeline_stopped(False, options={
            "ItemId": A, "PlaySessionId": self.session(a),
            "PositionTicks": 10})
        self.pm._unload_ended(a)
        self.stop()
        self.assertEqual(self.reported(), [(A, self.session(a), "sent")])

    def test_an_offline_report(self):
        """`_report_stopped_offline`, mpv gone and `_video` cleared."""
        a = self.play(A)
        self.pm._video = None
        self.pm.send_timeline_stopped(
            options={"ItemId": A, "PlaySessionId": self.session(a)},
            client=a.client)
        self.assertEqual(self.reported(), [(A, self.session(a), "sent")])

    def test_only_a_2xx_is_sent(self):
        """The apiclient returns None for a 500 and for a 204 alike."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        for answer, outcome in ((204, "sent"), (200, "sent"),
                                (500, "failed"),
                                (HTTPException("ReadTimeout", None), "failed"),
                                (HTTPException(401, None), "failed")):
            with self.subTest(answer=answer):
                self.bridge.client.jellyfin.answer = answer
                video = self.play(A)
                self.stop()
                self.assertEqual(self.reported()[-1],
                                 (A, self.session(video), outcome))

    def test_a_session_that_never_opened_is_none(self):
        """It negotiated a PlaySessionId (an ``opening`` state can show it)
        and never sent Playing; the item it was replacing is reported
        once."""
        def cancel():
            self.elsewhere("stop")

        for how in ("fail_load", "fail", "cancelled"):
            with self.subTest(how=how):
                del self.sink.data[:]
                a = self.play(A)
                videos = []
                if how == "cancelled":
                    self.pm.on_load = cancel
                else:
                    self.pm.next_play = how
                original_play = self.pm.play

                def capture(video, *args, **kwargs):
                    videos.append(video)
                    return original_play(video, *args, **kwargs)

                self.pm.play = capture
                try:
                    self.play(B)
                finally:
                    self.pm.play = original_play
                self.pm.run_deferred()
                self.assertEqual(self.reported(), [
                    (A, self.session(a), "sent"),
                    (B, self.session(videos[0]), "none")])
                self.stop()

    def test_a_start_that_did_not_take_is_none_even_if_another_did(self):
        """A start cancelled while it negotiated, as another start (an
        auto-advance on the action thread) went through: the epoch moved,
        but not for this video, whose session never opened."""
        client = self.bridge.client
        b, c = FakeVideo(B, client=client), FakeVideo(C, client=client)

        def meanwhile():
            self.pm.play(c)                 # loads: the epoch moves
            self.pm._load_cancelled = True  # and B is cancelled

        self.pm.on_negotiate = meanwhile
        self.pm.play(b)
        self.assertIs(self.pm._video, c)
        self.assertIn((B, self.session(b), "none"), self.reported())

    def test_a_start_stopped_as_it_returns_is_not_none(self):
        """Its Playing went out under the player's lock; a stop then took
        it before the hook was done with the start. That session is the
        stop's to close (``sent``), not "never opened"."""
        inside, go = threading.Event(), threading.Event()
        self.addCleanup(go.set)
        stopper = []

        def stop_as_it_returns():
            def slow_report():
                inside.set()
                go.wait(5)
            self.pm.on_report = slow_report
            thread = threading.Thread(target=self.pm.stop, daemon=True)
            thread.start()
            stopper.append(thread)
            self.assertTrue(inside.wait(5))     # `_video` cleared, reporting

        self.pm.on_return = stop_as_it_returns
        b = self.play(B)
        self.assertIsNone(b)
        video = [e for e in self.runtime.journal if e[0] == "play"]
        self.assertEqual(video[-1][1], B)
        go.set()
        stopper[0].join(5)
        reported = self.reported()
        self.assertEqual([(item, outcome) for item, _s, outcome in reported],
                         [(B, "sent")])

    def test_a_restart_that_failed_to_load_reports_its_new_session(self):
        """The failed load's stop() takes the video down under the session
        its restart negotiated: that session's Stopped is pending, so it is
        not "never opened"; the one it replaced is abandoned."""
        a = self.play(A)
        old = self.session(a)
        self.pm._reporter.hold = True
        self.pm.on_negotiate = lambda: setattr(
            a, "playback_info", {"PlaySessionId": "ps-restarted"})
        self.pm.next_play = "fail_load"
        self.pm.play(a)                     # restart_playback
        self.assertIsNone(self.pm._video)
        self.pm._reporter.run_jobs()
        self.assertEqual(self.reported(), [(A, old, "failed"),
                                           (A, "ps-restarted", "sent")])

    def test_a_restart_closes_the_session_it_replaced(self):
        """A track change on a transcode renegotiates on the same video,
        and upstream sends no Stopped for the session it leaves: that one
        is abandoned (``failed``) once the new one has opened."""
        a = self.play(A)
        old = self.session(a)
        self.pm.on_negotiate = lambda: setattr(
            a, "playback_info", {"PlaySessionId": "ps-restarted"})
        self.pm.play(a)                     # restart_playback
        self.assertEqual(self.reported(), [(A, old, "failed")])
        self.stop()
        self.assertEqual(self.reported(), [(A, old, "failed"),
                                           (A, "ps-restarted", "sent")])

    def test_a_switch(self):
        """The old client is retired: a healthy old server still takes the
        Stopped (``sent``); one the guard gives up on is ``failed``."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        patcher = mock.patch.object(bridge_mod, "STOP_WAIT", 0.5)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.addCleanup(self.pm.start_action_thread())
        for answer, outcome in ((204, "sent"),
                                (HTTPException("ServerUnreachable", None),
                                 "failed")):
            with self.subTest(outcome=outcome):
                del self.sink.data[:]
                a = self.play(A)
                a.client.jellyfin.answer = answer
                self.assertTrue(self.connect_server()["ok"])
                deadline = time.monotonic() + 5
                while not self.reported() and time.monotonic() < deadline:
                    time.sleep(0.02)    # the stop runs on the action thread
                self.assertEqual(self.reported(),
                                 [(A, self.session(a), outcome)])

    def test_play_file_never(self):
        self.dev = True
        self.bridge.dev = True
        self.send("play_file", {"url": "av://lavfi:testsrc"})
        self.run_plays()
        self.stop()
        self.bridge.tracker.update(self.bridge._observe())
        self.bridge.request_shutdown("test", 0)
        self.bridge.stop()
        self.assertEqual(self.reported(), [])

    def test_at_most_one_per_session(self):
        """The shutdown's sweep closed a report still on the worker; the
        worker finishing afterwards is not a second outcome."""
        a = self.play(A)
        self.pm._reporter.hold = True
        self.stop()                         # its Stopped queued, not run
        self.assertEqual(self.bridge.tracker.sweep_sessions(), 1)
        self.pm._reporter.run_jobs()        # delivered late: a 204
        self.assertEqual(self.reported(), [(A, self.session(a), "failed")])

    def test_reported_follows_the_state_that_showed_it(self):
        """The monitor writes a tick's ``state`` first, then the outcomes
        queued by then: a session closed after the sample was taken is
        never closed before the state that showed it opening."""
        samples = []

        def while_loading():
            samples.append(self.bridge._observe())
            self.elsewhere("stop")

        self.pm.on_load = while_loading
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.bridge._emit_tick(samples[0])
        messages = self.messages()
        shown = [i for i, m in enumerate(messages)
                 if m.get("event") == "state"
                 and m["data"]["state"] == "opening"
                 and m["data"]["playSessionId"]]
        closed = [i for i, m in enumerate(messages)
                  if m.get("event") == "reported"]
        self.assertTrue(shown and closed, messages)
        self.assertLess(shown[0], closed[0])
        self.assertEqual(messages[closed[0]]["data"]["outcome"], "none")


class ReportedAtShutdownTest(BridgeCase):
    """A session with no outcome when the backend shuts down gets one
    before the ``shutdown`` answer. The shutdown here is the real
    `ShimRuntime.shutdown` over this test's player."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        player = types.ModuleType("jellyfin_mpv_shim.player")
        player.bound_ipc_replies = lambda seconds=5: None
        player.is_using_ext_mpv = True
        player._mark_own_close = lambda handle: None
        patcher = mock.patch.dict(sys.modules,
                                  {"jellyfin_mpv_shim.player": player})
        patcher.start()
        self.addCleanup(patcher.stop)
        shim = bridge_mod.ShimRuntime("unused")
        shim.pm, shim.player_module = self.pm, player
        self.runtime.shutdown = shim.shutdown
        patcher = mock.patch.object(bridge_mod, "SHUTDOWN_BUDGET", 0.6)
        patcher.start()
        self.addCleanup(patcher.stop)

    def shut_down(self):
        self.bridge.shutdown_request_id = 99
        self.bridge.request_shutdown("shutdown command", 0)
        self.bridge.stop()
        messages = self.messages()
        answer = [i for i, m in enumerate(messages) if m.get("id") == 99]
        self.assertEqual(len(answer), 1)
        return messages, answer[0]

    def outcomes(self, messages):
        return [(i, m["data"]["itemId"], m["data"]["outcome"])
                for i, m in enumerate(messages)
                if m.get("event") == "reported"]

    def test_the_final_report_delivered(self):
        self.assertTrue(self.send("play", {"itemIds": [A]})["ok"])
        self.run_plays()
        messages, answer = self.shut_down()
        self.assertTrue(self.bridge.clean_shutdown)
        self.assertEqual([(item, outcome) for i, item, outcome
                          in self.outcomes(messages) if i < answer],
                         [(A, "sent")])

    def test_the_final_report_not_delivered_is_failed_first(self):
        self.assertTrue(self.send("play", {"itemIds": [A]})["ok"])
        self.run_plays()
        self.pm._reporter.hold = True
        self.pm._reporter.hang = True       # the server never answers
        messages, answer = self.shut_down()
        self.assertFalse(self.bridge.clean_shutdown)
        self.assertEqual([(item, outcome) for i, item, outcome
                          in self.outcomes(messages) if i < answer],
                         [(A, "failed")])
        self.pm._reporter.run_jobs()        # the worker, after all
        self.bridge._flush_reported()
        self.assertEqual(len(self.outcomes(self.messages())), 1)

    def test_one_still_opening_is_none(self):
        """The final stop never ran (the player's lock held to the
        deadline): the session that was playing is ``failed``, and the one
        a state showed opening is ``none``."""
        self.assertTrue(self.send("play", {"itemIds": [A]})["ok"])
        self.run_plays()
        opening = FakeVideo(B, client=self.bridge.client)
        self.bridge.opening = opening
        self.bridge.tracker.update(self.bridge._observe())
        held = threading.Event()
        release = threading.Event()
        self.addCleanup(release.set)

        def a_start_holding_the_lock():
            with self.pm._lock:
                held.set()
                release.wait(5)

        threading.Thread(target=a_start_holding_the_lock,
                         daemon=True).start()
        self.assertTrue(held.wait(5))
        messages, answer = self.shut_down()
        self.assertEqual(sorted((item, outcome) for i, item, outcome
                                in self.outcomes(messages) if i < answer),
                         [(A, "failed"), (B, "none")])


class RestartNotTakenTest(BridgeCase):
    """A restart -- a track change on a transcode, a quality change: play()
    on the video that is playing -- that does not take.

    rr5 item 2 (``restart_pbi_fail``): its PlaybackInfo request fails. The
    apiclient answers None for a 500, `get_playback_url` stored that over
    the video's session and then raised on it: the session was reported
    ``failed`` while its stream played on, no progress report went out
    again (play() turns them off for the start) and the stop sent no
    Stopped. BRIDGE.md section 5: it keeps the old session, which goes on
    reporting and gets its normal Stopped.

    ``restart_load_error``, ``restart_cancel_queue``: mpv was handed the new
    stream and the start did not take -- the load failed or was stopped.
    mpv dropped the file; the load's stop() returns early once it has, and
    the video stayed current: "paused", nothing loaded, a ``stop`` doing
    nothing."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.a = self.pm._video
        self.old = self.a.playback_info["PlaySessionId"]
        self.bridge.tracker.update(self.bridge._observe())
        self.assertIs(self.pm.should_send_timeline, True)

    def reported(self):
        self.bridge._flush_reported()
        return [(m["data"]["itemId"], m["data"]["playSessionId"],
                 m["data"]["outcome"]) for m in self.messages()
                if m.get("event") == "reported"]

    def stops(self):
        return [s["PlaySessionId"]
                for s in self.bridge.client.jellyfin.stops]

    def stop(self):
        self.assertTrue(self.send("stop")["ok"])
        self.pm.run_deferred()

    def failed_request(self, then=None):
        """restart_playback, its PlaybackInfo answered with a 500: the
        apiclient's None stored over the session, then the TypeError
        `get_best_media_source` raises on it."""
        def negotiate():
            # Raised inside the negotiation, so before play()'s own cancel
            # check: a cancel landing meanwhile does not stop it raising.
            self.a.playback_info = None
            if then is not None:
                then()
            raise TypeError("'NoneType' object is not subscriptable")

        self.pm.on_negotiate = negotiate
        with self.assertRaises(TypeError):
            self.pm.play(self.a)

    def test_a_500_keeps_the_session_it_had(self):
        self.failed_request()
        self.assertIs(self.pm._video, self.a)
        self.assertEqual(self.a.playback_info, {"PlaySessionId": self.old})
        self.assertIs(self.pm.should_send_timeline, True,
                      "progress reports stay off")
        self.assertEqual(self.reported(), [], "abandoned while it played")
        state = self.bridge.tracker.snapshot(self.bridge._observe())
        self.assertEqual((state["state"], state["playSessionId"]),
                         ("playing", self.old))
        self.stop()
        self.assertEqual(self.stops(), [self.old])
        self.assertEqual(self.reported(), [(A, self.old, "sent")])

    def test_a_request_that_raised_goes_on_reporting(self):
        """A timeout or a refused connection raises before anything is
        stored: the session was never lost, but the progress reports
        were."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.pm.next_play = HTTPException("ReadTimeout", None)
        with self.assertRaises(HTTPException):
            self.pm.play(self.a)
        self.assertEqual(self.a.playback_info, {"PlaySessionId": self.old})
        self.assertIs(self.pm.should_send_timeline, True)
        self.stop()
        self.assertEqual(self.reported(), [(A, self.old, "sent")])

    def test_cancelled_as_its_request_failed_stops_on_its_session(self):
        """A ``stop`` landing while the request failed: the stop reports
        the session the stream played under -- and no progress report is
        let in before it does."""
        self.failed_request(then=lambda: self.elsewhere("stop"))
        self.assertEqual(self.a.playback_info, {"PlaySessionId": self.old})
        self.assertIs(self.pm.should_send_timeline, False)
        self.pm.run_deferred()
        self.assertIsNone(self.pm._video)
        self.assertEqual(self.stops(), [self.old])
        self.assertEqual(self.reported(), [(A, self.old, "sent")])

    def errors(self):
        """The ``error`` events of the tracker's next update."""
        return [d for n, d in self.bridge.tracker.update(
            self.bridge._observe()) if n == "error"]

    def no_stream(self, source=True, session="ps-new"):
        """The restart's PlaybackInfo answers with a new session and no
        stream: a source nothing can play (no URL), or none at all. What the
        negotiation overwrites before play() gives up, as the real one
        does: the session, the source and its stream maps, the play method
        (`_get_url_from_source` clears it when it returns no url)."""
        def negotiate():
            self.a.playback_info = {"PlaySessionId": session}
            self.a.media_source = ({"Id": "src-new", "MediaStreams": []}
                                   if source else None)
            self.a.subtitle_enc, self.a.subtitle_uid = set(), {}
            self.a.audio_uid = {}
            self.a.play_method = None
            self.a.is_transcode = True

        self.pm.on_negotiate = negotiate
        self.pm.next_play = "fail"

    def test_a_negotiation_that_found_no_stream_keeps_the_old_session(self):
        """rr6 item 2 (``restart_no_url``, ``restart_no_source``). BRIDGE.md
        section 5: a restart that never got a usable stream keeps the old
        session -- the stream mpv still plays -- which goes on reporting and
        gets its normal Stopped. The new session, never Playing, is
        ``none``; the change gets an ``error`` event naming the item, with
        the code a new item gets for no stream, and no ``state: error``.
        Without a media source the stop raised: a stop that did nothing."""
        for source in (True, False):
            with self.subTest(source=source):
                if self.pm._video is None:
                    self.setUp()
                del self.sink.data[:]
                new = "ps-new-%s" % source
                saved = dict(vars(self.a))
                self.no_stream(source=source, session=new)
                self.pm.play(self.a)
                self.assertIs(self.pm._video, self.a)
                for key in ("playback_info", "media_source", "subtitle_enc",
                            "subtitle_uid", "audio_uid", "play_method",
                            "is_transcode"):
                    self.assertIs(getattr(self.a, key), saved[key], key)
                self.assertIs(self.pm.should_send_timeline, True,
                              "progress reports stay off")
                self.assertEqual(self.reported(), [(A, new, "none")])
                errors = self.errors()
                self.assertEqual([e["code"] for e in errors], ["mpv_failed"])
                # fluentfin-mpv: for the user -- the item by its title,
                # never its id.
                self.assertIn('"Item"', errors[0]["message"])
                self.assertNotIn(A, errors[0]["message"])
                state = self.bridge.tracker.snapshot(self.bridge._observe())
                self.assertEqual((state["state"], state["playSessionId"],
                                  state["mediaSourceId"]),
                                 ("playing", self.old, "src-" + A))
                self.assertIsNone(self.bridge.tracker.error,
                                  "an error event, not the state")
                self.stop()
                self.assertIsNone(self.pm._video, "the stop did nothing")
                self.assertEqual(self.stops(), [self.old])
                self.assertEqual(self.reported(), [(A, new, "none"),
                                                   (A, self.old, "sent")])
                self.bridge.client.jellyfin.stops.clear()

    def test_the_tracks_it_asked_for_are_not_kept(self):
        """``state`` shows the tracks actually playing: the subtitle the
        change asked for is not on screen (``restart_no_url``: ``state``
        showed it), and it is not left picked for the next renegotiation.
        `Video.set_streams` writes the pick before the restart begins."""
        self.a.subtitle_enc = {2}               # a burned-in subtitle
        self.pm.restart_on_tracks = True
        seen = []

        def negotiate():
            seen.append(self.a.sid)
            self.a.playback_info = {"PlaySessionId": "ps-new"}
            self.a.play_method = None

        self.pm.on_negotiate = negotiate
        self.pm.next_play = "fail"
        self.assertTrue(self.send("set_subtitle", {"index": 2})["ok"])
        self.run_plays()
        self.assertEqual(seen, [2], "the pick is written first")
        self.assertIs(self.pm._video, self.a)
        self.assertEqual((self.a.aid, self.a.sid, self.a.explicit_tracks),
                         (None, None, False))
        state = self.bridge.tracker.snapshot(self.bridge._observe())
        self.assertEqual(state["subtitleStreamIndex"], -1)
        self.assertEqual([e["code"] for e in self.errors()], ["mpv_failed"])

    def test_the_quality_it_asked_for_is_not_kept(self):
        """rr7 item 3: a quality change writes the override
        (`set_trs_override` -- the menu, on its own thread) and then queues
        the restart, so the restart's snapshot already had it: put back, it
        was kept, and the next renegotiation -- a track change on a
        transcode -- asked for the quality that had just failed. Put back
        is the one the stream playing was negotiated with; a change that
        takes is kept, and is what a later failure goes back to."""
        asked = []

        def no_stream():
            asked.append(self.a.trs_ovr)
            self.a.playback_info = {"PlaySessionId": "ps-new-%d"
                                    % len(asked)}
            self.a.play_method = None

        def change(bitrate, takes):
            menu = threading.Thread(target=self.a.set_trs_override,
                                    args=(bitrate, True), daemon=True)
            menu.start()
            menu.join(5)
            if not takes:
                self.pm.on_negotiate = no_stream
                self.pm.next_play = "fail"
            self.pm.put_task(self.pm.restart_playback)
            self.pm.run_deferred()              # the action thread
            self.assertIs(self.pm._video, self.a)

        change(1500000, takes=False)
        self.assertEqual(asked, [(1500000, True)], "the change asked for")
        self.assertIsNone(self.a.trs_ovr, "the failed quality kept")
        change(None, takes=True)
        self.assertEqual(self.a.trs_ovr, (None, True))
        change(800000, takes=False)
        self.assertEqual(asked[-1], (800000, True))
        self.assertEqual(self.a.trs_ovr, (None, True))

    # -- fluentfin-mpv: the message is the user's --------------------------

    def change_fails(self, command, args, outcome):
        """``command`` restarts the item (a transcode's track change), and
        the restart finds nothing: ``outcome`` is play()'s (``"fail"``: no
        url) or what it raises. The one ``error`` event and the log."""
        self.pm.restart_on_tracks = True
        self.pm.on_negotiate = lambda: setattr(self.a, "play_method", None)
        self.pm.next_play = outcome
        with self.assertLogs("bridge", "WARNING") as logs:
            self.assertTrue(self.send(command, args)["ok"])
            self.run_plays()
        errors = self.errors()
        self.assertEqual(len(errors), 1, errors)
        return errors[0], "\n".join(logs.output)

    def test_a_failed_change_is_said_for_the_user(self):
        """The visual check: FluentFin's notice showed the item id and
        "ServerUnreachable ... HTTP 500". It shows this message as it is."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.a.item = {"Name": "Mac's Big Break", "Type": "Episode",
                       "SeriesName": "It's Always Sunny in Philadelphia",
                       "ParentIndexNumber": 6, "IndexNumber": 4}
        title = ('"It\'s Always Sunny in Philadelphia — S6E4 · '
                 'Mac\'s Big Break"')
        for command, args, outcome, want, raw in (
                ("set_subtitle", {"index": 3}, "fail",
                 "The subtitles of %s could not be changed: the server "
                 "offered no stream this player can play." % title,
                 "no stream this player can play"),
                ("set_audio", {"index": 2}, HTTPException(500, None),
                 "The audio track of %s could not be changed: the server "
                 "answered with an error." % title, "500"),
                ("set_subtitle", {"index": 4},
                 HTTPException("ServerUnreachable", None),
                 "The subtitles of %s could not be changed: the server "
                 "could not be reached." % title, "ServerUnreachable"),
                ("set_audio", {"index": 5},
                 HTTPException("Unauthorized", None),
                 "The audio track of %s could not be changed: the server "
                 "refused the request." % title, "Unauthorized")):
            with self.subTest(command=command, outcome=outcome):
                error, log_text = self.change_fails(command, args, outcome)
                self.assertEqual(error["message"],
                                 want + " It goes on playing as before.")
                self.assertFalse(error["fatal"])
                for raw_part in (A, "HTTP", "Exception", "ps-"):
                    self.assertNotIn(raw_part, error["message"])
                # The raw cause is in the log, with the item.
                self.assertIn(raw, log_text)
                self.assertIn(A, log_text)

    def test_a_failed_quality_change_says_quality(self):
        def no_stream():
            self.a.playback_info = {"PlaySessionId": "ps-q"}
            self.a.play_method = None

        menu = threading.Thread(target=self.a.set_trs_override,
                                args=(1500000, True), daemon=True)
        menu.start()
        menu.join(5)
        self.pm.on_negotiate = no_stream
        self.pm.next_play = "fail"
        self.pm.put_task(self.pm.restart_playback)
        self.pm.run_deferred()
        error, = self.errors()
        self.assertEqual(error["message"],
                         'The quality of "Item" could not be changed: the '
                         "server offered no stream this player can play. "
                         "It goes on playing as before.")

    def test_without_a_name_it_is_this_item(self):
        self.a.item = {"Type": "Movie"}
        error, _log = self.change_fails("set_subtitle", {"index": 3}, "fail")
        self.assertTrue(error["message"].startswith(
            "The subtitles of this item could not be changed"),
            error["message"])

    def test_mpvs_auth_header_is_put_back(self):
        """``http-header-fields`` is global (docs/auth-headers.md): play()'s
        no-URL return revokes it, and a subtitle the old stream loads later
        then went out without it (401). The other way round: a start that
        installed it and then raised left it on for a stream from another
        host, which had it off."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        handle = self.pm._player
        self.a.auth_via_header = True
        self.no_stream()
        self.pm.play(self.a)
        self.assertEqual(handle.http_header_fields, [AUTH_HEADER])
        self.assertIs(self.a.auth_via_header, True)
        # Off before (a direct path to another host), on after a raise.
        self.a.auth_via_header = False
        handle.http_header_fields = []
        self.pm.next_play = HTTPException("ServerUnreachable", "no answer")
        with self.assertRaises(HTTPException):
            self.pm.play(self.a)
        self.assertEqual(handle.http_header_fields, [])
        self.assertIs(self.a.auth_via_header, False)

    def test_a_failed_request_is_named_server_unreachable(self):
        """rr6 item 7: the PlaybackInfo the apiclient answered None for now
        raises where it comes back (`bridge_http.check_play_info`); the
        change's ``error`` event carries the request's own code, once --
        the play worker's handler does not report it again."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.pm.restart_on_tracks = True
        error = HTTPException(
            "ServerUnreachable", "the server answered PlaybackInfo with "
            "HTTP 500")
        # As `check_play_info` marks it (PlaybackInfoAnswerTest).
        error.bridge_answered, error.bridge_http_status = True, 500
        self.pm.next_play = error
        with self.assertLogs("bridge", "WARNING") as logs:
            self.assertTrue(self.send("set_audio", {"index": 1})["ok"])
            self.run_plays()
        errors = self.errors()
        self.assertEqual([e["code"] for e in errors], ["server_unreachable"])
        # fluentfin-mpv: the visual check's case. The user reads words;
        # the id and "ServerUnreachable ... HTTP 500" are in the log only.
        self.assertEqual(errors[0]["message"],
                         'The audio track of "Item" could not be changed: '
                         "the server answered with an error. It goes on "
                         "playing as before.")
        log_text = "\n".join(logs.output)
        self.assertIn(A, log_text)
        self.assertIn("HTTP 500", log_text)
        self.assertIs(self.pm._video, self.a)
        self.assertIs(self.pm.should_send_timeline, True)

    def test_interrupted_it_keeps_the_old_session_quietly(self):
        """A ``stop`` landing while it found no stream: the old session is
        put back for the stop to report, no progress report slips in before
        it, and the interrupted change says nothing."""
        self.no_stream()
        negotiate = self.pm.on_negotiate

        def then_stop():
            negotiate()
            self.elsewhere("stop")

        self.pm.on_negotiate = then_stop
        self.pm.play(self.a)
        self.assertEqual(self.a.playback_info, {"PlaySessionId": self.old})
        self.assertIs(self.pm.should_send_timeline, False)
        self.assertEqual(self.errors(), [])
        self.pm.run_deferred()
        self.assertIsNone(self.pm._video)
        self.assertEqual(self.stops(), [self.old])

    def test_cancelled_with_a_stream_in_hand_the_new_session_stands(self):
        """Cancelled at the profile lookup, after its PlaybackInfo found a
        stream: that session replaced the old one (``failed``), and the
        stop that cancelled it reports it -- BRIDGE.md: "whether the new
        session then opened, was cancelled or failed to load"."""
        def negotiate():
            self.a.playback_info = {"PlaySessionId": "ps-new"}
            self.elsewhere("stop")

        self.pm.on_negotiate = negotiate
        self.pm.play(self.a)
        self.assertEqual(self.a.playback_info, {"PlaySessionId": "ps-new"})
        self.pm.run_deferred()
        self.assertEqual(self.stops(), ["ps-new"])
        self.assertEqual(self.reported(), [(A, self.old, "failed"),
                                           (A, "ps-new", "sent")])

    def test_while_it_negotiates_state_shows_the_session_playing(self):
        """Until mpv has the restart's load, mpv plays the session it had --
        and a restart that finds no stream keeps it. The UI tracks the
        latest session a ``state`` shows and never reopens one it has seen
        (`MpvBackendHost.TrackPlaySession`): shown the new id mid-
        negotiation (``restart_no_url`` was), it dropped the old session,
        which then played on with no crash-rule Stopped owed to it. From
        the load on, the new id."""
        seen = []

        def look():
            seen.append(self.bridge.tracker.snapshot(
                self.bridge._observe())["playSessionId"])

        def negotiate():
            self.a.playback_info = {"PlaySessionId": "ps-new"}
            look()

        self.pm.on_negotiate = negotiate
        self.pm.on_load = look
        self.pm.play(self.a)
        look()
        self.assertEqual(seen, [self.old, "ps-new", "ps-new"])

    def restart_reaching_mpv(self, how, session="ps-restarted"):
        self.pm.early_stop_after_abort = True   # mpv dropped the file
        self.pm.on_negotiate = lambda: setattr(
            self.a, "playback_info", {"PlaySessionId": session})
        if how == "cancelled":
            self.pm.on_load = lambda: self.elsewhere("stop")
        else:
            self.pm.next_play = "fail_load"
        self.pm.play(self.a)

    def test_one_whose_load_did_not_take_ends(self):
        for how in ("fail_load", "cancelled"):
            with self.subTest(how=how):
                if self.pm._video is None:
                    self.setUp()
                del self.sink.data[:]
                self.restart_reaching_mpv(how)
                self.assertIsNone(self.pm._video, "left current, nothing "
                                  "loaded")
                self.pm.run_deferred()
                events = self.bridge.tracker.update(self.bridge._observe())
                state = [d for n, d in events if n == "state"][-1]
                ended = [d for n, d in events if n == "ended"]
                if how == "fail_load":
                    self.assertEqual((state["state"], state["itemId"]),
                                     ("error", A))
                    self.assertEqual(ended, [{"itemId": A,
                                              "reason": "error"}])
                else:
                    self.assertEqual(state["state"], "idle")
                    self.assertEqual(ended, [{"itemId": A,
                                              "reason": "stopped"}])
                self.assertEqual(self.stops(), [], "no position to report")
                self.assertEqual(self.reported(), [
                    (A, self.old, "failed"), (A, "ps-restarted", "none")])
                self.assertIn(("release_stream", A), self.runtime.journal)
                self.stop()
                self.assertEqual(self.bridge.tracker.snapshot(
                    self.bridge._observe())["state"], "idle")
                self.assertEqual(self.stops(), [])
                self.bridge.client.jellyfin.stops.clear()
                self.pm._video = None

    def test_one_whose_load_did_not_take_under_the_same_id_is_failed(self):
        """A server that handed the restart the session id it had: nothing
        replaced it, it opened, and it gets no Stopped -- ``failed``."""
        self.restart_reaching_mpv("fail_load", session=self.old)
        self.assertIsNone(self.pm._video)
        self.assertEqual(self.reported(), [(A, self.old, "failed")])

    def test_its_track_change_does_not_raise_once_it_ended(self):
        """`PlayerManager.set_streams` -- the real source, compiled on its
        own (importing ``player`` builds mpv) -- after a restart whose load
        did not take has ended the video: the track memory it takes next
        read None, and the AttributeError was reported over the load's own
        error, naming no item (``restart_load_error``)."""
        path = os.path.join(ROOT, "jellyfin_mpv_shim", "player.py")
        with open(path, encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        cls = next(n for n in tree.body if isinstance(n, ast.ClassDef)
                   and n.name == "PlayerManager")
        fn = next(n for n in cls.body if isinstance(n, ast.FunctionDef)
                  and n.name == "set_streams")
        fn.decorator_list = []          # @synchronous: no lock here
        ns = {}
        exec(compile(ast.Module(body=[fn], type_ignores=[]), path, "exec"),
             ns)
        for ends in (True, False):
            with self.subTest(restart_ends_the_video=ends):
                captured = []
                video = types.SimpleNamespace(
                    set_streams=lambda aid, sid: True,    # a restart
                    media_source={"Id": "src"})
                player = types.SimpleNamespace(_video=video)
                player.restart_playback = lambda: setattr(
                    player, "_video", None if ends else video)
                player.configure_streams = lambda: None
                player._capture_track_memory = lambda v: captured.append(
                    v.media_source)
                player.timeline_handle = lambda: None
                ns["set_streams"](player, 1, None)
                self.assertEqual(captured, [] if ends else [{"Id": "src"}])

    def test_one_that_never_reached_mpv_is_left_to_its_canceller(self):
        """Cancelled while it negotiated, mpv untouched: still playing, and
        the stop that cancelled it reports it."""
        self.pm.on_negotiate = lambda: self.elsewhere("stop")
        self.pm.play(self.a)
        self.assertIs(self.pm._video, self.a)
        self.pm.run_deferred()
        self.assertIsNone(self.pm._video)
        self.assertEqual(self.stops(), [self.old])


    def test_a_restart_a_play_cancels_ends_replaced(self):
        """rr6 item 3 (``restart_cancel_play``): mpv has the restart's load
        when a ``play`` cancels it. The restart ends the item itself
        (`_restart_not_taken`) before the play's start marks it, and it
        ended ``stopped``: another item took over, so ``replaced``."""
        self.pm.early_stop_after_abort = True   # mpv dropped the file
        self.pm.on_negotiate = lambda: setattr(
            self.a, "playback_info", {"PlaySessionId": "ps-restarted"})
        self.pm.on_load = lambda: self.elsewhere("play", {"itemIds": [B]})
        self.pm.play(self.a)
        self.assertIsNone(self.pm._video)
        ended = [d for n, d in self.bridge.tracker.update(
            self.bridge._observe()) if n == "ended"]
        self.assertEqual(ended, [{"itemId": A, "reason": "replaced"}])
        self.run_plays()
        self.assertEqual(self.pm._video.item_id, B)


class PlayOwnsWhatComesNextTest(BridgeCase):
    """rr6 item 1 (``play_vs_advance``): queue [A, B], A in its last
    seconds, ``play C``. C's start reports A Stopped and negotiates C's
    stream without the player's lock; A reaches its end meanwhile, and the
    queue's finished callback advanced to B -- Playing to the server --
    which C then loaded over, with no Stopped for B. A ``play`` pending or
    opening owns what comes next: B never starts. If C fails before mpv
    (PlaybackInfo, no stream, the item fetch), A's end is honoured as it
    would have been: the queue advances, or A ends. Every session: one
    Stopped, or ``reported none``."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.a = self.pm._video
        self.bridge.tracker.update(self.bridge._observe())

    def journal(self, kind):
        return [e[1] for e in self.runtime.journal if e[0] == kind]

    def a_ends(self):
        """A's end of file, as mpv's observer queues it: the finished
        callback on the action thread (here: a thread of its own), now."""
        epoch = self.pm._play_epoch
        thread = threading.Thread(target=self.pm.finished_callback,
                                  args=(True, epoch), daemon=True)
        thread.start()
        thread.join(5)
        self.assertFalse(thread.is_alive())

    def ended(self):
        return [d for n, d in self.bridge.tracker.update(
            self.bridge._observe()) if n == "ended"]

    def play_c(self, then=None, fails=None):
        """``play C``, A ending while C negotiates its stream."""
        def negotiate():
            self.a_ends()
            if then is not None:
                then()

        self.pm.on_negotiate = negotiate
        if fails is not None:
            self.pm.next_play = fails
        self.assertTrue(self.send("play", {"itemIds": [C]})["ok"])
        self.run_plays()

    def test_the_queue_does_not_advance_into_a_play(self):
        self.pm.queue_next = FakeVideo(B)
        self.play_c()
        self.assertEqual(self.pm._video.item_id, C)
        self.assertNotIn(B, self.journal("play"), "B started")
        self.assertEqual(self.journal("report_playing"), [A, C])
        self.assertEqual(self.ended(), [{"itemId": A, "reason": "replaced"}])
        self.assertIsNone(self.bridge._deferred_finish, "held for nothing")
        self.send("stop")
        self.pm.run_deferred()
        self.assertEqual(self.journal("report_stopped"), [A, C])

    def test_nor_while_the_play_is_still_pending(self):
        """A's end before C's start has even reached play() (C's item
        fetch): held all the same."""
        self.pm.queue_next = FakeVideo(B)
        self.assertTrue(self.send("play", {"itemIds": [C]})["ok"])
        self.a_ends()
        self.run_plays()
        self.assertEqual(self.pm._video.item_id, C)
        self.assertNotIn(B, self.journal("play"))
        self.assertEqual(self.journal("report_stopped"), [A])

    def test_a_play_that_fails_before_mpv_lets_the_queue_advance(self):
        """C's PlaybackInfo fails: A is still current, at its end, and its
        end -- held -- runs after all: B plays, A ends ``eof``, and A is
        reported once (by C's start)."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.pm.queue_next = FakeVideo(B)
        self.play_c(fails=HTTPException("ServerUnreachable", "HTTP 500"))
        self.assertIs(self.pm._video, self.a)
        self.pm.run_deferred()          # the action thread: A's end
        self.assertEqual(self.pm._video.item_id, B)
        self.assertEqual(self.journal("report_playing"), [A, B])
        self.assertEqual(self.ended(), [{"itemId": A, "reason": "eof"}])
        self.assertEqual(self.journal("report_stopped"), [A])

    def test_an_end_without_the_lock_keeps_the_first_ones(self):
        """mpv's end-of-file observer and the poll rescue both queue the
        callback; only the first takes the `_finished_lock` (``has_lock``),
        and only that one may advance. Held twice, the lock is kept: the
        advance still happens when the end runs after all."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.pm.queue_next = FakeVideo(B)
        epoch = self.pm._play_epoch

        def both_end():
            for has_lock in (True, False):
                thread = threading.Thread(target=self.pm.finished_callback,
                                          args=(has_lock, epoch), daemon=True)
                thread.start()
                thread.join(5)

        self.pm.on_negotiate = both_end
        self.pm.next_play = HTTPException("ServerUnreachable", "HTTP 500")
        self.assertTrue(self.send("play", {"itemIds": [C]})["ok"])
        self.run_plays()
        self.pm.run_deferred()
        self.assertEqual(self.pm._video.item_id, B)

    def test_with_no_next_item_it_ends(self):
        self.play_c(fails="fail")       # no stream for C
        self.assertIs(self.pm._video, self.a)
        self.pm.run_deferred()
        self.assertIsNone(self.pm._video, "A sat at its end for good")
        self.assertEqual(self.ended(), [{"itemId": A, "reason": "eof"}])
        self.assertEqual(self.journal("report_stopped"), [A])

    def test_an_item_fetch_that_fails_lets_it_advance(self):
        """C never reaches play(): A was never reported by it, so the
        advance reports A itself."""
        self.pm.queue_next = FakeVideo(B)
        self.assertTrue(self.send("play", {"itemIds": [C]})["ok"])
        self.a_ends()
        self.runtime.fail_fetch = ConnectionResetError("reset")
        self.run_plays()
        self.assertIs(self.pm._video, self.a)
        self.pm.run_deferred()
        self.assertEqual(self.pm._video.item_id, B)
        self.assertEqual(self.journal("report_stopped"), [A])

    def test_a_stop_in_between_ends_it_and_nothing_follows(self):
        self.pm.queue_next = FakeVideo(B)
        self.play_c(then=lambda: self.elsewhere("stop"))
        self.pm.run_deferred()
        self.assertIsNone(self.pm._video)
        self.assertNotIn(B, self.journal("play"))
        self.assertEqual(self.journal("report_stopped"), [A])
        self.assertIsNone(self.bridge._deferred_finish)

    def test_with_nothing_replacing_it_the_end_runs_at_once(self):
        """The control: no ``play`` in sight, A's end advances as ever."""
        self.pm.queue_next = FakeVideo(B)
        self.a_ends()
        self.assertEqual(self.pm._video.item_id, B)
        self.assertIsNone(self.bridge._deferred_finish)

    def held_while_pending(self):
        """``play C`` waiting on its item fetch; A, playing for a while,
        reaches its end: mpv's end-of-file and abort observers both queue
        the finished callback, and the action thread goes round. Returns
        how many passes it made back to back, and the bridge's log."""
        self.assertTrue(self.send("play", {"itemIds": [C]})["ok"])
        self.pm.start_time = time.time() - 60
        self.pm.eof_reached = True
        with self.assertLogs("bridge", level="INFO") as logs:
            self.pm._queue_finished()
            self.pm._queue_finished()
            passes = self.pm.pump(50)
        return passes, logs.output

    def test_a_held_end_is_not_queued_again_on_every_pass(self):
        """rr7 item 1 (``spin:20,2``, ``spin:20,1``): the poll rescue in
        `update`, gated on the progress reports, queued the held end again
        on every pass, and each queueing woke the action thread for the
        next -- ~40,000 holds and 14 s of CPU in 19 s while C's item fetch
        took its time, and every test passed (no fake had the rescue).
        Held once, logged once, and the thread goes back to waiting; C's
        start then goes ahead as before."""
        for queued in (True, False):        # A with a next item, and last
            with self.subTest(next_item=queued):
                if self.pm._video is not self.a or self.bridge.pending:
                    self.setUp()
                if queued:
                    self.pm.queue_next = FakeVideo(B)
                passes, logs = self.held_while_pending()
                self.assertLessEqual(passes, 2, "the action thread spun")
                self.assertEqual(
                    sum("Holding the end" in line for line in logs), 1)
                self.assertIsNotNone(self.bridge._deferred_finish)
                for _ in range(3):
                    self.assertEqual(self.pm.pump(50), 1,
                                     "a later pass queued it again")
                self.assertIs(self.pm._video, self.a)
                self.run_plays()                # the item fetch is back
                self.assertEqual(self.pm._video.item_id, C)
                self.assertNotIn(B, self.journal("play"))
                self.assertEqual(self.journal("report_stopped"), [A])

    def test_a_held_end_still_runs_after_a_failed_fetch(self):
        """The rescue off does not cost the end: C's item fetch failing,
        A's end runs on the next pass -- the queue advances, or A ends."""
        for queued in (True, False):
            with self.subTest(next_item=queued):
                if self.pm._video is not self.a or self.bridge.pending:
                    self.setUp()
                if queued:
                    self.pm.queue_next = FakeVideo(B)
                self.held_while_pending()
                self.runtime.fail_fetch = ConnectionResetError("reset")
                self.run_plays()
                self.runtime.fail_fetch = None
                self.pm.pump(50)
                if queued:
                    self.assertEqual(self.pm._video.item_id, B)
                else:
                    self.assertIsNone(self.pm._video)
                self.assertEqual(self.journal("report_stopped"), [A])
                self.assertIsNone(self.bridge._deferred_finish)


class HeldEndAtTheStopTest(BridgeCase):
    """rr7 item 2 (``held_last:shutdown``, ``held_last:eof``): A, the last
    item, reaches its end while ``play C`` waits on its item fetch. mpv does
    not keep the last item open: it idles (``playback_abort``), so stop()
    returns early and reports nothing, and A's end is held for C. The
    shutdown refused to run a held end and its stop() reported nothing:
    Playing 1, Stopped 0, ``reported failed``. A ``stop`` that dropped C's
    job before it started left A current for good -- nothing else runs a
    held end. Both now end it: its Stopped (``finished``), and never the
    queue's advance."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.a = self.pm._video
        self.session = self.a.playback_info["PlaySessionId"]
        self.bridge.tracker.update(self.bridge._observe())

    def journal(self, kind):
        return [e[1] for e in self.runtime.journal if e[0] == kind]

    def stops(self):
        return [s["PlaySessionId"] for s in self.bridge.client.jellyfin.stops]

    def a_ends_held(self):
        self.assertTrue(self.send("play", {"itemIds": [C]})["ok"])
        self.pm._aborted = True                 # mpv idles at the end
        self.pm.early_stop_after_abort = True
        self.pm.finished_callback(True, self.pm._play_epoch)
        self.assertIsNotNone(self.bridge._deferred_finish)
        self.assertIs(self.pm._video, self.a)

    def test_the_shutdown_reports_it(self):
        for reason in ("shutdown command", "stdin closed"):
            with self.subTest(reason=reason):
                if self.pm._video is not self.a or self.runtime.shut:
                    self.setUp()
                self.a_ends_held()
                self.runtime.final_report = True
                if reason == "shutdown command":
                    self.bridge.shutdown_request_id = 99
                self.bridge.request_shutdown(reason, 0)
                self.bridge.stop()
                self.assertEqual(self.stops(), [self.session])
                self.assertEqual(self.journal("report_stopped"), [A])
                messages = self.messages()
                done = [(i, m["data"]) for i, m in enumerate(messages)
                        if m.get("event") == "reported"]
                self.assertEqual([d for _, d in done], [{
                    "itemId": A, "playSessionId": self.session,
                    "outcome": "sent"}])
                answer = [i for i, m in enumerate(messages)
                          if m.get("id") == 99]
                if answer:
                    self.assertLess(done[0][0], answer[0])
                self.assertNotIn(B, self.journal("play"))
                # The shutdown's own stop ends what is loaded, its replies
                # bounded to the deadline: no unload of its own.
                self.assertEqual(self.journal("unload"), [])

    def test_the_shutdown_answers_in_time_with_mpv_hung(self):
        """rr8 (``held_hung:shutdown``, ``held_hung:eof``): A's end held for
        C, mpv then stops answering, then the shutdown. Each read of mpv
        waits out the reply bound and times out (here 1.2 s of a 2 s
        budget; there 5 s of 8). The held end, run before stop(), reported A
        through `get_timeline_options` -- which swallows the timeout and
        leaves mpv for alive -- and stop() then waited again, on
        ``playback_abort``: 10 s, past the exit watchdog at 9.5 -- exit 1,
        no answer, no ``reported`` (exit 0 at ~6 s before round 7 put it
        there). Run after stop(), whose one timeout is mpv lost: A is
        reported from bookkeeping and the held end finds it gone. One reply
        waited out, and A's Stopped and ``reported sent`` before the answer,
        in time."""
        for reason in ("shutdown command", "stdin closed"):
            with self.subTest(reason=reason):
                if self.pm._video is not self.a or self.runtime.shut:
                    self.setUp()
                self.a_ends_held()
                self.runtime.final_report = True
                self.pm.mpv_hung = 1.2
                if reason == "shutdown command":
                    self.bridge.shutdown_request_id = 99
                with mock.patch.object(bridge_mod, "SHUTDOWN_BUDGET", 2.0):
                    started = time.monotonic()
                    self.bridge.request_shutdown(reason, 0)
                    self.bridge.stop()
                    took = time.monotonic() - started
                self.assertLess(took, 2.0, "past the shutdown's budget")
                self.assertIs(self.bridge.clean_shutdown, True)
                self.assertEqual(self.journal("mpv_read"), ["timed out"],
                                 "a hung mpv waited out more than once")
                self.assertEqual(self.stops(), [self.session])
                self.assertEqual(self.journal("report_stopped"), [A])
                messages = self.messages()
                done = [(i, m["data"]) for i, m in enumerate(messages)
                        if m.get("event") == "reported"]
                self.assertEqual([d for _, d in done], [{
                    "itemId": A, "playSessionId": self.session,
                    "outcome": "sent"}])
                answer = [i for i, m in enumerate(messages)
                          if m.get("id") == 99]
                self.assertEqual(len(answer),
                                 1 if reason == "shutdown command" else 0)
                if answer:
                    self.assertLess(done[0][0], answer[0])
                self.assertNotIn(B, self.journal("play"))

    def stop_as_the_fetch_returns(self, dropped):
        """Queue [A, B]; ``play C`` waits on its item fetch, and A reaches
        its end meanwhile -- held (kept open by mpv, or ``dropped``: its
        stream gave out). Then ``stop``, with C's fetch coming back inside
        it: after its `_supersede`, before it queues its deferred stop --
        C's job, superseded, does its last step (`_finish_deferred`) in
        that window. Then the action thread runs what was queued."""
        self.pm.queue_next = FakeVideo(B)
        in_fetch, fetched = threading.Event(), threading.Event()
        start_playback = self.runtime.start_playback

        def a_slow_fetch(client, user_id, request):
            if request["itemIds"] == [C]:
                in_fetch.set()
                self.assertTrue(fetched.wait(10))
            return start_playback(client, user_id, request)

        self.runtime.start_playback = a_slow_fetch
        self.assertTrue(self.send("play", {"itemIds": [C]})["ok"])
        if dropped:
            self.pm._aborted = True
            self.pm.early_stop_after_abort = True
        self.pm.finished_callback(True, self.pm._play_epoch)
        self.assertIsNotNone(self.bridge._deferred_finish)
        worker = threading.Thread(target=self.run_plays, daemon=True)
        worker.start()                          # the play worker: C's job
        self.assertTrue(in_fetch.wait(10))
        supersede = self.bridge._supersede

        def then_the_fetch_returns():
            dev = supersede()
            fetched.set()
            worker.join(10)                     # C's job, to its end
            self.assertFalse(worker.is_alive())
            return dev

        self.bridge._supersede = then_the_fetch_returns
        self.assertTrue(self.send("stop")["ok"])
        self.assertTrue(fetched.is_set(), "the stop did not supersede")
        self.pm.run_deferred()                  # the action thread

    def test_a_stop_as_the_fetch_returns_does_not_advance_the_queue(self):
        """Round 7's low finding: C's job, superseded by the stop while its
        item fetch came back, still ran A's held end -- queued ahead of the
        stop's deferred stop: the queue advanced, B got Playing, and the
        stop then stopped B. Superseded, the job leaves the held end to
        what superseded it: the stop ends A -- its own Stopped, or the held
        end's when mpv had dropped A -- and B never starts."""
        for dropped in (False, True):
            with self.subTest(dropped=dropped):
                if self.pm._video is not self.a or self.bridge.pending:
                    self.setUp()
                self.stop_as_the_fetch_returns(dropped)
                self.assertNotIn(B, self.journal("play"), "B started")
                self.assertEqual(self.journal("report_playing"), [A])
                self.assertIsNone(self.pm._video)
                self.assertEqual(self.journal("report_stopped"), [A])
                self.assertEqual(self.stops(), [self.session])
                self.assertIsNone(self.bridge._deferred_finish)
                self.assertEqual(self.bridge.tracker.snapshot(
                    self.bridge._observe())["state"], "idle")

    def test_a_play_done_once_the_shutdown_began_leaves_it_to_it(self):
        """C's job ends -- its fetch failed -- after the shutdown has begun
        and before its stop: the held end is not run then (the shutdown
        ends what is loaded), and not dropped either -- the shutdown's stop
        reports it."""
        self.a_ends_held()
        self.runtime.final_report = True
        self.bridge.request_shutdown("stdin closed", 0)
        with self.bridge._state_lock:
            self.bridge.pending = None          # C's job, done
        self.bridge._finish_deferred()          # its last step
        self.assertEqual(self.pm.deferred, [], "run during the shutdown")
        self.bridge.stop()
        self.assertEqual(self.stops(), [self.session])
        self.assertEqual(self.journal("report_stopped"), [A])

    def test_a_stop_ends_it_when_the_play_never_starts(self):
        """``stop`` drops C's job before it starts. Answered at once, as
        ever; the deferred stop, finding stop() did nothing, ends A."""
        self.a_ends_held()
        self.assertTrue(self.send("stop")["ok"])
        self.assertIs(self.pm._video, self.a, "the stop is deferred")
        self.pm.run_deferred()                  # the stop, action thread
        self.run_plays()                        # C: dropped, superseded
        self.assertIsNone(self.pm._video, "A left current")
        self.assertEqual(self.journal("report_stopped"), [A])
        self.assertEqual(self.stops(), [self.session])
        self.assertEqual(self.journal("start_playback"), [[A]])
        events = self.bridge.tracker.update(self.bridge._observe())
        self.assertEqual([d for n, d in events if n == "ended"],
                         [{"itemId": A, "reason": "eof"}])
        self.assertEqual(self.bridge.tracker.snapshot(
            self.bridge._observe())["state"], "idle")
        self.assertIsNone(self.bridge._deferred_finish)

    def test_nor_does_a_stop_advance_the_queue(self):
        """The held end of an item with a next one -- its stream gave out
        and mpv dropped it -- ended by a ``stop``: the item ends, the next
        never starts."""
        self.pm.queue_next = FakeVideo(B)
        self.a_ends_held()
        self.send("stop")
        self.pm.run_deferred()
        self.run_plays()
        self.assertIsNone(self.pm._video)
        self.assertNotIn(B, self.journal("play"))
        self.assertEqual(self.journal("report_stopped"), [A])

    def test_a_stop_that_stopped_it_leaves_nothing_held(self):
        """The control: mpv still has the file (a next item keeps it open
        at its end), stop() reports and drops it; the held end is spent,
        not run after."""
        self.pm.queue_next = FakeVideo(B)
        self.a_ends_held()
        self.pm._aborted = False
        self.send("stop")
        self.pm.run_deferred()
        self.run_plays()
        self.assertIsNone(self.pm._video)
        self.assertEqual(self.journal("report_stopped"), [A])
        self.assertEqual(self.journal("unload"), [], "ended twice")
        self.assertIsNone(self.bridge._deferred_finish)


class LoadHooksAreTheirStartsTest(BridgeCase):
    """rr6 item 4: `_on_load_start` and `_on_load_error` took the load for
    ``opening``'s -- the latest start on any thread. A ``play`` whose start
    entered the hook while a restart on the action thread (the OSC's track
    change) negotiated made itself ``opening``; the restart's load was then
    taken for the play's: the restart, whose load failed and dropped the
    file, was not ended (left current with nothing loaded until the play
    reported it), and the play was marked failed with the restart's error.
    Each start now has its own record on its own thread."""

    def test_a_restart_loading_while_a_play_enters(self):
        self.connect_server()
        self.runtime.start_plays = True
        pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        a = pm._video
        self.bridge.tracker.update(self.bridge._observe())
        pm.early_stop_after_abort = True        # mpv dropped the file
        pm.restart_on_tracks = True
        negotiating, go = threading.Event(), threading.Event()
        self.addCleanup(go.set)

        def slow_playbackinfo():
            negotiating.set()
            go.wait(10)

        pm.on_negotiate = slow_playbackinfo
        # Accepted before the restart began, so nothing for it to cancel.
        self.assertTrue(self.send("play", {"itemIds": [B]})["ok"])
        restart = threading.Thread(target=pm.set_streams, args=(None, 2),
                                   daemon=True)
        restart.start()                         # the OSC, on its thread
        self.assertTrue(negotiating.wait(5))
        worker = threading.Thread(target=self.run_plays, daemon=True)
        worker.start()                          # B's start, into the hook
        deadline = time.monotonic() + 5
        while (getattr(self.bridge.opening, "item_id", None) != B
               and time.monotonic() < deadline):
            time.sleep(0.01)
        b = self.bridge.opening
        self.assertEqual(getattr(b, "item_id", None), B)
        pm.next_play = "fail_load"              # the restart's load fails
        go.set()
        restart.join(5)
        worker.join(5)
        self.assertFalse(restart.is_alive() or worker.is_alive())
        self.assertIn(("unload", A), self.runtime.journal,
                      "the restart's item left current, nothing loaded")
        self.assertNotIn(b, self.bridge._start_failed)
        self.assertIs(pm._video, b)
        events = self.bridge.tracker.update(self.bridge._observe())
        self.assertEqual([d for n, d in events if n == "ended"],
                         [{"itemId": A, "reason": "error"}])
        self.assertEqual([d["code"] for n, d in events if n == "error"],
                         ["mpv_failed"])
        self.assertIsNot(pm._video, a)

    def test_an_interrupted_stream_is_the_playing_items_whatever_opens(self):
        """The finished callback's interrupted stream, on the action
        thread, while a ``play`` negotiates: the error is the item playing,
        not the start."""
        self.connect_server()
        self.runtime.start_plays = True
        pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.bridge.tracker.update(self.bridge._observe())
        seen = []

        def interrupted():
            thread = threading.Thread(target=pm.on_load_error, args=(
                {"detail": "the server stopped sending", "timed_out": False,
                 "owns_window": False},), daemon=True)
            thread.start()
            thread.join(5)
            seen.append(self.bridge.opening)

        pm.on_negotiate = interrupted
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.assertEqual(pm._video.item_id, B)
        self.assertNotIn(seen[0], self.bridge._start_failed)


class TimelineOptionsWithoutASourceTest(unittest.TestCase):
    """rr6 item 2 (``restart_no_source``): a video left with a session and
    no media source made `get_timeline_options` raise a TypeError, which
    made `stop()` raise before it dropped the video -- ``stop`` answered
    ok, the state stayed ``playing``, no Stopped ever went, and the
    shutdown's stop raised too. The real method, on a stand-in player."""

    def test_the_report_is_built_and_names_no_source(self):
        from jellyfin_mpv_shim import player_reporting

        fake_player = types.ModuleType("jellyfin_mpv_shim.player")
        fake_player._mpv_errors = (BrokenPipeError,)
        fake_player.discord_presence = False
        handle = types.SimpleNamespace(
            volume=100, mute=False, pause=False, duration=120.0,
            cache_buffering_state=0, playback_time=30.0)
        video = types.SimpleNamespace(
            is_photo=False, playback_info={"PlaySessionId": "ps-1"},
            media_source=None, sid=None, aid=None, is_transcode=False,
            item_id=A, is_tv=False, item={},
            parent=types.SimpleNamespace(queue=[]),
            get_playlist_id=lambda: "p0")
        player = types.SimpleNamespace(
            _video=video, _player=handle, _last_playback_position=0,
            _finished_at_eof=lambda v, t=None: False, last_seek=0,
            repeat_mode=None, start_time=time.time())
        with mock.patch.dict(sys.modules,
                             {"jellyfin_mpv_shim.player": fake_player}):
            options = player_reporting.ReportingMixin.get_timeline_options(
                player, video=video)
        self.assertEqual((options["PlaySessionId"], options["MediaSourceId"],
                          options["PositionTicks"]),
                         ("ps-1", None, 300000000))


class ReportedWithoutATickTest(BridgeCase):
    """rr5 item 3 (``hung_mpv_stop``): the monitor reads mpv before each
    tick, and an mpv that stops answering holds that read (jsonipc waits
    120 s for a reply). Every ``reported`` queued meanwhile waited for it
    -- until the shutdown. BRIDGE.md section 5: written on the next tick, or
    directly within about 1 s if no tick completes."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        self.a = self.pm._video

    def hang_the_monitor(self):
        """mpv stops answering: the monitor's next sample of it blocks."""
        self.hung, self.release = threading.Event(), threading.Event()
        self.addCleanup(self.release.set)
        observe = self.bridge._observe

        def stuck(cheap=False):
            if not cheap and not self.release.is_set():
                self.hung.set()
                self.release.wait(10)
            return observe(cheap)

        self.bridge._observe = stuck
        self.start_threads()
        self.bridge.poke()
        self.assertTrue(self.hung.wait(5))

    def reported(self):
        return [m["data"] for m in self.messages()
                if m.get("event") == "reported"]

    def test_written_directly_when_the_tick_does_not_come(self):
        self.hang_the_monitor()
        self.send("stop")
        queued = time.monotonic()
        self.pm.run_deferred()          # the Stopped, answered 204
        written = self.wait_message(lambda m: m.get("event") == "reported",
                                    timeout=5)
        took = time.monotonic() - queued
        self.assertIsNotNone(written, "held until the monitor came back")
        self.assertEqual(written["data"]["outcome"], "sent")
        self.assertLess(took, bridge_mod.REPORTED_WAIT + 1.0)
        self.assertGreaterEqual(took, bridge_mod.REPORTED_WAIT * 0.8,
                                "written ahead of a healthy monitor's tick")
        self.release.set()
        self.bridge.poke()
        time.sleep(0.5)
        self.assertEqual(len(self.reported()), 1, "written twice")

    def test_a_healthy_monitor_writes_it_after_its_state(self):
        """The direct write is only for a tick that does not come: with
        the monitor answering, the outcome follows the tick's ``state``."""
        self.start_threads()            # primes the tracker: A playing
        self.bridge.poke()
        time.sleep(0.2)
        with self.assertNoLogs("bridge", level="WARNING"):
            self.send("stop")
            self.pm.run_deferred()
            self.assertIsNotNone(self.wait_message(
                lambda m: m.get("event") == "reported"))
            time.sleep(bridge_mod.REPORTED_WAIT + 0.3)
        messages = self.messages()
        idle = [i for i, m in enumerate(messages)
                if m.get("event") == "state" and m["data"]["state"] == "idle"]
        done = [i for i, m in enumerate(messages)
                if m.get("event") == "reported"]
        self.assertEqual(len(done), 1)
        self.assertTrue(idle and idle[0] < done[0], messages)

    def test_the_shutdown_still_writes_it_before_its_answer(self):
        """Hung monitor, outcome queued, shutdown at once: written once,
        before the answer, whichever writes it."""
        self.hang_the_monitor()
        self.send("stop")
        self.pm.run_deferred()
        self.bridge.shutdown_request_id = 99
        self.bridge.request_shutdown("shutdown command", 0)
        self.bridge.stop()
        messages = self.messages()
        answer = [i for i, m in enumerate(messages) if m.get("id") == 99]
        done = [i for i, m in enumerate(messages)
                if m.get("event") == "reported"]
        self.assertEqual(len(done), 1)
        self.assertEqual(len(answer), 1)
        self.assertLess(done[0], answer[0])
        self.assertTrue(self.bridge._reported_closed,
                        "the shutdown's flush is the last")

    def test_written_under_the_emit_lock(self):
        """Never in the middle of the monitor's own writes: a tick holding
        `_emit_lock` (its ``state`` events) holds the direct write too --
        and the reporting thread never writes at all."""
        self.hang_the_monitor()
        held, release = threading.Event(), threading.Event()
        self.addCleanup(release.set)

        def a_tick_writing():
            with self.bridge._emit_lock:
                held.set()
                release.wait(10)

        holder = threading.Thread(target=a_tick_writing, daemon=True)
        holder.start()
        self.assertTrue(held.wait(5))
        self.send("stop")
        self.pm.run_deferred()          # the Stopped, on this thread
        time.sleep(bridge_mod.REPORTED_WAIT + 0.5)
        self.assertEqual(self.reported(), [],
                         "written while a tick held the lock")
        release.set()
        holder.join(5)
        self.assertIsNotNone(self.wait_message(
            lambda m: m.get("event") == "reported", timeout=3))

    def test_nothing_is_written_after_the_last_flush(self):
        """The shutdown's flush is the last: an outcome queued after it --
        there should be none, the sweep closed every session -- is not
        written between the answer and the writer closing."""
        self.bridge._flush_reported(final=True)
        self.bridge.tracker.finish_session("ps-late", A, "sent")
        with self.bridge._emit_lock:
            self.bridge._send_reported()
        self.bridge._flush_reported()
        self.assertEqual(self.reported(), [])


    def test_with_nothing_queued_it_does_not_spin(self):
        """rr6 item 5 (the round-6 reviewer's m1): the writer clears its
        wake-up each time it wakes. Left set, it spun -- two lock round
        trips per pass, for the life of the process -- and every test
        passed. Over a second with nothing queued: a handful of looks at
        most."""
        calls = []
        pending = self.bridge.tracker.reported_pending

        def counted():
            calls.append(1)
            return pending()

        self.bridge.tracker.reported_pending = counted
        self.start_threads()
        self.send("stop")
        self.pm.run_deferred()          # one outcome, queued and written
        self.assertIsNotNone(self.wait_message(
            lambda m: m.get("event") == "reported"))
        time.sleep(bridge_mod.MONITOR_INTERVAL)
        del calls[:]
        time.sleep(1.0)
        self.assertLessEqual(len(calls), 5, "the writer spins")

class EndingAtTheShutdownTest(BridgeCase):
    """``hung_mpv_next``: a next holds the player's lock across a slow
    PlaybackInfo, mpv stops answering, and the shutdown cancels the next.
    The hook then ended the item before it -- an unload that talks to mpv
    -- with the lock held, past the shutdown's deadline: exit 1, where the
    shutdown's own stop, bounding mpv's replies, made it in time. During the
    shutdown the hook leaves that to the shutdown. The shutdown here is the
    real `ShimRuntime.shutdown` over this test's player."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.runtime.start_plays = True
        self.pm = self.runtime.pm
        player = types.ModuleType("jellyfin_mpv_shim.player")
        player.bound_ipc_replies = lambda seconds=5: None
        player.is_using_ext_mpv = True
        player._mark_own_close = lambda handle: None
        patcher = mock.patch.dict(sys.modules,
                                  {"jellyfin_mpv_shim.player": player})
        patcher.start()
        self.addCleanup(patcher.stop)
        shim = bridge_mod.ShimRuntime("unused")
        shim.pm, shim.player_module = self.pm, player
        self.runtime.shutdown = shim.shutdown
        patcher = mock.patch.object(bridge_mod, "SHUTDOWN_BUDGET", 0.8)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_a_next_the_shutdown_cancelled(self):
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        unload = self.pm._unload_ended
        unloads = []

        def mpv_not_answering(video):
            unloads.append(video.item_id)
            time.sleep(1.5)             # a reply that never comes, bounded
            unload(video)

        self.pm._unload_ended = mpv_not_answering
        negotiating = threading.Event()

        def slow_playbackinfo():
            negotiating.set()
            deadline = time.monotonic() + 5
            while (not self.bridge._shutdown.is_set()
                   and time.monotonic() < deadline):
                time.sleep(0.01)
            time.sleep(0.1)             # the shutdown's cancel lands

        self.pm.on_negotiate = slow_playbackinfo
        self.pm.queue_next = FakeVideo(B)
        self.send("next")
        worker = threading.Thread(target=self.run_plays, daemon=True)
        worker.start()
        self.assertTrue(negotiating.wait(5))
        self.bridge.shutdown_request_id = 99
        self.bridge.request_shutdown("shutdown command", 0)
        self.bridge.stop()
        worker.join(5)
        self.assertTrue(self.bridge.clean_shutdown,
                        "the hook's unload held the lock past the deadline")
        self.assertEqual(unloads, [])
        self.assertIsNone(self.pm._video, "the shutdown's own stop ended A")


class PlaySessionsTest(unittest.TestCase):
    """`bridge_state.PlaySessions` over several steps."""

    def setUp(self):
        self.sessions = bridge_state.PlaySessions()

    def test_one_outcome_per_id(self):
        s = self.sessions
        s.note("s1", A, s.OPENED)
        self.assertEqual(s.finish("s1", None, "sent"),
                         {"itemId": A, "playSessionId": "s1",
                          "outcome": "sent"})
        for _ in range(3):
            self.assertIsNone(s.finish("s1", A, "failed"))
            s.note("s1", A, s.SEEN)         # a late state: not re-opened
            self.assertEqual(s.phase("s1"), "done")
        self.assertEqual(s.unfinished(), [])

    def test_phases_only_go_up(self):
        s = self.sessions
        s.note("s1", A, s.STOPPING)
        s.note("s1", A, s.SEEN)
        s.note("s1", A, s.OPENED)
        self.assertEqual(s.phase("s1"), s.STOPPING)

    def test_no_id_no_outcome(self):
        self.assertIsNone(self.sessions.finish(None, A, "none"))
        self.sessions.note(None, A, "seen")
        self.assertEqual(self.sessions.unfinished(), [])

    def test_finished_ids_are_bounded(self):
        s = self.sessions
        for n in range(s.KEEP + 10):
            s.finish("s%d" % n, A, "sent")
        self.assertEqual(len(s._done), s.KEEP)

    def test_the_sweep(self):
        tracker = bridge_state.Tracker()
        s = tracker.sessions
        s.note("seen", A, s.SEEN)
        s.note("open", B, s.OPENED)
        s.note("stopping", C, s.STOPPING)
        tracker.finish_session("done", A, "sent")
        self.assertEqual(tracker.sweep_sessions(), 3)
        self.assertEqual(sorted((d["playSessionId"], d["outcome"])
                                for d in tracker.drain_reported()),
                         [("done", "sent"), ("open", "failed"),
                          ("seen", "none"), ("stopping", "failed")])
        self.assertEqual(tracker.sweep_sessions(), 0)
        self.assertEqual(tracker.drain_reported(), [])


class ReportPathsTest(unittest.TestCase):
    """What makes the hooks see every report, read from the player's
    source (importing ``player`` builds mpv): the Stopped request is made
    only inside ``send_timeline_stopped``, Playing only through
    ``send_timeline_initial``, and both are always called through
    ``self`` -- the instance attribute the bridge replaces -- so stop, the
    finished callback, next, previous, ``skip_to``, the restart and the
    report from bookkeeping all pass the bridge's wrappers."""

    def functions(self):
        """Module functions and class methods (a nested function counts as
        part of the one it is in)."""
        out = []
        for module in ("player.py", "player_reporting.py",
                       "player_audio.py", "player_window.py"):
            path = os.path.join(ROOT, "jellyfin_mpv_shim", module)
            with open(path, encoding="utf-8") as fh:
                tree = ast.parse(fh.read())
            for node in tree.body:
                if isinstance(node, ast.FunctionDef):
                    out.append(node)
                elif isinstance(node, ast.ClassDef):
                    out += [n for n in node.body
                            if isinstance(n, ast.FunctionDef)]
        return out

    def test_no_other_module_makes_a_stopped_request(self):
        package = os.path.join(ROOT, "jellyfin_mpv_shim")
        found = []
        for folder, _dirs, files in os.walk(package):
            for name in files:
                if name.endswith(".py"):
                    with open(os.path.join(folder, name),
                              encoding="utf-8") as fh:
                        if ".session_stop(" in fh.read():
                            found.append(name)
        self.assertEqual(found, ["player_reporting.py"])

    def calls(self, attr):
        """``(enclosing function, receiver source)`` of every call of a
        method named ``attr``."""
        found = []
        for fn in self.functions():
            for node in ast.walk(fn):
                if (isinstance(node, ast.Call)
                        and isinstance(node.func, ast.Attribute)
                        and node.func.attr == attr):
                    found.append((fn.name, ast.unparse(node.func.value)))
        return found

    def test_the_stopped_request_is_made_in_one_place(self):
        self.assertEqual({fn for fn, _ in self.calls("session_stop")},
                         {"send_timeline_stopped"})

    def test_playing_is_sent_in_one_place(self):
        self.assertEqual({fn for fn, _ in self.calls("session_playing")},
                         {"_session_playing_safe"})
        self.assertEqual({fn for fn, _ in self.calls("_session_playing_safe")},
                         {"send_timeline_initial"})

    def test_every_report_goes_through_self(self):
        for name in ("send_timeline_stopped", "send_timeline_initial"):
            with self.subTest(name=name):
                calls = self.calls(name)
                self.assertTrue(calls)
                self.assertEqual({receiver for _, receiver in calls},
                                 {"self"})


class ShutdownDeadlineTest(BridgeCase):
    def test_the_deadline_counts_from_the_request(self):
        """BRIDGE.md section 3: 8 s from the request, not from whenever the
        main thread got round to the shutdown."""
        self.bridge.request_shutdown("shutdown command", 0)
        time.sleep(0.3)
        self.bridge.stop()
        self.assertAlmostEqual(
            self.runtime.shutdown_deadline,
            self.bridge._shutdown_at + bridge_mod.SHUTDOWN_BUDGET,
            delta=0.05)
        self.assertEqual(self.bridge.shutdown_deadline,
                         self.runtime.shutdown_deadline)


class OpeningTransportTest(BridgeCase):
    """BRIDGE.md section 5: transport sent while ``opening`` is applied
    once the item has started, and a ``stop`` in between drops it."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.journal = self.runtime.journal

    def calls(self):
        """The transport calls (and the start), not the reports."""
        return [c for c in self.journal if c != ("run_action",)
                and c[0] != "report_playing"]

    def test_pause_and_seek_wait_for_the_start(self):
        self.runtime.start_plays = True
        self.send("play", {"itemIds": [A]})
        del self.journal[:]
        self.assertTrue(self.send("pause")["ok"])
        self.assertTrue(
            self.send("seek", {"positionTicks": 600_000_000})["ok"])
        self.assertTrue(self.send("seek_relative", {"seconds": 5})["ok"])
        self.assertTrue(self.send("toggle_pause")["ok"])
        self.assertEqual(self.calls(), [], "not before the start")
        self.run_plays()
        self.assertEqual(self.calls(), [
            ("start_playback", [A]), ("play", A), ("pause_if_playing",),
            ("seek", 60.0, True), ("seek", 5.0, False), ("toggle_pause",),
            ("timeline_handle",)])

    def test_they_never_land_on_an_item_the_start_did_not_open(self):
        """``seek_failed_replace``: A playing, ``play B`` whose PlaybackInfo
        fails. Nothing reaches mpv, A plays on -- and the seek sent while B
        was opening landed on A. Sent before B's video exists (``play``
        accepted) and after (B in the hook), it is B's or nothing."""
        from jellyfin_apiclient_python.exceptions import HTTPException

        self.runtime.start_plays = True
        pm = self.runtime.pm
        self.send("play", {"itemIds": [A]})
        self.run_plays()
        for when in ("accepted", "opening"):
            with self.subTest(when=when):
                pm.next_play = HTTPException(500, None)
                self.send("play", {"itemIds": [B]})
                if when == "accepted":
                    self.send("seek", {"positionTicks": 1_000_000_000})
                else:
                    pm.on_negotiate = lambda: self.elsewhere(
                        "seek", {"positionTicks": 1_000_000_000})
                del self.journal[:]
                self.run_plays()
                self.run_plays()        # what was queued during the start
                self.assertEqual(pm._video.item_id, A, "A plays on")
                self.assertIn(("play", B), self.journal)
                self.assertIn(("run_action",), self.journal,
                              "the queued seek ran, and did nothing")
                self.assertNotIn(("seek", 100.0, True), self.journal)
        # The control: a start that opens gets it.
        self.send("play", {"itemIds": [C]})
        self.send("seek", {"positionTicks": 1_000_000_000})
        del self.journal[:]
        self.run_plays()
        self.assertEqual(pm._video.item_id, C)
        self.assertIn(("seek", 100.0, True), self.journal)

    def test_the_state_goes_from_opening_to_paused_never_playing(self):
        """The start ends unpaused and the queued pause lands a moment
        later; in between, the state must not read "playing"."""
        self.runtime.start_plays = True
        pm = self.runtime.pm
        seen = []
        act = pm.run_action

        def look_then_act(fn):
            seen.append(self.bridge.tracker.snapshot(
                self.bridge._observe())["state"])
            return act(fn)

        pm.run_action = look_then_act
        self.send("play", {"itemIds": [A]})
        self.send("pause")
        self.run_plays()
        self.assertEqual(pm._video.item_id, A, "the start happened first")
        self.assertEqual(seen, ["opening"])
        self.assertEqual(self.bridge.tracker.snapshot(
            self.bridge._observe())["state"], "paused")

    def test_a_stop_in_between_drops_them(self):
        self.send("play", {"itemIds": [A]})
        self.send("pause")
        self.send("seek", {"positionTicks": 600_000_000})
        self.send("stop")
        self.run_plays()
        self.assertNotIn(("pause_if_playing",), self.journal)
        self.assertNotIn(("seek", 60.0, True), self.journal)

    def test_a_dropped_one_does_not_hold_the_state_at_opening(self):
        """Dropped by the stop, the queued pause never runs to release its
        hold; the stop has to, or the state reads "opening" for good."""
        self.send("play", {"itemIds": [A]})
        self.send("pause")
        self.send("stop")
        self.run_plays()
        for _ in range(3):
            self.assertEqual(self.bridge.tracker.snapshot(
                self.bridge._observe())["state"], "idle")

    def test_a_new_play_in_between_drops_them(self):
        self.send("play", {"itemIds": [A]})
        self.send("pause")
        self.send("play", {"itemIds": [B]})
        self.run_plays()
        self.assertEqual([c for c in self.calls()
                          if c[0] in ("start_playback", "pause_if_playing")],
                         [("start_playback", [B])])

    def test_a_start_in_progress_elsewhere_counts_as_opening(self):
        """An auto-advance starts on the action thread, not the play
        worker; the queued pause still waits for the worker."""
        self.runtime.pm._start_in_progress = True
        self.send("unpause")
        self.assertNotIn(("play_if_paused",), self.journal)
        self.runtime.pm._start_in_progress = False
        self.run_plays()
        self.assertIn(("play_if_paused",), self.journal)

    def test_with_something_playing_it_applies_at_once(self):
        """The control: nothing opening, nothing queued."""
        self.runtime.pm._video = FakeVideo(A)
        del self.journal[:]
        self.send("pause")
        self.assertEqual(self.calls(), [("pause_if_playing",)])
        self.assertTrue(self.bridge._play_q.empty())


class OneBadMessageTest(BridgeCase):
    """No single message may stop a thread: the monitor writes every event,
    the control worker every answer, the reader every line."""

    def test_a_surrogate_in_an_item_name_does_not_end_the_monitor(self):
        self.connect_server()
        self.start_threads()
        pm = self.runtime.pm
        pm._video = FakeVideo(A, name="Episode " + chr(0xD83D) + " broken")
        self.bridge.poke()
        item = self.wait_message(lambda m: m.get("event") == "item")
        self.assertIsNotNone(item, "the item event was never written")
        self.assertEqual(item["data"]["name"],
                         "Episode " + chr(0xFFFD) + " broken")
        pm._player.volume = 55.0
        self.bridge.poke()
        self.assertIsNotNone(self.wait_message(
            lambda m: m.get("event") == "state"
            and m["data"]["volume"] == 55), "later events stopped")
        self.assertTrue(self.thread("bridge-monitor").is_alive())

    def test_an_event_that_will_not_encode_does_not_end_the_monitor(self):
        self.connect_server()
        original = proto.encode_message

        def flaky(obj):
            if obj.get("event") == "item":
                raise TypeError("not JSON serializable")
            return original(obj)

        with mock.patch.object(proto, "encode_message", flaky):
            self.start_threads()
            self.runtime.pm._video = FakeVideo(A)
            self.bridge.poke()
            self.assertIsNotNone(self.wait_message(
                lambda m: m.get("event") == "tracks"),
                "the events after the bad one were lost")
        self.assertTrue(self.thread("bridge-monitor").is_alive())

    def test_an_exception_message_with_a_surrogate_is_answered(self):
        self.connect_server()

        def boom(bridge, args):
            raise RuntimeError("bad " + chr(0xD83D) + " name")

        with mock.patch.dict(bridge_mod.Bridge.HANDLERS, {"set_mute": boom}):
            answer = self.send("set_mute", {"muted": True})
        self.assertEqual(answer["error"]["code"], "internal")
        self.assertIn(chr(0xFFFD), answer["error"]["message"])

    def test_a_result_that_will_not_encode_is_still_answered(self):
        self.connect_server()
        with mock.patch.dict(bridge_mod.Bridge.HANDLERS,
                             {"set_mute": lambda b, a: {"x": float("nan")}}):
            answer = self.send("set_mute", {"muted": True})
        self.assertEqual(answer["error"]["code"], "internal")

    def test_the_control_worker_survives_and_answers_the_next(self):
        self.connect_server()
        self.start_threads()

        def boom(bridge, args):
            raise RuntimeError("bad " + chr(0xD83D) + " name")

        with mock.patch.dict(bridge_mod.Bridge.HANDLERS, {"set_mute": boom}):
            self.bridge.handle_line(b'{"id": 50, "cmd": "set_mute", '
                                    b'"args": {"muted": true}}\n')
            self.assertIsNotNone(self.wait_message(
                lambda m: m.get("id") == 50))
        self.bridge.handle_line(b'{"id": 51, "cmd": "set_volume", '
                                b'"args": {"volume": 40}}\n')
        answer = self.wait_message(lambda m: m.get("id") == 51)
        self.assertEqual(answer, {"id": 51, "ok": True, "result": {}})
        self.assertTrue(self.thread("bridge-control").is_alive())


class ShutdownDuringConnectTest(BridgeCase):
    """stdin EOF (or ``shutdown``) while the first ``connect`` is starting
    mpv: the shutdown has to see that mpv, or it outlives the backend."""

    def test_the_shutdown_waits_for_the_connect_and_sees_its_player(self):
        entered, release = threading.Event(), threading.Event()
        self.runtime.start_gate = (entered, release)
        self.addCleanup(release.set)
        self.start_threads()
        args = dict(SERVER, protocol=1, window={"mode": "standalone"})
        self.bridge.handle_line(json.dumps(
            {"id": 1, "cmd": "connect", "args": args}).encode())
        self.assertTrue(entered.wait(5), "the connect never started mpv")
        self.bridge.request_shutdown("stdin closed", 0)
        stopper = threading.Thread(target=self.bridge.stop)
        stopper.start()
        stopper.join(0.5)       # the shutdown is waiting for the connect
        release.set()
        stopper.join(10)
        self.assertFalse(stopper.is_alive())
        self.assertTrue(self.runtime.shut)
        self.assertTrue(self.runtime.shutdown_saw_player,
                        "the shutdown ran before mpv was published")
        answers = [m for m in self.messages() if m.get("id") == 1]
        self.assertEqual(len(answers), 1, "the connect is answered once")

    def test_commands_queued_behind_it_are_refused_not_run(self):
        self.connect_server()
        self.bridge.handle_line(b'{"id": 40, "cmd": "play", '
                                b'"args": {"itemIds": ["%s"]}}\n' % A.encode())
        self.bridge.request_shutdown("stdin closed", 0)
        self.bridge._control_q.put(None)
        self.bridge._control_loop()
        answer = [m for m in self.messages() if m.get("id") == 40][0]
        self.assertEqual(answer["error"]["code"], "internal")
        self.assertEqual(self.runtime.playbacks, [])


class PublishTest(unittest.TestCase):
    """`ShimRuntime._publish`: whichever of the shutdown and the start comes
    second ends the player, exactly once."""

    def test_published_after_the_shutdown_the_publisher_ends_it(self):
        runtime = bridge_mod.ShimRuntime("unused")
        self.assertIs(runtime.shutdown(), True)       # nothing to end yet
        pm = mock.Mock()
        self.assertIs(runtime._publish(pm, types.ModuleType("p")), False)
        pm.terminate.assert_called_once_with()

    def test_published_before_the_shutdown_the_shutdown_ends_it(self):
        runtime = bridge_mod.ShimRuntime("unused")
        pm = mock.Mock(_lock=threading.RLock())
        self.assertIs(runtime._publish(pm, types.ModuleType("p")), True)
        pm.terminate.assert_not_called()
        fake_player = types.ModuleType("jellyfin_mpv_shim.player")
        fake_player.bound_ipc_replies = lambda: None
        with mock.patch.dict(sys.modules,
                             {"jellyfin_mpv_shim.player": fake_player}):
            runtime.shutdown()
        pm.terminate.assert_called_once_with()


class SlowServerTest(BridgeCase):
    """A start -- here a ``next`` -- holding the player's lock across a
    server request that does not answer. A server switch must not wait on
    that lock, and its stop must not land on what plays after it."""

    def setUp(self):
        super().setUp()
        self.connect_server()
        self.pm = self.runtime.pm
        self.pm._video = FakeVideo(A)
        patcher = mock.patch.object(bridge_mod, "STOP_WAIT", 0.3,
                                    create=True)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.release = threading.Event()
        held = threading.Event()

        def hold():
            with self.pm._lock:
                held.set()
                self.release.wait(4)

        self.holder = threading.Thread(target=hold, daemon=True)
        self.holder.start()
        self.assertTrue(held.wait(5))
        self.addCleanup(self.release.set)

    def let_go(self):
        self.release.set()
        self.holder.join(5)

    def test_a_server_switch_does_not_wait_for_the_lock(self):
        started = time.monotonic()
        answer = self.connect_server()
        self.assertLess(time.monotonic() - started, 2.0)
        self.assertTrue(answer["ok"])
        self.assertTrue(self.bridge.connected)
        self.assertIsNot(self.bridge.client, self.runtime.clients[0])
        self.assertEqual(len(self.pm.deferred), 1,
                         "the stop waits for the lock on the action thread")
        self.let_go()
        self.pm.run_deferred()
        self.assertIsNone(self.pm._video, "and stops A once it has it")

    def test_a_late_stop_does_not_stop_a_newer_start(self):
        self.connect_server()
        self.let_go()
        self.runtime.start_plays = True
        self.send("play", {"itemIds": [B]})
        self.run_plays()                 # B wins the lock first
        self.pm.run_deferred()
        self.assertIsNotNone(self.pm._video)
        self.assertEqual(self.pm._video.item_id, B)

    def test_a_switch_does_not_wait_on_a_hung_progress_report(self):
        """``tl_switch``: the player's lock is free, but the timeline thread
        holds the report lock across a progress report the old server never
        answers. Run inline, the switch's stop() waited on that lock -- 30 s
        and more, the request's own timeout and retries."""
        self.let_go()
        reporting = threading.Event()

        def progress_report():
            with self.pm._tl_lock:
                reporting.set()
                self.release_report.wait(10)

        self.release_report = threading.Event()
        self.addCleanup(self.release_report.set)
        threading.Thread(target=progress_report, daemon=True).start()
        self.assertTrue(reporting.wait(5))
        switch = threading.Thread(target=self.connect_server, daemon=True)
        started = time.monotonic()
        switch.start()
        switch.join(3)
        self.assertFalse(switch.is_alive(), "the switch waited on the report")
        self.assertLess(time.monotonic() - started, 2.0)
        self.assertTrue(self.bridge.connected)
        self.assertIsNot(self.bridge.client, self.runtime.clients[0])

    def test_a_switch_waits_for_its_stop_up_to_stop_wait(self):
        """The stop is deferred; the switch still waits (bounded) for it to
        land before it talks to the new server."""
        self.addCleanup(self.pm.start_action_thread())
        threading.Timer(0.3, self.release.set).start()
        del self.runtime.journal[:]
        with mock.patch.object(bridge_mod, "STOP_WAIT", 3.0):
            started = time.monotonic()
            self.assertTrue(self.connect_server()["ok"])
        names = [c[0] for c in self.runtime.journal]
        self.assertLess(names.index("stop"), names.index("make_client"))
        self.assertGreater(time.monotonic() - started, 0.25)
        self.assertIsNone(self.pm._video)

    def test_a_switch_stop_gives_way_to_a_shutdown(self):
        """The shutdown joins the control worker inside its one budget; a
        switch waiting on its stop must not spend STOP_WAIT of it."""
        with mock.patch.object(bridge_mod, "STOP_WAIT", 5.0):
            threading.Timer(0.2, self.bridge._shutdown.set).start()
            started = time.monotonic()
            self.assertIs(self.bridge._stop_playback(blocking=True), False)
        self.assertLess(time.monotonic() - started, 1.0)

    def test_a_shutdown_is_not_marked_clean_when_mpv_was_ended_unlocked(self):
        self.runtime.shutdown_result = False
        self.bridge.request_shutdown("shutdown command", 0)
        self.let_go()
        self.bridge.stop()
        self.assertIs(self.bridge.clean_shutdown, False)


class _LockedPM:
    """What `ShimRuntime.shutdown` reaches. stop() and terminate() are
    @synchronous("_lock"), and stop()'s report takes the report lock; the
    reporter's drain is what delivers it."""

    def __init__(self):
        self._lock = threading.RLock()
        self._tl_lock = threading.RLock()
        self._player = mock.Mock()
        self._reporter = FakeReporter()
        self._start_in_progress = False
        self._video = FakeVideo(A)
        self.should_send_timeline = True
        self.calls = []

    def cancel_load(self):
        self.calls.append("cancel_load")

    def stop(self):
        with self._lock:
            video, self._video = self._video, None
            if video is not None:
                with self._tl_lock:
                    self.calls.append("stop")

    def terminate(self):
        with self._lock:
            self.stop()
            self.calls.append("terminate")


def _shim_thread():
    return mock.Mock(halt=False, trigger=threading.Event(),
                     is_alive=mock.Mock(return_value=False))


class _RuntimeCase(unittest.TestCase):
    def setUp(self):
        self.player = types.ModuleType("jellyfin_mpv_shim.player")
        #: Each bound on mpv's replies, with when it was set.
        self.bounds = []
        self.player.bound_ipc_replies = lambda seconds=5: self.bounds.append(
            (seconds, time.monotonic()))
        self.player.is_using_ext_mpv = True
        self.marked = []
        self.player._mark_own_close = self.marked.append
        patcher = mock.patch.dict(sys.modules,
                                  {"jellyfin_mpv_shim.player": self.player})
        patcher.start()
        self.addCleanup(patcher.stop)
        self.runtime, self.pm = self.make_runtime()
        self.shim_thread = self.runtime._threads[0][1]

    def make_runtime(self):
        runtime = bridge_mod.ShimRuntime("unused")
        pm = _LockedPM()
        runtime.pm = pm
        runtime.player_module = self.player
        runtime._threads = (("action thread", _shim_thread()),)
        return runtime, pm

    def hold(self, lock, seconds, after=0.0):
        """``lock`` held for ``seconds`` on a thread of its own, from
        ``after`` seconds from now (returns once it is, or will be)."""
        held = threading.Event()

        def hold():
            if after:
                time.sleep(after)
            with lock:
                held.set()
                time.sleep(seconds)

        threading.Thread(target=hold, daemon=True).start()
        if not after:
            self.assertTrue(held.wait(5))

    def shutdown(self, budget, timeout=10):
        """`shutdown` on a thread, so a regression that blocks fails the
        test instead of hanging the suite. Returns (result, seconds)."""
        box = {}

        def run():
            box["result"] = self.runtime.shutdown(
                time.monotonic() + budget)

        started = time.monotonic()
        worker = threading.Thread(target=run, daemon=True)
        worker.start()
        worker.join(timeout)
        self.assertFalse(worker.is_alive(), "the shutdown did not return")
        return box["result"], time.monotonic() - started

    def assert_degraded(self):
        self.assertNotIn("terminate", self.pm.calls)
        self.assertEqual(self.marked, [self.pm._player])
        self.pm._player.terminate.assert_called_once_with()
        # Halted, not joined: the threads are parked on the locks or in
        # the request holding them.
        self.shim_thread.join.assert_not_called()
        self.assertIs(self.shim_thread.halt, True)
        self.assertTrue(self.shim_thread.trigger.is_set())


class RuntimeShutdownTest(_RuntimeCase):
    """`ShimRuntime.shutdown` under one deadline: a slow server gets the
    ordinary shutdown (stop, final report delivered), one that does not
    answer -- whichever request it is not answering -- gets mpv ended
    without it, by the deadline. Never an orphaned mpv."""

    def test_a_free_player_is_the_ordinary_shutdown(self):
        result, _ = self.shutdown(1.0)
        self.assertIs(result, True)
        self.assertEqual(self.pm.calls, ["cancel_load", "stop", "terminate"])
        self.assertEqual(len(self.pm._reporter.drains), 1)
        self.assertGreater(self.pm._reporter.drains[0], 0.5)
        self.assertIs(self.pm.should_send_timeline, False)
        self.pm._player.terminate.assert_not_called()
        self.shim_thread.join.assert_called_once()
        self.assertLessEqual(self.shim_thread.join.call_args.args[0], 1.0,
                             "joined within the budget, not its own 15 s")
        self.assertIs(self.shim_thread.halt, True)

    def test_a_merely_slow_server_still_gets_the_ordinary_shutdown(self):
        self.hold(self.pm._lock, 0.3)
        self.hold(self.pm._tl_lock, 0.3)
        result, _ = self.shutdown(3.0)
        self.assertIs(result, True)
        self.assertEqual(self.pm.calls, ["cancel_load", "stop", "terminate"])

    def test_a_held_end_runs_after_the_stop_under_both_locks(self):
        """rr7 item 2, rr8: the bridge's ``before_final_stop`` (the end of
        an item it held for a ``play``, which stop() returns early on and
        does not report) runs inside the orderly stop -- both locks held,
        mpv's replies bounded -- right after stop(), and its report is
        delivered by the same drain. Run before stop(), an mpv that had
        stopped answering was waited out twice, past the exit watchdog
        (`HeldEndAtTheStopTest`, ``..._in_time_with_mpv_hung``)."""
        seen = []

        def held(pm):
            seen.append((pm is self.pm, pm._lock._is_owned(),
                         pm._tl_lock._is_owned(), list(self.pm.calls),
                         len(self.bounds)))

        self.runtime.before_final_stop = held
        result, _ = self.shutdown(1.0)
        self.assertIs(result, True)
        self.assertEqual(seen, [(True, True, True,
                                 ["cancel_load", "stop"], 2)])
        self.assertEqual(self.pm.calls, ["cancel_load", "stop", "terminate"])
        self.assertEqual(len(self.pm._reporter.drains), 1)

    def test_a_hung_playback_info_is_bounded(self):
        """A next holding the player's lock across PlaybackInfo."""
        self.hold(self.pm._lock, 6.0)
        result, seconds = self.shutdown(0.5)
        self.assertLess(seconds, 1.5)
        self.assertIs(result, False)
        self.assertNotIn("stop", self.pm.calls)
        self.assert_degraded()

    def test_a_hung_progress_report_is_bounded(self):
        """``tl_shutdown``: the player's lock is free, the report lock is
        held by the timeline thread inside a progress report. terminate()
        took the free lock and then waited on the report lock in stop()
        until the exit watchdog killed the process, mpv left behind."""
        self.hold(self.pm._tl_lock, 6.0)
        result, seconds = self.shutdown(0.5)
        self.assertLess(seconds, 1.5)
        self.assertIs(result, False)
        self.assertNotIn("stop", self.pm.calls)
        self.assert_degraded()

    def test_a_hung_stopped_report_is_bounded(self):
        """The locks are free and the report is queued, but the server never
        takes it: the drain gets what is left of the budget, not its own
        5 s, and terminate()'s own drain is never reached."""
        self.pm._reporter.hang = True
        result, seconds = self.shutdown(0.5)
        self.assertLess(seconds, 1.5)
        self.assertIs(result, False)
        self.assertIn("stop", self.pm.calls)
        self.assertLessEqual(self.pm._reporter.drains[0], 0.5)
        self.assert_degraded()

    def test_the_locks_share_one_deadline(self):
        """0.7 s on the player's lock, then the report lock until 1.3 s:
        each inside a 1 s of its own, not inside one 1 s between them."""
        self.hold(self.pm._lock, 0.7)
        self.hold(self.pm._tl_lock, 0.8, after=0.5)
        result, seconds = self.shutdown(1.0)
        self.assertIs(result, False)
        self.assertLess(seconds, 1.4)

    def test_libmpv_has_no_process_to_end(self):
        self.player.is_using_ext_mpv = False
        self.hold(self.pm._lock, 4.0)
        self.shutdown(0.3)
        self.pm._player.terminate.assert_not_called()

    def test_mpv_replies_get_what_is_left_of_the_deadline(self):
        """``hung_mpv_next``: a start gave the player's lock back late in
        the budget, and ``stop()`` then waited the teardown's 5 s on a reply
        from an mpv that had stopped answering -- past the exit watchdog,
        with no answer. Its replies get what is left."""
        at_stop = []
        stop = self.pm.stop

        def stop_reading_the_bound():
            at_stop.append(self.bounds[-1][0])
            stop()

        self.pm.stop = stop_reading_the_bound
        self.hold(self.pm._lock, 0.6)
        result, _seconds = self.shutdown(1.0)
        self.assertIs(result, True)
        self.assertEqual(len(at_stop), 2, "the final stop and terminate's")
        self.assertLessEqual(at_stop[0], 0.5)
        self.assertGreaterEqual(at_stop[0], bridge_mod.IPC_FLOOR)
        self.assertLessEqual(at_stop[1], at_stop[0])

    def test_terminate_is_bounded_to_what_is_left_then(self):
        """G5: the final stop and its report took their time; terminate()'s
        own stop is bounded again, to what is left by then -- not to the
        bound set before the final stop."""
        at_stop = []
        stop = self.pm.stop

        def a_slow_final_stop():
            at_stop.append(self.bounds[-1][0])
            if len(at_stop) == 1:
                time.sleep(0.6)
            stop()

        self.pm.stop = a_slow_final_stop
        self.hold(self.pm._lock, 0.6)
        result, _seconds = self.shutdown(2.0)
        self.assertIs(result, True)
        self.assertEqual(len(at_stop), 2, "the final stop and terminate's")
        self.assertLessEqual(at_stop[1], at_stop[0] - 0.4)

    def test_a_spent_deadline_still_leaves_mpv_a_moment(self):
        """G6: past the deadline a reply still gets `IPC_FLOOR`. A zero
        bound times every reply out at once, which the shim reads as mpv
        lost (`_handle_mpv_disconnect`), healthy or not."""
        bridge_mod.ShimRuntime._bound_replies(
            self.player.bound_ipc_replies, time.monotonic() - 1.0)
        self.assertGreaterEqual(self.bounds[-1][0], 0.2)


class EndMpvProcessTest(_RuntimeCase):
    """`ShimRuntime.end_mpv_process`, the exit's last step on both ways
    out: an external mpv still running is ended by its process handle, not
    over its IPC. ``hung_mpv_next``: outside a job, the mpv a watchdog exit
    left behind kept running."""

    def process(self, alive):
        process = mock.Mock()
        process.poll.return_value = None if alive else 0
        self.pm._player = types.SimpleNamespace(
            mpv_process=types.SimpleNamespace(process=process))
        return process

    def test_a_live_mpv_is_killed(self):
        process = self.process(alive=True)
        self.runtime.end_mpv_process()
        process.kill.assert_called_once_with()

    def test_an_mpv_already_gone_is_left_alone(self):
        process = self.process(alive=False)
        self.runtime.end_mpv_process()
        process.kill.assert_not_called()

    def test_libmpv_and_no_player_are_nothing_to_do(self):
        process = self.process(alive=True)
        self.player.is_using_ext_mpv = False
        self.runtime.end_mpv_process()
        process.kill.assert_not_called()
        self.runtime.pm = None
        self.runtime.end_mpv_process()

    def test_it_never_raises(self):
        process = self.process(alive=True)
        process.kill.side_effect = OSError("access denied")
        self.runtime.end_mpv_process()
        self.pm._player = object()          # no mpv_process at all
        self.runtime.end_mpv_process()


class ShutdownBudgetTest(_RuntimeCase):
    """BRIDGE.md section 3: from the request, the backend answers and exits
    within ~9 s whatever the server is not answering -- the control worker's
    join, both locks and the final report share one 8 s deadline, and the
    exit's straggler grace is cut to fit (`EXIT_SLACK`). At the real
    constants, every hung endpoint at once, in parallel."""

    def bridge_for(self, runtime):
        bridge = bridge_mod.Bridge(proto.ProtocolWriter(Sink()), runtime)
        sink = bridge.writer._stream
        return bridge, sink

    def shape(self, name):
        """A bridge whose server is not answering ``name``."""
        runtime, pm = self.make_runtime()
        bridge, sink = self.bridge_for(runtime)
        never = threading.Event()
        self.addCleanup(never.set)

        def forever(lock):
            def hold():
                with lock:
                    never.wait(30)
            threading.Thread(target=hold, daemon=True).start()

        if name == "PlaybackInfo":          # a next holding _lock
            forever(pm._lock)
        elif name == "progress":            # the timeline holding _tl_lock
            forever(pm._tl_lock)
        elif name == "Playing":             # the reporter holding _tl_lock
            forever(pm._tl_lock)
            pm._reporter.hang = True
        elif name == "Stopped":             # the final report itself
            pm._reporter.hang = True
        elif name == "item fetch":
            # The play worker inside a request; no lock held.
            bridge._play_thread = threading.Thread(
                target=bridge._play_loop, daemon=True)
            bridge._play_thread.start()
            bridge._play_q.put((0, "play", lambda: never.wait(30)))
            time.sleep(0.1)
        elif name == "Users/Me":
            self.switch_hung_on_users_me(bridge, runtime)
        return bridge, sink

    def switch_hung_on_users_me(self, bridge, runtime):
        """The control worker inside a real ``connect`` to another server
        whose ``Users/Me`` never answers -- the old one's playback stopped
        first, as a switch does. The check waits until its client is
        retired, then fails as `bridge_http.ClientGuard` fails a retired
        caller. Returns (the new client, the connect's id)."""
        retired = threading.Event()
        self.addCleanup(retired.set)
        client = mock.Mock(name="the new server's client")
        runtime.make_client = lambda server: client
        runtime.stop_client = lambda c: None

        def retire(c, grace=None):
            runtime.retired = getattr(runtime, "retired", []) + [(c, grace)]
            if c is client:
                retired.set()

        def verify(c, server):
            retired.wait(30)
            raise BridgeError(proto.SERVER_UNREACHABLE, "switched away")

        runtime.retire_client = retire
        runtime.verify_client = verify
        # The switch's stop goes to the action thread.
        runtime.pm.put_task = lambda fn, *args: threading.Thread(
            target=fn, args=args, daemon=True).start()
        bridge.window, bridge.player_info = {"mode": "standalone"}, {}
        bridge._control_thread = threading.Thread(
            target=bridge._control_loop, daemon=True)
        bridge._control_thread.start()
        bridge._control_q.put(proto.Request(5, "connect",
                                            dict(SERVER, protocol=1)))
        deadline = time.monotonic() + 5
        while bridge._verifying is not client:
            self.assertLess(time.monotonic(), deadline, "never verifying")
            time.sleep(0.01)
        return client, 5

    def test_every_hung_endpoint_answers_and_exits_within_nine_seconds(self):
        shapes = ("PlaybackInfo", "progress", "Playing", "Stopped",
                  "item fetch", "Users/Me")
        bridges = {name: self.shape(name) for name in shapes}
        results = {}

        def shut(name):
            bridge, sink = bridges[name]
            bridge.shutdown_request_id = 77
            bridge.request_shutdown("shutdown command", 0)
            started = bridge._shutdown_at
            bridge.stop()
            answered = time.monotonic() - started
            grace = bridge_mod.exit_grace(bridge.shutdown_deadline, 3.0)
            results[name] = (answered, answered + grace,
                             bridge.clean_shutdown, bytes(sink.data))

        workers = [threading.Thread(target=shut, args=(n,), daemon=True)
                   for n in shapes]
        for worker in workers:
            worker.start()
        for worker in workers:
            worker.join(15)
        for name in shapes:
            with self.subTest(endpoint=name):
                self.assertIn(name, results, "never answered")
                answered, exited, clean, out = results[name]
                answers = [json.loads(line) for line in out.splitlines()]
                self.assertIn({"id": 77, "ok": True, "result": {}}, answers)
                self.assertLess(answered, 8.6)
                self.assertLessEqual(exited, 9.05)
                self.assertIs(clean, name in ("item fetch", "Users/Me"))

    def test_a_busy_control_worker_shares_the_deadline(self):
        """The join and the player's lock are one budget, not 5 s + 8 s."""
        runtime, pm = self.make_runtime()
        bridge, sink = self.bridge_for(runtime)
        never = threading.Event()
        self.addCleanup(never.set)

        def busy(_bridge, _args):
            never.wait(30)

        with mock.patch.dict(bridge_mod.Bridge.HANDLERS, {"set_mute": busy}):
            bridge._control_thread = threading.Thread(
                target=bridge._control_loop, daemon=True)
            bridge._control_thread.start()
            bridge._control_q.put(proto.Request(1, "set_mute", {}))
            time.sleep(0.1)
            self.hold(pm._lock, 6.0)
            with mock.patch.object(bridge_mod, "SHUTDOWN_BUDGET", 0.8):
                bridge.request_shutdown("shutdown command", 0)
                bridge.stop()
        self.assertLess(time.monotonic() - bridge._shutdown_at, 1.3)
        self.assertIs(bridge.clean_shutdown, False)

    def test_a_connect_being_verified_is_abandoned(self):
        """A switch waiting on its new server's ``Users/Me``: the shutdown
        retires that client at once rather than spend the budget on it."""
        bridge, _sink = self.bridge_for(self.runtime)
        client = mock.Mock()
        retired = []
        bridge.runtime.retire_client = lambda c, grace=None: retired.append(
            (c, grace))
        bridge._verifying = client
        bridge.request_shutdown("shutdown command", 0)
        bridge.stop()
        self.assertEqual(retired, [(client, 0)])

    def test_the_abandoned_check_leaves_the_final_report_its_budget(self):
        """``verify_shutdown``, end to end: the switch stopped A, its check
        is retired, the ``connect`` answered once, the control worker free
        at once -- and the final report's drain gets the budget, so the
        clean shutdown is earned (a drain with nothing left would not
        deliver a queued report)."""
        bridge, sink = self.bridge_for(self.runtime)
        client, connect_id = self.switch_hung_on_users_me(bridge,
                                                          self.runtime)
        bridge.shutdown_request_id = 77
        bridge.request_shutdown("shutdown command", 0)
        bridge.stop()
        answered = time.monotonic() - bridge._shutdown_at
        self.assertLess(answered, 1.0)
        self.assertIs(bridge.clean_shutdown, True)
        self.assertIn((client, 0), self.runtime.retired)
        self.assertEqual(self.pm.calls[:1], ["stop"], "the switch stopped A")
        self.assertGreater(self.pm._reporter.drains[0], 7.0)
        answers = [json.loads(line) for line in bytes(sink.data).splitlines()]
        self.assertEqual(
            [(a["ok"], a["error"]["code"]) for a in answers
             if a.get("id") == connect_id],
            [(False, "server_unreachable")])
        self.assertIn({"id": 77, "ok": True, "result": {}}, answers)


class MainExitTest(unittest.TestCase):
    """`main` after the shutdown: the status, and an exit inside the
    budget."""

    def run_main(self, clean, waited=0, deadline_in=0.2):
        calls = {}
        root = logging.getLogger()
        self.addCleanup(root.setLevel, root.level)

        class Bridge:
            def __init__(self, writer, runtime, dev=False):
                self.clean_shutdown = True
                self.shutdown_reason = "test"
                self.shutdown_deadline = None

            def start(self, stdin):
                pass

            def wait(self):
                return waited

            def stop(self):
                self.clean_shutdown = clean
                self.shutdown_deadline = time.monotonic() + deadline_in

        class Runtime:
            load_ok = True

            def __init__(self, config_dir):
                pass

            def load_settings(self):
                pass

            def log_level(self):
                return logging.INFO

            def end_mpv_process(self):
                calls["mpv ended"] = True

        from jellyfin_mpv_shim import exit_watchdog

        def finish(status):
            calls["status"] = status
            calls["grace"] = exit_watchdog.GRACE_SECONDS

        def set_final_action(fn):
            calls["final action"] = fn
            calls["final before arm"] = "armed" not in calls

        def arm(*args):
            calls["armed"] = mock.call(*args)

        config = _tmpdirs.tmpdir("jms-bridge-main-")
        with mock.patch.object(proto, "isolate_stdout",
                               return_value=io.BytesIO()), \
                mock.patch.object(bridge_mod, "Bridge", Bridge), \
                mock.patch.object(bridge_mod, "ShimRuntime", Runtime), \
                mock.patch.object(bridge_mod, "preseed_shim_args"), \
                mock.patch.object(bridge_mod, "configure_logging"), \
                mock.patch.object(bridge_mod, "_install_signal_handlers"), \
                mock.patch.object(sys, "argv", ["bridge"]), \
                mock.patch.object(exit_watchdog, "GRACE_SECONDS", 3.0), \
                mock.patch.object(exit_watchdog, "set_final_action",
                                  set_final_action), \
                mock.patch.object(exit_watchdog, "arm", arm), \
                mock.patch.object(exit_watchdog, "finish", finish):
            bridge_mod.main(["--config-dir", config])
        return calls

    def test_a_clean_shutdown_exits_zero(self):
        self.assertEqual(self.run_main(clean=True)["status"], 0)

    def test_an_unclean_shutdown_exits_one(self):
        """BRIDGE.md section 3: a hung server costs the final report, and
        the exit status says so."""
        self.assertEqual(self.run_main(clean=False)["status"], 1)

    def test_a_status_already_set_is_kept(self):
        self.assertEqual(self.run_main(clean=False, waited=1)["status"], 1)

    def test_the_straggler_grace_fits_the_budget(self):
        calls = self.run_main(clean=False, deadline_in=-0.8)
        self.assertLessEqual(calls["grace"], bridge_mod.EXIT_SLACK - 0.7)
        self.assertGreaterEqual(calls["grace"], 0.0)
        calls = self.run_main(clean=False, deadline_in=-5.0)
        self.assertEqual(calls["grace"], 0.0)
        calls = self.run_main(clean=True, deadline_in=10.0)
        self.assertEqual(calls["grace"], 3.0, "never more than its own")
        self.assertEqual(calls["armed"], mock.call(
            bridge_mod.SHUTDOWN_BUDGET + bridge_mod.EXIT_SLACK + 0.5))

    def test_both_ways_out_end_a_live_mpv(self):
        """The watchdog's forced exit as well as the orderly finish run
        `exit_watchdog`'s final action: registered before the watchdog is
        armed, it is the runtime's `end_mpv_process`."""
        calls = self.run_main(clean=False)
        self.assertIs(calls["final before arm"], True)
        calls["final action"]()
        self.assertIs(calls.get("mpv ended"), True)


class UncaughtExceptionTest(unittest.TestCase):
    """An exception nothing caught goes through the redacting log -- into
    the log file, redacted -- not to stderr raw (Python's own hooks)."""

    SECRET = "0123456789abcdef0123456789abcdef"

    def setUp(self):
        root = logging.getLogger()
        saved = (list(root.handlers), root.level, threading.excepthook,
                 sys.excepthook)

        def restore():
            for handler in list(root.handlers):
                root.removeHandler(handler)
                handler.close()
            for handler in saved[0]:
                root.addHandler(handler)
            root.setLevel(saved[1])
            threading.excepthook, sys.excepthook = saved[2], saved[3]

        self.addCleanup(restore)
        self.addCleanup(proto._secrets.clear)
        proto.add_secret(self.SECRET)
        self.log_path = os.path.join(_tmpdirs.tmpdir("jms-bridge-log-"),
                                     "bridge.log")
        self.stderr = io.StringIO()
        with mock.patch.object(sys, "stderr", self.stderr):
            bridge_mod.configure_logging(self.log_path)

    def log_text(self):
        for handler in logging.getLogger().handlers:
            handler.flush()
        with open(self.log_path, encoding="utf-8") as fh:
            return fh.read()

    def test_a_thread_exception_is_logged_redacted(self):
        def fail():
            raise ValueError('Token="%s" in a message' % self.SECRET)

        with mock.patch.object(sys, "stderr", self.stderr):
            worker = threading.Thread(target=fail, name="bridge-worker-x")
            worker.start()
            worker.join(5)
        text = self.log_text()
        self.assertIn("Uncaught exception in bridge-worker-x", text)
        self.assertIn("ValueError", text)
        self.assertNotIn(self.SECRET, text)
        self.assertNotIn(self.SECRET, self.stderr.getvalue())

    def test_a_main_thread_exception_is_logged_redacted(self):
        try:
            raise KeyError("api_key=%s" % self.SECRET)
        except KeyError:
            with mock.patch.object(sys, "stderr", self.stderr):
                sys.excepthook(*sys.exc_info())
        text = self.log_text()
        self.assertIn("Uncaught exception", text)
        self.assertNotIn(self.SECRET, text)
        self.assertNotIn(self.SECRET, self.stderr.getvalue())


class VerifyAgainstTheApiclientTest(unittest.TestCase):
    """`verify_client` through the apiclient's real request path, the
    session answering as a server would. ``GET /Users/Me`` names the
    token's own user; a 500 comes back from the apiclient as None, which is
    not success."""

    def setUp(self):
        self.runtime = bridge_mod.ShimRuntime("unused")
        self.server = proto.validate_connect(
            dict(SERVER, protocol=1))["server"]
        self.client = self.runtime.make_client(self.server)
        self.addCleanup(self.client.stop)
        self.addCleanup(proto._secrets.clear)

    def answer(self, status, body=None, raw=None):
        import requests

        response = requests.models.Response()
        response.status_code = status
        if raw is None:
            raw = b"" if body is None else json.dumps(body).encode()
        response._content = raw
        response.encoding = "utf-8"
        response.headers["Date"] = "Fri, 13 Nov 2026 00:00:00 GMT"
        response.url = self.server["url"] + "/Users/Me"
        response.elapsed = datetime.timedelta(milliseconds=1)
        return response

    def verify(self, response=None, error=None):
        with mock.patch.object(self.client.http.session, "get",
                               return_value=response,
                               side_effect=error) as get:
            try:
                self.runtime.verify_client(self.client, self.server)
            except BridgeError as failure:
                return failure.code, get
            return None, get

    def test_the_users_own_id_passes(self):
        code, get = self.verify(self.answer(200, {"Id": SERVER["user"]["id"]}))
        self.assertIsNone(code)
        self.assertTrue(get.call_args.kwargs["url"].endswith("/Users/Me"))
        self.assertEqual(get.call_args.kwargs["timeout"],
                         bridge_mod.VERIFY_TIMEOUT)
        dashed = "01234567-89AB-CDEF-0123-456789ABCDEF"
        self.assertIsNone(self.verify(self.answer(200, {"Id": dashed}))[0],
                          "ids compare case- and dash-insensitively")

    def test_another_users_token_is_auth_failed(self):
        self.assertEqual(self.verify(self.answer(200, {"Id": "f" * 32}))[0],
                         "auth_failed")

    def test_401_and_403_are_auth_failed(self):
        for status in (401, 403):
            with self.subTest(status=status):
                self.assertEqual(self.verify(self.answer(status))[0],
                                 "auth_failed")

    def test_a_500_is_not_success(self):
        self.assertEqual(self.verify(self.answer(500))[0],
                         "server_unreachable")

    def test_5xx_timeouts_and_no_connection_are_unreachable(self):
        import requests

        for name, response, error in (
                ("503", self.answer(503), None),
                ("not json", self.answer(200, raw=b"<html>"), None),
                ("refused", None, requests.exceptions.ConnectionError("no")),
                ("timeout", None, requests.exceptions.ReadTimeout("slow"))):
            with self.subTest(name):
                self.assertEqual(self.verify(response, error)[0],
                                 "server_unreachable")


class _RealClientCase(unittest.TestCase):
    """A real apiclient client made by `ShimRuntime.make_client`, its
    session answering as a server would."""

    def setUp(self):
        self.runtime = bridge_mod.ShimRuntime("unused")
        self.server = proto.validate_connect(
            dict(SERVER, protocol=1))["server"]
        self.client = self.runtime.make_client(self.server)
        self.addCleanup(self.client.stop)
        self.addCleanup(proto._secrets.clear)
        self.session = self.client.http.session

    def answer(self, status=204):
        import requests

        response = requests.models.Response()
        response.status_code = status
        response._content = b""
        response.headers["Date"] = "Fri, 13 Nov 2026 00:00:00 GMT"
        response.url = self.server["url"] + "/Sessions/Playing/Progress"
        response.elapsed = datetime.timedelta(milliseconds=1)
        return response


class ClientGuardTest(_RealClientCase):
    """`bridge_http.ClientGuard` on a real apiclient client."""

    def test_a_retried_post_stays_a_post(self):
        """The apiclient pops the method inside its retry loop
        (``http.py:149``): the first attempt took it, the retry went out as
        a GET -- body and all. A progress report that timed out once became
        ``GET /Sessions/Playing/Progress``."""
        import requests

        body = {"ItemId": A, "PositionTicks": 10}
        attempts = []

        def post(**kwargs):
            attempts.append(("POST", kwargs.get("json")))
            if len(attempts) == 1:
                raise requests.exceptions.ReadTimeout("slow")
            return self.answer()

        def get(**kwargs):
            attempts.append(("GET", kwargs.get("json")))
            return self.answer()

        with mock.patch.object(self.session, "post", side_effect=post), \
                mock.patch.object(self.session, "get", side_effect=get), \
                mock.patch("time.sleep"):
            self.client.jellyfin.session_progress(body)
        self.assertEqual(attempts, [("POST", body), ("POST", body)])

    def test_every_retry_keeps_its_method(self):
        import requests

        attempts = []

        def delete(**kwargs):
            attempts.append("DELETE")
            if len(attempts) < 3:
                raise requests.exceptions.ConnectionError("reset")
            return self.answer()

        with mock.patch.object(self.session, "delete", side_effect=delete), \
                mock.patch.object(self.session, "get") as get, \
                mock.patch("time.sleep"):
            self.client.jellyfin._http("DELETE", "Videos/ActiveEncodings",
                                       {"params": {}})
        self.assertEqual(attempts, ["DELETE"] * 3)
        get.assert_not_called()

    def test_a_retired_client_lets_go_of_a_request_in_flight(self):
        """The old server's progress report, hung, held the report lock:
        once the client is retired its caller gets ServerUnreachable within
        the grace, and the request's own retries are refused."""
        from jellyfin_apiclient_python.exceptions import HTTPException
        import requests

        inside, letgo = threading.Event(), threading.Event()
        self.addCleanup(letgo.set)
        attempts = []

        def post(**kwargs):
            attempts.append(kwargs.get("timeout"))
            inside.set()
            letgo.wait(10)
            raise requests.exceptions.ReadTimeout("hung")

        caught = []

        def report():
            try:
                self.client.jellyfin.session_progress({"ItemId": A})
            except HTTPException as error:
                caught.append(error.status)

        with mock.patch.object(self.session, "post", side_effect=post):
            caller = threading.Thread(target=report, daemon=True)
            caller.start()
            self.assertTrue(inside.wait(5))
            retired = time.monotonic()
            self.runtime.retire_client(self.client, 0.2)
            caller.join(5)
            self.assertFalse(caller.is_alive())
            self.assertLess(time.monotonic() - retired, 1.0)
            self.assertEqual(caught, ["ServerUnreachable"])
            letgo.set()
            time.sleep(1.5)     # the apiclient's own sleep before a retry
        self.assertEqual(len(attempts), 1, "no retry after the switch")

    def test_a_retired_client_still_reaches_a_healthy_server_once(self):
        """The Stopped report a switch sends to the old server: one bounded
        attempt, delivered when that server answers."""
        self.runtime.retire_client(self.client)
        with mock.patch.object(self.session, "post",
                               return_value=self.answer()) as post:
            self.client.jellyfin.session_stop({"ItemId": A})
        self.assertEqual(post.call_count, 1)
        self.assertLessEqual(post.call_args.kwargs["timeout"],
                             bridge_http.RETIRED_TIMEOUT)

    def test_a_retired_client_makes_one_attempt_with_no_retry(self):
        """Its own failure, at once: not the apiclient's one-second sleep
        before a retry that would then be refused."""
        from jellyfin_apiclient_python.exceptions import HTTPException
        import requests

        self.runtime.retire_client(self.client)
        started = time.monotonic()
        with mock.patch.object(
                self.session, "post",
                side_effect=requests.exceptions.ReadTimeout("slow")) as post:
            with self.assertRaises(HTTPException) as caught:
                self.client.jellyfin.session_stop({"ItemId": A})
        self.assertEqual(caught.exception.status, "ReadTimeout")
        self.assertLess(time.monotonic() - started, 0.8)
        self.assertEqual(post.call_count, 1)

    def test_a_retired_client_gives_up_on_a_hung_server(self):
        from jellyfin_apiclient_python.exceptions import HTTPException

        letgo = threading.Event()
        self.addCleanup(letgo.set)

        def post(**kwargs):
            letgo.wait(10)
            return self.answer()

        self.runtime.retire_client(self.client, 0.2)
        started = time.monotonic()
        with mock.patch.object(self.session, "post", side_effect=post):
            with self.assertRaises(HTTPException):
                self.client.jellyfin.session_stop({"ItemId": A})
        self.assertLess(time.monotonic() - started, 1.0)

    def test_a_request_made_after_the_retirement_gets_its_own_grace(self):
        """The Stopped report of a switch's stop goes to the old server
        after the retirement -- once a hung progress report has let go of
        the report lock, up to the grace later. Counted from the
        retirement, its grace was gone before it was sent."""
        self.runtime.retire_client(self.client, 0.6)
        time.sleep(0.9)

        def post(**kwargs):
            time.sleep(0.1)
            return self.answer()

        with mock.patch.object(self.session, "post",
                               side_effect=post) as sent:
            self.client.jellyfin.session_stop({"ItemId": A})
        self.assertEqual(sent.call_count, 1)

    def test_the_request_thread_never_keeps_the_process_alive(self):
        """A request to a hung server outlives its caller: a daemon, so the
        exit does not wait for it (`exit_watchdog.finish` waits out
        non-daemon threads, and reports them as leaks)."""
        inside, letgo = threading.Event(), threading.Event()
        self.addCleanup(letgo.set)

        def post(**kwargs):
            inside.set()
            letgo.wait(5)
            return self.answer()

        with mock.patch.object(self.session, "post", side_effect=post):
            caller = threading.Thread(
                target=self.client.jellyfin.session_stop, args=({"ItemId": A},),
                daemon=True)
            caller.start()
            self.assertTrue(inside.wait(5))
            helpers = [t for t in threading.enumerate()
                       if t.name == "bridge-http"]
            letgo.set()
            caller.join(5)
        self.assertTrue(helpers)
        self.assertTrue(all(t.daemon for t in helpers))


class StopOutcomeTest(_RealClientCase):
    """`bridge_http.watch_stops` on a real apiclient client: the outcome of
    each Stopped report from the HTTP status of its last attempt. The
    apiclient answers None for a 204 and for a 500 alike."""

    def setUp(self):
        super().setUp()
        self.outcomes = []
        self.runtime.on_stop_outcome = lambda *a: self.outcomes.append(a)
        self.body = {"ItemId": A, "PlaySessionId": "ps-1"}

    def stop_with(self, post):
        from jellyfin_apiclient_python.exceptions import HTTPException

        with mock.patch.object(self.session, "post", side_effect=post), \
                mock.patch("time.sleep"):
            try:
                self.client.jellyfin.session_stop(self.body)
            except HTTPException:
                pass
        return self.outcomes[-1] if self.outcomes else None

    def test_the_status_decides(self):
        import requests

        for name, post, outcome in (
                ("204", lambda **k: self.answer(204), "sent"),
                ("200", lambda **k: self.answer(200), "sent"),
                ("500", lambda **k: self.answer(500), "failed"),
                ("401", lambda **k: self.answer(401), "failed"),
                ("404", lambda **k: self.answer(404), "failed"),
                ("no connection", requests.exceptions.ConnectionError("no"),
                 "failed"),
                ("timeout", requests.exceptions.ReadTimeout("slow"),
                 "failed")):
            with self.subTest(name):
                self.assertEqual(self.stop_with(post),
                                 (A, "ps-1", outcome))

    def test_the_last_attempt_counts(self):
        """A 502 is retried: the answer to the retry is the outcome."""
        answers = [self.answer(502), self.answer(204)]
        self.assertEqual(self.stop_with(lambda **k: answers.pop(0)),
                         (A, "ps-1", "sent"))

    def test_a_retired_client_giving_up_is_failed(self):
        letgo = threading.Event()
        self.addCleanup(letgo.set)

        def hung(**kwargs):
            letgo.wait(10)
            return self.answer(204)

        self.runtime.retire_client(self.client, 0.2)
        started = time.monotonic()
        self.assertEqual(self.stop_with(hung), (A, "ps-1", "failed"))
        self.assertLess(time.monotonic() - started, 1.5)

    def test_a_retired_client_reaching_its_server_is_sent(self):
        self.runtime.retire_client(self.client)
        self.assertEqual(self.stop_with(lambda **k: self.answer(204)),
                         (A, "ps-1", "sent"))

    def test_the_status_is_the_calling_threads_own(self):
        """Two reports in flight on two threads -- the worker's and an
        inline one after the worker stopped -- each get their own answer."""
        gate = threading.Barrier(2, timeout=5)

        def post(**kwargs):
            gate.wait()             # both requests answered together
            return self.answer(500 if kwargs["json"]["PlaySessionId"]
                               == "ps-500" else 204)

        def report(session):
            self.client.jellyfin.session_stop(
                {"ItemId": A, "PlaySessionId": session})

        guard = self.client.bridge_guard
        read = guard.last_status

        def read_late():
            time.sleep(0.2)         # both answers are in by now
            return read()

        guard.last_status = read_late
        with mock.patch.object(self.session, "post", side_effect=post):
            threads = [threading.Thread(target=report, args=(s,),
                                        daemon=True)
                       for s in ("ps-500", "ps-204")]
            for thread in threads:
                thread.start()
            for thread in threads:
                thread.join(5)
        self.assertEqual(sorted(self.outcomes),
                         [(A, "ps-204", "sent"), (A, "ps-500", "failed")])

    def test_a_callback_that_raises_does_not_take_the_report(self):
        def broken(*args):
            raise RuntimeError("no")

        self.runtime.on_stop_outcome = broken
        with mock.patch.object(self.session, "post",
                               return_value=self.answer(204)) as post:
            self.client.jellyfin.session_stop(self.body)
        self.assertEqual(post.call_count, 1)


class PlaybackInfoAnswerTest(_RealClientCase):
    """rr6 item 7, `bridge_http.check_play_info` on a real apiclient
    client: **the apiclient answers None for a 500** (and for an empty or
    non-JSON body), and `Video.get_playback_url` stored that and
    dereferenced it -- a TypeError the bridge could only call ``internal``,
    "'NoneType' object is not subscriptable", on a restart or a next.
    BRIDGE.md section 5: ``server_unreachable``, with a readable message."""

    def info(self, status, body=None, raw=None):
        import requests

        response = requests.models.Response()
        response.status_code = status
        if raw is None:
            raw = b"" if body is None else json.dumps(body).encode()
        response._content = raw
        response.encoding = "utf-8"
        response.headers["Date"] = "Fri, 13 Nov 2026 00:00:00 GMT"
        response.url = self.server["url"] + "/Items/%s/PlaybackInfo" % A
        response.elapsed = datetime.timedelta(milliseconds=1)
        with mock.patch.object(self.session, "post",
                               return_value=response) as post, \
                mock.patch("time.sleep"):
            result = self.client.jellyfin.get_play_info(A, {"Name": "p"})
        self.assertTrue(post.call_args.kwargs["url"].endswith(
            "/Items/%s/PlaybackInfo" % A))
        return result

    def test_an_answer_that_is_not_one_is_server_unreachable(self):
        from jellyfin_apiclient_python.exceptions import HTTPException

        answered = "the server answered with an error"
        unusable = "the server's answer could not be used"
        for name, status, body, raw, says, user in (
                ("500", 500, None, None, "HTTP 500", answered),
                ("empty", 200, None, b"", "empty or not JSON", unusable),
                ("not json", 200, None, b"<html>", "empty or not JSON",
                 unusable),
                ("no sources", 200, {"PlaySessionId": "p"}, None,
                 "no media sources", unusable)):
            with self.subTest(name):
                with self.assertRaises(HTTPException) as caught:
                    self.info(status, body, raw)
                error = caught.exception
                self.assertEqual(bridge_mod.classify_exception(error),
                                 "server_unreachable")
                message = bridge_mod.describe_exception(error)
                self.assertIn("PlaybackInfo", message)
                self.assertIn(says, message)
                self.assertNotIn("NoneType", message)
                # fluentfin-mpv: what the user is told (the log has the
                # above): the server did answer.
                self.assertEqual(bridge_mod.failure_cause(
                    "server_unreachable", error), user)

    def test_only_the_bridges_own_sentence_is_shown(self):
        """`describe_exception`: the apiclient's HTTPException carries the
        requests error, which names the url -- not shown; the bridge's own
        carries a sentence -- shown."""
        import requests
        from jellyfin_apiclient_python.exceptions import HTTPException

        theirs = HTTPException("ServerUnreachable", requests.ConnectionError(
            "http://jf.example/Items?ApiKey=tok-abcdef-0123456789"))
        ours = HTTPException("ServerUnreachable",
                             "the server answered PlaybackInfo with HTTP 500")
        self.assertEqual(bridge_mod.describe_exception(theirs),
                         "ServerUnreachable (HTTPException)")
        self.assertEqual(bridge_mod.describe_exception(ours),
                         "ServerUnreachable: the server answered PlaybackInfo "
                         "with HTTP 500")

    def test_an_answer_passes_as_it_came(self):
        """``MediaSources: []`` is an answer (nothing to play: the start's
        own "no URL" path), not a server that did not answer."""
        for body in ({"MediaSources": [], "PlaySessionId": "p"},
                     {"MediaSources": [{"Id": "s"}], "PlaySessionId": "p"}):
            with self.subTest(sources=len(body["MediaSources"])):
                self.assertEqual(self.info(200, body), body)


class ErrorClearingTest(BridgeCase):
    """BRIDGE.md section 5: ``state: "error"`` clears on the next
    ``play``, ``play_file``, ``stop`` or a successful ``connect`` that
    includes a server -- and on nothing else."""

    dev = True

    def setUp(self):
        super().setUp()
        self.assertTrue(self.connect()["ok"])        # window only (--dev)

    def fail_something(self):
        self.bridge.tracker.report_error("mpv_failed", "could not open",
                                         item_id=A)
        self.assertEqual(self.state(), "error")

    def state(self):
        return self.bridge.tracker.snapshot(
            self.bridge._observe(cheap=True))["state"]

    def test_what_leaves_it(self):
        self.fail_something()
        for _ in range(3):
            self.assertTrue(self.connect()["ok"])    # window-only again
            self.assertEqual(self.state(), "error")
        self.runtime.verify_error = BridgeError(proto.AUTH_FAILED, "401")
        self.assertEqual(self.code("connect", dict(
            SERVER, protocol=1, window={"mode": "standalone"})),
            "auth_failed")
        self.assertEqual(self.state(), "error", "a failed connect")
        for cmd, args in (("set_volume", {"volume": 5}),
                          ("set_mute", {"muted": True}),
                          ("set_fullscreen", {"fullscreen": True}),
                          ("pause", None), ("next", None)):
            self.send(cmd, args)
            self.assertEqual(self.state(), "error", cmd)

    def test_what_clears_it(self):
        self.runtime.verify_error = None
        for cmd, args in (("stop", None),
                          ("play_file", {"url": "av://lavfi:testsrc"}),
                          ("connect", dict(SERVER, protocol=1)),
                          ("play", {"itemIds": [A]})):
            with self.subTest(cmd=cmd):
                self.fail_something()
                self.assertTrue(self.send(cmd, args)["ok"])
                self.assertIsNone(self.bridge.tracker.error)
                self.assertNotEqual(self.state(), "error")
                self.send("stop")
                self.run_plays()


class AttachedWindowTest(BridgeCase):
    """fluentfin-mpv player controls: ``connect.window`` ``attached``, the
    UI's own mpv on a pipe (BRIDGE.md section 4)."""

    ATTACHED = {"mode": "attached", "ipc": "fluentfin-mpv-1-0011223344556677"}

    def test_it_reaches_the_runtime_and_the_state_and_is_fixed(self):
        self.assertTrue(self.connect_server(self.ATTACHED)["ok"])
        self.assertEqual(self.runtime.started,
                         [{"mode": "attached", "hwnd": None,
                           "ipc": "fluentfin-mpv-1-0011223344556677"}])
        self.assertEqual(self.send("get_state")["result"]["window"],
                         "attached")
        # another pipe is another window: the UI restarts the backend
        self.assertEqual(self.code("connect", dict(
            SERVER, protocol=1, window=dict(self.ATTACHED, ipc="other"))),
            "window_locked")
        self.assertEqual(len(self.runtime.started), 1)

    def test_full_screen_is_the_uis_as_when_embedded(self):
        self.connect_server(self.ATTACHED)
        self.assertTrue(self.send("set_fullscreen",
                                  {"fullscreen": True})["ok"])
        self.assertIn(("set_embedded_fullscreen", True),
                      self.runtime.journal)
        window_embed.ui_fullscreen = True
        self.addCleanup(setattr, window_embed, "ui_fullscreen", False)
        self.assertIs(self.send("get_state")["result"]["fullscreen"], True)


class AttachedRuntimeTest(unittest.TestCase):
    """`ShimRuntime.start_player` for an ``attached`` window: the shim's
    external backend on the UI's pipe, never starting an mpv, for the run
    only. The player module is a stand-in (importing the real one builds a
    real mpv, docs/testing.md section 1)."""

    def test_the_external_backend_attaches_to_the_uis_pipe(self):
        import jellyfin_mpv_shim
        from jellyfin_mpv_shim.conf import settings

        keys = ([k for k, _v in bridge_mod.ShimRuntime.EMBEDDED_OVERRIDES]
                + [k for k, _v in
                   bridge_mod.ShimRuntime.attached_overrides("p")])
        for key in keys:
            self.addCleanup(setattr, settings, key, getattr(settings, key))
        self.addCleanup(window_embed.configure, None)
        # start_player also switches these on, for the life of a backend
        from jellyfin_mpv_shim import fluentfin_osc

        self.addCleanup(fluentfin_osc.configure, fluentfin_osc.active())
        self.addCleanup(logging.getLogger().setLevel,
                        logging.getLogger().level)
        on_disk = settings.dict()

        seen = {}
        pm = mock.Mock()
        pm._player.mpv_version = "mpv 0.42.0"

        class Player(types.ModuleType):
            is_using_ext_mpv = True

            @property
            def playerManager(self):
                # what the real import reads, at the moment it would build mpv
                seen.update({k: getattr(settings, k) for k in keys})
                seen["embedded"] = (window_embed.active(),
                                    window_embed.attached, window_embed.hwnd)
                return pm

        fake_player = Player("jellyfin_mpv_shim.player")
        stubs = {"jellyfin_mpv_shim.player": fake_player,
                 "jellyfin_mpv_shim.action_thread": mock.Mock(),
                 "jellyfin_mpv_shim.timeline": mock.Mock()}
        runtime = bridge_mod.ShimRuntime("unused")
        with mock.patch.dict(sys.modules, stubs), \
                mock.patch.object(jellyfin_mpv_shim, "player", fake_player,
                                  create=True), \
                mock.patch.object(jellyfin_mpv_shim, "action_thread",
                                  stubs["jellyfin_mpv_shim.action_thread"],
                                  create=True), \
                mock.patch.object(jellyfin_mpv_shim, "timeline",
                                  stubs["jellyfin_mpv_shim.timeline"],
                                  create=True):
            info = runtime.start_player(
                {"mode": "attached", "hwnd": None, "ipc": "the-pipe"})

        self.assertEqual(info, {"mpv": "mpv 0.42.0",
                                "mpvBackend": "external"})
        self.assertIs(seen["mpv_ext"], True)
        self.assertIs(seen["mpv_ext_start"], False, "never starts an mpv")
        self.assertEqual(seen["mpv_ext_ipc"], "the-pipe")
        self.assertIs(seen["mpv_ext_no_ovr"], False)
        self.assertIs(seen["fullscreen"], False, "the UI's window")
        self.assertIs(seen["raise_mpv"], False)
        self.assertEqual(seen["embedded"], (True, True, None))
        for key in keys:
            self.assertEqual(settings.dict()[key], on_disk[key],
                             "%s reached conf.json" % key)


class DefaultsTest(unittest.TestCase):
    def test_default_config_dir_is_the_forks_own(self):
        with mock.patch.dict(os.environ, {"LOCALAPPDATA": r"C:\L",
                                          "XDG_CONFIG_HOME": "/x"}):
            path = bridge_mod.default_config_dir()
        self.assertEqual(os.path.basename(path), "mpv-backend")
        self.assertEqual(os.path.basename(os.path.dirname(path)),
                         "Seaglass")

    def test_the_shim_parser_never_sees_our_argv(self):
        from jellyfin_mpv_shim import args as shim_args

        saved = shim_args._args
        self.addCleanup(setattr, shim_args, "_args", saved)
        with mock.patch.object(sys, "argv",
                               ["bridge", "--config-dir", "x", "--dev"]):
            bridge_mod.preseed_shim_args("C:/cfg")
            got = shim_args.get_args()
        self.assertEqual(got.config, "C:/cfg")
        self.assertIs(got.enable_gui, False)


if __name__ == "__main__":
    unittest.main()
