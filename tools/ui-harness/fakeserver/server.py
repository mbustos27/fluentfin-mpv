"""A realistic fake Jellyfin server on 127.0.0.1 for running Nixie against: FluentFin's pages (login, home,
libraries, item pages, search) and the mpv backend's playback (PlaybackInfo, direct play with two audio and three
subtitle tracks, media segments, trickplay, progress reports). Standard library only.

    python server.py --out <dir> [--media <dir> | --mpv <mpv.exe> --media <dir>] [--port 0]
                     [--latency MS] [--latency-rule REGEX=MS|hang ...] [--jitter MS] [--ready-file <file>]

- Login: the generated test credentials in credentials.json (this folder), never real ones, or Quick Connect with no
  password at all: the client asks for a code (POST /QuickConnect/Initiate), the harness approves the code the client
  shows (POST /__harness/quickconnect/approve {"code": ...}, as a user would on another device), and the client's
  next GET /QuickConnect/Connect sees it and signs in (POST /Users/AuthenticateWithQuickConnect). Either way each
  sign-in gets a fresh random access token; tokens and Quick Connect secrets are never printed or logged. A signed-in
  client approves another device's code with POST /QuickConnect/Authorize?code=... (Settings > Other devices).
- Library: library.py (several views, hundreds of movies and shows with seasons and episodes, continue watching, next
  up, latest), artwork from imagegen.py at real sizes (posters 1000x1500, backdrops 1920x1080, ...), resized to the
  fillHeight/fillWidth/maxHeight/maxWidth asked for, as Jellyfin does.
- Media: media.py's test video (built with --mpv into --media when missing).
- Latency: --latency for every request, --latency-rule REGEX=MS for the paths matching REGEX (first match wins,
  'hang' = never answer, 'status:NNN' = answer that status (400-599) at once, 'empty' = 200 with no items); also
  POST /__harness/latency {"default": ms, "rules": [["regex", ms], ...]} while running.
- Segments: POST /__harness/segments {"outro_seconds": N}: the end credits (Outro) run the last N seconds (40).
- Streams: POST /__harness/streams {"encode_subtitle": true} serves the last embedded subtitle as burnt in
  ("Encode"), so picking it renegotiates the stream (a track change that can fail, with a PlaybackInfo rule).
- Request log: <out>/requests.jsonl, one line per request (time, method, path, query with keys/tokens redacted,
  status, bytes, total ms, injected delay, client); <out>/summary.json on shutdown and on GET /__harness/summary.
  Unknown endpoints answer 404 and are listed there as unhandled.
- Control (127.0.0.1 only, no auth): GET /__harness/fixture (the showcase items the UI harness navigates to),
  GET /__harness/trailer/<id> (Nixie 1.3.1: a still page standing in for YouTube's trailer embed),
  GET /__harness/release/latest and POST /__harness/release {"version"} (Nixie 1.3.1: GitHub's latest-release answer for
  the app's update check; FAKESERVER_RELEASE sets the version at start, 0.0.1 by default),
  GET /__harness/summary, POST /__harness/latency, POST /__harness/streams, POST /__harness/shutdown,
  GET /__harness/quickconnect (the codes waiting, never their secrets), POST /__harness/quickconnect/approve,
  POST /__harness/libraries {"rename": {"old": "new"}, "add": ["name"]} (the views change; clients hear LibraryChanged).
"""

import argparse
import base64
import collections
import datetime
import hashlib
import html
import json
import os
import random
import re
import secrets
import socket
import struct
import sys
import threading
import time
import urllib.parse
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import imagegen  # noqa: E402
import library  # noqa: E402
import media as media_mod  # noqa: E402
import admin  # noqa: E402  (Nixie 1.2.0: the administration endpoints)

def release_answer(version):
    """Nixie 1.3.1: GitHub's latest-release answer for this repository at `version` (its page and assets on github.com,
    which the app only opens or downloads on a click; a harness run never clicks them)."""
    base = "https://github.com/mbustos27/fluentfin-mpv/releases"
    msi = "Nixie-%s-win-x64.msi" % version
    return {"tag_name": "v" + version, "name": "Nixie " + version, "draft": False, "prerelease": False,
            "html_url": "%s/tag/v%s" % (base, version), "body": "Harness release " + version,
            "assets": [{"name": msi, "browser_download_url": "%s/download/v%s/%s" % (base, version, msi)},
                       {"name": msi + ".sha256", "browser_download_url": "%s/download/v%s/%s.sha256" % (base, version, msi)}]}


# Nixie 1.3.1: the trailer embed's stand-in (GET /__harness/trailer/<id>): a still frame of a "film" with a play bar, made of
# CSS only (no script, nothing loaded from anywhere), so the theatre window's screenshots show a picture where the video goes
TRAILER_PAGE = """<!doctype html><html><head><meta charset="utf-8"><title>Harness trailer {id}</title><style>
html,body{margin:0;height:100%;overflow:hidden;background:#000;font-family:Segoe UI,sans-serif}
.f{position:absolute;inset:0;background:linear-gradient(180deg,#1c0512 0%,#500723 38%,#c9305a 62%,#f5b3a6 78%,#3a0f1f 79%,#12040a 100%)}
.s{position:absolute;left:62%;top:44%;width:16vw;height:16vw;margin:-8vw;border-radius:50%;background:linear-gradient(#fff4ee,#f5a8a5 50%,#f96985)}
.t{position:absolute;left:6%;bottom:22%;color:#fff4ee;font-size:4.2vw;font-weight:600;letter-spacing:.3vw;text-shadow:0 .3vw 1vw #000}
.k{display:block;font-size:1.4vw;letter-spacing:.5vw;color:#f7aa9c}
.b{position:absolute;left:0;right:0;bottom:0;height:12%;background:linear-gradient(transparent,rgba(0,0,0,.8))}
.p{position:absolute;left:3%;right:3%;bottom:5%;height:.5vw;background:rgba(255,255,255,.35)}
.p i{display:block;width:34%;height:100%;background:#f96985}
</style></head><body><div class="f"></div><div class="s"></div>
<div class="t"><span class="k">HARNESS TRAILER &middot; {id}</span>A Quiet Harbor</div><div class="b"></div><div class="p"><i></i></div></body></html>"""

TICKS = library.TICKS
TOKEN_RE = re.compile(r'Token="([^"]*)"')
CLIENT_RE = re.compile(r'Client="([^"]*)"')
SECRET_KEYS = {"api_key", "apikey", "token", "x-emby-token", "pw", "password", "secret"}
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
VERSION = "10.10.7"

BASE_SIZES = {  # (kind, image type) -> (width, height); None kind = any
    ("movie", "Primary"): (1000, 1500), ("series", "Primary"): (1000, 1500), ("season", "Primary"): (1000, 1500),
    ("person", "Primary"): (500, 750), ("episode", "Primary"): (1280, 720), ("view", "Primary"): (960, 540),
    (None, "Backdrop"): (1920, 1080), (None, "Thumb"): (1280, 720), (None, "Logo"): (800, 310),
    (None, "Banner"): (1000, 185), (None, "Chapter"): (1280, 720), (None, "Art"): (1000, 562),
    (None, "Disc"): (1000, 1000), (None, "Primary"): (1000, 1500),
}


def rule_value(value):
    """A rule's value: ms (a number) of delay, 'hang' (never answer), 'status:NNN' (answer NNN at once: a failing
    server; 400-599 only, as the answer has a JSON body) or 'empty' (200 with no items: an empty library). Anything else
    is a ValueError."""
    s = str(value).strip().lower()
    if s in ("hang", "empty"):
        return s
    if s.startswith("status:"):
        code = s.split(":", 1)[1]
        if not code.isdigit() or not 400 <= int(code) <= 599:
            raise ValueError("status:%s is not an HTTP error status (400-599)" % code)
        return "status:%d" % int(code)
    return float(value)


