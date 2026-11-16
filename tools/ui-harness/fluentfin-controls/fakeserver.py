"""The player controls harness's fake Jellyfin server: tools/backend-harness/rr/fakejf2.py serving one clip as two
episodes of a series ("Spike Show", S1E1 "Pilot" and S1E2 "Second"), so the title and Next can be checked. S1E1 has
an intro from 0:03 to 0:15 (a MediaSegment), so the Skip button can be checked.

    python -B -I fakeserver.py <fakejf2 dir> <clip>

stdin, first line: the fake token (never argv or the environment). Then prints ``READY <url> <userId>`` and answers
one command per stdin line: ``stopped`` -> the number of Stopped reports the server accepted (a 2xx). EOF ends it.
Fake tokens only.
"""
import os
import sys

EPISODES = {"e" * 31 + "1": (1, "Pilot"), "e" * 31 + "2": (2, "Second")}
INTROS = {"e" * 31 + "1": (3, 15)}  # seconds


def main():
    fakejf2_dir, clip = sys.argv[1], sys.argv[2]
    sys.path.insert(0, os.path.abspath(fakejf2_dir))
    import fakejf2  # noqa: E402  (from the folder given, -I keeps the cwd out)

    token = sys.stdin.readline().strip()
    if not token:
        print("NO TOKEN", flush=True)
        return 2

    class ClipServer(fakejf2.FakeJF):
        def segments(self, item_id):
            if item_id not in INTROS:
                return []
            start, end = INTROS[item_id]
            return [{"Id": "1" * 32, "ItemId": item_id, "Type": "Intro",
                     "StartTicks": start * 10_000_000, "EndTicks": end * 10_000_000}]

        def source(self, item_id):
            src = super().source(item_id)
            src["MediaStreams"] = [{"Type": "Video", "Index": 0, "Codec": "h264", "Width": 1920,
                                    "Height": 1080, "IsDefault": True}]
            src["RunTimeTicks"] = 600000000
            return src

        def item(self, item_id):
            out = super().item(item_id)
            out["RunTimeTicks"] = 600000000
            if item_id in EPISODES:
                number, name = EPISODES[item_id]
                out.update({"Type": "Episode", "Name": name, "SeriesName": "Spike Show", "SeriesId": "f" * 32,
                            "ParentIndexNumber": 1, "IndexNumber": number})
            return out

    user_id = "a" * 32
    server = ClipServer("controls", token, os.path.abspath(clip), user_id)
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
