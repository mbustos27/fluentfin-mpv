"""Tests of the showcase library (showcase.py, FAKESERVER_SHOWCASE): off by default; on, only the manifest's titles and
files, nothing generated; and the manifest's content checks. Standard library only:

    python -B -m unittest discover -s tools/ui-harness/fakeserver -p "test_showcase.py"
"""

import json
import os
import shutil
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import imagegen  # noqa: E402
import library  # noqa: E402
import server  # noqa: E402
import showcase  # noqa: E402
from test_fakeserver import AUTH, CREDS, make_media  # noqa: E402

COMMONS = "https://commons.wikimedia.org/wiki/File:"


def write_manifest(folder):
    """A small showcase: two films (one the showcase movie, with a logo and a runtime), a series of two seasons, and a
    collection; every picture a small PNG of its own, each credited."""
    art = os.path.join(folder, "art")
    os.makedirs(art, exist_ok=True)
    names = ["film-a-poster.png", "film-a-backdrop.png", "film-a-logo.png", "film-b-poster.png", "film-b-backdrop.png",
             "show-poster.png", "show-backdrop.png", "show-s2.png", "box-poster.png"]
    for n, name in enumerate(names):
        with open(os.path.join(art, name), "wb") as f:
            f.write(imagegen.render("showcase-test-%d" % n, name, "", 40, 60))
    credit = lambda name: {"file": name, "work": name, "author": "Someone", "licence": "CC BY 3.0",
                           "licence_url": "https://creativecommons.org/licenses/by/3.0/", "source": COMMONS + name}
    manifest = {
        "art_dir": "art",
        "views": [{"name": "Films", "type": "movies"}, {"name": "Television", "type": "tvshows"}],
        "films": [
            {"key": "a", "name": "Film Alpha", "year": 1927, "view": "Films", "role": "movie", "genres": ["Drama"],
             "rating": "NR", "runtime": 96, "overview": "Our own words about Film Alpha.", "studios": ["Studio A"],
             "people": [{"name": "Director One", "type": "Director"}],
             "art": {"Primary": "film-a-poster.png", "Backdrop": "film-a-backdrop.png", "Logo": "film-a-logo.png"},
             "user": {"progress": 0.5, "days_ago": 1}},
            {"key": "b", "name": "Film Beta", "year": 2010, "view": "Films", "genres": ["Animation"],
             "overview": "Our own words about Film Beta.",
             "art": {"Primary": "film-b-poster.png", "Backdrop": "film-b-backdrop.png"}, "user": {"favorite": True}},
        ],
        "series": [
            {"key": "s", "name": "Show Gamma", "year": 1954, "view": "Television", "role": "series", "genres": ["Comedy"],
             "overview": "Our own words about Show Gamma.",
             "art": {"Primary": "show-poster.png", "Backdrop": "show-backdrop.png"},
             "seasons": [{"index": 1, "episodes": [{"index": i, "name": "Episode %d" % i, "overview": "One."} for i in (1, 2, 3)]},
                         {"index": 2, "art": {"Primary": "show-s2.png"},
                          "episodes": [{"index": 1, "name": "Second Start", "overview": "Two."}]}],
             "user": {"watched": 2, "days_ago": 2}},
        ],
        "collections": [{"name": "Box Delta", "members": ["a", "b"], "overview": "Two films.",
                         "art": {"Primary": "box-poster.png"}}],
        "credits": [credit(n) for n in names],
    }
    path = os.path.join(folder, "showcase.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(manifest, f)
    return path, manifest


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="ffh-showcase-")
        self.path, _ = write_manifest(self.tmp)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def edited(self, change):
        manifest = showcase.load(self.path)
        change(manifest)
        return showcase.validate(manifest)

    def test_a_good_manifest_has_no_problems(self):
        self.assertEqual([], showcase.validate(showcase.load(self.path)))

    def test_every_picture_needs_a_free_licence_a_commons_source_and_its_author(self):
        def change(m):
            m["credits"][0]["licence"] = "All rights reserved"
            m["credits"][1]["source"] = "https://example.com/poster.jpg"
            m["credits"][2]["author"] = ""
            del m["credits"][3]
        problems = "\n".join(self.edited(change))
        self.assertIn("film-a-poster.png: licence 'All rights reserved'", problems)
        self.assertIn("film-a-backdrop.png: source 'https://example.com/poster.jpg' is not a Wikimedia Commons", problems)
        self.assertIn("film-a-logo.png: a CC BY picture needs its author", problems)
        self.assertIn("film-b-poster.png has no credit", problems)

    def test_public_domain_and_pictures_made_for_nixie_pass(self):
        def change(m):
            m["credits"][0].update(licence="Public domain", author="")
            m["credits"][1].update(licence="CC0 1.0", source="nixie",
                                   licence_url="https://creativecommons.org/publicdomain/zero/1.0/")
        self.assertEqual([], self.edited(change))

    def test_a_cc_licence_links_to_its_own_deed(self):
        def change(m):
            m["credits"][0]["licence_url"] = "https://creativecommons.org/licenses/by/4.0/"
            m["credits"][1].update(licence="CC BY-SA 4.0", licence_url="https://creativecommons.org/licenses/by-sa/4.0/")
            del m["credits"][2]["licence_url"]
        problems = "\n".join(self.edited(change))
        self.assertIn("film-a-poster.png: licence CC BY 3.0 links to 'https://creativecommons.org/licenses/by/4.0/'", problems)
        self.assertNotIn("film-a-backdrop.png", problems, "CC BY-SA 4.0 with its own deed is fine")
        self.assertIn("film-a-logo.png: licence CC BY 3.0 links to None", problems)

    def test_overviews_stay_short_and_the_fixture_roles_are_there(self):
        def change(m):
            m["films"][1]["overview"] = "x" * (showcase.OVERVIEW_MAX + 1)
            m["series"][0]["role"] = None
        problems = "\n".join(self.edited(change))
        self.assertIn("Film Beta: the overview is over", problems)
        self.assertIn("exactly one item with role 'series'", problems)

    def test_missing_files_and_members_are_named(self):
        def change(m):
            m["films"][0]["art"]["Primary"] = "nowhere.png"
            m["collections"][0]["members"].append("zz")
        problems = "\n".join(self.edited(change))
        self.assertIn("Film Alpha: Primary file nowhere.png is missing", problems)
        self.assertIn("collection Box Delta: no film with key 'zz'", problems)

    def test_the_variable_names_a_manifest_or_its_folder(self):
        self.assertIsNone(showcase.path_from(""))
        self.assertEqual(os.path.join("x", "showcase.json"), showcase.path_from("x"))
        self.assertEqual("y.json", showcase.path_from("y.json"))


