"""Nixie 1.7 (stream K): the outside sources' strict fakes (outside.py, outside_tmdb_exports.py, outside_wikidata.py), as
the plan's §2.1 and the terms re-check of 2026-11-21 say the real services refuse. Standard library only:

    python -B -m unittest discover -s tools/ui-harness/fakeserver -p "test_outside.py"
"""

import datetime
import gzip
import json
import os
import shutil
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import server  # noqa: E402
import smartdata  # noqa: E402
import test_fakeserver as tf  # noqa: E402
CREDS, make_media = tf.CREDS, tf.make_media

UA = "Nixie/1.7.0 (https://github.com/mbustos27/nixie)"
QUERY = ("PREFIX wd: <http://www.wikidata.org/entity/>\nPREFIX wdt: <http://www.wikidata.org/prop/direct/>\n"
         "SELECT ?item ?tmdb ?imdb ?date WHERE { ?item wdt:P166 wd:Q102427 . OPTIONAL { ?item wdt:P4947 ?tmdb . } }")


class OutsideFakeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ["FAKESERVER_COLLECTIONS"] = "1"
        try:
            cls.tmp = tempfile.mkdtemp(prefix="ffh-outside-")
            cls.srv = server.Server(os.path.join(cls.tmp, "out"), make_media(os.path.join(cls.tmp, "media")), CREDS)
        finally:
            del os.environ["FAKESERVER_COLLECTIONS"]
        threading.Thread(target=cls.srv.serve, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.httpd.shutdown()
        cls.srv.httpd.server_close()
        cls.srv.log_file.close()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def setUp(self):
        self.configure({"files.tmdb.org": {"require_key": False, "now": None},
                        "query.wikidata.org": {"mode": "ok", "next_open": False, "timeout_s": 60, "reset_errors": True}})

    def call(self, path, agent=UA, method="GET", body=None, headers=None):
        req = urllib.request.Request(self.srv.url + path, method=method, data=body)
        if agent is not None:
            req.add_header("User-Agent", agent)
        for k, v in (headers or {}).items():
            req.add_header(k, v)
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                return r.status, r.read(), r.headers
        except urllib.error.HTTPError as e:
            return e.code, e.read(), e.headers

    def configure(self, settings):
        status, body, _ = self.call("/__harness/outside/config", method="POST", body=json.dumps(settings).encode(),
                                    headers={"Content-Type": "application/json"})
        self.assertEqual(200, status)
        return json.loads(body)

    def export(self, kind, day):
        return "/__harness/outside/files.tmdb.org/p/exports/%s_ids_%s.json.gz" % (kind, day.strftime("%m_%d_%Y"))

    def sparql(self, host="query.wikidata.org", query=QUERY, agent=UA, path="/sparql"):
        return self.call("/__harness/outside/%s%s?%s" % (host, path, urllib.parse.urlencode({"query": query, "format": "json"})), agent)

    # ---------- TMDb export files

    def test_yesterdays_collection_file_is_gzip_json_lines_with_the_libraries_franchises(self):
        self.configure({"files.tmdb.org": {"now": "2026-11-21T07:30:00Z"}})
        status, body, headers = self.call(self.export("collection", datetime.date(2026, 11, 20)))
        self.assertEqual(200, status)
        self.assertEqual("application/octet-stream", headers["Content-Type"])
        self.assertIsNone(headers.get("Content-Encoding"))  # a file, not an encoded answer
        rows = [json.loads(line) for line in gzip.decompress(body).decode().splitlines()]
        names = {r["id"]: r["name"] for r in rows}
        for tmdb, _ in smartdata.FRANCHISES:
            self.assertIn(int(tmdb), names)
        self.assertEqual("Kestrel Bay Collection", names[int(smartdata.BOXED_FRANCHISE)])

    def test_todays_file_is_404_before_8_utc_and_there_after(self):
        today = datetime.date(2026, 11, 21)
        self.configure({"files.tmdb.org": {"now": "2026-11-21T07:59:00Z"}})
        status, body, _ = self.call(self.export("keyword", today))
        self.assertEqual(404, status)
        self.assertIn(b"NoSuchKey", body)
        self.configure({"files.tmdb.org": {"now": "2026-11-21T08:00:00Z"}})
        self.assertEqual(200, self.call(self.export("keyword", today))[0])
        self.assertEqual(404, self.call(self.export("keyword", today + datetime.timedelta(days=1)))[0])

    def test_files_older_than_3_months_are_gone(self):
        self.configure({"files.tmdb.org": {"now": "2026-11-21T12:00:00Z"}})
        self.assertEqual(200, self.call(self.export("collection", datetime.date(2026, 8, 21)))[0])
        self.assertEqual(404, self.call(self.export("collection", datetime.date(2026, 8, 1)))[0])

    def test_a_key_requirement_answers_401(self):
        self.configure({"files.tmdb.org": {"require_key": True}})
        status, body, _ = self.call(self.export("collection", datetime.date(2026, 11, 20)))
        self.assertEqual(401, status)
        self.assertIn(b"Invalid API key", body)

    def test_an_unknown_file_is_404(self):
        self.assertEqual(404, self.call("/__harness/outside/files.tmdb.org/p/exports/library_ids_11_20_2026.json.gz")[0])
        self.assertEqual(404, self.call("/__harness/outside/files.tmdb.org/p/exports/collection_ids_13_40_2026.json.gz")[0])

    # ---------- Wikidata

    def test_a_query_with_a_contact_in_the_user_agent_gets_sparql_json_with_owned_and_unowned_films(self):
        status, body, headers = self.sparql()
        self.assertEqual(200, status)
        self.assertTrue(headers["Content-Type"].startswith("application/sparql-results+json"))
        data = json.loads(body)
        self.assertEqual(["item", "tmdb", "imdb", "date"], data["head"]["vars"])
        tmdb = [b["tmdb"]["value"] for b in data["results"]["bindings"] if "tmdb" in b]
        owned = {smartdata.provider_ids(self.srv.lib, i)["Tmdb"] for i in self.srv.lib.items.values() if i["kind"] == "movie"}
        self.assertTrue(any(t in owned for t in tmdb))
        self.assertTrue(any(t not in owned for t in tmdb))
        self.assertEqual(data, json.loads(self.sparql()[1]))  # the same list every time

    def test_no_user_agent_or_one_without_a_contact_is_403(self):
        for agent in ("Python-urllib/3.14", "Nixie/1.7.0", None):
            status, body, _ = self.sparql(agent=agent)
            self.assertEqual(403, status, agent)
            self.assertIn(b"robot policy", body)

    def test_query_next_refuses_until_it_opens(self):
        status, body, _ = self.sparql(host="query-next.wikidata.org")
        self.assertEqual(403, status)
        self.assertIn(b"Unauthorized User-Agent", body)
        self.configure({"query.wikidata.org": {"next_open": True}})
        self.assertEqual(200, self.sparql(host="query-next.wikidata.org")[0])
        self.assertEqual(200, self.sparql(host="qlever.cs.uni-freiburg.de", path="/api/wikidata")[0])

    def test_a_429_carries_retry_after(self):
        self.configure({"query.wikidata.org": {"mode": "429", "retry_after": 42}})
        status, _, headers = self.sparql()
        self.assertEqual(429, status)
        self.assertEqual("42", headers["Retry-After"])

    def test_more_than_30_error_queries_a_minute_get_429(self):
        self.configure({"query.wikidata.org": {"mode": "error"}})
        for _ in range(30):
            self.assertEqual(500, self.sparql()[0])
        status, _, headers = self.sparql()
        self.assertEqual(429, status)
        self.assertGreater(int(headers["Retry-After"]), 0)
        self.configure({"query.wikidata.org": {"mode": "ok"}})
        self.assertEqual(429, self.sparql()[0])  # even a good query, for the rest of the minute
        self.assertEqual(200, self.sparql(agent="Other/1.0 (https://example.org)")[0])  # per User-Agent

    def test_the_timeout_answers_blazegraphs_500_after_the_limit(self):
        self.configure({"query.wikidata.org": {"mode": "timeout", "timeout_s": 0.2}})
        status, body, _ = self.sparql()
        self.assertEqual(500, status)
        self.assertIn(b"java.util.concurrent.TimeoutException", body)

    def test_a_query_that_is_not_a_select_is_400(self):
        self.assertEqual(400, self.sparql(query="DESCRIBE wd:Q102427")[0])

    def test_a_post_form_body_works_like_get(self):
        status, body, _ = self.call("/__harness/outside/query.wikidata.org/sparql", method="POST",
                                    body=urllib.parse.urlencode({"query": QUERY}).encode(),
                                    headers={"Content-Type": "application/x-www-form-urlencoded"})
        self.assertEqual(200, status)
        self.assertEqual(json.loads(self.sparql()[1]), json.loads(body))

    # ---------- the hook

    def test_every_request_is_recorded_for_the_privacy_check_and_an_unknown_host_is_404(self):
        self.call("/__harness/outside/requests", method="POST", body=b'{"clear": true}', headers={"Content-Type": "application/json"})
        self.sparql()
        self.assertEqual(404, self.call("/__harness/outside/example.org/anything")[0])
        _, body, _ = self.call("/__harness/outside/requests")
        seen = json.loads(body)["requests"]
        self.assertEqual(["query.wikidata.org", "example.org"], [r["host"] for r in seen])
        self.assertEqual(UA, seen[0]["headers"]["User-Agent"])
        self.assertIn("query=", seen[0]["query"])


if __name__ == "__main__":
    unittest.main()
