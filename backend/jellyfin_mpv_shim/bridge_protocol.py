"""The bridge's wire protocol, with nothing of the player in it.

Framing, argument validation, secret redaction and the Jellyfin <-> mpv
translations the bridge reports in. The contract is ``BRIDGE.md`` at the
repository root (``../docs/BRIDGE.md`` from ``backend/``), protocol
version 1; how the shim is wired behind it is docs/bridge.md.

**Imports nothing from the shim**, deliberately: importing almost anything
under ``jellyfin_mpv_shim`` reaches ``args.get_args()``, and importing
``player`` builds a real mpv (docs/testing.md sections 1 and 2). The bridge
has to do several of these things -- take over stdout above all -- before it
is safe to import any of that, and the unit tests have to be able to check
them without it.
"""

import json
import logging
import math
import os
import re
import sys
import threading
import urllib.parse

#: The protocol this backend speaks (`connect` must ask for it).
PROTOCOL_VERSION = 1
#: This bridge's own version, reported in `hello` beside the shim's.
BRIDGE_VERSION = "1.0.0"

#: Jellyfin ticks are 100 ns.
TICKS_PER_SECOND = 10_000_000

# Error codes, BRIDGE.md section 2.
NOT_CONNECTED = "not_connected"
BAD_ARGS = "bad_args"
UNKNOWN_CMD = "unknown_cmd"
AUTH_FAILED = "auth_failed"
SERVER_UNREACHABLE = "server_unreachable"
MPV_FAILED = "mpv_failed"
# fluentfin-mpv (release 1.0): connect could not find the mpv it would start itself (BRIDGE.md section 2)
MPV_NOT_FOUND = "mpv_not_found"
NO_PLAYBACK = "no_playback"
WINDOW_LOCKED = "window_locked"
INTERNAL = "internal"
# 1.6 player: the server will not convert the video for this account
# (set_quality; BRIDGE.md section 2)
NOT_ALLOWED = "not_allowed"

ERROR_CODES = frozenset({
    NOT_CONNECTED, BAD_ARGS, UNKNOWN_CMD, AUTH_FAILED, SERVER_UNREACHABLE,
    MPV_FAILED, MPV_NOT_FOUND, NO_PLAYBACK, WINDOW_LOCKED, INTERNAL,
    NOT_ALLOWED,  # 1.6 player
})


