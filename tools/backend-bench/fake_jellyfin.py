"""A tiny stand-in Jellyfin server for benchmarking the shim's play path.

Serves exactly what one direct-stream play needs and LOGS EVERY REQUEST
(time, method, path, status, ms) so the play path's round trips can be
counted and ordered. Anything it does not know answers 404 and is logged, so
an unexpected request shows up rather than hiding.

Not a Jellyfin: shapes are the minimum the shim reads (media.py, player.py,
player_reporting.py, trickplay.py), modelled on Jellyfin 10.10/10.11 DTOs.
`Trickplay` is returned on the item whatever `Fields` says, which is what
10.9+ does for the single-item endpoint.

Stdlib + Pillow (for the trickplay mosaic only).
"""

import io
import json
import os
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ITEM_ID = "a1b2c3d4e5f60718293a4b5c6d7e8f90"
SOURCE_ID = ITEM_ID
USER_ID = "0123456789abcdef0123456789abcdef"
SERVER_ID = "fedcba9876543210fedcba9876543210"
TOKEN = "benchtoken0000000000000000000000"

TICKS = 10_000_000


class Log:
    """Request log. With `path`, also appended as JSON lines (flushed), so a
    server running in its own process can be read by the benchmark: on
    Windows time.perf_counter is QueryPerformanceCounter, which is one
    system-wide clock, so its values compare across processes."""

    def __init__(self, path=None):
        self.lock = threading.Lock()
        self.rows = []
        self.t0 = time.perf_counter()
        self.fh = open(path, "a", encoding="utf-8") if path else None

    def add(self, **row):
        with self.lock:
            row["t"] = time.perf_counter()
            self.rows.append(row)
            if self.fh:
                self.fh.write(json.dumps(row) + "\n")
                self.fh.flush()

    def snapshot(self):
        with self.lock:
            return list(self.rows)


def read_log(path):
    rows = []
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line:
                    rows.append(json.loads(line))
    except FileNotFoundError:
        pass
    return rows


def _mosaic_jpeg(w=320, h=180, tw=10, th=10):
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        return b""
    img = Image.new("RGB", (w * tw, h * th), (20, 20, 40))
    d = ImageDraw.Draw(img)
    for i in range(tw * th):
        x, y = (i % tw) * w, (i // tw) * h
        d.rectangle([x + 4, y + 4, x + w - 4, y + h - 4],
                    outline=(200, 200, 60), width=3)
        d.text((x + 20, y + 20), str(i), fill=(255, 255, 255))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=80)
    return buf.getvalue()


