"""1.6.1 (BH-160-M1): the analysis behind `skip_snap`, with the libmpv Nixie
ships.

The release package has ``app\\libmpv\\libmpv-2.dll`` (the UI plays with it)
and no ``mpv.exe``, so 1.6.0's analyser, a separate mpv binary, was missing
on most PCs and no skip rule ever snapped. This runs the same analysis in a
libmpv handle with no window and no sound (``vo=null``, ``ao=null``), in a
process of its own that `skip_snap` starts and ends by its handle (a
decoder that hangs or crashes takes only this process with it):

- frozen: the backend's own exe, ``fluentfin-mpv-backend.exe
  --nixie-skip-analyse ...`` (``tools/release/backend_entry.py``);
- from source: ``python -I <this file> ...``.

Usage (``argv`` after the switch, or after the file):

- ``<libmpv-2.dll> <media> <log> <video filter> <audio filter>``: plays
  ``media`` untimed through the two ``lavfi`` filters and writes mpv's log
  lines (``[level][prefix] text``, verbose and up: FFmpeg's detections are
  verbose in mpv) to ``log``, then ``nixie-duration=<seconds>``;
- ``--make-clip <libmpv-2.dll> <out>``: encodes `skip_snap`'s check clip (a
  test pattern and a tone, 20 s, black and silent from 9.0 to 9.6 s; never
  real footage) into ``out``, for the package check and the tests.

Exit status: 0 done, 2 bad arguments, 3 libmpv could not be loaded, 4 mpv
refused an option or the file, 5 the file never loaded, 6 out of time.

Stdlib only, and nothing from the package: it is run by path.
"""

import ctypes
import os
import sys
import time

#: mpv_event_id values used here (client.h)
_SHUTDOWN, _LOG_MESSAGE, _END_FILE, _FILE_LOADED = 1, 2, 7, 8
#: LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS: the
#: DLLs libmpv needs come from its own folder (python-mpv loads it the same)
_LOAD_FLAGS = 0x00000100 | 0x00001000
#: How long one run may take before it gives up by itself (the caller ends
#: the process sooner: `skip_snap.ANALYSE_TIMEOUT`).
TIMEOUT = 30.0

CLIP_SECONDS = 20
CLIP_GAP = (9.0, 9.6)


class _LogMessage(ctypes.Structure):
    _fields_ = [("prefix", ctypes.c_char_p), ("level", ctypes.c_char_p),
                ("text", ctypes.c_char_p), ("log_level", ctypes.c_int)]


class _Event(ctypes.Structure):
    _fields_ = [("event_id", ctypes.c_int), ("error", ctypes.c_int),
                ("reply_userdata", ctypes.c_uint64),
                ("data", ctypes.c_void_p)]


class _EndFile(ctypes.Structure):
    _fields_ = [("reason", ctypes.c_int), ("error", ctypes.c_int)]


class MpvError(Exception):
    pass


def _bytes(value):
    return str(value).encode("utf-8")


class Libmpv:
    """The few libmpv calls used here, on one handle (``mpv_create``)."""

    def __init__(self, dll_path):
        if os.name == "nt":
            # 1.7.5 (BH-161-q4): the search flags are ``winmode``; the second
            # positional is ``mode``, which Windows ignores (it worked only
            # because Python's default flags are these)
            dll = ctypes.CDLL(dll_path, winmode=_LOAD_FLAGS)
        else:
            dll = ctypes.CDLL(dll_path)
        p, s, i = ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int
        for name, args, result in (
                ("mpv_create", [], p),
                ("mpv_set_option_string", [p, s, s], i),
                ("mpv_initialize", [p], i),
                ("mpv_request_log_messages", [p, s], i),
                ("mpv_command", [p, ctypes.POINTER(s)], i),
                ("mpv_get_property_string", [p, s], p),
                ("mpv_free", [p], None),
                ("mpv_wait_event", [p, ctypes.c_double],
                 ctypes.POINTER(_Event)),
                ("mpv_error_string", [i], s),
                ("mpv_terminate_destroy", [p], None)):
            function = getattr(dll, name)
            function.argtypes, function.restype = args, result
        self._dll = dll
        self.handle = dll.mpv_create()
        if not self.handle:
            raise MpvError("mpv_create failed")

    def _check(self, code, what):
        if code < 0:
            raise MpvError("%s: %s" % (what, self._dll.mpv_error_string(
                code).decode("utf-8", "replace")))

    def set_option(self, name, value):
        self._check(self._dll.mpv_set_option_string(
            self.handle, _bytes(name), _bytes(value)), name)

    def initialize(self, log_level="v"):
        self._check(self._dll.mpv_initialize(self.handle), "initialize")
        self._dll.mpv_request_log_messages(self.handle, _bytes(log_level))

    def command(self, *args):
        array = (ctypes.c_char_p * (len(args) + 1))(
            *[_bytes(a) for a in args], None)
        self._check(self._dll.mpv_command(self.handle, array), args[0])

    def get(self, name):
        """A property as mpv prints it (JSON for a map), or None."""
        pointer = self._dll.mpv_get_property_string(self.handle, _bytes(name))
        if not pointer:
            return None
        try:
            return ctypes.string_at(pointer).decode("utf-8", "replace")
        finally:
            self._dll.mpv_free(pointer)

    def wait(self, timeout):
        """``(event id, error, data pointer)`` of the next event."""
        event = self._dll.mpv_wait_event(self.handle, timeout).contents
        return event.event_id, event.error, event.data

    def close(self):
        if self.handle:
            handle, self.handle = self.handle, None
            self._dll.mpv_terminate_destroy(handle)