class CommittedShowcaseTests(unittest.TestCase):
    """The showcase the README's screenshots use (showcase/): clean, every picture used, every source credited."""

    def setUp(self):
        self.manifest = showcase.load(os.path.join(HERE, "showcase", "showcase.json"))

    def test_it_has_no_problems_and_every_picture_is_used(self):
        self.assertEqual([], showcase.validate(self.manifest))
        art = os.path.join(HERE, "showcase", "art")
        self.assertEqual(sorted(os.listdir(art)), sorted(c["file"] for c in self.manifest["credits"]))
        named = {v for e in self.manifest["films"] + self.manifest["series"] for v in (e.get("art") or {}).values()}
        named |= {v for s in self.manifest["series"] for season in s["seasons"] for ep in season["episodes"]
                  for v in (ep.get("art") or {}).values()}
        self.assertEqual(set(os.listdir(art)), named, "a picture in art/ that no item shows, or the other way round")

    def test_every_downloaded_file_is_credited_in_the_readme(self):
        import csv
        with open(os.path.join(HERE, "showcase", "sources.tsv"), encoding="utf-8", newline="") as f:
            rows = list(csv.DictReader(f, delimiter="\t"))
        with open(os.path.join(HERE, "..", "..", "..", "README.md"), encoding="utf-8") as f:
            readme = f.read()
        self.assertEqual(21, len(rows))
        # the README's credits: one list entry per download, each ending in its Commons link
        entries = readme[readme.index("<summary>All 21 files"):readme.index("</details>")].split("\n- ")[1:]
        self.assertEqual(21, len(entries))
        for row in rows:
            entry = [e for e in entries if "(%s)" % row["source page"] in e]
            self.assertEqual(1, len(entry), "%s is not credited in the README once" % row["file"])
            text = " ".join(entry[0].split())
            if row["licence"].startswith("CC"):
                self.assertIn("[%s](%s)" % (row["licence"], showcase.licence_url(row["licence"])), text, row["file"])
            else:
                self.assertIn("Public domain (%s)" % row["licence"], text, row["file"])
        by_file = {r["file"]: r for r in rows}
        for c in self.manifest["credits"]:
            row = by_file.get(c["from"])
            self.assertIsNotNone(row, "%s comes from a file sources.tsv doesn't list" % c["file"])
            self.assertEqual(row["source page"], c["source"], c["file"])
            if row["licence"].startswith("CC"):
                self.assertEqual((row["licence"], showcase.licence_url(row["licence"])), (c["licence"], c["licence_url"]), c["file"])
            else:
                self.assertEqual(("Public domain", row["licence"]), (c["licence"], c["licence_tag"]), c["file"])


