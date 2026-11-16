"""A fake Jellyfin server on loopback, for probing the bridge. Fake tokens only."""
import json
import os
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOKEN_RE = re.compile(r'Token="([^"]*)"')


class FakeJF:
    def __init__(self, name, token, media_path, user_id):
        self.name = name
        self.token = token
        self.media_path = media_path
        self.user_id = user_id
        self.records = []
        self.lock = threading.Lock()
        self.playbackinfo_delay = 0.0
        self.item_delay = 0.0
        self.progress_delay = 0.0
        self.stopped_delay = 0.0
        self.playing_delay = 0.0
        self.delete_delay = 0.0
        self.stream_delay = {}
        self.me_mode = 'ok'
        self.me_delay = 0.0
        self.pbi_fail = set()
        #: Jellyfin answers every PlaybackInfo with a new PlaySessionId. True
        #: models a server that hands the same one to every play of an item
        #: (the `replay:fixed` probe); the default is Jellyfin's.
        self.fixed_sessions = False
        self._session_seq = 0
        #: Every PlaySessionId handed out, in order (the round-4 probes
        #: count `reported` per session against these).
        self.issued = []
        #: The status a Stopped report is answered with (500: the apiclient
        #: swallows it and returns None, as it does a 204).
        self.stopped_status = 204
        outer = self

        class H(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def _rec(self, body=None):
                auth = self.headers.get("Authorization") or self.headers.get("X-Emby-Authorization") or ""
                m = TOKEN_RE.search(auth)
                tok = m.group(1) if m else None
                q = self.path
                with outer.lock:
                    outer.records.append({"t": time.time(), "method": self.command, "path": q,
                                          "token": tok, "body": body,
                                          "ua": self.headers.get("User-Agent")})

            def _json(self, code, obj):
                data = b"" if obj is None else json.dumps(obj).encode()
                self.send_response(code)
                if obj is not None:
                    self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Date", self.date_time_string())
                self.end_headers()
                if data:
                    self.wfile.write(data)

            def _body(self):
                n = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(n) if n else b""
                try:
                    return json.loads(raw) if raw else None
                except Exception:
                    return raw.decode("latin1")

            def do_GET(self):
                self._rec()
                path = self.path.split("?")[0]
                if path.startswith("/Videos/") and path.endswith("/stream"):
                    return self._serve_file(head=False)
                auth = self.headers.get("Authorization") or ""
                m = TOKEN_RE.search(auth)
                if not m or m.group(1) != outer.token:
                    return self._json(401, None)
                if path == "/Users/Me":
                    time.sleep(outer.me_delay)
                if path == "/Users/Me" and outer.me_mode == '404':
                    return self._json(404, None)
                if path == "/Users/Me" and outer.me_mode == 'other':
                    return self._json(200, {"Id": 'f'*32, "Name": 'x'})
                if path == "/Users/Me":   # added by the fix agent: the token's own user, as Jellyfin answers
                    return self._json(200, {"Id": outer.user_id, "Name": "u", "Policy": {}, "Configuration": {}})
                mm = re.match(r"^/Users/([0-9a-f]{32})$", path)
                if mm:
                    return self._json(200, {"Id": mm.group(1), "Name": "u", "Policy": {}, "Configuration": {}})
                mm = re.match(r"^/Users/([0-9a-f]{32})/Items/([0-9a-f]{32})$", path)
                if mm:
                    time.sleep(outer.item_delay)
                    return self._json(200, outer.item(mm.group(2)))
                if path.startswith("/MediaSegments/"):
                    return self._json(200, {"Items": outer.segments(path[len("/MediaSegments/"):].strip("/"))})
                if path.startswith("/DisplayPreferences/"):
                    return self._json(200, {"CustomPrefs": {}})
                return self._json(200, {})

            def do_HEAD(self):
                self._rec()
                path = self.path.split("?")[0]
                if path.startswith("/Videos/") and path.endswith("/stream"):
                    return self._serve_file(head=True)
                return self._json(200, None)

            def do_POST(self):
                body = self._body()
                self._rec(body)
                path = self.path.split("?")[0]
                auth = self.headers.get("Authorization") or ""
                m = TOKEN_RE.search(auth)
                if not m or m.group(1) != outer.token:
                    return self._json(401, None)
                mm = re.match(r"^/Items/([0-9a-f]{32})/PlaybackInfo$", path)
                if mm:
                    time.sleep(outer.playbackinfo_delay)
                    if mm.group(1) in outer.pbi_fail:
                        return self._json(500, None)
                    return self._json(200, outer.playback_info(mm.group(1)))
                if path.startswith("/Sessions/Playing/Progress"):
                    time.sleep(outer.progress_delay)
                elif path.startswith("/Sessions/Playing/Stopped"):
                    time.sleep(outer.stopped_delay)
                elif path.startswith("/Sessions/Playing"):
                    time.sleep(outer.playing_delay)
                if path.startswith("/Sessions/"):
                    status = 204
                    if path.startswith("/Sessions/Playing/Stopped"):
                        status = outer.stopped_status
                    with outer.lock:
                        outer.records.append({"t": time.time(), "method": "DONE", "path": path, "token": None, "body": body, "ua": None, "status": status})
                    return self._json(status, None)
                return self._json(200, {})

            def do_DELETE(self):
                self._rec()
                time.sleep(outer.delete_delay)
                return self._json(204, None)

            def _serve_file(self, head):
                mid = re.match(r'^/Videos/([0-9a-f]{32})/stream', self.path)
                if mid and outer.stream_delay.get(mid.group(1)):
                    time.sleep(outer.stream_delay[mid.group(1)])
                size = os.path.getsize(outer.media_path)
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
                length = max(0, end - start + 1)
                self.send_response(code)
                self.send_header("Content-Type", "video/x-matroska")
                self.send_header("Accept-Ranges", "bytes")
                self.send_header("Content-Length", str(length))
                if code == 206:
                    self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
                self.end_headers()
                if head:
                    return
                try:
                    with open(outer.media_path, "rb") as fh:
                        fh.seek(start)
                        left = length
                        while left > 0:
                            chunk = fh.read(min(65536, left))
                            if not chunk:
                                break
                            self.wfile.write(chunk)
                            left -= len(chunk)
                except (ConnectionError, OSError):
                    pass

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), H)
        self.httpd.daemon_threads = True
        self.port = self.httpd.server_address[1]
        self.url = "http://127.0.0.1:%d" % self.port
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def item(self, item_id):
        return {"Id": item_id, "Name": "Fake %s" % item_id[:4], "Type": "Movie",
                "MediaType": "Video", "RunTimeTicks": 1200000000,
                "UserData": {"PlaybackPositionTicks": 0, "Played": False},
                "MediaSources": [self.source(item_id)], "ServerId": "srv" + self.name}

    def segments(self, item_id):
        """The item's MediaSegments (Jellyfin's MediaSegmentDto list): none; a harness's subclass gives some."""
        return []

    def source(self, item_id):
        return {"Id": item_id, "Path": "/media/%s.mkv" % item_id, "Protocol": "File",
                "Container": "mkv", "Size": os.path.getsize(self.media_path),
                "SupportsDirectPlay": True, "SupportsDirectStream": True,
                "SupportsTranscoding": True, "RunTimeTicks": 1200000000,
                "MediaStreams": [{"Type": "Video", "Index": 0, "Codec": "mpeg4",
                                  "Width": 320, "Height": 180, "IsDefault": True}]}

    def playback_info(self, item_id):
        if self.fixed_sessions:
            session = "ps-%s-%s" % (self.name, item_id[:6])
        else:
            with self.lock:
                self._session_seq += 1
                session = "ps-%s-%s-%d" % (self.name, item_id[:6], self._session_seq)
        with self.lock:
            self.issued.append(session)
        return {"MediaSources": [self.source(item_id)], "PlaySessionId": session}

    def sessions(self, kind=None):
        with self.lock:
            out = []
            for r in self.records:
                if r["method"] == "POST" and r["path"].startswith("/Sessions/Playing"):
                    k = {"/Sessions/Playing": "Playing", "/Sessions/Playing/Progress": "Progress",
                         "/Sessions/Playing/Stopped": "Stopped"}.get(r["path"].split("?")[0], r["path"])
                    if kind is None or k == kind:
                        b = r["body"] or {}
                        out.append((k, b.get("ItemId"), b.get("PositionTicks"), r["token"]))
            return out

    def close(self):
        self.httpd.shutdown()


class Sniffer:
    """A 'third-party host': records every request's headers."""
    def __init__(self):
        self.records = []
        outer = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                outer.records.append({"path": self.path, "headers": dict(self.headers)})
                self.send_response(404)
                self.send_header("Content-Length", "0")
                self.end_headers()
            do_HEAD = do_GET

        self.httpd = ThreadingHTTPServer(("127.0.0.2", 0), H)
        self.httpd.daemon_threads = True
        self.port = self.httpd.server_address[1]
        self.url = "http://127.0.0.2:%d" % self.port
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()
