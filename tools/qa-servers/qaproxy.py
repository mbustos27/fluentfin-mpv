"""A logging tap in front of a local QA Jellyfin server, for the off-screen UI harness (run-offscreen.ps1 -QaServer).

    python -I -B qaproxy.py --upstream http://127.0.0.1:8196 --out <dir> --ready-file <file>
                            [--credentials %USERPROFILE%\\Dev\\jellyfin-qa\\qa-credentials.json]

The harness judges a run by the requests its fake server logged (who reported Playing and Stopped, when sign-in
answered) and drives it through /__harness/ endpoints. In front of a real server this tap plays the fake server's part
and nothing else:
- Every request is forwarded unchanged to --upstream (the original Host header kept; bodies streamed both ways; a
  WebSocket upgrade becomes a byte pipe) and written to <out>/requests.jsonl in the fake server's format (time, method,
  path, query with keys and tokens redacted, status, bytes, ms, client, User-Agent, route). Never a token or a password.
- GET /__harness/fixture: the showcase items the driver navigates to, read from the server as the ordinary test user.
- POST /__harness/quickconnect/approve {"code"}: the ordinary test user approves the code (POST /QuickConnect/Authorize),
  as a user would on another device. GET /__harness/summary, POST /__harness/shutdown.
Listens on 127.0.0.1 only (an ephemeral port unless --port). Stdlib only.
"""

import argparse
import http.client
import json
import os
import re
import socket
import sys
import threading
import time
import urllib.parse
from collections import Counter
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SECRET_KEYS = {"api_key", "apikey", "token", "x-emby-token", "pw", "password", "secret"}
HOP = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailer", "trailers",
       "transfer-encoding", "upgrade"}
CLIENT_RE = re.compile(r'Client="([^"]*)"')
TOKEN_RE = re.compile(r'Token="([^"]*)"')
ID = r"([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"
SHOWCASE = {"movie": ("Movies", "Big Buck Bunny"), "series": ("Shows", "The Twilight Zone"),
            "anime": ("Anime", "Spy x Family")}

ROUTES = [  # (regex on the lower-case path, route): the fake server's names where the harness reads them
    (r"/system/info/public", "public-info"), (r"/system/ping", "ping"),
    (r"/users/authenticatebyname|/users/authenticatewithquickconnect", "authenticate"),
    (r"/users/public", "users-public"), (r"/quickconnect/.*", "quickconnect"),
    (r"/sessions/playing", "report-playing"), (r"/sessions/playing/progress", "report-progress"),
    (r"/sessions/playing/stopped", "report-stopped"), (r"/sessions/playing/ping", "report-ping"),
    (r"/items/" + ID + r"/playbackinfo", "playbackinfo"),
    (r"/(videos|audio)/" + ID + r"/(stream(\.[a-z0-9]+)?|master\.m3u8|main\.m3u8|live\.m3u8|hls\d*/.*)", "stream"),
    (r"/items/" + ID + r"/download", "stream"),
    (r"/videos/" + ID + r"/" + ID + r"/subtitles/.*", "subtitles"), (r"/videos/" + ID + r"/trickplay/.*", "trickplay"),
    (r"/items/" + ID + r"/images(/.*)?", "image"), (r"/branding/.*", "branding"), (r"/socket", "websocket"),
    (r"/users/me", "users-me"), (r"/userviews|/library/mediafolders|/users/" + ID + r"/views", "views"),
    (r"/useritems/resume|/users/" + ID + r"/items/resume", "resume"),
    (r"/items/latest|/users/" + ID + r"/items/latest", "latest"), (r"/items/filters2?", "filters"),
    (r"/items|/users/" + ID + r"/items", "items"), (r"/shows/nextup", "nextup"),
    (r"/shows/" + ID + r"/seasons", "seasons"), (r"/shows/" + ID + r"/episodes", "episodes"),
    (r"(/userplayeditems|/users/" + ID + r"/playeditems)/" + ID, "played"),
    (r"(/useritems|/users/" + ID + r"/items)/" + ID + r"/userdata", "user-data"),
    (r"/sessions/logout", "logout"), (r"/sessions.*", "sessions"),
]
ROUTES = [(re.compile(r"(?:" + rx + r")/?$"), name) for rx, name in ROUTES]


def route_of(low):
    for rx, name in ROUTES:
        if rx.match(low):
            return name
    return "proxy"