def run(dll_path, options, target, timeout=TIMEOUT):
    """Plays ``target`` once with ``options`` and returns ``(lines,
    duration, error)``: mpv's log lines, the file's duration (None when it
    never loaded) and an error text (None when it played to its end)."""
    mpv = Libmpv(dll_path)
    lines, duration, error = [], None, None
    try:
        for name, value in options:
            mpv.set_option(name, value)
        mpv.initialize()
        mpv.command("loadfile", target)
        deadline = time.monotonic() + timeout
        quitting = False
        while True:
            left = deadline - time.monotonic()
            if left <= 0:
                error = "out of time"
                break
            event, _, data = mpv.wait(min(left, 0.5))
            if event == _LOG_MESSAGE:
                message = ctypes.cast(
                    data, ctypes.POINTER(_LogMessage)).contents
                lines.append("[%s][%s] %s" % (
                    (message.level or b"").decode("ascii", "replace"),
                    (message.prefix or b"").decode("utf-8", "replace"),
                    (message.text or b"").decode("utf-8", "replace")
                    .rstrip("\n")))
            elif event == _FILE_LOADED:
                try:
                    duration = float(mpv.get("duration"))
                except (TypeError, ValueError):
                    duration = None
            elif event == _END_FILE and not quitting:
                end = ctypes.cast(data, ctypes.POINTER(_EndFile)).contents
                if end.error < 0:
                    error = "the file did not play (%d)" % end.error
                # the detections FFmpeg prints as the stream ends come
                # before mpv's shutdown: read on until it
                quitting = True
                try:
                    mpv.command("quit")
                except MpvError:
                    pass            # already on its way out (idle=no)
            elif event == _SHUTDOWN:
                break
    finally:
        mpv.close()
    return lines, duration, error


#: 1.6.1 (review): at most this many decoder threads (`skip_snap`'s
#: DECODER_THREADS): the analysis never takes the CPU playback needs.
DECODER_THREADS = 2


def analyse_options(video_filter, audio_filter):
    """What `skip_snap.analyse` gives an external mpv on its command line."""
    return [("config", "no"), ("load-scripts", "no"), ("osc", "no"),
            ("ytdl", "no"), ("idle", "no"), ("force-window", "no"),
            ("input-default-bindings", "no"), ("vo", "null"), ("ao", "null"),
            ("ao-null-untimed", "yes"), ("untimed", "yes"), ("sid", "no"),
            ("audio-file-auto", "no"), ("sub-auto", "no"),
            ("vd-lavc-threads", str(DECODER_THREADS)),
            ("vf", "lavfi=[%s]" % video_filter),
            ("af", "lavfi=[%s]" % audio_filter)]


def make_clip(dll_path, out):
    """Encodes the check clip into ``out``; returns an error text or None."""
    gap = "enable='between(t,%g,%g)'" % CLIP_GAP
    options = [("config", "no"), ("load-scripts", "no"), ("osc", "no"),
               ("idle", "no"), ("force-window", "no"), ("vo", "null"),
               ("ao", "null"),
               ("audio-files", "av://lavfi:sine=f=440:d=%d,volume=volume=0:%s"
                % (CLIP_SECONDS, gap)),
               ("o", out), ("of", "matroska"), ("ovc", "mpeg4"),
               ("oac", "aac")]
    source = ("av://lavfi:testsrc=size=320x180:rate=25:duration=%d,"
              "drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:%s"
              % (CLIP_SECONDS, gap))
    _, _, error = run(dll_path, options, source, timeout=60.0)
    if error:
        return error
    if not os.path.isfile(out) or os.path.getsize(out) == 0:
        return "no clip was written"
    return None


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv[:1] == ["--make-clip"]:
        if len(argv) != 3:
            return 2
        try:
            error = make_clip(argv[1], argv[2])
        except OSError:
            return 3
        except MpvError:
            return 4
        return 0 if error is None else 5
    if len(argv) != 5:
        return 2
    dll_path, media, log_path, video_filter, audio_filter = argv
    status = 0
    try:
        lines, duration, error = run(
            dll_path, analyse_options(video_filter, audio_filter), media)
    except OSError as e:
        lines, duration, error, status = [], None, "libmpv: %s" % e, 3
    except MpvError as e:
        lines, duration, error, status = [], None, str(e), 4
    if status == 0 and error:
        status = 6 if error == "out of time" else 5
    with open(log_path, "w", encoding="utf-8") as handle:
        for line in lines:
            handle.write(line + "\n")
        if duration is not None:
            handle.write("nixie-duration=%s\n" % duration)
        if error:
            handle.write("nixie-error=%s\n" % error)
    return status


if __name__ == "__main__":
    sys.exit(main())