class FakeJellyfin:
    def __init__(self, media_path, duration_s, segments=True, trickplay=True,
                 latency_ms=0.0, log_path=None, live=False):
        # live: the stream has no length (no Content-Length, no ranges, the
        # connection closes at the end) and the source says IsInfiniteStream,
        # so mpv never reports a duration -- the Live TV / .strm start path.
        self.live = live
        self.media_path = media_path
        self.media_size = os.path.getsize(media_path)
        self.duration_s = duration_s
        self.segments = segments
        self.trickplay = trickplay
        # Applied to API calls, not to the media stream (a WAN RTT stand-in
        # for the round trips the play path makes in sequence).
        self.latency = latency_ms / 1000.0
        self.log = Log(log_path)
        self.tile = _mosaic_jpeg() if trickplay else b""
        self.httpd = None
        self.thread = None

    # -- DTOs -----------------------------------------------------------
    def media_source(self):
        src = self._media_source()
        if self.live:
            src.update(IsInfiniteStream=True, Container="ts")
            src.pop("RunTimeTicks", None)
            src.pop("Size", None)
        return src

    def _media_source(self):
        return {
            "Protocol": "File", "Id": SOURCE_ID, "Path": "/media/bench/bench.mkv",
            "Type": "Default", "Container": "mkv", "Size": self.media_size,
            "Name": "bench", "IsRemote": False,
            "RunTimeTicks": int(self.duration_s * TICKS),
            "SupportsTranscoding": True, "SupportsDirectStream": True,
            "SupportsDirectPlay": True, "IsInfiniteStream": False,
            "RequiresOpening": False, "RequiresClosing": False,
            "Bitrate": 2_000_000, "DefaultAudioStreamIndex": 1,
            "MediaStreams": [
                {"Codec": "h264", "Type": "Video", "Index": 0, "Width": 1280,
                 "Height": 720, "IsDefault": True, "IsExternal": False,
                 "IsInterlaced": False, "BitRate": 1_800_000},
                {"Codec": "aac", "Type": "Audio", "Index": 1, "Language": "eng",
                 "Channels": 2, "IsDefault": True, "IsExternal": False,
                 "DisplayTitle": "English - AAC - Stereo"},
            ],
            "MediaAttachments": [],
        }

    def item(self):
        it = {
            "Name": "Bench Episode", "ServerId": SERVER_ID, "Id": ITEM_ID,
            "Type": "Episode", "MediaType": "Video", "IsFolder": False,
            "SeriesName": "Bench Series", "SeriesId": "5" * 32,
            "SeasonId": "6" * 32, "ParentIndexNumber": 1, "IndexNumber": 2,
            "RunTimeTicks": int(self.duration_s * TICKS),
            "Path": "/media/bench/bench.mkv", "Container": "mkv",
            "UserData": {"PlaybackPositionTicks": 0, "Played": False,
                         "IsFavorite": False},
            "MediaSources": [self.media_source()],
            "MediaStreams": self.media_source()["MediaStreams"],
            "Chapters": [],
        }
        if self.live:
            it.pop("RunTimeTicks", None)
        if self.trickplay:
            it["Trickplay"] = {SOURCE_ID: {"320": {
                "Width": 320, "Height": 180, "TileWidth": 10, "TileHeight": 10,
                "ThumbnailCount": int(self.duration_s), "Interval": 1000,
                "Bandwidth": 100000}}}
        return it

    # -- server ---------------------------------------------------------
    def start(self):
        fake = self

        class H(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def _send(self, code, body=b"", ctype="application/json",
                      extra=None):
                self.send_response(code)
                self.send_header("Content-Type", ctype)
                self.send_header("Content-Length", str(len(body)))
                for k, v in (extra or {}).items():
                    self.send_header(k, v)
                self.end_headers()
                if self.command != "HEAD" and body:
                    try:
                        self.wfile.write(body)
                    except (ConnectionError, OSError):
                        pass

            def _json(self, obj, code=200):
                self._send(code, json.dumps(obj).encode())

            def _route(self):
                t0 = time.perf_counter()
                path = self.path.split("?", 1)[0]
                if fake.latency and "/stream" not in path.lower():
                    time.sleep(fake.latency)
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length) if length else b""
                code = 200
                lp = path.lower()
                try:
                    if re.fullmatch(r"/users/[0-9a-f]+/items/[0-9a-f]+", lp) or \
                            re.fullmatch(r"/items/[0-9a-f]+", lp):
                        self._json(fake.item())
                    elif re.fullmatch(r"/items/[0-9a-f]+/playbackinfo", lp):
                        self._json({"MediaSources": [fake.media_source()],
                                    "PlaySessionId": "benchsession"})
                    elif lp.startswith("/mediasegments/"):
                        items = []
                        if fake.segments:
                            items = [{"Id": "1" * 32, "ItemId": ITEM_ID,
                                      "Type": "Intro",
                                      "StartTicks": 5 * TICKS,
                                      "EndTicks": 15 * TICKS}]
                        self._json({"Items": items, "TotalRecordCount":
                                    len(items), "StartIndex": 0})
                    elif lp in ("/sessions/playing", "/sessions/playing/progress",
                                "/sessions/playing/stopped",
                                "/sessions/capabilities/full"):
                        code = 204
                        self._send(204)
                    elif lp == "/system/endpoint":
                        self._json({"IsLocal": True, "IsInNetwork": True})
                    elif re.fullmatch(r"/videos/[0-9a-f]+/stream(\.\w+)?", lp):
                        code = self._stream()
                    elif re.fullmatch(r"/videos/[0-9a-f]+/trickplay/\d+/\d+\.jpg",
                                      lp):
                        self._send(200, fake.tile, "image/jpeg")
                    elif lp.startswith("/videos/activeencodings"):
                        code = 204
                        self._send(204)
                    else:
                        code = 404
                        self._send(404, b"")
                finally:
                    fake.log.add(method=self.command, path=self.path,
                                 status=code, ms=(time.perf_counter() - t0) * 1000,
                                 body=len(body), t_start=t0)

            def _stream(self):
                if fake.live:
                    self.send_response(200)
                    self.send_header("Content-Type", "video/mp2t")
                    self.send_header("Connection", "close")
                    self.end_headers()
                    self.close_connection = True
                    try:
                        with open(fake.media_path, "rb") as f:
                            while True:
                                chunk = f.read(64 * 1024)
                                if not chunk:
                                    break
                                self.wfile.write(chunk)
                                time.sleep(0.01)     # paced, like a tuner
                    except (ConnectionError, OSError):
                        pass
                    return 200
                size = fake.media_size
                rng = self.headers.get("Range")
                start, end = 0, size - 1
                code = 200
                if rng:
                    m = re.match(r"bytes=(\d*)-(\d*)", rng)
                    if m:
                        if m.group(1):
                            start = int(m.group(1))
                        if m.group(2):
                            end = min(int(m.group(2)), size - 1)
                        code = 206
                if start >= size:
                    self._send(416, b"", extra={"Content-Range":
                                               "bytes */%d" % size})
                    return 416
                n = end - start + 1
                self.send_response(code)
                self.send_header("Content-Type", "video/x-matroska")
                self.send_header("Accept-Ranges", "bytes")
                self.send_header("Content-Length", str(n))
                if code == 206:
                    self.send_header("Content-Range",
                                     "bytes %d-%d/%d" % (start, end, size))
                self.end_headers()
                if self.command == "HEAD":
                    return code
                try:
                    with open(fake.media_path, "rb") as f:
                        f.seek(start)
                        left = n
                        while left > 0:
                            chunk = f.read(min(256 * 1024, left))
                            if not chunk:
                                break
                            self.wfile.write(chunk)
                            left -= len(chunk)
                except (ConnectionError, OSError):
                    pass
                return code

            do_GET = do_POST = do_HEAD = do_DELETE = _route

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), H)
        self.httpd.daemon_threads = True
        self.thread = threading.Thread(target=self.httpd.serve_forever,
                                       name="fake-jellyfin", daemon=True)
        self.thread.start()
        return "http://127.0.0.1:%d" % self.httpd.server_address[1]

    def stop(self):
        if self.httpd:
            self.httpd.shutdown()


def serve_main():
    """`python -I fake_jellyfin.py --media F --duration S --log L --port-file P`
    -- run in its own process so its threads never share a GIL with the shim
    being measured."""
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--media", required=True)
    ap.add_argument("--duration", type=float, required=True)
    ap.add_argument("--log", required=True)
    ap.add_argument("--port-file", required=True)
    ap.add_argument("--latency-ms", type=float, default=0.0)
    ap.add_argument("--no-trickplay", action="store_true")
    ap.add_argument("--live", action="store_true")
    a = ap.parse_args()
    fake = FakeJellyfin(a.media, a.duration, trickplay=not a.no_trickplay,
                        latency_ms=a.latency_ms, log_path=a.log, live=a.live)
    url = fake.start()
    tmp = a.port_file + ".tmp"
    with open(tmp, "w") as f:
        f.write(url)
    os.replace(tmp, a.port_file)
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    serve_main()
