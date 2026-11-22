"""1.6 skip: the margin. A show's extras are not the same length in every
episode, so a skip rule's edge -- where the episode starts again after a
start rule, where the extra begins in an end rule -- snaps to the nearest
black frame or silence within a few seconds of where the rule puts it.

Only that stretch is read, and only from what mpv has already read ahead:
mpv's ``dump-cache`` writes the few seconds around the edge (a few MB) from
its demuxer cache to a temporary file, never asking the server for anything,
and an ``mpv`` with no window and no sound (``--vo=null --ao=null``) runs
FFmpeg's ``blackdetect`` and ``silencedetect`` over that file. The file is
deleted at once. Nothing happens when mpv has not cached the stretch (a
local file, a cache too short), when no mpv binary is found, or when nothing
black or silent is near: the rule's own edge stands.

Nothing here raises into playback; every worker catches and logs.
"""

import atexit
import hashlib
import logging
import os
import re
import shutil
import subprocess
import tempfile
import threading
import time

log = logging.getLogger("skip_snap")

#: How far either side of a rule's edge a black frame or silence is looked
#: for (seconds); half that after the viewer corrected the rule by hand.
WINDOW = 3.0
CORRECTED_WINDOW = 1.5
#: An end rule's edge is watched for this long before playback reaches it.
END_LOOKAHEAD = 300.0
#: How often a worker asks mpv how far its cache reaches.
POLL = 1.0
#: The longest a worker waits for the cache, and the analysis may take.
MAX_WAIT = 600.0
ANALYSE_TIMEOUT = 15.0
#: Black (pixels under 10 % of white, for at least 80 ms) and silence (under
#: -50 dB, for at least 150 ms).
BLACK_FILTER = "blackdetect=d=0.08:pix_th=0.10"
SILENCE_FILTER = "silencedetect=n=-50dB:d=0.15"
#: Where in a gap the edge goes: a start rule's skip lands this much before
#: the gap's end (the episode's first frame is never cut); an end rule's
#: part starts this much after the gap's start.
LEAD = 0.25
#: A cut between an extra and the episode is short: a black or silent
#: stretch longer than this (a quiet scene, a dark one) is not one, nor one
#: that runs into either end of what was read (it may go on beyond it; the
#: fake server's digital silence did, and the edge moved 1.8 s for nothing).
MAX_GAP = 2.5
EDGE_MARGIN = 0.05

_DURATION_SCRIPT = (
    'mp.register_event("file-loaded", function() '
    'mp.msg.info("nixie-duration=" .. tostring('
    'mp.get_property_native("duration"))) end)\n')

_BLACK = re.compile(r"black_start:\s*([\d.]+)\s+black_end:\s*([\d.]+)")
_SILENCE_START = re.compile(r"silence_start:\s*(-?[\d.]+)")
_SILENCE_END = re.compile(r"silence_end:\s*(-?[\d.]+)")
_DURATION = re.compile(r"nixie-duration=([\d.]+)")


def edge_of(segment):
    """``(edge, window, anchor)`` for a rule's segment: the end of a start
    rule, the start of an end rule."""
    anchor = getattr(segment, "anchor", "start")
    window = CORRECTED_WINDOW if getattr(segment, "corrected", False) \
        else WINDOW
    edge = getattr(segment, "rule_end", segment.end) if anchor == "start" \
        else getattr(segment, "rule_start", segment.start)
    return float(edge), window, anchor


def parse_detections(text):
    """``(blacks, silences, duration)`` from the analysing mpv's log: lists
    of ``(start, end)`` in the dumped file's own seconds."""
    blacks = [(float(a), float(b)) for a, b in _BLACK.findall(text)]
    silences = []
    start = None
    for line in text.splitlines():
        found = _SILENCE_START.search(line)
        if found:
            start = max(0.0, float(found.group(1)))
            continue
        found = _SILENCE_END.search(line)
        if found and start is not None:
            silences.append((start, float(found.group(1))))
            start = None
    duration = None
    found = _DURATION.search(text)
    if found:
        try:
            duration = float(found.group(1))
        except ValueError:
            duration = None
    return blacks, silences, duration