class Server:
    def __init__(self, out, media_meta, credentials, latency=0.0, rules=(), jitter=0.0, port=0, seed=20261115,
                 admin=True):
        self.out = out
        os.makedirs(out, exist_ok=True)
        self.media = media_meta
        self.creds = credentials
        self.server_id = library.norm_id(hashlib.md5(b"ffh-server").hexdigest())
        self.users = {}
        for entry in [credentials] + ([credentials["second_user"]] if credentials.get("second_user") else []):
            uid = hashlib.md5(("ffh-user:" + entry["username"]).encode()).hexdigest()
            self.users[uid] = {"name": entry["username"], "password": entry["password"]}
        self.user_id = next(iter(self.users))
        self.admin = admin
        runtime = int(media_meta["duration_ms"]) * 10_000
        self.lib = library.Library(self.server_id, self.user_id, runtime, seed=seed)
        self.tokens = {}            # token -> user id (never logged)
        self.quick = {}             # Quick Connect secret -> {code, user (once approved), client...} (never logged)
        self.lock = threading.Lock()
        self.latency = latency
        self.rules = [(re.compile(r, re.I), rule_value(ms)) for r, ms in rules]
        self.jitter = jitter
        self.encode_subtitle = False    # POST /__harness/streams: the last embedded subtitle burnt in (a restart to pick)
        # Nixie 1.3.1: the version GET /__harness/release/latest answers (the app's update check in a harness run)
        self.release_version = os.environ.get("FAKESERVER_RELEASE") or "0.0.1"
        self.seq = 0
        self.counts = collections.Counter()
        self.unhandled = collections.Counter()
        self.sessions = []          # PlaySessionIds issued
        self.reports = []           # playback reports: kind, item, position, play session, client
        self.sockets = []
        self.image_cache = collections.OrderedDict()
        self.started = time.time()
        self.log_file = open(os.path.join(out, "requests.jsonl"), "a", encoding="utf-8")
        self.httpd = ThreadingHTTPServer(("127.0.0.1", port), self._handler())
        self.httpd.daemon_threads = True
        self.port = self.httpd.server_address[1]
        self.url = "http://127.0.0.1:%d" % self.port
        self._srt = {}

    # ------------------------------------------------------------------ plumbing
    def serve(self):
        self.httpd.serve_forever(poll_interval=0.2)

    def stop(self):
        self.write_summary()
        threading.Thread(target=self.httpd.shutdown, daemon=True).start()

    def delay_for(self, path):
        for rx, ms in self.rules:
            if rx.search(path):
                return ms
        return self.latency

    def record(self, entry):
        with self.lock:
            self.seq += 1
            entry["seq"] = self.seq
            self.counts[entry.get("route") or "?"] += 1
            if entry.get("unhandled"):
                self.unhandled["%s %s" % (entry["method"], entry["pattern"])] += 1
            self.log_file.write(json.dumps(entry) + "\n")
            self.log_file.flush()

    def summary(self):
        with self.lock:
            return {"url": self.url, "uptime_s": round(time.time() - self.started, 1), "requests": self.seq,
                    "by_route": dict(self.counts.most_common()), "unhandled": dict(self.unhandled.most_common()),
                    "play_sessions": list(self.sessions), "reports": list(self.reports),
                    "latency": {"default_ms": self.latency, "rules": [[r.pattern, ms] for r, ms in self.rules],
                                "jitter_ms": self.jitter}}

    def write_summary(self):
        with open(os.path.join(self.out, "summary.json"), "w", encoding="utf-8") as f:
            json.dump(self.summary(), f, indent=2)

    # ------------------------------------------------------------------ model helpers
    def user_dto(self, uid):
        u = self.users[uid]
        now = library.iso(datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None))
        return {"Name": u["name"], "ServerId": self.server_id, "ServerName": "Harness Jellyfin (fake)", "Id": uid,
                "HasPassword": True, "HasConfiguredPassword": True, "HasConfiguredEasyPassword": False,
                "EnableAutoLogin": False, "LastLoginDate": now, "LastActivityDate": now,
                "Configuration": {"AudioLanguagePreference": "", "PlayDefaultAudioTrack": True,
                                  "SubtitleLanguagePreference": "", "DisplayMissingEpisodes": False,
                                  "GroupedFolders": [], "SubtitleMode": "Default", "DisplayCollectionsView": False,
                                  "EnableLocalPassword": False, "OrderedViews": [], "LatestItemsExcludes": [],
                                  "MyMediaExcludes": [], "HidePlayedInLatest": True, "RememberAudioSelections": True,
                                  "RememberSubtitleSelections": True, "EnableNextEpisodeAutoPlay": True,
                                  "CastReceiverId": "F007D354"},
                "Policy": {"IsAdministrator": self.admin and uid == self.user_id, "IsHidden": False,
                           "IsDisabled": False, "EnableAllFolders": True, "EnableMediaPlayback": True,
                           "EnableAudioPlaybackTranscoding": True, "EnableVideoPlaybackTranscoding": True,
                           "EnablePlaybackRemuxing": True, "EnableContentDeletion": False,
                           "EnableContentDownloading": True, "EnableRemoteControlOfOtherUsers": False,
                           "EnableSharedDeviceControl": True, "EnableRemoteAccess": True,
                           "EnableLiveTvManagement": False, "EnableLiveTvAccess": False,
                           "EnableSyncTranscoding": True, "EnableMediaConversion": True,
                           "EnableUserPreferenceAccess": True, "EnablePublicSharing": True,
                           "AccessSchedules": [], "BlockUnratedItems": [], "EnabledDevices": [],
                           "EnabledChannels": [], "EnabledFolders": [], "BlockedTags": [], "AllowedTags": [],
                           "InvalidLoginAttemptCount": 0, "LoginAttemptsBeforeLockout": -1, "MaxActiveSessions": 0,
                           "RemoteClientBitrateLimit": 0, "SyncPlayAccess": "CreateAndJoinGroups",
                           "AuthenticationProviderId": "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
                           "PasswordResetProviderId": "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider"}}

    def media_streams(self, item):
        m, iid = self.media, item["Id"]
        v = m["video"]
        streams = [{"Codec": v["codec"], "TimeBase": "1/1000", "VideoRange": "SDR", "VideoRangeType": "SDR",
                    "DisplayTitle": "720p %s SDR" % v["codec"].upper(), "IsInterlaced": False, "IsAVC": v["codec"] == "h264",
                    "BitRate": v["bitrate"], "BitDepth": 8, "RefFrames": 1, "IsDefault": True, "IsForced": False,
                    "Height": v["height"], "Width": v["width"], "AverageFrameRate": v["fps"], "RealFrameRate": v["fps"],
                    "Profile": "Main" if v["codec"] == "h264" else "Simple Profile", "Type": "Video",
                    "AspectRatio": "16:9", "Index": 0, "IsExternal": False, "IsTextSubtitleStream": False,
                    "SupportsExternalStream": False, "PixelFormat": "yuv420p", "Level": 40}]
        for k, a in enumerate(m["audio"]):
            streams.append({"Codec": a["codec"], "Language": a["language"], "Title": a["title"],
                            "DisplayTitle": "%s - %s - Stereo%s" % (a["title"], a["codec"].upper(), " - Default" if k == 0 else ""),
                            "ChannelLayout": "stereo", "BitRate": a["bitrate"], "Channels": a["channels"],
                            "SampleRate": 48000, "IsDefault": k == 0, "IsForced": False, "Type": "Audio",
                            "Index": 1 + k, "IsExternal": False, "IsTextSubtitleStream": False,
                            "SupportsExternalStream": False, "Level": 0})
        base = 1 + len(m["audio"])
        for k, s in enumerate(m["subtitles"]):
            # the server burns a subtitle in ("Encode") when the client cannot show it: picking it restarts the stream
            encode = self.encode_subtitle and k == len(m["subtitles"]) - 1
            streams.append({"Codec": s["codec"], "Language": s["language"], "Title": s["title"],
                            "DisplayTitle": "%s - %s" % (s["title"], s["codec"].upper()), "IsDefault": False,
                            "IsForced": False, "IsHearingImpaired": False, "Type": "Subtitle", "Index": base + k,
                            "IsExternal": False, "IsTextSubtitleStream": True, "SupportsExternalStream": True,
                            "DeliveryMethod": "Encode" if encode else "Embed", "LocalizedUndefined": "Undefined",
                            "LocalizedDefault": "Default", "LocalizedForced": "Forced", "Level": 0})
        ext = m["external_subtitle"]
        index = base + len(m["subtitles"])
        streams.append({"Codec": ext["codec"], "Language": ext["language"], "Title": ext["title"],
                        "DisplayTitle": "%s - %s - External" % (ext["title"], ext["codec"].upper()), "IsDefault": False,
                        "IsForced": False, "IsHearingImpaired": False, "Type": "Subtitle", "Index": index,
                        "IsExternal": True, "IsTextSubtitleStream": True, "SupportsExternalStream": True,
                        "DeliveryMethod": "External", "IsExternalUrl": False,
                        "DeliveryUrl": "/Videos/%s/%s/Subtitles/%d/0/Stream.srt" % (iid, iid, index),
                        "Path": self.lib.path(item)[:-4] + ".fr.srt", "Level": 0})
        return streams

    def media_source(self, item, audio=None, subtitle=None):
        iid = item["Id"]
        return {"Protocol": "File", "Id": iid, "Path": self.lib.path(item), "Type": "Default", "Container": "mkv",
                "Size": self.media["size"], "Name": item["Name"], "IsRemote": False, "ETag": library._tag(iid, "src")[:32],
                "RunTimeTicks": self.lib.runtime, "ReadAtNativeFramerate": False, "IgnoreDts": False,
                "IgnoreIndex": False, "GenPtsInput": False, "SupportsTranscoding": True, "SupportsDirectStream": True,
                "SupportsDirectPlay": True, "IsInfiniteStream": False, "UseMostCompatibleTranscodingProfile": False,
                "RequiresOpening": False, "RequiresClosing": False, "RequiresLooping": False, "SupportsProbing": True,
                "VideoType": "VideoFile", "MediaStreams": self.media_streams(item), "MediaAttachments": [],
                "Formats": [], "Bitrate": self.media["video"]["bitrate"] + sum(a["bitrate"] for a in self.media["audio"]),
                "RequiredHttpHeaders": {}, "TranscodingSubProtocol": "http", "HasSegments": True,
                "DefaultAudioStreamIndex": 1 if audio is None else audio,
                "DefaultSubtitleStreamIndex": -1 if subtitle is None else subtitle}

    def trickplay(self, item):
        t = self.media.get("trickplay") or {}
        if not t.get("ok"):
            return None
        return {item["Id"]: {str(t["width"]): {"Width": t["width"], "Height": t["height"], "TileWidth": t["tile"],
                                               "TileHeight": t["tile"], "ThumbnailCount": t["thumbnails"],
                                               "Interval": t["interval_ms"], "Bandwidth": 2_000_000}}}

    def detail(self, item):
        d = self.lib.dto(item, detail=True)
        if item["kind"] in ("movie", "episode"):
            source = self.media_source(item)
            d["MediaSources"] = [source]
            d["MediaStreams"] = source["MediaStreams"]
            tp = self.trickplay(item)
            if tp:
                d["Trickplay"] = tp
        return d

    def photo(self, item, kind):
        """FAKESERVER_PHOTO_DIR=<folder of .jpg>: photographic art (the JPEG as it is, one per item) for Primary, Backdrop and Thumb, so
        the UI can be judged on real pictures (faces, scenes) instead of the generated colour blocks. None when unset."""
        folder = os.environ.get("FAKESERVER_PHOTO_DIR")
        # UXCAP (the 1.4.0 review captures): FAKESERVER_PHOTO_KINDS limits the photos to some image types (e.g. Backdrop,Thumb)
        kinds = [k.strip() for k in os.environ.get("FAKESERVER_PHOTO_KINDS", "Primary,Backdrop,Thumb").split(",") if k.strip()]
        if not folder or kind not in ("Primary", "Backdrop", "Thumb") or kind not in kinds:
            return None
        files = sorted(f for f in os.listdir(folder) if f.lower().endswith((".jpg", ".jpeg")))
        if not files:
            return None
        pick = files[int(hashlib.md5(item["Id"].encode()).hexdigest(), 16) % len(files)]
        with open(os.path.join(folder, pick), "rb") as f:
            return f.read()

    def image(self, item, kind, index, query):
        def num(key):
            v = query.get(key)
            try:
                return int(float(v[0])) if v else None
            except ValueError:
                return None
        bw, bh = BASE_SIZES.get((item["kind"], kind)) or BASE_SIZES.get((None, kind)) or (1000, 1500)
        scale = 1.0
        fw, fh = num("fillwidth"), num("fillheight")
        mw, mh = num("maxwidth") or num("width"), num("maxheight") or num("height")
        if fw or fh:
            scale = max((fw or 0) / bw, (fh or 0) / bh)
        elif mw or mh:
            scale = min((mw or bw) / bw, (mh or bh) / bh)
        scale = min(1.0, scale) if scale > 0 else 1.0
        w, h = max(1, round(bw * scale)), max(1, round(bh * scale))
        key = (item["Id"], kind, index, w, h)
        with self.lock:
            data = self.image_cache.get(key)
            if data is not None:
                self.image_cache.move_to_end(key)
                return data
        title, sub = item["Name"], ""
        if item["kind"] in ("movie", "series"):
            sub = "%d - %s" % (item["year"], item["rating"])
        elif item["kind"] == "season":
            title, sub = self.lib.items[item["series"]]["Name"], item["Name"]
        elif item["kind"] == "episode":
            sub = "S%02dE%02d - %s" % (item["parent_index"], item["index"], self.lib.items[item["series"]]["Name"])
        elif item["kind"] == "view":
            sub = "Library"
        elif item["kind"] == "boxset":   # UXCAP
            sub = "Collection"
        if kind == "Chapter":
            sub = "Chapter %d" % ((index or 0) + 1)
        elif kind in ("Backdrop", "Thumb", "Banner", "Art"):
            sub = kind.upper()
        data = imagegen.render(item["Id"] + kind, title, sub, w, h, alpha=kind == "Logo")
        with self.lock:
            self.image_cache[key] = data
            while len(self.image_cache) > 600:
                self.image_cache.popitem(last=False)
        return data

    def subtitle_text(self, index, fmt):
        key = (index, fmt)
        if key in self._srt:
            return self._srt[key]
        m = self.media
        base = 1 + len(m["audio"])
        seconds = m["seconds"]
        if index == base + len(m["subtitles"]):
            with open(m["external_subtitle"]["path"], encoding="utf-8") as f:
                text = f.read()
        elif base <= index < base + len(m["subtitles"]):
            k = index - base
            text = media_mod.srt(media_mod.subtitle_cues(["[English]", "[Espanol]"][k] if k < 2 else "[Sub]", seconds,
                                                         every=4.0 if k == 0 else 5.0))
        else:
            return None
        if fmt == "vtt":
            text = "WEBVTT\n\n" + re.sub(r"(\d\d:\d\d:\d\d),(\d\d\d)", r"\1.\2", text)
        self._srt[key] = text
        return text

    def push_user_data(self, uid, items):
        if not items:
            return
        msg = {"MessageType": "UserDataChanged", "MessageId": uuid.uuid4().hex,
               "Data": {"UserId": uid, "UserDataList": [self.lib.user_data(i) for i in items]}}
        for ws in list(self.sockets):
            if ws.user_id == uid:
                ws.send_json(msg)

    # ------------------------------------------------------------------ HTTP
    def _handler(self):
        outer = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"
            server_version = "Kestrel"
            sys_version = ""

            def log_message(self, *args):
                pass

            def setup(self):
                super().setup()
                self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

            # every method goes through _dispatch
            def do_GET(self):
                self._dispatch("GET")

            def do_HEAD(self):
                self._dispatch("HEAD")

            def do_POST(self):
                self._dispatch("POST")

            def do_DELETE(self):
                self._dispatch("DELETE")

            def do_PUT(self):
                self._dispatch("PUT")

            def do_OPTIONS(self):
                self._dispatch("OPTIONS")

            def _dispatch(self, method):
                t0 = time.perf_counter()
                parsed = urllib.parse.urlsplit(self.path)
                path = urllib.parse.unquote(parsed.path)
                raw_query = urllib.parse.parse_qs(parsed.query, keep_blank_values=True)
                self.q = {k.lower(): v for k, v in raw_query.items()}
                self.method = method
                self.status, self.sent, self.route, self.pattern, self.unhandled = 0, 0, None, None, False
                self.extra = None
                self.body_bytes = b""
                if method in ("POST", "PUT", "DELETE"):
                    n = int(self.headers.get("Content-Length") or 0)
                    self.body_bytes = self.rfile.read(n) if n else b""
                auth = self.headers.get("Authorization") or self.headers.get("X-Emby-Authorization") or ""
                m = TOKEN_RE.search(auth)
                token = (m.group(1) if m else None) or self.headers.get("X-Emby-Token") or \
                    self.headers.get("X-MediaBrowser-Token") or (self.q.get("api_key") or self.q.get("apikey") or [None])[0]
                self.token_given = bool(token)
                self.uid = outer.tokens.get(token) if token else None
                cm = CLIENT_RE.search(auth)
                self.client = cm.group(1) if cm else ""
                delay = 0.0
                fault = None
                if not path.lower().startswith("/__harness/"):
                    rule = outer.delay_for(path)
                    if rule == "hang":
                        delay = -1.0
                    elif isinstance(rule, str):
                        fault = rule        # status:NNN or empty (rule_value)
                    else:
                        delay = max(0.0, float(rule) + (random.uniform(-outer.jitter, outer.jitter) if outer.jitter else 0))
                try:
                    if delay < 0:
                        self.route = "hang"
                        time.sleep(3600)
                        return
                    if delay:
                        time.sleep(delay / 1000.0)
                    if fault:
                        self._fault(fault, path)
                        return
                    self._route(method, path)
                except (ConnectionError, OSError):
                    self.close_connection = True
                except Exception as ex:  # a bug here must not take the server down
                    if not self.status:
                        self._json(500, {"error": type(ex).__name__})
                    print("fakeserver: %s %s failed: %r" % (method, path, ex), file=sys.stderr)
                finally:
                    entry = {"t": round(time.time(), 4), "method": method, "path": path, "query": self._redacted(),
                             "status": self.status, "bytes": self.sent, "ms": round((time.perf_counter() - t0) * 1000, 1),
                             "delay_ms": round(delay, 1), "client": self.client, "ua": self.headers.get("User-Agent") or "",
                             "auth": self.uid is not None, "route": self.route}
                    if self.unhandled:
                        entry["unhandled"] = True
                        entry["pattern"] = re.sub(r"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}",
                                                  "{id}", path)
                    if self.extra:
                        entry["report"] = self.extra
                    outer.record(entry)

            def _redacted(self):
                return {k: (["<redacted>"] if k in SECRET_KEYS else v) for k, v in self.q.items()}

            # ---------------- responses
            def _send(self, code, body=b"", ctype=None, headers=None):
                self.status = code
                self.send_response(code)
                if ctype:
                    self.send_header("Content-Type", ctype)
                for k, v in (headers or {}).items():
                    self.send_header(k, v)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                if self.method != "HEAD" and body:
                    self.wfile.write(body)
                self.sent = len(body)

            def _json(self, code, obj):
                if obj is None:
                    return self._send(code)
                return self._send(code, json.dumps(obj).encode("utf-8"), "application/json; charset=utf-8")

            def _fault(self, fault, path):
                """A rule's injected answer: status:NNN (that status, at once) or empty (200, no items: an empty
                library; /Items/Latest answers a list, everything else an item page)."""
                self.route = "fault:" + fault
                if fault == "empty":
                    empty = [] if path.lower().rstrip("/").endswith("/latest") else \
                        {"Items": [], "TotalRecordCount": 0, "StartIndex": 0}
                    return self._json(200, empty)
                return self._json(int(fault.split(":", 1)[1]), {"error": "injected by the harness"})

            def _body(self):
                try:
                    return json.loads(self.body_bytes) if self.body_bytes else {}
                except ValueError:
                    return {}

            def _one(self, key, default=None):
                v = self.q.get(key.lower())
                return v[0] if v else default

            def _need_auth(self):
                if self.uid is None:
                    self._json(401, None)
                    return False
                return True

            # ---------------- routing
            def _route(self, method, path):
                p = path.rstrip("/") or "/"
                low = p.lower()
                ID = r"([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"
                U = r"(?:/users/" + ID + r")?"

                def match(rx):
                    return re.fullmatch(rx, low)

                # harness control (the server listens on 127.0.0.1 only)
                if low.startswith("/__harness/"):
                    self.route = "harness"
                    return self._harness(method, low)
                # public
                if low == "/system/info/public":
                    self.route = "public-info"
                    return self._json(200, {"LocalAddress": outer.url, "ServerName": "Harness Jellyfin (fake)",
                                            "Version": VERSION, "ProductName": "Jellyfin Server",
                                            "OperatingSystem": "", "Id": outer.server_id,
                                            "StartupWizardCompleted": True})
                if low == "/system/ping":
                    self.route = "ping"
                    return self._send(200, b'"Jellyfin Server"', "application/json")
                if low == "/users/authenticatebyname" and method == "POST":
                    self.route = "authenticate"
                    return self._authenticate()
                if low == "/users/public":
                    self.route = "users-public"
                    return self._json(200, [])
                if low == "/quickconnect/enabled":
                    self.route = "quickconnect"
                    return self._json(200, True)
                if low == "/quickconnect/initiate" and method in ("POST", "GET"):
                    self.route = "quickconnect"
                    return self._quick_initiate()
                if low == "/quickconnect/connect":
                    self.route = "quickconnect"
                    entry = outer.quick.get(self._one("secret") or "")
                    return self._json(200, self._quick_dto(entry)) if entry else self._json(404, None)
                if low == "/users/authenticatewithquickconnect" and method == "POST":
                    self.route = "authenticate"
                    with outer.lock:
                        entry = outer.quick.get(str(self._body().get("Secret") or ""))
                        if entry and entry["user"]:
                            del outer.quick[entry["secret"]]   # one sign-in per approved code
                    if not entry or not entry["user"]:
                        return self._send(401, b"Error processing request.", "text/plain")
                    return self._signed_in(entry["user"])
                if low == "/quickconnect/authorize" and method == "POST":
                    # a signed-in user approves the code another device shows (Nixie: Settings > Other devices >
                    # Quick Connect), as jellyfin does: true, or 404 for a code that is not waiting
                    self.route = "quickconnect"
                    if not self._need_auth():
                        return
                    code = self._one("code") or ""
                    with outer.lock:
                        entry = next((e for e in outer.quick.values() if e["code"] == code), None)
                        if entry:
                            entry["user"] = self.uid
                    return self._json(200, True) if entry else self._json(404, None)
                if low.startswith("/quickconnect/"):
                    self.route = "quickconnect"
                    return self._json(401, None)
                if low == "/branding/configuration" or low == "/system/configuration/branding":
                    self.route = "branding"
                    return self._json(200, {"LoginDisclaimer": "", "CustomCss": "", "SplashscreenEnabled": True})
                if low in ("/branding/css", "/branding/css.css"):
                    self.route = "branding"
                    return self._send(200, b"", "text/css")
                if low == "/branding/splashscreen":
                    self.route = "image"
                    data = imagegen.render("splash", "Harness Jellyfin", "fake server - test data", 1920, 1080)
                    return self._send(200, data, "image/png")
                mm = match(r"/items/" + ID + r"/images/([a-z]+)(?:/(\d+))?")
                if mm and method in ("GET", "HEAD"):
                    self.route = "image"
                    item = outer.lib.get(mm.group(1))
                    if not item:
                        return self._json(404, None)
                    kind = mm.group(2).capitalize()
                    photo = outer.photo(item, kind)
                    data = photo or outer.image(item, kind, int(mm.group(3)) if mm.group(3) else None, self.q)
                    return self._send(200, data, "image/jpeg" if photo else "image/png", {"Cache-Control": "public, max-age=31536000",
                                                               "ETag": '"%s"' % library._tag(item["Id"], kind)})
                mm = match(r"/videos/" + ID + r"/(?:stream|stream\.[a-z0-9]+)") or match(r"/items/" + ID + r"/download")
                if mm and method in ("GET", "HEAD"):
                    self.route = "stream"
                    return self._file(outer.media["path"], "video/x-matroska")
                mm = match(r"/videos/" + ID + r"/" + ID + r"/subtitles/(\d+)(?:/\d+)?/stream\.([a-z]+)")
                if mm:
                    self.route = "subtitles"
                    text = outer.subtitle_text(int(mm.group(3)), mm.group(4))
                    if text is None:
                        return self._json(404, None)
                    return self._send(200, text.encode("utf-8"), "text/vtt" if mm.group(4) == "vtt" else "application/x-subrip")
                mm = match(r"/videos/" + ID + r"/trickplay/(\d+)/(\d+)\.jpg")
                if mm:
                    self.route = "trickplay"
                    t = outer.media.get("trickplay") or {}
                    sheet = os.path.join(t.get("dir", ""), "%s.jpg" % mm.group(3))
                    if str(t.get("width")) != mm.group(2) or not os.path.exists(sheet):
                        return self._json(404, None)
                    with open(sheet, "rb") as f:
                        return self._send(200, f.read(), "image/jpeg")
                if low == "/socket":
                    self.route = "websocket"
                    return self._websocket()

                # everything else needs a token
                if not self._need_auth():
                    self.route = "unauthorized"
                    return
                if admin.route(self, outer, method, p, low):   # Nixie 1.2.0: admin.py (logged to <out>/admin.jsonl)
                    return
                lib = outer.lib
                if low == "/system/info":
                    self.route = "system-info"
                    return self._json(200, {"ServerName": "Harness Jellyfin (fake)", "Version": VERSION,
                                            "Id": outer.server_id, "OperatingSystem": "Windows", "HasPendingRestart": False,
                                            "IsShuttingDown": False, "SupportsLibraryMonitor": True,
                                            "WebSocketPortNumber": outer.port, "CanSelfRestart": False,
                                            "CanLaunchWebBrowser": False, "HasUpdateAvailable": False,
                                            "TranscodingTempPath": "", "LogPath": "", "InternalMetadataPath": "",
                                            "CachePath": "", "ProgramDataPath": "", "ItemsByNamePath": "",
                                            "WebPath": "", "EncoderLocation": "System", "SystemArchitecture": "X64",
                                            "LocalAddress": outer.url, "StartupWizardCompleted": True,
                                            "ProductName": "Jellyfin Server", "CompletedInstallations": []})
                if low == "/system/endpoint":
                    self.route = "endpoint"
                    return self._json(200, {"IsLocal": True, "IsInNetwork": True})
                if low == "/system/info/storage":
                    self.route = "storage"
                    return self._json(200, {"ProgramDataFolder": {"Path": "", "FreeSpace": 0, "UsedSpace": 0},
                                            "Libraries": []})
                if low == "/system/configuration":
                    self.route = "configuration"
                    if method == "POST":
                        return self._send(204)
                    return self._json(200, {"EnableMetrics": False, "ServerName": "Harness Jellyfin (fake)",
                                            "UICulture": "en-US", "MinResumePct": 5, "MaxResumePct": 90,
                                            "MinResumeDurationSeconds": 300, "LibraryMonitorDelay": 60,
                                            "EnableFolderView": False, "IsStartupWizardCompleted": True,
                                            "TrickplayOptions": {"Interval": 10000, "WidthResolutions": [320],
                                                                 "TileWidth": 10, "TileHeight": 10}})
                if match(r"/system/configuration/[a-z]+"):
                    self.route = "configuration"
                    return self._send(204) if method == "POST" else self._json(200, {})
                if low == "/system/activitylog/entries":
                    self.route = "activity"
                    return self._json(200, {"Items": [], "TotalRecordCount": 0, "StartIndex": 0})
                if low == "/playback/bitratetest":
                    self.route = "bitrate-test"
                    return self._send(200, b"\0" * min(int(self._one("size", "500000")), 10_000_000),
                                      "application/octet-stream")
                if low == "/getutctime":
                    self.route = "utc"
                    now = datetime.datetime.now(datetime.timezone.utc).isoformat()
                    return self._json(200, {"RequestReceptionTime": now, "ResponseTransmissionTime": now})
                if low == "/users/me":
                    self.route = "users-me"
                    return self._json(200, outer.user_dto(self.uid))
                if low == "/users":
                    self.route = "users"
                    return self._json(200, [outer.user_dto(u) for u in outer.users])
                mm = match(r"/users/" + ID)
                if mm and method == "GET":
                    self.route = "user"
                    uid = library.norm_id(mm.group(1))
                    return self._json(200, outer.user_dto(uid)) if uid in outer.users else self._json(404, None)
                if low in ("/userviews", "/library/mediafolders") or match(r"/users/" + ID + r"/views"):
                    self.route = "views"
                    return self._json(200, lib.query({}))
                if low == "/library/virtualfolders" and method == "GET":
                    self.route = "virtual-folders"
                    return self._json(200, [{"Name": lib.items[v]["Name"], "Locations": ["/media/" + lib.items[v]["Name"].lower()],
                                             "CollectionType": lib.items[v]["collection"], "ItemId": v,
                                             "LibraryOptions": {}, "RefreshStatus": "Idle"} for v in lib.views])
                if low == "/useritems/resume" or match(r"/users/" + ID + r"/items/resume"):
                    self.route = "resume"
                    return self._json(200, lib.resume(int(self._one("limit", "12")), parent=self._one("parentid")))
                if low == "/items/latest" or match(r"/users/" + ID + r"/items/latest"):
                    self.route = "latest"
                    return self._json(200, lib.latest(self._one("parentid"), int(self._one("limit", "16"))))
                if low in ("/items/filters", "/items/filters2"):
                    self.route = "filters"
                    f = lib.filters(self._one("parentid"))
                    if low.endswith("2"):
                        f = {"Genres": [{"Name": g, "Id": library._id("genre", g)} for g in f["Genres"]],
                             "Tags": f["Tags"]}
                    return self._json(200, f)
                if low == "/items" or match(r"/users/" + ID + r"/items"):
                    self.route = "items"
                    return self._json(200, lib.query(self.q))
                if low == "/shows/nextup":
                    self.route = "nextup"
                    return self._json(200, lib.next_up(int(self._one("limit", "24")), self._one("seriesid")))
                mm = match(r"/shows/" + ID + r"/seasons")
                if mm:
                    self.route = "seasons"
                    return self._json(200, lib.seasons(mm.group(1)))
                mm = match(r"/shows/" + ID + r"/episodes")
                if mm:
                    self.route = "episodes"
                    return self._json(200, lib.episodes(mm.group(1), self._one("seasonid"),
                                                        int(self._one("startindex", "0")),
                                                        lib.int_of(self._one("limit"), None)))
                mm = match(r"/(?:items|movies|shows)/" + ID + r"/similar")
                if mm:
                    self.route = "similar"
                    return self._json(200, lib.similar(mm.group(1), int(self._one("limit", "12"))))
                mm = match(r"/mediasegments/" + ID)
                if mm:
                    self.route = "segments"
                    item = lib.get(mm.group(1))
                    segs = lib.segments(item) if item else []
                    if os.environ.get("FAKESERVER_NO_SEGMENTS"):
                        segs = []      # a server without Intro Skipper: Skip intro then comes from the file's chapters
                    types = {t.lower() for v in self.q.get("includesegmenttypes", []) for t in v.split(",") if t}
                    if types:
                        segs = [s for s in segs if s["Type"].lower() in types]
                    return self._json(200, {"Items": segs, "TotalRecordCount": len(segs), "StartIndex": 0})
                mm = match(r"/items/" + ID + r"/playbackinfo")
                if mm:
                    self.route = "playbackinfo"
                    return self._playback_info(mm.group(1))
                mm = match(r"/items/" + ID + r"/images")
                if mm:
                    self.route = "image-list"
                    item = lib.get(mm.group(1))
                    if not item:
                        return self._json(404, None)
                    tags = lib.image_tags(item)
                    infos = [{"ImageType": k, "ImageTag": v, "Height": BASE_SIZES.get((item["kind"], k), BASE_SIZES[(None, k)])[1],
                              "Width": BASE_SIZES.get((item["kind"], k), BASE_SIZES[(None, k)])[0]} for k, v in tags.items()]
                    return self._json(200, infos)
                mm = match(r"/items/" + ID + r"/ancestors")
                if mm:
                    self.route = "ancestors"
                    item = lib.get(mm.group(1))
                    chain, cur = [], item
                    while cur and cur.get("parent"):
                        cur = lib.items.get(cur["parent"])
                        if cur:
                            chain.append(lib.dto(cur))
                    return self._json(200, chain)
                mm = match(U + r"/items/" + ID + r"/(intros)") or match(U + r"/items/" + ID + r"/(specialfeatures|localtrailers|externalidinfos)")
                if mm:
                    self.route = "extras"
                    return self._json(200, {"Items": [], "TotalRecordCount": 0, "StartIndex": 0}
                                      if mm.group(3) == "intros" else [])
                mm = match(r"/items/" + ID + r"/thememedia")
                if mm:
                    self.route = "extras"
                    empty = {"Items": [], "TotalRecordCount": 0, "StartIndex": 0, "OwnerId": mm.group(1)}
                    return self._json(200, {"ThemeVideosResult": empty, "ThemeSongsResult": empty,
                                            "SoundtrackSongsResult": empty})
                mm = match(r"/items/" + ID) or match(r"/users/" + ID + r"/items/" + ID)
                if mm:
                    item_id = mm.group(mm.lastindex)
                    item = lib.get(item_id)
                    if method == "POST":
                        self.route = "item-update"
                        return self._send(204)
                    self.route = "item"
                    return self._json(200, outer.detail(item)) if item else self._json(404, None)
                mm = match(r"/items/" + ID + r"/refresh")
                if mm:
                    self.route = "item-update"
                    return self._send(204)
                mm = match(r"(?:/userfavoriteitems|/users/" + ID + r"/favoriteitems)/" + ID)
                if mm:
                    self.route = "favorite"
                    item = lib.set_favorite(mm.group(mm.lastindex), method == "POST")
                    if not item:
                        return self._json(404, None)
                    outer.push_user_data(self.uid, [item])
                    return self._json(200, lib.user_data(item))
                mm = match(r"(?:/userplayeditems|/users/" + ID + r"/playeditems)/" + ID)
                if mm:
                    self.route = "played"
                    item = lib.set_played(mm.group(mm.lastindex), method == "POST")
                    if not item:
                        return self._json(404, None)
                    outer.push_user_data(self.uid, [item])
                    return self._json(200, lib.user_data(item))
                mm = match(r"(?:/useritems|/users/" + ID + r"/items)/" + ID + r"/userdata")
                if mm:
                    self.route = "user-data"
                    item = lib.get(mm.group(mm.lastindex))
                    if not item:
                        return self._json(404, None)
                    if method == "POST":
                        body = self._body()
                        if "Played" in body and body["Played"] is not None:
                            item = lib.set_played(item["Id"], bool(body["Played"])) or item
                        if "IsFavorite" in body and body["IsFavorite"] is not None:
                            item = lib.set_favorite(item["Id"], bool(body["IsFavorite"])) or item
                        if "PlaybackPositionTicks" in body and body["PlaybackPositionTicks"] is not None:
                            item = lib.set_position(item["Id"], body["PlaybackPositionTicks"]) or item
                        outer.push_user_data(self.uid, [item])
                    return self._json(200, lib.user_data(item))
                if low.startswith("/user_usage_stats/"):
                    # the Playback Reporting plugin's API: a server without the plugin answers 404, as this one does
                    self.route = "plugin-absent"
                    return self._json(404, None)
                if low.startswith("/sessions/playing"):
                    return self._report(low)
                if low in ("/sessions/capabilities/full", "/sessions/capabilities"):
                    self.route = "capabilities"
                    return self._send(204)
                if low == "/sessions/logout":
                    self.route = "logout"
                    token = self._token_value()
                    with outer.lock:
                        outer.tokens.pop(token, None)
                    return self._send(204)
                if low == "/sessions":
                    self.route = "sessions"
                    return self._json(200, [])
                if low == "/videos/activeencodings":
                    self.route = "active-encodings"
                    return self._send(204)
                if match(r"/displaypreferences/[^/]+"):
                    self.route = "display-preferences"
                    if method == "POST":
                        return self._send(204)
                    return self._json(200, {"Id": p.rsplit("/", 1)[1], "SortBy": "SortName", "RememberIndexing": False,
                                            "PrimaryImageHeight": 250, "PrimaryImageWidth": 250, "CustomPrefs": {},
                                            "ScrollDirection": "Horizontal", "ShowBackdrop": True, "RememberSorting": False,
                                            "SortOrder": "Ascending", "ShowSidebar": False, "Client": self._one("client", "emby")})
                if low in ("/plugins", "/packages", "/scheduledtasks", "/syncplay/list", "/localization/cultures",
                           "/localization/countries", "/localization/parentalratings", "/localization/options",
                           "/livetv/channels", "/devices", "/auth/keys"):
                    self.route = "lists"
                    if low in ("/devices", "/auth/keys"):
                        return self._json(200, {"Items": [], "TotalRecordCount": 0, "StartIndex": 0})
                    return self._json(200, [])
                if low in ("/genres", "/studios", "/persons", "/musicgenres", "/artists"):
                    self.route = "names"
                    return self._json(200, {"Items": [], "TotalRecordCount": 0, "StartIndex": 0})
                if low == "/environment/directorycontents":
                    # the server's folders, for the dashboard's folder picker: /media/<library>/A..C; anything else none
                    self.route = "directory"
                    return self._json(200, directory_contents(self._one("path", "/"), [lib.items[v]["Name"] for v in lib.views]))
                self.route = "unhandled"
                self.unhandled = True
                return self._json(404, None)

            def _token_value(self):
                auth = self.headers.get("Authorization") or self.headers.get("X-Emby-Authorization") or ""
                m = TOKEN_RE.search(auth)
                return (m.group(1) if m else None) or self.headers.get("X-Emby-Token") or \
                    (self.q.get("api_key") or self.q.get("apikey") or [None])[0]

            # ---------------- handlers
            def _harness(self, method, low):
                if low == "/__harness/fixture":
                    return self._json(200, dict(outer.lib.showcase(), server=outer.url, username=outer.creds["username"],
                                                counts=outer.lib.counts()))
                if low == "/__harness/summary":
                    outer.write_summary()
                    return self._json(200, outer.summary())
                if low == "/__harness/latency" and method == "POST":
                    body = self._body()
                    if "default" in body:
                        outer.latency = float(body["default"])
                    if "rules" in body:
                        outer.rules = [(re.compile(r, re.I), rule_value(ms)) for r, ms in body["rules"]]
                    if "jitter" in body:
                        outer.jitter = float(body["jitter"])
                    return self._json(200, outer.summary()["latency"])
                if low == "/__harness/streams" and method == "POST":
                    # {"encode_subtitle": true}: the last embedded subtitle is burnt in by the server ("Encode"), so a
                    # client that picks it renegotiates the stream (with a PlaybackInfo rule: a track change that fails)
                    body = self._body()
                    if "encode_subtitle" in body:
                        outer.encode_subtitle = bool(body["encode_subtitle"])
                    return self._json(200, {"encode_subtitle": outer.encode_subtitle})
                if low == "/__harness/segments" and method == "POST":
                    # {"outro_seconds": 120}: the end credits run that long, to the end (40 s by default): credits that
                    # start before 90 % of the item, where Jellyfin's own played mark does not reach
                    body = self._body()
                    if "outro_seconds" in body:
                        outer.lib.outro_seconds = float(body["outro_seconds"])
                    return self._json(200, {"outro_seconds": outer.lib.outro_seconds})
                if low == "/__harness/release/latest" and method in ("GET", "HEAD"):
                    # Nixie 1.3.1: the stand-in for GitHub's latest-release API (NIXIE_UPDATE_API points the app here): the
                    # version FAKESERVER_RELEASE names (or POST /__harness/release {"version"}), 0.0.1 by default (up to date)
                    return self._json(200, release_answer(outer.release_version))
                if low == "/__harness/release" and method == "POST":
                    version = str(self._body().get("version") or "")
                    if re.fullmatch(r"\d+\.\d+\.\d+", version):
                        outer.release_version = version
                    return self._json(200, {"version": outer.release_version})
                if low.startswith("/__harness/trailer/") and method in ("GET", "HEAD"):
                    # Nixie 1.3.1: the stand-in for YouTube's embed page (NIXIE_TRAILER_EMBED_BASE points the app here):
                    # a still "trailer" card, no script, nothing fetched from anywhere
                    vid = html.escape(low.rsplit("/", 1)[-1][:32])
                    return self._send(200, TRAILER_PAGE.replace("{id}", vid).encode("utf-8"), "text/html; charset=utf-8")
                if low == "/__harness/shutdown" and method == "POST":
                    self._json(200, {"ok": True})
                    outer.stop()
                    return
                if low == "/__harness/quickconnect":
                    with outer.lock:
                        waiting = [{"code": e["code"], "client": e["client"], "approved": bool(e["user"])}
                                   for e in outer.quick.values()]
                    return self._json(200, waiting)
                if low == "/__harness/quickconnect/approve" and method == "POST":
                    # as the user would on another device: approve the code the client shows, for the test user
                    # (or {"user": "<the second test user's name>"})
                    body = self._body()
                    code, name = str(body.get("code") or ""), (body.get("user") or outer.creds["username"]).lower()
                    uid = next((u for u, v in outer.users.items() if v["name"].lower() == name), None)
                    with outer.lock:
                        entry = next((e for e in outer.quick.values() if e["code"] == code), None)
                        if entry and uid:
                            entry["user"] = uid
                    if not entry or not uid:
                        return self._json(404, {"error": "no such code waiting" if not entry else "no such user"})
                    return self._json(200, {"code": code, "user": outer.users[uid]["name"]})
                if low == "/__harness/libraries" and method == "POST":
                    # the libraries change on the server: {"rename": {"Shows": "Television Shows"}, "add": ["Concerts"]};
                    # every connected client hears LibraryChanged, as from Jellyfin, so it reads its views again
                    body = self._body()
                    with outer.lock:
                        renamed = outer.lib.rename_views(body.get("rename") or {})
                        added = outer.lib.add_views([str(n) for n in body.get("add") or []])
                        sockets = list(outer.sockets)
                    message = {"MessageType": "LibraryChanged", "MessageId": uuid.uuid4().hex,
                               "Data": {"FoldersAddedTo": [], "FoldersRemovedFrom": [], "ItemsAdded": added,
                                        "ItemsRemoved": [], "ItemsUpdated": renamed, "CollectionFolders": added + renamed}}
                    for ws in sockets:
                        try:
                            ws.send_json(message)
                        except (ConnectionError, OSError):
                            pass
                    return self._json(200, {"views": [outer.lib.items[v]["Name"] for v in outer.lib.views],
                                            "told": len(sockets)})
                return self._json(404, None)

            def _quick_dto(self, entry):
                return {"Authenticated": bool(entry["user"]), "Secret": entry["secret"], "Code": entry["code"],
                        "DeviceId": entry["device_id"], "DeviceName": entry["device"], "AppName": entry["client"],
                        "AppVersion": entry["version"], "DateAdded": entry["added"]}

            def _quick_initiate(self):
                auth = self.headers.get("Authorization") or self.headers.get("X-Emby-Authorization") or ""

                def field(name):
                    m = re.search(name + r'="([^"]*)"', auth)
                    return m.group(1) if m else ""
                with outer.lock:
                    codes = {e["code"] for e in outer.quick.values()}
                    code = "%06d" % secrets.randbelow(1_000_000)
                    while code in codes:
                        code = "%06d" % secrets.randbelow(1_000_000)
                    entry = {"secret": secrets.token_hex(32), "code": code, "user": None, "client": self.client,
                             "device": field("Device"), "device_id": field("DeviceId"), "version": field("Version"),
                             "added": library.iso(datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None))}
                    outer.quick[entry["secret"]] = entry
                return self._json(200, self._quick_dto(entry))

            def _authenticate(self):
                body = self._body()
                name, pw = body.get("Username") or "", body.get("Pw") or ""
                uid = next((u for u, v in outer.users.items() if v["name"].lower() == name.lower()), None)
                if uid is None or not secrets.compare_digest(outer.users[uid]["password"], pw):
                    return self._send(401, b"Error processing request.", "text/plain")
                return self._signed_in(uid)

            def _signed_in(self, uid):
                token = secrets.token_hex(16)
                with outer.lock:
                    outer.tokens[token] = uid
                session = {"Id": uuid.uuid4().hex, "UserId": uid, "UserName": outer.users[uid]["name"],
                           "Client": self.client, "DeviceName": "", "DeviceId": "", "ApplicationVersion": "",
                           "IsActive": True, "SupportsMediaControl": False, "SupportsRemoteControl": False,
                           "PlayableMediaTypes": [], "ServerId": outer.server_id,
                           "LastActivityDate": library.iso(datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None))}
                return self._json(200, {"User": outer.user_dto(uid), "SessionInfo": session, "AccessToken": token,
                                        "ServerId": outer.server_id})

            def _playback_info(self, item_id):
                item = outer.lib.get(item_id)
                if not item or item["kind"] not in ("movie", "episode"):
                    return self._json(404, None)
                body = self._body() if self.method == "POST" else {}

                def pick(key):
                    v = body.get(key)
                    if v is None:
                        v = self._one(key)
                    try:
                        return None if v is None else int(v)
                    except (TypeError, ValueError):
                        return None
                session = uuid.uuid4().hex
                with outer.lock:
                    outer.sessions.append(session)
                self.extra = {"item": item["Id"], "play_session": session}
                return self._json(200, {"MediaSources": [outer.media_source(item, pick("AudioStreamIndex"),
                                                                            pick("SubtitleStreamIndex"))],
                                        "PlaySessionId": session})

            def _report(self, low):
                kind = {"/sessions/playing": "Playing", "/sessions/playing/progress": "Progress",
                        "/sessions/playing/stopped": "Stopped", "/sessions/playing/ping": "Ping"}.get(low)
                self.route = "report-" + (kind or "?").lower()
                body = self._body()
                item_id = library.norm_id(str(body.get("ItemId") or self._one("itemid") or ""))
                position = body.get("PositionTicks")
                entry = {"kind": kind, "item": item_id, "position": position, "play_session": body.get("PlaySessionId"),
                         "paused": body.get("IsPaused"), "failed": body.get("Failed"),
                         "audio": body.get("AudioStreamIndex"), "subtitle": body.get("SubtitleStreamIndex"),
                         "client": self.client}
                self.extra = entry
                with outer.lock:
                    outer.reports.append(dict(entry, t=round(time.time(), 3)))
                if kind in ("Playing", "Progress", "Stopped") and item_id:
                    item = outer.lib.report(item_id, position, stopped=kind == "Stopped")
                    if item and kind != "Progress":
                        outer.push_user_data(self.uid, [item])
                return self._send(204)

            def _file(self, path, ctype):
                size = os.path.getsize(path)
                start, end, code = 0, size - 1, 200
                rng = self.headers.get("Range")
                if rng:
                    m = re.match(r"bytes=(\d*)-(\d*)", rng)
                    if m:
                        if m.group(1):
                            start = int(m.group(1))
                            if m.group(2):
                                end = min(int(m.group(2)), size - 1)
                        elif m.group(2):
                            start = max(0, size - int(m.group(2)))
                        code = 206
                if start >= size:
                    self.status = 416
                    self.send_response(416)
                    self.send_header("Content-Range", "bytes */%d" % size)
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                length = end - start + 1
                self.status = code
                self.send_response(code)
                self.send_header("Content-Type", ctype)
                self.send_header("Accept-Ranges", "bytes")
                self.send_header("Content-Length", str(length))
                if code == 206:
                    self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
                self.end_headers()
                if self.method == "HEAD":
                    return
                with open(path, "rb") as f:
                    f.seek(start)
                    left = length
                    while left > 0:
                        chunk = f.read(min(262144, left))
                        if not chunk:
                            break
                        self.wfile.write(chunk)
                        self.sent += len(chunk)
                        left -= len(chunk)

            def _websocket(self):
                if self.uid is None:
                    return self._json(401, None)
                key = self.headers.get("Sec-WebSocket-Key")
                if not key or "websocket" not in (self.headers.get("Upgrade") or "").lower():
                    return self._json(400, None)
                accept = base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()
                self.status = 101
                self.send_response(101)
                self.send_header("Upgrade", "websocket")
                self.send_header("Connection", "Upgrade")
                self.send_header("Sec-WebSocket-Accept", accept)
                self.end_headers()
                self.wfile.flush()
                ws = WebSocket(self.connection, self.rfile, self.uid)
                outer.sockets.append(ws)
                try:
                    ws.send_json({"MessageType": "ForceKeepAlive", "MessageId": uuid.uuid4().hex, "Data": 60})
                    while True:
                        opcode, payload = ws.read()
                        if opcode is None or opcode == 8:
                            ws.send_frame(8, b"")
                            break
                        if opcode == 9:
                            ws.send_frame(10, payload)
                        elif opcode == 1:
                            try:
                                msg = json.loads(payload)
                            except ValueError:
                                continue
                            if msg.get("MessageType") == "KeepAlive":
                                ws.send_json({"MessageType": "KeepAlive", "MessageId": uuid.uuid4().hex})
                except (ConnectionError, OSError):
                    pass
                finally:
                    if ws in outer.sockets:
                        outer.sockets.remove(ws)
                    self.close_connection = True

        return Handler


