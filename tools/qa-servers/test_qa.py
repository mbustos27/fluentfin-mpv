"""Tests of the QA server tools' own logic (no Jellyfin needed): serve.py's network.xml gate, qaproxy.py's routing,
redaction and forwarding (against a stub upstream on 127.0.0.1), and media.py's spec.

    python -I -B tools\\qa-servers\\test_qa.py
"""

import http.client
import json
import os
import socket
import sys
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import media  # noqa: E402
import qaproxy  # noqa: E402
import serve  # noqa: E402

GOOD = """<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration>
  <EnableHttps>false</EnableHttps><RequireHttps>false</RequireHttps>
  <InternalHttpPort>{port}</InternalHttpPort><PublicHttpPort>{port}</PublicHttpPort>
  <AutoDiscovery>false</AutoDiscovery>{upnp}<EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6>
  <LocalNetworkAddresses>{bind}</LocalNetworkAddresses>
</NetworkConfiguration>"""


def network(tmp, port=8196, upnp="<EnableUPnP>false</EnableUPnP>", bind="<string>127.0.0.1</string>", extra=None):
    text = GOOD.format(port=port, upnp=upnp, bind=bind)
    if extra:
        text = text.replace(*extra)
    with open(os.path.join(tmp, "network.xml"), "w", encoding="utf-8") as f:
        f.write(text)
    return serve.check_network(tmp, 8196)


class NetworkGate(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="qa-net-")

    def test_loopback_only_passes(self):
        self.assertEqual(network(self.tmp), [])

    def test_upnp_may_be_missing(self):
        # 10.11 drops the obsolete EnableUPnP when it saves the file; its default is off
        self.assertEqual(network(self.tmp, upnp=""), [])

    def test_every_unsafe_setting_is_refused(self):
        self.assertTrue(network(self.tmp, bind=""))                                   # all interfaces
        self.assertTrue(network(self.tmp, bind="<string>0.0.0.0</string>"))
        self.assertTrue(network(self.tmp, bind="<string>127.0.0.1</string><string>10.0.0.2</string>"))
        self.assertTrue(network(self.tmp, port=8096))
        self.assertTrue(network(self.tmp, extra=("<AutoDiscovery>false</AutoDiscovery>", "")))   # default: on
        self.assertTrue(network(self.tmp, extra=("<AutoDiscovery>false", "<AutoDiscovery>true")))
        self.assertTrue(network(self.tmp, extra=("<EnableIPv6>false", "<EnableIPv6>true")))
        self.assertTrue(network(self.tmp, extra=("<EnableHttps>false", "<EnableHttps>true")))
        self.assertTrue(network(self.tmp, upnp="<EnableUPnP>true</EnableUPnP>"))

    def test_no_file_is_refused(self):
        self.assertTrue(serve.check_network(self.tmp, 8196))


class Routes(unittest.TestCase):
    def test_the_routes_the_harness_reads(self):
        i = "0123456789abcdef0123456789abcdef"
        cases = {
            "/users/authenticatewithquickconnect": "authenticate", "/users/authenticatebyname": "authenticate",
            "/sessions/playing": "report-playing", "/sessions/playing/stopped": "report-stopped",
            "/sessions/playing/progress": "report-progress", "/items/%s/playbackinfo" % i: "playbackinfo",
            "/videos/%s/stream" % i: "stream", "/videos/%s/stream.mkv" % i: "stream",
            "/videos/%s/master.m3u8" % i: "stream", "/videos/%s/hls1/main/0.ts" % i: "stream",
            "/items/%s/images/primary" % i: "image", "/userviews": "views", "/items": "items",
            "/items/latest": "latest", "/useritems/resume": "resume", "/shows/nextup": "nextup",
            "/socket": "websocket", "/quickconnect/connect": "quickconnect",
            "/useritems/%s/userdata" % i: "user-data", "/userplayeditems/%s" % i: "played",
            # an item's own page is not the items list (an unanchored alternative once matched it)
            "/items/%s" % i: "proxy", "/items/%s/similar" % i: "proxy",
        }
        for path, want in cases.items():
            self.assertEqual(qaproxy.route_of(path), want, path)

    def test_only_a_local_upstream(self):
        with self.assertRaises(SystemExit):
            qaproxy.Tap("http://192.168.1.5:8096", tempfile.mkdtemp(), "x")
        with self.assertRaises(SystemExit):
            qaproxy.Tap("https://127.0.0.1:8920", tempfile.mkdtemp(), "x")