class Tap:
    def __init__(self, upstream, out, credentials):
        u = urllib.parse.urlsplit(upstream)
        if u.scheme != "http" or u.hostname not in ("127.0.0.1", "localhost"):
            raise SystemExit("qaproxy: --upstream must be http://127.0.0.1:<port> (a local QA server)")
        self.up_host, self.up_port = u.hostname, u.port or 80
        self.upstream = upstream.rstrip("/")
        self.out = out
        self.credentials = credentials
        self.lock = threading.Lock()
        self.counts = Counter()
        self.unhandled = Counter()
        self.log_file = open(os.path.join(out, "requests.jsonl"), "a", encoding="utf-8")
        self._user = None
        self._fixture = None
        self.url = None
        self.server = None

    def record(self, entry):
        with self.lock:
            self.counts[entry.get("route") or "?"] += 1
            self.log_file.write(json.dumps(entry, ensure_ascii=False) + "\n")
            self.log_file.flush()

    def summary(self):
        with self.lock:
            return {"upstream": self.upstream, "url": self.url, "by_route": dict(self.counts.most_common()),
                    "unhandled": dict(self.unhandled), "requests": sum(self.counts.values())}

    def write_summary(self):
        with open(os.path.join(self.out, "summary.json"), "w", encoding="utf-8") as f:
            json.dump(self.summary(), f, indent=1)

    # --- the harness's own calls to the server, as the ordinary test user (the token stays in this process) ---
    def _call(self, method, path, body=None, token=None):
        conn = http.client.HTTPConnection(self.up_host, self.up_port, timeout=30)
        auth = 'MediaBrowser Client="Nixie QA harness", Device="qa-harness-tap", DeviceId="nixie-qa-harness-tap", ' \
               'Version="1.0"' + (', Token="%s"' % token if token else "")
        data = json.dumps(body).encode("utf-8") if body is not None else None
        headers = {"Authorization": auth, "Accept": "application/json"}
        if data is not None:
            headers["Content-Type"] = "application/json"
        conn.request(method, path, body=data if data is not None else (b"" if method == "POST" else None), headers=headers)
        r = conn.getresponse()
        raw = r.read()
        conn.close()
        try:
            return r.status, json.loads(raw) if raw else None
        except ValueError:
            return r.status, None

    def user(self):
        if self._user is None:
            with open(self.credentials, encoding="utf-8-sig") as f:
                c = json.load(f)["user"]
            status, res = self._call("POST", "/Users/AuthenticateByName", {"Username": c["username"], "Pw": c["password"]})
            if status != 200 or not res or not res.get("AccessToken"):
                raise RuntimeError("the ordinary test user could not sign in (HTTP %s)" % status)
            self._user = (res["AccessToken"], res["User"]["Id"], res["User"]["Name"])
        return self._user

    def fixture(self):
        if self._fixture is not None:
            return self._fixture
        token, uid, name = self.user()
        _, views = self._call("GET", "/UserViews?userId=%s" % uid, token=token)
        views = (views or {}).get("Items") or []
        by_name = {v["Name"]: v["Id"] for v in views}
        found = {}
        for key, (lib, title) in SHOWCASE.items():
            kind = "Movie" if key == "movie" else "Series"
            q = "/Items?userId=%s&parentId=%s&recursive=true&includeItemTypes=%s&searchTerm=%s&limit=5" % (
                uid, by_name.get(lib, ""), kind, urllib.parse.quote(title))
            _, res = self._call("GET", q, token=token)
            items = (res or {}).get("Items") or []
            if items:
                found[key] = {"id": items[0]["Id"], "name": items[0]["Name"], "library": lib}
        series = found.get("series")
        season = episode = None
        if series:
            _, res = self._call("GET", "/Shows/%s/Seasons?userId=%s" % (series["id"], uid), token=token)
            seasons = [s for s in (res or {}).get("Items") or [] if s.get("IndexNumber") == 1]
            if seasons:
                season = {"id": seasons[0]["Id"], "name": seasons[0]["Name"]}
                _, res = self._call("GET", "/Shows/%s/Episodes?userId=%s&seasonId=%s" % (series["id"], uid, season["id"]),
                                    token=token)
                eps = (res or {}).get("Items") or []
                if eps:
                    episode = {"id": eps[0]["Id"], "name": eps[0]["Name"], "index": eps[0].get("IndexNumber") or 1}
        counts = {}
        for kind, key in (("Movie", "movie"), ("Series", "series"), ("Episode", "episode")):
            _, res = self._call("GET", "/Items?userId=%s&recursive=true&includeItemTypes=%s&limit=0" % (uid, kind),
                                token=token)
            counts[key] = (res or {}).get("TotalRecordCount", 0)
        self._fixture = {"movie": dict(found.get("movie") or {}, r_word=None), "series": series, "season": season,
                         "episode": episode, "anime": found.get("anime"), "documentary": None,
                         "views": [v["Name"] for v in views], "collections": [], "big": [],
                         "server": self.url, "upstream": self.upstream, "username": name, "counts": counts}
        return self._fixture

    def approve(self, code):
        token, uid, _ = self.user()
        status, _ = self._call("POST", "/QuickConnect/Authorize?code=%s&userId=%s" % (
            urllib.parse.quote(code), uid), token=token)
        return status

    def sign_out(self):
        if self._user:
            try:
                self._call("POST", "/Sessions/Logout", token=self._user[0])
            except OSError:
                pass