class WebSocket:
    """The server side of one RFC 6455 connection (text frames, ping/pong, close)."""

    def __init__(self, conn, rfile, user_id):
        self.conn, self.rfile, self.user_id = conn, rfile, user_id
        self.lock = threading.Lock()

    def _exact(self, n):
        data = b""
        while len(data) < n:
            chunk = self.rfile.read(n - len(data))
            if not chunk:
                raise ConnectionError("closed")
            data += chunk
        return data

    def read(self):
        try:
            b1, b2 = self._exact(2)
        except ConnectionError:
            return None, b""
        opcode, length = b1 & 0x0F, b2 & 0x7F
        if length == 126:
            length = struct.unpack(">H", self._exact(2))[0]
        elif length == 127:
            length = struct.unpack(">Q", self._exact(8))[0]
        mask = self._exact(4) if b2 & 0x80 else None
        payload = self._exact(length)
        if mask:
            payload = bytes(c ^ mask[i % 4] for i, c in enumerate(payload))
        return opcode, payload

    def send_frame(self, opcode, payload):
        n = len(payload)
        header = bytes([0x80 | opcode]) + (bytes([n]) if n < 126 else
                                           (bytes([126]) + struct.pack(">H", n) if n < 65536 else
                                            bytes([127]) + struct.pack(">Q", n)))
        try:
            with self.lock:
                self.conn.sendall(header + payload)
        except OSError:
            pass

    def send_json(self, obj):
        self.send_frame(1, json.dumps(obj).encode("utf-8"))


