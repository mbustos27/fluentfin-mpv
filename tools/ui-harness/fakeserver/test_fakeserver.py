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
import zlib

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

    def test_the_release_answer_stands_in_for_github_and_its_version_can_be_set(self):
        # Nixie 1.3.1: the app's update check asks this in a harness run (NIXIE_UPDATE_API), never GitHub
        latest = self.get("/__harness/release/latest")
        self.assertEqual("v0.0.1", latest["tag_name"])
        self.assertFalse(latest["draft"] or latest["prerelease"])
        self.assertEqual(200, self.request("/__harness/release", "POST", {"version": "1.3.2"})[0])
        latest = self.get("/__harness/release/latest")
        self.assertEqual("v1.3.2", latest["tag_name"])
        self.assertEqual(["Nixie-1.3.2-win-x64.msi", "Nixie-1.3.2-win-x64.msi.sha256"], [a["name"] for a in latest["assets"]])
        self.assertTrue(all(a["browser_download_url"].startswith("https://github.com/mbustos27/fluentfin-mpv/releases/download/v1.3.2/")
                            for a in latest["assets"]))
        self.request("/__harness/release", "POST", {"version": "not a version"})
        self.assertEqual("v1.3.2", self.get("/__harness/release/latest")["tag_name"])
        self.request("/__harness/release", "POST", {"version": "0.0.1"})

    def test_the_showcase_movie_has_a_youtube_trailer_served_by_the_harness(self):
        # Nixie 1.3.1: the theatre window plays it from the fake server's page, never from YouTube
        fx = self.get("/__harness/fixture")
        movie = self.get("/Items/%s?userId=%s" % (fx["movie"]["id"], self.srv.user_id))
        self.assertEqual(["https://www.youtube.com/watch?v=" + library.TRAILER_ID], [t["Url"] for t in movie["RemoteTrailers"]])
        episode = self.get("/Items/%s?userId=%s" % (fx["episode"]["id"], self.srv.user_id))
        self.assertEqual([], episode["RemoteTrailers"])
        status, page, headers = self.request("/__harness/trailer/%s?autoplay=1&rel=0&playsinline=1" % library.TRAILER_ID, raw=True)
        self.assertEqual(200, status)
        self.assertTrue(headers["Content-Type"].startswith("text/html"))
        self.assertIn(b"HARNESS TRAILER", page)
        self.assertNotIn(b"<script", page)
        self.assertNotIn(b"http", page.split(b"<style>")[1])
        status, page, _ = self.request("/__harness/trailer/%3Cb%3Ex", raw=True)
        self.assertNotIn(b"<b>x", page)

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

    def test_flat_art_is_one_colour_and_changes_the_image_tags(self):
        # Nixie 1.4.0 (the contrast floor): POST /__harness/art makes every image white or black, the worst case under a
        # label; the tags change with it (a client reading the item again asks for the new picture); "" puts the art back
        def first_pixels(data):
            pos, idat = 8, b""
            while pos < len(data):
                n, kind = struct.unpack(">I4s", data[pos:pos + 8])
                if kind == b"IDAT":
                    idat += data[pos + 8:pos + 8 + n]
                pos += 12 + n
            raw = zlib.decompress(idat)
            width = imagegen.png_size(data)[0]
            return set(raw[1 + 3 * x:4 + 3 * x] for x in range(0, width, 37))
        movie = self.get("/__harness/fixture")["movie"]["id"]
        def tag():
            return self.get("/Items/%s?userId=%s" % (movie, self.srv.user_id))["ImageTags"]["Primary"]
        own = tag()
        try:
            for art, rgb in (("white", b"\xff\xff\xff"), ("black", b"\x00\x00\x00")):
                status, body, _ = self.request("/__harness/art", "POST", {"art": art})
                self.assertEqual((200, art), (status, body["art"]))
                now = tag()
                self.assertNotEqual(own, now)
                for kind in ("Primary", "Backdrop", "Thumb"):
                    status, data, _ = self.request("/Items/%s/Images/%s?maxWidth=300&tag=%s" % (movie, kind, now), raw=True)
                    self.assertEqual(200, status)
                    self.assertEqual({rgb}, first_pixels(data), kind)
            self.assertEqual(400, self.request("/__harness/art", "POST", {"art": "grey"})[0])
        finally:
            self.request("/__harness/art", "POST", {"art": ""})
        self.assertEqual(own, tag())
        status, data, _ = self.request("/Items/%s/Images/Primary?maxWidth=300" % movie, raw=True)
        self.assertTrue(first_pixels(data).isdisjoint({b"\xff\xff\xff", b"\x00\x00\x00"}))   # the item's own gradient again

    def test_latency_rule_only_where_it_matches(self):
        t = time.perf_counter()
        self.get("/Items/Filters?parentId=" + self.get("/UserViews")["Items"][0]["Id"])
        self.assertGreaterEqual(time.perf_counter() - t, 0.29)
        t = time.perf_counter()
        self.get("/System/Endpoint")
        self.assertLess(time.perf_counter() - t, 0.25)

    def test_fault_rules_fail_or_empty_only_where_they_match(self):
        views = self.get("/UserViews")["Items"]
        rules = [["/Shows/NextUp", "status:503"], ["/Items/Latest", "empty"], ["/UserItems/Resume", "Empty"],
                 ["/Items/Filters", 300]]
        self.assertEqual(200, self.request("/__harness/latency", "POST", {"rules": rules})[0])
        try:
            self.assertEqual(503, self.request("/Shows/NextUp?limit=24", token=self.token)[0])
            self.assertEqual([], self.get("/Items/Latest?limit=16&parentId=" + views[0]["Id"]))
            self.assertEqual({"Items": [], "TotalRecordCount": 0, "StartIndex": 0}, self.get("/UserItems/Resume?limit=12"))
            self.assertEqual(len(views), len(self.get("/UserViews")["Items"]), "a path no rule matches answers as before")
        finally:
            self.request("/__harness/latency", "POST", {"rules": [["/Items/Filters", 300]]})
        self.assertGreater(len(self.get("/Shows/NextUp?limit=24")["Items"]), 4)
        self.assertEqual(16, len(self.get("/Items/Latest?limit=16&parentId=" + views[0]["Id"])))
        self.assertEqual("status:500", server.rule_value(" Status:500 "))
        self.assertEqual(250.0, server.rule_value("250"))
        self.assertEqual("status:400", server.rule_value("status:400"))
        self.assertEqual("status:599", server.rule_value("status:599"))
        # the injected answer has a JSON body: never a 1xx, 204 or 304, nor a success
        for bad in ("status:abc", "status:42", "status:", "sometimes", "status:200", "status:204", "status:304",
                    "status:399", "status:600"):
            with self.assertRaises(ValueError, msg=bad):
                server.rule_value(bad)

    def test_encode_subtitle_is_burnt_in_only_while_asked(self):
        item = self.get("/__harness/fixture")["episode"]["id"]

        def methods():
            info = self.get("/Items/%s/PlaybackInfo" % item, method="POST", body={})
            return [s.get("DeliveryMethod") for s in info["MediaSources"][0]["MediaStreams"] if s["Type"] == "Subtitle"]

        self.assertEqual(["Embed", "Embed", "External"], methods())
        status, answer, _ = self.request("/__harness/streams", "POST", {"encode_subtitle": True})
        self.assertEqual((200, {"encode_subtitle": True}), (status, answer))
        try:
            self.assertEqual(["Embed", "Encode", "External"], methods())
            detail = self.get("/Items/%s?userId=%s" % (item, self.srv.user_id))["MediaSources"][0]["MediaStreams"]
            self.assertEqual("Encode", detail[4]["DeliveryMethod"], "the item page's streams say so too")
        finally:
            self.request("/__harness/streams", "POST", {"encode_subtitle": False})
        self.assertEqual(["Embed", "Embed", "External"], methods())

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

    def test_quick_connect_signs_in_without_a_password_once_the_harness_approves(self):
        self.assertIs(True, self.request("/QuickConnect/Enabled")[1])
        status, qc, _ = self.request("/QuickConnect/Initiate", "POST")
        self.assertEqual(200, status)
        self.assertRegex(qc["Code"], r"^\d{6}$")
        self.assertFalse(qc["Authenticated"])
        secret = qc["Secret"]
        # not approved yet: polling says so, signing in fails
        self.assertFalse(self.request("/QuickConnect/Connect?secret=" + secret)[1]["Authenticated"])
        self.assertEqual(401, self.request("/Users/AuthenticateWithQuickConnect", "POST", {"Secret": secret})[0])
        self.assertEqual(404, self.request("/QuickConnect/Connect?secret=" + "0" * 64)[0])
        # the harness sees the code (never the secret) and approves it
        waiting = self.get("/__harness/quickconnect")
        self.assertIn(qc["Code"], [w["code"] for w in waiting])
        self.assertNotIn(secret, json.dumps(waiting))
        self.assertEqual(404, self.request("/__harness/quickconnect/approve", "POST", {"code": "x"})[0])
        self.assertEqual(200, self.request("/__harness/quickconnect/approve", "POST", {"code": qc["Code"]})[0])
        self.assertTrue(self.request("/QuickConnect/Connect?secret=" + secret)[1]["Authenticated"])
        status, auth, _ = self.request("/Users/AuthenticateWithQuickConnect", "POST", {"Secret": secret})
        self.assertEqual(200, status)
        self.assertEqual(CREDS["username"], auth["User"]["Name"])
        token = auth["AccessToken"]
        self.assertEqual(200, self.request("/Users/Me", token=token)[0])
        # one sign-in per approved code
        self.assertEqual(401, self.request("/Users/AuthenticateWithQuickConnect", "POST", {"Secret": secret})[0])
        with open(os.path.join(self.srv.out, "requests.jsonl"), encoding="utf-8") as f:
            text = f.read()
        self.assertIn("/QuickConnect/Connect", text)
        self.assertNotIn(secret, text)
        self.assertNotIn(token, text)

    def test_quick_connect_authorize_approves_another_devices_code_for_the_signed_in_user(self):
        # Seaglass 1.1.4: Settings > Other devices > Quick Connect, the signed-in app approving a code
        status, qc, _ = self.request("/QuickConnect/Initiate", "POST")
        self.assertEqual(200, status)
        code, secret = qc["Code"], qc["Secret"]
        # signed out: refused, the code stays waiting
        self.assertEqual(401, self.request("/QuickConnect/Authorize?code=" + code, "POST")[0])
        self.assertFalse(self.request("/QuickConnect/Connect?secret=" + secret)[1]["Authenticated"])
        # a code that is not waiting
        self.assertEqual(404, self.request("/QuickConnect/Authorize?code=x", "POST", token=self.token)[0])
        status, ok, _ = self.request("/QuickConnect/Authorize?code=%s&userId=%s" % (code, self.srv.user_id), "POST", token=self.token)
        self.assertEqual(200, status)
        self.assertIs(True, ok)
        self.assertTrue(self.request("/QuickConnect/Connect?secret=" + secret)[1]["Authenticated"])
        status, auth, _ = self.request("/Users/AuthenticateWithQuickConnect", "POST", {"Secret": secret})
        self.assertEqual(200, status)
        self.assertEqual(CREDS["username"], auth["User"]["Name"])

    def test_harness_renames_a_library_and_the_views_say_so(self):
        # the top bar's tabs follow the libraries: a long name, then back (the class shares one server)
        def names():
            return [v["Name"] for v in self.get("/UserViews")["Items"]]
        status, body, _ = self.request("/__harness/libraries", "POST", {"rename": {"Shows": "Television Shows"}})
        self.assertEqual(200, status)
        self.assertIn("Television Shows", body["views"])
        self.assertEqual(["Movies", "Television Shows", "Documentaries", "Anime"], names())
        self.request("/__harness/libraries", "POST", {"rename": {"Television Shows": "Shows"}})
        self.assertEqual(["Movies", "Shows", "Documentaries", "Anime"], names())

    def test_user_data_endpoint_and_absent_plugin(self):
        movie = self.get("/__harness/fixture")["movie"]["id"]
        data = self.get("/UserItems/%s/UserData" % movie)
        self.assertIn("Played", data)
        was = data["IsFavorite"]
        status, after, _ = self.request("/UserItems/%s/UserData" % movie, "POST", {"IsFavorite": not was}, token=self.token)
        self.assertEqual((200, not was), (status, after["IsFavorite"]))
        self.request("/UserItems/%s/UserData" % movie, "POST", {"IsFavorite": was}, token=self.token)
        # the Playback Reporting plugin is not installed: 404, and not a gap of the fake server
        self.assertEqual(404, self.request("/user_usage_stats/submit_custom_query", "POST", {}, token=self.token)[0])
        unhandled = self.get("/__harness/summary")["unhandled"]
        self.assertFalse([k for k in unhandled if "user_usage_stats" in k or "UserData" in k], unhandled)

    def test_directory_contents_is_a_small_folder_tree(self):
        # the dashboard's folder picker: / > media > one folder per library > A, B, C (empty)
        self.assertEqual(["media"], [e["Name"] for e in self.get("/Environment/DirectoryContents?path=/&includeDirectories=true")])
        media = self.get("/Environment/DirectoryContents?path=/media&includeDirectories=true")
        self.assertEqual(["movies", "shows", "documentaries", "anime"], [e["Name"] for e in media])
        self.assertEqual("/media/movies", media[0]["Path"])
        self.assertEqual("Directory", media[0]["Type"])
        self.assertEqual(["/media/movies/A", "/media/movies/B", "/media/movies/C"],
                         [e["Path"] for e in self.get("/Environment/DirectoryContents?path=/media/movies")])
        self.assertEqual([], self.get("/Environment/DirectoryContents?path=/media/movies/A"))
        self.assertEqual(401, self.request("/Environment/DirectoryContents?path=/")[0])

    def test_user_data_resume_point_takes_an_item_out_of_continue_watching_and_back(self):
        # Seaglass 1.1.4: Remove from Continue Watching clears the resume point (POST UserData), Undo sets it again
        resume = self.get("/UserItems/Resume?Limit=50")["Items"]
        item = resume[0]
        was = item["UserData"]["PlaybackPositionTicks"]
        status, after, _ = self.request("/UserItems/%s/UserData" % item["Id"], "POST", {"PlaybackPositionTicks": 0}, token=self.token)
        self.assertEqual((200, 0), (status, after["PlaybackPositionTicks"]))
        self.assertNotIn(item["Id"], [i["Id"] for i in self.get("/UserItems/Resume?Limit=50")["Items"]])
        self.request("/UserItems/%s/UserData" % item["Id"], "POST", {"PlaybackPositionTicks": was}, token=self.token)
        self.assertIn(item["Id"], [i["Id"] for i in self.get("/UserItems/Resume?Limit=50")["Items"]])

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


