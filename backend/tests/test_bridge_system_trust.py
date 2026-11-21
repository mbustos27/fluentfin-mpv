"""fluentfin-mpv: the backend trusts the certificates Windows trusts (BH-140X-M1).

The UI checks a server's certificate against the Windows store, libmpv's
GnuTLS reads the same store, and the backend used certifi's list: a server with
a home or company certificate signed in and browsed, and played nothing.

A local HTTPS server here wears a certificate from a private CA made for the
tests (tests/fixtures/tls). That CA is trusted for one request at a time and is
never added to a certificate store: what the tests prove is that verification
goes through the operating system's chain engine (truststore), and that it
still refuses a certificate nothing trusts.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))


import http.server
import inspect
import logging
import os
import ssl
import sys
import threading
import unittest
from unittest import mock

import requests

from jellyfin_mpv_shim import system_trust

try:
    import truststore
    from truststore import _api as truststore_api
    from truststore._ssl_constants import _original_SSLContext
except ImportError:  # the release pins it; the dev venv should have it too
    truststore = None
    _original_SSLContext = ssl.SSLContext

FIXTURES = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "fixtures", "tls")
CA = os.path.join(FIXTURES, "ca.pem")
REPO = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))


class _Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = b'{"Version": "10.11.0"}'
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


class HttpsServer:
    """127.0.0.1 over HTTPS with the fixture's server certificate."""

    def __enter__(self):
        # the server's context is always the ssl module's own, whatever a
        # test has injected for the client side
        ctx = _original_SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ctx.load_cert_chain(os.path.join(FIXTURES, "server.pem"),
                            os.path.join(FIXTURES, "server.key"))
        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0),
                                                     _Handler)
        self.httpd.socket = ctx.wrap_socket(self.httpd.socket,
                                            server_side=True)
        self.thread = threading.Thread(target=self.httpd.serve_forever,
                                       daemon=True)
        self.thread.start()
        self.url = ("https://127.0.0.1:%d/System/Info/Public"
                    % self.httpd.server_address[1])
        return self

    def __exit__(self, *exc):
        self.httpd.shutdown()
        self.httpd.server_close()
        self.thread.join(5)


@unittest.skipIf(truststore is None, "truststore is not installed")
class SystemTrustTest(unittest.TestCase):
    def setUp(self):
        truststore.extract_from_ssl()
        self.addCleanup(truststore.extract_from_ssl)
        real = truststore_api._verify_peercerts_impl
        self.verified = []

        def spy(ssl_context, cert_chain, server_hostname=None):
            self.verified.append(server_hostname)
            return real(ssl_context, cert_chain, server_hostname)

        patcher = mock.patch.object(truststore_api, "_verify_peercerts_impl",
                                    spy)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_the_os_chain_engine_checks_the_servers_certificate(self):
        self.assertTrue(system_trust.use_system_trust())
        with HttpsServer() as server:
            response = requests.get(server.url, verify=CA, timeout=10)

        self.assertEqual(200, response.status_code)
        self.assertEqual({"127.0.0.1"}, set(self.verified))

    def test_a_certificate_nothing_trusts_is_still_refused(self):
        self.assertTrue(system_trust.use_system_trust())
        with HttpsServer() as server:
            with self.assertRaises(requests.exceptions.SSLError):
                requests.get(server.url, timeout=10)

        # refused by the system's store, not by certifi's list
        self.assertEqual({"127.0.0.1"}, set(self.verified))

    def test_urllib_follows_too(self):
        """intro_online.py's lookups (urllib, ssl.create_default_context)."""
        import urllib.request

        self.assertTrue(system_trust.use_system_trust())
        with HttpsServer() as server:
            context = ssl.create_default_context(cafile=CA)
            with urllib.request.urlopen(server.url, context=context,
                                        timeout=10) as answer:
                self.assertEqual(200, answer.status)

        self.assertEqual({"127.0.0.1"}, set(self.verified))

    def test_using_it_twice_is_harmless(self):
        self.assertTrue(system_trust.use_system_trust())
        first = ssl.SSLContext
        self.assertTrue(system_trust.use_system_trust())
        self.assertIs(first, ssl.SSLContext)
        self.assertIs(truststore.SSLContext, ssl.SSLContext)

    def test_without_truststore_certifi_stays_and_the_log_says_so(self):
        with mock.patch.dict(sys.modules, {"truststore": None}):
            with self.assertLogs("bridge.trust", logging.WARNING) as logs:
                self.assertFalse(system_trust.use_system_trust())

        self.assertIs(_original_SSLContext, ssl.SSLContext)
        self.assertIn("certifi", logs.output[0])


class WiringTest(unittest.TestCase):
    """Where it is used: before the bridge makes any request."""

    def test_the_bridge_uses_it_before_its_runtime_exists(self):
        from jellyfin_mpv_shim import bridge

        source = inspect.getsource(bridge.main)
        self.assertIn("system_trust.use_system_trust()", source)
        self.assertLess(source.index("system_trust.use_system_trust()"),
                        source.index("ShimRuntime("))

    def test_the_frozen_entry_uses_it_before_importing_the_bridge(self):
        path = os.path.join(REPO, "tools", "release", "backend_entry.py")
        with open(path, encoding="utf-8") as f:
            source = f.read()
        self.assertIn("import truststore", source)
        self.assertLess(source.index("truststore.inject_into_ssl()"),
                        source.index("from jellyfin_mpv_shim.bridge"))

    def test_this_venv_has_truststore(self):
        """The review of 1.5 (m4): the tests above skip without it, so a
        venv without it fails here instead of passing quietly. A dev venv
        gets it from the backend's `bridge` (or `all`) extra."""
        self.assertIsNotNone(truststore, "truststore is not installed: "
                             "pip install -e backend[bridge]")

    def test_the_backend_package_asks_for_truststore(self):
        path = os.path.join(REPO, "backend", "pyproject.toml")
        with open(path, encoding="utf-8") as f:
            text = f.read()
        extras = text[text.index("[project.optional-dependencies]"):]
        extras = extras[:extras.index("\n[", 1)]
        self.assertIn('bridge = ["truststore"]', extras)
        self.assertIn('"truststore"', extras[extras.index("all = ["):])

    def test_the_release_pins_truststore(self):
        path = os.path.join(REPO, "tools", "release",
                            "requirements-backend.txt")
        with open(path, encoding="utf-8") as f:
            pins = [line.split("==")[0] for line in f
                    if line.strip() and not line.startswith("#")]
        self.assertIn("truststore", pins)


if __name__ == "__main__":
    unittest.main()