def directory_contents(path, libraries):
    """The fake server's folder tree (GET /Environment/DirectoryContents): / holds media, /media one folder per library,
    each of those the folders A, B and C (which are empty). Jellyfin's FileSystemEntryInfo, folders only."""
    parts = [p for p in (path or "/").replace("\\", "/").split("/") if p]
    if not parts:
        names = ["media"]
    elif parts == ["media"]:
        names = [n.lower() for n in libraries]
    elif len(parts) == 2 and parts[0] == "media" and parts[1] in [n.lower() for n in libraries]:
        names = ["A", "B", "C"]
    else:
        names = []
    base = "/" + "/".join(parts)
    return [{"Name": n, "Path": (base.rstrip("/") + "/" + n), "Type": "Directory"} for n in names]


def load_credentials(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True, help="request log, summary and ready file go here")
    ap.add_argument("--media", required=True, help="folder with media.py's build (media.json, test.mkv, ...)")
    ap.add_argument("--mpv", help="build the media into --media first when it is missing (mpv.exe)")
    ap.add_argument("--media-seconds", type=int, default=600)
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--latency", type=float, default=0.0, help="ms added to every request")
    ap.add_argument("--latency-rule", action="append", default=[], metavar="REGEX=MS",
                    help="ms (or 'hang', 'status:NNN', 'empty') for paths matching REGEX; first match wins")
    ap.add_argument("--jitter", type=float, default=0.0, help="+/- ms of random jitter")
    ap.add_argument("--credentials", default=os.path.join(HERE, "credentials.json"))
    ap.add_argument("--ready-file", help="written with {url, port, pid} once listening")
    ap.add_argument("--seed", type=int, default=20261115)
    ap.add_argument("--not-admin", action="store_true", help="the test user is not an administrator")
    a = ap.parse_args(argv)
    meta_path = os.path.join(a.media, "media.json")
    if not os.path.exists(meta_path):
        if not a.mpv:
            ap.error("%s is missing; pass --mpv to build it" % meta_path)
        media_mod.build(a.mpv, os.path.abspath(a.media), a.media_seconds)
    with open(meta_path, encoding="utf-8") as f:
        meta = json.load(f)
    rules = []
    for r in a.latency_rule:
        rx, _, ms = r.rpartition("=")
        try:
            rules.append((rx, rule_value(ms)))
        except ValueError as ex:
            ap.error("--latency-rule %s: %s" % (r, ex))
    srv = Server(os.path.abspath(a.out), meta, load_credentials(a.credentials), a.latency, rules, a.jitter, a.port,
                 a.seed, admin=not a.not_admin)
    if a.ready_file:
        tmp = a.ready_file + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"url": srv.url, "port": srv.port, "pid": os.getpid()}, f)
        os.replace(tmp, a.ready_file)
    print("READY %s" % srv.url, flush=True)
    try:
        srv.serve()
    except KeyboardInterrupt:
        pass
    finally:
        srv.write_summary()
    return 0


if __name__ == "__main__":
    sys.exit(main())
