"""Tests of the fake server's administration endpoints (admin.py). Standard library only, no mpv:

    python -B -m unittest discover -s tools/ui-harness/fakeserver -p "test_admin.py"
"""

import json
import os
import shutil
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import admin  # noqa: E402
import server  # noqa: E402
from test_fakeserver import AUTH, CREDS, make_media  # noqa: E402


class AdminTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="ffh-admin-")
        self.srv = server.Server(os.path.join(self.tmp, "out"), make_media(os.path.join(self.tmp, "media")), CREDS)
        threading.Thread(target=self.srv.serve, daemon=True).start()
        self.token = self.call("/Users/AuthenticateByName", "POST",
                               {"Username": CREDS["username"], "Pw": CREDS["password"]}, token=None)[1]["AccessToken"]

    def tearDown(self):
        self.srv.httpd.shutdown()
        self.srv.httpd.server_close()
        self.srv.log_file.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def call(self, path, method="GET", body=None, token=""):
        req = urllib.request.Request(self.srv.url + path, method=method, data=None if body is None else json.dumps(body).encode())
        tok = self.token if token == "" else token
        req.add_header("Authorization", AUTH + (', Token="%s"' % tok if tok else ""))
        if body is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                data = r.read()
                return r.status, (json.loads(data) if data else None)
        except urllib.error.HTTPError as e:
            return e.code, None

    def logged(self):
        path = os.path.join(self.srv.out, "admin.jsonl")
        if not os.path.exists(path):
            return []
        with open(path, encoding="utf-8") as f:
            return [json.loads(line) for line in f]

    def test_restart_is_logged_and_the_server_is_down_for_a_while(self):
        self.assertEqual(self.call("/System/Info")[0], 200)
        old = admin.RESTART_SECONDS
        admin.RESTART_SECONDS = 0.6
        try:
            self.assertEqual(self.call("/System/Restart", "POST")[0], 204)
            self.assertEqual(self.call("/System/Info")[0], 500)
            time.sleep(0.8)
            self.assertEqual(self.call("/System/Info")[0], 200)
        finally:
            admin.RESTART_SECONDS = old
        self.assertEqual([(e["method"], e["path"]) for e in self.logged()], [("POST", "/System/Restart")])

    def test_nothing_is_logged_without_a_token(self):
        self.assertEqual(self.call("/System/Shutdown", "POST", token=None)[0], 401)
        self.assertEqual(self.logged(), [])

    def test_extra_users_can_be_deleted_but_never_a_sign_in_user(self):
        users = self.call("/Users")[1]
        names = [u["Name"] for u in users]
        self.assertIn("bob", names)
        bob = next(u for u in users if u["Name"] == "bob")
        me = next(u for u in users if u["Name"] == CREDS["username"])
        self.assertEqual(self.call("/Users/" + me["Id"], "DELETE")[0], 403)
        self.assertEqual(self.call("/Users/" + bob["Id"], "DELETE")[0], 204)
        self.assertNotIn("bob", [u["Name"] for u in self.call("/Users")[1]])
        self.assertEqual(self.call("/Users/" + bob["Id"], "DELETE")[0], 404)

    def test_password_bodies_are_redacted_in_the_log(self):
        self.call("/Users/Password?userId=" + admin._uid("bob"), "POST", {"CurrentPw": "fake-old", "NewPw": "fake-new"})
        self.call("/Users/Password?userId=" + admin._uid("bob"), "POST", {"ResetPassword": True})
        entries = self.logged()
        self.assertEqual(entries[0]["body"], {"CurrentPw": "<redacted>", "NewPw": "<redacted>"})
        self.assertEqual(entries[1]["body"], {"ResetPassword": True})
        with open(os.path.join(self.srv.out, "admin.jsonl"), encoding="utf-8") as f:
            self.assertNotIn("fake-new", f.read())

    def test_a_library_added_shows_in_the_list_and_a_deleted_one_goes(self):
        before = [f["Name"] for f in self.call("/Library/VirtualFolders")[1]]
        status, _ = self.call("/Library/VirtualFolders?name=Nature%20Films&collectionType=movies&refreshLibrary=true", "POST",
                              {"LibraryOptions": {"PathInfos": [{"Path": "/media/docs"}]}})
        self.assertEqual(status, 204)
        after = self.call("/Library/VirtualFolders")[1]
        docs = next(f for f in after if f["Name"] == "Nature Films")
        self.assertEqual(docs["Locations"], ["/media/docs"])
        self.assertEqual(self.call("/Library/VirtualFolders?name=Nature%20Films&collectionType=movies", "POST", {})[0], 400)
        self.assertEqual(self.call("/Library/VirtualFolders?name=%s&refreshLibrary=true" % before[0], "DELETE")[0], 204)
        self.assertNotIn(before[0], [f["Name"] for f in self.call("/Library/VirtualFolders")[1]])
        self.assertEqual(self.call("/Library/Refresh", "POST")[0], 204)
        self.assertEqual([e["path"] for e in self.logged()],
                         ["/Library/VirtualFolders", "/Library/VirtualFolders", "/Library/VirtualFolders", "/Library/Refresh"])

    def test_tasks_and_the_editors_lists_answer(self):
        tasks = self.call("/ScheduledTasks")[1]
        scan = next(t for t in tasks if t["Key"] == "RefreshLibrary")
        self.assertEqual(self.call("/ScheduledTasks/Running/" + scan["Id"], "POST")[0], 204)
        self.assertTrue(self.call("/Libraries/AvailableOptions?libraryContentType=movies&isNewLibrary=true")[1]["TypeOptions"])
        item = self.srv.lib.views[0]
        editor = self.call("/Items/%s/MetadataEditor" % item)[1]
        self.assertTrue(editor["ParentalRatingOptions"])
        self.assertEqual(self.call("/Items/%s" % item, "POST", {"Name": "x"})[0], 204)
        self.assertEqual(self.logged()[-1]["body"], {"Name": "x"})

    def test_images_renumber_after_a_delete_as_jellyfin_does(self):
        item = next(i for i in self.srv.lib.items.values() if i["kind"] == "movie")["Id"]
        backdrops = [e for e in self.call("/Items/%s/Images" % item)[1] if e["ImageType"] == "Backdrop"]
        self.assertEqual([0, 1, 2], [e["ImageIndex"] for e in backdrops])
        a, b, c = (e["ImageTag"] for e in backdrops)
        self.assertEqual(self.call("/Items/%s/Images/Backdrop/0" % item, "DELETE")[0], 204)   # A
        after = [e for e in self.call("/Items/%s/Images" % item)[1] if e["ImageType"] == "Backdrop"]
        self.assertEqual([(b, 0), (c, 1)], [(e["ImageTag"], e["ImageIndex"]) for e in after])
        # a client that kept B's old index (1) would now delete C
        self.assertEqual(self.call("/Items/%s/Images/Backdrop/0" % item, "DELETE")[0], 204)   # B, by its new index
        self.assertEqual([c], [e["ImageTag"] for e in self.call("/Items/%s/Images" % item)[1] if e["ImageType"] == "Backdrop"])
        self.assertEqual(self.call("/Items/%s/Images/Backdrop/1" % item, "DELETE")[0], 404)


if __name__ == "__main__":
    unittest.main()
