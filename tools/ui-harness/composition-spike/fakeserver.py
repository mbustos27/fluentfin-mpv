"""The composition spike's fake Jellyfin server: tools/backend-harness/rr/fakejf2.py serving one clip.

    python -B -I fakeserver.py <fakejf2 dir> <clip>

stdin, first line: the fake token (never argv or the environment). Then prints
``READY <url> <userId>`` and answers one command per stdin line:
``stopped`` -> the number of Stopped reports the server accepted (a 2xx).
EOF ends it. The clip's codec, size and HDR are read from its name
(``<codec>-<w>x<h>[-hdr10]-...``, as run.ps1 names them) so the shim's
direct-play decision sees what the file holds. Fake tokens only.
"""
import os
import re
import sys


def main():
    fakejf2_dir, clip = sys.argv[1], sys.argv[2]
    sys.path.insert(0, os.path.abspath(fakejf2_dir))
    import fakejf2  # noqa: E402  (from the folder given, -I keeps the cwd out)

    token = sys.stdin.readline().strip()
    if not token:
        print("NO TOKEN", flush=True)
        return 2

    name = os.path.basename(clip).lower()
    m = re.match(r"(?P<codec>[a-z0-9]+)-(?P<w>\d+)x(?P<h>\d+)", name)
    codec = m.group("codec") if m else "mpeg4"
    width = int(m.group("w")) if m else 640
    height = int(m.group("h")) if m else 360
    hdr = "-hdr10" in name

    class ClipServer(fakejf2.FakeJF):
        def source(self, item_id):
            src = super().source(item_id)
            video = {"Type": "Video", "Index": 0, "Codec": codec,
                     "Width": width, "Height": height, "IsDefault": True}
            if hdr:
                video.update({"VideoRange": "HDR", "VideoRangeType": "HDR10",
                              "ColorTransfer": "smpte2084",
                              "ColorPrimaries": "bt2020", "BitDepth": 10})
            src["MediaStreams"] = [video]
            src["RunTimeTicks"] = 600000000
            return src

        def item(self, item_id):
            out = super().item(item_id)
            out["Name"] = "Composition spike clip"
            out["RunTimeTicks"] = 600000000
            return out

    user_id = "a" * 32
    server = ClipServer("spike", token, os.path.abspath(clip), user_id)
    print("READY %s %s" % (server.url, user_id), flush=True)
    for line in sys.stdin:
        if line.strip() == "stopped":
            with server.lock:
                n = sum(1 for r in server.records
                        if r["method"] == "DONE"
                        and r["path"].startswith("/Sessions/Playing/Stopped")
                        and 200 <= r.get("status", 0) < 300)
            print(n, flush=True)
    server.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