class BridgeError(Exception):
    """A command that fails with one of the protocol's error codes."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


class FramingError(Exception):
    """A line that is not a request at all: no usable ``id``, so there is
    nobody to answer. Reported as an ``error`` event instead."""


class Request:
    __slots__ = ("id", "cmd", "args")

    def __init__(self, req_id, cmd, args):
        self.id = req_id
        self.cmd = cmd
        self.args = args

    def __repr__(self):
        # Never the args: `connect` carries the access token.
        return "Request(id=%r, cmd=%r)" % (self.id, self.cmd)


# ---------------------------------------------------------------------------
# Framing


def encode_message(obj):
    """One protocol message: compact UTF-8 JSON and a single ``\\n``.

    ``json.dumps`` escapes every control character inside strings, so the
    newline that ends the line is the only one in it. No BOM, ever.

    **A lone UTF-16 surrogate is replaced, not sent.** The server's JSON
    can carry one (``"\\ud83d"`` in a badly decoded file name), ``json``
    decodes it into the str as-is, and UTF-8 cannot encode it -- the
    ``UnicodeEncodeError`` used to kill whichever thread was writing.
    Escaping it instead (``ensure_ascii``) is no better: System.Text.Json
    refuses an unpaired surrogate escape when the UI reads the string. So a
    pair split across two code points is rejoined and a lone one becomes
    U+FFFD; everything else is untouched.
    """
    text = json.dumps(obj, ensure_ascii=False, separators=(",", ":"),
                      allow_nan=False)
    try:
        data = text.encode("utf-8")
    except UnicodeEncodeError:
        data = text.encode("utf-16", "surrogatepass").decode(
            "utf-16", "replace").encode("utf-8")
    return data + b"\n"


def decode_line(raw):
    """Parse one line from the UI into a `Request`.

    Returns None for a blank line. Raises `FramingError` when there is no
    request id to answer (unparseable JSON, not an object, no positive
    integer ``id``), and `BridgeError` (``bad_args``) with ``.request_id``
    set when there is one but the rest is malformed.

    A leading BOM and a trailing ``\\r`` are tolerated rather than refused:
    neither side should send them, but a line is not worth losing over one.
    """
    if isinstance(raw, bytes):
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            raise FramingError("line is not valid UTF-8")
    else:
        text = raw
    text = text.lstrip("﻿").strip()
    if not text:
        return None
    try:
        obj = json.loads(text)
    except ValueError:
        raise FramingError("line is not valid JSON")
    if not isinstance(obj, dict):
        raise FramingError("message is not a JSON object")
    req_id = obj.get("id")
    if not _is_int(req_id) or req_id <= 0:
        raise FramingError("request has no positive integer id")
    cmd = obj.get("cmd")
    if not isinstance(cmd, str) or not cmd:
        error = BridgeError(BAD_ARGS, "cmd must be a non-empty string")
        error.request_id = req_id
        raise error
    args = obj.get("args")
    if args is None:
        args = {}
    if not isinstance(args, dict):
        error = BridgeError(BAD_ARGS, "args must be an object")
        error.request_id = req_id
        raise error
    return Request(req_id, cmd, args)


class ProtocolWriter:
    """The backend -> UI half: thread-safe, one whole line per write.

    Every thread that has something to say goes through one lock, so lines
    never interleave. A write that fails (the UI went away) marks the writer
    closed and calls ``on_broken`` once -- that is a shutdown, the same as
    stdin reaching EOF.
    """

    def __init__(self, stream, on_broken=None):
        self._stream = stream
        self._lock = threading.Lock()
        self.closed = False
        self.on_broken = on_broken

    def send(self, obj):
        data = encode_message(obj)
        broken = False
        with self._lock:
            if self.closed:
                return False
            try:
                view = memoryview(data)
                while view:
                    written = self._stream.write(view)
                    if written is None:     # a buffered stream wrote it all
                        break
                    view = view[written:]
                flush = getattr(self._stream, "flush", None)
                if flush is not None:
                    flush()
            except (OSError, ValueError):
                self.closed = True
                broken = True
        if broken and self.on_broken is not None:
            try:
                self.on_broken()
            except Exception:
                logging.getLogger("bridge").debug(
                    "on_broken failed", exc_info=True)
        return not broken

    def respond(self, req_id, result=None):
        return self.send({"id": req_id, "ok": True,
                          "result": {} if result is None else result})

    def fail(self, req_id, code, message):
        return self.send({"id": req_id, "ok": False,
                          "error": {"code": code,
                                    "message": redact(str(message))}})

    def event(self, name, data=None):
        return self.send({"event": name, "data": {} if data is None else data})

    def close(self):
        with self._lock:
            self.closed = True
        try:
            self._stream.close()
        except Exception:
            pass


def isolate_stdout():
    """Take fd 1 for the protocol and send everything else to stderr.

    Returns an unbuffered binary stream on a duplicate of the original
    stdout. After this, fd 1, ``sys.stdout`` and -- on Windows -- the
    process's ``STD_OUTPUT_HANDLE`` all point at stderr, so a stray
    ``print``, a C library writing to fd 1, a ``WriteFile`` on the standard
    handle, or a child process that inherits the standard handles can none
    of them put a byte into the protocol stream.

    The handle matters on Windows: it is what native code and child
    processes use. The C runtime's ``_dup2`` re-points it for fds 0-2 in a
    *console* process (measured: the isolation test passes without the
    ``SetStdHandle`` below), but not in a GUI-subsystem one, which a frozen
    backend built windowless would be -- so it is done explicitly. The duplicate is
    non-inheritable (PEP 446), so no child ever holds the protocol pipe
    open -- which would keep the UI from seeing EOF after we exit.
    """
    for stream in (sys.stdout, sys.__stdout__):
        try:
            if stream is not None:
                stream.flush()
        except Exception:
            pass
    proto_fd = os.dup(1)
    try:
        os.dup2(2, 1)
    except OSError:
        # No usable stderr: point fd 1 at nothing rather than the protocol.
        devnull = os.open(os.devnull, os.O_WRONLY)
        os.dup2(devnull, 1)
        os.close(devnull)
    if os.name == "nt":
        import ctypes
        import msvcrt
        from ctypes import wintypes

        msvcrt.setmode(proto_fd, os.O_BINARY)
        try:
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.SetStdHandle.argtypes = [wintypes.DWORD, wintypes.HANDLE]
            kernel32.SetStdHandle.restype = wintypes.BOOL
            std_output_handle = wintypes.DWORD(-11 & 0xFFFFFFFF)
            kernel32.SetStdHandle(std_output_handle, msvcrt.get_osfhandle(1))
        except Exception:
            pass
    sys.stdout = sys.stderr
    return os.fdopen(proto_fd, "wb", buffering=0)


# ---------------------------------------------------------------------------
# Item ids


_ITEM_ID = re.compile(r"^[0-9a-f]{32}$")


def normalize_item_id(value):
    """A Jellyfin item id in N format -- 32 lowercase hex digits, no dashes
    -- or None if ``value`` is not one. Accepts the dashed (D) form too."""
    if not isinstance(value, str):
        return None
    text = value.strip().replace("-", "").lower()
    return text if _ITEM_ID.match(text) else None


def item_id_out(value):
    """An id about to be emitted, in N format (BRIDGE.md: every item id the
    backend emits is). Anything that is not an id passes through unchanged
    rather than vanishing: None stays None."""
    normalized = normalize_item_id(value)
    return normalized if normalized is not None else value


# ---------------------------------------------------------------------------
# Argument validation


_MISSING = object()


def _is_int(value):
    """An int that is not a bool -- JSON true is not 1 here."""
    return isinstance(value, int) and not isinstance(value, bool)


def _bad(message):
    return BridgeError(BAD_ARGS, message)


def get_int(args, key, default=_MISSING, minimum=None, maximum=None,
            nullable=False, label=None):
    """An integer argument. ``null`` is "not given" when there is a
    default, and a value of its own only when ``nullable``."""
    name = label or key
    value = args.get(key, _MISSING)
    if value is _MISSING:
        if default is _MISSING:
            raise _bad("%s is required" % name)
        return default
    if value is None:
        if nullable:
            return None
        if default is not _MISSING:
            return default
        raise _bad("%s must be an integer" % name)
    if not _is_int(value):
        raise _bad("%s must be an integer" % name)
    if minimum is not None and value < minimum:
        raise _bad("%s must be >= %d" % (name, minimum))
    if maximum is not None and value > maximum:
        raise _bad("%s must be <= %d" % (name, maximum))
    return value


def get_number(args, key):
    value = args.get(key, _MISSING)
    if value is _MISSING:
        raise _bad("%s is required" % key)
    if (isinstance(value, bool) or not isinstance(value, (int, float))
            or not math.isfinite(value)):
        raise _bad("%s must be a number" % key)
    return value


def get_bool(args, key):
    value = args.get(key, _MISSING)
    if value is _MISSING:
        raise _bad("%s is required" % key)
    if not isinstance(value, bool):
        raise _bad("%s must be true or false" % key)
    return value


def get_str(args, key, required=True, nullable=False, allow_empty=False,
            label=None):
    name = label or key
    value = args.get(key, _MISSING)
    if value is _MISSING or (value is None and nullable):
        if required:
            raise _bad("%s is required" % name)
        return None
    if not isinstance(value, str):
        raise _bad("%s must be a string" % name)
    if not allow_empty and not value.strip():
        raise _bad("%s must not be empty" % name)
    return value


def get_object(args, key, required=True):
    value = args.get(key, _MISSING)
    if value is _MISSING or value is None:
        if required:
            raise _bad("%s is required" % key)
        return None
    if not isinstance(value, dict):
        raise _bad("%s must be an object" % key)
    return value


#: ``set_controls``: who draws the player controls (BRIDGE.md section 4).
CONTROLS = ("ui", "mpv")
#: The most of the video's height the UI's bottom controls may claim.
MAX_SUBTITLE_INSET = 0.5


def validate_controls(args):
    """``set_controls`` args -> ``(controls, subtitle_inset)``. The inset is
    0 for ``"mpv"``, whatever was sent (the UI draws nothing then)."""
    controls = get_str(args, "controls")
    if controls not in CONTROLS:
        raise _bad("controls must be one of %s" % ", ".join(CONTROLS))
    inset = 0.0
    if args.get("subtitleInset") is not None:
        inset = float(get_number(args, "subtitleInset"))
        if not 0.0 <= inset <= MAX_SUBTITLE_INSET:
            raise _bad("subtitleInset must be between 0 and %g"
                       % MAX_SUBTITLE_INSET)
    return controls, (inset if controls == "ui" else 0.0)


# 1.2.0: the playback speed and the subtitle delay (BRIDGE.md
# section 4 ``set_speed``, ``set_subtitle_delay``).
SPEED_RANGE = (0.5, 2.0)
SUBTITLE_DELAY_LIMIT = 30.0


def get_number_between(args, key, minimum, maximum):
    """A number argument within ``minimum``..``maximum`` (inclusive), as a
    float."""
    value = float(get_number(args, key))
    if not minimum <= value <= maximum:
        raise _bad("%s must be between %g and %g" % (key, minimum, maximum))
    return value


def validate_speed(args):
    """``set_speed`` args -> mpv's ``speed``."""
    return get_number_between(args, "speed", *SPEED_RANGE)


