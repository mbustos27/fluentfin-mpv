"""Tests of g4_forward.py (the basics gate's offline switch): it forwards both ways, reads the DeviceId of requests and keeps
nothing else, and while offline resets what is open and refuses what comes, counting the attempts, until online again.

    python -I -B tools\\gate\\checks\\test_g4_forward.py

Starts the forwarder and a small echo server on 127.0.0.1 in a temporary folder; ends both (its own processes only).
"""

import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))


def _echo_server():
    srv = socket.socket()
    srv.bind(("127.0.0.1", 0))
    srv.listen(16)

    def serve():
        while True:
            try:
                c, _ = srv.accept()
            except OSError:
                return

            def one(c=c):
                try:
                    while True:
                        d = c.recv(65536)
                        if not d:
                            break
                        c.sendall(d)
                except OSError:
                    pass
                finally:
                    c.close()
            threading.Thread(target=one, daemon=True).start()
    threading.Thread(target=serve, daemon=True).start()
    return srv


def _wait(test, seconds=5.0):
    end = time.time() + seconds
    while time.time() < end:
        v = test()
        if v:
            return v
        time.sleep(0.05)
    return None


class ForwarderTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="g4fwd-")
        self.echo = _echo_server()
        target = "127.0.0.1:%d" % self.echo.getsockname()[1]
        self.proc = subprocess.Popen([sys.executable, "-I", "-B", os.path.join(HERE, "g4_forward.py"), "--target", target,
                                      "--dir", self.dir])
        port = _wait(lambda: os.path.exists(os.path.join(self.dir, "port.txt")) and open(os.path.join(self.dir, "port.txt")).read().strip())
        self.assertTrue(port, "the forwarder wrote no port")
        self.port = int(port)

    def tearDown(self):
        open(os.path.join(self.dir, "stop"), "w").close()
        try:
            self.proc.wait(5)
        except subprocess.TimeoutExpired:
            self.proc.kill()   # the Popen handle of the process this test started
            self.proc.wait(5)
        self.echo.close()

    def stats(self):
        try:
            with open(os.path.join(self.dir, "stats.json"), encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return None

    def connect(self):
        return socket.create_connection(("127.0.0.1", self.port), timeout=3)

    def test_forwards_both_ways_and_keeps_only_the_device_id(self):
        s = self.connect()
        req = b'GET /Users/Me HTTP/1.1\r\nAuthorization: MediaBrowser Client="Nixie", DeviceId="dev-1", Token="secret-token"\r\n\r\n'
        s.sendall(req)
        got = b""
        while len(got) < len(req):
            got += s.recv(65536)
        self.assertEqual(got, req)
        st = _wait(lambda: (self.stats() or {}).get("devices", {}).get("dev-1") and self.stats())
        self.assertIsNotNone(st)
        self.assertEqual(list(st["devices"]), ["dev-1"])
        self.assertNotIn("secret-token", json.dumps(st))
        s.close()

    def test_a_reader_holding_the_stats_open_does_not_stop_the_switch(self):
        # the harness reads stats.json while the forwarder rewrites it: on Windows the replace is refused then, and the
        # switch must still follow the flag once the reader lets go
        self.assertTrue(_wait(lambda: os.path.exists(os.path.join(self.dir, "stats.json"))))
        flag = os.path.join(self.dir, "offline")
        with open(os.path.join(self.dir, "stats.json"), encoding="utf-8"):
            open(flag, "w").close()
            time.sleep(0.5)
            os.remove(flag)
            time.sleep(0.5)
        open(flag, "w").close()
        self.assertTrue(_wait(lambda: (self.stats() or {}).get("offline")), "offline not seen after the reader let go")
        os.remove(flag)
        self.assertTrue(_wait(lambda: (self.stats() or {}).get("offline") is False))

    # 1.8.1 (BH-175-m4): a Stopped report held in the network while hold-stopped is there; anything else goes on
    def test_a_stopped_report_is_held_while_asked_and_passes_once_let_go(self):
        hold = os.path.join(self.dir, "hold-stopped")
        open(hold, "w").close()
        other = self.connect()
        other.sendall(b"POST /Sessions/Playing/Progress HTTP/1.1\r\n\r\n")
        self.assertTrue(other.recv(65536).startswith(b"POST /Sessions/Playing/Progress"), "a progress report is not held")
        s = self.connect()
        req = b"POST /Sessions/Playing/Stopped HTTP/1.1\r\nContent-Length: 0\r\n\r\n"
        s.sendall(req)
        s.settimeout(1.0)
        with self.assertRaises(socket.timeout):
            s.recv(65536)   # held: nothing came back
        self.assertTrue(_wait(lambda: (self.stats() or {}).get("held") == 1))
        os.remove(hold)
        s.settimeout(3)
        got = b""
        while len(got) < len(req):
            got += s.recv(65536)
        self.assertEqual(got, req)
        s.close()
        other.close()

    def test_offline_resets_and_refuses_then_online_forwards_again(self):
        open_conn = self.connect()
        open_conn.sendall(b"x")
        self.assertEqual(open_conn.recv(1), b"x")
        flag = os.path.join(self.dir, "offline")
        open(flag, "w").close()
        self.assertTrue(_wait(lambda: (self.stats() or {}).get("offline")))
        # the open connection is reset
        with self.assertRaises(OSError):
            open_conn.settimeout(3)
            for _ in range(3):
                open_conn.sendall(b"y")
                if not open_conn.recv(1):
                    raise ConnectionResetError("closed")
                time.sleep(0.1)
        # new ones are refused (reset at once) and counted
        for _ in range(3):
            c = self.connect()
            c.settimeout(3)
            try:
                c.sendall(b"z")
                self.assertIn(c.recv(1), (b"",))
            except OSError:
                pass
            finally:
                c.close()
        self.assertTrue(_wait(lambda: (self.stats() or {}).get("resets", 0) >= 3))
        os.remove(flag)
        self.assertTrue(_wait(lambda: (self.stats() or {}).get("offline") is False))
        c = self.connect()
        c.sendall(b"back")
        got = b""
        while len(got) < 4:
            got += c.recv(65536)
        self.assertEqual(got, b"back")
        c.close()


if __name__ == "__main__":
    unittest.main()
