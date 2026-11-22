"""fluentfin-mpv 1.6 (the player stream): the quality choice and the Playback
stats (docs/BRIDGE.md: ``set_quality``, ``set_quality_defaults``, the
``stream`` event and ``get_stats``).

A choice is ``(bitrate, original)``: Auto (None, False), today's behaviour and
the default; Original (None, True), no cap at all, so the server direct-plays
whatever this player can open; or a cap in bits per second (n, False), which
the server compares with the file as jellyfin-web's own quality menu does: a
file under the cap still plays directly, one over it is converted down.

It lives on the video as the shim's own quality override, ``trs_ovr``
(`media.Video.set_trs_override`, which the shim's gear menu writes), so a
quality change is the same restart that menu makes (`restart_playback`: the
same viewing, at the same position) and a change that finds no stream is put
back by the bridge's `_put_back` like any other. Here the override never
forces a transcode: ``(kbps, False)`` is a bitrate cap, and
``(ORIGINAL_KBPS, False)`` the profile's own maximum.

Nothing here talks to the server: the account's policy comes from the
``Users/Me`` answer ``connect`` already has (`remember_policy`), and
`stats` reads mpv only when ``get_stats`` asks (the UI asks once a second
while its panel is open, never while it is closed).
"""

import logging
import math
import threading
import time
import urllib.parse

from . import bridge_protocol as proto

log = logging.getLogger("quality")

#: The cap that is no cap: `utils.get_profile` sends ``kbps * 1000`` as the
#: profile's MaxStreamingBitrate, and this is the shim's own ``local_kbps``
#: default, just under a signed 32-bit integer of bits per second (what the
#: server reads it into).
ORIGINAL_KBPS = 2147483

#: The caps the protocol accepts, in bits per second: 100 kbps to 1 Gbps.
MIN_BITRATE = 100_000
MAX_BITRATE = 1_000_000_000

#: What the user is told when the server will not convert the video for
#: their account (BRIDGE.md section 4, ``set_quality``). Shown as it is.
REFUSED = ("This server doesn't allow a lower quality for your account. "
           "Playing at the original quality.")

#: The ``error`` code of that refusal (BRIDGE.md section 2).
NOT_ALLOWED = proto.NOT_ALLOWED


class Choice(tuple):
    """``(bitrate, original)``: bitrate in bits per second or None."""

    def __new__(cls, bitrate=None, original=False):
        return super().__new__(cls, (bitrate, bool(original)))

    @property
    def bitrate(self):
        return self[0]

    @property
    def original(self):
        return self[1]

    @property
    def is_auto(self):
        return self[0] is None and not self[1]

    @property
    def is_cap(self):
        return self[0] is not None

    def payload(self):
        return {"bitrate": self[0], "original": self[1]}


AUTO = Choice(None, False)
ORIGINAL = Choice(None, True)


def _choice_from(value, name):
    if not isinstance(value, dict):
        raise proto.BridgeError(proto.BAD_ARGS, "%s must be an object" % name)
    bitrate = proto.get_int(value, "bitrate", default=None, nullable=True,
                            minimum=MIN_BITRATE, maximum=MAX_BITRATE,
                            label=name + ".bitrate" if name else "bitrate")
    original = value.get("original", False)
    if original is None:
        original = False
    if not isinstance(original, bool):
        raise proto.BridgeError(
            proto.BAD_ARGS, "%soriginal must be true or false"
            % (name + "." if name else ""))
    if original and bitrate is not None:
        raise proto.BridgeError(
            proto.BAD_ARGS, "%soriginal and a bitrate cannot both be given"
            % (name + "." if name else ""))
    return Choice(bitrate, original)


def validate(args):
    """``set_quality`` args -> a `Choice`."""
    return _choice_from(args, "")


def validate_defaults(args):
    """``set_quality_defaults`` args -> ``{"home": Choice|None, "away":
    Choice|None}`` (None: left as it is)."""
    out = {}
    for key in ("home", "away"):
        value = args.get(key)
        out[key] = None if value is None else _choice_from(value, key)
    return out


# -- the override on a video ------------------------------------------------