def validate_subtitle_delay(args):
    """``set_subtitle_delay`` args -> mpv's ``sub-delay`` in seconds."""
    return get_number_between(args, "seconds", -SUBTITLE_DELAY_LIMIT,
                              SUBTITLE_DELAY_LIMIT)


def validate_skip_options(args):
    """``set_skip_options`` args -> ``{"onlineLookup": bool|None,
    "learnIntros": bool|None}``: each optional (None = leave as is), a bool
    when given. 1.6 skip: and ``"learnSkips"`` (the same switch, renamed;
    it wins over ``learnIntros``), ``"segmentActions"`` (``{type: action}``
    or None) and ``"ruleAction"`` (``"ask"``/``"always"`` or None)."""
    out = {}
    for key in ("onlineLookup", "learnIntros"):
        value = args.get(key)
        if value is not None and not isinstance(value, bool):
            raise _bad("%s must be true or false" % key)
        out[key] = value
    # -- 1.6 skip ----------------------------------------------------------
    learn = args.get("learnSkips")
    if learn is not None and not isinstance(learn, bool):
        raise _bad("learnSkips must be true or false")
    if learn is not None:
        out["learnIntros"] = learn
    out["segmentActions"] = validate_segment_actions(args.get(
        "segmentActions"))
    rule_action = args.get("ruleAction")
    if rule_action is not None and rule_action not in SKIP_RULE_ACTIONS:
        raise _bad("ruleAction must be one of %s"
                   % ", ".join(SKIP_RULE_ACTIONS))
    out["ruleAction"] = rule_action
    # -- end 1.6 skip ------------------------------------------------------
    return out


