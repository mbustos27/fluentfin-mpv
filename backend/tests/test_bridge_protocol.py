"""The bridge's wire protocol, with no player behind it.

`jellyfin_mpv_shim.bridge_protocol` is everything about the protocol that
can be checked without mpv: framing, stdout isolation, argument validation,
redaction and the Jellyfin <-> mpv translations. The contract is
``BRIDGE.md`` at the repository root (``../docs/BRIDGE.md`` from
``backend/``); docs/bridge.md is how the backend implements it.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import io
import json
import logging
import os
import subprocess
import sys
import threading
import unittest

sys.argv = [sys.argv[0]]      # importing the shim reaches args.get_args()

from jellyfin_mpv_shim import bridge_protocol as proto  # noqa: E402
from jellyfin_mpv_shim.bridge_protocol import BridgeError  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

A = "a" * 32
B = "b" * 32


class FramingTest(unittest.TestCase):
    def test_one_compact_line_of_utf8(self):
        data = proto.encode_message({"event": "item", "data": {
            "name": "Amélie\nPart 2"}})
        self.assertTrue(data.endswith(b"\n"))
        self.assertEqual(data.count(b"\n"), 1,
                         "a newline inside a string must stay escaped")
        self.assertFalse(data.startswith(b"\xef\xbb\xbf"), "no BOM")
        self.assertIn("Amélie".encode("utf-8"), data)
        self.assertNotIn(b": ", data)
        self.assertEqual(json.loads(data)["data"]["name"], "Amélie\nPart 2")

    def test_a_lone_surrogate_is_replaced_and_the_line_is_valid_utf8(self):
        """What the server's JSON hands over for a badly decoded file name:
        ``json`` keeps the unpaired surrogate, and UTF-8 cannot encode it.
        It used to raise out of the writer and kill the writing thread."""
        lone, low, replacement = chr(0xD83D), chr(0xDE00), chr(0xFFFD)
        name = json.loads('{"Name": "Episode \\' 'ud83d broken"}')["Name"]
        self.assertEqual(name, "Episode " + lone + " broken")
        data = proto.encode_message({"event": "item", "data": {
            "name": name}})
        text = data.decode("utf-8")          # strict: valid UTF-8
        self.assertEqual(json.loads(text)["data"]["name"],
                         "Episode " + replacement + " broken")
        self.assertNotIn("\\" "ud", text, "no surrogate escape either: "
                         "System.Text.Json refuses an unpaired one")
        # A pair held as two code points (Python never joins them) is
        # rejoined, not replaced.
        pair = lone + low
        self.assertEqual(len(pair), 2)
        self.assertEqual(json.loads(proto.encode_message({"n": pair}))["n"],
                         chr(0x1F600))

    def test_nan_is_refused_rather_than_sent(self):
        with self.assertRaises(ValueError):
            proto.encode_message({"x": float("nan")})

    def test_a_request_round_trips(self):
        req = proto.decode_line(b'{"id": 7, "cmd": "seek", '
                                b'"args": {"positionTicks": 6000000000}}\n')
        self.assertEqual((req.id, req.cmd, req.args),
                         (7, "seek", {"positionTicks": 6000000000}))

    def test_args_default_to_empty(self):
        self.assertEqual(proto.decode_line(b'{"id":1,"cmd":"ping"}').args, {})
        self.assertEqual(
            proto.decode_line(b'{"id":1,"cmd":"ping","args":null}').args, {})

    def test_a_blank_line_is_nothing(self):
        self.assertIsNone(proto.decode_line(b"\r\n"))
        self.assertIsNone(proto.decode_line(b""))

    def test_bom_and_crlf_are_tolerated(self):
        req = proto.decode_line('﻿{"id":2,"cmd":"ping"}\r\n'
                                .encode("utf-8"))
        self.assertEqual((req.id, req.cmd), (2, "ping"))

    def test_lines_with_nobody_to_answer_are_framing_errors(self):
        for line in (b"garbage", b"[1, 2]", b'"ping"', b'{"cmd": "ping"}',
                     b'{"id": 0, "cmd": "ping"}', b'{"id": -3, "cmd": "x"}',
                     b'{"id": true, "cmd": "ping"}', b'{"id": "1", "cmd": "x"}',
                     b'{"id": 1.5, "cmd": "ping"}', b"\xff\xfe{}"):
            with self.subTest(line=line):
                with self.assertRaises(proto.FramingError):
                    proto.decode_line(line)

    def test_a_request_with_an_id_but_no_cmd_is_bad_args_for_that_id(self):
        for line in (b'{"id": 4}', b'{"id": 4, "cmd": ""}',
                     b'{"id": 4, "cmd": 12}',
                     b'{"id": 4, "cmd": "seek", "args": [1]}'):
            with self.subTest(line=line):
                with self.assertRaises(BridgeError) as caught:
                    proto.decode_line(line)
                self.assertEqual(caught.exception.code, proto.BAD_ARGS)
                self.assertEqual(caught.exception.request_id, 4)

    def test_repr_never_shows_args(self):
        req = proto.decode_line(b'{"id":1,"cmd":"connect","args":'
                                b'{"accessToken":"secret-token-value"}}')
        self.assertNotIn("secret", repr(req))


class _Sink(io.RawIOBase):
    """A raw stream that may write short, or break."""

    def __init__(self, chunk=None, broken=False):
        self.data = bytearray()
        self.chunk = chunk
        self.broken = broken

    def writable(self):
        return True

    def write(self, b):
        if self.broken:
            raise BrokenPipeError("gone")
        b = bytes(b)
        if self.chunk:
            b = b[:self.chunk]
        self.data += b
        return len(b)


class WriterTest(unittest.TestCase):
    def test_short_writes_are_completed(self):
        sink = _Sink(chunk=3)
        proto.ProtocolWriter(sink).event("hello", {"protocol": 1})
        self.assertEqual(json.loads(bytes(sink.data)),
                         {"event": "hello", "data": {"protocol": 1}})

    def test_concurrent_writers_never_interleave(self):
        sink = _Sink(chunk=5)
        writer = proto.ProtocolWriter(sink)

        def spam(n):
            for i in range(200):
                writer.event("position", {"thread": n, "i": i,
                                          "pad": "x" * 50})

        threads = [threading.Thread(target=spam, args=(n,)) for n in range(4)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        lines = bytes(sink.data).split(b"\n")
        self.assertEqual(lines[-1], b"")
        self.assertEqual(len(lines) - 1, 800)
        for line in lines[:-1]:
            json.loads(line)

    def test_a_broken_pipe_closes_once_and_says_so_once(self):
        calls = []
        writer = proto.ProtocolWriter(_Sink(broken=True),
                                      on_broken=lambda: calls.append(1))
        self.assertFalse(writer.event("state", {}))
        self.assertFalse(writer.event("state", {}))
        self.assertTrue(writer.closed)
        self.assertEqual(calls, [1])

    def test_shapes(self):
        sink = _Sink()
        writer = proto.ProtocolWriter(sink)
        writer.respond(3)
        writer.respond(4, {"mpv": "v"})
        writer.fail(5, proto.NO_PLAYBACK, "Nothing is playing")
        writer.event("ended", {"itemId": "a", "reason": "eof"})
        got = [json.loads(line) for line in bytes(sink.data).splitlines()]
        self.assertEqual(got, [
            {"id": 3, "ok": True, "result": {}},
            {"id": 4, "ok": True, "result": {"mpv": "v"}},
            {"id": 5, "ok": False, "error": {"code": "no_playback",
                                             "message": "Nothing is playing"}},
            {"event": "ended", "data": {"itemId": "a", "reason": "eof"}},
        ])

    def test_an_error_message_is_redacted(self):
        sink = _Sink()
        proto.ProtocolWriter(sink).fail(
            1, proto.SERVER_UNREACHABLE, "GET http://x/Items?api_key=abc123def")
        self.assertNotIn(b"abc123def", bytes(sink.data))


_ISOLATION_CHILD = r"""
import os, subprocess, sys
sys.path.insert(0, %r)
from jellyfin_mpv_shim.bridge_protocol import ProtocolWriter, isolate_stdout
out = isolate_stdout()
print("stray print")
sys.stdout.flush()
os.write(1, b"stray fd1\n")
if os.name == "nt":
    import ctypes
    from ctypes import wintypes
    k = ctypes.WinDLL("kernel32")
    k.GetStdHandle.argtypes = [wintypes.DWORD]
    k.GetStdHandle.restype = wintypes.HANDLE
    k.WriteFile.argtypes = [wintypes.HANDLE, ctypes.c_char_p, wintypes.DWORD,
                            ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
    h = k.GetStdHandle(wintypes.DWORD(-11 & 0xFFFFFFFF))
    n = wintypes.DWORD()
    k.WriteFile(h, b"stray native\n", 13, ctypes.byref(n), None)
subprocess.run([sys.executable, "-c", "print('stray child')"])
subprocess.run([sys.executable, "-c", "print('stray child2')"],
               stderr=subprocess.DEVNULL)
ProtocolWriter(out).event("ok", {"x": 1})
"""


class StdoutIsolationTest(unittest.TestCase):
    """BRIDGE.md section 1: nothing but protocol on stdout, whoever writes."""

    def test_only_the_protocol_reaches_stdout(self):
        done = subprocess.run(
            [sys.executable, "-I", "-c", _ISOLATION_CHILD % ROOT],
            capture_output=True, timeout=60)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout, b'{"event":"ok","data":{"x":1}}\n')
        for stray in (b"stray print", b"stray fd1"):
            self.assertIn(stray, done.stderr)
        if os.name == "nt":
            self.assertIn(b"stray native", done.stderr)


class _BadArgs:
    def bad(self, fn, *args):
        with self.assertRaises(BridgeError) as caught:
            fn(*args)
        self.assertEqual(caught.exception.code, proto.BAD_ARGS)
        return caught.exception.message


class ValidationTest(_BadArgs, unittest.TestCase):

    def test_play_defaults(self):
        self.assertEqual(proto.validate_play({"itemIds": [A, B]}), {
            "itemIds": [A, B], "startIndex": 0, "startPositionTicks": 0,
            "mediaSourceId": None, "audioStreamIndex": None,
            "subtitleStreamIndex": None})

    def test_play_accepts_the_full_shape(self):
        got = proto.validate_play({
            "itemIds": [A, B], "startIndex": 1,
            "startPositionTicks": 50, "mediaSourceId": "m",
            "audioStreamIndex": 2, "subtitleStreamIndex": -1,
            "somethingNew": True})
        self.assertEqual((got["startIndex"], got["subtitleStreamIndex"],
                          got["mediaSourceId"]), (1, -1, "m"))

    def test_play_refusals(self):
        for args in ({}, {"itemIds": []}, {"itemIds": A},
                     {"itemIds": [1]}, {"itemIds": [""]}, {"itemIds": ["a"]},
                     {"itemIds": [A], "startIndex": 1},
                     {"itemIds": [A], "startIndex": -1},
                     {"itemIds": [A], "startIndex": True},
                     {"itemIds": [A], "startPositionTicks": 1.5},
                     {"itemIds": [A], "startPositionTicks": -1},
                     {"itemIds": [A], "audioStreamIndex": -1},
                     {"itemIds": [A], "subtitleStreamIndex": -2},
                     {"itemIds": [A], "mediaSourceId": 3}):
            with self.subTest(args=args):
                self.bad(proto.validate_play, args)

    def test_scalars(self):
        self.assertEqual(proto.get_int({"v": 5}, "v", minimum=0, maximum=100),
                         5)
        self.bad(proto.get_int, {"v": 101}, "v", None, 0, 100)
        self.bad(proto.get_int, {"v": False}, "v")
        self.bad(proto.get_int, {}, "v")
        self.assertEqual(proto.get_number({"s": -2.5}, "s"), -2.5)
        for value in (True, "1", float("inf"), None):
            self.bad(proto.get_number, {"s": value}, "s")
        self.assertIs(proto.get_bool({"b": False}, "b"), False)
        for value in (0, "true", None):
            self.bad(proto.get_bool, {"b": value}, "b")


class ConnectValidationTest(_BadArgs, unittest.TestCase):
    def full(self, **changes):
        args = {
            "protocol": 1,
            "server": {"url": "https://jf.example:8920/", "id": "S",
                       "name": "Home"},
            "user": {"id": "u" * 32, "name": "alice"},
            "accessToken": "tok-123456",
            "device": {"id": "dev-1", "name": "Desk"},
            "client": {"name": "FluentFin", "version": "1.0.0"},
            "window": {"mode": "standalone"},
        }
        args.update(changes)
        return args

    def test_the_full_shape(self):
        got = proto.validate_connect(self.full())
        self.assertEqual(got["window"], {"mode": "standalone", "hwnd": None})
        server = got["server"]
        self.assertEqual(server["url"], "https://jf.example:8920")
        self.assertEqual((server["userId"], server["token"],
                          server["deviceId"], server["deviceName"],
                          server["clientName"], server["clientVersion"]),
                         ("u" * 32, "tok-123456", "dev-1", "Desk",
                          "FluentFin", "1.0.0"))

    def test_the_protocol_must_be_ours(self):
        for value in (2, 0, "1", True, None):
            with self.subTest(value=value):
                self.bad(proto.validate_connect, self.full(protocol=value))
        args = self.full()
        del args["protocol"]
        self.bad(proto.validate_connect, args)

    def test_server_part_is_all_or_nothing(self):
        for key in ("server", "user", "accessToken", "device", "client"):
            with self.subTest(missing=key):
                args = self.full()
                del args[key]
                self.bad(proto.validate_connect, args)

    def test_nested_fields(self):
        for changes in ({"server": {"url": "jf.example"}},
                        {"server": {"url": "ftp://jf.example"}},
                        {"server": {}},
                        {"user": {"name": "alice"}},
                        {"device": {"id": "d"}},
                        {"client": {"version": "1"}},
                        {"accessToken": ""},
                        {"accessToken": 5}):
            with self.subTest(changes=changes):
                self.bad(proto.validate_connect, self.full(**changes))

    def test_the_message_names_the_nested_key(self):
        message = self.bad(proto.validate_connect,
                           self.full(device={"id": "d"}))
        self.assertIn("device.name", message)

    def test_window_only_is_dev_only(self):
        args = {"protocol": 1, "window": {"mode": "standalone"}}
        self.bad(proto.validate_connect, args)
        got = proto.validate_connect(args, dev=True)
        self.assertEqual(got, {"window": {"mode": "standalone", "hwnd": None},
                               "server": None, "prewarm": False})

    def test_prewarm_is_an_optional_bool(self):
        """BRIDGE.md section 4: absent or null is false; anything but a
        bool is refused, so a UI's typo never silently means "build the
        video output now" (or "do not")."""
        self.assertIs(proto.validate_connect(self.full())["prewarm"], False)
        self.assertIs(proto.validate_connect(
            self.full(prewarm=None))["prewarm"], False)
        for value in (True, False):
            with self.subTest(value=value):
                self.assertIs(proto.validate_connect(
                    self.full(prewarm=value))["prewarm"], value)
        self.assertIs(proto.validate_connect(
            {"protocol": 1, "window": {"mode": "standalone"},
             "prewarm": True}, dev=True)["prewarm"], True)
        for value in ("yes", "true", 1, 0, [], {}):
            with self.subTest(value=value):
                message = self.bad(proto.validate_connect,
                                   self.full(prewarm=value))
                self.assertIn("prewarm", message)

    def test_window_shapes(self):
        self.assertIsNone(proto.validate_connect(
            self.full(window=None))["window"])
        self.assertEqual(proto.validate_window(
            {"mode": "embedded", "hwnd": 1311242}),
            {"mode": "embedded", "hwnd": 1311242})
        for window in ({"mode": "embedded"}, {"mode": "embedded", "hwnd": 0},
                       {"mode": "embedded", "hwnd": "12"},
                       {"mode": "embedded", "hwnd": True},
                       {"mode": "fullscreen"}, {}, "standalone"):
            with self.subTest(window=window):
                self.bad(proto.validate_window, window)

    def test_the_handle_range_is_0_exclusive_to_2_to_the_32(self):
        """BRIDGE.md: 0 < hwnd <= 4294967295. mpv reads a negative or
        out-of-range wid as "no window" and opens its own, so a
        sign-extended handle is refused, not reinterpreted."""
        for value in (1, 0x80000001, 0xFFFFFFFF):
            self.assertEqual(proto.normalize_hwnd(value), value)
        for value in (0, -2, -(1 << 31), 1 << 32, 1 << 40, None, 3.0, True):
            with self.subTest(value=value):
                self.assertIsNone(proto.normalize_hwnd(value))
                self.bad(proto.validate_window,
                         {"mode": "embedded", "hwnd": value})

    def test_item_ids(self):
        self.assertEqual(proto.normalize_item_id(
            "1234ABCD-5678-90ef-ABCD-1234567890EF"),
            "1234abcd567890efabcd1234567890ef")
        self.assertEqual(proto.normalize_item_id(A), A)
        for value in ("a", A + "0", A.replace("a", "g"), None, 5, ""):
            with self.subTest(value=value):
                self.assertIsNone(proto.normalize_item_id(value))
        self.assertIsNone(proto.item_id_out(None))
        self.assertEqual(proto.item_id_out(A.upper()), A)


class RedactionTest(unittest.TestCase):
    TOKEN = "f3a9c0de11223344556677889900aabb"

    def setUp(self):
        proto.add_secret(self.TOKEN)
        self.addCleanup(proto._secrets.clear)

    def assertClean(self, text):
        self.assertNotIn(self.TOKEN, text)
        self.assertNotIn("other-secret", text)

    def test_every_spelling(self):
        lines = [
            '{"accessToken": "other-secret"}',
            "{'AccessToken': 'other-secret'}",
            "headers {'X-Emby-Token': 'other-secret', 'Accept': 'json'}",
            "X-Emby-Token: other-secret",
            "x-mediabrowser-token=other-secret&x=1",
            "GET /Videos/1/stream?api_key=other-secret&Static=true",
            "GET /Videos/1/stream?ApiKey=other-secret",
            'Authorization: MediaBrowser Client="FluentFin", '
            'Token="other-secret"',
            '{"Authorization": "MediaBrowser Device=\\"d\\", '
            'Token=\\"other-secret\\""}',
            "{'X-Emby-Authorization': 'MediaBrowser Token=\"other-secret\"'}",
            'mpv: Token=\\"other-secret\\"',
            "the literal %s anywhere" % self.TOKEN,
            "url-encoded " + self.TOKEN,
        ]
        for line in lines:
            with self.subTest(line=line):
                self.assertClean(proto.redact(line))

    def test_whole_authorization_value_goes(self):
        """BRIDGE.md: Authorization *values* -- the device and client are
        not secret, but the line is one value and goes as one."""
        text = proto.redact("Authorization: MediaBrowser Client=\"FluentFin\"")
        self.assertEqual(text, "Authorization: REDACTED")

    def test_ordinary_text_survives(self):
        text = "Playing item 1234 at 00:12:00 (token bucket full)"
        self.assertEqual(proto.redact(text), text)

    def test_short_values_are_not_registered(self):
        proto.add_secret("abc")
        self.assertEqual(proto.redact("abc abc"), "abc abc")

    def test_the_formatter_covers_arguments_and_tracebacks(self):
        stream = io.StringIO()
        handler = logging.StreamHandler(stream)
        handler.setFormatter(proto.RedactingFormatter("%(message)s"))
        logger = logging.getLogger("test_bridge_redaction")
        logger.propagate = False
        logger.addHandler(handler)
        self.addCleanup(logger.removeHandler, handler)
        logger.warning("connecting with %s", {"accessToken": self.TOKEN})
        logger.warning("headers %s", {"X-Emby-Token": "other-secret"})
        try:
            raise RuntimeError("request failed for token " + self.TOKEN)
        except RuntimeError:
            logger.exception("boom")
        text = stream.getvalue()
        self.assertClean(text)
        self.assertIn("REDACTED", text)
        self.assertIn("RuntimeError", text)


class TicksAndTracksTest(unittest.TestCase):
    def test_ticks(self):
        self.assertEqual(proto.seconds_to_ticks(1.5), 15_000_000)
        self.assertEqual(proto.seconds_to_ticks(0), 0)
        self.assertIsNone(proto.seconds_to_ticks(None))
        self.assertIsNone(proto.seconds_to_ticks(float("nan")))
        self.assertEqual(proto.ticks_to_seconds(6_000_000_000), 600.0)
        for seconds in (0.0, 0.1, 59.97, 7199.123456):
            self.assertAlmostEqual(
                proto.ticks_to_seconds(proto.seconds_to_ticks(seconds)),
                seconds, places=6)

    def video(self, **kw):
        class V:
            pass
        v = V()
        v.aid, v.sid, v.is_transcode = None, None, False
        # Jellyfin index -> mpv id is *_seq; mpv id -> Jellyfin index *_uid.
        v.audio_uid = {1: 1, 2: 4}
        v.subtitle_uid = {1: 5, 2: 7}
        v.subtitle_url = {9: "http://jf/Videos/x/Subtitles/9/Stream.srt"}
        v.subtitle_enc = {11}
        for key, value in kw.items():
            setattr(v, key, value)
        return v

    def test_audio_follows_mpv_through_the_shims_own_map(self):
        v = self.video(aid=1)
        self.assertEqual(proto.audio_index_from_mpv(v, 2), 4)
        self.assertEqual(proto.audio_index_from_mpv(v, "2"), 4)
        self.assertEqual(proto.audio_index_from_mpv(v, False), 1)
        self.assertEqual(proto.audio_index_from_mpv(v, "auto"), 1)
        self.assertEqual(proto.audio_index_from_mpv(v, 7), 1)
        self.assertIsNone(proto.audio_index_from_mpv(None, 2))

    def test_a_transcode_reports_what_was_negotiated(self):
        v = self.video(aid=4, is_transcode=True)
        self.assertEqual(proto.audio_index_from_mpv(v, 1), 4)

    def test_subtitles(self):
        v = self.video(sid=5)
        self.assertEqual(proto.subtitle_index_from_mpv(v, 2), 7)
        self.assertEqual(proto.subtitle_index_from_mpv(v, False), -1)
        self.assertEqual(proto.subtitle_index_from_mpv(v, "no"), -1)
        # A sidecar the shim added: index -> mpv id in external_subtitles.
        self.assertEqual(proto.subtitle_index_from_mpv(v, 3, {9: 3}), 9)
        # Loading: selected, no mpv track yet -- mpv shows nothing meanwhile.
        loading = self.video(sid=9)
        self.assertEqual(proto.subtitle_index_from_mpv(loading, False, {}), 9)
        # Burned in by the server: mpv has no track for it at all.
        burned = self.video(sid=11)
        self.assertEqual(proto.subtitle_index_from_mpv(burned, False), 11)
        # Off as a decision.
        self.assertEqual(proto.subtitle_index_from_mpv(self.video(sid=-1),
                                                       False), -1)
        self.assertIsNone(proto.subtitle_index_from_mpv(None, 1))

    def test_play_methods(self):
        v = self.video(play_method="Remux", playback_info={})
        self.assertEqual(proto.play_method_name(v), "DirectStream")
        for shim, wire in (("DirectPlay", "DirectPlay"),
                           ("DirectStream", "DirectStream"),
                           ("Transcode", "Transcode")):
            self.assertEqual(proto.play_method_name(
                self.video(play_method=shim)), wire)
        self.assertEqual(proto.play_method_name(
            self.video(play_method=None, is_transcode=True)), "Transcode")
        self.assertIsNone(proto.play_method_name(
            self.video(play_method=None, playback_info=None)))

    def test_tracks_payload(self):
        source = {"MediaStreams": [
            {"Index": 0, "Type": "Video", "Codec": "h264"},
            {"Index": 1, "Type": "Audio", "Language": "eng", "Codec": "aac",
             "Channels": 2, "IsDefault": True, "DisplayTitle": "English"},
            {"Index": 2, "Type": "Audio", "Codec": "ac3", "Channels": 6},
            {"Index": 3, "Type": "Subtitle", "Language": "fre",
             "Codec": "subrip", "IsForced": True, "IsExternal": True},
            {"Index": 4, "Type": "Subtitle", "Title": "Signs"},
        ]}
        self.assertEqual(proto.tracks_payload(source), {
            "audio": [
                {"index": 1, "isDefault": True, "isExternal": False,
                 "language": "eng", "title": "English", "codec": "aac",
                 "channels": 2},
                {"index": 2, "isDefault": False, "isExternal": False,
                 "codec": "ac3", "channels": 6},
            ],
            "subtitle": [
                {"index": 3, "isDefault": False, "isExternal": True,
                 "language": "fre", "codec": "subrip", "isForced": True},
                {"index": 4, "isDefault": False, "isExternal": False,
                 "title": "Signs"},
            ]})
        self.assertEqual(proto.tracks_payload(None),
                         {"audio": [], "subtitle": []})

    def test_item_payload(self):
        self.assertEqual(proto.item_payload("e1", {
            "Name": "Pilot", "Type": "Episode", "SeriesName": "Show",
            "ParentIndexNumber": 1, "IndexNumber": 2,
            "RunTimeTicks": 12_000_000_000}), {
            "itemId": "e1", "name": "Pilot", "type": "Episode",
            "seriesName": "Show", "seasonNumber": 1, "episodeNumber": 2,
            "runTimeTicks": 12_000_000_000})
        # A music video's ordinals are disc and track, not S/E.
        self.assertEqual(proto.item_payload("m1", {
            "Name": "Song", "Type": "MusicVideo", "IndexNumber": 3}),
            {"itemId": "m1", "name": "Song", "type": "MusicVideo"})


if __name__ == "__main__":
    unittest.main()
