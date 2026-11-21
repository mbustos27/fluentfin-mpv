"""fluentfin-mpv (1.5, privacy and the review of 1.5): whether the server is
local is decided from its address alone, asking nobody.

Upstream's `utils.is_local_domain` asked checkip.amazonaws.com for this
machine's public address on every play to a server with a public IPv4
address (hairpin NAT), and the server's /System/Endpoint for IPv6. The first
was a privacy leak; the second calls every play through a reverse proxy
local (the server sees only the proxy's LAN address), which lifted the remote
bitrate cap from remote plays. Now: loopback, private and link-local
addresses, single-label and never-public names are local; a public address
is remote; a public-looking name is resolved (no request), and only a
private answer makes it local (BH-150-m5: split DNS at home); no request is
made.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))


import socket
import unittest
from unittest import mock

import requests

from jellyfin_mpv_shim import utils


class _Client:
    """A client whose server, asked, would say every play is in its network
    (what a reverse proxy on the server's LAN makes it say)."""

    def __init__(self, server):
        self.config = mock.Mock()
        self.config.data = {"auth.server": server}
        self.jellyfin = mock.Mock()
        self.jellyfin.get_endpoint_info.return_value = {
            "IsLocal": False, "IsInNetwork": True}


#: The stub resolver's answers: what a split-DNS home network and the public
#: internet would say. A name not listed does not resolve.
_DNS = {
    "jellyfin.example.com": ["93.184.216.34"],
    "jellyfin.home.example.org": ["192.168.1.5"],
    "nas.home": ["10.0.0.9"],
    "dual.example.net": ["2606:4700::6810:84e5", "192.168.1.6"],
}


class AddressAloneTest(unittest.TestCase):
    def setUp(self):
        self.asked = []
        self.looked_up = []

        def refuse(*args, **kwargs):
            self.asked.append(args)
            raise AssertionError("asked the network: %r" % (args,))

        def resolve(host, *args, **kwargs):
            self.looked_up.append(host)
            if host not in _DNS:
                raise socket.gaierror(11001, "getaddrinfo failed")
            return [(None, None, None, "", (ip, 0)) for ip in _DNS[host]]

        for target in (mock.patch.object(requests, "get", refuse),
                       mock.patch.object(requests.Session, "request", refuse),
                       mock.patch.object(utils.requests, "get", refuse),
                       mock.patch.object(utils.socket, "getaddrinfo", resolve),
                       mock.patch.object(socket, "getaddrinfo", resolve)):
            target.start()
            self.addCleanup(target.stop)

        self.addCleanup(lambda: self.assertEqual([], self.asked))

    def local(self, server):
        client = _Client(server)
        answer = utils.is_local_domain(client)
        client.jellyfin.get_endpoint_info.assert_not_called()
        return answer

    def test_a_server_behind_a_reverse_proxy_keeps_its_remote_cap(self):
        """The review's case: the proxy makes the server call the play local
        (IsInNetwork); the public name is what decides, and it is remote."""
        self.assertIs(False, self.local("https://jellyfin.example.com"))
        self.assertIs(False, self.local("https://jellyfin.example.com:8920/jellyfin/"))

    def test_a_name_that_resolves_to_a_private_address_is_local(self):
        """BH-150-m5: split DNS (the owner's domain answers the LAN address
        at home), a router's name and a hosts-file entry are at home, as
        1.4.1 had them, and the lookup asks nobody else."""
        for server in ("https://jellyfin.home.example.org",
                       "http://nas.home:8096",
                       "https://dual.example.net:8920/jellyfin/"):
            with self.subTest(server=server):
                self.assertIs(True, self.local(server))
        self.assertEqual(["jellyfin.home.example.org", "nas.home",
                          "dual.example.net"], self.looked_up)

    def test_a_name_that_does_not_resolve_is_remote(self):
        self.assertIs(False, self.local("https://gone.example.com"))

    def test_public_addresses_are_remote(self):
        for server in ("https://93.184.216.34", "http://93.184.216.34:8096",
                       "https://[2606:4700::6810:84e5]:8920",
                       "http://100.64.0.7:8096"):        # carrier-grade NAT
            with self.subTest(server=server):
                self.assertIs(False, self.local(server))
        self.assertEqual([], self.looked_up, "an address is not looked up")

    def test_loopback_private_and_link_local_are_local(self):
        for server in ("http://127.0.0.1:8096", "http://[::1]:8096",
                       "http://192.168.1.20:8096", "http://10.0.0.5",
                       "http://172.16.4.2:8096", "http://169.254.10.1",
                       "http://[fe80::1%25eth0]:8096", "http://[fd12:3456::7]",
                       "http://[::ffff:192.168.1.20]:8096"):
            with self.subTest(server=server):
                self.assertIs(True, self.local(server))
        self.assertEqual([], self.looked_up, "an address is not looked up")

    def test_single_label_and_never_public_names_are_local(self):
        for server in ("http://localhost:8096", "http://nas:8096",
                       "http://jellyfin.local:8096", "http://media.lan",
                       "http://jf.home.arpa", "http://jf.corp.internal",
                       "http://JELLYFIN.LOCAL.:8096"):
            with self.subTest(server=server):
                self.assertIs(True, self.local(server))
        self.assertEqual([], self.looked_up, "a local name is not looked up")

    def test_no_address_is_remote(self):
        self.assertIs(False, self.local(""))

    def test_nothing_was_asked_and_no_code_names_the_host(self):
        self.assertEqual([], self.asked)
        with open(utils.__file__, encoding="utf-8") as f:
            self.assertNotIn("checkip.amazonaws.com/", f.read())


if __name__ == "__main__":
    unittest.main()