def validate_play(args):
    """``play`` args, normalised. Mirrors the server's PlayNow command."""
    item_ids = args.get("itemIds", _MISSING)
    if item_ids is _MISSING:
        raise _bad("itemIds is required")
    if not isinstance(item_ids, list) or not item_ids:
        raise _bad("itemIds must be a non-empty list of item ids")
    normalized = [normalize_item_id(i) for i in item_ids]
    if None in normalized:
        raise _bad("itemIds must be Jellyfin item ids (32 hex digits, "
                   "with or without dashes)")
    item_ids = normalized
    start_index = get_int(args, "startIndex", default=0, minimum=0)
    if start_index >= len(item_ids):
        raise _bad("startIndex %d is outside itemIds (%d)"
                   % (start_index, len(item_ids)))
    return {
        "itemIds": list(item_ids),
        "startIndex": start_index,
        "startPositionTicks": get_int(args, "startPositionTicks", default=0,
                                      minimum=0),
        "mediaSourceId": get_str(args, "mediaSourceId", required=False,
                                 nullable=True),
        "audioStreamIndex": get_int(args, "audioStreamIndex", default=None,
                                    minimum=0, nullable=True),
        "subtitleStreamIndex": get_int(args, "subtitleStreamIndex",
                                       default=None, minimum=-1,
                                       nullable=True),
    }


#: fluentfin-mpv player controls: an ``attached`` window's pipe name, without
#: ``\\.\pipe\`` (BRIDGE.md section 4).
_PIPE_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}")


def validate_window(window):
    """A ``connect`` window, normalised to ``{"mode", "hwnd"}`` (plus
    ``"ipc"`` for ``attached``)."""
    if not isinstance(window, dict):
        raise _bad("window must be an object")
    mode = window.get("mode")
    if mode == "standalone":
        return {"mode": "standalone", "hwnd": None}
    if mode == "attached":
        # fluentfin-mpv player controls: the UI's own mpv, on this pipe.
        ipc = window.get("ipc")
        if not isinstance(ipc, str) or not _PIPE_NAME.fullmatch(ipc):
            raise _bad("window.ipc must be a pipe name (1 to 100 of A-Z a-z "
                       "0-9 . _ -, the first a letter or digit) for "
                       "attached mode")
        return {"mode": "attached", "hwnd": None, "ipc": ipc}
    if mode != "embedded":
        raise _bad('window.mode must be "embedded", "attached" or '
                   '"standalone"')
    hwnd = normalize_hwnd(window.get("hwnd"))
    if hwnd is None:
        raise _bad("window.hwnd must be an integer with 0 < hwnd <= "
                   "4294967295 for embedded mode")
    return {"mode": "embedded", "hwnd": hwnd}


def normalize_hwnd(value):
    """A window handle mpv's ``--wid`` can take, or None.

    BRIDGE.md: ``0 < hwnd <= 4294967295``. Win32 handles are 32 bits wide
    on 64-bit Windows too, and mpv reads a negative or out-of-range ``wid``
    as "no window" and opens its own top-level one -- so a value outside
    that range (a sign-extended ``IntPtr`` included) is refused rather than
    guessed at.
    """
    if not _is_int(value) or not 0 < value <= 0xFFFFFFFF:
        return None
    return value


def is_window_handle(hwnd):
    """Whether ``hwnd`` names a live window. Windows only; elsewhere the
    value is an X11 window id we cannot cheaply check, so it is believed."""
    if os.name != "nt":
        return True
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.WinDLL("user32")
        user32.IsWindow.argtypes = [wintypes.HWND]
        user32.IsWindow.restype = wintypes.BOOL
        return bool(user32.IsWindow(hwnd))
    except Exception:
        return True


_SERVER_KEYS = ("server", "user", "accessToken", "device", "client")


def validate_connect(args, dev=False):
    """``connect`` args, normalised.

    Returns ``{"window": {...} | None, "server": {...} | None, "prewarm":
    bool}``. ``server`` carries everything the Jellyfin client is built
    from. Without ``--dev`` the server part is required; with it,
    ``protocol`` and ``window`` alone are a valid connect (enough for
    ``play_file``). ``prewarm`` (BRIDGE.md section 4): absent or null is
    False, anything but a bool is ``bad_args``.
    """
    protocol = args.get("protocol", _MISSING)
    if protocol is _MISSING:
        raise _bad("protocol is required")
    if not _is_int(protocol):
        raise _bad("protocol must be an integer")
    if protocol != PROTOCOL_VERSION:
        raise _bad("protocol %d is not supported; this backend speaks %d"
                   % (protocol, PROTOCOL_VERSION))

    window = args.get("window", _MISSING)
    window = None if window is _MISSING or window is None \
        else validate_window(window)
    # Connected ahead of playback: mpv without its video output until the
    # first play (BRIDGE.md section 4).
    prewarm = args.get("prewarm")
    if prewarm is None:
        prewarm = False
    elif not isinstance(prewarm, bool):
        raise _bad("prewarm must be true or false")

    present = [k for k in _SERVER_KEYS if args.get(k) is not None]
    if not present:
        if not dev:
            raise _bad("server, user, accessToken, device and client are "
                       "required")
        return {"window": window, "server": None, "prewarm": prewarm}
    missing = [k for k in _SERVER_KEYS if args.get(k) is None]
    if missing:
        raise _bad("%s required alongside %s" % (", ".join(missing),
                                                  ", ".join(present)))

    server = get_object(args, "server")
    user = get_object(args, "user")
    device = get_object(args, "device")
    client = get_object(args, "client")
    url = get_str(server, "url", label="server.url")
    parsed = urllib.parse.urlsplit(url.strip())
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise _bad("server.url must be an http(s) URL")
    token = get_str(args, "accessToken")
    user_id = get_str(user, "id", label="user.id")
    device_id = get_str(device, "id", label="device.id")
    device_name = get_str(device, "name", label="device.name")
    client_name = get_str(client, "name", label="client.name")
    client_version = get_str(client, "version", required=False,
                             nullable=True, label="client.version")
    server_id = get_str(server, "id", required=False, nullable=True,
                        allow_empty=True, label="server.id")
    server_name = get_str(server, "name", required=False, nullable=True,
                          allow_empty=True, label="server.name")
    user_name = get_str(user, "name", required=False, nullable=True,
                        allow_empty=True, label="user.name")
    return {
        "window": window,
        "prewarm": prewarm,
        "server": {
            "url": url.strip().rstrip("/"),
            "serverId": server_id,
            "serverName": server_name,
            "userId": user_id,
            "userName": user_name,
            "token": token,
            "deviceId": device_id,
            "deviceName": device_name,
            "clientName": client_name,
            "clientVersion": client_version or "0.0.0",
        },
    }