def override_for(choice):
    """The ``trs_ovr`` a choice makes: None for Auto (the shim's settings
    decide, as before 1.6), else a bitrate cap in kbps that forces nothing."""
    if choice is None or choice.is_auto:
        return None
    if choice.original:
        return (ORIGINAL_KBPS, False)
    return (max(1, choice.bitrate // 1000), False)


#: Set on a video while a quality change runs: the override its playing
#: stream was negotiated with, as a 1-tuple. The new one is written before
#: the restart begins (the restart negotiates with it), and a ``stream``
#: event sampled meanwhile must not show it as taken (`changing`).
_PINNED = "_fluentfin_quality_pinned"


class changing:
    """``with quality.changing(video):`` around a quality change: the choice
    shown stays the one playing until the change is over, taken or not."""

    def __init__(self, video):
        self.video = video

    def __enter__(self):
        try:
            setattr(self.video, _PINNED, (getattr(self.video, "trs_ovr", None),))
        except Exception:
            pass
        return self

    def __exit__(self, *exc):
        try:
            delattr(self.video, _PINNED)
        except Exception:
            pass
        return False


def choice_of(video):
    """The choice in effect for ``video``, read back from its override (or,
    while a change runs, the one still playing). The shim's own forced
    transcode (a failed load retried, its gear menu) is Auto, or that menu's
    cap."""
    pinned = getattr(video, _PINNED, None)
    override = pinned[0] if isinstance(pinned, tuple) and pinned else getattr(
        video, "trs_ovr", None)
    if not override:
        return AUTO
    try:
        kbps, _forced = override
    except (TypeError, ValueError):
        return AUTO
    if not isinstance(kbps, int) or isinstance(kbps, bool) or kbps <= 0:
        return AUTO
    if kbps >= ORIGINAL_KBPS:
        return ORIGINAL
    return Choice(kbps * 1000, False)


class Defaults:
    """The default choice at home and away (``set_quality_defaults``), held
    for every later item. Auto both, until the UI says otherwise; nothing
    here ever moves them."""

    def __init__(self):
        self._lock = threading.Lock()
        self.home = AUTO
        self.away = AUTO

    def update(self, home=None, away=None):
        with self._lock:
            if home is not None:
                self.home = home
            if away is not None:
                self.away = away

    def reset(self):
        self.update(AUTO, AUTO)

    def for_video(self, video):
        with self._lock:
            return self.home if is_local(video) else self.away


DEFAULTS = Defaults()

#: Set on a video once its first start has had the default applied, so a
#: retry of the same video (or anything else that plays it again) keeps the
#: choice it had.
_SEEN = "_fluentfin_quality_default"
#: 1.6.1: the default choice `apply_default` wrote, for `settle_default`.
_DEFAULT_CHOICE = "_fluentfin_quality_default_choice"


def apply_default(video, defaults=None):
    """At the first start of ``video``: the default for where the server is
    (1.5's local-or-remote rule, `utils.is_local_domain`, on the video's
    queue). Leaves an override something else already wrote (the shim's
    forced transcode) alone. 1.6.1 (the 1.6 review's queue: the defaults
    skipped the checks a choice by hand has): a cap is not asked for an
    account that may not have video converted (`may_convert_video`; the
    file's bitrate, which `refused_up_front` weighs, is not known before the
    first start, and such an account gets the file as it is either way).
    Never raises."""
    try:
        if getattr(video, _SEEN, False):
            return
        setattr(video, _SEEN, True)
        if getattr(video, "trs_ovr", None) is not None:
            return
        choice = (defaults or DEFAULTS).for_video(video)
        if choice.is_auto:
            return
        where = "home" if is_local(video) else "away"
        item = getattr(video, "item_id", "an item")
        if choice.is_cap and not may_convert_video(getattr(video, "client",
                                                           None)):
            log.info("Quality for %s: the %s default's cap is not asked for "
                     "(the account may not have video converted)", item,
                     where)
            return
        video.trs_ovr = override_for(choice)
        setattr(video, _DEFAULT_CHOICE, choice)
        log.info("Quality for %s: the %s default", item, where)
    except Exception:
        log.debug("could not apply the default quality", exc_info=True)


def settle_default(video):
    """1.6.1: after the first start of ``video`` with a default cap: a cap the
    server ignored (`cap_ignored`: the file sent as it is although it is over
    the cap) reads Original, as a change by hand does
    (`Bridge._change_quality`), but quietly: the viewer chose nothing. True
    when it changed the choice. Never raises."""
    try:
        choice = getattr(video, _DEFAULT_CHOICE, None)
        if not isinstance(choice, Choice) or getattr(
                video, "trs_ovr", None) != override_for(choice):
            return False
        if not cap_ignored(video, choice):
            return False
        video.trs_ovr = override_for(ORIGINAL)
        log.info("Quality for %s: the server sent it as it is, over the "
                 "default's cap; it reads Original", getattr(
                     video, "item_id", "an item"))
        return True
    except Exception:
        log.debug("could not settle the default quality", exc_info=True)
        return False


def is_local(video):
    """Whether ``video`` plays from a server at home (`media.Media.is_local`:
    the address the client reaches the server at). True with no queue to
    ask, as the shim's Media says with no client."""
    parent = getattr(video, "parent", None)
    if parent is None:
        return True
    return bool(getattr(parent, "is_local", True))


# -- the account ------------------------------------------------------------

_POLICY_ATTR = "_jms_user_policy"   # user_policy._CACHE_ATTR


def remember_policy(client, me):
    """``connect``'s ``Users/Me`` answer: the user's policy, cached where
    `user_policy.policy_for` looks, so the quality check costs no request."""
    policy = (me or {}).get("Policy") if isinstance(me, dict) else None
    if not isinstance(policy, dict):
        return
    try:
        setattr(client, _POLICY_ATTR, policy)
    except Exception:
        pass


def may_convert_video(client):
    """False only when the server said this account may not have video
    converted (``EnableVideoPlaybackTranscoding``). Fails open, as
    `user_policy` does: an unknown policy is no reason to refuse."""
    from . import user_policy

    try:
        policy = user_policy.policy_for(client) or {}
    except Exception:
        return True
    return policy.get("EnableVideoPlaybackTranscoding") is not False


def _source_bitrate(video):
    value = (getattr(video, "media_source", None) or {}).get("Bitrate")
    return value if proto._is_int(value) and value > 0 else None


def refused_up_front(video, choice):
    """A cap under the file's own bitrate for an account that may not have
    video converted: the server would refuse it, so nothing is asked."""
    if choice is None or not choice.is_cap:
        return False
    source = _source_bitrate(video)
    if source is None or choice.bitrate >= source:
        return False
    return not may_convert_video(getattr(video, "client", None))


def cap_ignored(video, choice):
    """After a change to a cap took: the server still sends the file as it is
    although it is over the cap (it plays directly for this account whatever
    is asked). Then the user is told, and the choice reads Original."""
    if choice is None or not choice.is_cap:
        return False
    if proto.play_method_name(video) == "Transcode":
        return False
    source = _source_bitrate(video)
    return source is not None and source > choice.bitrate


# -- what is playing --------------------------------------------------------


def _first(streams, kind):
    return next((s for s in streams if s.get("Type") == kind), None)


def _int(value):
    return value if proto._is_int(value) else None


def _target_bitrate(transcoding_url):
    """The bits per second a transcode was asked for: its ``VideoBitrate``
    plus ``AudioBitrate``, read from the url's query. Nothing else of the
    url is kept (it carries the access token)."""
    try:
        query = urllib.parse.parse_qs(
            urllib.parse.urlparse(transcoding_url or "").query)
    except Exception:
        return None
    total = 0
    for key in ("VideoBitrate", "AudioBitrate"):
        values = [v for k, v in query.items() if k.lower() == key.lower()]
        try:
            total += int(values[0][0]) if values else 0
        except (TypeError, ValueError, IndexError):
            pass
    return total or None


def stream_bitrate(video):
    """The stream as the server sends it: the transcode's target, else the
    file's own bitrate."""
    source = getattr(video, "media_source", None) or {}
    if proto.play_method_name(video) == "Transcode":
        target = _target_bitrate(source.get("TranscodingUrl"))
        if target is not None:
            return target
    return _source_bitrate(video)


def stream_payload(video):
    """The ``stream`` event (BRIDGE.md section 5) for ``video``."""
    source = getattr(video, "media_source", None) or {}
    streams = source.get("MediaStreams") or []
    picture = _first(streams, "Video") or {}
    info = getattr(video, "playback_info", None) or {}
    reasons = [r for r in (getattr(video, "transcode_reasons", None) or [])
               if isinstance(r, str)]
    return {
        "itemId": proto.item_id_out(getattr(video, "item_id", None)),
        "playSessionId": info.get("PlaySessionId"),
        "playMethod": proto.play_method_name(video),
        "transcodeReasons": reasons,
        "bitrate": stream_bitrate(video),
        "sourceBitrate": _source_bitrate(video),
        "sourceWidth": _int(picture.get("Width")),
        "sourceHeight": _int(picture.get("Height")),
        "quality": choice_of(video).payload(),
        "isLocal": is_local(video),
    }


# -- Playback stats ---------------------------------------------------------

#: mpv's properties `stats` reads, in one place (each one read per call).
STATS_PROPERTIES = (
    "video-params", "hwdec-current", "container-fps", "estimated-vf-fps",
    "frame-drop-count", "decoder-frame-drop-count", "vo-delayed-frame-count",
    "audio-codec-name", "audio-params", "video-bitrate", "audio-bitrate",
    "demuxer-cache-duration", "demuxer-cache-state", "cache-speed",
    "file-format", "video-format", "mpv-version", "aid", "sid",
)


def read_property(player, name):
    """One mpv property, None when mpv has no answer. libmpv reads in this
    process (`_get_property`, which takes any name, ``a/b`` paths too); the
    external backend asks over its IPC (``get_property``)."""
    try:
        getter = getattr(type(player), "_get_property", None)
        if getter is not None:
            return getter(player, name)
        return player.command("get_property", name)
    except Exception:
        return None


def _number(value):
    """A number mpv reported, None for anything else. 1.6.1: also for NaN
    and the infinities (a frame rate mpv has not worked out yet): the
    protocol's JSON has no word for them, and the answer could not be sent
    at all."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def _hdr(picture):
    """The server's word for the picture's range (VideoRangeType: SDR, HDR10,
    HDR10Plus, HLG, DOVI and its variants), else VideoRange."""
    for key in ("VideoRangeType", "VideoRange"):
        value = picture.get(key)
        if isinstance(value, str) and value and value != "Unknown":
            return value
    return None


def _stream(streams, kind, index):
    if index is None:
        return None
    return next((s for s in streams if s.get("Type") == kind
                 and s.get("Index") == index), None)


def stats(pm, video, read=None, deadline=None, cancelled=None):
    """The ``get_stats`` result (BRIDGE.md section 4): what the server
    negotiated and what mpv reports now. Every field may be null. Past
    ``deadline`` (`time.monotonic`), or once ``cancelled`` is set, no
    further property is asked: those read null."""
    read = read or (lambda name: read_property(pm._player, name))
    values = {}
    for name in STATS_PROPERTIES:
        if ((deadline is not None and time.monotonic() >= deadline)
                or (cancelled is not None and cancelled.is_set())):
            values[name] = None
            continue
        values[name] = read(name)
    source = getattr(video, "media_source", None) or {}
    streams = source.get("MediaStreams") or []
    picture = _first(streams, "Video") or {}
    params = values["video-params"] if isinstance(
        values["video-params"], dict) else {}
    audio_params = values["audio-params"] if isinstance(
        values["audio-params"], dict) else {}
    cache = values["demuxer-cache-state"] if isinstance(
        values["demuxer-cache-state"], dict) else {}
    hwdec = values["hwdec-current"]
    if not isinstance(hwdec, str) or hwdec in ("", "no"):
        hwdec = None
    aid = proto.audio_index_from_mpv(video, values["aid"])
    sid = proto.subtitle_index_from_mpv(
        video, values["sid"], dict(getattr(pm, "external_subtitles", {}) or {}))
    audio = _stream(streams, "Audio", aid) or {}
    subtitle = _stream(streams, "Subtitle", sid)
    measured = [v for v in (_number(values["video-bitrate"]),
                            _number(values["audio-bitrate"])) if v]
    frame_rate = (_number(values["container-fps"])
                  or _number(values["estimated-vf-fps"]))
    dropped = [v for v in (_number(values["frame-drop-count"]),
                           _number(values["decoder-frame-drop-count"]))
               if v is not None]
    version = values["mpv-version"]
    return {
        "itemId": proto.item_id_out(getattr(video, "item_id", None)),
        "playMethod": proto.play_method_name(video),
        "transcodeReasons": [r for r in (getattr(
            video, "transcode_reasons", None) or []) if isinstance(r, str)],
        "container": source.get("Container") or None,
        "streamFormat": values["file-format"] if isinstance(
            values["file-format"], str) else None,
        "videoCodec": picture.get("Codec") or None,
        "videoProfile": picture.get("Profile") or None,
        # what mpv decodes: a transcode's own codec (h264 from an HEVC file)
        "streamVideoCodec": values["video-format"] if isinstance(
            values["video-format"], str) else None,
        "sourceWidth": _int(picture.get("Width")),
        "sourceHeight": _int(picture.get("Height")),
        "width": _int(params.get("w")),
        "height": _int(params.get("h")),
        "frameRate": frame_rate,
        "hdr": _hdr(picture),
        "hardwareDecoding": hwdec,
        "droppedFrames": int(sum(dropped)) if dropped else None,
        "delayedFrames": _int(values["vo-delayed-frame-count"]),
        "audioCodec": (values["audio-codec-name"] if isinstance(
            values["audio-codec-name"], str) else None)
        or audio.get("Codec") or None,
        "audioChannels": _int(audio_params.get("channel-count"))
        or _int(audio.get("Channels")),
        "audioSampleRate": _int(audio_params.get("samplerate"))
        or _int(audio.get("SampleRate")),
        "audioTrack": audio.get("DisplayTitle") or audio.get("Title") or None,
        "subtitleFormat": (subtitle or {}).get("Codec") or None,
        "subtitlesOff": subtitle is None,
        "streamBitrate": stream_bitrate(video),
        "measuredBitrate": int(sum(measured)) if measured else None,
        "bufferSeconds": _number(values["demuxer-cache-duration"]),
        "bufferBytes": _int(cache.get("fw-bytes")),
        "networkSpeed": _int(values["cache-speed"]),
        "isLocal": is_local(video),
        "mpvVersion": version if isinstance(version, str) else None,
    }


class StatsReads:
    """``get_stats`` beside the command queue (BRIDGE.md section 4): it never
    delays another command. One read at a time on a thread of its own (a
    request while one is being read is answered at once), answered within
    `BUDGET`: the result, or ``mpv_failed`` when mpv has not answered by
    then. A ``stop``, ``play`` or ``connect`` (`end`) answers the read in
    progress at once and stops its reads. Exactly one answer per request.

    Review (1.6): read on the control worker, 19 property reads of a hung
    mpv (each waiting up to the IPC client's 120 s) queued ahead of a
    ``stop``, and the UI, giving up after 2 s, asked again behind them."""

    #: Seconds from the request to its answer, at most.
    BUDGET = 1.5

    def __init__(self, writer):
        self._writer = writer
        self._lock = threading.Lock()
        self._busy = False
        self._open = None

    def ask(self, req_id, read):
        """Reads ``read(deadline, cancelled)`` for request ``req_id`` on a
        thread of its own; never blocks the caller."""
        with self._lock:
            busy = self._busy
            if not busy:
                self._busy = True
                entry = {"id": req_id, "done": False,
                         "cancel": threading.Event()}
                self._open = entry
        if busy:
            self._writer.fail(req_id, proto.MPV_FAILED,
                              "mpv has not answered the last stats read yet")
            return
        deadline = time.monotonic() + self.BUDGET
        timer = threading.Timer(self.BUDGET, self._answer, (
            entry, False, (proto.MPV_FAILED, "mpv did not answer in time")))
        timer.daemon = True

        def work():
            try:
                result = read(deadline, entry["cancel"])
            except Exception:
                log.debug("the stats could not be read", exc_info=True)
                self._answer(entry, False, (proto.INTERNAL,
                                            "the stats could not be read"))
            else:
                self._answer(entry, True, result)
            finally:
                timer.cancel()
                with self._lock:
                    self._busy = False
                    if self._open is entry:
                        self._open = None

        timer.start()
        threading.Thread(target=work, name="bridge-stats", daemon=True).start()

    def _answer(self, entry, ok, payload):
        with self._lock:
            if entry["done"]:
                return
            entry["done"] = True
        if ok:
            try:
                self._writer.respond(entry["id"], payload)
                return
            except ValueError:
                # 1.6.1: a result the protocol's JSON cannot carry (a NaN
                # `_number` did not catch) went unanswered: still one answer
                log.debug("the stats could not be sent", exc_info=True)
                payload = (proto.INTERNAL, "the stats could not be read")
        self._writer.fail(entry["id"], *payload)

    def end(self, code, message):
        """A stop, play or connect, or the shutdown: the read in progress
        asks mpv nothing more, and its request is answered now."""
        with self._lock:
            entry = self._open
        if entry is not None:
            entry["cancel"].set()
            self._answer(entry, False, (code, message))

    @property
    def busy(self):
        with self._lock:
            return self._busy
