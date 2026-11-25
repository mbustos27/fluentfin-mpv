"""The basics gate's second Jellyfin (class 2, servers; Nixie 1.7.5, g2).

The fake server (tools/ui-harness/fakeserver/server.py, unchanged) as another server: its own server id and name, so
Nixie saves it as a second server beside the run's. Same arguments as server.py (--out, --media, --ready-file, ...); it
listens on 127.0.0.1 only, as server.py does. Started by tools/gate/checks/g2-common.ps1 (Start-G2SecondServer) inside
the run's kill-on-close job.

It reads server.py's source and runs it with two strings swapped: the seed of its server id and its name. A swap that
finds nothing stops it at once with a line saying which (the fake server changed: update the swap here).
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FAKE = os.path.normpath(os.path.join(HERE, "..", "..", "ui-harness", "fakeserver", "server.py"))
NAME = "Second Harness (fake)"
SWAPS = (
    ('b"ffh-server"', 'b"ffh-server-second"'),          # Server.server_id's seed: another id, so another saved server
    ('"Harness Jellyfin (fake)"', json.dumps(NAME)),     # ServerName in /System/Info, /System/Info/Public, users
)


def source():
    with open(FAKE, encoding="utf-8") as f:
        text = f.read()
    for old, new in SWAPS:
        if text.count(old) == 0:
            sys.exit("second-server.py: %s is not in %s any more (the fake server changed): update SWAPS" % (old, FAKE))
        text = text.replace(old, new)
    return text


def main():
    code = compile(source(), FAKE, "exec")
    sys.argv = [FAKE] + sys.argv[1:]
    exec(code, {"__name__": "__main__", "__file__": FAKE})


if __name__ == "__main__":
    main()