# ---------------------------------------------------------------------------
# Redaction


_secrets = set()
_secrets_lock = threading.Lock()

#: Shortest value worth redacting literally; anything shorter would start
#: blanking ordinary words out of log lines.
_MIN_SECRET = 6

_REDACTED = "REDACTED"

_PATTERNS = (
    # Quoted key/value pairs: JSON ("accessToken": "x"), a dict repr
    # ('X-Emby-Token': 'x'), with escaped quotes inside the value allowed.
    (re.compile(r'(?i)(["\']?(?:access_?token|x-emby-token|'
                r'x-mediabrowser-token|api_?key|(?:x-emby-)?authorization)'
                r'["\']?\s*[:=]\s*)(["\'])((?:\\.|(?!\2).)*)\2'),
     lambda m: "%s%s%s%s" % (m.group(1), m.group(2), _REDACTED,
                             m.group(2))),
    # Header lines: `Authorization: MediaBrowser ...`, `X-Emby-Token: x`.
    (re.compile(r'(?im)\b((?:x-emby-)?authorization\s*:\s*)[^\r\n]+'),
     lambda m: m.group(1) + _REDACTED),
    (re.compile(r'(?i)\b((?:x-emby-token|x-mediabrowser-token)\s*:\s*)'
                r'[^\s,;"\']+'),
     lambda m: m.group(1) + _REDACTED),
    # Query strings and bare key=value: api_key=, ApiKey=, X-Emby-Token=.
    (re.compile(r'(?i)\b(api_?key|x-emby-token|x-mediabrowser-token|'
                r'access_?token)=([^&\s"\'<>,;]+)'),
     lambda m: "%s=%s" % (m.group(1), _REDACTED)),
    # The MediaBrowser scheme's own spelling, plain and escaped.
    (re.compile(r'Token=\\"[^"\\]*\\"'), lambda m: 'Token=\\"%s\\"' % _REDACTED),
    (re.compile(r'Token="[^"]*"'), lambda m: 'Token="%s"' % _REDACTED),
)


def add_secret(value):
    """Redact this exact value wherever it appears from now on (the access
    token, as soon as `connect` hands it over -- before anything can log
    it). Its URL-encoded spelling too."""
    if not isinstance(value, str) or len(value) < _MIN_SECRET:
        return
    with _secrets_lock:
        _secrets.add(value)
        quoted = urllib.parse.quote(value, safe="")
        if quoted != value:
            _secrets.add(quoted)


def redact(text):
    """``text`` with every access token in it replaced. Never raises."""
    try:
        text = str(text)
        with _secrets_lock:
            secrets = sorted(_secrets, key=len, reverse=True)
        for secret in secrets:
            text = text.replace(secret, _REDACTED)
        for pattern, replace in _PATTERNS:
            text = pattern.sub(replace, text)
        return text
    except Exception:
        return "<unloggable>"


class RedactingFormatter(logging.Formatter):
    """Formats the whole record -- message, arguments, traceback -- and only
    then redacts, so no field can carry a token past it."""

    def format(self, record):
        return redact(super().format(record))


# ---------------------------------------------------------------------------
# Ticks and track translation


def seconds_to_ticks(seconds):
    if seconds is None:
        return None
    try:
        value = float(seconds)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(value):
        return None
    return int(round(value * TICKS_PER_SECOND))


def ticks_to_seconds(ticks):
    return ticks / TICKS_PER_SECOND


#: The shim's play-method vocabulary -> the protocol's. `Remux` is
#: jellyfin-web's name for a container remux, which the server calls a
#: direct stream.
_PLAY_METHODS = {
    "DirectPlay": "DirectPlay",
    "DirectStream": "DirectStream",
    "Remux": "DirectStream",
    "Transcode": "Transcode",
}


def play_method_name(video):
    if video is None:
        return None
    method = _PLAY_METHODS.get(getattr(video, "play_method", None))
    if method is not None:
        return method
    if getattr(video, "is_transcode", False):
        return "Transcode"
    if getattr(video, "playback_info", None) is not None:
        return "DirectPlay"
    return None


