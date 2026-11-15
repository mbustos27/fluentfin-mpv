"""Tests of the fake Jellyfin server, its library, artwork and Matroska muxer. Standard library only, no mpv needed
(a synthetic media folder stands in for media.py's build):

    python -B -m unittest discover -s tools/ui-harness/fakeserver -p "test_*.py"
"""

import base64
import json
import os
import shutil
import socket
import struct
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import imagegen  # noqa: E402
import library  # noqa: E402
import media  # noqa: E402
import server  # noqa: E402

CREDS = server.load_credentials(os.path.join(HERE, "credentials.json"))
AUTH = 'MediaBrowser Client="FluentFin", Device="test", DeviceId="t1", Version="1.0"'


def make_media(folder, seconds=600):
    os.makedirs(os.path.join(folder, "trickplay"), exist_ok=True)
    video = os.path.join(folder, "test.mkv")
    with open(video, "wb") as f:
        f.write(bytes(range(256)) * 4096)          # 1 MiB with a known pattern
    with open(os.path.join(folder, "test.fr.srt"), "w", encoding="utf-8") as f:
        f.write(media.srt(media.subtitle_cues("[Francais]", seconds, every=6.0)))
    with open(os.path.join(folder, "trickplay", "0.jpg"), "wb") as f:
        f.write(b"\xff\xd8fake jpeg\xff\xd9")
    return {"seconds": seconds, "path": video, "size": os.path.getsize(video), "duration_ms": seconds * 1000,
            "video": {"codec": "h264", "width": 1280, "height": 720, "fps": 24, "bitrate": 1500000},
            "audio": [{"codec": "aac", "language": "eng", "title": "English", "channels": 2, "bitrate": 128000},
                      {"codec": "ac3", "language": "jpn", "title": "Japanese", "channels": 2, "bitrate": 192000}],
            "subtitles": [{"codec": "subrip", "language": "eng", "title": "English"},
                          {"codec": "subrip", "language": "spa", "title": "Spanish"}],
            "external_subtitle": {"codec": "srt", "language": "fre", "title": "French",
                                  "path": os.path.join(folder, "test.fr.srt")},
            "trickplay": {"width": 320, "height": 180, "tile": 10, "interval_ms": 10000, "thumbnails": 60,
                          "sheets": 1, "dir": os.path.join(folder, "trickplay"), "ok": True}}


class ServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="ffh-fakeserver-")
        cls.meta = make_media(os.path.join(cls.tmp, "media"))
        cls.srv = server.Server(os.path.join(cls.tmp, "out"), cls.meta, CREDS,
                                rules=[(r"/Items/Filters", 300.0)])
        threading.Thread(target=cls.srv.serve, daemon=True).start()
        cls.token = cls.login(CREDS["username"], CREDS["password"])["AccessToken"]

    @classmethod
    def tearDownClass(cls):
        cls.srv.httpd.shutdown()
        cls.srv.httpd.server_close()
        cls.srv.log_file.close()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    @classmethod
    def request(cls, path, method="GET", body=None, token=None, headers=None, raw=False):
        req = urllib.request.Request(cls.srv.url + path, method=method,
                                     data=None if body is None else json.dumps(body).encode())
        auth = AUTH + (', Token="%s"' % token if token else "")
        req.add_header("Authorization", auth)
        if body is not None:
            req.add_header("Content-Type", "application/json")
        for k, v in (headers or {}).items():
            req.add_header(k, v)
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                data = r.read()
                return r.status, (data if raw else (json.loads(data) if data else None)), r.headers
        except urllib.error.HTTPError as e:
            return e.code, e.read(), e.headers

    @classmethod
    def login(cls, user, password):
        status, data, _ = cls.request("/Users/AuthenticateByName", "POST", {"Username": user, "Pw": password})
        assert status == 200, status
        return data

    def get(self, path, **kw):
        status, data, _ = self.request(path, token=self.token, **kw)
        self.assertEqual(200, status, path)
        return data

    def test_public_info_and_login(self):
        status, info, _ = self.request("/System/Info/Public")
        self.assertEqual(200, status)
        self.assertEqual(self.srv.server_id, info["Id"])
        auth = self.login(CREDS["username"], CREDS["password"])
        self.assertEqual(CREDS["username"], auth["User"]["Name"])
        self.assertEqual(32, len(auth["AccessToken"]))
        self.assertNotEqual(self.token, auth["AccessToken"], "a fresh token per login")

    def test_wrong_password_and_missing_token_are_401(self):
        status, _, _ = self.request("/Users/AuthenticateByName", "POST", {"Username": CREDS["username"], "Pw": "nope"})
        self.assertEqual(401, status)
        self.assertEqual(401, self.request("/Users/Me")[0])
        self.assertEqual(401, self.request("/Users/Me", token="0" * 32)[0])
        self.assertEqual(200, self.request("/Users/Me?api_key=" + self.token)[0])

    def test_views_items_paging_and_search(self):
        views = self.get("/UserViews?userId=" + self.srv.user_id)
        self.assertEqual(["Movies", "Shows", "Documentaries", "Anime"], [v["Name"] for v in views["Items"]])
        movies_view = views["Items"][0]["Id"]
        # the SDK sends ids in D format and arrays as repeated keys
        dashed = "%s-%s-%s-%s-%s" % (movies_view[:8], movies_view[8:12], movies_view[12:16], movies_view[16:20], movies_view[20:])
        page = self.get("/Items?parentId=%s&includeItemTypes=Movie&recursive=true&sortBy=SortName&startIndex=100&limit=50"
                        "&fields=Overview&fields=Genres" % dashed)
        self.assertEqual(320, page["TotalRecordCount"])
        self.assertEqual(50, len(page["Items"]))
        first = self.get("/Items?parentId=%s&includeItemTypes=Movie&recursive=true&limit=1" % movies_view)["Items"][0]
        self.assertEqual(library.SHOWCASE["movie"], first["Name"])
        found = self.get("/Items?searchTerm=harbor&recursive=true&includeItemTypes=Movie,Series,Person&limit=100")
        self.assertTrue(found["Items"])
        self.assertTrue(all("harbor" in i["Name"].lower() for i in found["Items"]))

    def test_home_rows(self):
        resume = self.get("/UserItems/Resume?limit=12&mediaTypes=Video")
        self.assertGreater(len(resume["Items"]), 4)
        self.assertTrue(all(i["UserData"]["PlaybackPositionTicks"] > 0 for i in resume["Items"]))
        nextup = self.get("/Shows/NextUp?limit=24&userId=" + self.srv.user_id)
        self.assertGreater(len(nextup["Items"]), 4)
        self.assertTrue(all(i["Type"] == "Episode" for i in nextup["Items"]))
        for view in self.get("/UserViews")["Items"]:
            latest = self.get("/Items/Latest?limit=16&parentId=" + view["Id"])
            self.assertEqual(16, len(latest), view["Name"])

    def test_series_seasons_episodes(self):
        fx = self.get("/__harness/fixture")
        series = fx["series"]["id"]
        seasons = self.get("/Items?parentId=" + series)
        self.assertEqual(["Season 1", "Season 2", "Season 3"], [s["Name"] for s in seasons["Items"]])
        self.assertEqual(seasons["Items"], self.get("/Shows/%s/Seasons" % series)["Items"])
        episodes = self.get("/Items?parentId=" + fx["season"]["id"])
        self.assertEqual(fx["episode"]["name"], episodes["Items"][0]["Name"])
        self.assertEqual(8, len(self.get("/Shows/%s/Episodes?seasonId=%s" % (series, fx["season"]["id"]))["Items"]))
        self.assertEqual(24, self.get("/Shows/%s/Episodes?isMissing=false" % series)["TotalRecordCount"])
        nextup = self.get("/Shows/NextUp?seriesId=" + series)["Items"]
        self.assertEqual((1, 4), (nextup[0]["ParentIndexNumber"], nextup[0]["IndexNumber"]))

    def test_item_detail_streams_and_playbackinfo(self):
        fx = self.get("/__harness/fixture")
        item = self.get("/Items/%s?userId=%s" % (fx["episode"]["id"], self.srv.user_id))
        streams = item["MediaSources"][0]["MediaStreams"]
        self.assertEqual(["Video", "Audio", "Audio", "Subtitle", "Subtitle", "Subtitle"], [s["Type"] for s in streams])
        self.assertEqual(list(range(6)), [s["Index"] for s in streams])
        self.assertTrue(streams[5]["IsExternal"])
        self.assertIn(item["Id"], item["Trickplay"])
        self.assertEqual(6, len(item["Chapters"]))
        a = self.get("/Items/%s/PlaybackInfo" % item["Id"], method="POST",
                     body={"UserId": self.srv.user_id, "AudioStreamIndex": 2, "SubtitleStreamIndex": 4})
        b = self.get("/Items/%s/PlaybackInfo" % item["Id"], method="POST", body={})
        self.assertNotEqual(a["PlaySessionId"], b["PlaySessionId"])
        self.assertEqual((2, 4), (a["MediaSources"][0]["DefaultAudioStreamIndex"], a["MediaSources"][0]["DefaultSubtitleStreamIndex"]))
        self.assertEqual((1, -1), (b["MediaSources"][0]["DefaultAudioStreamIndex"], b["MediaSources"][0]["DefaultSubtitleStreamIndex"]))
        srt = self.request(streams[5]["DeliveryUrl"], raw=True)
        self.assertEqual(200, srt[0])
        self.assertIn(b"[Francais] 1 - 00:01", srt[1])
        segments = self.get("/MediaSegments/" + item["Id"])["Items"]
        self.assertEqual(["Intro", "Outro"], [s["Type"] for s in segments])
        self.assertEqual(200, self.request("/Videos/%s/Trickplay/320/0.jpg" % item["Id"], raw=True)[0])

    def test_stream_serves_ranges(self):
        fx = self.get("/__harness/fixture")
        status, data, headers = self.request("/Videos/%s/stream?static=true" % fx["movie"]["id"], raw=True,
                                             headers={"Range": "bytes=1000-1999"})
        self.assertEqual(206, status)
        self.assertEqual(bytes((1000 + i) % 256 for i in range(1000)), data)
        self.assertEqual("bytes 1000-1999/%d" % self.meta["size"], headers["Content-Range"])
        status, data, headers = self.request("/Videos/%s/stream.mkv" % fx["movie"]["id"], method="HEAD", raw=True)
        self.assertEqual((200, b"", str(self.meta["size"])), (status, data, headers["Content-Length"]))

    def test_images_have_real_sizes_and_are_resized(self):
        fx = self.get("/__harness/fixture")
        movie = fx["movie"]["id"]
        status, data, headers = self.request("/Items/%s/Images/Primary?fillHeight=300&tag=x" % movie, raw=True)
        self.assertEqual((200, "image/png"), (status, headers["Content-Type"]))
        self.assertEqual((200, 300), imagegen.png_size(data))
        self.assertEqual((1920, 1080), imagegen.png_size(self.request("/Items/%s/Images/Backdrop" % movie, raw=True)[1]))
        self.assertEqual((480, 270), imagegen.png_size(self.request("/Items/%s/Images/Backdrop/0?maxWidth=480" % movie, raw=True)[1]))
        self.assertEqual(404, self.request("/Items/%s/Images/Primary" % ("0" * 32), raw=True)[0])

    def test_latency_rule_only_where_it_matches(self):
        t = time.perf_counter()
        self.get("/Items/Filters?parentId=" + self.get("/UserViews")["Items"][0]["Id"])
        self.assertGreaterEqual(time.perf_counter() - t, 0.29)
        t = time.perf_counter()
        self.get("/System/Endpoint")
        self.assertLess(time.perf_counter() - t, 0.25)

    def test_reports_update_user_data_and_reach_the_websocket(self):
        fx = self.get("/__harness/fixture")
        movie = fx["movie"]["id"]
        sock = socket.create_connection(("127.0.0.1", self.srv.port), timeout=10)
        key = base64.b64encode(os.urandom(16)).decode()
        sock.sendall(("GET /socket?api_key=%s&deviceId=t1 HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\n"
                      "Connection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n"
                      % (self.token, key)).encode())
        reader = sock.makefile("rb")
        self.assertIn(b" 101 ", reader.readline())
        while reader.readline() not in (b"\r\n", b""):
            pass

        def frame():
            b1, b2 = reader.read(2)
            n = b2 & 0x7F
            if n == 126:
                n = struct.unpack(">H", reader.read(2))[0]
            elif n == 127:
                n = struct.unpack(">Q", reader.read(8))[0]
            return json.loads(reader.read(n))
        self.assertEqual("ForceKeepAlive", frame()["MessageType"])
        runtime = self.srv.lib.runtime
        self.request("/Sessions/Playing", "POST", {"ItemId": movie, "PositionTicks": 0, "PlaySessionId": "p1"}, token=self.token)
        self.assertEqual("UserDataChanged", frame()["MessageType"])
        self.request("/Sessions/Playing/Stopped", "POST", {"ItemId": movie, "PositionTicks": runtime // 3,
                                                           "PlaySessionId": "p1"}, token=self.token)
        self.assertEqual(runtime // 3, frame()["Data"]["UserDataList"][0]["PlaybackPositionTicks"])
        self.assertEqual(runtime // 3, self.get("/Items/" + movie)["UserData"]["PlaybackPositionTicks"])
        self.request("/Sessions/Playing/Stopped", "POST", {"ItemId": movie, "PositionTicks": runtime - 10 * library.TICKS,
                                                           "PlaySessionId": "p2"}, token=self.token)
        data = frame()["Data"]["UserDataList"][0]
        self.assertTrue(data["Played"])
        self.assertEqual(0, data["PlaybackPositionTicks"])
        sock.close()
        # put the showcase movie back the way the fixture has it
        self.request("/UserPlayedItems/" + movie, "DELETE", token=self.token)

    def test_unknown_endpoint_is_404_and_listed(self):
        self.assertEqual(404, self.request("/No/Such/Thing", token=self.token)[0])
        self.assertIn("GET /No/Such/Thing", self.get("/__harness/summary")["unhandled"])

    def test_log_never_holds_the_token_or_password(self):
        self.request("/Users/Me?api_key=" + self.token)
        self.request("/Users/AuthenticateByName", "POST", {"Username": CREDS["username"], "Pw": CREDS["password"]})
        self.get("/__harness/summary")
        with open(os.path.join(self.srv.out, "requests.jsonl"), encoding="utf-8") as f:
            text = f.read()
        self.assertIn("/Users/Me", text)
        self.assertNotIn(self.token, text)
        self.assertNotIn(CREDS["password"], text)
        with open(os.path.join(self.srv.out, "summary.json"), encoding="utf-8") as f:
            summary = f.read()
        self.assertNotIn(self.token, summary)


class MuxTests(unittest.TestCase):
    def test_mux_round_trip(self):
        tmp = tempfile.mkdtemp(prefix="ffh-mux-")
        try:
            def source(path, codec, track_type, n):
                entry = media.el(media.TRACKENTRY, media.el_uint(media.TRACKNUMBER, 1) + media.el_uint(media.TRACKTYPE, track_type)
                                 + media.el_str(media.CODECID, codec))
                clusters = b""
                for c in range(2):
                    body = media.el_uint(media.TIMESTAMP, c * 2000)
                    for k in range(n):
                        body += media.el(media.SIMPLEBLOCK, b"\x81" + struct.pack(">h", k * 100)
                                         + bytes([0x80 if k == 0 else 0]) + bytes([c, k]) * 10)
                    clusters += media.el(media.CLUSTER, body)
                with open(path, "wb") as f:
                    f.write(media.el(media.EBML, b"") + media.el(
                        media.SEGMENT, media.el(media.INFO, media.el_uint(media.TIMESCALE, 1000000))
                        + media.el(media.TRACKS, entry) + clusters))
            v, a = os.path.join(tmp, "v.mkv"), os.path.join(tmp, "a.mka")
            source(v, "V_MPEG4/ISO/AVC", 1, 5)
            source(a, "A_AAC", 2, 8)
            out = os.path.join(tmp, "out.mkv")
            end = media.mux(out, [(v, 1, {"language": "und", "name": "V", "default": True}),
                                  (a, 1, {"language": "eng", "name": "English", "default": True}),
                                  (a, 1, {"language": "jpn", "name": "Japanese"})],
                            [({"language": "eng", "name": "English"}, [(100, 900, "hello")]),
                             ({"language": "spa", "name": "Spanish"}, [(200, 800, "hola")])], "t")
            tracks, blocks = media.read_mkv(out)
            self.assertEqual([1, 2, 3, 4, 5], sorted(tracks))
            by_track = {}
            for b in blocks:
                by_track.setdefault(b["track"], []).append(b)
            self.assertEqual([10, 16, 16, 1, 1], [len(by_track[t]) for t in range(1, 6)])
            self.assertEqual(b"hola", by_track[5][0]["data"])
            self.assertEqual((200, 600), (by_track[5][0]["time"], by_track[5][0]["duration"]))
            self.assertEqual([0, 100, 200, 300, 400, 2000, 2100, 2200, 2300, 2400], [b["time"] for b in by_track[1]])
            self.assertEqual(2700, end)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class ImageTests(unittest.TestCase):
    def test_blurhash_is_valid_length(self):
        h = imagegen.blurhash("x")
        self.assertEqual(6 + 2 * 2, len(h))     # 1x3 components: flag, max, DC (4), 2 AC (2 each)


if __name__ == "__main__":
    unittest.main()