class Stub(BaseHTTPRequestHandler):
    """The upstream: echoes what it got; /chunked answers in chunks without a length; /socket upgrades and echoes."""
    protocol_version = "HTTP/1.1"
    seen = []

    def log_message(self, *a):
        pass

    def do_GET(self):
        Stub.seen.append(("GET", self.path, self.headers.get("Host"), self.headers.get("Authorization")))
        if self.path.startswith("/socket"):
            self.send_response(101)
            self.send_header("Upgrade", "websocket")
            self.send_header("Connection", "Upgrade")
            self.end_headers()
            self.wfile.flush()
            data = self.connection.recv(100)
            self.connection.sendall(b"echo:" + data)
            self.close_connection = True
            return
        if self.path.startswith("/short"):
            # declares 10 bytes, sends 3, and hangs up
            self.send_response(200)
            self.send_header("Content-Length", "10")
            self.end_headers()
            self.wfile.write(b"abc")
            self.wfile.flush()
            self.close_connection = True
            return
        if self.path.startswith("/chunked"):
            self.send_response(200)
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            for part in (b"abc", b"defgh"):
                self.wfile.write(b"%x\r\n%s\r\n" % (len(part), part))
                self.wfile.flush()
                time.sleep(0.05)
            self.wfile.write(b"0\r\n\r\n")
            return
        body = json.dumps({"path": self.path}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        data = self.rfile.read(n)
        Stub.seen.append(("POST", self.path, self.headers.get("Host"), data))
        self.send_response(204)
        self.send_header("Content-Length", "0")
        self.end_headers()


class Forwarding(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.up = ThreadingHTTPServer(("127.0.0.1", 0), Stub)
        cls.up.daemon_threads = True
        threading.Thread(target=cls.up.serve_forever, daemon=True).start()
        cls.out = tempfile.mkdtemp(prefix="qa-tap-")
        cls.tap = qaproxy.Tap("http://127.0.0.1:%d" % cls.up.server_address[1], cls.out, "unused")
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), qaproxy.make_handler(cls.tap))
        cls.srv.daemon_threads = True
        cls.tap.server = cls.srv
        cls.port = cls.srv.server_address[1]
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.up.shutdown()
        cls.tap.log_file.close()

    def log(self):
        with open(os.path.join(self.out, "requests.jsonl"), encoding="utf-8") as f:
            return [json.loads(line) for line in f]

    def test_get_post_chunked_and_the_log(self):
        c = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        c.request("GET", "/Items?api_key=SECRETTOKEN&userId=u1",
                  headers={"Authorization": 'MediaBrowser Client="Nixie", Token="SECRET2"', "User-Agent": "kiota-dotnet/1"})
        r = c.getresponse()
        self.assertEqual(r.status, 200)
        self.assertEqual(json.loads(r.read())["path"], "/Items?api_key=SECRETTOKEN&userId=u1")
        # keep-alive: the same connection again, a POST with a body
        c.request("POST", "/Sessions/Playing/Stopped", body=json.dumps({"PlaySessionId": "ps1", "PositionTicks": 1e8}),
                  headers={"Content-Type": "application/json", "User-Agent": "Nixie/1.5"})
        r = c.getresponse()
        r.read()
        self.assertEqual(r.status, 204)
        c.request("GET", "/chunked")
        r = c.getresponse()
        self.assertEqual(r.read(), b"abcdefgh")
        c.close()
        time.sleep(0.2)
        # the original Host header goes on, as a reverse proxy sends it
        self.assertTrue(any(s[0] == "GET" and s[2] == "127.0.0.1:%d" % self.port for s in Stub.seen))
        self.assertTrue(any(s[0] == "POST" and b"ps1" in s[3] for s in Stub.seen))
        entries = self.log()
        text = json.dumps(entries)
        self.assertNotIn("SECRETTOKEN", text)
        self.assertNotIn("SECRET2", text)
        items = [e for e in entries if e["path"] == "/Items"][0]
        self.assertEqual(items["route"], "items")
        self.assertEqual(items["query"]["api_key"], ["<redacted>"])
        self.assertEqual(items["client"], "Nixie")
        self.assertTrue(items["auth"])
        stopped = [e for e in entries if e["route"] == "report-stopped"][0]
        self.assertEqual(stopped["report"]["play_session"], "ps1")
        self.assertEqual(stopped["ua"], "Nixie/1.5")
        self.assertEqual([e for e in entries if e["path"] == "/chunked"][0]["bytes"], 8)

    def test_websocket_is_a_pipe(self):
        s = socket.create_connection(("127.0.0.1", self.port), timeout=10)
        s.sendall(b"GET /socket?api_key=X HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n")
        head = b""
        while b"\r\n\r\n" not in head:
            head += s.recv(1024)
        self.assertIn(b" 101 ", head.split(b"\r\n")[0] + b" ")
        s.sendall(b"hello")
        got = head.split(b"\r\n\r\n", 1)[1]
        while len(got) < 10:
            chunk = s.recv(1024)
            if not chunk:
                break
            got += chunk
        self.assertEqual(got, b"echo:hello")
        s.close()
        time.sleep(0.3)
        ws = [e for e in self.log() if e["route"] == "websocket"]
        self.assertTrue(ws and ws[-1]["status"] == 101)

    def test_an_upstream_that_ends_early_closes_the_connection(self):
        # the client was promised 10 bytes and got 3: the tap must hang up, never leave the connection open as if whole
        s = socket.create_connection(("127.0.0.1", self.port), timeout=5)
        s.sendall(b"GET /short HTTP/1.1\r\nHost: x\r\n\r\n")
        got = b""
        try:
            while True:
                chunk = s.recv(1024)
                if not chunk:
                    break
                got += chunk
        except socket.timeout:
            self.fail("the tap kept the connection open after a short body (%r)" % got)
        finally:
            s.close()
        self.assertTrue(got.endswith(b"abc"))

    def test_harness_endpoint_unknown(self):
        c = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        c.request("GET", "/__harness/release/latest")
        r = c.getresponse()
        r.read()
        self.assertEqual(r.status, 404)
        c.close()