def _track_id(value):
    """mpv's ``aid``/``sid`` as an int, or None for off/auto/unknown. mpv
    reports "no" as False (jsonipc) or the string "no" (libmpv)."""
    if value is None or isinstance(value, bool):
        return None
    if _is_int(value):
        return value
    if isinstance(value, str) and value.isdigit():
        return int(value)
    return None


def audio_index_from_mpv(video, mpv_aid):
    """The Jellyfin MediaStream index of the audio mpv is playing.

    On a transcode mpv's track numbering is the transcode's, not the
    source's, and the audio was chosen when the stream was negotiated, so the
    video's own ``aid`` is the answer. Otherwise mpv's id is mapped back
    through the shim's own table (``Video.audio_uid``, built by
    ``map_streams``), which also catches a track switched with mpv's own key.
    """
    if video is None:
        return None
    selected = getattr(video, "aid", None)
    if getattr(video, "is_transcode", False):
        return selected
    track = _track_id(mpv_aid)
    if track is None:
        return selected
    return (getattr(video, "audio_uid", None) or {}).get(track, selected)


def subtitle_index_from_mpv(video, mpv_sid, external_subtitles=None):
    """The Jellyfin index of the subtitle on screen, ``-1`` for none.

    Three cases mpv cannot see: a burned-in subtitle (``subtitle_enc``,
    painted into the video by the server), and a sidecar still loading
    (``subtitle_url`` with no mpv track yet -- the shim shows nothing
    meanwhile, docs/track-selection.md section 7). Both report the
    selection. Otherwise mpv's track id is mapped back through the embedded
    table (``subtitle_uid``) or the sidecars the shim has added
    (``PlayerManager.external_subtitles``, index -> mpv id).
    """
    if video is None:
        return None
    selected = getattr(video, "sid", None)
    external = external_subtitles or {}
    if selected is not None and selected in (
            getattr(video, "subtitle_enc", None) or ()):
        return selected
    track = _track_id(mpv_sid)
    if track is None:
        if (selected is not None and selected >= 0
                and selected in (getattr(video, "subtitle_url", None) or {})
                and selected not in external):
            return selected     # loading
        return -1
    for index, mpv_id in external.items():
        if mpv_id == track:
            return index
    return (getattr(video, "subtitle_uid", None) or {}).get(track, selected)


def _track(stream, audio):
    out = {
        "index": stream.get("Index"),
        "isDefault": bool(stream.get("IsDefault")),
        "isExternal": bool(stream.get("IsExternal")),
    }
    if stream.get("Language"):
        out["language"] = stream["Language"]
    title = stream.get("DisplayTitle") or stream.get("Title")
    if title:
        out["title"] = title
    # fluentfin-mpv (player controls): the stream's own title tag alone, and
    # the hearing-impaired flag, for labels the UI builds itself (BRIDGE.md
    # section 5, Track).
    if isinstance(stream.get("Title"), str) and stream["Title"].strip():
        out["streamTitle"] = stream["Title"]
    if stream.get("Codec"):
        out["codec"] = stream["Codec"]
    if audio and stream.get("Channels") is not None:
        out["channels"] = stream["Channels"]
    if not audio and "IsForced" in stream:
        out["isForced"] = bool(stream.get("IsForced"))
    if "IsHearingImpaired" in stream:
        out["isHearingImpaired"] = bool(stream.get("IsHearingImpaired"))
    return out


def tracks_payload(media_source):
    """The ``tracks`` event for a media source: Jellyfin's own indices."""
    streams = (media_source or {}).get("MediaStreams") or []
    return {
        "audio": [_track(s, True) for s in streams
                  if s.get("Type") == "Audio" and _is_int(s.get("Index"))],
        "subtitle": [_track(s, False) for s in streams
                     if s.get("Type") == "Subtitle"
                     and _is_int(s.get("Index"))],
    }


def item_payload(item_id, item):
    """The ``item`` event for a queue item's DTO."""
    item = item or {}
    out = {"itemId": item_id_out(item_id), "name": item.get("Name") or "",
           "type": item.get("Type")}
    if item.get("Type") == "Episode":
        if item.get("SeriesName"):
            out["seriesName"] = item["SeriesName"]
        if _is_int(item.get("ParentIndexNumber")):
            out["seasonNumber"] = item["ParentIndexNumber"]
        if _is_int(item.get("IndexNumber")):
            out["episodeNumber"] = item["IndexNumber"]
    if _is_int(item.get("RunTimeTicks")):
        out["runTimeTicks"] = item["RunTimeTicks"]
    return out


# -- fluentfin-mpv: media segments and the queue's neighbours (BRIDGE.md
# section 5, ``segments`` and ``next``) ---------------------------------------

#: What a segment type can be set to do that the protocol lists; "off" types
#: are neither fetched nor sent.
SEGMENT_ACTIONS = ("ask", "always")

#: The most ticks a receiver can hold: a signed 64-bit integer (C#'s
#: ``long``, which `BridgeSegment` parses into and would throw past).
MAX_TICKS = 2 ** 63 - 1


def _clamp_ticks(ticks):
    return min(MAX_TICKS, max(0, ticks))


