"""Nixie 1.6 (smart collections): the fake server's smart-collection data and "Save as playlist" (smartdata.py), modelled on
what the QA servers answered (docs\\research\\smart-collections-data.md). Standard library only:

    python -B -m unittest discover -s tools/ui-harness/fakeserver -p "test_smartdata.py"
"""

import os
import shutil
import sys
import tempfile
import threading
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import library  # noqa: E402
import server  # noqa: E402
import test_fakeserver as tf  # noqa: E402 (its helpers; a module import keeps its test classes out of this one)
CREDS, make_media = tf.CREDS, tf.make_media

INDEX = ("/Items?parentId={view}&includeItemTypes=Movie&recursive=true&sortBy=SortName&limit=1000"
         "&fields=ProviderIds,Tags,Genres,Studios,ProductionLocations,SortName,DateCreated")


class SmartDataTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ["FAKESERVER_COLLECTIONS"] = "1"
        try:
            cls.tmp = tempfile.mkdtemp(prefix="ffh-smart-")
            cls.srv = server.Server(os.path.join(cls.tmp, "out"), make_media(os.path.join(cls.tmp, "media")), CREDS)
        finally:
            del os.environ["FAKESERVER_COLLECTIONS"]
        threading.Thread(target=cls.srv.serve, daemon=True).start()
        cls.token = tf.ServerTests.login.__func__(cls, CREDS["username"], CREDS["password"])["AccessToken"]

    request = classmethod(tf.ServerTests.request.__func__)

    @classmethod
    def tearDownClass(cls):
        cls.srv.httpd.shutdown()
        cls.srv.httpd.server_close()
        cls.srv.log_file.close()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def get(self, path):
        status, data, _ = self.request(path, token=self.token)
        self.assertEqual(200, status, path)
        return data

    def movies_view(self):
        return next(v["Id"] for v in self.get("/UserViews")["Items"] if v["Name"] == "Movies")

    def test_the_index_fields_come_only_when_asked_and_an_unknown_name_is_dropped(self):
        view = self.movies_view()
        asked = self.get(INDEX.format(view=view))["Items"]
        bogus = self.get(INDEX.format(view=view).split("&fields=")[0] + "&fields=NixieNoSuchField")["Items"]
        self.assertEqual(len(asked), len(bogus))
        self.assertTrue(all("ProviderIds" in i and "Studios" in i and "ProductionLocations" in i for i in asked))
        self.assertFalse(any("ProviderIds" in i or "Studios" in i or "People" in i or "ProductionLocations" in i for i in bogus))
        people = self.get("/Items?parentId=%s&includeItemTypes=Movie&recursive=true&limit=5&fields=People" % view)["Items"]
        self.assertTrue(all(p.get("Type") in ("Actor", "Director", "Writer", "Producer") for i in people for p in i["People"]))

    def test_franchises_directors_and_countries_are_there(self):
        films = self.get(INDEX.format(view=self.movies_view()) + ",People")["Items"]
        franchises = {}
        for f in films:
            if f["ProviderIds"].get("TmdbCollection"):
                franchises.setdefault(f["ProviderIds"]["TmdbCollection"], []).append(f["Name"])
        self.assertGreaterEqual(len(franchises), 6)
        self.assertTrue(all(len(v) >= 2 for v in franchises.values()))
        directors = [p["Name"] for f in films for p in f["People"] if p["Type"] == "Director"]
        self.assertGreaterEqual(directors.count("Ava Abara"), 3)
        self.assertTrue(all(f["ProductionLocations"] for f in films))

    def test_shows_carry_no_production_locations(self):
        shows = next(v["Id"] for v in self.get("/UserViews")["Items"] if v["Name"] == "Shows")
        items = self.get("/Items?parentId=%s&includeItemTypes=Series&recursive=true&limit=5&fields=ProductionLocations,ProviderIds" % shows)["Items"]
        self.assertTrue(items)
        self.assertFalse(any("ProductionLocations" in i for i in items))

    def test_the_admins_box_set_has_its_franchises_tmdb_id_and_leaves_one_film_out(self):
        sets = self.get("/Items?includeItemTypes=BoxSet&recursive=true&fields=ProviderIds,ChildCount")["Items"]
        box = next(s for s in sets if s["Name"] == "Kestrel Bay Trilogy")
        self.assertEqual("9001", box["ProviderIds"]["Tmdb"])
        self.assertFalse(any(s["ProviderIds"].get("Tmdb") for s in sets if s["Name"] != "Kestrel Bay Trilogy"))
        members = {i["Id"] for i in self.get("/Items?parentId=%s" % box["Id"])["Items"]}
        owned = [f for f in self.get(INDEX.format(view=self.movies_view()))["Items"] if f["ProviderIds"].get("TmdbCollection") == "9001"]
        self.assertEqual(1, len([f for f in owned if f["Id"] not in members]))
        self.assertEqual("9001", self.get("/Items/%s" % box["Id"])["ProviderIds"]["Tmdb"])

    def test_a_show_left_over_a_month_ago_has_its_played_episodes_dated(self):
        played = self.get("/Items?includeItemTypes=Episode&recursive=true&filters=IsPlayed&sortBy=DatePlayed&sortOrder=Ascending&limit=5")["Items"]
        self.assertTrue(played)
        self.assertTrue(all(e.get("SeriesId") and e["UserData"].get("LastPlayedDate") for e in played))

    def test_save_as_playlist_makes_the_users_own_and_a_refusal_is_its_status(self):
        films = self.get(INDEX.format(view=self.movies_view()))["Items"][:3]
        ids = [f["Id"] for f in films]
        status, made, _ = self.request("/Playlists", "POST", {"Name": "Heists", "Ids": ids, "UserId": self.srv.user_id, "MediaType": "Video"}, token=self.token)
        self.assertEqual(200, status)
        items = self.get("/Playlists/%s/Items" % made["Id"])["Items"]
        self.assertEqual(ids, [i["Id"] for i in items])
        self.assertEqual(200, self.request("/Playlists", "POST", {"Name": " ", "Ids": ids}, token=self.token)[0])   # as the QA servers
        self.assertEqual(401, self.request("/Playlists", "POST", {"Name": "x", "Ids": ids})[0])
        # a show sent to a playlist is every one of its episodes, as Jellyfin adds it
        show = next(i for i in self.get("/Items?includeItemTypes=Series&recursive=true&limit=5")["Items"])
        episodes = [e["Id"] for e in self.get("/Shows/%s/Episodes" % show["Id"])["Items"]]
        status, expanded, _ = self.request("/Playlists", "POST", {"Name": "A show", "Ids": [show["Id"]]}, token=self.token)
        self.assertEqual(episodes, [i["Id"] for i in self.get("/Playlists/%s/Items" % expanded["Id"])["Items"]])
        self.assertGreater(len(episodes), 1)
        # public unless asked: the request that leaves IsPublic out makes one every account sees, as the real servers do
        self.assertTrue(self.get("/Playlists/%s" % made["Id"])["OpenAccess"])
        status, private, _ = self.request("/Playlists", "POST", {"Name": "Mine", "Ids": ids, "IsPublic": False}, token=self.token)
        self.assertEqual(200, status)
        self.assertFalse(self.get("/Playlists/%s" % private["Id"])["OpenAccess"])
        try:
            self.assertEqual(200, self.request("/__harness/playlists", "POST", {"refuse": 403})[0])
            self.assertEqual(403, self.request("/Playlists", "POST", {"Name": "No", "Ids": ids}, token=self.token)[0])
        finally:
            self.request("/__harness/playlists", "POST", {"refuse": 0})


class DefaultFixtureTests(unittest.TestCase):
    def test_the_default_fixture_without_box_sets_still_has_its_franchises(self):
        lib = library.Library("s", "u", 600 * library.TICKS)
        films = [i for i in lib.items.values() if i["kind"] == "movie"]
        self.assertGreaterEqual(len({f["tmdb_collection"] for f in films if f.get("tmdb_collection")}), 6)
        self.assertFalse(any(i.get("tmdb") for i in lib.items.values()))


if __name__ == "__main__":
    unittest.main()