class LibraryViewsTests(unittest.TestCase):
    def test_a_library_added_is_a_fifth_view_once(self):
        lib = library.Library("server", "user", 600, seed=3)
        added = lib.add_views(["Concerts", "Concerts"])
        self.assertEqual(1, len(added))
        self.assertEqual("Concerts", lib.items[lib.views[-1]]["Name"])
        self.assertEqual(5, len(lib.views))
        self.assertEqual(5, len(lib.query({})["Items"]))


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


class SilentMediaTests(unittest.TestCase):
    """No test run may make a sound: the test video's audio tracks are digital silence, and a cached build from before
    that rule (no "silent" in media.json) is rebuilt instead of reused. mpv is not run: _run records the commands."""

    def test_audio_is_silence_and_old_builds_are_rebuilt(self):
        tmp = tempfile.mkdtemp(prefix="ffh-media-")
        calls = []

        def fake_run(cmd, log):
            calls.append(cmd)
            out = next((a[4:] for a in cmd if a.startswith("--o=")), None)
            if out:
                with open(out, "wb") as f:
                    f.write(b"\0" * 20000)
            return 0

        real = media._run
        media._run = fake_run
        try:
            old = make_media(tmp)          # an earlier build: same length, no "silent"
            with open(os.path.join(tmp, "media.json"), "w", encoding="utf-8") as f:
                json.dump(old, f)
            try:
                media.build("mpv.exe", tmp, seconds=600)
            except Exception:
                pass                       # the muxer cannot read the fake parts; the commands are what matters
            sources = [a for cmd in calls for a in cmd if a.startswith("av://lavfi:")]
            audio = [s for s in sources if not s.startswith("av://lavfi:testsrc")]
            self.assertTrue(calls, "a media.json without \"silent\" was reused")
            self.assertEqual(2, len(audio), sources)
            for s in audio:
                self.assertIn("anullsrc", s)
                self.assertNotIn("sine", s)
        finally:
            media._run = real
            shutil.rmtree(tmp, ignore_errors=True)

    def test_a_silent_build_is_reused(self):
        tmp = tempfile.mkdtemp(prefix="ffh-media-")
        real = media._run
        media._run = lambda cmd, log: self.fail("a complete silent build was rebuilt")
        try:
            meta = dict(make_media(tmp), silent=True)
            with open(os.path.join(tmp, "media.json"), "w", encoding="utf-8") as f:
                json.dump(meta, f)
            self.assertEqual(meta["path"], media.build("mpv.exe", tmp, seconds=600)["path"])
        finally:
            media._run = real
            shutil.rmtree(tmp, ignore_errors=True)


class ImageTests(unittest.TestCase):
    def test_blurhash_is_valid_length(self):
        h = imagegen.blurhash("x")
        self.assertEqual(6 + 2 * 2, len(h))     # 1x3 components: flag, max, DC (4), 2 AC (2 each)


if __name__ == "__main__":
    unittest.main()