#: Where a segment came from (BRIDGE.md ``segments.source``; 1.6 skip:
#: ``rule``).
SEGMENT_SOURCES = ("server", "chapters", "online", "learned", "rule")


def segment_list(segments):
    """The ``segments`` list from ``(type, start, end, action[, source])``
    tuples (seconds; the source is ``server`` when left out), in the order
    given, without the ones set to off or with no
    usable times. Times are held to 0 .. `MAX_TICKS` (a server's segment
    data is not checked by anyone before it gets here); one left empty by
    that is dropped."""
    out = []
    for entry in segments or ():
        kind, start, end, action = entry[:4]
        source = entry[4] if len(entry) > 4 else "server"
        # 1.6 skip: a rule's segment names its rule and where it came from
        extra = entry[5] if len(entry) > 5 and source == "rule" else None
        if source not in SEGMENT_SOURCES:
            source = "server"
        start_ticks = seconds_to_ticks(start)
        end_ticks = seconds_to_ticks(end)
        if start_ticks is not None:
            start_ticks = _clamp_ticks(start_ticks)
        if end_ticks is not None:
            end_ticks = _clamp_ticks(end_ticks)
        if (action not in SEGMENT_ACTIONS or not isinstance(kind, str)
                or start_ticks is None or end_ticks is None
                or end_ticks <= start_ticks):
            continue
        segment = {"type": kind, "startTicks": start_ticks,
                   "endTicks": end_ticks, "action": action,
                   "source": source}
        if extra:   # 1.6 skip
            segment.update(_rule_fields(extra))
        out.append(segment)
    return out


#: 1.6 skip: the last stretch of a segment that is no longer offered. mpv's
#: exact seek lands on the frame at or before its target (97.875 for a seek
#: to a snapped 97.88, at 24 fps), so the seek to a segment's end that skips
#: it could leave the position inside it, and the offer came back with the
#: next sample (seen in the harness). A tenth of a second covers 10 fps.
OFFER_END_SLACK_TICKS = 1_000_000


def offered_segment(segments, position):
    """The index in ``segments`` (a `segment_list`) of the one on offer at
    ``position`` (seconds): the first ``ask`` segment the position is in,
    start inclusive and end exclusive -- so the seek to its end that skips
    it also ends the offer (1.6 skip: its last tenth of a second is not
    offered, `OFFER_END_SLACK_TICKS`). None when there is none, or no
    position."""
    ticks = seconds_to_ticks(position)
    if ticks is None:
        return None
    for index, segment in enumerate(segments):
        if (segment["action"] == "ask"
                and segment["startTicks"] <= ticks
                < segment["endTicks"] - OFFER_END_SLACK_TICKS):
            return index
    return None


def segments_payload(item_id, segments, current):
    """The ``segments`` event."""
    return {"itemId": item_id_out(item_id), "segments": segments,
            "current": current}


def next_payload(item_id, queue, auto_play):
    """The ``next`` event from a ``state.queue`` (``{"itemIds", "index"}``):
    the neighbours ``next``/``previous`` would play, and whether the backend
    plays the next one by itself at the end."""
    ids = (queue or {}).get("itemIds") or []
    index = (queue or {}).get("index")
    out = {"itemId": item_id_out(item_id)}
    if _is_int(index) and 0 <= index < len(ids):
        if index + 1 < len(ids) and ids[index + 1]:
            out["nextItemId"] = ids[index + 1]
        if index > 0 and ids[index - 1]:
            out["previousItemId"] = ids[index - 1]
    out["autoPlay"] = bool(auto_play) and "nextItemId" in out
    return out


# -- 1.6 skip: skip rules (BRIDGE.md section 4 ``skip_rules``,
# ``add_skip_rule``, ``forget_skip_rules``, ``answer_skip_suggestion``;
# section 5 ``skip_suggestion`` and ``segments`` with ``source: "rule"``) ---

#: What a skip rule does: offer a Skip button, or skip by itself.
SKIP_RULE_ACTIONS = ("ask", "always")
#: The segment types ``set_skip_options.segmentActions`` can set.
SEGMENT_TYPES = ("Intro", "Outro", "Commercial", "Preview", "Recap")
#: The actions it can set them to ("off": neither fetched nor listed).
SEGMENT_TYPE_ACTIONS = ("off", "ask", "always")
SKIP_ANCHORS = ("start", "end")
#: The longest part a rule may cover, in seconds (an ad block, never an act).
MAX_SKIP_RULE_SECONDS = 600.0
#: The latest a rule may sit from its anchor, in seconds.
MAX_SKIP_RULE_OFFSET = 4 * 3600.0
SUGGESTION_ANSWERS = ("yes", "not_now", "never")
_RULE_ID = re.compile(r"^(r[0-9a-f]{12}|intro-[0-9a-f]{32}-\d{1,4})$")
_CANDIDATE_ID = re.compile(r"^c[0-9a-f]{12}$")


def validate_segment_actions(value):
    """``segmentActions``: ``{type: "off"|"ask"|"always"}`` for some of
    `SEGMENT_TYPES` (others are left as they are), or None."""
    if value is None:
        return None
    if not isinstance(value, dict):
        raise _bad("segmentActions must be an object")
    out = {}
    for kind, action in value.items():
        if kind not in SEGMENT_TYPES:
            raise _bad("segmentActions: unknown segment type %r" % (kind,))
        if action not in SEGMENT_TYPE_ACTIONS:
            raise _bad("segmentActions.%s must be one of %s"
                       % (kind, ", ".join(SEGMENT_TYPE_ACTIONS)))
        out[kind] = action
    return out


