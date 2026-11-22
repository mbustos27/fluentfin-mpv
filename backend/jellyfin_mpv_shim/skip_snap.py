"""1.6 skip: the margin. A show's extras are not the same length in every
episode, so a skip rule's edge -- where the episode starts again after a
start rule, where the extra begins in an end rule -- snaps to the nearest
black frame or silence within a few seconds of where the rule puts it.

Only that stretch is read, and only from what mpv has already read ahead:
mpv's ``dump-cache`` writes the few seconds around the edge (a few MB) from
its demuxer cache to a temporary file, never asking the server for anything,
and an mpv with no window and no sound (``vo=null``, ``ao=null``) runs
FFmpeg's ``blackdetect`` and ``silencedetect`` over that file. The file is
deleted at once. Nothing happens when mpv has not cached the stretch (a
local file, a cache too short), when there is nothing to analyse with, or
when nothing black or silent is near: the rule's own edge stands.

1.6.1 (BH-160-M1): the analysing mpv is the libmpv Nixie ships
(`libmpv_path`), in a process of the backend's own (`skip_analyse`); an
``mpv`` binary (`mpv_binary`) only where there is no libmpv, as a source
checkout without one. 1.6.0 needed the binary, which the release package
does not have, so on most PCs no rule ever snapped.

Nothing here raises into playback; every worker catches and logs.
"""

import atexit
import collections
import glob
import hashlib
import logging
import os
import re
import shutil
import subprocess
import sys
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
#: 1.6.1 (review): the analysis must never compete with playback (it reads
#: untimed, as fast as the CPU goes, and end rules from up to
#: `END_LOOKAHEAD` ahead): its process runs below normal priority, with at
#: most this many decoder threads.
DECODER_THREADS = 2
BELOW_NORMAL_PRIORITY_CLASS = 0x00004000


def _creation_flags():
    """No window, and (Windows) below normal priority."""
    return (getattr(subprocess, "CREATE_NO_WINDOW", 0)
            | getattr(subprocess, "BELOW_NORMAL_PRIORITY_CLASS",
                      BELOW_NORMAL_PRIORITY_CLASS if os.name == "nt" else 0))
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

#: What analyses: ``kind`` "libmpv" (``path`` a libmpv-2.dll, run in a
#: process of the backend's own, `skip_analyse`) or "mpv" (an mpv binary).
Analyser = collections.namedtuple("Analyser", "kind path")
LIBMPV = "libmpv-2.dll"
#: A source run's libmpv folder when it is not the checkout's
#: ``vendor/libmpv/<build>`` (as FluentFin.csproj's ``-p:LibmpvDir``).
LIBMPV_ENV = "NIXIE_LIBMPV_DIR"
#: The frozen backend's switch for `skip_analyse` (backend_entry.py).
ANALYSE_SWITCH = "--nixie-skip-analyse"

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


def snap_key(segment):
    """What one analysis is for: the rule and its edge. 1.6.1 (review): a
    rules rebuild mid-episode (`bridge_skip.refresh_rules`) makes new
    segment objects for the same rules, and an analysis keyed by the object
    started a second one of the same edge while the first still ran."""
    edge, _window, anchor = edge_of(segment)
    return (getattr(segment, "rule_id", None) or id(segment), anchor,
            round(edge, 2))


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


def child_command():
    """How the backend starts `skip_analyse` in a process of its own: the
    frozen backend's exe with `ANALYSE_SWITCH`, else this Python with the
    module's file (``-I``: nothing from the environment or the folder). A
    venv's python.exe on Windows is a launcher with the interpreter as its
    child; ending the launcher by its handle ends that child too
    (`test_a_cancel_ends_the_analysing_interpreter_itself`)."""
    if getattr(sys, "frozen", False):
        return [sys.executable, ANALYSE_SWITCH]
    return [sys.executable, "-I",
            os.path.join(os.path.dirname(os.path.abspath(__file__)),
                         "skip_analyse.py")]