class OffByDefaultTests(unittest.TestCase):
    def test_without_the_variable_the_generated_library_is_unchanged(self):
        old = os.environ.pop("FAKESERVER_SHOWCASE", None)
        try:
            lib = library.Library("srv", "user", 600 * library.TICKS)
        finally:
            if old is not None:
                os.environ["FAKESERVER_SHOWCASE"] = old
        fixture = lib.showcase()
        self.assertEqual("A Quiet Harbor", fixture["movie"]["name"])
        self.assertEqual("Movies", fixture["movie"]["library"])
        self.assertEqual("Anime", fixture["anime"]["library"])
        self.assertEqual("Documentaries", fixture["documentary"]["library"])
        self.assertEqual({"on": False}, showcase.report(lib))
        self.assertFalse(any("art" in item for item in lib.items.values()))
        movie = lib.get(fixture["movie"]["id"])
        self.assertIn("Logo", lib.dto(movie)["ImageTags"])


class ShowcaseServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="ffh-showcase-server-")
        cls.path, cls.manifest = write_manifest(os.path.join(cls.tmp, "showcase"))
        cls.meta = make_media(os.path.join(cls.tmp, "media"))
        old = os.environ.get("FAKESERVER_SHOWCASE")
        os.environ["FAKESERVER_SHOWCASE"] = os.path.dirname(cls.path)
        try:
            cls.srv = server.Server(os.path.join(cls.tmp, "out"), cls.meta, CREDS)
        finally:
            if old is None:
                os.environ.pop("FAKESERVER_SHOWCASE")
            else:
                os.environ["FAKESERVER_SHOWCASE"] = old
        threading.Thread(target=cls.srv.serve, daemon=True).start()
        cls.token = cls.request("/Users/AuthenticateByName", "POST", {"Username": CREDS["username"], "Pw": CREDS["password"]})[1]["AccessToken"]
        cls.art = os.path.join(os.path.dirname(cls.path), "art")

    @classmethod
    def tearDownClass(cls):
        cls.srv.httpd.shutdown()
        cls.srv.httpd.server_close()
        cls.srv.log_file.close()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    @classmethod
    def request(cls, path, method="GET", body=None, raw=False):
        req = urllib.request.Request(cls.srv.url + path, method=method, data=None if body is None else json.dumps(body).encode())
        req.add_header("Authorization", AUTH + (', Token="%s"' % cls.token if getattr(cls, "token", None) else ""))
        if body is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                data = r.read()
                return r.status, (data if raw else (json.loads(data) if data else None))
        except urllib.error.HTTPError as e:
            with e:
                return e.code, e.read()

    def get(self, path):
        status, data = self.request(path)
        self.assertEqual(200, status, path)
        return data

    def file(self, name):
        with open(os.path.join(self.art, name), "rb") as f:
            return f.read()

    def test_only_the_manifests_titles_are_in_the_library(self):
        lib = self.srv.lib
        shown = {i["Name"] for i in lib.items.values() if i["kind"] in ("movie", "series", "boxset", "view")}
        self.assertEqual({"Film Alpha", "Film Beta", "Show Gamma", "Box Delta", "Films", "Television", "Collections"}, shown)
        views = self.get("/UserViews?userId=" + self.srv.user_id)["Items"]
        self.assertEqual(["Films", "Television", "Collections"], [v["Name"] for v in views])
        films = self.get("/Items?ParentId=%s&IncludeItemTypes=Movie" % views[0]["Id"])["Items"]
        self.assertEqual(["Film Alpha", "Film Beta"], [f["Name"] for f in films])

    def test_the_fixture_opens_the_showcase_movie_and_series(self):
        fixture = self.get("/__harness/fixture")
        self.assertEqual(("Film Alpha", "Films"), (fixture["movie"]["name"], fixture["movie"]["library"]))
        self.assertEqual(("Show Gamma", "Television"), (fixture["series"]["name"], fixture["series"]["library"]))
        self.assertEqual("Season 1", fixture["season"]["name"])
        self.assertEqual("Episode 1", fixture["episode"]["name"])
        self.assertIsNone(fixture["anime"])
        self.assertIsNone(fixture["documentary"])

    def test_pictures_are_the_manifests_files_and_nothing_else_is_advertised_or_served(self):
        fixture = self.get("/__harness/fixture")
        movie = fixture["movie"]["id"]
        d = self.get("/Users/%s/Items/%s" % (self.srv.user_id, movie))
        self.assertEqual({"Primary", "Logo", "Thumb"}, set(d["ImageTags"]))
        self.assertEqual(1, len(d["BackdropImageTags"]))
        self.assertTrue(all("ImageTag" not in c for c in d["Chapters"]), "chapters have no generated stills")
        self.assertEqual(self.file("film-a-poster.png"), self.request("/Items/%s/Images/Primary" % movie, raw=True)[1])
        self.assertEqual(self.file("film-a-backdrop.png"), self.request("/Items/%s/Images/Backdrop/0" % movie, raw=True)[1])
        self.assertEqual(self.file("film-a-backdrop.png"), self.request("/Items/%s/Images/Thumb" % movie, raw=True)[1])
        self.assertEqual(self.file("film-a-logo.png"), self.request("/Items/%s/Images/Logo" % movie, raw=True)[1])
        self.assertEqual(404, self.request("/Items/%s/Images/Banner" % movie, raw=True)[0])
        other = next(i for i in self.srv.lib.items.values() if i["Name"] == "Film Beta")
        self.assertNotIn("Logo", self.get("/Users/%s/Items/%s" % (self.srv.user_id, other["Id"]))["ImageTags"])
        self.assertEqual(404, self.request("/Items/%s/Images/Logo" % other["Id"], raw=True)[0])
        # people: no photos
        person = d["People"][0]
        self.assertNotIn("PrimaryImageTag", person)
        self.assertEqual({}, self.get("/Users/%s/Items/%s" % (self.srv.user_id, person["Id"]))["ImageTags"])
        self.assertEqual(404, self.request("/Items/%s/Images/Primary" % person["Id"], raw=True)[0])

    def test_seasons_episodes_collections_and_libraries_borrow_real_pictures(self):
        lib = self.srv.lib
        series = next(i for i in lib.items.values() if i["Name"] == "Show Gamma")
        s1, s2 = [lib.items[s] for s in lib.children[series["Id"]]]
        episode = lib.items[lib.children[s1["Id"]][0]]
        self.assertEqual(self.file("show-poster.png"), self.request("/Items/%s/Images/Primary" % s1["Id"], raw=True)[1])
        self.assertEqual(self.file("show-s2.png"), self.request("/Items/%s/Images/Primary" % s2["Id"], raw=True)[1])
        self.assertEqual(self.file("show-backdrop.png"), self.request("/Items/%s/Images/Primary" % episode["Id"], raw=True)[1])
        d = self.get("/Users/%s/Items/%s" % (self.srv.user_id, episode["Id"]))
        self.assertNotIn("ParentLogoImageTag", d, "the series has no logo")
        self.assertIn("ParentBackdropImageTags", d)
        box = next(i for i in lib.items.values() if i["kind"] == "boxset")
        self.assertEqual(self.file("box-poster.png"), self.request("/Items/%s/Images/Primary" % box["Id"], raw=True)[1])
        self.assertEqual(self.file("film-a-backdrop.png"), self.request("/Items/%s/Images/Backdrop/0" % box["Id"], raw=True)[1])
        films = next(v for v in lib.views if lib.items[v]["Name"] == "Films")
        self.assertEqual(self.file("film-a-backdrop.png"), self.request("/Items/%s/Images/Primary" % films, raw=True)[1])

    def test_runtimes_resume_favourites_and_next_up_come_from_the_manifest(self):
        lib = self.srv.lib
        alpha = next(i for i in lib.items.values() if i["Name"] == "Film Alpha")
        d = self.get("/Users/%s/Items/%s" % (self.srv.user_id, alpha["Id"]))
        self.assertEqual(96 * 60 * library.TICKS, d["RunTimeTicks"])
        self.assertEqual(50.0, d["UserData"]["PlayedPercentage"])
        resume = self.get("/UserItems/Resume?userId=%s&MediaTypes=Video" % self.srv.user_id)["Items"]
        self.assertEqual(["Film Alpha"], [i["Name"] for i in resume])
        beta = next(i for i in lib.items.values() if i["Name"] == "Film Beta")
        self.assertTrue(self.get("/Users/%s/Items/%s" % (self.srv.user_id, beta["Id"]))["UserData"]["IsFavorite"])
        nextup = self.get("/Shows/NextUp?limit=24&userId=" + self.srv.user_id)["Items"]
        self.assertEqual(["Episode 3"], [i["Name"] for i in nextup])

    def test_the_harness_lists_the_credits_in_use_and_no_problems(self):
        report = self.get("/__harness/showcase")
        self.assertTrue(report["on"])
        self.assertEqual([], report["problems"])
        self.assertEqual(sorted(c["file"] for c in self.manifest["credits"]), sorted(c["file"] for c in report["credits"]))
        self.assertEqual(report["pictures"], sorted(report["pictures"]))


if __name__ == "__main__":
    unittest.main()