def choose_edge(blacks, silences, edge, window, anchor, read=None):
    """The snapped edge (seconds of the item), or None for none near.

    Gaps are black stretches and silences; where both meet, their overlap,
    which wins over either alone (a cut between a bumper and the show is
    usually both). Within ``window`` of ``edge``, the gap nearest it. Not a
    gap longer than `MAX_GAP`, nor one touching either end of ``read`` (the
    stretch analysed, ``(start, end)``)."""
    def cut(start, end):
        if end - start > MAX_GAP:
            return False
        if read is not None and (start <= read[0] + EDGE_MARGIN
                                 or end >= read[1] - EDGE_MARGIN):
            return False
        return True

    blacks = [(a, b) for a, b in blacks if cut(a, b)]
    silences = [(a, b) for a, b in silences if cut(a, b)]
    gaps = []
    for b_start, b_end in blacks:
        for s_start, s_end in silences:
            start, end = max(b_start, s_start), min(b_end, s_end)
            if end - start >= 0.05:
                gaps.append((0, start, end))
    gaps += [(1, a, b) for a, b in blacks + silences if b > a]
    best = None
    for rank, start, end in gaps:
        length = end - start
        if anchor == "start":
            point = end - min(LEAD, length / 2)
        else:
            point = start + min(LEAD, length / 2)
        distance = abs(point - edge)
        if distance > window:
            continue
        score = (rank, distance)
        if best is None or score < best[0]:
            best = (score, point)
    return None if best is None else round(best[1], 2)


def covered(cache_state, start, end):
    """Whether mpv's ``demuxer-cache-state`` holds ``start``..``end``."""
    try:
        for entry in (cache_state or {}).get("seekable-ranges") or ():
            if entry.get("start", 1e18) <= start and entry.get("end", -1) \
                    >= end:
                return True
    except Exception:
        pass
    return False