def _rule_fields(extra):
    out = {}
    if isinstance(extra.get("ruleId"), str):
        out["ruleId"] = extra["ruleId"]
    if extra.get("origin") in ("marked", "learned"):
        out["origin"] = extra["origin"]
    if extra.get("anchor") in SKIP_ANCHORS:
        out["anchor"] = extra["anchor"]
    return out


def _series_arg(args, required):
    value = args.get("seriesId")
    if value is None:
        if required:
            raise _bad("seriesId is required")
        return None
    normalized = normalize_item_id(value)
    if normalized is None:
        raise _bad("seriesId must be a Jellyfin item id")
    return normalized


def _seconds_arg(args, key):
    value = float(get_number(args, key))
    if not 0.0 <= value <= MAX_SKIP_RULE_OFFSET:
        raise _bad("%s must be between 0 and %g" % (key,
                                                     MAX_SKIP_RULE_OFFSET))
    return value


def validate_add_skip_rule(args):
    """``add_skip_rule`` args: ``seriesId`` (optional: the show playing),
    ``seriesName`` (optional), ``anchor``, ``startS``, ``endS``, ``action``
    (optional: the Settings choice). For ``start`` the times are seconds
    from the episode's start (``startS < endS``); for ``end`` seconds
    *before* its end (``startS > endS >= 0``). At most
    `MAX_SKIP_RULE_SECONDS` long."""
    anchor = get_str(args, "anchor")
    if anchor not in SKIP_ANCHORS:
        raise _bad("anchor must be one of %s" % ", ".join(SKIP_ANCHORS))
    start = _seconds_arg(args, "startS")
    end = _seconds_arg(args, "endS")
    span = end - start if anchor == "start" else start - end
    if span <= 0:
        raise _bad("startS must be %s endS for a rule anchored to the %s"
                   % ("before" if anchor == "start" else "farther from the "
                      "end than", anchor))
    if span > MAX_SKIP_RULE_SECONDS:
        raise _bad("a skip rule covers at most %d seconds"
                   % MAX_SKIP_RULE_SECONDS)
    action = args.get("action")
    if action is not None and action not in SKIP_RULE_ACTIONS:
        raise _bad("action must be one of %s" % ", ".join(SKIP_RULE_ACTIONS))
    name = get_str(args, "seriesName", required=False, nullable=True,
                   allow_empty=True)
    return {"seriesId": _series_arg(args, required=False),
            "seriesName": name.strip()[:200] if name else None,
            "anchor": anchor, "startS": start, "endS": end,
            "action": action}


def validate_forget_skip_rules(args):
    """``forget_skip_rules`` args: ``seriesId`` and ``ruleId``, each
    optional (both left out: everything)."""
    rule_id = get_str(args, "ruleId", required=False, nullable=True)
    if rule_id is not None and not _RULE_ID.match(rule_id):
        raise _bad("ruleId is not a skip rule's id")
    return {"seriesId": _series_arg(args, required=False),
            "ruleId": rule_id}


def validate_skip_suggestion_answer(args):
    """``answer_skip_suggestion`` args: ``id`` (the ``skip_suggestion``'s),
    ``answer`` and an optional ``action`` for a ``yes``."""
    suggestion = get_str(args, "id")
    if not _CANDIDATE_ID.match(suggestion):
        raise _bad("id is not a skip suggestion's id")
    answer = get_str(args, "answer")
    if answer not in SUGGESTION_ANSWERS:
        raise _bad("answer must be one of %s" % ", ".join(SUGGESTION_ANSWERS))
    action = args.get("action")
    if action is not None and action not in SKIP_RULE_ACTIONS:
        raise _bad("action must be one of %s" % ", ".join(SKIP_RULE_ACTIONS))
    return {"id": suggestion, "answer": answer, "action": action}


def validate_skip_rules_account(args):
    """``skip_rules`` and ``forget_skip_rules`` before ``connect``: whose
    rules, as ``serverId`` and ``userId`` (no secret: the ids Jellyfin
    shows any client). None when neither is given."""
    server = args.get("serverId")
    user = args.get("userId")
    if server is None and user is None:
        return None
    server = get_str(args, "serverId")
    user = normalize_item_id(get_str(args, "userId"))
    if user is None:
        raise _bad("userId must be a Jellyfin user id")
    return {"serverId": server.strip(), "userId": user}


def skip_suggestion_payload(suggestion):
    """The ``skip_suggestion`` event from `skip_rules.SkipWatcher`'s."""
    out = {"id": suggestion["id"],
           "itemId": item_id_out(suggestion.get("itemId")),
           "seriesId": item_id_out(suggestion.get("seriesId")),
           "anchor": suggestion["anchor"],
           "startTicks": _clamp_ticks(seconds_to_ticks(
               suggestion["startS"]) or 0),
           "endTicks": _clamp_ticks(seconds_to_ticks(
               suggestion["endS"]) or 0)}
    if suggestion.get("seriesName"):
        out["seriesName"] = suggestion["seriesName"]
    return out