def make_handler(tap):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "qaproxy"

        def log_message(self, *args):
            pass

        def do_GET(self): self._handle()
        def do_POST(self): self._handle()
        def do_PUT(self): self._handle()
        def do_DELETE(self): self._handle()
        def do_HEAD(self): self._handle()
        def do_OPTIONS(self): self._handle()
        def do_PATCH(self): self._handle()

        def _handle(self):
            t0 = time.perf_counter()
            started = time.time()
            parsed = urllib.parse.urlsplit(self.path)
            path = urllib.parse.unquote(parsed.path)
            low = (path.rstrip("/") or "/").lower()
            q = {k.lower(): v for k, v in urllib.parse.parse_qs(parsed.query, keep_blank_values=True).items()}
            auth = self.headers.get("Authorization") or self.headers.get("X-Emby-Authorization") or ""
            cm = CLIENT_RE.search(auth)
            given = bool(TOKEN_RE.search(auth) or self.headers.get("X-Emby-Token") or self.headers.get("X-MediaBrowser-Token")
                         or q.get("api_key") or q.get("apikey"))
            self.status, self.sent, self.extra = 0, 0, None
            route = "harness" if low.startswith("/__harness/") else route_of(low)
            try:
                if route == "harness":
                    self._harness(low)
                elif (self.headers.get("Upgrade") or "").lower() == "websocket":
                    self._websocket()
                else:
                    self._forward(route)
            except (ConnectionError, OSError):
                self.close_connection = True
            except Exception as ex:  # never take the tap down
                print("qaproxy: %s %s failed: %r" % (self.command, path, ex), file=sys.stderr)
                if not self.status:
                    try:
                        self._send(502, json.dumps({"error": type(ex).__name__}).encode(), "application/json")
                    except OSError:
                        pass
                self.close_connection = True
            finally:
                entry = {"t": round(started, 4), "method": self.command, "path": path,
                         "query": {k: (["<redacted>"] if k in SECRET_KEYS else v) for k, v in q.items()},
                         "status": self.status, "bytes": self.sent, "ms": round((time.perf_counter() - t0) * 1000, 1),
                         "delay_ms": 0.0, "client": cm.group(1) if cm else "", "ua": self.headers.get("User-Agent") or "",
                         "auth": given, "route": route}
                if self.extra:
                    entry["report"] = self.extra
                tap.record(entry)

        def _send(self, code, body=b"", ctype=None):
            self.status = code
            self.send_response_only(code)
            if ctype:
                self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if self.command != "HEAD" and body:
                self.wfile.write(body)
            self.sent = len(body)

        def _json(self, code, obj):
            self._send(code, json.dumps(obj).encode("utf-8"), "application/json; charset=utf-8")

        def _read_body(self):
            if (self.headers.get("Transfer-Encoding") or "").lower() == "chunked":
                data = bytearray()
                while True:
                    size = int(self.rfile.readline().split(b";")[0].strip() or b"0", 16)
                    if size == 0:
                        while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                            pass
                        return bytes(data)
                    data += self.rfile.read(size)
                    self.rfile.readline()
            n = int(self.headers.get("Content-Length") or 0)
            return self.rfile.read(n) if n else b""

        # ---------------- the harness's endpoints
        def _harness(self, low):
            body = self._read_body()
            if low == "/__harness/fixture":
                return self._json(200, tap.fixture())
            if low == "/__harness/summary":
                tap.write_summary()
                return self._json(200, tap.summary())
            if low == "/__harness/quickconnect/approve" and self.command == "POST":
                try:
                    code = str(json.loads(body or b"{}").get("code") or "")
                except ValueError:
                    code = ""
                status = tap.approve(code) if code else 400
                return self._json(200 if status == 200 else 404, {"approved": status == 200, "status": status})
            if low == "/__harness/quickconnect":
                return self._json(200, [])
            if low == "/__harness/shutdown" and self.command == "POST":
                self._json(200, {"ok": True})
                tap.write_summary()
                threading.Thread(target=tap.server.shutdown, daemon=True).start()
                return
            tap.unhandled[low] += 1
            return self._json(404, {"error": "not a QA tap endpoint (the fake server's harness endpoints are not here)"})

        # ---------------- forwarding
        def _forward(self, route):
            body = self._read_body()
            if route.startswith("report-") and body:
                try:
                    b = json.loads(body)
                    self.extra = {"kind": route[7:].capitalize(), "item": b.get("ItemId"), "position": b.get("PositionTicks"),
                                  "play_session": b.get("PlaySessionId"), "paused": b.get("IsPaused"),
                                  "failed": b.get("Failed"), "audio": b.get("AudioStreamIndex"),
                                  "subtitle": b.get("SubtitleStreamIndex")}
                except ValueError:
                    pass
            headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP and k.lower() != "content-length"}
            if body or self.command in ("POST", "PUT", "PATCH"):
                headers["Content-Length"] = str(len(body))
            conn = http.client.HTTPConnection(tap.up_host, tap.up_port, timeout=300)
            try:
                conn.putrequest(self.command, self.path, skip_host=True, skip_accept_encoding=True)
                for k, v in headers.items():
                    conn.putheader(k, v)
                conn.endheaders(body if body else None)
                resp = conn.getresponse()
                self.status = resp.status
                self.send_response_only(resp.status, resp.reason)
                length = resp.getheader("Content-Length")
                for k, v in resp.getheaders():
                    if k.lower() not in HOP and k.lower() != "content-length":
                        self.send_header(k, v)
                no_body = self.command == "HEAD" or resp.status in (204, 304) or 100 <= resp.status < 200
                chunked = not no_body and length is None
                if no_body:
                    self.send_header("Content-Length", length or "0")
                elif chunked:
                    self.send_header("Transfer-Encoding", "chunked")
                else:
                    self.send_header("Content-Length", length)
                self.end_headers()
                if no_body:
                    return
                while True:
                    data = resp.read1(65536)
                    if not data:
                        break
                    if chunked:
                        self.wfile.write(b"%x\r\n%s\r\n" % (len(data), data))
                    else:
                        self.wfile.write(data)
                    self.sent += len(data)
                if chunked:
                    self.wfile.write(b"0\r\n\r\n")
                elif self.sent != int(length):
                    self.close_connection = True    # the upstream ended early: this connection's framing is broken
                self.wfile.flush()
            finally:
                conn.close()

        def _websocket(self):
            up = socket.create_connection((tap.up_host, tap.up_port), timeout=30)
            try:
                head = "%s %s HTTP/1.1\r\n" % (self.command, self.path) + \
                       "".join("%s: %s\r\n" % (k, v) for k, v in self.headers.items()) + "\r\n"
                up.sendall(head.encode("latin-1"))
                buf = b""
                while b"\r\n\r\n" not in buf:
                    chunk = up.recv(4096)
                    if not chunk:
                        raise ConnectionError("upstream closed during the upgrade")
                    buf += chunk
                status_line = buf.split(b"\r\n", 1)[0].decode("latin-1")
                self.status = int(status_line.split()[1])
                self.connection.sendall(buf)
                if self.status != 101:
                    self.close_connection = True
                    return
                up.settimeout(None)
                self.connection.settimeout(None)
                done = threading.Event()

                def pump(read, dst, count):
                    try:
                        while True:
                            data = read(65536)
                            if not data:
                                break
                            dst.sendall(data)
                            if count:
                                self.sent += len(data)
                    except OSError:
                        pass
                    finally:
                        done.set()

                # the client's side through rfile: bytes it sent right after the upgrade may already be buffered there
                threading.Thread(target=pump, args=(up.recv, self.connection, True), daemon=True).start()
                threading.Thread(target=pump, args=(self.rfile.read1, up, False), daemon=True).start()
                done.wait()
                self.close_connection = True
            finally:
                try:
                    up.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                up.close()

    return Handler


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--upstream", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--ready-file", required=True)
    ap.add_argument("--credentials", default=os.path.join(
        os.environ.get("NIXIE_QA_ROOT") or os.path.join(os.path.expanduser("~"), "Dev", "jellyfin-qa"), "qa-credentials.json"))
    ap.add_argument("--port", type=int, default=0)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    tap = Tap(a.upstream, a.out, a.credentials)
    server = ThreadingHTTPServer(("127.0.0.1", a.port), make_handler(tap))
    server.daemon_threads = True
    tap.server = server
    tap.url = "http://127.0.0.1:%d" % server.server_address[1]
    tap.user()          # fail early, before the harness starts the app, if the test user cannot sign in
    tmp = a.ready_file + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"url": tap.url, "upstream": tap.upstream}, f)
    os.replace(tmp, a.ready_file)
    print("qaproxy: %s -> %s" % (tap.url, tap.upstream), flush=True)
    try:
        server.serve_forever(poll_interval=0.25)
    finally:
        tap.write_summary()
        tap.sign_out()
        tap.log_file.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