class MediaSpec(unittest.TestCase):
    def test_spec(self):
        names = [rel for rel, _, _ in media.SPEC]
        self.assertEqual(len(names), len(set(names)))
        self.assertIn("Movies/The Matrix (1999)/The Matrix (1999).mkv", names)
        self.assertIn("Movies/The Matrix Reloaded (2003)/The Matrix Reloaded (2003).mkv", names)
        self.assertTrue(any(o.get("v") == "hevc10" for _, _, o in media.SPEC))
        self.assertTrue(any(len(o.get("a") or []) >= 3 and o.get("sidecar") for _, _, o in media.SPEC))
        self.assertTrue(any(rel.startswith("Shows/") and "/Specials/" in rel for rel in names))
        self.assertTrue(any(ord(ch) > 127 for rel in names for ch in rel))
        self.assertLessEqual(max(sec for _, sec, _ in media.SPEC), 120)
        # a spec change rebuilds the file; the same spec never does
        rel, sec, opts = media.SPEC[0]
        self.assertEqual(media.spec_key(rel, sec, opts), media.spec_key(rel, sec, dict(opts)))
        self.assertNotEqual(media.spec_key(rel, sec, opts), media.spec_key(rel, sec + 1, opts))

    def test_srt(self):
        text = media.srt("eng", "x", 70)
        self.assertIn("00:01:06,000 --> 00:01:09,000", text)
        self.assertEqual(text.count(" --> "), 14)


if __name__ == "__main__":
    unittest.main(verbosity=1)