def analyse(mpv_binary, path, timeout=ANALYSE_TIMEOUT, started=None):
    """Runs a windowless, silent mpv over ``path``; returns its log text, or
    None. The process is this function's own: it is ended on its timeout,
    and ``started(process)`` (if given) gets its handle, so a stop or the
    backend's exit can end it sooner (`Snapper.close`)."""
    folder = os.path.dirname(path)
    script = os.path.join(folder, "duration.lua")
    log_path = os.path.join(folder, "analyse.log")
    with open(script, "w", encoding="utf-8") as handle:
        handle.write(_DURATION_SCRIPT)
    command = [mpv_binary, "--no-config", "--load-scripts=no", "--osc=no",
               "--ytdl=no", "--idle=no", "--force-window=no", "--vo=null",
               "--ao=null", "--ao-null-untimed", "--untimed", "--sid=no",
               "--no-terminal", "--log-file=" + log_path,
               "--script=" + script,
               "--vf=lavfi=[%s]" % BLACK_FILTER,
               "--af=lavfi=[%s]" % SILENCE_FILTER, "--", path]
    try:
        process = subprocess.Popen(
            command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except OSError:
        log.info("Could not run mpv to find the skip rule's edge",
                 exc_info=True)
        return None
    try:
        if started is not None:
            started(process)
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
        log.info("The skip rule's analysis took too long; its edge stands")
        return None
    finally:
        if started is not None:
            started(process, done=True)
    if process.returncode not in (0, None) and not os.path.isfile(log_path):
        return None
    try:
        with open(log_path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except OSError:
        return None


def snap_root(config_path=None):
    """Where this backend's dumps go: ``<temp>/nixie-snap/<this config
    directory's tag>``, so a sweep never touches another backend's (a second
    data folder, a harness run beside it)."""
    if config_path is None:
        try:
            from . import conf

            config_path = conf.config_path
        except Exception:
            config_path = None
    base = os.path.dirname(os.path.abspath(config_path)) if config_path \
        else "none"
    tag = hashlib.sha1(base.lower().encode("utf-8")).hexdigest()[:12]
    return os.path.join(tempfile.gettempdir(), "nixie-snap", tag)


def sweep(root):
    """Removes the ``nixie-snap-*`` folders in ``root`` (this backend's own,
    left by a crash or an exit mid-analysis). Returns how many went."""
    gone = 0
    try:
        names = os.listdir(root)
    except OSError:
        return 0
    for name in names:
        path = os.path.join(root, name)
        if name.startswith("nixie-snap-") and os.path.isdir(path):
            shutil.rmtree(path, ignore_errors=True)
            gone += 0 if os.path.exists(path) else 1
    if gone:
        log.info("Removed %d skip snap folder(s) left by an earlier run", gone)
    return gone


def mpv_binary():
    """The mpv to analyse with: the shim's ``mpv_ext_path``, else one on
    PATH or from WinGet. None when there is none."""
    try:
        from .conf import settings

        path = settings.mpv_ext_path
        if path and os.path.isfile(path):
            return path
    except Exception:
        pass
    try:
        from .bridge import find_mpv_binary

        return find_mpv_binary()
    except Exception:
        return None


class Snapper:
    """Starts one worker per rule segment of the item playing, when
    playback nears its edge (`tick`, from the monitor; reads nothing from
    mpv itself). The worker waits for mpv's cache to hold the stretch, dumps
    it, analyses it and moves the segment's edge; the next ``segments``
    event carries it."""

    def __init__(self, binary=None, root=None):
        self._binary = binary
        self._lock = threading.Lock()
        self._video = None
        self._started = set()
        self.threads = []
        #: the analysers running and the folders in use (`close`)
        self._processes = set()
        self._folders = set()
        self._closed = False
        # a crash, or an exit mid-analysis, left folders behind: this
        # backend's own go now; and at a normal exit, what is in use
        self.root = root or snap_root()
        sweep(self.root)
        atexit.register(self.close)

    def _process(self, process, done=False):
        with self._lock:
            if done:
                self._processes.discard(process)
            else:
                self._processes.add(process)
                closed = self._closed
            if not done and closed:
                process.kill()

    def cancel(self):
        """Playback stopped or moved on: the analysers end now (the workers
        then remove their folders)."""
        with self._lock:
            processes = list(self._processes)
        for process in processes:
            try:
                process.kill()
            except Exception:
                pass

    def close(self):
        """The backend is exiting: the analysers end, the folders go. Never
        raises; safe twice."""
        with self._lock:
            self._closed = True
            folders = list(self._folders)
        self.cancel()
        for folder in folders:
            shutil.rmtree(folder, ignore_errors=True)
        sweep(self.root)

    def tick(self, pm, video, position):
        try:
            self._tick(pm, video, position)
        except Exception:
            log.debug("snapper tick failed", exc_info=True)

    def _tick(self, pm, video, position):
        with self._lock:
            moved_on = video is not self._video and self._video is not None
            if video is not self._video:
                self._video, self._started = video, set()
                self.threads = [t for t in self.threads if t.is_alive()]
        if moved_on:
            # stopped, or another item: the last one's analysis is not
            # wanted any more (its worker then removes its folder)
            self.cancel()
        if video is None or position is None:
            return
        with self._lock:
            for segment in list(getattr(video, "intros", None) or ()):
                if not getattr(segment, "snap_pending", False):
                    continue
                if id(segment) in self._started:
                    continue
                edge, window, anchor = edge_of(segment)
                if position >= edge:
                    segment.snap_pending = False
                    continue
                if anchor == "end" and position < edge - END_LOOKAHEAD:
                    continue
                self._started.add(id(segment))
                thread = threading.Thread(
                    target=self._work, args=(pm, video, segment),
                    name="skip-snap", daemon=True)
                self.threads.append(thread)
                thread.start()

    def _work(self, pm, video, segment):
        try:
            self._snap(pm, video, segment)
        except Exception:
            log.debug("skip snap failed", exc_info=True)
        finally:
            segment.snap_pending = False

    def _snap(self, pm, video, segment):
        edge, window, anchor = edge_of(segment)
        start, end = max(0.0, edge - window), edge + window
        binary = self._binary or mpv_binary()
        if not binary:
            log.info("No mpv binary to analyse with; skip rules keep their "
                     "own edges")
            return
        deadline = time.monotonic() + MAX_WAIT
        while True:
            if pm._video is not video or not getattr(pm, "_mpv_alive", True):
                return
            player = pm._player
            position = player.playback_time
            if position is not None and position >= edge:
                return              # too late: the edge has played
            if covered(player.demuxer_cache_state, start, end):
                break
            if time.monotonic() > deadline:
                return
            time.sleep(POLL)
        if self._closed:
            return
        os.makedirs(self.root, exist_ok=True)
        folder = tempfile.mkdtemp(prefix="nixie-snap-", dir=self.root)
        with self._lock:
            self._folders.add(folder)
        try:
            path = os.path.join(folder, "part.mkv")
            pm._player.command("dump-cache", str(start), str(end), path)
            for _ in range(50):     # the dump is written by mpv's demuxer
                if os.path.isfile(path) and os.path.getsize(path) > 0:
                    break
                time.sleep(0.1)
            text = analyse(binary, path, started=self._process)
            if not text:
                return
            blacks, silences, duration = parse_detections(text)
            if not duration:
                return
            # The dump starts at the keyframe before ``start`` and its times
            # start at 0; it ends at ``end``, which places it.
            offset = end - duration
            blacks = [(a + offset, b + offset) for a, b in blacks]
            silences = [(a + offset, b + offset) for a, b in silences]
            snapped = choose_edge(blacks, silences, edge, window, anchor,
                                  read=(offset, end))
            if snapped is None or pm._video is not video:
                return
            if anchor == "start":
                if snapped > segment.start + 1.0:
                    segment.end = snapped
            elif snapped < segment.end - 1.0:
                segment.start = snapped
            log.info("Skip rule %s: its edge snapped from %.2f to %.2f s",
                     getattr(segment, "rule_id", "?"), edge, snapped)
        finally:
            shutil.rmtree(folder, ignore_errors=True)
            with self._lock:
                self._folders.discard(folder)
