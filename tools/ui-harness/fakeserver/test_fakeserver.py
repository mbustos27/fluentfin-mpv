"""Tests of the fake Jellyfin server, its library, artwork and Matroska muxer. Standard library only, no mpv needed
(a synthetic media folder stands in for media.py's build):

    python -B -m unittest discover -s tools/ui-harness/fakeserver -p "test_*.py"
"""

import base64
import datetime
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
        # 1.5.1 fixes (BH-150-M2): a device of its own, so the tests' sign-ins (AUTH's DeviceId) don't end it, as on Jellyfin
        cls.token = cls.login(CREDS["username"], CREDS["password"], device="class-session")["AccessToken"]

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
    def login(cls, user, password, device=None):
        headers = {"Authorization": AUTH.replace('DeviceId="t1"', 'DeviceId="%s"' % device)} if device else None
        status, data, _ = cls.request("/Users/AuthenticateByName", "POST", {"Username": user, "Pw": password}, headers=headers)
        assert status == 200, status
        return data

    # 1.5.1 fixes (BH-150-M2): devices, as Jellyfin keeps them
    def test_a_sign_in_ends_the_same_users_older_session_on_the_same_device_only(self):
        first = self.login(CREDS["username"], CREDS["password"], device="copy-a")["AccessToken"]
        other = self.login(CREDS["username"], CREDS["password"], device="copy-b")["AccessToken"]
        self.assertEqual(200, self.request("/Users/Me", token=first)[0], "another device id leaves it alone")
        again = self.login(CREDS["username"], CREDS["password"], device="copy-a")
        self.assertEqual("copy-a", again["SessionInfo"]["DeviceId"])
        self.assertEqual(401, self.request("/Users/Me", token=first)[0], "the same device id ends it")
        self.assertEqual(200, self.request("/Users/Me", token=other)[0])
        self.assertEqual(200, self.request("/Users/Me", token=again["AccessToken"])[0])
        self.assertEqual(200, self.request("/Users/Me", token=self.token)[0])

    def test_the_harness_lists_devices_signs_in_another_copy_and_revokes(self):
        token = self.login(CREDS["username"], CREDS["password"], device="nixie-copy")["AccessToken"]
        devices = self.get("/__harness/devices")
        self.assertIn({"device": "nixie-copy", "client": "FluentFin", "user": CREDS["username"]}, devices)
        self.assertNotIn(token, json.dumps(devices))
        # another copy with its own id: nothing ends; with this one's id (what every copy sent up to Nixie 1.5.0): it ends
        self.assertEqual({"device": "a-second-copy", "ended": 0}, self.request("/__harness/signin", "POST", {"device": "a-second-copy"})[1])
        self.assertEqual(200, self.request("/Users/Me", token=token)[0])
        self.assertEqual({"device": "nixie-copy", "ended": 1}, self.request("/__harness/signin", "POST", {"device": "nixie-copy"})[1])
        self.assertEqual(401, self.request("/Users/Me", token=token)[0])
        self.assertEqual(400, self.request("/__harness/signin", "POST", {})[0])
        # one device's sessions, then all of the user's ("sign out of all devices")
        mine = self.login(CREDS["username"], CREDS["password"], device="revoked-one")["AccessToken"]
        self.assertEqual(1, self.request("/__harness/revoke", "POST", {"device": "revoked-one"})[1]["ended"])
        self.assertEqual(401, self.request("/Users/Me", token=mine)[0])
        self.assertEqual(200, self.request("/Users/Me", token=self.token)[0])
        status, answer, _ = self.request("/__harness/revoke", "POST", {})
        self.assertEqual(200, status)
        self.assertGreaterEqual(answer["ended"], 1)
        self.assertEqual(401, self.request("/Users/Me", token=self.token)[0])
        type(self).token = self.login(CREDS["username"], CREDS["password"], device="class-session")["AccessToken"]
        self.assertEqual(200, self.request("/Users/Me", token=self.token)[0])

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
        # 1.5 qacheck: strict by default, as Jellyfin 12 (AuthTests): ApiKey, not api_key
        self.assertEqual(200, self.request("/Users/Me?ApiKey=" + self.token)[0])
        self.assertEqual(401, self.request("/Users/Me?api_key=" + self.token)[0])

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

    def test_another_users_id_is_refused(self):
        # 1.6.1 (the 1.6 review): the fake accepted any userId; Jellyfin refuses another user's to a user who is not an
        # administrator, and Nixie never sends one: a request that does is a bug to see
        other = "0123456789abcdef0123456789abcdef"
        dashed = "%s-%s-%s-%s-%s" % (self.srv.user_id[:8], self.srv.user_id[8:12], self.srv.user_id[12:16], self.srv.user_id[16:20], self.srv.user_id[20:])
        self.assertEqual(403, self.request("/Items?userId=%s&recursive=true&limit=1" % other, token=self.token)[0])
        self.assertEqual(403, self.request("/UserViews?userId=" + other, token=self.token)[0])
        self.assertEqual(200, self.request("/Items?userId=%s&recursive=true&limit=1" % dashed, token=self.token)[0])
        self.assertEqual(200, self.request("/UserViews?userId=" + self.srv.user_id, token=self.token)[0])
        self.assertEqual(200, self.request("/Items?recursive=true&limit=1", token=self.token)[0])

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
        seasons = self.get("/Items?parentId=%s&fields=SortName" % series)   # 1.5 search: /Items sends SortName when asked
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
        # 1.5 qacheck: trickplay needs a token, as Jellyfin's does (live-auth.ps1)
        self.assertEqual(401, self.request("/Videos/%s/Trickplay/320/0.jpg" % item["Id"], raw=True)[0])
        self.assertEqual(200, self.request("/Videos/%s/Trickplay/320/0.jpg" % item["Id"], raw=True, token=self.token)[0])

    def test_the_release_answer_stands_in_for_github_and_its_version_can_be_set(self):
        # Nixie 1.3.1: the app's update check asks this in a harness run (NIXIE_UPDATE_API), never GitHub
        latest = self.get("/__harness/release/latest")
        self.assertEqual("v0.0.1", latest["tag_name"])
        self.assertFalse(latest["draft"] or latest["prerelease"])
        self.assertEqual(200, self.request("/__harness/release", "POST", {"version": "1.3.2"})[0])
        latest = self.get("/__harness/release/latest")
        self.assertEqual("v1.3.2", latest["tag_name"])
        self.assertEqual(["Nixie-1.3.2-win-x64.msi", "Nixie-1.3.2-win-x64.msi.sha256"], [a["name"] for a in latest["assets"]])
        self.assertTrue(all(a["browser_download_url"].startswith("https://github.com/mbustos27/nixie/releases/download/v1.3.2/")
                            for a in latest["assets"]))
        self.request("/__harness/release", "POST", {"version": "not a version"})
        self.assertEqual("v1.3.2", self.get("/__harness/release/latest")["tag_name"])
        self.request("/__harness/release", "POST", {"version": "0.0.1"})

    # ---- 1.5 updater: the fake release is as strict as GitHub (release.py)
    def _release(self, **settings):
        status, state, _ = self.request("/__harness/release", "POST", settings)
        self.assertEqual(200, status)
        return state

    def _bare(self, path, headers=None):
        """A request with exactly these headers: urllib's own User-Agent left out unless given."""
        import http.client
        host, port = self.srv.url.split("//", 1)[1].split(":")
        conn = http.client.HTTPConnection(host, int(port), timeout=10)
        conn.putrequest("GET", path, skip_accept_encoding=True)
        for k, v in (headers or {}).items():
            conn.putheader(k, v)
        conn.endheaders()
        r = conn.getresponse()
        body = r.read()
        conn.close()
        return r.status, body, r

    def test_the_release_api_refuses_a_request_without_a_user_agent_as_github_does(self):
        status, body, _ = self._bare("/__harness/release/latest")
        self.assertEqual(403, status)
        self.assertIn(b"User-Agent", body)
        status, _, r = self._bare("/__harness/release/latest", {"User-Agent": "Nixie/1.5.0"})
        self.assertEqual(200, status)
        self.assertEqual("60", r.getheader("x-ratelimit-limit"))
        self.assertEqual("core", r.getheader("x-ratelimit-resource"))
        self.assertTrue(int(r.getheader("x-ratelimit-reset")) > time.time())

    def test_the_release_api_refuses_an_api_version_github_does_not_support(self):
        """BH-150-m8: GitHub answers an unknown X-GitHub-Api-Version with a 400 naming it (live, 2026-11-20); a known one,
        or none (its default), with a 200 that says which version it used."""
        self._release(reset_limit=True)
        agent = {"User-Agent": "Nixie/1.5.0"}
        status, body, _ = self._bare("/__harness/release/latest", dict(agent, **{"X-GitHub-Api-Version": "1999-01-01"}))
        self.assertEqual(400, status)
        self.assertIn(b'\\"1999-01-01\\", is not a supported version', body)
        for asked, used in (("2022-11-28", "2022-11-28"), ("2026-03-10", "2026-03-10"), (None, "2022-11-28")):
            headers = dict(agent, **({"X-GitHub-Api-Version": asked} if asked else {}))
            status, _, r = self._bare("/__harness/release/latest", headers)
            self.assertEqual(200, status, asked)
            self.assertEqual(used, r.getheader("x-github-api-version-selected"))
            self.assertEqual(asked, self.get("/__harness/release/state")["api_version_asked"])

    def test_the_release_api_counts_down_its_hourly_limit_then_answers_403_with_none_left(self):
        try:
            self._release(limit=3, reset_limit=True)
            remaining = [self.request("/__harness/release/latest")[2]["x-ratelimit-remaining"] for _ in range(3)]
            self.assertEqual(["2", "1", "0"], remaining)
            status, _, headers = self.request("/__harness/release/latest")
            self.assertEqual(403, status)
            self.assertEqual("0", headers["x-ratelimit-remaining"])
            self._release(mode="retry-after", limit=60, reset_limit=True)
            status, _, headers = self.request("/__harness/release/latest")
            self.assertEqual((429, "90"), (status, headers["Retry-After"]))
            self._release(mode="refuse")
            status, _, headers = self.request("/__harness/release/latest")
            self.assertEqual(403, status)
            self.assertNotEqual("0", headers["x-ratelimit-remaining"])
        finally:
            self._release(mode="ok", limit=60, reset_limit=True)

    def test_a_renamed_repository_answers_through_githubs_redirect_with_the_new_name(self):
        try:
            self._release(version="1.5.1", repository="mbustos27/nixie-next", reset_limit=True)
            status, body, r = self._bare("/__harness/release/latest", {"User-Agent": "Nixie/1.5.0"})
            self.assertEqual(301, status)
            self.assertEqual("/__harness/release/repositories/4242/latest", r.getheader("Location"))
            latest = self.get("/__harness/release/latest")          # urllib follows it, as HttpClient does
            self.assertEqual("https://github.com/mbustos27/nixie-next/releases/tag/v1.5.1", latest["html_url"])
            self.assertEqual("https://api.github.com/repos/mbustos27/nixie-next/releases/1", latest["url"])
            self.assertTrue(all(a["browser_download_url"].startswith("https://github.com/mbustos27/nixie-next/releases/download/v1.5.1/")
                                for a in latest["assets"]))
        finally:
            self._release(version="0.0.1", repository="mbustos27/nixie")

    def test_the_installer_downloads_with_ranges_its_checksum_and_githubs_digest_agree(self):
        import hashlib
        try:
            self._release(version="1.5.1", size=300_000, kbps=100_000, digest="ok", mismatch=False)
            latest = self.get("/__harness/release/latest")
            msi = latest["assets"][0]
            self.assertEqual(300_000, msi["size"])
            base = "/__harness/release/download/v1.5.1/Nixie-1.5.1-win-x64.msi"
            status, whole, _ = self.request(base, raw=True)
            self.assertEqual((200, 300_000), (status, len(whole)))
            digest = hashlib.sha256(whole).hexdigest()
            self.assertEqual("sha256:" + digest, msi["digest"])
            status, sha, _ = self.request(base + ".sha256", raw=True)
            self.assertEqual("%s *Nixie-1.5.1-win-x64.msi\n" % digest, sha.decode())
            status, tail, headers = self.request(base, raw=True, headers={"Range": "bytes=100000-"})
            self.assertEqual(206, status)
            self.assertEqual("bytes 100000-299999/300000", headers["Content-Range"])
            self.assertEqual(whole[100000:], tail)
            self.assertEqual(416, self.request(base, raw=True, headers={"Range": "bytes=300000-"})[0])
            self.assertEqual(404, self.request("/__harness/release/download/v1.5.0/Nixie-1.5.0-win-x64.msi", raw=True)[0])
            # the mismatch, no digest, a wrong digest, and a cut connection
            self._release(mismatch=True, digest="none")
            self.assertNotIn(digest, self.request(base + ".sha256", raw=True)[1].decode())
            self.assertNotIn("digest", self.get("/__harness/release/latest")["assets"][0])
            self._release(digest="wrong")
            self.assertEqual("sha256:" + "0" * 64, self.get("/__harness/release/latest")["assets"][0]["digest"])
            self._release(cut_at=65536, mismatch=False, digest="ok")
            with self.assertRaises(Exception):
                self.request(base, raw=True)                         # stops after 64 KB: urllib says the read was short
            self.assertEqual(200, self.request(base, raw=True)[0])  # the cut is once
        finally:
            self._release(version="0.0.1", size=24 * 1024 * 1024, kbps=8192, digest="ok", mismatch=False, cut_at=0)

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
        self.assertIn(library.TRAILER_ID.encode(), page)
        # Nixie 1.4.0: one inline script (the IFrame Player API's messages), nothing loaded from anywhere
        self.assertEqual(1, page.count(b"<script"))
        self.assertNotIn(b"<script src", page)
        self.assertNotIn(b"http", page.split(b"<style>")[1].split(b"</style>")[0])
        self.assertIn(b'var mode = "play"', page)
        status, page, _ = self.request("/__harness/trailer/%3Cb%3Ex", raw=True)
        self.assertNotIn(b"<b>x", page)

    def test_the_trailer_stand_in_answers_as_its_mode_says_and_the_link_can_change(self):
        # Nixie 1.4.0: the harness's error and never-starts pages, and another site's link (the theatre's message)
        fx = self.get("/__harness/fixture")
        try:
            for mode in ("error150", "error153", "hang", "ad", "play"):
                self.assertEqual(mode, self.request("/__harness/trailer", "POST", {"mode": mode})[1]["mode"])
                _, page, _ = self.request("/__harness/trailer/%s" % library.TRAILER_ID, raw=True)
                self.assertIn(('var mode = "%s"' % mode).encode(), page)
                self.assertEqual(mode.startswith("error"), b"YouTube error" in page)
            # like YouTube, it answers state changes and errors only to a page that subscribed to them
            self.assertIn(b'subs[need]', page)
            self.assertIn(b'd.func === "addEventListener"', page)
            self.assertNotIn(b"infoDelivery", page)
            # slow: the page's own load waits for an image the server answers after that long
            self.assertEqual(2, self.request("/__harness/trailer", "POST", {"slow": 2})[1]["slow"])
            _, page, _ = self.request("/__harness/trailer/%s" % library.TRAILER_ID, raw=True)
            self.assertIn(b'src="/__harness/trailer-slow?s=2"', page)
            t0 = time.time()
            status, gif, _ = self.request("/__harness/trailer-slow?s=2", raw=True)
            self.assertEqual(200, status)
            self.assertGreaterEqual(time.time() - t0, 1.9)
            self.request("/__harness/trailer", "POST", {"slow": 0})
            self.assertEqual(204, self.request("/__harness/trailer-seen", raw=True)[0])
            self.request("/__harness/trailer", "POST", {"mode": "evil\"<script>"})
            _, page, _ = self.request("/__harness/trailer/%s" % library.TRAILER_ID, raw=True)
            self.assertIn(b'var mode = "play"', page)
            self.request("/__harness/trailer", "POST", {"url": "https://vimeo.com/76979871"})
            movie = self.get("/Items/%s?userId=%s" % (fx["movie"]["id"], self.srv.user_id))
            self.assertEqual(["https://vimeo.com/76979871"], [t["Url"] for t in movie["RemoteTrailers"]])
            self.request("/__harness/trailer", "POST", {"url": "javascript:alert(1)"})
            movie = self.get("/Items/%s?userId=%s" % (fx["movie"]["id"], self.srv.user_id))
            self.assertEqual(["https://www.youtube.com/watch?v=" + library.TRAILER_ID], [t["Url"] for t in movie["RemoteTrailers"]])
        finally:
            self.request("/__harness/trailer", "POST", {"mode": "play", "url": ""})

    def test_stream_serves_ranges(self):
        fx = self.get("/__harness/fixture")
        status, data, headers = self.request("/Videos/%s/stream?static=true" % fx["movie"]["id"], raw=True,
                                             headers={"Range": "bytes=1000-1999"})
        self.assertEqual(206, status)
        self.assertEqual(bytes((1000 + i) % 256 for i in range(1000)), data)
        self.assertEqual("bytes 1000-1999/%d" % self.meta["size"], headers["Content-Range"])
        status, data, headers = self.request("/Videos/%s/stream.mkv" % fx["movie"]["id"], method="HEAD", raw=True)
        self.assertEqual((200, b"", str(self.meta["size"])), (status, data, headers["Content-Length"]))

    # 1.5 season: a list sends only the image types it asks for, as Jellyfin does (Library.keep_image_types)
    def test_a_list_sends_only_the_image_types_asked_for(self):
        series = self.get("/__harness/fixture")["series"]["id"]
        def seasons(extra):
            return self.get("/Items?parentId=%s%s" % (series, extra))["Items"]
        every = seasons("")[0]
        self.assertIn("ParentLogoImageTag", every)
        self.assertIn("ParentBackdropImageTags", every)
        no_logo = seasons("&enableImageTypes=Primary,Backdrop,Banner,Thumb&imageTypeLimit=1")[0]
        self.assertNotIn("ParentLogoImageTag", no_logo)
        self.assertNotIn("ParentLogoItemId", no_logo)
        self.assertTrue(no_logo["ParentBackdropImageTags"])
        self.assertIn("Primary", no_logo["ImageTags"])
        with_logo = seasons("&enableImageTypes=Primary&enableImageTypes=Logo")[0]
        self.assertIn("ParentLogoImageTag", with_logo)
        self.assertNotIn("ParentBackdropImageTags", with_logo)
        self.assertEqual([], with_logo["BackdropImageTags"])
        none = seasons("&enableImages=false")[0]
        self.assertEqual({}, none["ImageTags"])
        self.assertNotIn("ParentLogoImageTag", none)

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
        sock.sendall(("GET /socket?ApiKey=%s&deviceId=t1 HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\n"
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

    # Nixie 1.8.1 (BH-175-q4): MinDateLastSaved, as the SDK sends it (its "+" encoded), and the harness's titles
    def test_min_date_last_saved_lists_only_what_was_saved_since_and_a_harness_title_is_saved_now(self):
        everything = "/Items?includeItemTypes=Movie&includeItemTypes=Series&recursive=true&limit=1&enableTotalRecordCount=true"
        total = self.get(everything)["TotalRecordCount"]
        self.assertGreater(total, 300)
        self.assertEqual(total, self.get(everything + "&minDateLastSaved=1970-01-01T00%3A00%3A00.0000000%2B00%3A00")["TotalRecordCount"])
        tomorrow = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=1)).strftime("%Y-%m-%dT%H%%3A%M%%3A%S.0000000%%2B00%%3A00")
        self.assertEqual(0, self.get(everything + "&minDateLastSaved=" + tomorrow)["TotalRecordCount"])
        since = (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(minutes=5)).strftime("%Y-%m-%dT%H%%3A%M%%3A%S.0000000%%2B00%%3A00")
        self.assertEqual(0, self.get(everything + "&minDateLastSaved=" + since)["TotalRecordCount"], "the fixture was saved long ago")
        status, body, _ = self.request("/__harness/titles", "POST", {"add": [{"name": "A Title Added Now", "library": "Movies"}]})
        self.assertEqual(200, status)
        try:
            saved = self.get(everything.replace("limit=1", "limit=10") + "&minDateLastSaved=" + since)
            self.assertEqual(["A Title Added Now"], [i["Name"] for i in saved["Items"]])
            self.assertEqual(body["added"], [i["Id"] for i in saved["Items"]])
            self.assertEqual(total + 1, self.get(everything)["TotalRecordCount"])
        finally:
            status, gone, _ = self.request("/__harness/titles", "POST", {"remove": ["A Title Added Now"]})
        self.assertEqual((200, body["added"]), (status, gone["removed"]))
        self.assertEqual(total, self.get(everything)["TotalRecordCount"])

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

    def test_a_series_watched_again_comes_back_in_next_up_only_with_rewatching(self):
        # sg-cw: as Jellyfin 10.11 and 12 (QA servers): a fully watched show whose first episode is played again is in
        # Next Up (its second episode) only with EnableRewatching; its first, stopped midway, is in Continue Watching
        fx = self.get("/__harness/fixture")
        series = fx["series"]["id"]
        status, marked, _ = self.request("/__harness/played", "POST", {"series": series, "played": True})
        self.assertEqual(200, status)
        eps = marked["episodes"]
        self.assertGreaterEqual(len(eps), 3)
        self.assertEqual([1, 1], [eps[0]["season"], eps[0]["index"]])
        try:
            def next_up(rewatching):
                q = "/Shows/NextUp?Limit=24" + ("&EnableRewatching=true" if rewatching else "")
                return [i["Id"] for i in self.get(q)["Items"] if i.get("SeriesId") == series]
            self.assertEqual([], next_up(False))
            self.assertEqual([], next_up(True))   # nothing played again yet: nothing after the one last played
            runtime = self.srv.lib.runtime
            self.request("/Sessions/Playing", "POST", {"ItemId": eps[0]["id"], "PositionTicks": 0, "PlaySessionId": "rw1"}, token=self.token)
            self.request("/Sessions/Playing/Stopped", "POST", {"ItemId": eps[0]["id"], "PositionTicks": runtime // 4,
                                                               "PlaySessionId": "rw1"}, token=self.token)
            self.assertEqual([], next_up(False))
            self.assertEqual([eps[1]["id"]], next_up(True))
            resume = {i["Id"]: i["UserData"] for i in self.get("/UserItems/Resume?Limit=50")["Items"]}
            self.assertEqual(runtime // 4, resume[eps[0]["id"]]["PlaybackPositionTicks"])
        finally:
            self.request("/__harness/played", "POST", {"series": series, "played": False})
            self.srv.lib._seed_user_data()

    def test_lag_applies_a_stopped_report_after_it_was_answered(self):
        # sg-cw: a server that has not caught up with a Stopped report when the client asks again
        movie = self.get("/__harness/fixture")["movie"]["id"]
        runtime = self.srv.lib.runtime
        self.assertEqual(200, self.request("/__harness/lag", "POST", {"stopped_ms": 400})[0])
        try:
            self.request("/Sessions/Playing/Stopped", "POST", {"ItemId": movie, "PositionTicks": runtime // 5,
                                                               "PlaySessionId": "lag1"}, token=self.token)
            self.assertNotEqual(runtime // 5, self.get("/Items/" + movie)["UserData"]["PlaybackPositionTicks"])
            deadline = time.time() + 5
            while time.time() < deadline and self.get("/Items/" + movie)["UserData"]["PlaybackPositionTicks"] != runtime // 5:
                time.sleep(0.05)
            self.assertEqual(runtime // 5, self.get("/Items/" + movie)["UserData"]["PlaybackPositionTicks"])
        finally:
            self.request("/__harness/lag", "POST", {"stopped_ms": 0})
            self.request("/UserItems/%s/UserData" % movie, "POST", {"PlaybackPositionTicks": 0}, token=self.token)

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

    def test_what_needs_a_token_is_what_jellyfin_asks_one_for(self):
        # 1.5 qacheck (live-auth.ps1, 10.11.11 and 12.4.0): an image, a static stream and a subtitle file answer without a
        # token; trickplay is 401 and the socket 403 without one; with ApiKey (any case) they answer
        fx = self.get("/__harness/fixture")
        movie = fx["movie"]["id"]
        self.assertEqual(200, self.request("/Items/%s/Images/Primary" % movie, raw=True)[0])
        self.assertEqual(200, self.request("/Videos/%s/stream?static=true" % movie, raw=True,
                                           headers={"Range": "bytes=0-1023"})[0] // 100 * 100)
        self.assertEqual(401, self.request("/Videos/%s/Trickplay/320/0.jpg" % movie, raw=True)[0])
        self.assertEqual(200, self.request("/Videos/%s/Trickplay/320/0.jpg?ApiKey=%s" % (movie, self.token), raw=True)[0])
        for query, want in (("", b" 403 "), ("&apikey=" + self.token, b" 101 ")):
            sock = socket.create_connection(("127.0.0.1", self.srv.port), timeout=10)
            key = base64.b64encode(os.urandom(16)).decode()
            sock.sendall(("GET /socket?deviceId=t1%s HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\n"
                          "Connection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n"
                          % (query, key)).encode())
            line = sock.makefile("rb").readline()
            sock.close()
            self.assertIn(want, line, query or "no token")

    def test_strict_by_default_says_12_and_logs_the_form(self):
        # 1.5 qacheck (BH-140X-m4): the version a v12 server reports, and each request's auth form in the log
        self.assertEqual("12.4.0", self.get("/System/Info/Public")["Version"])
        self.request("/Users/Me", token=self.token)
        self.request("/Users/Me", headers={"X-Emby-Token": self.token})
        with open(os.path.join(self.srv.out, "requests.jsonl"), encoding="utf-8") as f:
            me = [json.loads(line) for line in f if '"/Users/Me"' in line]
        self.assertEqual("header", me[-2]["auth_form"])
        self.assertTrue(me[-2]["auth"])
        self.assertEqual("legacy:x-emby-token", me[-1]["auth_form"])
        self.assertFalse(me[-1]["auth"])


class AuthTests(unittest.TestCase):
    """1.5 qacheck (BH-140X-m4): the token forms, strict (Jellyfin 12, the default) and legacy (10.x). The strict answers
    are the ones tools\\qa-servers\\live-auth.ps1 got from a real 12.4.0 (2026-11-20)."""

    TOKEN = "a" * 32
    MB = 'MediaBrowser Client="Nixie", Device="d", DeviceId="i", Version="1"'

    def form(self, headers, query, mode=server.AUTH_STRICT):
        return server.auth_token(headers, {k.lower(): v for k, v in query.items()}, mode)

    def test_strict_reads_the_two_forms_jellyfin_12_reads(self):
        t = self.TOKEN
        self.assertEqual((t, "header", "Nixie"), self.form({"Authorization": self.MB + ', Token="%s"' % t}, {}))
        self.assertEqual((t, "apikey", ""), self.form({}, {"ApiKey": [t]}))
        # query keys are case-insensitive in ASP.NET: apikey is ApiKey (live-auth.ps1: 200 on 12.4.0)
        self.assertEqual((t, "apikey", ""), self.form({}, {"apikey": [t]}))
        # the client's name still comes from the header next to an ApiKey
        self.assertEqual((t, "apikey", "Nixie"), self.form({"Authorization": self.MB}, {"ApiKey": [t]}))
        self.assertEqual((None, "none", "Nixie"), self.form({"Authorization": self.MB}, {}))

    def test_strict_refuses_every_legacy_form(self):
        t = self.TOKEN
        for headers, query, form in (
                ({"X-Emby-Authorization": self.MB + ', Token="%s"' % t}, {}, "legacy:x-emby-authorization"),
                ({"Authorization": 'Emby Client="Nixie", Token="%s"' % t}, {}, "legacy:scheme-emby"),
                ({"X-Emby-Token": t}, {}, "legacy:x-emby-token"),
                ({"X-MediaBrowser-Token": t}, {}, "legacy:x-mediabrowser-token"),
                ({}, {"api_key": [t]}, "legacy:query-api_key")):
            token, got, _ = self.form(headers, query)
            self.assertIsNone(token, form)
            self.assertEqual(form, got)
            # the legacy server reads each of them
            self.assertEqual((t, form), self.form(headers, query, server.AUTH_LEGACY)[:2])

    def test_a_legacy_server_answers_the_old_forms_and_says_10_11(self):
        tmp = tempfile.mkdtemp(prefix="ffh-legacy-")
        srv = server.Server(os.path.join(tmp, "out"), make_media(os.path.join(tmp, "media")), CREDS, auth=server.AUTH_LEGACY)
        threading.Thread(target=srv.serve, daemon=True).start()
        try:
            def call(path, method="GET", body=None, headers=None):
                req = urllib.request.Request(srv.url + path, method=method, data=None if body is None else json.dumps(body).encode())
                for k, v in (headers or {}).items():
                    req.add_header(k, v)
                if body is not None:
                    req.add_header("Content-Type", "application/json")
                try:
                    with urllib.request.urlopen(req, timeout=10) as r:
                        return r.status, json.loads(r.read() or b"null")
                except urllib.error.HTTPError as e:
                    return e.code, None
            _, info = call("/System/Info/Public")
            self.assertEqual("10.11.11", info["Version"])
            _, auth = call("/Users/AuthenticateByName", "POST", {"Username": CREDS["username"], "Pw": CREDS["password"]},
                           {"X-Emby-Authorization": AUTH})
            token = auth["AccessToken"]
            self.assertEqual(200, call("/Users/Me?api_key=" + token)[0])
            self.assertEqual(200, call("/Users/Me", headers={"X-Emby-Token": token})[0])
            with self.assertRaises(ValueError):
                server.Server(os.path.join(tmp, "out2"), make_media(os.path.join(tmp, "media2")), CREDS, auth="loose")
        finally:
            srv.httpd.shutdown()
            srv.httpd.server_close()
            srv.log_file.close()
            shutil.rmtree(tmp, ignore_errors=True)


class LatencyRuleQueryTests(unittest.TestCase):
    def test_a_rule_with_an_escaped_question_mark_reads_the_query_others_the_path_alone(self):
        srv = server.Server.__new__(server.Server)
        srv.latency = 5.0
        srv.rules = [(__import__("re").compile(r, __import__("re").I), ms) for r, ms in
                     [(r"^/Items\?.*ParentId=", 300.0), (r"/Items/[^/]+/Images/", 80.0), (r"ParentId", 999.0)]]
        self.assertEqual(300.0, srv.delay_for("/Items", "SortBy=SortName&parentId=abc&Fields=Overview"))
        self.assertEqual(5.0, srv.delay_for("/Items", "Ids=abc"), "a list without a parent: the default")
        self.assertEqual(5.0, srv.delay_for("/Items/abc", "parentId=abc"), "one item: the path is not /Items")
        self.assertEqual(80.0, srv.delay_for("/Items/abc/Images/Primary", "maxWidth=352&tag=x"))
        # a rule without "\?" never sees the query (as before)
        self.assertEqual(5.0, srv.delay_for("/Shows/x/Episodes", "ParentId=abc"))


class BigSeriesTests(unittest.TestCase):
    def test_the_size_is_seasons_x_episodes_or_seasons_alone(self):
        self.assertEqual((10, 20), library.Library.big_series_size("10x20"))
        self.assertEqual((8, 20), library.Library.big_series_size(" 8 "))
        self.assertEqual((3, 5), library.Library.big_series_size("3X5"))
        for off in (None, "", "0", "x20", "abc", "4x0", "-2x5"):
            self.assertIsNone(library.Library.big_series_size(off), off)

    def test_off_by_default_and_the_default_fixture_is_unchanged(self):
        old = os.environ.pop("FAKESERVER_BIG_SERIES", None)
        try:
            plain = library.Library("server", "user", 600)
            os.environ["FAKESERVER_BIG_SERIES"] = "10x20"
            big = library.Library("server", "user", 600)
        finally:
            os.environ.pop("FAKESERVER_BIG_SERIES", None)
            if old is not None:
                os.environ["FAKESERVER_BIG_SERIES"] = old
        self.assertIsNone(plain.showcase()["big_series"])
        # every item of the default library is the same in the big one (names, ids, user data), the showcase first in Shows
        for iid, item in plain.items.items():
            self.assertEqual(item["Name"], big.items[iid]["Name"], iid)
        self.assertEqual(plain.user, big.user)
        self.assertEqual({k: v for k, v in plain.showcase().items() if k != "big_series"},
                         {k: v for k, v in big.showcase().items() if k != "big_series"})
        shows = library._id("view", "Shows")
        self.assertEqual(plain.children[shows][0], big.children[shows][0])
        self.assertEqual(library.Library.BIG_SERIES_NAME, big.items[big.children[shows][1]]["Name"])
        fx = big.showcase()["big_series"]
        self.assertEqual(["Season %d" % s for s in range(1, 11)], [s["name"] for s in fx["seasons"]])
        names = [e["name"] for s in fx["seasons"] for e in s["episodes"]]
        self.assertEqual(200, len(names))
        self.assertEqual(200, len(set(names)), "every episode has a name of its own in the series")
        others = {i["Name"] for i in big.items.values() if i["kind"] in ("movie", "series", "boxset")}
        self.assertFalse(set(names) & others, "no episode is named as a film or a show")
        # nothing watched (the page opens on Season 1), every episode with a Primary image, old enough for no Home row
        episodes = big.episodes_of(fx["id"])
        self.assertFalse(any((big.user.get(e["Id"]) or {}).get("played") for e in episodes))
        self.assertTrue(all("Primary" in big.dto(e)["ImageTags"] for e in episodes))
        self.assertNotIn(fx["id"], [d["Id"] for d in big.latest(shows)])
        # the season's list, as the app asks for it
        season2 = fx["seasons"][1]
        listed = big.query({"parentid": [season2["id"]], "sortby": ["SortName"]})["Items"]
        self.assertEqual([e["name"] for e in season2["episodes"]], [d["Name"] for d in listed])
        # Nixie 1.7.4: the series and every season have the long cast (16, names as long as real ones and longer), and the
        # fixture lists it with the role shown under each photo
        names = [p["name"] for p in fx["cast"]]
        self.assertEqual(16, len(names))
        self.assertEqual(library.Library.LONG_CAST, names)
        self.assertEqual("Commander Evangeline Ashcroft-Reyes", fx["cast"][0]["role"])
        self.assertEqual("Producer", fx["cast"][-1]["role"])
        for season in fx["seasons"]:
            self.assertEqual(names, [p["Name"] for p in big.items[season["id"]]["people"]])
        self.assertTrue(all(p.get("PrimaryImageTag") for p in big.items[fx["id"]]["people"]))


class RealShapeTests(unittest.TestCase):
    """Nixie 1.7.5 (the basics gate): FAKESERVER_REAL_SHAPE=1 adds real-shaped titles and changes nothing else."""

    def _lib(self, **env):
        keys = ("FAKESERVER_REAL_SHAPE", "FAKESERVER_COLLECTIONS", "FAKESERVER_BIG_SERIES")
        old = {k: os.environ.pop(k, None) for k in keys}
        try:
            os.environ.update(env)
            return library.Library("server", "user", 600)
        finally:
            for k in keys:
                os.environ.pop(k, None)
                if old[k] is not None:
                    os.environ[k] = old[k]

    def test_off_by_default_and_the_rest_of_the_fixture_is_unchanged(self):
        plain, real = self._lib(), self._lib(FAKESERVER_REAL_SHAPE="1")
        self.assertIsNone(plain.showcase()["real_shape"])
        for iid, item in plain.items.items():
            self.assertEqual(item["Name"], real.items[iid]["Name"], iid)
            # a film the person is added to keeps its own people first, in their order
            self.assertEqual(item.get("people") or [], (real.items[iid].get("people") or [])[:len(item.get("people") or [])], iid)
        for iid, ud in plain.user.items():
            self.assertEqual(ud, real.user[iid], iid)
        self.assertEqual({k: v for k, v in plain.showcase().items() if k != "real_shape"},
                         {k: v for k, v in real.showcase().items() if k != "real_shape"})
        # the showcase titles stay first in their libraries; Home's rows other than Continue watching are as they were
        for view in ("Movies", "Shows"):
            vid = library._id("view", view)
            self.assertEqual(plain.children[vid][0], real.children[vid][0], view)
            self.assertEqual([d["Id"] for d in plain.latest(vid)], [d["Id"] for d in real.latest(vid)], view)
        self.assertEqual([d["Id"] for d in plain.next_up()["Items"]], [d["Id"] for d in real.next_up()["Items"]])
        # and with the big series too: it stays second in Shows, the real series after it
        both = self._lib(FAKESERVER_REAL_SHAPE="1", FAKESERVER_BIG_SERIES="24x13")
        shows = [both.items[i]["Name"] for i in both.children[library._id("view", "Shows")][:3]]
        self.assertEqual([library.SHOWCASE["series"], library.Library.BIG_SERIES_NAME, library.Library.REAL_SERIES_NAME], shows)

    def test_seasons_of_13_and_24_long_names_a_cast_of_48_and_a_person_in_40_films(self):
        real = self._lib(FAKESERVER_REAL_SHAPE="1", FAKESERVER_COLLECTIONS="1")
        fx = real.showcase()["real_shape"]
        series = fx["series"]
        self.assertGreater(len(series["name"]), 80)
        self.assertEqual([13, 24], [len(s["episodes"]) for s in series["seasons"]])
        names = [e["name"] for s in series["seasons"] for e in s["episodes"]]
        self.assertEqual(len(names), len(set(names)), "every episode has a name of its own in the series")
        self.assertTrue(any(len(n) > 60 for n in names) and any(len(n) < 10 for n in names))
        self.assertTrue(any(" " not in n and len(n) > 30 for n in names), "a long name with no space to break at")
        # the season's list, as the app asks for it, and its count
        s2 = series["seasons"][1]
        listed = real.query({"parentid": [s2["id"]], "sortby": ["SortName"]})["Items"]
        self.assertEqual([e["name"] for e in s2["episodes"]], [d["Name"] for d in listed])
        self.assertEqual(24, real.dto(real.items[s2["id"]])["ChildCount"])
        self.assertEqual(library.Library.LONG_CAST, [p["name"] for p in series["cast"]])
        # Season 1 and Season 2 to E5 watched; E6 in progress, Continue watching's first card
        progress = fx["in_progress"]
        self.assertEqual((2, 6), (progress["season"], progress["index"]))
        first = real.resume(limit=12)["Items"][0]
        self.assertEqual((progress["id"], series["name"]), (first["Id"], first["SeriesName"]))
        eps = real.episodes_of(series["id"])
        self.assertEqual(13 + 5, sum(1 for e in eps if (real.user.get(e["Id"]) or {}).get("played")))
        # the film: a long title, 48 people (36 and more in Cast & crew), second in Movies after the showcase film
        film = fx["film"]
        self.assertGreater(len(film["name"]), 80)
        self.assertEqual(48, film["cast"])
        detail = real.dto(real.items[film["id"]], detail=True)
        self.assertEqual(48, len(detail["People"]))
        self.assertEqual(48, len({p["Id"] for p in detail["People"]}))
        movies = library._id("view", "Movies")
        self.assertEqual(film["id"], real.children[movies][1])
        # the person: in 40 films and more (their titles' grid and "With <name>")
        person = fx["person"]
        self.assertGreaterEqual(person["films"], 40)
        titles = real.query({"personids": [person["id"]], "recursive": ["true"], "includeitemtypes": ["Movie"]})
        self.assertEqual(person["films"], titles["TotalRecordCount"])
        # the box set of 40 (FAKESERVER_COLLECTIONS), never the first of the Collections library
        box = fx["collection"]
        self.assertEqual(40, box["count"])
        self.assertGreater(len(box["name"]), 60)
        self.assertNotEqual(box["name"], real.showcase()["collections"][0]["name"])
        self.assertIsNone(self._lib(FAKESERVER_REAL_SHAPE="1").showcase()["real_shape"]["collection"])

    def test_the_big_librarys_first_and_last_titles_as_sorted_by_name(self):
        old = os.environ.pop("FAKESERVER_BIG_LIBRARY", None)
        try:
            os.environ["FAKESERVER_BIG_LIBRARY"] = "300"
            lib = library.Library("server", "user", 600)
        finally:
            os.environ.pop("FAKESERVER_BIG_LIBRARY", None)
            if old is not None:
                os.environ["FAKESERVER_BIG_LIBRARY"] = old
        big = lib.showcase()["big"][0]
        listed = lib.query({"parentid": [library._id("view", "Archive")], "sortby": ["SortName"]})["Items"]
        self.assertEqual((300, listed[0]["Name"], listed[-1]["Name"]), (big["count"], big["first"], big["last"]))


class SearchTests(unittest.TestCase):
    """1.5 search: the fake answers as Jellyfin 12.4 (docs/research/search.md): one cleaned substring, scored, capped."""

    @classmethod
    def setUpClass(cls):
        old = {k: os.environ.pop(k, None) for k in ("FAKESERVER_SEARCH", "FAKESERVER_SEARCH_MODE")}
        try:
            cls.plain = library.Library("server", "user", 600)
            os.environ["FAKESERVER_SEARCH"] = "sunny"
            cls.lib = library.Library("server", "user", 600)
        finally:
            for k, v in old.items():
                os.environ.pop(k, None)
                if v is not None:
                    os.environ[k] = v

    def names(self, term, kind="Movie,Series", **more):
        q = {"searchterm": [term], "recursive": ["true"], "includeitemtypes": [kind]}
        q.update({k: [str(v)] for k, v in more.items()})
        return [i["Name"] for i in self.lib.query(q)["Items"]]

    def test_off_by_default_the_default_fixture_is_unchanged(self):
        for iid, item in self.plain.items.items():
            self.assertEqual(item["Name"], self.lib.items[iid]["Name"], iid)
        self.assertEqual(self.plain.user, self.lib.user)
        self.assertEqual("stock", self.plain.search.mode)

    def test_one_substring_in_the_order_typed_apostrophes_and_punctuation_as_spaces(self):
        self.assertEqual(["It's Always Sunny in Philadelphia"], self.names("it's always sunny"))
        self.assertEqual(["It's Always Sunny in Philadelphia"], self.names("it\u2019s always sunny"))
        self.assertEqual([], self.names("sunny always"))           # words out of order: nothing
        self.assertEqual(["WALL\u00b7E"], self.names("wall-e"))
        self.assertEqual(["Am\u00e9lie"], self.names("AMELIE"))
        # the original title, as LIKE: the accents are taken off the term only, so the accented title is not found
        self.assertEqual(["Am\u00e9lie"], self.names("fabuleux destin"))
        self.assertEqual([], self.names("destin d'am\u00e9lie"))
        # "M*A*S*H" is "m a s h": "mash" finds nothing
        self.assertEqual([], self.names("mash"))

    def test_scored_then_sort_name_whatever_the_sort_asked(self):
        # 12.4: a word start (75) beats the exact title after "The" (50); sortBy is ignored while searching
        names = self.names("philadelphia", "Movie", sortby="ProductionYear", sortorder="Descending")
        self.assertEqual("Philadelphia", names[0])
        self.assertLess(names.index("The Philadelphia Story"), len(names))

    def test_the_total_is_capped_at_three_times_the_limit(self):
        q = {"searchterm": ["a"], "recursive": ["true"], "includeitemtypes": ["Movie"]}
        every = self.lib.query(dict(q, limit=["1000"]))["TotalRecordCount"]
        self.assertGreater(every, 30)
        capped = self.lib.query(dict(q, limit=["10"]))
        self.assertEqual(30, capped["TotalRecordCount"])
        self.assertEqual(10, len(capped["Items"]))
        self.assertEqual(100, self.lib.query(q)["TotalRecordCount"])          # no limit: the providers' 100

    def test_a_term_with_nothing_left_matches_by_like(self):
        q = {"searchterm": ["%"], "recursive": ["true"], "includeitemtypes": ["Series"], "limit": ["1000"]}
        self.assertEqual(len([i for i in self.lib.items.values() if i["kind"] == "series"]), self.lib.query(q)["TotalRecordCount"])

    def test_hints_people_persons_and_person_ids(self):
        hints = self.lib.hints({"searchterm": ["sunny"], "limit": ["60"]})
        types = {h["Type"] for h in hints["SearchHints"]}
        self.assertTrue({"Movie", "Series", "Person"} <= types, types)
        self.assertLessEqual(len(hints["SearchHints"]), 60)
        self.assertEqual(hints["TotalRecordCount"], len(hints["SearchHints"]))
        self.assertNotIn("Person", {h["Type"] for h in self.lib.hints({"searchterm": ["sunny"], "includepeople": ["false"]})["SearchHints"]})
        self.assertIsNone(self.lib.hints({"searchterm": ["  "]}))           # the server's 400
        people = self.lib.persons({"searchterm": ["sunny holm"]})
        self.assertEqual(["Sunny Holm"], [p["Name"] for p in people["Items"]])
        pid = people["Items"][0]["Id"]
        films = self.lib.query({"personids": [pid], "recursive": ["true"], "includeitemtypes": ["Movie,Series"]})
        self.assertTrue(films["Items"])
        self.assertTrue(all(any(p["Id"] == pid for p in self.lib.items[i["Id"]]["people"]) for i in films["Items"]))
        # 1.6.1 (BH-150-m3): a guest star in two episodes, credited on them only, with their show for Nixie's one card per show
        self.assertNotIn("Episode", {i["Type"] for i in films["Items"]})
        guest = self.lib.query({"personids": [pid], "recursive": ["true"], "includeitemtypes": ["Movie,Series,Episode"]})
        episodes = [i for i in guest["Items"] if i["Type"] == "Episode"]
        self.assertEqual(2, len(episodes))
        self.assertEqual({"It's Always Sunny in Philadelphia"}, {e["SeriesName"] for e in episodes})
        self.assertTrue(all(e.get("SeriesId") and e.get("SeriesPrimaryImageTag") for e in episodes))

    def test_sort_and_original_titles_only_when_asked_for(self):
        q = {"searchterm": ["amelie"], "recursive": ["true"], "includeitemtypes": ["Movie"]}
        bare = self.lib.query(q)["Items"]
        self.assertTrue(bare)
        self.assertFalse([i for i in bare if "SortName" in i or "OriginalTitle" in i])
        asked = self.lib.query(dict(q, fields=["OriginalTitle,SortName"]))["Items"]
        amelie = [i for i in asked if i["Name"] == "Amélie"][0]
        self.assertEqual("Le Fabuleux Destin d'Amélie Poulain", amelie["OriginalTitle"])
        self.assertIn("SortName", amelie)

    def test_home_videos_music_videos_albums_and_songs_are_found_as_jellyfin_finds_them(self):
        # 1.5.1 fixes (BH-150-m7): the kinds Nixie asks for now, in no library view (no tab, page or Home row changes)
        self.assertEqual(["Sunny Afternoon", "Sunny Beach Day"], sorted(self.names("sunny", "Video,MusicVideo")))
        found = self.lib.query({"searchterm": ["sunny"], "recursive": ["true"], "includeitemtypes": ["MusicAlbum,Audio"]})["Items"]
        self.assertEqual({"Sunny Side Songs": "MusicAlbum", "Sunny Morning": "Audio"}, {i["Name"]: i["Type"] for i in found})
        song = [i for i in found if i["Type"] == "Audio"][0]
        album = [i for i in found if i["Type"] == "MusicAlbum"][0]
        self.assertEqual((album["Id"], "Sunny Side Songs"), (song["AlbumId"], song["Album"]))
        self.assertEqual(["Sunny Morning"], [i["Name"] for i in self.lib.query({"parentid": [album["Id"]]})["Items"]])
        types = {h["Type"] for h in self.lib.hints({"searchterm": ["sunny"], "limit": ["200"]})["SearchHints"]}
        self.assertTrue({"Video", "MusicVideo", "MusicAlbum", "Audio"} <= types, types)
        self.assertFalse([v for v in self.lib.views
                          if any(d["kind"] in ("video", "musicvideo", "musicalbum", "audio") for d in self.lib.descendants(v))])
        # not in the default fixture
        self.assertFalse([i for i in self.plain.items.values() if i["kind"] in ("video", "musicvideo", "musicalbum", "audio")])

    def test_loose_mode_answers_any_word_as_a_search_plugin(self):
        os.environ["FAKESERVER_SEARCH"] = "sunny"
        try:
            loose = library.Library("server", "user", 600)
        finally:
            os.environ.pop("FAKESERVER_SEARCH", None)
        loose.search = library.search_mod.Search("loose")
        q = {"searchterm": ["it's always sunny"], "recursive": ["true"], "includeitemtypes": ["Movie"], "limit": ["100"]}
        films = loose.query(q)
        self.assertGreater(films["TotalRecordCount"], 20)               # the owner's "Movies (100 of 295)"
        self.assertTrue(any(i["Name"].startswith("Always ") for i in films["Items"]))


class SearchRouteTests(unittest.TestCase):
    """The routes: /Search/Hints (400 without a term, 401 without a token), /Persons, the harness switch."""
    setUpClass = classmethod(ServerTests.setUpClass.__func__)
    tearDownClass = classmethod(ServerTests.tearDownClass.__func__)
    request = classmethod(ServerTests.request.__func__)
    login = classmethod(ServerTests.login.__func__)
    get = ServerTests.get

    def test_hints_route_and_refusals(self):
        self.assertEqual(400, self.request("/Search/Hints?searchTerm=", token=self.token)[0])
        self.assertEqual(401, self.request("/Search/Hints?searchTerm=harbor")[0])
        found = self.get("/Search/Hints?searchTerm=harbor&limit=5")
        self.assertLessEqual(len(found["SearchHints"]), 5)
        self.assertEqual(401, self.request("/Persons?searchTerm=a")[0])
        self.assertIn("Items", self.get("/Persons?searchTerm=a&limit=3"))
        # an unknown parameter is dropped, as the real server drops it (200, the same answer)
        self.assertEqual(self.get("/Items?searchTerm=harbor&recursive=true&includeItemTypes=Movie")["TotalRecordCount"],
                         self.get("/Items?searchTerm=harbor&recursive=true&includeItemTypes=Movie&nixieBogus=1")["TotalRecordCount"])

    def test_the_harness_switch(self):
        status, body, _ = self.request("/__harness/search", "POST", {"mode": "loose"})
        self.assertEqual((200, "loose"), (status, body["mode"]))
        status, body, _ = self.request("/__harness/search", "POST", {"mode": "stock"})
        self.assertEqual("stock", body["mode"])


# 1.6 topbar
class TopBarFixtureTests(unittest.TestCase):
    """FAKESERVER_VIEWS lists the first n default libraries; FAKESERVER_HUB_VIEWS puts three "[HUB]" hubs first."""

    def _lib(self, **env):
        keys = ("FAKESERVER_VIEWS", "FAKESERVER_HUB_VIEWS")
        old = {k: os.environ.pop(k, None) for k in keys}
        try:
            os.environ.update(env)
            return library.Library("server", "user", 600)
        finally:
            for k in keys:
                os.environ.pop(k, None)
                if old[k] is not None:
                    os.environ[k] = old[k]

    def test_the_first_views_only_and_the_showcase_still_answers(self):
        lib = self._lib(FAKESERVER_VIEWS="1")
        self.assertEqual(["Movies"], [lib.items[v]["Name"] for v in lib.views])
        self.assertEqual(["Movies"], lib.showcase()["views"])
        self.assertEqual("Shows", lib.showcase()["series"]["library"])

    def test_the_hubs_come_first_by_kind(self):
        lib = self._lib(FAKESERVER_HUB_VIEWS="1")
        names = [lib.items[v]["Name"] for v in lib.views]
        self.assertEqual(["[HUB] Trending Movies", "[HUB] Kids Movies", "[HUB] Trending Shows", "Movies", "Shows"], names[:5])
        self.assertEqual(["movies", "movies", "tvshows"], [lib.items[v]["collection"] for v in lib.views[:3]])
        self.assertTrue(all(lib.children[v] for v in lib.views[:3]))
# end 1.6 topbar


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



class CriticRatingTests(unittest.TestCase):
    """1.7.3 fix round (BH-173-m3): every film had a critics' rating, so every harness run passed Acclaimed's backing check
    on a signal the servers Nixie meets lack. Two films in five have none now; FAKESERVER_PLAIN=1, none."""

    def _films(self):
        lib = library.Library("server", "user", 600)
        return [i for i in lib.items.values() if i.get("kind") == "movie"]

    def test_some_films_have_no_critics_rating_and_the_names_stay(self):
        films = self._films()
        none = [f for f in films if f["critic"] is None]
        self.assertTrue(0.25 < len(none) / len(films) < 0.55, "%d of %d" % (len(none), len(films)))
        self.assertTrue(any(f["critic"] is not None for f in films))
        self.assertTrue(all(len(f["tags"]) <= 3 for f in films))   # few tags: none backs a title on its own
        old = os.environ.pop("FAKESERVER_PLAIN", None)
        try:
            os.environ["FAKESERVER_PLAIN"] = "1"
            plain = self._films()
        finally:
            os.environ.pop("FAKESERVER_PLAIN", None)
            if old is not None:
                os.environ["FAKESERVER_PLAIN"] = old
        self.assertTrue(all(f["critic"] is None for f in plain))
        # the seed's draws are the same: every other name and number stays
        self.assertEqual([(f["Name"], f["community"]) for f in films], [(f["Name"], f["community"]) for f in plain])


class SavedSinceTests(unittest.TestCase):
    """Nixie 1.8.1 (BH-175-q4): /Items' MinDateLastSaved, and the harness's titles (a mixed library the index skips)."""

    def test_a_title_added_to_a_mixed_library_is_saved_now_and_a_removed_one_is_gone(self):
        lib = library.Library("server", "user", 600)
        q = {"includeitemtypes": ["Movie,Series"], "recursive": ["true"]}
        total = lib.query(q)["TotalRecordCount"]
        now = datetime.datetime.now(datetime.timezone.utc)
        # the SDK's form, its "+" read as a space by the query's decoding
        since = (now - datetime.timedelta(minutes=1)).strftime("%Y-%m-%dT%H:%M:%S.0000000 00:00")
        self.assertEqual(0, lib.query(dict(q, mindatelastsaved=[since]))["TotalRecordCount"])
        self.assertEqual(total, lib.query(dict(q, mindatelastsaved=["1970-01-01T00:00:00Z"]))["TotalRecordCount"])
        self.assertEqual(total, lib.query(dict(q, mindatelastsaved=["not a date"]))["TotalRecordCount"])
        movies = library._id("view", "Movies")
        removed = lib.items[lib.children[movies][5]]["Name"]
        added = lib.add_titles([{"name": "Mixed Film", "library": "Home and Away", "collection": None}, {"name": "New Film"}])
        self.assertEqual(2, len(added))
        self.assertEqual([], lib.remove_titles([]))
        self.assertEqual(1, len(lib.remove_titles([removed])))
        saved = lib.query(dict(q, mindatelastsaved=[since]))
        self.assertEqual(["Mixed Film", "New Film"], sorted(i["Name"] for i in saved["Items"]))
        self.assertEqual(["New Film"], [i["Name"] for i in lib.query(dict(q, parentid=[movies], mindatelastsaved=[since]))["Items"]])
        self.assertEqual(total + 1, lib.query(q)["TotalRecordCount"])   # two added, one removed
        self.assertNotIn(removed, [i["Name"] for i in lib.query(dict(q, parentid=[movies]))["Items"]])
        mixed = [v for v in lib.views if lib.items[v]["Name"] == "Home and Away"]
        self.assertEqual(1, len(mixed))
        self.assertIsNone(lib.dto(lib.items[mixed[0]])["CollectionType"])   # a mixed library: no CollectionType


if __name__ == "__main__":
    unittest.main()