def analyse(tool, path, timeout=ANALYSE_TIMEOUT, started=None):
    """Runs a windowless, silent mpv over ``path``; returns its log text, or
    None. ``tool`` is an `Analyser` (a plain string: an mpv binary). The
    process is this function's own: it is ended on its timeout, and
    ``started(process)`` (if given) gets its handle, so a stop or the
    backend's exit can end it sooner (`Snapper.close`)."""
    if not isinstance(tool, Analyser):
        tool = Analyser("mpv", tool)
    folder = os.path.dirname(path)
    log_path = os.path.join(folder, "analyse.log")
    if tool.kind == "libmpv":
        command = child_command() + [tool.path, path, log_path,
                                     BLACK_FILTER, SILENCE_FILTER]
    else:
        script = os.path.join(folder, "duration.lua")
        with open(script, "w", encoding="utf-8") as handle:
            handle.write(_DURATION_SCRIPT)
        command = [tool.path, "--no-config", "--load-scripts=no", "--osc=no",
                   "--ytdl=no", "--idle=no", "--force-window=no", "--vo=null",
                   "--ao=null", "--ao-null-untimed", "--untimed", "--sid=no",
                   "--vd-lavc-threads=%d" % DECODER_THREADS,
                   "--no-terminal", "--log-file=" + log_path,
                   "--script=" + script,
                   "--vf=lavfi=[%s]" % BLACK_FILTER,
                   "--af=lavfi=[%s]" % SILENCE_FILTER, "--", path]
    try:
        process = subprocess.Popen(
            command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, creationflags=_creation_flags())
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
    try:
        with open(log_path, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
    except OSError:
        text = None
    if process.returncode not in (0, None):
        # skip_analyse's status (a stop's kill is 1 on Windows) and its
        # last word; the edge stands
        error = re.findall(r"^nixie-error=(.*)$", text or "", re.M)
        log.info("The skip rule's analysis (%s) ended with status %s%s",
                 tool.kind, process.returncode,
                 ": " + error[-1] if error else "")
    return text


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


def libmpv_path():
    """The libmpv to analyse with, the one the UI plays with, or None.

    Frozen (the release package): ``app\\libmpv\\libmpv-2.dll``, beside the
    backend's ``app\\backend`` folder (build-release.ps1 puts it there), and
    nothing else. From source: `LIBMPV_ENV`'s folder, else the checkout's
    ``vendor/libmpv/<build>`` (FluentFin.csproj's default; the last build
    by name)."""
    if getattr(sys, "frozen", False):
        candidates = [os.path.join(os.path.dirname(os.path.abspath(
            sys.executable)), os.pardir, "libmpv", LIBMPV)]
    else:
        candidates = []
        folder = os.environ.get(LIBMPV_ENV)
        if folder:
            candidates.append(os.path.join(folder, LIBMPV))
        repo = os.path.dirname(os.path.dirname(os.path.dirname(
            os.path.abspath(__file__))))
        candidates += sorted(glob.glob(os.path.join(
            repo, "vendor", "libmpv", "*", LIBMPV)), reverse=True)
    for candidate in candidates:
        if os.path.isfile(candidate):
            return os.path.normpath(candidate)
    return None


def find_analyser():
    """An `Analyser`: the shipped libmpv (`libmpv_path`), else an mpv
    binary (`mpv_binary`); None when there is neither."""
    path = libmpv_path()
    if path:
        return Analyser("libmpv", path)
    path = mpv_binary()
    if path:
        return Analyser("mpv", path)
    return None


def make_test_clip(dll, out, timeout=120.0):
    """Has `skip_analyse` encode its check clip into ``out`` with ``dll``,
    in a process of its own (as an analysis runs); True when written."""
    try:
        process = subprocess.run(
            child_command() + ["--make-clip", dll, out],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, timeout=timeout,
            creationflags=_creation_flags())
    except (OSError, subprocess.TimeoutExpired):
        log.info("Could not make the skip check's clip", exc_info=True)
        return False
    return (process.returncode == 0 and os.path.isfile(out)
            and os.path.getsize(out) > 0)


#: The check clip's cut (skip_analyse.CLIP_GAP) and where a start rule that
#: ends 1 s before it lands: ``LEAD`` before the gap's end.
CHECK_EDGE = 8.0
CHECK_SNAP = 9.6 - LEAD


def self_check(out=None):
    """Whether a skip rule can snap here, with what a skip would use.

    Finds libmpv as a skip does (`libmpv_path`, which `find_analyser`
    tries first; never the mpv binary, so the shim's settings are not read
    either); has it encode the check clip, analyses the clip in a process
    of the backend's own (`analyse`) and snaps a start rule ending at
    `CHECK_EDGE` to the clip's cut. Prints one JSON line to ``out``
    (stdout) and returns 0 when the edge landed on `CHECK_SNAP`, else 1.
    The release package's check (verify-package.ps1, the frozen backend's
    ``--nixie-skip-check``) and the tests run it."""
    import json

    out = out or sys.stdout
    result = {"analyser": None, "path": None, "duration": None,
              "blacks": [], "silences": [], "snapped": None, "error": None}
    path = libmpv_path()
    tool = Analyser("libmpv", path) if path else None
    if tool is not None:
        result["analyser"], result["path"] = tool.kind, tool.path
    folder = tempfile.mkdtemp(prefix="nixie-snap-check-")
    try:
        clip = os.path.join(folder, "part.mkv")
        if tool is None:
            result["error"] = "no libmpv to analyse with"
        elif not make_test_clip(tool.path, clip):
            result["error"] = "libmpv made no check clip"
        else:
            text = analyse(tool, clip)
            blacks, silences, duration = parse_detections(text or "")
            result.update(blacks=blacks, silences=silences,
                          duration=duration)
            if not duration:
                result["error"] = "the analysis read no duration"
            else:
                result["snapped"] = choose_edge(
                    blacks, silences, CHECK_EDGE, WINDOW, "start",
                    read=(0.0, duration))
                if result["snapped"] is None:
                    result["error"] = "no cut found"
    finally:
        shutil.rmtree(folder, ignore_errors=True)
    ok = result["snapped"] is not None \
        and abs(result["snapped"] - CHECK_SNAP) <= 0.15
    if result["snapped"] is not None and not ok:
        result["error"] = "the edge landed off the cut"
    result["ok"] = ok
    out.write(json.dumps(result) + "\n")
    out.flush()
    return 0 if ok else 1


def mpv_binary():
    """The mpv binary to analyse with when there is no libmpv: the shim's
    ``mpv_ext_path``, else one on PATH or from WinGet. None when there is
    none."""
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

    def __init__(self, binary=None, root=None, analyser=None):
        #: what analyses: given (an `Analyser`; ``binary``, an mpv binary),
        #: else `find_analyser` at each snap
        self._analyser = analyser or (Analyser("mpv", binary) if binary
                                      else None)
        self._told = False  # nothing said yet (None: said "nothing")
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
                key = snap_key(segment)
                if key in self._started:
                    continue
                edge, window, anchor = edge_of(segment)
                if position >= edge:
                    segment.snap_pending = False
                    continue
                if anchor == "end" and position < edge - END_LOOKAHEAD:
                    continue
                self._started.add(key)
                thread = threading.Thread(
                    target=self._work, args=(pm, video, segment),
                    name="skip-snap", daemon=True)
                self.threads.append(thread)
                thread.start()

    @staticmethod
    def _standing_for(video, segment):
        """``segment`` and the segments of ``video`` now standing for the same
        rule and edge (`snap_key`: a rebuild made new ones meanwhile)."""
        key = snap_key(segment)
        out = [segment]
        for other in list(getattr(video, "intros", None) or ()):
            if other is not segment and getattr(other, "rule_id", None) \
                    and snap_key(other) == key:
                out.append(other)
        return out

    def _work(self, pm, video, segment):
        try:
            self._snap(pm, video, segment)
        except Exception:
            log.debug("skip snap failed", exc_info=True)
        finally:
            for standing in self._standing_for(video, segment):
                standing.snap_pending = False

    def _snap(self, pm, video, segment):
        edge, window, anchor = edge_of(segment)
        start, end = max(0.0, edge - window), edge + window
        tool = self._analyser or find_analyser()
        if tool != self._told:
            # once per change, not per rule
            self._told = tool
            if tool is None:
                log.info("No libmpv or mpv to analyse with; skip rules keep "
                         "their own edges")
            else:
                log.info("Skip rules' edges are analysed with %s (%s)",
                         tool.kind, tool.path)
        if tool is None:
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
            text = analyse(tool, path, started=self._process)
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
            # the rule's segment as it stands now (a rebuild made a new one
            # while this ran), and the one it was started for
            for standing in self._standing_for(video, segment):
                if anchor == "start":
                    if snapped > standing.start + 1.0:
                        standing.end = snapped
                elif snapped < standing.end - 1.0:
                    standing.start = snapped
            log.info("Skip rule %s: its edge snapped from %.2f to %.2f s",
                     getattr(segment, "rule_id", "?"), edge, snapped)
        finally:
            shutil.rmtree(folder, ignore_errors=True)
            with self._lock:
                self._folders.discard(folder)
